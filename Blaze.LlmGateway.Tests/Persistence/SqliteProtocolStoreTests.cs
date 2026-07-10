using Blaze.LlmGateway.Api;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blaze.LlmGateway.Tests.Persistence;

/// <summary>P1.2: SQLite protocol store round-trips + JSON migration.</summary>
public sealed class SqliteProtocolStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cbr-sqlite-tests-{Guid.NewGuid():N}");

    private SqliteProtocolStore CreateStore(string? legacyJsonPath = null)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteProtocolStore(
            Path.Combine(_directory, "store.sqlite3"),
            legacyJsonPath,
            NullLogger<SqliteProtocolStore>.Instance);
    }

    [Fact]
    public async Task Responses_RoundTrip()
    {
        using var store = CreateStore();
        var response = new ResponseObject("resp_1", "response", 123, "completed", "codebrewRouter", [], "hello");

        await store.SaveResponseAsync(response);

        (await store.GetResponseAsync("resp_1"))!.OutputText.Should().Be("hello");
        (await store.DeleteResponseAsync("resp_1")).Should().BeTrue();
        (await store.GetResponseAsync("resp_1")).Should().BeNull();
    }

    [Fact]
    public async Task Conversations_WithItems_RoundTrip()
    {
        using var store = CreateStore();
        var conversation = ConversationObject.Create("conv_1");
        await store.SaveConversationAsync(conversation);
        await store.AddConversationItemsAsync("conv_1",
        [
            new ConversationItem("message", "user", "first"),
            new ConversationItem("message", "assistant", "second")
        ]);

        var items = await store.ListConversationItemsAsync("conv_1");
        items.Should().HaveCount(2);
        items[0].Content.Should().Be("first");

        var descending = await store.ListConversationItemsAsync("conv_1", order: "desc");
        descending[0].Content.Should().Be("second");

        (await store.DeleteConversationAsync("conv_1")).Should().BeTrue();
        (await store.ListConversationItemsAsync("conv_1")).Should().BeEmpty();
    }

    [Fact]
    public async Task ApiKeys_RoundTrip()
    {
        using var store = CreateStore();
        var key = new AdminApiKey("key_1", "tenant", "test", "cbr_secret", ["codebrewRouter"], false, ["chat"], DateTimeOffset.UtcNow);

        await store.SaveApiKeyAsync(key);

        (await store.ListApiKeysAsync()).Should().ContainSingle().Which.Key.Should().Be("cbr_secret");
        (await store.DeleteApiKeyAsync("key_1")).Should().BeTrue();
        (await store.ListApiKeysAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RouteDecisions_CappedAndOrdered()
    {
        using var store = CreateStore();
        await store.AddRouteDecisionAsync(new RouteDecision("rd_1", DateTimeOffset.UtcNow, "auto", "OllamaRouter", "keyword"));
        await store.AddRouteDecisionAsync(new RouteDecision("rd_2", DateTimeOffset.UtcNow, "auto", "LocalGemma", "fallback"));

        var decisions = await store.ListRouteDecisionsAsync(10);
        decisions.Should().HaveCount(2);
        decisions[0].Id.Should().Be("rd_2");
    }

    [Fact]
    public async Task Usage_SummaryAndDailyBuckets()
    {
        using var store = CreateStore();
        await store.AddUsageAsync(new UsageRecord("u1", DateTimeOffset.UtcNow, "key_a", "auto", "m1", 100, 50, 150, 0.003m, 42, "ok", true));
        await store.AddUsageAsync(new UsageRecord("u2", DateTimeOffset.UtcNow, "key_a", "auto", "m1", 200, 100, 300, 0.006m, 55, "ok", false));
        await store.AddUsageAsync(new UsageRecord("u3", DateTimeOffset.UtcNow, "key_b", "fusion", "m2", 10, 5, 15, 0m, 12, "error", true));

        var all = await store.GetUsageSummaryAsync();
        all.TotalRequests.Should().Be(3);
        all.TotalTokens.Should().Be(465);
        all.PromptTokens.Should().Be(310);
        all.EstimatedCostUsd.Should().BeApproximately(0.009m, 0.0001m);

        var keyA = await store.GetUsageSummaryAsync("key_a");
        keyA.TotalRequests.Should().Be(2);
        keyA.TotalTokens.Should().Be(450);

        var history = await store.ListUsageAsync(limit: 2);
        history.Should().HaveCount(2);

        var daily = await store.GetUsageDailyAsync(7);
        daily.Should().ContainSingle().Which.Requests.Should().Be(3);
    }

    [Fact]
    public async Task LegacyJson_MigratesOnce_AndRenamesFile()
    {
        Directory.CreateDirectory(_directory);
        var jsonPath = Path.Combine(_directory, "protocol-store.json");
        var legacyKey = new AdminApiKey("key_legacy", "tenant", "legacy", "cbr_legacy", ["codebrewRouter"], false, ["chat"], DateTimeOffset.UtcNow);

        // Produce a real legacy file via the JSON store itself.
        var jsonStore = new JsonProtocolStore(jsonPath);
        await jsonStore.SaveApiKeyAsync(legacyKey);
        await jsonStore.SaveResponseAsync(new ResponseObject("resp_legacy", "response", 1, "completed", "auto", [], "old"));

        using var store = CreateStore(jsonPath);

        (await store.ListApiKeysAsync()).Should().ContainSingle().Which.Id.Should().Be("key_legacy");
        (await store.GetResponseAsync("resp_legacy")).Should().NotBeNull();
        File.Exists(jsonPath).Should().BeFalse();
        File.Exists(jsonPath + ".migrated").Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
