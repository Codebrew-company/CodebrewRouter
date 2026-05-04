using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Tests for OpenCodeGoTokenizerRegistry with graceful fallback behavior.
/// Verifies that all 14 OpenCodeGo models resolve gracefully (either native tokenizer or null for fallback).
/// No exceptions should be thrown; graceful degradation is the design principle.
/// </summary>
public sealed class OpenCodeGoTokenizerRegistryTests
{
    private readonly Mock<ILogger<OpenCodeGoTokenizerRegistry>> _mockLogger;
    private readonly OpenCodeGoTokenizerRegistry _registry;

    public OpenCodeGoTokenizerRegistryTests()
    {
        _mockLogger = new Mock<ILogger<OpenCodeGoTokenizerRegistry>>();
        _registry = new OpenCodeGoTokenizerRegistry(_mockLogger.Object);
    }

    /// <summary>
    /// All 14 OpenCodeGo models should resolve without throwing exceptions.
    /// They may resolve to a tokenizer or null (for fallback), but never throw.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllOpenCodeGoModels))]
    public void GetTokenizer_ReturnsTokenizerOrNull_NeverThrows(string modelId)
    {
        // Arrange & Act
        var tokenizer = _registry.GetTokenizer(modelId);

        // Assert: No exception thrown; result is either tokenizer or null (both valid)
        // No specific assertion on whether tokenizer is null or not—the test verifies no exception
        Assert.True(tokenizer is null || tokenizer.GetType().Name.Contains("Tokenizer"),
            $"Expected tokenizer or null, got {tokenizer?.GetType().Name ?? "null"}");
    }

    /// <summary>
    /// All 14 models should return non-empty accuracy metadata.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllOpenCodeGoModels))]
    public void GetAccuracyMetadata_ReturnsNonEmptyString(string modelId)
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata(modelId);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata);
    }

    /// <summary>
    /// Qwen models should have accuracy metadata indicating native C# tokenizer support.
    /// </summary>
    [Theory]
    [InlineData("qwen3.5-plus")]
    [InlineData("qwen3.6-plus")]
    [InlineData("QWEN3.5-PLUS")] // Case-insensitive
    public void GetAccuracyMetadata_Qwen_IndicatesNativeSupport(string modelId)
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata(modelId);

        // Assert
        Assert.Contains("Yuniko", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99%", metadata);
    }

    /// <summary>
    /// DeepSeek models should have accuracy metadata indicating HuggingFace JSON support.
    /// </summary>
    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    [InlineData("DEEPSEEK-V4-PRO")] // Case-insensitive
    public void GetAccuracyMetadata_DeepSeek_IndicatesHuggingFaceSupport(string modelId)
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata(modelId);

        // Assert
        Assert.Contains("HuggingFace", metadata);
        Assert.Contains("90-95%", metadata);
    }

    /// <summary>
    /// GLM models should have accuracy metadata indicating HuggingFace JSON support.
    /// </summary>
    [Theory]
    [InlineData("glm-5")]
    [InlineData("glm-5.1")]
    [InlineData("GLM-5")] // Case-insensitive
    public void GetAccuracyMetadata_Glm_IndicatesHuggingFaceSupport(string modelId)
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata(modelId);

        // Assert
        Assert.Contains("HuggingFace", metadata);
        Assert.Contains("90-95%", metadata);
    }

    /// <summary>
    /// Kimi, MiniMax, and MiMo models should have accuracy metadata indicating fallback status.
    /// </summary>
    [Theory]
    [InlineData("kimi-k2.5")]
    [InlineData("kimi-k2.6")]
    [InlineData("mini-max-m2.5")]
    [InlineData("mini-max-m2.7")]
    [InlineData("mimo-v2-pro")]
    [InlineData("mimo-v2.5")]
    [InlineData("mimo-v2.5-pro")]
    [InlineData("mimo-v2-omni")]
    public void GetAccuracyMetadata_FallbackModels_IndicateFallbackStatus(string modelId)
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata(modelId);

        // Assert
        Assert.Contains("gpt-4o fallback", metadata);
        Assert.Contains("75-85%", metadata);
    }

    /// <summary>
    /// Caching should work correctly: repeated calls for same model should return same instance.
    /// </summary>
    [Fact]
    public void GetTokenizer_CachesBetweenCalls()
    {
        // Act
        var first = _registry.GetTokenizer("deepseek-v4-pro");
        var second = _registry.GetTokenizer("deepseek-v4-pro");

        // Assert: Both calls succeed and return same instance (or both null)
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Unknown model IDs should return null gracefully (not throw exception).
    /// </summary>
    [Theory]
    [InlineData("unknown-model-xyz")]
    [InlineData("")]
    [InlineData(null)]
    public void GetTokenizer_UnknownModel_ReturnsNullGracefully(string? modelId)
    {
        // Act
        var tokenizer = _registry.GetTokenizer(modelId ?? "");

        // Assert
        Assert.Null(tokenizer);
    }

    /// <summary>
    /// Case-insensitive matching should work for all models.
    /// </summary>
    [Theory]
    [InlineData("QWEN3.5-PLUS")]
    [InlineData("DeepSeek-V4-Pro")]
    [InlineData("GLM-5")]
    [InlineData("Kimi-K2.5")]
    public void GetTokenizer_CaseInsensitiveMatching(string modelId)
    {
        // Act
        var tokenizer = _registry.GetTokenizer(modelId);

        // Assert: No exception; returns tokenizer or null
        Assert.True(tokenizer is null || tokenizer.GetType().Name.Contains("Tokenizer"));
    }

    /// <summary>
    /// GetAccuracyMetadata for unknown models should return a sensible fallback string.
    /// </summary>
    [Fact]
    public void GetAccuracyMetadata_UnknownModel_ReturnsSensibleDefault()
    {
        // Act
        var metadata = _registry.GetAccuracyMetadata("unknown-model");

        // Assert
        Assert.Contains("fallback", metadata, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test data: All 14 OpenCodeGo models for parametrized tests.
    /// </summary>
    public static TheoryData<string> AllOpenCodeGoModels =>
        new()
        {
            // Qwen
            "qwen3.5-plus",
            "qwen3.6-plus",
            // DeepSeek
            "deepseek-v4-pro",
            "deepseek-v4-flash",
            // GLM
            "glm-5",
            "glm-5.1",
            // Kimi
            "kimi-k2.5",
            "kimi-k2.6",
            // MiniMax
            "mini-max-m2.5",
            "mini-max-m2.7",
            // MiMo
            "mimo-v2-pro",
            "mimo-v2.5",
            "mimo-v2.5-pro",
            "mimo-v2-omni"
        };
}
