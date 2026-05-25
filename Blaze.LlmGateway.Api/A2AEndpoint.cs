using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

public static class A2AEndpoint
{
    public static A2AAgentCard AgentCard(HttpContext httpContext, IOptions<LlmGatewayOptions> options, string? agentName = null)
    {
        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var models = options.Value.GetEffectiveVirtualModels()
            .Where(model => string.IsNullOrWhiteSpace(agentName) || string.Equals(model.ModelId, agentName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var name = string.IsNullOrWhiteSpace(agentName) ? "CodebrewRouter" : agentName;
        return new A2AAgentCard(
            Name: name,
            Description: "CodebrewRouter OpenAI-compatible and A2A-capable agent gateway.",
            Url: string.IsNullOrWhiteSpace(agentName) ? $"{baseUrl}/a2a/codebrewRouter" : $"{baseUrl}/a2a/{agentName}",
            Version: "0.1.0",
            Capabilities: new Dictionary<string, object>
            {
                ["streaming"] = true,
                ["pushNotifications"] = true,
                ["stateTransitionHistory"] = true
            },
            Skills: models.Length == 0
                ?
                [
                    new A2AAgentSkill("codebrewRouter", "codebrewRouter", "General routing and chat agent.", ["chat", "routing"])
                ]
                : models.Select(model => new A2AAgentSkill(
                        model.ModelId,
                        model.ModelId,
                        $"{model.Provider} virtual model profile.",
                        BuildTags(model)))
                    .ToArray());
    }

    public static async Task<A2ATask> SendMessageAsync(
        string agentName,
        A2ASendMessageRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var availabilityRegistry = httpContext.RequestServices.GetRequiredService<IModelAvailabilityRegistry>();
        var text = string.Join("\n", request.Message.Parts.Select(part => part.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));
        var messages = new List<ChatMessage> { new(ChatRole.User, text) };
        messages = OpenAiProtocolMapper.ApplyInstructions(messages, null, agentName, gatewayOptions);

        var selected = await OpenAiProtocolMapper.ResolveClientAsync(
            agentName,
            chatClient,
            modelSelectionResolver,
            availabilityRegistry,
            cancellationToken);
        var completion = await selected.GetResponseAsync(
            messages,
            new ChatOptions
            {
                ModelId = agentName,
                ResponseFormat = OpenAiProtocolMapper.GetResponseFormatForModel(agentName, gatewayOptions)
            },
            cancellationToken);
        var responseText = OpenAiProtocolMapper.NormalizeAssistantContent(
            agentName,
            completion.Text ?? completion.Messages?.FirstOrDefault()?.Text ?? string.Empty,
            gatewayOptions);
        var task = new A2ATask(
            Id: Ids.New("task"),
            AgentName: agentName,
            Status: new A2ATaskStatus("completed", DateTimeOffset.UtcNow),
            Artifacts:
            [
                new A2AArtifact(
                    ArtifactId: Ids.New("artifact"),
                    Name: "response",
                    Parts: [new A2APart("text", responseText)])
            ],
            CreatedAt: DateTimeOffset.UtcNow,
            Metadata: request.Metadata);

        await store.SaveA2ATaskAsync(task, cancellationToken);
        await store.AddRouteDecisionAsync(
            new RouteDecision(Ids.New("route"), DateTimeOffset.UtcNow, agentName, agentName, "a2a.send"),
            cancellationToken);
        return task;
    }

    public static async Task<IResult> SendMessageResultAsync(
        string agentName,
        A2ASendMessageRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => Results.Json(await SendMessageAsync(
            agentName,
            request,
            chatClient,
            modelSelectionResolver,
            gatewayOptions,
            store,
            httpContext,
            cancellationToken));

    public static async Task<IResult> StreamMessageAsync(
        string agentName,
        A2ASendMessageRequest request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var task = await SendMessageAsync(
            agentName,
            request,
            chatClient,
            modelSelectionResolver,
            gatewayOptions,
            store,
            httpContext,
            cancellationToken);

        httpContext.Response.ContentType = "text/event-stream";
        await WriteEventAsync(httpContext, "taskStatusUpdate", new { taskId = task.Id, status = task.Status }, cancellationToken);
        foreach (var artifact in task.Artifacts)
        {
            await WriteEventAsync(httpContext, "taskArtifactUpdate", new { taskId = task.Id, artifact }, cancellationToken);
        }

        await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        return Results.Empty;
    }

    public static async Task<IResult> GetTaskAsync(string taskId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.GetA2ATaskAsync(taskId, cancellationToken) is { } task
            ? Results.Json(task)
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"A2A task '{taskId}' was not found.", "not_found", "a2a_task_not_found")));

    public static async Task<IReadOnlyList<A2ATask>> ListTasksAsync(string agentName, IProtocolStore store, CancellationToken cancellationToken)
        => await store.ListA2ATasksAsync(agentName, cancellationToken);

    public static async Task<IResult> CancelTaskAsync(string taskId, IProtocolStore store, CancellationToken cancellationToken)
    {
        var task = await store.GetA2ATaskAsync(taskId, cancellationToken);
        if (task is null)
        {
            return Results.NotFound(new ErrorResponse(new ErrorDetail($"A2A task '{taskId}' was not found.", "not_found", "a2a_task_not_found")));
        }

        var cancelled = task with { Status = new A2ATaskStatus("cancelled", DateTimeOffset.UtcNow) };
        await store.SaveA2ATaskAsync(cancelled, cancellationToken);
        return Results.Json(cancelled);
    }

    public static async Task<IResult> GetArtifactsAsync(string taskId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.GetA2ATaskAsync(taskId, cancellationToken) is { } task
            ? Results.Json(new { @object = "list", data = task.Artifacts })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"A2A task '{taskId}' was not found.", "not_found", "a2a_task_not_found")));

    public static IResult PushNotificationConfig(string taskId)
        => Results.Json(new
        {
            taskId,
            pushNotificationConfig = new
            {
                supported = true,
                signing = "hmac-sha256"
            }
        });

    public static async Task<IResult> JsonRpcAsync(
        string agentName,
        JsonElement request,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IProtocolStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var id = request.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;

        if (method is "message/send" or "tasks/send")
        {
            var messageRequest = request.TryGetProperty("params", out var parameters)
                ? parameters.Deserialize<A2ASendMessageRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;

            if (messageRequest is null)
            {
                return Results.Json(JsonRpcError(id, -32602, "Invalid params"));
            }

            var task = await SendMessageAsync(
                agentName,
                messageRequest,
                chatClient,
                modelSelectionResolver,
                gatewayOptions,
                store,
                httpContext,
                cancellationToken);
            return Results.Json(new { jsonrpc = "2.0", id, result = task });
        }

        return Results.Json(JsonRpcError(id, -32601, $"Method '{method}' is not supported."));
    }

    private static IList<string> BuildTags(VirtualModelOptions model)
        =>
        [
            "chat",
            model.Source,
            model.Provider,
            string.IsNullOrWhiteSpace(model.Extends) ? "root" : $"extends:{model.Extends}"
        ];

    private static object JsonRpcError(JsonElement id, int code, string message)
        => new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        };

    private static async Task WriteEventAsync(HttpContext context, string eventName, object payload, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
