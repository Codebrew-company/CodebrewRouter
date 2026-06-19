using Blaze.LlmGateway.Infrastructure.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Blaze.LlmGateway.Tests.RateLimiting;

public sealed class RateLimitBucketTests
{
    [Fact]
    public void TryConsumeRequest_WhenUnlimited_ReturnsTrue()
    {
        var bucket = new RateLimitBucket(0, 0);
        Assert.True(bucket.TryConsumeRequest());
        Assert.True(bucket.TryConsumeRequest());
        Assert.True(bucket.TryConsumeRequest());
    }

    [Fact]
    public void TryConsumeRequest_WhenWithinLimit_ReturnsTrue()
    {
        var bucket = new RateLimitBucket(10, 0);
        for (int i = 0; i < 10; i++)
            Assert.True(bucket.TryConsumeRequest());
    }

    [Fact]
    public void TryConsumeRequest_WhenExhausted_ReturnsFalse()
    {
        var bucket = new RateLimitBucket(2, 0);
        Assert.True(bucket.TryConsumeRequest());
        Assert.True(bucket.TryConsumeRequest());
        Assert.False(bucket.TryConsumeRequest());
    }

    [Fact]
    public void TryReserveTokens_WhenUnlimited_ReturnsRequested()
    {
        var bucket = new RateLimitBucket(0, 0);
        Assert.Equal(1000, bucket.TryReserveTokens(1000));
    }

    [Fact]
    public void TryReserveTokens_WhenWithinLimit_ReturnsAll()
    {
        var bucket = new RateLimitBucket(0, 500);
        Assert.Equal(300, bucket.TryReserveTokens(300));
    }

    [Fact]
    public void TryReserveTokens_WhenExhausted_ReturnsReduced()
    {
        var bucket = new RateLimitBucket(0, 100);
        var first = bucket.TryReserveTokens(80);
        Assert.Equal(80, first);
        var second = bucket.TryReserveTokens(50);
        Assert.True(second <= 20 && second > 0);
    }

    [Fact]
    public void AvailableRequests_ReflectsCurrentCapacity()
    {
        var bucket = new RateLimitBucket(5, 0);
        Assert.True(bucket.AvailableRequests > 4.0);
        bucket.TryConsumeRequest();
        Assert.True(bucket.AvailableRequests < 5.0);
    }

    [Fact]
    public void AvailableTokens_ReflectsCurrentCapacity()
    {
        var bucket = new RateLimitBucket(0, 200);
        Assert.True(bucket.AvailableTokens > 190.0);
        bucket.TryReserveTokens(50);
        Assert.True(bucket.AvailableTokens < 200.0);
    }
}

public sealed class RateLimitingChatClientTests
{
    private static Mock<IChatClient> CreateMockInner(string responseText = "Hello")
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 5,
                    TotalTokenCount = 15
                }
            });
        return mock;
    }

    [Fact]
    public async Task GetResponseAsync_WhenWithinLimit_ForwardsRequest()
    {
        var inner = CreateMockInner();
        var bucket = new RateLimitBucket(10, 100);
        var client = new RateLimitingChatClient(
            inner.Object, bucket, "test-dep", NullLogger<RateLimitingChatClient>.Instance);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]);

        Assert.NotNull(response);
        Assert.Equal("Hello", response.Text);
        inner.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetResponseAsync_WhenRequestExhausted_ThrowsRateLimitExceeded()
    {
        var inner = CreateMockInner();
        var bucket = new RateLimitBucket(2, 100);
        var client = new RateLimitingChatClient(
            inner.Object, bucket, "test-dep", NullLogger<RateLimitingChatClient>.Instance);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi2")]);

        var ex = await Assert.ThrowsAsync<RateLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi3")]));

        Assert.Equal("test-dep", ex.DeploymentName);
        Assert.Equal("request", ex.LimitType);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenWithinLimit_StreamsChunks()
    {
        var chunks = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, "Hello"),
            new ChatResponseUpdate(ChatRole.Assistant, " World"),
        };

        var mockInner = new Mock<IChatClient>();
        mockInner.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());

        var bucket = new RateLimitBucket(10, 1000);
        var client = new RateLimitingChatClient(
            mockInner.Object, bucket, "test-dep", NullLogger<RateLimitingChatClient>.Instance);

        var results = new List<ChatResponseUpdate>();
        await foreach (var chunk in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]))
        {
            results.Add(chunk);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal("Hello", results[0].Text);
        Assert.Equal(" World", results[1].Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenRequestExhausted_ThrowsRateLimitExceeded()
    {
        var mockInner = new Mock<IChatClient>();
        mockInner.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Array.Empty<ChatResponseUpdate>().ToAsyncEnumerable());

        var bucket = new RateLimitBucket(1, 1000);
        var client = new RateLimitingChatClient(
            mockInner.Object, bucket, "test-dep", NullLogger<RateLimitingChatClient>.Instance);

        // First call consumes the only request
        var results = new List<ChatResponseUpdate>();
        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]))
        {
            results.Add(_);
        }

        var ex = await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi2")]))
            {
            }
        });

        Assert.Equal("test-dep", ex.DeploymentName);
    }

    [Fact]
    public void Constructor_ThrowsOnNullBucket()
    {
        var inner = CreateMockInner();
        Assert.Throws<ArgumentNullException>(() =>
            new RateLimitingChatClient(
                inner.Object, null!, "test-dep", NullLogger<RateLimitingChatClient>.Instance));
    }
}
