using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Default <see cref="IRoutingStrategyResolver"/> that maps strategy names
/// to the 5 built-in catalog routing strategies.
/// Strategies are created once and cached in a thread-safe dictionary.
/// </summary>
public sealed class RoutingStrategyResolver : IRoutingStrategyResolver
{
    private readonly IProviderCatalog _catalog;
    private readonly ConcurrentDictionary<string, Core.Catalog.IRoutingStrategy> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RoutingStrategyResolver(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public Core.Catalog.IRoutingStrategy Resolve(string strategyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);

        return _cache.GetOrAdd(strategyName, CreateStrategy);
    }

    private Core.Catalog.IRoutingStrategy CreateStrategy(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "round_robin" => new RoundRobinStrategy(_catalog),
            "shuffle" => new ShuffleStrategy(_catalog),
            "latency" => new LatencyStrategy(_catalog),
            "cost" => new CostStrategy(_catalog),
            "least_busy" => new LeastBusyStrategy(_catalog),
            _ => throw new ArgumentException(
                $"Unknown routing strategy '{name}'. Supported strategies: round_robin, shuffle, latency, cost, least_busy.",
                nameof(name))
        };
    }
}
