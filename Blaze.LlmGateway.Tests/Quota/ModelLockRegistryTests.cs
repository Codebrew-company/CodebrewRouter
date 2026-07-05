using Blaze.LlmGateway.Infrastructure.Quota;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blaze.LlmGateway.Tests.Quota;

/// <summary>P3.1: model-lock cooldowns (9router accountFallback pattern).</summary>
public sealed class ModelLockRegistryTests
{
    private static ModelLockRegistry Create() => new(NullLogger<ModelLockRegistry>.Instance);

    [Theory]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("Rate limit exceeded for deployment 'x' (request).")]
    [InlineData("insufficient_quota: You exceeded your current quota")]
    [InlineData("The model is overloaded, please retry later")]
    public void RateLimitSignals_LockTheProvider(string message)
    {
        var registry = Create();

        registry.ReportFailure("OpenCodeGo_DeepSeekV4Pro", message);

        registry.IsLocked("OpenCodeGo_DeepSeekV4Pro", out var remaining).Should().BeTrue();
        remaining.Should().BePositive();
    }

    [Fact]
    public void NonRateLimitFailure_DoesNotLock()
    {
        var registry = Create();

        registry.ReportFailure("LmStudio", "Connection refused");

        registry.IsLocked("LmStudio", out _).Should().BeFalse();
    }

    [Fact]
    public void RepeatedFailures_EscalateBackoffLevel()
    {
        var registry = Create();

        registry.ReportFailure("P", "429");
        registry.ReportFailure("P", "429");
        registry.ReportFailure("P", "429");

        var snapshot = registry.Snapshot();
        snapshot.Should().ContainSingle().Which.BackoffLevel.Should().Be(3);
        // level 3 → cooldown 2s * 2^2 = 8s from the last report
        registry.IsLocked("P", out var remaining).Should().BeTrue();
        remaining.Should().BeGreaterThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Success_ClearsLockAndResetsLevel()
    {
        var registry = Create();
        registry.ReportFailure("P", "429");

        registry.ReportSuccess("P");

        registry.IsLocked("P", out _).Should().BeFalse();
        registry.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_ExposesExpiryForCountdowns()
    {
        var registry = Create();
        registry.ReportFailure("P", "quota exceeded");

        var info = registry.Snapshot().Should().ContainSingle().Subject;
        info.ProviderKey.Should().Be("P");
        info.LockedUntil.Should().BeAfter(DateTimeOffset.UtcNow);
        info.Reason.Should().Contain("quota");
    }
}
