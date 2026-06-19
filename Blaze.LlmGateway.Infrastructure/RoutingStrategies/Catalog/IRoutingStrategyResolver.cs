using Blaze.LlmGateway.Core.Catalog;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Resolves strategy names to <see cref="IRoutingStrategy"/> implementations.
/// Strategies are cached once created and reused for the lifetime of the resolver.
/// </summary>
public interface IRoutingStrategyResolver
{
    /// <summary>
    /// Returns the <see cref="IRoutingStrategy"/> registered for the given name.
    /// </summary>
    /// <param name="strategyName">Strategy name (e.g. "round_robin", "shuffle", "latency", "cost", "least_busy").</param>
    /// <returns>The matching strategy instance.</returns>
    /// <exception cref="ArgumentException">Thrown when no strategy is registered for the given name.</exception>
    Core.Catalog.IRoutingStrategy Resolve(string strategyName);
}
