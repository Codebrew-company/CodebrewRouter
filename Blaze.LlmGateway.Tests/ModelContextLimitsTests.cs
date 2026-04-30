using Blaze.LlmGateway.Infrastructure.ModelCatalog;
using FluentAssertions;

namespace Blaze.LlmGateway.Tests;

public class ModelContextLimitsTests
{
    [Theory]
    [InlineData("gpt-4o",              128_000, 16_384)]
    [InlineData("gpt-4o-mini",         128_000, 16_384)]
    [InlineData("gpt-4o-2024-08-06",   128_000, 16_384)]
    [InlineData("gemini-2.0-flash",    1_048_576, 8_192)]
    [InlineData("llama3.1:8b",         128_000, 4_096)]
    [InlineData("llama3.2:3b",         128_000, 4_096)]
    [InlineData("phi-4",               16_384,  4_096)]
    [InlineData("phi-4-mini",          16_384,  4_096)]
    [InlineData("qwen2.5:7b",          128_000, 4_096)]
    public void Lookup_KnownModel_ReturnsExpectedLimits(
        string modelId, int expectedContext, int expectedMaxOut)
    {
        var (ctx, maxOut) = ModelContextLimits.Lookup(modelId);
        ctx.Should().Be(expectedContext);
        maxOut.Should().Be(expectedMaxOut);
    }

    [Theory]
    [InlineData("unknown-model-xyz")]
    [InlineData("")]
    public void Lookup_UnknownModel_ReturnsNulls(string modelId)
    {
        var (ctx, maxOut) = ModelContextLimits.Lookup(modelId);
        ctx.Should().BeNull();
        maxOut.Should().BeNull();
    }
}
