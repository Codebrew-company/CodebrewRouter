using Blaze.LlmGateway.Core.Catalog;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Shared pre-filter that all catalog routing strategies call before selecting a deployment.
/// Removes unhealthy, disabled, or capability-mismatched deployments.
/// </summary>
public static class HealthAwareRoutingFilter
{
    /// <summary>
    /// Filters the candidate list by removing:
    /// - Deployments with <c>Enabled = false</c>
    /// - Deployments marked unhealthy by the catalog
    /// - Deployments missing required capabilities (vision/tools) from the context
    /// </summary>
    public static IReadOnlyList<ProviderDeployment> Filter(
        IReadOnlyList<ProviderDeployment> candidates,
        IProviderCatalog catalog,
        RoutingContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<ProviderDeployment>(candidates.Count);

        foreach (var dep in candidates)
        {
            // Remove disabled deployments
            if (!dep.Enabled)
                continue;

            // Remove unhealthy deployments
            if (!catalog.IsHealthy(dep.Name))
                continue;

            // Remove deployments missing required capabilities
            if (context.ToolsRequested && !ArrayContains(dep.Capabilities, "tools"))
                continue;

            if (context.VisionRequested && !ArrayContains(dep.Capabilities, "vision"))
                continue;

            result.Add(dep);
        }

        return result.AsReadOnly();
    }

    private static bool ArrayContains(string[] array, string value)
    {
        for (var i = 0; i < array.Length; i++)
        {
            if (string.Equals(array[i], value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
