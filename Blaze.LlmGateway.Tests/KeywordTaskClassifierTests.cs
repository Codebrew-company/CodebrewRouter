using System.Collections.Generic;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class KeywordTaskClassifierTests
{
    private static KeywordTaskClassifier CreateClassifier() =>
        new(new Mock<ILogger<KeywordTaskClassifier>>().Object);

    private static List<ChatMessage> UserMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    // ── Vision ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Can you look at this image?")]
    [InlineData("Describe this photo in detail")]
    [InlineData("Detect objects in this picture")]
    [InlineData("What is in this screenshot?")]
    [InlineData("Vision analysis please")]
    public async Task ClassifiesVisionKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.VisionObjectDetection, result);
    }

    // ── Coding ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Write a C# function to sort a list")]
    [InlineData("Debug this code for me")]
    [InlineData("Implement a binary search algorithm")]
    [InlineData("Refactor this class to use SOLID principles")]
    [InlineData("Write a unit test for this method")]
    [InlineData("There is a bug fix needed in this snippet")]
    public async Task ClassifiesCodingKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.Coding, result);
    }

    // ── Reasoning ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Prove this theorem step by step")]
    [InlineData("What is the logic behind this formula?")]
    [InlineData("Solve this math equation")]
    [InlineData("Think through this problem carefully")]
    [InlineData("Deduce the answer from these premises")]
    [InlineData("Calculate the probability here")]
    public async Task ClassifiesReasoningKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.Reasoning, result);
    }

    // ── Data Analysis ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Analyze this dataset for patterns")]
    [InlineData("Create a chart of the sales trends")]
    [InlineData("Write a sql query to aggregate orders")]
    [InlineData("What are the statistics on this CSV?")]
    [InlineData("Show regression analysis of this data")]
    public async Task ClassifiesDataAnalysisKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.DataAnalysis, result);
    }

    // ── Research ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Do some research on quantum computing")]
    [InlineData("Give me a comprehensive overview of AI history")]
    [InlineData("Survey the literature on neural networks")]
    [InlineData("Compare React and Vue frameworks in detail")]
    [InlineData("Explain in detail how transformers work")]
    [InlineData("Deep dive into microservices")]
    public async Task ClassifiesResearchKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.Research, result);
    }

    // ── Creative ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Write a story about a dragon")]
    [InlineData("Write me a poem about autumn")]
    [InlineData("Create a creative essay on freedom")]
    [InlineData("Write a short story about space")]
    [InlineData("Draft a blog post about coffee")]
    public async Task ClassifiesCreativeKeywords(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.Creative, result);
    }

    // ── General / defaults ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Hello, how are you?")]
    [InlineData("What is the capital of France?")]
    [InlineData("Tell me something interesting")]
    public async Task DefaultsToGeneral_WhenNoKeywordsMatch(string message)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(message));
        Assert.Equal(TaskType.General, result);
    }

    [Fact]
    public async Task DefaultsToGeneral_WhenMessagesEmpty()
    {
        var result = await CreateClassifier().ClassifyAsync([]);
        Assert.Equal(TaskType.General, result);
    }

    [Fact]
    public async Task DefaultsToGeneral_WhenNoUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };
        var result = await CreateClassifier().ClassifyAsync(messages);
        Assert.Equal(TaskType.General, result);
    }

    [Fact]
    public async Task IsCaseInsensitive()
    {
        // "FUNCTION" should match the " function " keyword (case-insensitive)
        var result = await CreateClassifier().ClassifyAsync(UserMessages("WRITE A FUNCTION to solve this"));
        Assert.Equal(TaskType.Coding, result);
    }

    [Fact]
    public async Task UsesLastUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Write me a poem"),        // Creative
            new(ChatRole.Assistant, "Here is a poem…"),
            new(ChatRole.User, "Now debug this code")     // Coding — wins
        };
        var result = await CreateClassifier().ClassifyAsync(messages);
        Assert.Equal(TaskType.Coding, result);
    }

    [Fact]
    public async Task Vision_MatchesBeforeCoding()
    {
        // "image" is in the Vision rule which appears before Coding; also contains "code"
        var result = await CreateClassifier().ClassifyAsync(UserMessages("image code snippet"));
        Assert.Equal(TaskType.VisionObjectDetection, result);
    }

    [Theory]
    [InlineData("transcribe this recording for me")]
    [InlineData("summarize this voice note")]
    [InlineData("convert this text to speech")]
    public async Task Speech_MatchesAudioKeywords(string prompt)
    {
        var result = await CreateClassifier().ClassifyAsync(UserMessages(prompt));
        Assert.Equal(TaskType.Speech, result);
    }

    [Fact]
    public async Task Speech_MatchesBeforeVision()
    {
        // "transcribe" is in the Speech rule which appears before Vision; also contains "image"
        var result = await CreateClassifier().ClassifyAsync(UserMessages("transcribe the audio from this image slideshow"));
        Assert.Equal(TaskType.Speech, result);
    }
}
