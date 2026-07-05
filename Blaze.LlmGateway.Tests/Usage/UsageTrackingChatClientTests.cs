using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Api.UsageTracking;
using Blaze.LlmGateway.Core.Catalog;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.UsageLedger;

/// <summary>P1.1: usage ledger decorator records tokens, cost, latency, api key.</summary>
public sealed class UsageTrackingChatClientTests
{
    private static readonly ChatMessage UserMessage = new(ChatRole.User, "hello world, this is a prompt");

    private static Mock<IChatClient> CreateInner(long inputTokens = 100, long outputTokens = 40)
    {
        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi")])
            {
                ModelId = "provider-model",
                Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
            });
        inner
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Stream());
        return inner;

        static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk one ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk two");
            await Task.CompletedTask;
        }
    }

    private static IHttpContextAccessor AccessorWithKey(AdminApiKey? key)
    {
        var context = new DefaultHttpContext();
        if (key is not null)
        {
            context.Items[Blaze.LlmGateway.Api.Auth.ApiKeyAuthentication.ApiKeyItem] = key;
        }

        return new HttpContextAccessor { HttpContext = context };
    }

    [Fact]
    public async Task NonStreaming_RecordsUsageRow_WithApiKeyAndTokens()
    {
        var store = new InMemoryProtocolStore();
        var key = new AdminApiKey("key_u", "t", "n", "cbr_x", [], false, [], DateTimeOffset.UtcNow);
        var sut = new UsageTrackingChatClient(
            CreateInner().Object, store, AccessorWithKey(key), catalog: null,
            NullLogger<UsageTrackingChatClient>.Instance);

        await sut.GetResponseAsync([UserMessage], new ChatOptions { ModelId = "auto" });

        var rows = await store.ListUsageAsync();
        var row = rows.Should().ContainSingle().Subject;
        row.ApiKeyId.Should().Be("key_u");
        row.Model.Should().Be("auto");
        row.ProviderModel.Should().Be("provider-model");
        row.PromptTokens.Should().Be(100);
        row.CompletionTokens.Should().Be(40);
        row.TotalTokens.Should().Be(140);
        row.Status.Should().Be("ok");
        row.Streamed.Should().BeFalse();
    }

    [Fact]
    public async Task Streaming_RecordsRow_WithEstimatedTokens()
    {
        var store = new InMemoryProtocolStore();
        var sut = new UsageTrackingChatClient(
            CreateInner().Object, store, AccessorWithKey(null), catalog: null,
            NullLogger<UsageTrackingChatClient>.Instance);

        await foreach (var _ in sut.GetStreamingResponseAsync([UserMessage], new ChatOptions { ModelId = "fusion" }))
        {
        }

        var row = (await store.ListUsageAsync()).Should().ContainSingle().Subject;
        row.Model.Should().Be("fusion");
        row.Streamed.Should().BeTrue();
        row.TotalTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CostMath_UsesCatalogCostPerToken()
    {
        var store = new InMemoryProtocolStore();
        var catalog = new Mock<IProviderCatalog>();
        catalog.Setup(c => c.GetAllDeployments()).Returns(
        [
            new ProviderDeployment
            {
                Name = "dep",
                ModelName = "auto",
                Provider = "Test",
                CostPerToken = 0.00001
            }
        ]);

        var sut = new UsageTrackingChatClient(
            CreateInner(inputTokens: 1000, outputTokens: 500).Object, store, AccessorWithKey(null),
            catalog.Object, NullLogger<UsageTrackingChatClient>.Instance);

        await sut.GetResponseAsync([UserMessage], new ChatOptions { ModelId = "auto" });

        var row = (await store.ListUsageAsync()).Should().ContainSingle().Subject;
        row.CostUsd.Should().BeApproximately(0.015m, 0.000001m); // 1500 tokens * 0.00001
    }

    [Fact]
    public async Task ThreeRequests_SummaryTotalsMatch()
    {
        var store = new InMemoryProtocolStore();
        var sut = new UsageTrackingChatClient(
            CreateInner().Object, store, AccessorWithKey(null), catalog: null,
            NullLogger<UsageTrackingChatClient>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await sut.GetResponseAsync([UserMessage], new ChatOptions { ModelId = "auto" });
        }

        (await store.ListUsageAsync()).Should().HaveCount(3);
        var summary = await store.GetUsageSummaryAsync();
        summary.TotalRequests.Should().Be(3);
        summary.TotalTokens.Should().Be(3 * 140);
    }
}
