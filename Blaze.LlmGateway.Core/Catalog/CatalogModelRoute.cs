namespace Blaze.LlmGateway.Core.Catalog;

/// <summary>
/// Defines how a model name is routed through the catalog: which strategy to use,
/// which deployments are eligible, and which fallbacks to try when none are healthy.
/// </summary>
public sealed record CatalogModelRoute
{
    /// <summary>Logical model name (matches <c>ProviderDeployment.ModelName</c>).</summary>
    public required string ModelName { get; init; }

    /// <summary>
    /// Routing strategy name: <c>"round_robin"</c>, <c>"shuffle"</c>, <c>"latency"</c>, <c>"cost"</c>, <c>"least_busy"</c>.
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>Ordered list of deployment names eligible for this route.</summary>
    public required string[] Deployments { get; init; }

    /// <summary>Ordered list of deployment names to try as fallbacks when all primary deployments are unhealthy.</summary>
    public string[] Fallbacks { get; init; } = [];
}
