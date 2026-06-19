using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// Routes a virtual model request through the provider catalog by:
/// 1. Looking up the <see cref="CatalogModelRoute"/> for the given model name
/// 2. Resolving the <see cref="IRoutingStrategy"/> specified by the route
/// 3. Selecting a healthy, capable deployment via the strategy
/// 4. Falling back to the route's fallback deployments when all primary deployments are exhausted
/// </summary>
public sealed class CatalogModelRouter
{
    private readonly IProviderCatalog _catalog;
    private readonly IRoutingStrategyResolver _strategyResolver;
    private readonly ILogger<CatalogModelRouter> _logger;

    public CatalogModelRouter(
        IProviderCatalog catalog,
        IRoutingStrategyResolver strategyResolver,
        ILogger<CatalogModelRouter> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _strategyResolver = strategyResolver ?? throw new ArgumentNullException(nameof(strategyResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Selects a deployment for the given catalog model name and request context.
    /// Returns null when no suitable deployment is found (no route, all unhealthy, empty pools).
    /// </summary>
    /// <param name="catalogModelName">The model name defined in ProviderCatalog.ModelRouting.</param>
    /// <param name="context">The request routing context.</param>
    /// <returns>A selected deployment, or null if none is available.</returns>
    public ProviderDeployment? SelectDeployment(
        string catalogModelName,
        RoutingContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogModelName);
        ArgumentNullException.ThrowIfNull(context);

        var route = _catalog.GetRoute(catalogModelName);
        if (route is null)
        {
            _logger.LogDebug("No route found for catalog model {CatalogModelName}", catalogModelName);
            return null;
        }

        _logger.LogDebug(
            "Routing catalog model {CatalogModelName} with strategy {Strategy}, {DeploymentCount} primary deployments, {FallbackCount} fallbacks",
            catalogModelName,
            route.Strategy,
            route.Deployments.Length,
            route.Fallbacks.Length);

        // Stage 1: Try primary deployments
        var strategy = _strategyResolver.Resolve(route.Strategy);
        var primaryDeployments = ResolveDeployments(route.Deployments);
        var selected = strategy.Select(primaryDeployments, context);

        if (selected is not null)
        {
            _logger.LogDebug(
                "Selected deployment {DeploymentName} for catalog model {CatalogModelName} via primary pool",
                selected.Name,
                catalogModelName);
            return selected;
        }

        // Stage 2: Try fallback deployments when no primary deployment is suitable
        if (route.Fallbacks.Length > 0 && !context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "No primary deployment available for catalog model {CatalogModelName}; trying {FallbackCount} fallback(s)",
                catalogModelName,
                route.Fallbacks.Length);

            var fallbackDeployments = ResolveDeployments(route.Fallbacks);
            selected = strategy.Select(fallbackDeployments, context);

            if (selected is not null)
            {
                _logger.LogInformation(
                    "Selected fallback deployment {DeploymentName} for catalog model {CatalogModelName}",
                    selected.Name,
                    catalogModelName);
                return selected;
            }
        }

        _logger.LogWarning(
            "No suitable deployment found for catalog model {CatalogModelName} after primary and fallback pools",
            catalogModelName);
        return null;
    }

    private IReadOnlyList<ProviderDeployment> ResolveDeployments(string[] deploymentNames)
    {
        var result = new List<ProviderDeployment>(deploymentNames.Length);
        foreach (var name in deploymentNames)
        {
            var dep = _catalog.GetDeployment(name);
            if (dep is not null)
                result.Add(dep);
        }

        return result.AsReadOnly();
    }
}
