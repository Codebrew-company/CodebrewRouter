using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.LlmGateway.Api;

public interface IProtocolStore
{
    Task SaveResponseAsync(ResponseObject response, CancellationToken cancellationToken = default);

    Task<ResponseObject?> GetResponseAsync(string responseId, CancellationToken cancellationToken = default);

    Task<bool> DeleteResponseAsync(string responseId, CancellationToken cancellationToken = default);

    Task SaveConversationAsync(ConversationObject conversation, CancellationToken cancellationToken = default);

    Task<ConversationObject?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    Task AddConversationItemsAsync(string conversationId, IEnumerable<ConversationItem> items, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationItem>> ListConversationItemsAsync(
        string conversationId,
        int? limit = null,
        string? after = null,
        string? order = null,
        CancellationToken cancellationToken = default);

    Task<ConversationItem?> GetConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default);

    Task SaveA2ATaskAsync(A2ATask task, CancellationToken cancellationToken = default);

    Task<A2ATask?> GetA2ATaskAsync(string taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<A2ATask>> ListA2ATasksAsync(string? agentName = null, CancellationToken cancellationToken = default);

    Task SaveApiKeyAsync(AdminApiKey key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminApiKey>> ListApiKeysAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteApiKeyAsync(string keyId, CancellationToken cancellationToken = default);

    Task AddRouteDecisionAsync(RouteDecision decision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RouteDecision>> ListRouteDecisionsAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetDescriptor>> ListAssetsAsync(CancellationToken cancellationToken = default);

    Task AddUsageAsync(UsageRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageRecord>> ListUsageAsync(
        int limit = 100,
        int offset = 0,
        string? apiKeyId = null,
        CancellationToken cancellationToken = default);

    Task<SpendSummary> GetUsageSummaryAsync(string? apiKeyId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageDailyBucket>> GetUsageDailyAsync(int days = 30, CancellationToken cancellationToken = default);
}

public sealed class InMemoryProtocolStore : IProtocolStore
{
    private readonly ConcurrentDictionary<string, ResponseObject> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, A2ATask> _a2aTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AdminApiKey> _apiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RouteDecision> _routeDecisions = new();
    private readonly IReadOnlyList<AssetDescriptor> _assets =
    [
        new("asset_awesome_copilot", "awesome-copilot", "skill-pack", "Curated GitHub Copilot agents, skills, prompts, and instructions", true),
        new("asset_microsoft_learn_mcp", "microsoft-learn-mcp", "mcp", "Microsoft Learn MCP server profile for .NET and Azure help", true),
        new("asset_superpowers", "superpowers", "skill-pack", "Planning, TDD, debugging, and verification workflows", true)
    ];

    public Task SaveResponseAsync(ResponseObject response, CancellationToken cancellationToken = default)
    {
        _responses[response.Id] = response;
        return Task.CompletedTask;
    }

    public Task<ResponseObject?> GetResponseAsync(string responseId, CancellationToken cancellationToken = default)
        => Task.FromResult(_responses.GetValueOrDefault(responseId));

    public Task<bool> DeleteResponseAsync(string responseId, CancellationToken cancellationToken = default)
        => Task.FromResult(_responses.TryRemove(responseId, out _));

    public Task SaveConversationAsync(ConversationObject conversation, CancellationToken cancellationToken = default)
    {
        _conversations[conversation.Id] = new ConversationState(conversation);
        return Task.CompletedTask;
    }

    public Task<ConversationObject?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_conversations.GetValueOrDefault(conversationId)?.Conversation);

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_conversations.TryRemove(conversationId, out _));

    public Task AddConversationItemsAsync(string conversationId, IEnumerable<ConversationItem> items, CancellationToken cancellationToken = default)
    {
        var state = _conversations.GetOrAdd(conversationId, id => new ConversationState(ConversationObject.Create(id)));
        lock (state.Items)
        {
            foreach (var item in items)
            {
                state.Items.Add(item.EnsureIdentity());
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationItem>> ListConversationItemsAsync(
        string conversationId,
        int? limit = null,
        string? after = null,
        string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
        {
            return Task.FromResult<IReadOnlyList<ConversationItem>>([]);
        }

        List<ConversationItem> items;
        lock (state.Items)
        {
            items = [.. state.Items];
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            var index = items.FindIndex(item => string.Equals(item.Id, after, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                items = items[(index + 1)..];
            }
        }

        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase))
        {
            items.Reverse();
        }

        if (limit is > 0)
        {
            items = [.. items.Take(limit.Value)];
        }

        return Task.FromResult<IReadOnlyList<ConversationItem>>(items);
    }

    public Task<ConversationItem?> GetConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
        {
            return Task.FromResult<ConversationItem?>(null);
        }

        lock (state.Items)
        {
            return Task.FromResult(state.Items.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<bool> DeleteConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
        {
            return Task.FromResult(false);
        }

        lock (state.Items)
        {
            var removed = state.Items.RemoveAll(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase)) > 0;
            return Task.FromResult(removed);
        }
    }

    public Task SaveA2ATaskAsync(A2ATask task, CancellationToken cancellationToken = default)
    {
        _a2aTasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task<A2ATask?> GetA2ATaskAsync(string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(_a2aTasks.GetValueOrDefault(taskId));

    public Task<IReadOnlyList<A2ATask>> ListA2ATasksAsync(string? agentName = null, CancellationToken cancellationToken = default)
    {
        var tasks = _a2aTasks.Values
            .Where(task => string.IsNullOrWhiteSpace(agentName) || string.Equals(task.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(task => task.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<A2ATask>>(tasks);
    }

    public Task SaveApiKeyAsync(AdminApiKey key, CancellationToken cancellationToken = default)
    {
        _apiKeys[key.Id] = key;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminApiKey>> ListApiKeysAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AdminApiKey>>([.. _apiKeys.Values.OrderBy(key => key.CreatedAt)]);

    public Task<bool> DeleteApiKeyAsync(string keyId, CancellationToken cancellationToken = default)
        => Task.FromResult(_apiKeys.TryRemove(keyId, out _));

    public Task AddRouteDecisionAsync(RouteDecision decision, CancellationToken cancellationToken = default)
    {
        _routeDecisions.Enqueue(decision);
        while (_routeDecisions.Count > 500 && _routeDecisions.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RouteDecision>> ListRouteDecisionsAsync(int limit = 100, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RouteDecision>>([.. _routeDecisions.Reverse().Take(limit)]);

    public Task<IReadOnlyList<AssetDescriptor>> ListAssetsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_assets);

    public Task AddUsageAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        _usage.Enqueue(record);
        while (_usage.Count > 10_000 && _usage.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageRecord>> ListUsageAsync(
        int limit = 100,
        int offset = 0,
        string? apiKeyId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<UsageRecord>>(
        [
            .. _usage.Reverse()
                .Where(r => apiKeyId is null || string.Equals(r.ApiKeyId, apiKeyId, StringComparison.Ordinal))
                .Skip(offset)
                .Take(limit)
        ]);

    public Task<SpendSummary> GetUsageSummaryAsync(string? apiKeyId = null, CancellationToken cancellationToken = default)
    {
        var rows = _usage.Where(r => apiKeyId is null || string.Equals(r.ApiKeyId, apiKeyId, StringComparison.Ordinal)).ToArray();
        return Task.FromResult(new SpendSummary(
            "spend.summary",
            apiKeyId,
            rows.Length,
            rows.Sum(r => (long)r.TotalTokens),
            rows.Sum(r => r.CostUsd),
            rows.Sum(r => (long)r.PromptTokens),
            rows.Sum(r => (long)r.CompletionTokens)));
    }

    public Task<IReadOnlyList<UsageDailyBucket>> GetUsageDailyAsync(int days = 30, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1));
        var buckets = _usage
            .Where(r => r.CreatedAt.UtcDateTime >= cutoff)
            .GroupBy(r => r.CreatedAt.UtcDateTime.Date)
            .OrderBy(g => g.Key)
            .Select(g => new UsageDailyBucket(
                g.Key.ToString("yyyy-MM-dd"),
                g.Count(),
                g.Sum(r => (long)r.PromptTokens),
                g.Sum(r => (long)r.CompletionTokens),
                g.Sum(r => (long)r.TotalTokens),
                g.Sum(r => r.CostUsd)))
            .ToArray();
        return Task.FromResult<IReadOnlyList<UsageDailyBucket>>(buckets);
    }

    private readonly ConcurrentQueue<UsageRecord> _usage = new();

    private sealed record ConversationState(ConversationObject Conversation)
    {
        public List<ConversationItem> Items { get; } = [];
    }
}

public sealed record CreateResponseRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] JsonElement Input,
    [property: JsonPropertyName("instructions")] string? Instructions = null,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("store")] bool? Store = null,
    [property: JsonPropertyName("previous_response_id")] string? PreviousResponseId = null,
    [property: JsonPropertyName("conversation")] JsonElement? Conversation = null,
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("temperature")] float? Temperature = null,
    [property: JsonPropertyName("top_p")] float? TopP = null,
    [property: JsonPropertyName("max_output_tokens")] int? MaxOutputTokens = null,
    [property: JsonPropertyName("max_completion_tokens")] int? MaxCompletionTokens = null,
    [property: JsonPropertyName("reasoning")] JsonElement? Reasoning = null,
    [property: JsonPropertyName("tools")] IList<Tool>? Tools = null,
    [property: JsonPropertyName("tool_choice")] JsonElement? ToolChoice = null,
    [property: JsonPropertyName("parallel_tool_calls")] bool? ParallelToolCalls = null,
    [property: JsonPropertyName("include")] IList<string>? Include = null,
    [property: JsonPropertyName("background")] bool? Background = null);

public sealed record ResponseObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("output")] IList<ResponseOutputItem> Output,
    [property: JsonPropertyName("output_text")] string OutputText,
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null,
    [property: JsonPropertyName("previous_response_id")] string? PreviousResponseId = null,
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("usage")] Usage? Usage = null,
    [property: JsonPropertyName("error")] ErrorDetail? Error = null);

public sealed record ResponseOutputItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("content")] IList<ResponseContentPart>? Content = null,
    [property: JsonPropertyName("call_id")] string? CallId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("arguments")] string? Arguments = null,
    [property: JsonPropertyName("output")] string? Output = null);

public sealed record ResponseContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image_url")] string? ImageUrl = null,
    [property: JsonPropertyName("video_url")] string? VideoUrl = null);

public sealed record ResponseInputItemsList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IList<ResponseOutputItem> Data);

public sealed record TokenCountRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] JsonElement Input);

public sealed record TokenCountResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("input_tokens")] int InputTokens);

public sealed record CompactResponseRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] JsonElement Input,
    [property: JsonPropertyName("target_tokens")] int? TargetTokens = null);

public sealed record CompactResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("tokens_before")] int TokensBefore,
    [property: JsonPropertyName("tokens_after")] int TokensAfter);

public sealed record CreateConversationRequest(
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("items")] IList<ConversationItem>? Items = null);

public sealed record ConversationObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null)
{
    public static ConversationObject Create(string? id = null, IDictionary<string, string>? metadata = null)
        => new(
            string.IsNullOrWhiteSpace(id) ? Ids.New("conv") : id,
            "conversation",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            metadata);
}

public sealed record UpdateConversationRequest(
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null);

public sealed record CreateConversationItemsRequest(
    [property: JsonPropertyName("items")] IList<ConversationItem> Items);

public sealed record ConversationItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("created_at")] long? CreatedAt = null,
    [property: JsonPropertyName("raw")] JsonElement? Raw = null)
{
    public ConversationItem EnsureIdentity()
        => this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Ids.New(Type == "message" ? "msg" : "item") : Id,
            CreatedAt = CreatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
}

public sealed record ConversationItemsList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IList<ConversationItem> Data,
    [property: JsonPropertyName("has_more")] bool HasMore = false);

public sealed record A2ASendMessageRequest(
    [property: JsonPropertyName("message")] A2AMessage Message,
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null);

public sealed record A2AMessage(
    [property: JsonPropertyName("messageId")] string? MessageId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("parts")] IList<A2APart> Parts);

public sealed record A2APart(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("mimeType")] string? MimeType = null);

public sealed record A2ATask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("status")] A2ATaskStatus Status,
    [property: JsonPropertyName("artifacts")] IList<A2AArtifact> Artifacts,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("metadata")] IDictionary<string, string>? Metadata = null);

public sealed record A2ATaskStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

public sealed record A2AArtifact(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parts")] IList<A2APart> Parts);

public sealed record A2AAgentCard(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("capabilities")] IDictionary<string, object> Capabilities,
    [property: JsonPropertyName("skills")] IList<A2AAgentSkill> Skills);

public sealed record A2AAgentSkill(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("tags")] IList<string> Tags);

public sealed record AdminCreateApiKeyRequest(
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("allowed_models")] IList<string>? AllowedModels = null,
    [property: JsonPropertyName("allow_cloud")] bool AllowCloud = false,
    [property: JsonPropertyName("scopes")] IList<string>? Scopes = null);

public sealed record AdminApiKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("allowed_models")] IList<string> AllowedModels,
    [property: JsonPropertyName("allow_cloud")] bool AllowCloud,
    [property: JsonPropertyName("scopes")] IList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record SpendSummary(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("total_requests")] int TotalRequests,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    [property: JsonPropertyName("estimated_cost_usd")] decimal EstimatedCostUsd,
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens = 0,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens = 0);

/// <summary>One /v1 request in the usage ledger (P1.1).</summary>
public sealed record UsageRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("api_key_id")] string? ApiKeyId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("provider_model")] string? ProviderModel,
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens,
    [property: JsonPropertyName("cost_usd")] decimal CostUsd,
    [property: JsonPropertyName("latency_ms")] long LatencyMs,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("streamed")] bool Streamed);

/// <summary>Per-day usage aggregate for dashboard charts.</summary>
public sealed record UsageDailyBucket(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("requests")] int Requests,
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens,
    [property: JsonPropertyName("cost_usd")] decimal CostUsd);

public sealed record RouteDecision(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record AssetDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("enabled")] bool Enabled);

public static class Ids
{
    public static string New(string prefix)
        => $"{prefix}_{Guid.NewGuid():N}";
}
