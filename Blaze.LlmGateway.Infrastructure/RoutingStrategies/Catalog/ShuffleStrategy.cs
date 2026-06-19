using Blaze.LlmGateway.Core.Catalog;
// Alias: resolve IRoutingStrategy from Core.Catalog (not the legacy one in parent namespace)
using CatRoutingStrategy = Blaze.LlmGateway.Core.Catalog.IRoutingStrategy;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Shuffle (weighted random) routing strategy.
/// Uses the <c>Weight</c> field to bias probability. Falls back to uniform random
/// when all weights are equal.
/// </summary>
public sealed class ShuffleStrategy : CatRoutingStrategy
{
    private readonly IProviderCatalog _catalog;

    public ShuffleStrategy(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Name => "shuffle";

    /// <inheritdoc />
    public ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context)
    {
        // Filter through health-aware filter first
        var eligible = HealthAwareRoutingFilter.Filter(candidates, _catalog, context);

        if (eligible.Count == 0)
            return null;

        // Check if all weights are equal — fall back to uniform random
        var allWeightsEqual = true;
        var firstWeight = eligible[0].Weight;

        for (var i = 1; i < eligible.Count; i++)
        {
            if (eligible[i].Weight != firstWeight)
            {
                allWeightsEqual = false;
                break;
            }
        }

        if (allWeightsEqual)
        {
            // Uniform random selection
            var idx = Random.Shared.Next(eligible.Count);
            return eligible[idx];
        }

        // Weighted random selection
        // Build cumulative weights distribution
        var totalWeight = 0L;
        var cumulativeWeights = new long[eligible.Count];

        for (var i = 0; i < eligible.Count; i++)
        {
            totalWeight += eligible[i].Weight;
            cumulativeWeights[i] = totalWeight;
        }

        // Pick a random point in the total weight range
        var randomPoint = Random.Shared.NextInt64(totalWeight);

        // Find the deployment at that point (binary search style, but linear is fine for small sets)
        for (var i = 0; i < cumulativeWeights.Length; i++)
        {
            if (randomPoint < cumulativeWeights[i])
                return eligible[i];
        }

        // Should never reach here, but fallback to last item
        return eligible[^1];
    }
}
