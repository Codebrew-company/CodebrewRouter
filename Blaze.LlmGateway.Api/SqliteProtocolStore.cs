using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// SQLite-backed <see cref="IProtocolStore"/> (executes ADR-0004). WAL mode, busy_timeout 5s.
/// Complex protocol objects are stored as JSON documents; the usage ledger uses real
/// columns + indexes so dashboard queries stay fast.
/// On first start, migrates data from the legacy JSON store when present.
/// </summary>
public sealed class SqliteProtocolStore : IProtocolStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteProtocolStore> _logger;
    private readonly object _writeGate = new();

    public SqliteProtocolStore(string dbPath, string? legacyJsonPath, ILogger<SqliteProtocolStore> logger)
    {
        _logger = logger;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();

        if (!string.IsNullOrWhiteSpace(legacyJsonPath) && File.Exists(legacyJsonPath))
        {
            TryMigrateLegacyJson(legacyJsonPath);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS responses (id TEXT PRIMARY KEY COLLATE NOCASE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY COLLATE NOCASE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversation_items (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL COLLATE NOCASE,
                item_id TEXT NOT NULL COLLATE NOCASE,
                json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_conversation_items ON conversation_items(conversation_id);
            CREATE TABLE IF NOT EXISTS a2a_tasks (
                id TEXT PRIMARY KEY COLLATE NOCASE,
                agent_name TEXT NOT NULL COLLATE NOCASE,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS api_keys (
                id TEXT PRIMARY KEY COLLATE NOCASE,
                key_material TEXT NOT NULL,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS route_decisions (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS usage_history (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                api_key_id TEXT NULL,
                model TEXT NOT NULL,
                provider_model TEXT NULL,
                prompt_tokens INTEGER NOT NULL,
                completion_tokens INTEGER NOT NULL,
                total_tokens INTEGER NOT NULL,
                cost_usd TEXT NOT NULL,
                latency_ms INTEGER NOT NULL,
                status TEXT NOT NULL,
                streamed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_usage_created ON usage_history(created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_usage_key ON usage_history(api_key_id);
            CREATE INDEX IF NOT EXISTS ix_usage_model ON usage_history(model);
            """;
        command.ExecuteNonQuery();
    }

    // ── Legacy JSON migration ────────────────────────────────────────────────

    private sealed record LegacyConversation(ConversationObject Conversation, List<ConversationItem>? Items);

    private sealed class LegacySnapshot
    {
        public Dictionary<string, ResponseObject> Responses { get; set; } = [];
        public Dictionary<string, LegacyConversation> Conversations { get; set; } = [];
        public Dictionary<string, A2ATask> A2ATasks { get; set; } = [];
        public Dictionary<string, AdminApiKey> ApiKeys { get; set; } = [];
        public List<RouteDecision> RouteDecisions { get; set; } = [];
        public List<UsageRecord> Usage { get; set; } = [];
    }

    private void TryMigrateLegacyJson(string legacyJsonPath)
    {
        try
        {
            using (var connection = Open())
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT value FROM meta WHERE key = 'json_migrated'";
                if (check.ExecuteScalar() is not null)
                {
                    return;
                }
            }

            var snapshot = JsonSerializer.Deserialize<LegacySnapshot>(File.ReadAllText(legacyJsonPath), JsonOptions);
            if (snapshot is null)
            {
                return;
            }

            foreach (var response in snapshot.Responses.Values)
            {
                SaveResponseAsync(response).GetAwaiter().GetResult();
            }

            foreach (var (id, conversation) in snapshot.Conversations)
            {
                SaveConversationAsync(conversation.Conversation).GetAwaiter().GetResult();
                if (conversation.Items is { Count: > 0 })
                {
                    AddConversationItemsAsync(id, conversation.Items).GetAwaiter().GetResult();
                }
            }

            foreach (var task in snapshot.A2ATasks.Values)
            {
                SaveA2ATaskAsync(task).GetAwaiter().GetResult();
            }

            foreach (var key in snapshot.ApiKeys.Values)
            {
                SaveApiKeyAsync(key).GetAwaiter().GetResult();
            }

            foreach (var decision in snapshot.RouteDecisions)
            {
                AddRouteDecisionAsync(decision).GetAwaiter().GetResult();
            }

            foreach (var usage in snapshot.Usage)
            {
                AddUsageAsync(usage).GetAwaiter().GetResult();
            }

            using (var connection = Open())
            {
                using var mark = connection.CreateCommand();
                mark.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('json_migrated', @when)";
                mark.Parameters.AddWithValue("@when", DateTimeOffset.UtcNow.ToString("O"));
                mark.ExecuteNonQuery();
            }

            File.Move(legacyJsonPath, legacyJsonPath + ".migrated", overwrite: true);
            _logger.LogInformation(
                "Migrated legacy JSON protocol store to SQLite ({Keys} keys, {Responses} responses, {Conversations} conversations)",
                snapshot.ApiKeys.Count, snapshot.Responses.Count, snapshot.Conversations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy JSON protocol store from {Path}; continuing with empty SQLite store", legacyJsonPath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Upsert(string table, string id, string json)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT OR REPLACE INTO {table}(id, json) VALUES(@id, @json)";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@json", json);
            command.ExecuteNonQuery();
        }
    }

    private T? GetById<T>(string table, string id) where T : class
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT json FROM {table} WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<T>(json, JsonOptions)
            : null;
    }

    private bool DeleteById(string table, string id)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ── Responses ────────────────────────────────────────────────────────────

    public Task SaveResponseAsync(ResponseObject response, CancellationToken cancellationToken = default)
    {
        Upsert("responses", response.Id, JsonSerializer.Serialize(response, JsonOptions));
        return Task.CompletedTask;
    }

    public Task<ResponseObject?> GetResponseAsync(string responseId, CancellationToken cancellationToken = default)
        => Task.FromResult(GetById<ResponseObject>("responses", responseId));

    public Task<bool> DeleteResponseAsync(string responseId, CancellationToken cancellationToken = default)
        => Task.FromResult(DeleteById("responses", responseId));

    // ── Conversations ────────────────────────────────────────────────────────

    public Task SaveConversationAsync(ConversationObject conversation, CancellationToken cancellationToken = default)
    {
        Upsert("conversations", conversation.Id, JsonSerializer.Serialize(conversation, JsonOptions));
        return Task.CompletedTask;
    }

    public Task<ConversationObject?> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult(GetById<ConversationObject>("conversations", conversationId));

    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM conversation_items WHERE conversation_id = @id; DELETE FROM conversations WHERE id = @id";
            command.Parameters.AddWithValue("@id", conversationId);
            return Task.FromResult(command.ExecuteNonQuery() > 0);
        }
    }

    public Task AddConversationItemsAsync(string conversationId, IEnumerable<ConversationItem> items, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            using (var ensure = connection.CreateCommand())
            {
                ensure.Transaction = transaction;
                ensure.CommandText = "INSERT OR IGNORE INTO conversations(id, json) VALUES(@id, @json)";
                ensure.Parameters.AddWithValue("@id", conversationId);
                ensure.Parameters.AddWithValue("@json", JsonSerializer.Serialize(ConversationObject.Create(conversationId), JsonOptions));
                ensure.ExecuteNonQuery();
            }

            foreach (var item in items)
            {
                var withIdentity = item.EnsureIdentity();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO conversation_items(conversation_id, item_id, json) VALUES(@cid, @iid, @json)";
                insert.Parameters.AddWithValue("@cid", conversationId);
                insert.Parameters.AddWithValue("@iid", withIdentity.Id!);
                insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(withIdentity, JsonOptions));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
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
        var items = new List<ConversationItem>();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM conversation_items WHERE conversation_id = @cid ORDER BY seq";
            command.Parameters.AddWithValue("@cid", conversationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (JsonSerializer.Deserialize<ConversationItem>(reader.GetString(0), JsonOptions) is { } item)
                {
                    items.Add(item);
                }
            }
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
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM conversation_items WHERE conversation_id = @cid AND item_id = @iid LIMIT 1";
        command.Parameters.AddWithValue("@cid", conversationId);
        command.Parameters.AddWithValue("@iid", itemId);
        return Task.FromResult(command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<ConversationItem>(json, JsonOptions)
            : null);
    }

    public Task<bool> DeleteConversationItemAsync(string conversationId, string itemId, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversation_items WHERE conversation_id = @cid AND item_id = @iid";
            command.Parameters.AddWithValue("@cid", conversationId);
            command.Parameters.AddWithValue("@iid", itemId);
            return Task.FromResult(command.ExecuteNonQuery() > 0);
        }
    }

    // ── A2A tasks ────────────────────────────────────────────────────────────

    public Task SaveA2ATaskAsync(A2ATask task, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO a2a_tasks(id, agent_name, created_at, json) VALUES(@id, @agent, @created, @json)";
            command.Parameters.AddWithValue("@id", task.Id);
            command.Parameters.AddWithValue("@agent", task.AgentName);
            command.Parameters.AddWithValue("@created", task.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(task, JsonOptions));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<A2ATask?> GetA2ATaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM a2a_tasks WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        return Task.FromResult(command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<A2ATask>(json, JsonOptions)
            : null);
    }

    public Task<IReadOnlyList<A2ATask>> ListA2ATasksAsync(string? agentName = null, CancellationToken cancellationToken = default)
    {
        var tasks = new List<A2ATask>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(agentName))
        {
            command.CommandText = "SELECT json FROM a2a_tasks ORDER BY created_at DESC";
        }
        else
        {
            command.CommandText = "SELECT json FROM a2a_tasks WHERE agent_name = @agent ORDER BY created_at DESC";
            command.Parameters.AddWithValue("@agent", agentName);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<A2ATask>(reader.GetString(0), JsonOptions) is { } task)
            {
                tasks.Add(task);
            }
        }

        return Task.FromResult<IReadOnlyList<A2ATask>>(tasks);
    }

    // ── API keys ─────────────────────────────────────────────────────────────

    public Task SaveApiKeyAsync(AdminApiKey key, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO api_keys(id, key_material, created_at, json) VALUES(@id, @key, @created, @json)";
            command.Parameters.AddWithValue("@id", key.Id);
            command.Parameters.AddWithValue("@key", key.Key);
            command.Parameters.AddWithValue("@created", key.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(key, JsonOptions));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminApiKey>> ListApiKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<AdminApiKey>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM api_keys ORDER BY created_at";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<AdminApiKey>(reader.GetString(0), JsonOptions) is { } key)
            {
                keys.Add(key);
            }
        }

        return Task.FromResult<IReadOnlyList<AdminApiKey>>(keys);
    }

    public Task<bool> DeleteApiKeyAsync(string keyId, CancellationToken cancellationToken = default)
        => Task.FromResult(DeleteById("api_keys", keyId));

    // ── Route decisions ──────────────────────────────────────────────────────

    public Task AddRouteDecisionAsync(RouteDecision decision, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO route_decisions(created_at, json) VALUES(@created, @json);
                DELETE FROM route_decisions WHERE seq <= (SELECT MAX(seq) FROM route_decisions) - 500;
                """;
            command.Parameters.AddWithValue("@created", decision.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(decision, JsonOptions));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RouteDecision>> ListRouteDecisionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var decisions = new List<RouteDecision>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM route_decisions ORDER BY seq DESC LIMIT @limit";
        command.Parameters.AddWithValue("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<RouteDecision>(reader.GetString(0), JsonOptions) is { } decision)
            {
                decisions.Add(decision);
            }
        }

        return Task.FromResult<IReadOnlyList<RouteDecision>>(decisions);
    }

    // ── Assets (static, mirrors the JSON store) ──────────────────────────────

    public Task<IReadOnlyList<AssetDescriptor>> ListAssetsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AssetDescriptor>>(
        [
            new("asset_awesome_copilot", "awesome-copilot", "skill-pack", "Curated GitHub Copilot agents, skills, prompts, and instructions", true),
            new("asset_microsoft_learn_mcp", "microsoft-learn-mcp", "mcp", "Microsoft Learn MCP server profile for .NET and Azure help", true),
            new("asset_superpowers", "superpowers", "skill-pack", "Planning, TDD, debugging, and verification workflows", true)
        ]);

    // ── Usage ledger ─────────────────────────────────────────────────────────

    public Task AddUsageAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO usage_history
                    (id, created_at, api_key_id, model, provider_model, prompt_tokens, completion_tokens,
                     total_tokens, cost_usd, latency_ms, status, streamed)
                VALUES (@id, @created, @key, @model, @pmodel, @pt, @ct, @tt, @cost, @lat, @status, @streamed)
                """;
            command.Parameters.AddWithValue("@id", record.Id);
            command.Parameters.AddWithValue("@created", record.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@key", (object?)record.ApiKeyId ?? DBNull.Value);
            command.Parameters.AddWithValue("@model", record.Model);
            command.Parameters.AddWithValue("@pmodel", (object?)record.ProviderModel ?? DBNull.Value);
            command.Parameters.AddWithValue("@pt", record.PromptTokens);
            command.Parameters.AddWithValue("@ct", record.CompletionTokens);
            command.Parameters.AddWithValue("@tt", record.TotalTokens);
            command.Parameters.AddWithValue("@cost", record.CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@lat", record.LatencyMs);
            command.Parameters.AddWithValue("@status", record.Status);
            command.Parameters.AddWithValue("@streamed", record.Streamed ? 1 : 0);
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageRecord>> ListUsageAsync(
        int limit = 100,
        int offset = 0,
        string? apiKeyId = null,
        CancellationToken cancellationToken = default)
    {
        var records = new List<UsageRecord>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT id, created_at, api_key_id, model, provider_model, prompt_tokens, completion_tokens,
                    total_tokens, cost_usd, latency_ms, status, streamed
             FROM usage_history
             {(apiKeyId is null ? "" : "WHERE api_key_id = @key")}
             ORDER BY created_at DESC LIMIT @limit OFFSET @offset
             """;
        if (apiKeyId is not null)
        {
            command.Parameters.AddWithValue("@key", apiKeyId);
        }

        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadUsage(reader));
        }

        return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
    }

    public Task<SpendSummary> GetUsageSummaryAsync(string? apiKeyId = null, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT COUNT(*), COALESCE(SUM(total_tokens), 0), COALESCE(SUM(CAST(cost_usd AS REAL)), 0),
                    COALESCE(SUM(prompt_tokens), 0), COALESCE(SUM(completion_tokens), 0)
             FROM usage_history
             {(apiKeyId is null ? "" : "WHERE api_key_id = @key")}
             """;
        if (apiKeyId is not null)
        {
            command.Parameters.AddWithValue("@key", apiKeyId);
        }

        using var reader = command.ExecuteReader();
        reader.Read();
        return Task.FromResult(new SpendSummary(
            "spend.summary",
            apiKeyId,
            reader.GetInt32(0),
            reader.GetInt64(1),
            (decimal)reader.GetDouble(2),
            reader.GetInt64(3),
            reader.GetInt64(4)));
    }

    public Task<IReadOnlyList<UsageDailyBucket>> GetUsageDailyAsync(int days = 30, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1)).ToString("O");
        var buckets = new List<UsageDailyBucket>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT substr(created_at, 1, 10) AS day, COUNT(*), SUM(prompt_tokens), SUM(completion_tokens),
                   SUM(total_tokens), SUM(CAST(cost_usd AS REAL))
            FROM usage_history
            WHERE created_at >= @cutoff
            GROUP BY day
            ORDER BY day
            """;
        command.Parameters.AddWithValue("@cutoff", cutoff);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new UsageDailyBucket(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                (decimal)reader.GetDouble(5)));
        }

        return Task.FromResult<IReadOnlyList<UsageDailyBucket>>(buckets);
    }

    private static UsageRecord ReadUsage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            decimal.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt32(11) != 0);

    public void Dispose() => SqliteConnection.ClearAllPools();
}
