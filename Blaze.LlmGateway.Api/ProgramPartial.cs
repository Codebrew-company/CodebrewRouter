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
            IOptions<LlmGatewayOptions> gatewayOptions,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ChatCompletionsEndpoint.HandleAsync(req, chatClient, modelSelectionResolver, gatewayOptions, httpContext, ct))
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
        app.MapGet("/v1/models", (
            IModelCatalog modelCatalog,
            IModelAvailabilityRegistry availabilityRegistry,
            IOptions<LlmGatewayOptions> options,
            CancellationToken ct) =>
            ModelsEndpoint.HandleAsync(modelCatalog, availabilityRegistry, options, ct))
        .WithName("ListModels")
        .WithTags(DiscoveryTag)
        .WithSummary("List available models")
        .WithDescription("Returns the current model catalog exposed by the gateway, including unavailable configured offline models with error details.")
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

        app.MapGet("/v1/models/{modelId}", (
            string modelId,
            IModelCatalog modelCatalog,
            IModelAvailabilityRegistry availabilityRegistry,
            IOptions<LlmGatewayOptions> options,
            CancellationToken ct) =>
            ModelsEndpoint.HandleVirtualModelAsync(modelId, modelCatalog, availabilityRegistry, options, ct))
        .WithName("GetVirtualModel")
        .WithTags(DiscoveryTag)
        .WithSummary("Get virtual model details")
        .WithDescription("Returns details for a configured virtual model, including backing provider models and fallback order.")
        .Produces<CodebrewRouterModelsResponse>(StatusCodes.Status200OK, "application/json")
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound, "application/json")
        .Produces(StatusCodes.Status500InternalServerError)
        .WithMetadata(new EndpointNameMetadata("get-virtual-model"));

        app.MapPost("/v1/responses", async (
            CreateResponseRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            await ResponsesEndpoint.CreateResultAsync(req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("CreateResponse")
        .WithTags(OpenAiCompatibleTag)
        .WithSummary("Create a response")
        .WithDescription("OpenAI-compatible Responses endpoint backed by the CodebrewRouter agent/model pipeline.")
        .Accepts<CreateResponseRequest>("application/json")
        .Produces<ResponseObject>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status200OK)
        .WithMetadata(new EndpointNameMetadata("create-response"));

        app.MapGet("/v1/responses/{responseId}", (
            string responseId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ResponsesEndpoint.GetAsync(responseId, protocolStore, ct))
        .WithName("GetResponse")
        .WithTags(OpenAiCompatibleTag)
        .Produces<ResponseObject>(StatusCodes.Status200OK, "application/json")
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound, "application/json")
        .WithMetadata(new EndpointNameMetadata("get-response"));

        app.MapDelete("/v1/responses/{responseId}", (
            string responseId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ResponsesEndpoint.DeleteAsync(responseId, protocolStore, ct))
        .WithName("DeleteResponse")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("delete-response"));

        app.MapPost("/v1/responses/{responseId}/cancel", (
            string responseId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ResponsesEndpoint.CancelAsync(responseId, protocolStore, ct))
        .WithName("CancelResponse")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("cancel-response"));

        app.MapGet("/v1/responses/{responseId}/input_items", (
            string responseId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ResponsesEndpoint.ListInputItemsAsync(responseId, protocolStore, ct))
        .WithName("ListResponseInputItems")
        .WithTags(OpenAiCompatibleTag)
        .Produces<ResponseInputItemsList>(StatusCodes.Status200OK, "application/json")
        .WithMetadata(new EndpointNameMetadata("list-response-input-items"));

        app.MapPost("/v1/responses/input_tokens", (TokenCountRequest req) =>
            ResponsesEndpoint.CountInputTokens(req))
        .WithName("CountResponseInputTokens")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("count-response-input-tokens"));

        app.MapPost("/v1/responses/compact", (CompactResponseRequest req) =>
            ResponsesEndpoint.Compact(req))
        .WithName("CompactResponseInput")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("compact-response-input"));

        app.MapPost("/v1/conversations", (
            CreateConversationRequest req,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.CreateAsync(req, protocolStore, ct))
        .WithName("CreateConversation")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("create-conversation"));

        app.MapGet("/v1/conversations/{conversationId}", (
            string conversationId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.GetAsync(conversationId, protocolStore, ct))
        .WithName("GetConversation")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("get-conversation"));

        app.MapPost("/v1/conversations/{conversationId}", (
            string conversationId,
            UpdateConversationRequest req,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.UpdateAsync(conversationId, req, protocolStore, ct))
        .WithName("UpdateConversation")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("update-conversation"));

        app.MapDelete("/v1/conversations/{conversationId}", (
            string conversationId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.DeleteAsync(conversationId, protocolStore, ct))
        .WithName("DeleteConversation")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("delete-conversation"));

        app.MapGet("/v1/conversations/{conversationId}/items", (
            string conversationId,
            int? limit,
            string? after,
            string? order,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.ListItemsAsync(conversationId, limit, after, order, protocolStore, ct))
        .WithName("ListConversationItems")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("list-conversation-items"));

        app.MapPost("/v1/conversations/{conversationId}/items", (
            string conversationId,
            CreateConversationItemsRequest req,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.AddItemsAsync(conversationId, req, protocolStore, ct))
        .WithName("CreateConversationItems")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("create-conversation-items"));

        app.MapGet("/v1/conversations/{conversationId}/items/{itemId}", (
            string conversationId,
            string itemId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.GetItemAsync(conversationId, itemId, protocolStore, ct))
        .WithName("GetConversationItem")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("get-conversation-item"));

        app.MapDelete("/v1/conversations/{conversationId}/items/{itemId}", (
            string conversationId,
            string itemId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            ConversationsEndpoint.DeleteItemAsync(conversationId, itemId, protocolStore, ct))
        .WithName("DeleteConversationItem")
        .WithTags(OpenAiCompatibleTag)
        .WithMetadata(new EndpointNameMetadata("delete-conversation-item"));

        app.MapGet("/.well-known/agent-card.json", (
            HttpContext httpContext,
            IOptions<LlmGatewayOptions> options) =>
            A2AEndpoint.AgentCard(httpContext, options))
        .WithName("GetA2AAgentCard")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("get-a2a-agent-card"));

        app.MapGet("/a2a/{agentName}/.well-known/agent-card.json", (
            string agentName,
            HttpContext httpContext,
            IOptions<LlmGatewayOptions> options) =>
            A2AEndpoint.AgentCard(httpContext, options, agentName))
        .WithName("GetA2AAgentCardForAgent")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("get-a2a-agent-card-for-agent"));

        app.MapPost("/a2a/{agentName}/message:send", (
            string agentName,
            A2ASendMessageRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            A2AEndpoint.SendMessageResultAsync(agentName, req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("A2ASendMessage")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-send-message"));

        app.MapPost("/a2a/{agentName}/message:stream", (
            string agentName,
            A2ASendMessageRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            A2AEndpoint.StreamMessageAsync(agentName, req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("A2AStreamMessage")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-stream-message"));

        app.MapPost("/a2a/{agentName}/tasks/send", (
            string agentName,
            A2ASendMessageRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            A2AEndpoint.SendMessageResultAsync(agentName, req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("A2ASendTask")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-send-task"));

        app.MapPost("/a2a/{agentName}/tasks/sendSubscribe", (
            string agentName,
            A2ASendMessageRequest req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            A2AEndpoint.StreamMessageAsync(agentName, req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("A2ASendSubscribe")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-send-subscribe"));

        app.MapGet("/a2a/{agentName}/tasks", (
            string agentName,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            A2AEndpoint.ListTasksAsync(agentName, protocolStore, ct))
        .WithName("A2AListTasks")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-list-tasks"));

        app.MapGet("/a2a/{agentName}/tasks/{taskId}", (
            string taskId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            A2AEndpoint.GetTaskAsync(taskId, protocolStore, ct))
        .WithName("A2AGetTask")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-get-task"));

        app.MapPost("/a2a/{agentName}/tasks/{taskId}/cancel", (
            string taskId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            A2AEndpoint.CancelTaskAsync(taskId, protocolStore, ct))
        .WithName("A2ACancelTask")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-cancel-task"));

        app.MapGet("/a2a/{agentName}/tasks/{taskId}/artifacts", (
            string taskId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            A2AEndpoint.GetArtifactsAsync(taskId, protocolStore, ct))
        .WithName("A2AGetArtifacts")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-get-artifacts"));

        app.MapGet("/a2a/{agentName}/tasks/{taskId}/pushNotificationConfig", (string taskId) =>
            A2AEndpoint.PushNotificationConfig(taskId))
        .WithName("A2APushNotificationConfig")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-push-notification-config"));

        app.MapPost("/a2a/{agentName}", (
            string agentName,
            JsonElement req,
            IChatClient chatClient,
            IModelSelectionResolver modelSelectionResolver,
            IOptions<LlmGatewayOptions> gatewayOptions,
            IProtocolStore protocolStore,
            HttpContext httpContext,
            CancellationToken ct) =>
            A2AEndpoint.JsonRpcAsync(agentName, req, chatClient, modelSelectionResolver, gatewayOptions, protocolStore, httpContext, ct))
        .WithName("A2AJsonRpc")
        .WithTags("A2A")
        .WithMetadata(new EndpointNameMetadata("a2a-json-rpc"));

        app.MapPost("/admin/keys", (
            AdminCreateApiKeyRequest req,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.CreateKeyAsync(req, protocolStore, ct))
        .WithName("CreateAdminKey")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("create-admin-key"));

        app.MapGet("/admin/keys", (
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.ListKeysAsync(protocolStore, ct))
        .WithName("ListAdminKeys")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("list-admin-keys"));

        app.MapDelete("/admin/keys/{keyId}", (
            string keyId,
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.DeleteKeyAsync(keyId, protocolStore, ct))
        .WithName("DeleteAdminKey")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("delete-admin-key"));

        app.MapGet("/admin/spend", (string? keyId) =>
            AdminEndpoint.Spend(keyId))
        .WithName("GetAdminSpend")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("get-admin-spend"));

        app.MapGet("/admin/routes/recent", (
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.RecentRoutesAsync(protocolStore, ct))
        .WithName("GetRecentRoutes")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("get-recent-routes"));

        app.MapGet("/admin/assets", (
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.AssetsAsync(protocolStore, ct))
        .WithName("GetAssets")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("get-assets"));

        app.MapPost("/admin/assets/sync", (
            IProtocolStore protocolStore,
            CancellationToken ct) =>
            AdminEndpoint.SyncAssetsAsync(protocolStore, ct))
        .WithName("SyncAssets")
        .WithTags("Admin")
        .WithMetadata(new EndpointNameMetadata("sync-assets"));
    }
}
