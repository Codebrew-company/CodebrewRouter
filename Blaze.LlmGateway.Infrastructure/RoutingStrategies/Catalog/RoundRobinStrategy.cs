using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;
// Alias: resolve IRoutingStrategy from Core.Catalog (not the legacy one in parent namespace)
using CatRoutingStrategy = Blaze.LlmGateway.Core.Catalog.IRoutingStrategy;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Round-robin routing strategy that cycles through eligible deployments in order.
/// Tracks a per-model-name counter to distribute requests evenly.
/// </summary>
public sealed class RoundRobinStrategy : CatRoutingStrategy
{
    private readonly IProviderCatalog _catalog;
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);

    public RoundRobinStrategy(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Name => "round_robin";

    /// <inheritdoc />
    public ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context)
    {
        // Filter through health-aware filter first
        var eligible = HealthAwareRoutingFilter.Filter(candidates, _catalog, context);

        if (eligible.Count == 0)
            return null;

        // Get or increment the counter for this model
        var counter = _counters.AddOrUpdate(
            context.ModelId,
            _ => 1L,
            (_, val) => val + 1);

        var index = (int)((counter - 1) % eligible.Count);
        return eligible[index];
    }

    /// <summary>
    /// Resets the round-robin counter for a given model (useful for testing).
    /// </summary>
    public void ResetCounter(string modelId)
        => _counters.TryRemove(modelId, out _);
}
