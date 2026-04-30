using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Tests;

public class ContextSizingChatClientTests
{
    private static IOptions<ContextSizingOptions> EnabledOptions(int reservedOutput = 256) =>
        Options.Create(new ContextSizingOptions { Enabled = true, DefaultReservedOutputTokens = reservedOutput });

    private static IOptions<ContextSizingOptions> DisabledOptions() =>
        Options.Create(new ContextSizingOptions { Enabled = false });

    private static List<ChatMessage> OneUserMessage(string text = "hello") =>
        [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public async Task GetResponseAsync_TokensFit_ForwardsToInnerClient()
    {
        var messages = OneUserMessage();
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>())).Returns(100);

        var compactorMock = new Mock<IContextCompactor>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        compactorMock.Verify(c => c.CompactAsync(
            It.IsAny<IList<ChatMessage>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetResponseAsync_OverBudget_CompactsAndForwards()
    {
        var original = OneUserMessage("very long message");
        var compacted = OneUserMessage("summary");
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(It.IsAny<IList<ChatMessage>>(), 744 /* 1000-256 */, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(compacted, 900, 200, true, "summarize"));

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(original, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        compactorMock.Verify(c => c.CompactAsync(
            It.IsAny<IList<ChatMessage>>(), 744, "test-model", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetResponseAsync_CompactionInsufficient_ThrowsContextOverflowException()
    {
        var messages = OneUserMessage("extremely long");

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(It.IsAny<IList<ChatMessage>>(), 744, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 900, 800, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var act = () => sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        await act.Should().ThrowAsync<ContextOverflowException>()
            .Where(ex => ex.ModelId == "test-model"
                      && ex.RequiredTokens == 800
                      && ex.Budget == 744
                      && ex.AttemptedDestinations.Count == 0);

        innerMock.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetResponseAsync_Disabled_SkipsCountingAndForwards()
    {
        var messages = OneUserMessage();
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        var compactorMock = new Mock<IContextCompactor>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            DisabledOptions(),
            contextWindowTokens: 10,
            reservedOutputTokens: 5,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        tokenCounterMock.Verify(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task GetResponseAsync_ChatOptionsMaxOutputTokensOverridesDefault()
    {
        var messages = OneUserMessage();

        // window=1000, caller requests MaxOutputTokens=500 → budget=500
        // token count = 600 → over budget even though it would fit with default reserved=256 (budget=744)
        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>())).Returns(600);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(It.IsAny<IList<ChatMessage>>(), 500, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 600, 600, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var options = new ChatOptions { MaxOutputTokens = 500 };
        var act = () => sut.GetResponseAsync(messages, options, CancellationToken.None);

        await act.Should().ThrowAsync<ContextOverflowException>()
            .Where(ex => ex.Budget == 500);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_CompactionInsufficient_ThrowsBeforeFirstChunk()
    {
        var messages = OneUserMessage("huge");

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(It.IsAny<IList<ChatMessage>>(), 744, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 900, 800, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var enumerator = sut.GetStreamingResponseAsync(messages, new ChatOptions(), CancellationToken.None)
                            .GetAsyncEnumerator();

        var act = () => enumerator.MoveNextAsync().AsTask();

        await act.Should().ThrowAsync<ContextOverflowException>();
        innerMock.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
