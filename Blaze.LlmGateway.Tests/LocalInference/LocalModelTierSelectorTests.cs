using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using FluentAssertions;

namespace Blaze.LlmGateway.Tests.LocalInference;

public sealed class LocalModelTierSelectorTests
{
    private const string Path2b = "https://example/gemma-4-e2b.lmk";
    private const string Path4b = "https://example/gemma-4-e4b.lmk";
    private const string Path12b = "https://example/gemma-4-12b.lmk";

    private static LocalInferenceOptions ThreeTierOptions() => new()
    {
        ModelPath = "https://example/original.lmk",
        ModelTiers =
        [
            new LocalModelTierOptions { Name = "gemma-4-2b", MinTotalMemoryGb = 0, ModelPath = Path2b },
            new LocalModelTierOptions { Name = "gemma-4-4b", MinTotalMemoryGb = 12, ModelPath = Path4b },
            new LocalModelTierOptions { Name = "gemma-4-12b", MinTotalMemoryGb = 24, ModelPath = Path12b }
        ]
    };

    [Fact]
    public void Apply_NoTiersConfigured_ReturnsNullAndLeavesModelPathUntouched()
    {
        var options = new LocalInferenceOptions { ModelPath = "https://example/original.lmk" };

        var selection = LocalModelTierSelector.Apply(options, 16L << 30);

        selection.Should().BeNull();
        options.ModelPath.Should().Be("https://example/original.lmk");
    }

    [Theory]
    [InlineData(8L, "gemma-4-2b", Path2b)]
    [InlineData(16L, "gemma-4-4b", Path4b)]
    [InlineData(32L, "gemma-4-12b", Path12b)]
    [InlineData(12L, "gemma-4-4b", Path4b)] // exactly at threshold → eligible
    public void Apply_SelectsTierByTotalMemory(long totalGb, string expectedTier, string expectedPath)
    {
        var options = ThreeTierOptions();

        var selection = LocalModelTierSelector.Apply(options, totalGb << 30);

        selection.Should().NotBeNull();
        selection!.TierName.Should().Be(expectedTier);
        selection.ModelPath.Should().Be(expectedPath);
        options.ModelPath.Should().Be(expectedPath);
    }

    [Fact]
    public void Apply_MemoryBelowAllThresholds_SelectsLowestThresholdTier()
    {
        var options = new LocalInferenceOptions
        {
            ModelTiers =
            [
                new LocalModelTierOptions { Name = "gemma-4-4b", MinTotalMemoryGb = 12, ModelPath = Path4b },
                new LocalModelTierOptions { Name = "gemma-4-12b", MinTotalMemoryGb = 24, ModelPath = Path12b }
            ]
        };

        var selection = LocalModelTierSelector.Apply(options, 8L << 30);

        selection!.TierName.Should().Be("gemma-4-4b");
        options.ModelPath.Should().Be(Path4b);
    }

    [Fact]
    public void Apply_TiersDeclaredUnordered_StillSelectsCorrectTier()
    {
        var options = new LocalInferenceOptions
        {
            ModelTiers =
            [
                new LocalModelTierOptions { Name = "gemma-4-12b", MinTotalMemoryGb = 24, ModelPath = Path12b },
                new LocalModelTierOptions { Name = "gemma-4-2b", MinTotalMemoryGb = 0, ModelPath = Path2b },
                new LocalModelTierOptions { Name = "gemma-4-4b", MinTotalMemoryGb = 12, ModelPath = Path4b }
            ]
        };

        var selection = LocalModelTierSelector.Apply(options, 16L << 30);

        selection!.TierName.Should().Be("gemma-4-4b");
    }

    [Fact]
    public void Apply_TierWithBlankModelPath_Throws()
    {
        var options = new LocalInferenceOptions
        {
            ModelTiers =
            [
                new LocalModelTierOptions { Name = "broken", MinTotalMemoryGb = 0, ModelPath = " " }
            ]
        };

        var act = () => LocalModelTierSelector.Apply(options, 16L << 30);

        act.Should().Throw<InvalidOperationException>().WithMessage("*broken*");
    }

    [Fact]
    public void Apply_ReturnsSelectionRecordWithTotalMemoryGb()
    {
        var options = ThreeTierOptions();

        var selection = LocalModelTierSelector.Apply(options, 32L << 30);

        selection!.TotalMemoryGb.Should().BeApproximately(32.0, 0.01);
    }
}
