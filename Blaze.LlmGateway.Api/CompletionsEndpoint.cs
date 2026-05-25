using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Blaze.LlmGateway.Core.ModelCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ClientModel;
using System.Text.Json;
using Blaze.LlmGateway.Infrastructure;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Handler for POST /v1/completions endpoint.
/// Legacy text-only completions endpoint with streaming support.
/// </summary>
public static class CompletionsEndpoint
{
    /// <summary>Handle text completion requests</summary>
    public static async Task<IResult> HandleAsync(
        TextCompletionRequest req,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var availabilityRegistry = httpContext.RequestServices.GetRequiredService<IModelAvailabilityRegistry>();

        // Validate required fields
        if (string.IsNullOrWhiteSpace(req.Model))
            return Results.BadRequest(new
            {
                error = new
                {
                    message = "Missing required field: model",
                    type = "invalid_request_error",
                    code = "missing_field"
                }
            });

        if (string.IsNullOrWhiteSpace(req.Prompt))
            return Results.BadRequest(new
            {
                error = new
                {
                    message = "Missing required field: prompt",
                    type = "invalid_request_error",
                    code = "missing_field"
                }
            });

        // Convert text prompt to chat format
        var messages = new List<ChatMessage> { new(ChatRole.User, req.Prompt) };

        // Build ChatOptions from request
        var options = new ChatOptions
        {
            Temperature = req.Temperature,
            MaxOutputTokens = req.MaxTokens,
            TopP = req.TopP,
            FrequencyPenalty = req.FrequencyPenalty,
            PresencePenalty = req.PresencePenalty
        };

        if (req.Stream)
        {
            // Streaming response via SSE
            return await HandleStreamingAsync(httpContext, messages, options, req.Model, chatClient, modelSelectionResolver, availabilityRegistry, ct);
        }
        else
        {
            // Non-streaming response
            return await HandleNonStreamingAsync(messages, options, req.Model, chatClient, modelSelectionResolver, availabilityRegistry, ct);
        }
    }

    private static async Task<IResult> HandleStreamingAsync(
        HttpContext httpContext,
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        CancellationToken ct)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.Append("Cache-Control", "no-cache");
        httpContext.Response.Headers.Append("Connection", "keep-alive");
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        var id = $"cmpl-{Guid.NewGuid().ToString("N").Substring(0, 24)}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            var selectedClient = await ResolveClientAsync(model, chatClient, modelSelectionResolver, availabilityRegistry, ct);
            await foreach (var update in selectedClient.GetStreamingResponseAsync(messages, options, ct))
            {
                var choice = new { text = update.Text, index = 0, finish_reason = (string?)null };
                var chunk = new { id, @object = "text_completion.chunk", created, model, choices = new[] { choice } };
                var json = JsonSerializer.Serialize(chunk);
                await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            await httpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleNonStreamingAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        string model,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        CancellationToken ct)
    {
        try
        {
            var selectedClient = await ResolveClientAsync(model, chatClient, modelSelectionResolver, availabilityRegistry, ct);
            var completion = await selectedClient.GetResponseAsync(messages, options, ct);

            var id = $"cmpl-{Guid.NewGuid().ToString("N").Substring(0, 24)}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var choices = new List<TextChoice>
            {
                new(Index: 0, Text: completion.Messages?.FirstOrDefault()?.Text ?? "", FinishReason: "stop")
            };

            var result = new TextCompletionResponse(
                Id: id,
                Object: "text_completion",
                Created: created,
                Model: model,
                Choices: choices,
                Usage: null
            );

            return Results.Json(result);
        }
        catch (Exception ex)
        {
            return CreateProviderErrorResult(model, ex);
        }
    }

    private static async Task<IChatClient> ResolveClientAsync(
        string model,
        IChatClient defaultClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        CancellationToken cancellationToken)
    {
        var selectedClient = await modelSelectionResolver.ResolveAsync(model, cancellationToken);
        if (selectedClient is not null)
        {
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

        return defaultClient;
    }

    private static IResult CreateProviderErrorResult(string model, Exception? exception)
    {
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
}
