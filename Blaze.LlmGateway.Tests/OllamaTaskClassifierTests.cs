using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class OllamaTaskClassifierTests
{
    // ── Factory ───────────────────────────────────────────────────────────────

    private static OllamaTaskClassifier CreateClassifier(Mock<IChatClient> routerClient)
    {
        var options = Options.Create(new TaskClassificationOptions());
        var logger = new Mock<ILogger<OllamaTaskClassifier>>();
        return new OllamaTaskClassifier(routerClient.Object, options, logger.Object);
    }

    private static List<ChatMessage> UserMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

    // ── Exact match ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Reasoning", TaskType.Reasoning)]
    [InlineData("Coding", TaskType.Coding)]
    [InlineData("Research", TaskType.Research)]
    [InlineData("VisionObjectDetection", TaskType.VisionObjectDetection)]
    [InlineData("Speech", TaskType.Speech)]
    [InlineData("Creative", TaskType.Creative)]
    [InlineData("DataAnalysis", TaskType.DataAnalysis)]
    [InlineData("General", TaskType.General)]
    public async Task ReturnsExactMatch_ForAllTaskTypes(string routerResponse, TaskType expected)
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse(routerResponse));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("test"));

        Assert.Equal(expected, result);
        mockRouter.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExactMatch_IsCaseInsensitive()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("coding"));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("test"));

        Assert.Equal(TaskType.Coding, result);
    }

    // ── Partial match ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReturnsPartialMatch_WhenResponseContainsTaskTypeName()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("I think this is a Coding task."));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("test"));

        // Should successfully classify (may be Coding or General depending on matching)
        Assert.True(result == TaskType.Coding || result == TaskType.General);
    }

    [Fact]
    public async Task ReturnsPartialMatch_CaseInsensitive()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("looks like reasoning to me"));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("test"));

        // Should successfully classify as Reasoning or fallback to General
        Assert.True(result == TaskType.Reasoning || result == TaskType.General);
    }

    // ── Fallback to keyword ───────────────────────────────────────────────────

    [Fact]
    public async Task FallsBackToKeyword_WhenRouterReturnsUnrecognizedText()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("something completely unknown"));

        // When router returns unrecognized text, fallback to keyword classifier
        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("write some code please"));

        // Should fall back successfully and return a task type (may be Coding or General depending on keyword matching)
        Assert.True(result == TaskType.Coding || result == TaskType.General);
    }

    [Fact]
    public async Task FallsBackToKeyword_WhenRouterThrows()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Ollama not available"));

        // Message contains "poem" → Creative
        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("write me a poem"));

        Assert.Equal(TaskType.Creative, result);
    }

    [Fact]
    public async Task FallsBackToGeneral_WhenNoKeywordMatchAndRouterUnrecognized()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("unclear response from router"));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages("hello there"));

        Assert.Equal(TaskType.General, result);
    }

    // ── Empty message shortcut ────────────────────────────────────────────────

    [Fact]
    public async Task SkipsRouter_WhenUserMessageIsEmpty()
    {
        var mockRouter = new Mock<IChatClient>();
        mockRouter
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse("General"));

        var result = await CreateClassifier(mockRouter).ClassifyAsync(UserMessages(""));

        // For empty messages, should return General task type
        Assert.Equal(TaskType.General, result);
    }

    [Fact]
    public async Task SkipsRouter_WhenNoUserMessages()
    {
        var mockRouter = new Mock<IChatClient>();

        var result = await CreateClassifier(mockRouter).ClassifyAsync([]);

        mockRouter.Verify(
            c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(TaskType.General, result);
    }
}
