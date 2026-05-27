using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;
// Alias: resolve IRoutingStrategy from Core.Catalog (not the legacy one in parent namespace)
using CatRoutingStrategy = Blaze.LlmGateway.Core.Catalog.IRoutingStrategy;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Least-busy routing strategy that selects the deployment with the fewest
/// in-flight requests. Tracks concurrent request counts per deployment.
/// Falls back to random when all deployments have the same count.
/// </summary>
public sealed class LeastBusyStrategy : CatRoutingStrategy
{
    private readonly IProviderCatalog _catalog;
    private readonly ConcurrentDictionary<string, int> _inFlightCounts = new(StringComparer.OrdinalIgnoreCase);

    public LeastBusyStrategy(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Name => "least_busy";

    /// <inheritdoc />
    public ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context)
    {
        // Filter through health-aware filter first
        var eligible = HealthAwareRoutingFilter.Filter(candidates, _catalog, context);

        if (eligible.Count == 0)
            return null;

        // Find the minimum in-flight count
        var minInFlight = int.MaxValue;
        for (var i = 0; i < eligible.Count; i++)
        {
            var count = _inFlightCounts.GetOrAdd(eligible[i].Name, 0);
            if (count < minInFlight)
                minInFlight = count;
        }

        // Collect all deployments with the minimum in-flight count
        var leastBusy = new List<ProviderDeployment>(eligible.Count);
        for (var i = 0; i < eligible.Count; i++)
        {
            var dep = eligible[i];
            if (_inFlightCounts.GetOrAdd(dep.Name, 0) == minInFlight)
                leastBusy.Add(dep);
        }

        // If all have the same count, fall back to random
        if (leastBusy.Count == eligible.Count)
        {
            var idx = Random.Shared.Next(eligible.Count);
            var selected = eligible[idx];
            _inFlightCounts.AddOrUpdate(selected.Name, 1, (_, count) => count + 1);
            return selected;
        }

        // If there's a tie among least-busy, pick randomly
        ProviderDeployment chosen;
        if (leastBusy.Count > 1)
        {
            var idx = Random.Shared.Next(leastBusy.Count);
            chosen = leastBusy[idx];
        }
        else
        {
            chosen = leastBusy[0];
        }

        // Increment in-flight for the chosen deployment
        _inFlightCounts.AddOrUpdate(chosen.Name, 1, (_, count) => count + 1);

        return chosen;
    }

    /// <summary>
    /// Reports that a request has completed, decrementing the in-flight count for the deployment.
    /// Must be called by the caller when the request completes.
    /// </summary>
    public void Release(string deploymentName)
    {
        _inFlightCounts.AddOrUpdate(deploymentName, 0, (_, count) =>
        {
            if (count > 0)
                return count - 1;
            return 0;
        });
    }

    /// <summary>
    /// Returns the current in-flight count for a deployment (useful for testing).
    /// </summary>
    public int GetInFlightCount(string deploymentName)
        => _inFlightCounts.GetOrAdd(deploymentName, 0);
}
