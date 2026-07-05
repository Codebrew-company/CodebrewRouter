using System.Text.Json;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// P4.3: Anthropic-native POST /v1/messages (+ count_tokens). Lets Claude Code connect
/// with just ANTHROPIC_BASE_URL — no OpenAI translation shim. Translates the Anthropic
/// wire format (incl. SSE event stream, tool use, system blocks) to MEAI and back.
/// </summary>
public static class AnthropicMessagesEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IResult> HandleAsync(
        AnthropicMessagesRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var logger = httpContext.RequestServices.GetService(typeof(ILogger<AnthropicMessagesRequest>)) as ILogger<AnthropicMessagesRequest>;

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", "model is required");
        }

        if (request.Messages is not { Count: > 0 })
        {
            return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", "messages is required");
        }

        List<ChatMessage> messages;
        try
        {
            messages = TranslateMessages(request);
        }
        catch (Exception ex)
        {
            return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", $"Failed to parse messages: {ex.Message}");
        }

        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxTokens,
            StopSequences = request.StopSequences is { Count: > 0 } ? request.StopSequences : null,
            Tools = TranslateTools(request.Tools),
            ToolMode = TranslateToolChoice(request.ToolChoice, request.Tools)
        };

        IChatClient client;
        try
        {
            client = await modelSelectionResolver.ResolveAsync(request.Model, cancellationToken) ?? chatClient;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Model resolution failed for {Model}; using default router client", request.Model);
            client = chatClient;
        }

        try
        {
            return request.Stream
                ? await HandleStreamingAsync(httpContext, client, messages, options, request.Model, cancellationToken)
                : await HandleNonStreamingAsync(client, messages, options, request.Model, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Anthropic /v1/messages failed for model {Model}", request.Model);
            if (httpContext.Response.HasStarted)
            {
                return Results.Empty;
            }

            return AnthropicError(StatusCodes.Status502BadGateway, "api_error", $"Upstream provider failed: {ex.Message}");
        }
    }

    public static IResult CountTokens(AnthropicCountTokensRequest request, ITokenCounter tokenCounter)
    {
        try
        {
            var messages = TranslateMessages(new AnthropicMessagesRequest(request.Model, request.Messages, System: request.System));
            var count = tokenCounter.CountTokens(messages);
            return Results.Json(new AnthropicCountTokensResponse(count));
        }
        catch (Exception ex)
        {
            return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
        }
    }

    // ── Non-streaming ────────────────────────────────────────────────────────

    private static async Task<IResult> HandleNonStreamingAsync(
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        CancellationToken cancellationToken)
    {
        var response = await client.GetResponseAsync(messages, options, cancellationToken);
        var assistantMessage = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?? response.Messages.LastOrDefault();

        var blocks = new List<AnthropicContentBlock>();
        if (assistantMessage is not null)
        {
            foreach (var content in assistantMessage.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        blocks.Add(new AnthropicContentBlock("text", text.Text));
                        break;
                    case FunctionCallContent call:
                        blocks.Add(new AnthropicContentBlock(
                            "tool_use",
                            Id: string.IsNullOrWhiteSpace(call.CallId) ? Ids.New("toolu") : call.CallId,
                            Name: call.Name,
                            Input: JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(), JsonOptions)));
                        break;
                }
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new AnthropicContentBlock("text", response.Text ?? string.Empty));
        }

        var result = new AnthropicMessagesResponse(
            Ids.New("msg"),
            "message",
            "assistant",
            model,
            blocks,
            TranslateStopReason(response.FinishReason, blocks),
            StopSequence: null,
            new AnthropicUsage(
                (int?)response.Usage?.InputTokenCount ?? 0,
                (int?)response.Usage?.OutputTokenCount ?? 0));

        return Results.Json(result, JsonOptions);
    }

    // ── Streaming (Anthropic SSE event protocol) ─────────────────────────────

    private static async Task<IResult> HandleStreamingAsync(
        HttpContext httpContext,
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        CancellationToken cancellationToken)
    {
        var messageId = Ids.New("msg");
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        await WriteEventAsync(httpContext, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                model,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 }
            }
        }, cancellationToken);

        var blockIndex = -1;
        var textBlockOpen = false;
        long outputTokens = 0;
        var sawToolUse = false;
        ChatFinishReason? finishReason = null;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            finishReason ??= update.FinishReason;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        if (!textBlockOpen)
                        {
                            blockIndex++;
                            textBlockOpen = true;
                            await WriteEventAsync(httpContext, "content_block_start", new
                            {
                                type = "content_block_start",
                                index = blockIndex,
                                content_block = new { type = "text", text = "" }
                            }, cancellationToken);
                        }

                        await WriteEventAsync(httpContext, "content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = blockIndex,
                            delta = new { type = "text_delta", text = text.Text }
                        }, cancellationToken);
                        break;

                    case FunctionCallContent call:
                        if (textBlockOpen)
                        {
                            await WriteEventAsync(httpContext, "content_block_stop",
                                new { type = "content_block_stop", index = blockIndex }, cancellationToken);
                            textBlockOpen = false;
                        }

                        sawToolUse = true;
                        blockIndex++;
                        var callId = string.IsNullOrWhiteSpace(call.CallId) ? Ids.New("toolu") : call.CallId;
                        await WriteEventAsync(httpContext, "content_block_start", new
                        {
                            type = "content_block_start",
                            index = blockIndex,
                            content_block = new { type = "tool_use", id = callId, name = call.Name, input = new { } }
                        }, cancellationToken);
                        await WriteEventAsync(httpContext, "content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = blockIndex,
                            delta = new
                            {
                                type = "input_json_delta",
                                partial_json = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), JsonOptions)
                            }
                        }, cancellationToken);
                        await WriteEventAsync(httpContext, "content_block_stop",
                            new { type = "content_block_stop", index = blockIndex }, cancellationToken);
                        break;

                    case UsageContent usage:
                        outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                        break;
                }
            }
        }

        if (textBlockOpen)
        {
            await WriteEventAsync(httpContext, "content_block_stop",
                new { type = "content_block_stop", index = blockIndex }, cancellationToken);
        }

        var stopReason = sawToolUse ? "tool_use" : TranslateStopReason(finishReason, blocks: null);
        await WriteEventAsync(httpContext, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = outputTokens }
        }, cancellationToken);
        await WriteEventAsync(httpContext, "message_stop", new { type = "message_stop" }, cancellationToken);

        return Results.Empty;
    }

    private static async Task WriteEventAsync(HttpContext httpContext, string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await httpContext.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    // ── Translation helpers ──────────────────────────────────────────────────

    private static List<ChatMessage> TranslateMessages(AnthropicMessagesRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.System is { } system && system.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var systemText = system.ValueKind switch
            {
                JsonValueKind.String => system.GetString(),
                JsonValueKind.Array => string.Join(
                    "\n\n",
                    system.EnumerateArray()
                        .Where(block => block.TryGetProperty("text", out _))
                        .Select(block => block.GetProperty("text").GetString())),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(systemText))
            {
                messages.Add(new ChatMessage(ChatRole.System, systemText));
            }
        }

        foreach (var message in request.Messages)
        {
            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;

            if (message.Content.ValueKind == JsonValueKind.String)
            {
                messages.Add(new ChatMessage(role, message.Content.GetString() ?? string.Empty));
                continue;
            }

            if (message.Content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var contents = new List<AIContent>();
            var toolResults = new List<AIContent>();

            foreach (var part in message.Content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var typeProperty))
                {
                    continue;
                }

                switch (typeProperty.GetString())
                {
                    case "text":
                        contents.Add(new TextContent(part.GetProperty("text").GetString() ?? string.Empty));
                        break;

                    case "image" when part.TryGetProperty("source", out var source):
                        var sourceType = source.TryGetProperty("type", out var st) ? st.GetString() : null;
                        if (sourceType == "base64")
                        {
                            var mediaType = source.GetProperty("media_type").GetString() ?? "image/png";
                            var data = source.GetProperty("data").GetString() ?? string.Empty;
                            contents.Add(new DataContent(new Uri($"data:{mediaType};base64,{data}"), mediaType));
                        }
                        else if (sourceType == "url" && source.TryGetProperty("url", out var url))
                        {
                            contents.Add(new UriContent(new Uri(url.GetString()!), "image/*"));
                        }

                        break;

                    case "tool_use":
                        contents.Add(new FunctionCallContent(
                            part.GetProperty("id").GetString() ?? Ids.New("toolu"),
                            part.GetProperty("name").GetString() ?? "unknown",
                            part.TryGetProperty("input", out var input)
                                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(input.GetRawText(), JsonOptions) ?? []
                                : []));
                        break;

                    case "tool_result":
                        var toolUseId = part.GetProperty("tool_use_id").GetString() ?? string.Empty;
                        toolResults.Add(new FunctionResultContent(toolUseId, ExtractToolResultText(part)));
                        break;
                }
            }

            // Anthropic embeds tool_result blocks in user messages; MEAI expects Tool role.
            if (toolResults.Count > 0)
            {
                messages.Add(new ChatMessage(ChatRole.Tool, toolResults));
            }

            if (contents.Count > 0)
            {
                messages.Add(new ChatMessage(role, contents));
            }
        }

        return messages;
    }

    private static string ExtractToolResultText(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Where(block => block.TryGetProperty("text", out _))
                    .Select(block => block.GetProperty("text").GetString())),
            _ => content.GetRawText()
        };
    }

    private static IList<AITool>? TranslateTools(IList<AnthropicTool>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        var aiTools = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                continue;
            }

            var schema = tool.InputSchema is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } inputSchema
                ? inputSchema
                : JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object>() });
            aiTools.Add(new AnthropicToolDeclaration(tool.Name, tool.Description, schema));
        }

        return aiTools.Count > 0 ? aiTools : null;
    }

    private static ChatToolMode? TranslateToolChoice(AnthropicToolChoice? toolChoice, IList<AnthropicTool>? tools)
    {
        if (toolChoice is null)
        {
            return tools is { Count: > 0 } ? ChatToolMode.Auto : null;
        }

        return toolChoice.Type.ToLowerInvariant() switch
        {
            "auto" => ChatToolMode.Auto,
            "any" => ChatToolMode.RequireAny,
            "none" => ChatToolMode.None,
            "tool" when !string.IsNullOrWhiteSpace(toolChoice.Name) => ChatToolMode.RequireSpecific(toolChoice.Name),
            _ => null
        };
    }

    private static string TranslateStopReason(ChatFinishReason? finishReason, IList<AnthropicContentBlock>? blocks)
    {
        if (blocks?.Any(block => block.Type == "tool_use") == true)
        {
            return "tool_use";
        }

        if (finishReason == ChatFinishReason.Length)
        {
            return "max_tokens";
        }

        if (finishReason == ChatFinishReason.ToolCalls)
        {
            return "tool_use";
        }

        return "end_turn";
    }

    private static IResult AnthropicError(int statusCode, string type, string message)
        => Results.Json(new AnthropicErrorResponse("error", new AnthropicErrorDetail(type, message)), statusCode: statusCode);

    private sealed class AnthropicToolDeclaration(string name, string? description, JsonElement jsonSchema) : AIFunctionDeclaration
    {
        public override string Name { get; } = name;

        public override string Description { get; } = description ?? string.Empty;

        public override JsonElement JsonSchema { get; } = jsonSchema.Clone();

        public override JsonElement? ReturnJsonSchema => null;
    }
}
