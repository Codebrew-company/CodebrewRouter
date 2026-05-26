namespace Blaze.LlmGateway.Core.Catalog;

/// <summary>
/// Pluggable routing strategy that selects a deployment from a set of candidates.
/// Implementations should be stateless or thread-safe — they are used as singletons.
/// </summary>
public interface IRoutingStrategy
{
    /// <summary>Display name for the strategy (e.g. "round_robin", "shuffle", "latency").</summary>
    string Name { get; }

    /// <summary>
    /// Selects a deployment from the candidate list using the strategy's logic.
    /// Returns null when no deployment is suitable (all unhealthy, empty pool, etc.).
    /// </summary>
    ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context);
}
