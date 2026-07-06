using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Tests for TiktokenTokenCounter with registry and graceful fallback.
/// Verifies token counting works for OpenCodeGo and other models without throwing exceptions.
/// </summary>
public sealed class TiktokenTokenCounterTests
{
    private readonly Mock<ILogger<TiktokenTokenCounter>> _mockLogger;
    private readonly Mock<ITokenizerRegistry> _mockRegistry;

    public TiktokenTokenCounterTests()
    {
        _mockLogger = new Mock<ILogger<TiktokenTokenCounter>>();
        _mockRegistry = new Mock<ITokenizerRegistry>();
    }

    /// <summary>
    /// With a null registry, counter should fall back to default (gpt-4o) encoding.
    /// </summary>
    [Fact]
    public void CountTokens_NoRegistry_UsesDefaultEncoding()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello, world!")
        };

        // Act
        var count = counter.CountTokens(messages);

        // Assert: Token count should be positive (4 overhead + ~3 tokens for "Hello, world!" + 3 reply prime)
        Assert.True(count > 0, "Expected positive token count");
        Assert.InRange(count, 5, 20); // Reasonable range for short message
    }

    /// <summary>
    /// With a registry that returns null for a model, counter should fall back and log warning.
    /// </summary>
    [Fact]
    public void CountTokens_RegistryReturnsNull_FallsBackAndLogsWarning()
    {
        // Arrange
        _mockRegistry
            .Setup(r => r.GetTokenizer("deepseek-v4-pro"))
            .Returns((Microsoft.ML.Tokenizers.Tokenizer?)null);
        _mockRegistry
            .Setup(r => r.GetAccuracyMetadata("deepseek-v4-pro"))
            .Returns("gpt-4o fallback (~80% accuracy)");

        var counter = new TiktokenTokenCounter(
            "gpt-4o",
            registry: _mockRegistry.Object,
            logger: _mockLogger.Object);

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Test message for DeepSeek")
        };

        // Act
        var count = counter.CountTokens(messages, "deepseek-v4-pro");

        // Assert
        Assert.True(count > 0, "Expected positive token count even with fallback");
        
        // Verify registry was queried
        _mockRegistry.Verify(r => r.GetTokenizer("deepseek-v4-pro"), Times.Once);
        
        // Verify warning was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Tokenizer not available")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Token count should be deterministic for same message.
    /// </summary>
    [Fact]
    public void CountTokens_DeterministicResults()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Deterministic test message"),
            new ChatMessage(ChatRole.Assistant, "Response")
        };

        // Act
        var count1 = counter.CountTokens(messages);
        var count2 = counter.CountTokens(messages);

        // Assert
        Assert.Equal(count1, count2);
    }

    /// <summary>
    /// Empty messages should still return a positive count (for overhead).
    /// </summary>
    [Fact]
    public void CountTokens_EmptyMessages_ReturnsNonNegative()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var messages = Array.Empty<ChatMessage>();

        // Act
        var count = counter.CountTokens(messages);

        // Assert: Should be >= 0
        Assert.True(count >= 0);
    }

    /// <summary>
    /// Longer messages should result in higher token counts.
    /// </summary>
    [Fact]
    public void CountTokens_LongerMessage_HigherCount()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var shortMessage = new[] { new ChatMessage(ChatRole.User, "Hi") };
        var longMessage = new[]
        {
            new ChatMessage(ChatRole.User,
                "This is a much longer message with many more words and characters. " +
                "It should definitely result in more tokens being counted compared to the short message. " +
                "Lorem ipsum dolor sit amet, consectetur adipiscing elit.")
        };

        // Act
        var shortCount = counter.CountTokens(shortMessage);
        var longCount = counter.CountTokens(longMessage);

        // Assert
        Assert.True(longCount > shortCount, "Longer message should have more tokens");
    }

    /// <summary>
    /// Multiple messages in the same request should accumulate token count.
    /// </summary>
    [Fact]
    public void CountTokens_MultipleMessages_Accumulates()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var oneMessage = new[]
        {
            new ChatMessage(ChatRole.User, "First message"),
        };
        var twoMessages = new[]
        {
            new ChatMessage(ChatRole.User, "First message"),
            new ChatMessage(ChatRole.Assistant, "Response to first"),
        };

        // Act
        var oneCount = counter.CountTokens(oneMessage);
        var twoCount = counter.CountTokens(twoMessages);

        // Assert
        Assert.True(twoCount > oneCount, "More messages should result in higher token count");
    }

    /// <summary>
    /// Should handle model IDs with whitespace and mixed case gracefully.
    /// </summary>
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("GPT-4O")]
    [InlineData("  gpt-4o  ")] // With spaces (normalized)
    public void CountTokens_VariesModelId_NeverThrows(string modelId)
    {
        // Arrange
        _mockRegistry
            .Setup(r => r.GetTokenizer(It.IsAny<string>()))
            .Returns((Microsoft.ML.Tokenizers.Tokenizer?)null);
        _mockRegistry
            .Setup(r => r.GetAccuracyMetadata(It.IsAny<string>()))
            .Returns("gpt-4o fallback");

        var counter = new TiktokenTokenCounter(
            "gpt-4o",
            registry: _mockRegistry.Object,
            logger: _mockLogger.Object);
        
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };

        // Act & Assert: No exception
        var count = counter.CountTokens(messages, modelId);
        Assert.True(count >= 0);
    }

    /// <summary>
    /// Null model ID should use default model.
    /// </summary>
    [Fact]
    public void CountTokens_NullModelId_UsesDefault()
    {
        // Arrange
        var counter = new TiktokenTokenCounter("gpt-4o", registry: null, logger: _mockLogger.Object);
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };

        // Act
        var count = counter.CountTokens(messages, modelId: null);

        // Assert: Should use default encoding and return valid count
        Assert.True(count > 0);
    }

    /// <summary>
    /// Exception from registry should be handled gracefully (fallback).
    /// </summary>
    [Fact]
    public void CountTokens_RegistryThrows_FallsBackGracefully()
    {
        // Arrange
        _mockRegistry
            .Setup(r => r.GetTokenizer(It.IsAny<string>()))
            .Throws(new InvalidOperationException("Registry error"));
        _mockRegistry
            .Setup(r => r.GetAccuracyMetadata(It.IsAny<string>()))
            .Returns("gpt-4o fallback");

        var counter = new TiktokenTokenCounter(
            "gpt-4o",
            registry: _mockRegistry.Object,
            logger: _mockLogger.Object);

        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };

        // Act & Assert: Exception from registry should be caught and fallback used
        var count = counter.CountTokens(messages, "test-model");
        Assert.True(count > 0, "Should fall back to default encoding on registry exception");
        
        // Verify error was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
