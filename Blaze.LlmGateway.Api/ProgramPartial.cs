using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Extension methods for registering LiteLLM-compatible endpoints.
/// </summary>
public static class LiteLlmEndpoints
{
    private const string OpenAiCompatibleTag = "OpenAI-compatible";
    private const string DiscoveryTag = "Discovery";

    public static void RegisterLiteLlmEndpoints(this WebApplication app)
    {
        // Chat completions endpoint
        app.MapPost("/v1/chat/completions", async (
            ChatCompletionRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ChatCompletionsEndpoint.HandleAsync(req, chatClient, modelSelectionResolver, httpContext, ct))
        .WithName("ChatCompletions")
        .WithTags(OpenAiCompatibleTag)
        .WithSummary("Create a chat completion")
        .WithDescription("Accepts an OpenAI-compatible chat request and returns either a JSON completion or an SSE stream, depending on the `stream` flag. Routed models may resolve to a specific backend provider when the requested model matches a configured catalog entry.")
        .Accepts<ChatCompletionRequest>("application/json")
        .Produces<ChatCompletionResponse>(StatusCodes.Status200OK, "application/json")
        .Produces<ChatCompletionStreamChunk>(StatusCodes.Status200OK, "text/event-stream")
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("chat-completions"));

        // Text completions endpoint
        app.MapPost("/v1/completions", async (
            TextCompletionRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            HttpContext httpContext,
            CancellationToken ct) =>
            await CompletionsEndpoint.HandleAsync(req, chatClient, modelSelectionResolver, httpContext, ct))
        .WithName("TextCompletions")
        .WithTags(OpenAiCompatibleTag)
        .WithSummary("Create a legacy text completion")
        .WithDescription("Accepts an OpenAI-compatible legacy text completion request. Use this endpoint for prompt-only completion flows when a chat message envelope is not desired. When `stream=true`, the response is emitted as `text/event-stream` and ends with `data: [DONE]`.")
        .Accepts<TextCompletionRequest>("application/json")
        .Produces<TextCompletionResponse>(StatusCodes.Status200OK, "application/json")
        .Produces<TextCompletionStreamChunk>(StatusCodes.Status200OK, "text/event-stream")
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("text-completions"));

        // Models endpoint
        app.MapGet("/v1/models", (IModelCatalog modelCatalog, CancellationToken ct) =>
            ModelsEndpoint.HandleAsync(modelCatalog, ct))
        .WithName("ListModels")
        .WithTags(DiscoveryTag)
        .WithSummary("List available models")
        .WithDescription("Returns the currently available model catalog exposed by the gateway, including the provider backing each model identifier.")
        .Produces<ModelsResponse>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("list-models"));

        app.MapGet("/v1/models/diagnostics", (ModelAvailabilityRegistry registry) =>
            ModelsEndpoint.HandleDiagnosticsAsync(registry))
        .WithName("ListModelDiagnostics")
        .WithTags(DiscoveryTag)
        .WithSummary("List model connectivity diagnostics")
        .WithDescription("Returns all configured models and providers, including unavailable entries with the last probe error.")
        .Produces<ModelDiagnosticsResponse>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("list-model-diagnostics"));

        app.MapGet("/v1/models/codebrewRouter", (
            IModelCatalog modelCatalog,
            IModelAvailabilityRegistry availabilityRegistry,
            IOptions<LlmGatewayOptions> options,
            CancellationToken ct) =>
            ModelsEndpoint.HandleCodebrewRouterAsync(modelCatalog, availabilityRegistry, options, ct))
        .WithName("GetCodebrewRouterModel")
        .WithTags(DiscoveryTag)
        .WithSummary("Get CodebrewRouter model details")
        .WithDescription("Returns the virtual CodebrewRouter model, backing provider models currently visible to the gateway, and the configured provider fallback order by task type.")
        .Produces<CodebrewRouterModelsResponse>(StatusCodes.Status200OK, "application/json")
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("get-codebrewrouter-model"));
    }
}
