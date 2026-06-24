using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Routing;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Shared pre-filter that all catalog routing strategies call before selecting a deployment.
/// Removes unhealthy, disabled, or capability-mismatched deployments.
/// </summary>
public static class HealthAwareRoutingFilter
{
    /// <summary>
    /// Logger set by CatalogModelRouter at construction time for emitting [ROUTER-HEALTH] verbose logs.
    /// Null when verbose route logging is disabled.
    /// </summary>
    internal static ILogger? Logger { get; set; }
    internal static bool VerboseLoggingEnabled { get; set; }

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
        var log = VerboseLoggingEnabled ? Logger : null;

        foreach (var dep in candidates)
        {
            // Remove disabled deployments
            if (!dep.Enabled)
            {
                if (log is not null)
                    RouterLog.Write(log, new RouterHealthEvent(dep.Name, "unhealthy", "disabled"));
                continue;
            }

            // Remove unhealthy deployments
            if (!catalog.IsHealthy(dep.Name))
            {
                if (log is not null)
                    RouterLog.Write(log, new RouterHealthEvent(dep.Name, "unhealthy", "health probe failure"));
                continue;
            }

            // Remove deployments missing required capabilities
            if (context.ToolsRequested && !ArrayContains(dep.Capabilities, "tools"))
            {
                if (log is not null)
                    RouterLog.Write(log, new RouterHealthEvent(dep.Name, "unhealthy", "missing capability: tools"));
                continue;
            }

            if (context.VisionRequested && !ArrayContains(dep.Capabilities, "vision"))
            {
                if (log is not null)
                    RouterLog.Write(log, new RouterHealthEvent(dep.Name, "unhealthy", "missing capability: vision"));
                continue;
            }

            if (log is not null)
                RouterLog.Write(log, new RouterHealthEvent(dep.Name, "healthy", "passed all filters"));

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
