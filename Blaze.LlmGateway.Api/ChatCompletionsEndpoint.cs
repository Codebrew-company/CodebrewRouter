using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ClientModel;
using System.Text.Json;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Handler for POST /v1/chat/completions endpoint.
/// OpenAI-compatible chat completion endpoint with streaming support.
/// </summary>
public static class ChatCompletionsEndpoint
{
    private const string DirectTaskType = "DirectModel";

    /// <summary>Handle chat completion requests</summary>
    public static async Task<IResult> HandleAsync(
        ChatCompletionRequest req,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var logger = httpContext.RequestServices.GetService(typeof(ILogger<ChatCompletionRequest>)) as ILogger<ChatCompletionRequest>;
        var availabilityRegistry = httpContext.RequestServices.GetRequiredService<IModelAvailabilityRegistry>();
        
        LogRouter(logger, new RouterStartEvent(req.Messages?.Count ?? 0));

        // Validate required fields
        if (string.IsNullOrWhiteSpace(req.Model))
        {
            logger?.LogWarning("❌ Validation failed: Missing model field");
            return Results.BadRequest(new
            {
                error = new
                {
                    message = "Missing required field: model",
                    type = "invalid_request_error",
                    code = "missing_field"
                }
            });
        }

        if (req.Messages == null || req.Messages.Count == 0)
        {
            logger?.LogWarning("❌ Validation failed: Missing or empty messages field");
            return Results.BadRequest(new
            {
                error = new
                {
                    message = "Missing required field: messages",
                    type = "invalid_request_error",
                    code = "missing_field"
                }
            });
        }

        // Convert request DTOs to ChatMessage list
        var messages = new List<ChatMessage>();
        foreach (var msg in req.Messages)
        {
            var chatMsg = ToChatMessage(msg);
            var content = msg.Content ?? string.Empty;
            messages.Add(chatMsg);
            logger?.LogDebug("  ├─ Message added: {Role} - {ContentPreview}", 
                chatMsg.Role, content.Substring(0, Math.Min(50, content.Length)));
        }

        messages = OpenAiProtocolMapper.ApplyInstructions(messages, null, req.Model, gatewayOptions);

        // Build ChatOptions from request
        var options = new ChatOptions
        {
            ModelId = req.Model,
            Temperature = req.Temperature,
            MaxOutputTokens = req.MaxCompletionTokens ?? req.MaxTokens,
            TopP = req.TopP,
            FrequencyPenalty = req.FrequencyPenalty,
            PresencePenalty = req.PresencePenalty,
            StopSequences = ExtractStopSequences(req.Stop),
            ToolMode = ResolveToolMode(req.ToolChoice, req.Tools),
            AllowMultipleToolCalls = req.ParallelToolCalls,
            ResponseFormat = OpenAiProtocolMapper.GetResponseFormatForModel(
                req.Model,
                gatewayOptions,
                ResolveResponseFormat(req.ResponseFormat)),
            Reasoning = ResolveReasoning(req.ReasoningEffort)
        };
        logger?.LogDebug("  ├─ ChatOptions: Temp={Temperature}, MaxTokens={MaxTokens}, TopP={TopP}", 
            req.Temperature, options.MaxOutputTokens, req.TopP);

        if (req.Stream)
        {
            // Streaming response via SSE
            return await HandleStreamingAsync(httpContext, messages, options, req.Model, req.Tools, chatClient, modelSelectionResolver, availabilityRegistry, gatewayOptions, logger, ct);
        }
        else
        {
            // Non-streaming response
            return await HandleNonStreamingAsync(messages, options, req.Model, req.Tools, chatClient, modelSelectionResolver, availabilityRegistry, gatewayOptions, logger, ct);
        }
    }

    private static async Task<IResult> HandleStreamingAsync(
        HttpContext httpContext,
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        IList<Tool>? tools,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> gatewayOptions,
        ILogger<ChatCompletionRequest>? logger,
        CancellationToken ct)
    {
        var id = $"chatcmpl-{Guid.NewGuid().ToString("N").Substring(0, 24)}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;

        try
        {
            // Add tools to options if provided
            if (tools != null && tools.Count > 0)
            {
                logger?.LogDebug("  ├─ {ToolCount} tools requested - translating to MEAI AITools", tools.Count);
                var aiTools = TranslateTools(tools, logger);
                if (aiTools != null && aiTools.Count > 0)
                {
                    options.Tools = aiTools;
                    logger?.LogDebug("  ├─ {ToolCount} tools appended to ChatOptions", aiTools.Count);
                }
            }

            var selectedClient = await ResolveClientAsync(model, chatClient, modelSelectionResolver, availabilityRegistry, logger, ct);
            LogRouter(logger, new RouterTryEvent(1, 1, model, model, DirectTaskType));
            var probeSw = System.Diagnostics.Stopwatch.StartNew();
            var firstChunk = await TryGetFirstStreamingUpdateAsync(selectedClient, messages, options, ct);
            probeSw.Stop();
            LogRouter(logger, new RouterProbeEvent(1, model, model, probeSw.ElapsedMilliseconds, firstChunk.Success));
            if (!firstChunk.Success)
            {
                LogRouter(logger, new RouterFailEvent(1, model, model, firstChunk.Exception?.Message ?? "Provider failed before streaming started"));
                return CreateProviderErrorResult(model, firstChunk.Exception);
            }

            enumerator = firstChunk.Enumerator;
            var chunkCount = 0;
            var streamSw = System.Diagnostics.Stopwatch.StartNew();
            LogRouter(logger, new RouterSuccessEvent(
                1, model, model, DirectTaskType, null, null, null, probeSw.ElapsedMilliseconds));

            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.Append("Cache-Control", "no-cache");
            httpContext.Response.Headers.Append("Connection", "keep-alive");
            // Disable proxy/nginx buffering so each SSE chunk reaches the client immediately.
            httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

            var firstChoice = new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null };
            var firstRoleChunk = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { firstChoice } };
            var firstRoleJson = JsonSerializer.Serialize(firstRoleChunk);
            await httpContext.Response.WriteAsync($"data: {firstRoleJson}\n\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
            logger?.LogDebug("  ├─ First chunk with role sent");

            if (firstChunk.Update is not null)
            {
                await WriteStreamingContentChunkAsync(httpContext, id, created, model, firstChunk.Update, ct);
                chunkCount++;
            }

            while (enumerator is not null)
            {
                bool hasMore;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception)
                {
                    LogRouter(logger, new RouterMidstreamFailEvent(model, model));
                    break;
                }

                if (!hasMore)
                {
                    break;
                }

                await WriteStreamingContentChunkAsync(httpContext, id, created, model, enumerator.Current, ct);
                chunkCount++;

                if (chunkCount % 10 == 0)
                {
                    logger?.LogDebug("  ├─ Streamed {ChunkCount} chunks so far", chunkCount);
                }
            }

            // Emit final chunk with finish_reason
            var finalChoice = new { index = 0, delta = new { }, finish_reason = "stop" };
            var finalChunk = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { finalChoice } };
            var finalJson = JsonSerializer.Serialize(finalChunk);
            await httpContext.Response.WriteAsync($"data: {finalJson}\n\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
            
            streamSw.Stop();
            LogRouter(logger, new RouterStreamCompleteEvent(chunkCount, model, model, DirectTaskType, streamSw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            LogRouter(logger, new RouterFailEvent(1, model, model, ex.Message), LogLevel.Error);
            if (!httpContext.Response.HasStarted)
            {
                return CreateProviderErrorResult(model, ex);
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }

            if (httpContext.Response.HasStarted ||
                string.Equals(httpContext.Response.ContentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await httpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }

            logger?.LogDebug("  └─ [DONE] marker sent");
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleNonStreamingAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        IList<Tool>? tools,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> gatewayOptions,
        ILogger<ChatCompletionRequest>? logger,
        CancellationToken ct)
    {
        try
        {
            // Add tools to options if provided
            if (tools != null && tools.Count > 0)
            {
                logger?.LogDebug("  ├─ {ToolCount} tools requested - translating to MEAI AITools", tools.Count);
                var aiTools = TranslateTools(tools, logger);
                if (aiTools != null && aiTools.Count > 0)
                {
                    options.Tools = aiTools;
                    logger?.LogDebug("  ├─ {ToolCount} tools appended to ChatOptions", aiTools.Count);
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var selectedClient = await ResolveClientAsync(model, chatClient, modelSelectionResolver, availabilityRegistry, logger, ct);
            LogRouter(logger, new RouterTryEvent(1, 1, model, model, DirectTaskType));
            
            var completion = await selectedClient.GetResponseAsync(messages, options, ct);
            
            sw.Stop();
            LogRouter(logger, new RouterSuccessEvent(
                1,
                model,
                model,
                DirectTaskType,
                completion.FinishReason?.ToString(),
                (int?)completion.Usage?.InputTokenCount,
                (int?)completion.Usage?.OutputTokenCount,
                sw.ElapsedMilliseconds));

            var id = $"chatcmpl-{Guid.NewGuid().ToString("N").Substring(0, 24)}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var responseMessage = completion.Messages?.FirstOrDefault() is { } message
                ? ToOpenAiMessage(message)
                : new ChatMessageDto(Role: "assistant", Content: "");
            if (responseMessage.ToolCalls is not { Count: > 0 })
            {
                responseMessage = responseMessage with
                {
                    Content = OpenAiProtocolMapper.NormalizeAssistantContent(
                        model,
                        responseMessage.Content,
                        gatewayOptions)
                };
            }
            var finishReason = responseMessage.ToolCalls is { Count: > 0 }
                ? "tool_calls"
                : TranslateFinishReason(completion.FinishReason);
            var choices = new List<Choice>
            {
                new(
                    Index: 0,
                    Message: responseMessage,
                    Delta: null,
                    FinishReason: finishReason
                )
            };

            var usage = completion.Usage is null
                ? null
                : new Usage(
                    PromptTokens: ToTokenCount(completion.Usage.InputTokenCount),
                    CompletionTokens: ToTokenCount(completion.Usage.OutputTokenCount),
                    TotalTokens: ToTokenCount(
                        completion.Usage.TotalTokenCount ??
                        (completion.Usage.InputTokenCount.GetValueOrDefault() + completion.Usage.OutputTokenCount.GetValueOrDefault())));

            var result = new ChatCompletionResponse(
                Id: id,
                Object: "chat.completion",
                Created: created,
                Model: model,
                Choices: choices,
                Usage: usage
            );

            logger?.LogDebug("  └─ Response text length: {TextLength}", responseMessage.Content.Length);
            return Results.Json(result);
        }
        catch (Exception ex)
        {
            LogRouter(logger, new RouterFailEvent(1, model, model, ex.Message), LogLevel.Error);
            return CreateProviderErrorResult(model, ex);
        }
    }

    private static async Task<StreamingProbeResult> TryGetFirstStreamingUpdateAsync(
        IChatClient selectedClient,
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        var enumerator = selectedClient.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                await enumerator.DisposeAsync();
                return StreamingProbeResult.Empty;
            }

            return new StreamingProbeResult(true, enumerator.Current, enumerator, null);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            return new StreamingProbeResult(false, null, null, ex);
        }
    }

    private static async Task WriteStreamingContentChunkAsync(
        HttpContext httpContext,
        string id,
        long created,
        string model,
        ChatResponseUpdate update,
        CancellationToken cancellationToken)
    {
        var finishReason = update.FinishReason is null
            ? null
            : TranslateFinishReason(update.FinishReason);
        var toolCalls = update.Contents
            .OfType<FunctionCallContent>()
            .Select((toolCall, index) => new
            {
                index,
                id = string.IsNullOrWhiteSpace(toolCall.CallId) ? $"call_{index}" : toolCall.CallId,
                type = "function",
                function = new
                {
                    name = toolCall.Name,
                    arguments = JsonSerializer.Serialize(toolCall.Arguments)
                }
            })
            .ToArray();

        object delta = toolCalls.Length > 0
            ? new
            {
                content = string.IsNullOrEmpty(update.Text) ? null : update.Text,
                tool_calls = toolCalls
            }
            : new { content = update.Text };
        var choice = new { index = 0, delta, finish_reason = finishReason };
        var chunk = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { choice } };
        var contentJson = JsonSerializer.Serialize(chunk);
        await httpContext.Response.WriteAsync($"data: {contentJson}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    private static ChatRole ResolveChatRole(string? role)
        => role?.ToLowerInvariant() switch
        {
            "developer" => ChatRole.System,
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            "function" => ChatRole.Tool,
            _ => ChatRole.User
        };

    private static ChatMessage ToChatMessage(ChatMessageDto message)
    {
        var role = ResolveChatRole(message.Role);
        var content = message.Content ?? string.Empty;

        if (message.ToolCalls is not { Count: > 0 } && string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            var simpleContents = ToAiContents(message);
            return simpleContents.Count > 0
                ? new ChatMessage(role, simpleContents)
                {
                    AuthorName = message.Name
                }
                : new ChatMessage(role, content)
                {
                    AuthorName = message.Name
                };
        }

        var contents = new List<AIContent>();
        if (role != ChatRole.Tool)
        {
            contents.AddRange(ToAiContents(message));
        }
        else if (!string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            contents.Add(new TextContent(content));
        }

        if (message.ToolCalls is not null)
        {
            foreach (var toolCall in message.ToolCalls)
            {
                if (toolCall.Function is null)
                {
                    continue;
                }

                contents.Add(new FunctionCallContent(
                    toolCall.Id,
                    toolCall.Function.Name,
                    ParseToolArguments(toolCall.Function.Arguments)));
            }
        }

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            contents.Add(new FunctionResultContent(message.ToolCallId, content));
        }

        return new ChatMessage(role, contents)
        {
            AuthorName = message.Name
        };
    }

    private static List<AIContent> ToAiContents(ChatMessageDto message)
    {
        var contents = new List<AIContent>();

        if (message.ContentParts is { Count: > 0 })
        {
            foreach (var part in message.ContentParts)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    contents.Add(new TextContent(part.Text));
                }

                if (!string.IsNullOrWhiteSpace(part.ImageUrl))
                {
                    contents.Add(ToImageContent(part.ImageUrl, part.MediaType));
                }

                if (!string.IsNullOrWhiteSpace(part.VideoUrl))
                {
                    contents.Add(ToVideoContent(part.VideoUrl, part.MediaType));
                }
            }
        }
        else if (!string.IsNullOrEmpty(message.Content))
        {
            contents.Add(new TextContent(message.Content));
        }

        return contents;
    }

    private static AIContent ToImageContent(string imageUrl, string? mediaType)
    {
        var resolvedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? InferMediaType(imageUrl)
            : mediaType;

        return imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? new DataContent(new Uri(imageUrl), resolvedMediaType)
            : new UriContent(new Uri(imageUrl), resolvedMediaType);
    }

    private static AIContent ToVideoContent(string videoUrl, string? mediaType)
    {
        var resolvedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? InferMediaType(videoUrl)
            : mediaType;

        return videoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? new DataContent(new Uri(videoUrl), resolvedMediaType)
            : new UriContent(new Uri(videoUrl), resolvedMediaType);
    }

    private static string InferMediaType(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = uri.IndexOf(';');
            return separator > "data:".Length
                ? uri["data:".Length..separator]
                : "application/octet-stream";
        }

        var withoutQuery = uri.Split('?', '#')[0];
        return Path.GetExtension(withoutQuery).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "image/*"
        };
    }

    private static ChatMessageDto ToOpenAiMessage(ChatMessage message)
    {
        var toolCalls = message.Contents
            .OfType<FunctionCallContent>()
            .Select((toolCall, index) => new ToolCallDto(
                Id: string.IsNullOrWhiteSpace(toolCall.CallId) ? $"call_{index}" : toolCall.CallId,
                Type: "function",
                Function: new ToolCallFunctionDto(
                    Name: toolCall.Name,
                    Arguments: JsonSerializer.Serialize(toolCall.Arguments)),
                Index: null))
            .ToList();

        var toolResult = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();

        return new ChatMessageDto(
            Role: ResolveOpenAiRole(message.Role),
            Content: message.Text ?? string.Empty,
            Name: message.AuthorName,
            ToolCallId: toolResult?.CallId,
            ToolCalls: toolCalls.Count == 0 ? null : toolCalls);
    }

    private static string ResolveOpenAiRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return "system";
        }

        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        return role == ChatRole.Tool ? "tool" : "user";
    }

    private static IDictionary<string, object?> ParseToolArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments);
            return parsed?.ToDictionary(
                    static pair => pair.Key,
                    static pair => (object?)pair.Value.Clone())
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>
            {
                ["arguments"] = arguments
            };
        }
    }

    private static IList<string>? ExtractStopSequences(JsonElement? stop)
    {
        if (stop is not { } value ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var stopText = value.GetString();
            return string.IsNullOrEmpty(stopText) ? null : [stopText];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sequences = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(item.GetString()))
            {
                sequences.Add(item.GetString()!);
            }
        }

        return sequences.Count == 0 ? null : sequences;
    }

    private static ChatToolMode? ResolveToolMode(JsonElement? toolChoice, IList<Tool>? tools)
    {
        if (toolChoice is not { } value ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return tools is { Count: > 0 } ? ChatToolMode.Auto : null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.ToLowerInvariant() switch
            {
                "none" => ChatToolMode.None,
                "auto" => ChatToolMode.Auto,
                "required" => ChatToolMode.RequireAny,
                _ => null
            };
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("function", out var function) &&
            function.ValueKind == JsonValueKind.Object &&
            function.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            return ChatToolMode.RequireSpecific(name.GetString()!);
        }

        if (value.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "allowed_tools", StringComparison.OrdinalIgnoreCase))
        {
            return ChatToolMode.RequireAny;
        }

        return null;
    }

    private static ChatResponseFormat? ResolveResponseFormat(JsonElement? responseFormat)
    {
        if (responseFormat is not { } value ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return type.GetString()?.ToLowerInvariant() switch
        {
            "json_object" => ChatResponseFormat.Json,
            "json_schema" => ChatResponseFormat.Json,
            "text" => ChatResponseFormat.Text,
            _ => null
        };
    }

    private static ReasoningOptions? ResolveReasoning(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var effort = reasoningEffort.Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "minimal" => ReasoningEffort.Low,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.ExtraHigh,
            "extra_high" => ReasoningEffort.ExtraHigh,
            "extra-high" => ReasoningEffort.ExtraHigh,
            _ => (ReasoningEffort?)null
        };

        return effort is null ? null : new ReasoningOptions { Effort = effort.Value };
    }

    private static string TranslateFinishReason(ChatFinishReason? finishReason)
    {
        if (finishReason is null)
        {
            return "stop";
        }

        if (finishReason == ChatFinishReason.Stop)
        {
            return "stop";
        }

        if (finishReason == ChatFinishReason.Length)
        {
            return "length";
        }

        if (finishReason == ChatFinishReason.ContentFilter)
        {
            return "content_filter";
        }

        if (finishReason == ChatFinishReason.ToolCalls)
        {
            return "tool_calls";
        }

        return finishReason.Value.Value;
    }

    private static int ToTokenCount(long? tokenCount)
        => tokenCount is null
            ? 0
            : tokenCount > int.MaxValue
                ? int.MaxValue
                : (int)tokenCount.Value;

    private static IResult CreateProviderErrorResult(string model, Exception? exception)
    {
        // Context overflow — no provider's window is large enough for this prompt.
        if (exception is ContextOverflowException coe)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        message = $"The prompt requires {coe.RequiredTokens} tokens but the largest available " +
                                  $"context window could only accommodate {coe.Budget} tokens. " +
                                  $"Please reduce the prompt length.",
                        type    = "context_length_exceeded",
                        code    = "context_length_exceeded",
                        param   = (string?)null,
                        required_tokens        = coe.RequiredTokens,
                        largest_window_budget  = coe.Budget,
                        attempted_destinations = coe.AttemptedDestinations,
                    }
                },
                statusCode: StatusCodes.Status413RequestEntityTooLarge);
        }

        var statusCode = exception is ClientResultException { Status: 404 }
            ? StatusCodes.Status404NotFound
            : IsUnavailableException(exception)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
        var code = statusCode switch
        {
            StatusCodes.Status404NotFound => "model_not_found",
            StatusCodes.Status503ServiceUnavailable => "model_unavailable",
            _ => "provider_error"
        };
        var message = statusCode switch
        {
            StatusCodes.Status404NotFound => $"Model or deployment '{model}' was not found by the configured provider.",
            StatusCodes.Status503ServiceUnavailable => exception?.Message ?? $"Model '{model}' is currently unavailable.",
            _ => $"The configured provider failed while processing model '{model}'."
        };

        return Results.Json(
            new ErrorResponse(new ErrorDetail(message, "provider_error", code)),
            statusCode: statusCode);
    }

    private static bool IsUnavailableException(Exception? exception)
    {
        if (exception is not InvalidOperationException invalidOperation)
        {
            return false;
        }

        var message = invalidOperation.Message;
        return message.Contains("currently unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No currently available", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not loaded", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StreamingProbeResult(
        bool Success,
        ChatResponseUpdate? Update,
        IAsyncEnumerator<ChatResponseUpdate>? Enumerator,
        Exception? Exception)
    {
        public static readonly StreamingProbeResult Empty = new(true, null, null, null);
    }

    private static async Task<IChatClient> ResolveClientAsync(
        string model,
        IChatClient defaultClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        ILogger<ChatCompletionRequest>? logger,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var selectedClient = await modelSelectionResolver.ResolveAsync(model, cancellationToken);
        
        if (selectedClient is not null)
        {
            LogRouter(logger, new RouterResolveEvent(
                DirectTaskType,
                0,
                1,
                selectedClient.GetType().Name,
                sw.ElapsedMilliseconds));
            return selectedClient;
        }

        var unavailableModel = availabilityRegistry.FindModel(model, includeUnavailable: true);
        if (unavailableModel is { Enabled: false })
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(unavailableModel.ErrorMessage)
                    ? $"Model '{model}' is currently unavailable."
                    : $"Model '{model}' is currently unavailable: {unavailableModel.ErrorMessage}");
        }

        LogRouter(logger, new RouterResolveEvent(
            DirectTaskType,
            0,
            1,
            defaultClient.GetType().Name,
            sw.ElapsedMilliseconds));
        return defaultClient;
    }

    private static void LogRouter(ILogger? logger, object routerEvent, LogLevel? level = null)
    {
        if (logger is not null)
        {
            RouterLog.Write(logger, routerEvent, level);
        }
    }

    /// <summary>
    /// Translates Tool objects from OpenAI wire format to declaration-only MEAI AITools.
    /// Client-supplied tools must round-trip as tool calls instead of being invoked by the gateway.
    /// </summary>
    private static IList<AITool>? TranslateTools(IList<Tool>? tools, ILogger? logger)
    {
        if (tools == null || tools.Count == 0)
            return null;

        var aiTools = new List<AITool>();
        
        foreach (var tool in tools)
        {
            try
            {
                if (!string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(tool.Function.Name))
                {
                    continue;
                }

                aiTools.Add(new OpenAiFunctionToolDeclaration(
                    tool.Function.Name,
                    tool.Function.Description,
                    ToJsonElement(tool.Function.Parameters)));
                logger?.LogDebug("  ├─ Tool translated: {ToolName}", tool.Function.Name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "  ├─ Failed to translate tool {ToolName}", tool.Function.Name);
            }
        }

        return aiTools.Count > 0 ? aiTools : null;
    }

    private static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement element &&
            element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            return element.Clone();
        }

        return value is null
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object>() })
            : JsonSerializer.SerializeToElement(value);
    }

    private sealed class OpenAiFunctionToolDeclaration(
        string name,
        string? description,
        JsonElement jsonSchema) : AIFunctionDeclaration
    {
        public override string Name { get; } = name;

        public override string Description { get; } = description ?? string.Empty;

        public override JsonElement JsonSchema { get; } = jsonSchema.Clone();

        public override JsonElement? ReturnJsonSchema => null;
    }
}
