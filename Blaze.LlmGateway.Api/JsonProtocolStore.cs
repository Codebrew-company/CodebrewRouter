using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.LlmGateway.Api;

public sealed class JsonProtocolStore : IProtocolStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private ProtocolStoreSnapshot _snapshot;

    public JsonProtocolStore(string path)
    {
        _path = path;
        _snapshot = Load(path);
    }

    public Task SaveResponseAsync(ResponseObject response, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snapshot.Responses[response.Id] = response;
            Save();
        }

        return Task.CompletedTask;
    }

    public Task<ResponseObject?> GetResponseAsync(string responseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshot.Responses.GetValueOrDefault(responseId));
        }
    }

    public Task<bool> DeleteResponseAsync(string responseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var removed = _snapshot.Responses.Remove(responseId);
            if (removed)
            {
                Save();
            }

            return Task.FromResult(removed);
        }
    }

    public Task SaveConversationAsync(ConversationObject conversation, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_snapshot.Conversations.TryGetValue(conversation.Id, out var state))
            {
                state = new ConversationSnapshot(conversation, []);
            }

            _snapshot.Conversations[conversation.Id] = state with { Conversation = conversation };
            Save();
        }

        return Task.CompletedTask;
    }

    public Task<ConversationObject?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshot.Conversations.GetValueOrDefault(conversationId)?.Conversation);
        }
    }

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var removed = _snapshot.Conversations.Remove(conversationId);
            if (removed)
            {
                Save();
            }

            return Task.FromResult(removed);
        }
    }

    public Task AddConversationItemsAsync(string conversationId, IEnumerable<ConversationItem> items, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_snapshot.Conversations.TryGetValue(conversationId, out var state))
            {
                state = new ConversationSnapshot(ConversationObject.Create(conversationId), []);
                _snapshot.Conversations[conversationId] = state;
            }

            state.Items.AddRange(items.Select(item => item.EnsureIdentity()));
            Save();
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
        lock (_gate)
        {
            if (!_snapshot.Conversations.TryGetValue(conversationId, out var state))
            {
                return Task.FromResult<IReadOnlyList<ConversationItem>>([]);
            }

            var items = state.Items.ToList();
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
    }

    public Task<ConversationItem?> GetConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshot.Conversations.GetValueOrDefault(conversationId)?.Items
                .FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<bool> DeleteConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_snapshot.Conversations.TryGetValue(conversationId, out var state))
            {
                return Task.FromResult(false);
            }

            var removed = state.Items.RemoveAll(item => string.Equals(item.Id, itemId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }

            return Task.FromResult(removed);
        }
    }

    public Task SaveA2ATaskAsync(A2ATask task, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snapshot.A2ATasks[task.Id] = task;
            Save();
        }

        return Task.CompletedTask;
    }

    public Task<A2ATask?> GetA2ATaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshot.A2ATasks.GetValueOrDefault(taskId));
        }
    }

    public Task<IReadOnlyList<A2ATask>> ListA2ATasksAsync(string? agentName = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var tasks = _snapshot.A2ATasks.Values
                .Where(task => string.IsNullOrWhiteSpace(agentName) || string.Equals(task.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(task => task.CreatedAt)
                .ToArray();
            return Task.FromResult<IReadOnlyList<A2ATask>>(tasks);
        }
    }

    public Task SaveApiKeyAsync(AdminApiKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snapshot.ApiKeys[key.Id] = key;
            Save();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminApiKey>> ListApiKeysAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AdminApiKey>>([.. _snapshot.ApiKeys.Values.OrderBy(key => key.CreatedAt)]);
        }
    }

    public Task<bool> DeleteApiKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var removed = _snapshot.ApiKeys.Remove(keyId);
            if (removed)
            {
                Save();
            }

            return Task.FromResult(removed);
        }
    }

    public Task AddRouteDecisionAsync(RouteDecision decision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snapshot.RouteDecisions.Add(decision);
            if (_snapshot.RouteDecisions.Count > 500)
            {
                _snapshot.RouteDecisions = [.. _snapshot.RouteDecisions.TakeLast(500)];
            }

            Save();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RouteDecision>> ListRouteDecisionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RouteDecision>>([.. _snapshot.RouteDecisions.AsEnumerable().Reverse().Take(limit)]);
        }
    }

    public Task<IReadOnlyList<AssetDescriptor>> ListAssetsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AssetDescriptor>>(
        [
            new("asset_awesome_copilot", "awesome-copilot", "skill-pack", "Curated GitHub Copilot agents, skills, prompts, and instructions", true),
            new("asset_microsoft_learn_mcp", "microsoft-learn-mcp", "mcp", "Microsoft Learn MCP server profile for .NET and Azure help", true),
            new("asset_superpowers", "superpowers", "skill-pack", "Planning, TDD, debugging, and verification workflows", true)
        ]);

    private static ProtocolStoreSnapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ProtocolStoreSnapshot();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProtocolStoreSnapshot>(json, JsonOptions) ?? new ProtocolStoreSnapshot();
        }
        catch (JsonException)
        {
            return new ProtocolStoreSnapshot();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_snapshot, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    private sealed record ConversationSnapshot(ConversationObject Conversation, List<ConversationItem> Items);

    private sealed class ProtocolStoreSnapshot
    {
        public Dictionary<string, ResponseObject> Responses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ConversationSnapshot> Conversations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, A2ATask> A2ATasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, AdminApiKey> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<RouteDecision> RouteDecisions { get; set; } = [];
    }
}
