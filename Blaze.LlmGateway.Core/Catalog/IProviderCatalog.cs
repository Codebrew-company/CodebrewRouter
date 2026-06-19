namespace Blaze.LlmGateway.Core.Catalog;

/// <summary>
/// Registry of all provider deployments and model routing rules.
/// Populated at startup from configuration and queried at runtime to resolve
/// the best deployment for each request.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>Returns all registered deployments.</summary>
    IReadOnlyList<ProviderDeployment> GetAllDeployments();

    /// <summary>Returns all deployments registered for a given model name.</summary>
    IReadOnlyList<ProviderDeployment> GetDeploymentsForModel(string modelName);

    /// <summary>Returns the routing configuration for a model name, or null if not found.</summary>
    CatalogModelRoute? GetRoute(string modelName);

    /// <summary>Returns a specific deployment by name, or null if not found.</summary>
    ProviderDeployment? GetDeployment(string name);

    /// <summary>Reports a health observation for a deployment (called by health monitor).</summary>
    void ReportHealth(string deploymentName, bool healthy);

    /// <summary>Returns true if the deployment is currently considered healthy.</summary>
    bool IsHealthy(string deploymentName);
}
