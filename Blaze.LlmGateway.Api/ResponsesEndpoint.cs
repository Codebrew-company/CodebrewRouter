using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

public static class ResponsesEndpoint
{
    public static async Task<ResponseObject> CreateAsync(
        CreateResponseRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var availabilityRegistry = httpContext.RequestServices.GetRequiredService<IModelAvailabilityRegistry>();
        var messages = OpenAiProtocolMapper.ToChatMessages(request.Input);
        messages = OpenAiProtocolMapper.ApplyInstructions(messages, request.Instructions, request.Model, gatewayOptions);
        var options = OpenAiProtocolMapper.ToChatOptions(request, gatewayOptions);

        var selectedClient = await OpenAiProtocolMapper.ResolveClientAsync(
            request.Model,
            chatClient,
            modelSelectionResolver,
            availabilityRegistry,
            cancellationToken);
        var completion = await selectedClient.GetResponseAsync(messages, options, cancellationToken);
        var conversationId = ResolveConversationId(request.Conversation);
        var response = OpenAiProtocolMapper.ToResponseObject(request, completion, gatewayOptions, conversationId);

        if (request.Store.GetValueOrDefault(true) || !string.IsNullOrWhiteSpace(conversationId))
        {
            await store.SaveResponseAsync(response, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            await store.AddConversationItemsAsync(
                conversationId,
                [.. messages.Select(OpenAiProtocolMapper.ToConversationItem), .. response.Output.Select(OpenAiProtocolMapper.ToConversationItem)],
                cancellationToken);
        }

        await store.AddRouteDecisionAsync(
            new RouteDecision(Ids.New("route"), DateTimeOffset.UtcNow, request.Model, options.ModelId ?? request.Model, "responses.create"),
            cancellationToken);

        return response;
    }

    public static async Task<IResult> CreateResultAsync(
        CreateResponseRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request.Stream)
        {
            return await CreateStreamingAsync(
                request,
                chatClient,
                modelSelectionResolver,
                gatewayOptions,
                store,
                httpContext,
                cancellationToken);
        }

        return Results.Json(await CreateAsync(
            request,
            chatClient,
            modelSelectionResolver,
            gatewayOptions,
            store,
            httpContext,
            cancellationToken));
    }

    public static async Task<IResult> CreateStreamingAsync(
        CreateResponseRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var response = await CreateAsync(
            request with { Stream = false },
            chatClient,
            modelSelectionResolver,
            gatewayOptions,
            store,
            httpContext,
            cancellationToken);

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.Append("Cache-Control", "no-cache");
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        await WriteEventAsync(httpContext, "response.created", new { type = "response.created", response }, cancellationToken);
        await WriteEventAsync(httpContext, "response.in_progress", new { type = "response.in_progress", response_id = response.Id }, cancellationToken);
        foreach (var item in response.Output)
        {
            await WriteEventAsync(httpContext, "response.output_item.added", new { type = "response.output_item.added", response_id = response.Id, item }, cancellationToken);

            if (item.Content is null)
            {
                continue;
            }

            foreach (var content in item.Content.Where(static content => !string.IsNullOrEmpty(content.Text)))
            {
                await WriteEventAsync(
                    httpContext,
                    "response.output_text.delta",
                    new { type = "response.output_text.delta", response_id = response.Id, item_id = item.Id, delta = content.Text },
                    cancellationToken);
            }
        }

        await WriteEventAsync(httpContext, "response.completed", new { type = "response.completed", response }, cancellationToken);
        await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
        return Results.Empty;
    }

    public static async Task<IResult> GetAsync(string responseId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.GetResponseAsync(responseId, cancellationToken) is { } response
            ? Results.Json(response)
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Response '{responseId}' was not found.", "not_found", "response_not_found")));

    public static async Task<IResult> DeleteAsync(string responseId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.DeleteResponseAsync(responseId, cancellationToken)
            ? Results.Json(new { id = responseId, @object = "response.deleted", deleted = true })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"Response '{responseId}' was not found.", "not_found", "response_not_found")));

    public static async Task<IResult> CancelAsync(string responseId, IProtocolStore store, CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        if (response is null)
        {
            return Results.NotFound(new ErrorResponse(new ErrorDetail($"Response '{responseId}' was not found.", "not_found", "response_not_found")));
        }

        var cancelled = response with { Status = "cancelled" };
        await store.SaveResponseAsync(cancelled, cancellationToken);
        return Results.Json(cancelled);
    }

    public static async Task<ResponseInputItemsList> ListInputItemsAsync(
        string responseId,
        IProtocolStore store,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        return new ResponseInputItemsList("list", response?.Output ?? []);
    }

    public static TokenCountResponse CountInputTokens(TokenCountRequest request)
    {
        var text = OpenAiProtocolMapper.ExtractText(request.Input);
        var approxTokens = Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
        return new TokenCountResponse("response.input_tokens", approxTokens);
    }

    public static CompactResponse Compact(CompactResponseRequest request)
    {
        var text = OpenAiProtocolMapper.ExtractText(request.Input);
        var before = Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
        var target = request.TargetTokens ?? Math.Min(before, 1024);
        var maxChars = Math.Max(1, target * 4);
        var compacted = text.Length <= maxChars
            ? text
            : text[..maxChars];
        var after = Math.Max(1, (int)Math.Ceiling(compacted.Length / 4.0));
        return new CompactResponse("response.compact", compacted, before, after);
    }

    private static string? ResolveConversationId(JsonElement? conversation)
    {
        if (conversation is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    private static async Task WriteEventAsync(
        HttpContext httpContext,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        await httpContext.Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
}
