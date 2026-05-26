using Blaze.LlmGateway.Core.Catalog;
// Alias: resolve IRoutingStrategy from Core.Catalog (not the legacy one in parent namespace)
using CatRoutingStrategy = Blaze.LlmGateway.Core.Catalog.IRoutingStrategy;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Cost-based routing strategy that selects the cheapest eligible deployment
/// by <c>CostPerToken</c>. Ties are broken by random selection (Shuffle).
/// </summary>
public sealed class CostStrategy : CatRoutingStrategy
{
    private readonly IProviderCatalog _catalog;

    public CostStrategy(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Name => "cost";

    /// <inheritdoc />
    public ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context)
    {
        // Filter through health-aware filter first
        var eligible = HealthAwareRoutingFilter.Filter(candidates, _catalog, context);

        if (eligible.Count == 0)
            return null;

        // Find the minimum cost
        var minCost = double.MaxValue;
        for (var i = 0; i < eligible.Count; i++)
        {
            if (eligible[i].CostPerToken < minCost)
                minCost = eligible[i].CostPerToken;
        }

        // Collect all deployments with the minimum cost
        var cheapest = new List<ProviderDeployment>(eligible.Count);
        for (var i = 0; i < eligible.Count; i++)
        {
            if (eligible[i].CostPerToken == minCost)
                cheapest.Add(eligible[i]);
        }

        // If there's a tie, break randomly using Shuffle
        if (cheapest.Count > 1)
        {
            var idx = Random.Shared.Next(cheapest.Count);
            return cheapest[idx];
        }

        return cheapest[0];
    }
}
