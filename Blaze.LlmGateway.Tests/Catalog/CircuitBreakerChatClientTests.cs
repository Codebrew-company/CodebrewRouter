using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Microsoft.Extensions.AI;
using Moq;

namespace Blaze.LlmGateway.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CircuitBreakerChatClient"/>.
/// Verifies the circuit-breaker middleware correctly checks health,
/// forwards calls, reports outcomes, and short-circuits on unhealthy deployments.
/// </summary>
public sealed class CircuitBreakerChatClientTests
{
    private const string TestDeployment = "test-deployment";

    private static Mock<IProviderCatalog> CreateHealthyCatalog()
    {
        var mock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        mock.Setup(c => c.IsHealthy(TestDeployment)).Returns(true);
        mock.Setup(c => c.ReportHealth(TestDeployment, It.IsAny<bool>()));
        return mock;
    }

    private static Mock<IProviderCatalog> CreateUnhealthyCatalog()
    {
        var mock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        mock.Setup(c => c.IsHealthy(TestDeployment)).Returns(false);
        return mock;
    }

    private static List<ChatMessage> OneUserMessage(string text = "hello") =>
        [new ChatMessage(ChatRole.User, text)];

    private static ChatResponse OkResponse(string text = "ok") =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

    // ── 1. CompleteAsync: healthy → forwards call ────────────────────────

    [Fact]
    public async Task CompleteAsync_WhenHealthy_ForwardsCall()
    {
        // Arrange
        var catalogMock = CreateHealthyCatalog();
        var messages = OneUserMessage();
        var expectedResponse = OkResponse();

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act
        var result = await sut.GetResponseAsync(messages);

        // Assert
        Assert.Same(expectedResponse, result);
        innerMock.VerifyAll();
    }

    // ── 2. CompleteAsync: unhealthy → throws ─────────────────────────────

    [Fact]
    public async Task CompleteAsync_WhenUnhealthy_Throws()
    {
        // Arrange
        var catalogMock = CreateUnhealthyCatalog();
        var messages = OneUserMessage();

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetResponseAsync(messages));

        Assert.Contains(TestDeployment, ex.Message);
        Assert.Contains("circuit breaker", ex.Message, StringComparison.OrdinalIgnoreCase);
        innerMock.VerifyNoOtherCalls();
    }

    // ── 3. CompleteAsync: success → reports healthy ──────────────────────

    [Fact]
    public async Task CompleteAsync_OnSuccess_ReportsHealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.IsHealthy(TestDeployment)).Returns(true);
        catalogMock.Setup(c => c.ReportHealth(TestDeployment, true));

        var messages = OneUserMessage();
        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResponse());

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act
        await sut.GetResponseAsync(messages);

        // Assert
        catalogMock.Verify(c => c.ReportHealth(TestDeployment, true), Times.Once);
    }

    // ── 4. CompleteAsync: exception → reports unhealthy ──────────────────

    [Fact]
    public async Task CompleteAsync_OnException_ReportsUnhealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.IsHealthy(TestDeployment)).Returns(true);
        catalogMock.Setup(c => c.ReportHealth(TestDeployment, false));

        var messages = OneUserMessage();
        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Provider unavailable"));

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GetResponseAsync(messages));

        catalogMock.Verify(c => c.ReportHealth(TestDeployment, false), Times.Once);
    }

    // ── 5. CompleteStreaming: healthy → streams chunks ───────────────────

    [Fact]
    public async Task CompleteStreamingAsync_WhenHealthy_StreamsChunks()
    {
        // Arrange
        var catalogMock = CreateHealthyCatalog();
        var messages = OneUserMessage();
        var expectedChunks = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, "Hello"),
            new ChatResponseUpdate(ChatRole.Assistant, " World"),
        };

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        innerMock
            .Setup(c => c.GetStreamingResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(expectedChunks));

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act
        var receivedChunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in sut.GetStreamingResponseAsync(messages))
        {
            receivedChunks.Add(chunk);
        }

        // Assert
        Assert.Equal(2, receivedChunks.Count);
    }

    // ── 6. CompleteStreaming: unhealthy → throws ─────────────────────────

    [Fact]
    public async Task CompleteStreamingAsync_WhenUnhealthy_Throws()
    {
        // Arrange
        var catalogMock = CreateUnhealthyCatalog();
        var messages = OneUserMessage();

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(messages))
            {
                // Never reached
            }
        });

        Assert.Contains(TestDeployment, ex.Message);
        Assert.Contains("circuit breaker", ex.Message, StringComparison.OrdinalIgnoreCase);
        innerMock.VerifyNoOtherCalls();
    }

    // ── 7. CompleteStreaming: mid-stream error → reports unhealthy ───────

    [Fact]
    public async Task CompleteStreamingAsync_MidStreamError_ReportsUnhealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.IsHealthy(TestDeployment)).Returns(true);
        catalogMock.Setup(c => c.ReportHealth(TestDeployment, false));

        var messages = OneUserMessage();
        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        innerMock
            .Setup(c => c.GetStreamingResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(StreamWithMidError());

        var sut = new CircuitBreakerChatClient(innerMock.Object, catalogMock.Object, TestDeployment);

        // Act & Assert
        var receivedChunks = new List<ChatResponseUpdate>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in sut.GetStreamingResponseAsync(messages))
            {
                receivedChunks.Add(chunk);
            }
        });

        // Should have received some chunks before the error
        Assert.NotEmpty(receivedChunks);
        catalogMock.Verify(c => c.ReportHealth(TestDeployment, false), Times.Once);
    }

    // ── 8. Constructor: null catalog throws ──────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullCatalog()
    {
        var innerMock = new Mock<IChatClient>();

        var ex = Assert.Throws<ArgumentNullException>(
            () => new CircuitBreakerChatClient(innerMock.Object, null!, TestDeployment));

        Assert.Contains("catalog", ex.ParamName, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        ChatResponseUpdate[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithMidError()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Before");
        yield return new ChatResponseUpdate(ChatRole.Assistant, " crash");
        throw new InvalidOperationException("Mid-stream failure");
    }
}
