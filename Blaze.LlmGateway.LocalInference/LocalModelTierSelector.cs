using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Picks the local model tier matching the host's total memory and rewrites
/// <see cref="LocalInferenceOptions.ModelPath"/> before any downstream consumer reads it.
/// </summary>
internal static class LocalModelTierSelector
{
    /// <summary>
    /// Applies tier selection. Returns <c>null</c> when no tiers are configured
    /// (legacy single-<c>ModelPath</c> behavior); otherwise mutates
    /// <paramref name="options"/>.<c>ModelPath</c> and returns the selection.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured tier has a blank ModelPath.</exception>
    internal static LocalModelTierSelection? Apply(LocalInferenceOptions options, long totalMemoryBytes)
    {
        if (options.ModelTiers.Count == 0)
        {
            return null;
        }

        foreach (var tier in options.ModelTiers)
        {
            if (string.IsNullOrWhiteSpace(tier.ModelPath))
            {
                throw new InvalidOperationException(
                    $"LocalInference model tier '{tier.Name}' (MinTotalMemoryGb={tier.MinTotalMemoryGb}) has no ModelPath configured.");
            }
        }

        // ponytail: no OOM downgrade-retry — thresholds keep peak working set under ~60% of RAM,
        // and BlockStartupUntilWarm is the repo's fail-fast policy. Tune MinTotalMemoryGb instead.
        var ordered = options.ModelTiers.OrderByDescending(tier => tier.MinTotalMemoryGb).ToArray();
        var selected = ordered.FirstOrDefault(tier => totalMemoryBytes >= (long)tier.MinTotalMemoryGb << 30)
            ?? ordered[^1]; // below every threshold: attempt the smallest model rather than refuse

        options.ModelPath = selected.ModelPath;
        return new LocalModelTierSelection(
            selected.Name,
            selected.ModelPath,
            totalMemoryBytes / (double)(1L << 30));
    }
}

/// <summary>The tier chosen at startup, registered in DI for warmup logging.</summary>
public sealed record LocalModelTierSelection(string TierName, string ModelPath, double TotalMemoryGb);
