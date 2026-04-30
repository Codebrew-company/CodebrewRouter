using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ClientModel;
using System.Text.Json;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Handler for POST /v1/chat/completions endpoint.
/// OpenAI-compatible chat completion endpoint with streaming support.
/// </summary>
public static class ChatCompletionsEndpoint
{
    /// <summary>Handle chat completion requests</summary>
    public static async Task<IResult> HandleAsync(
        ChatCompletionRequest req,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var logger = httpContext.RequestServices.GetService(typeof(ILogger<ChatCompletionRequest>)) as ILogger<ChatCompletionRequest>;
        var availabilityRegistry = httpContext.RequestServices.GetRequiredService<IModelAvailabilityRegistry>();
        
        logger?.LogInformation("📨 Chat completion request received - Model: {Model}, Stream: {Stream}, Messages: {MessageCount}", 
            req.Model, req.Stream, req.Messages?.Count ?? 0);

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
            var role = msg.Role.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.User
            };
            
            var chatMsg = new ChatMessage(role, msg.Content);
            messages.Add(chatMsg);
            logger?.LogDebug("  ├─ Message added: {Role} - {ContentPreview}", 
                role, msg.Content.Substring(0, Math.Min(50, msg.Content.Length)));
        }

        // Build ChatOptions from request
        var options = new ChatOptions
        {
            Temperature = req.Temperature,
            MaxOutputTokens = req.MaxTokens,
            TopP = req.TopP,
            FrequencyPenalty = req.FrequencyPenalty,
            PresencePenalty = req.PresencePenalty
        };
        logger?.LogDebug("  ├─ ChatOptions: Temp={Temperature}, MaxTokens={MaxTokens}, TopP={TopP}", 
            req.Temperature, req.MaxTokens, req.TopP);

        if (req.Stream)
        {
            logger?.LogInformation("  └─ Using STREAMING mode");
            // Streaming response via SSE
            return await HandleStreamingAsync(httpContext, messages, options, req.Model, req.Tools, chatClient, modelSelectionResolver, availabilityRegistry, logger, ct);
        }
        else
        {
            logger?.LogInformation("  └─ Using NON-STREAMING mode");
            // Non-streaming response
            return await HandleNonStreamingAsync(messages, options, req.Model, req.Tools, chatClient, modelSelectionResolver, availabilityRegistry, logger, ct);
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
            var firstChunk = await TryGetFirstStreamingUpdateAsync(selectedClient, messages, options, ct);
            if (!firstChunk.Success)
            {
                logger?.LogWarning(firstChunk.Exception, "Provider failed before streaming started for model {Model}", model);
                return CreateProviderErrorResult(model, firstChunk.Exception);
            }

            enumerator = firstChunk.Enumerator;
            var chunkCount = 0;
            logger?.LogInformation("⏳ Starting streaming response - ID: {RequestId}", id);

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
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Provider stream failed after {ChunkCount} chunks for model {Model}", chunkCount, model);
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
            
            logger?.LogInformation("✅ Stream completed - Total chunks: {ChunkCount}", chunkCount);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "❌ Streaming error after {ChunkCount} chunks", 
                httpContext.Response.HasStarted ? "headers sent" : "headers not sent");
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

            if (httpContext.Response.HasStarted)
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

            logger?.LogInformation("⏳ Getting non-streaming response from chat client");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var selectedClient = await ResolveClientAsync(model, chatClient, modelSelectionResolver, availabilityRegistry, logger, ct);
            
            var completion = await selectedClient.GetResponseAsync(messages, options, ct);
            
            sw.Stop();
            logger?.LogInformation("✅ Received response in {ElapsedMs}ms", sw.ElapsedMilliseconds);

            var id = $"chatcmpl-{Guid.NewGuid().ToString("N").Substring(0, 24)}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var responseText = completion.Messages?.FirstOrDefault()?.Text ?? "";
            var choices = new List<Choice>
            {
                new(
                    Index: 0,
                    Message: new ChatMessageDto(Role: "assistant", Content: responseText),
                    Delta: null,
                    FinishReason: "stop"
                )
            };

            var result = new ChatCompletionResponse(
                Id: id,
                Object: "chat.completion",
                Created: created,
                Model: model,
                Choices: choices,
                Usage: null
            );

            logger?.LogDebug("  └─ Response text length: {TextLength}", responseText.Length);
            return Results.Json(result);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "❌ Error in non-streaming handler");
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
        var choice = new { index = 0, delta = new { content = update.Text }, finish_reason = (string?)null };
        var chunk = new { id, @object = "chat.completion.chunk", created, model, choices = new[] { choice } };
        var contentJson = JsonSerializer.Serialize(chunk);
        await httpContext.Response.WriteAsync($"data: {contentJson}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

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
            : exception is InvalidOperationException invalidOperation &&
              invalidOperation.Message.Contains("currently unavailable", StringComparison.OrdinalIgnoreCase)
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
        var selectedClient = await modelSelectionResolver.ResolveAsync(model, cancellationToken);
        if (selectedClient is not null)
        {
            logger?.LogInformation("🎛️ Honoring selected model {Model}", model);
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

        logger?.LogInformation("🧭 No direct client match for model {Model}; using routed default client", model);
        return defaultClient;
    }

    /// <summary>
    /// Translates Tool objects from OpenAI wire format to MEAI AITool declarations.
    /// For Phase 1, we accept and log tool definitions; full execution handling is Phase 2.
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
                // Create a simple AIFunction that represents the tool schema
                // In Phase 2, actual implementation will invoke external tool handlers
                var aiFunction = AIFunctionFactory.Create(
                    new Func<string>(() => throw new NotImplementedException($"Tool '{tool.Function.Name}' execution not yet implemented"))
                );
                
                aiTools.Add(aiFunction);
                logger?.LogDebug("  ├─ Tool translated: {ToolName}", tool.Function.Name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "  ├─ Failed to translate tool {ToolName}", tool.Function.Name);
            }
        }

        return aiTools.Count > 0 ? aiTools : null;
    }
}
