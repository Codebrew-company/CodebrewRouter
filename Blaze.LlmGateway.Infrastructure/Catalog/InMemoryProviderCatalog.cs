using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// In-memory provider catalog populated from <c>ProviderCatalogOptions</c> at startup.
/// Thread-safe for concurrent reads and health state updates.
/// </summary>
public sealed class InMemoryProviderCatalog : IProviderCatalog
{
    private readonly IReadOnlyDictionary<string, ProviderDeployment> _deployments;
    private readonly IReadOnlyList<ProviderDeployment> _allDeployments;
    private readonly IReadOnlyDictionary<string, CatalogModelRoute> _routes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ProviderDeployment>> _deploymentsByModel;
    private readonly ConcurrentDictionary<string, DeploymentHealth> _healthState = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryProviderCatalog(ProviderCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Build deployment lookup by name
        var deployments = new Dictionary<string, ProviderDeployment>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in options.Deployments)
        {
            var dep = MapToDeployment(config);
            deployments[dep.Name] = dep;
        }
        _deployments = deployments;
        _allDeployments = deployments.Values.ToArray().AsReadOnly();

        // Build route lookup by model name
        var routes = new Dictionary<string, CatalogModelRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modelName, routeConfig) in options.ModelRouting)
        {
            routes[modelName] = new CatalogModelRoute
            {
                ModelName = modelName,
                Strategy = string.IsNullOrWhiteSpace(routeConfig.Strategy)
                    ? options.DefaultRoutingStrategy
                    : routeConfig.Strategy,
                Deployments = routeConfig.Deployments.ToArray(),
                Fallbacks = routeConfig.Fallbacks.ToArray()
            };
        }
        _routes = routes;

        // Build model→deployments index from deployment ModelName
        var byModel = new Dictionary<string, List<ProviderDeployment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dep in deployments.Values)
        {
            if (!byModel.ContainsKey(dep.ModelName))
                byModel[dep.ModelName] = [];
            byModel[dep.ModelName].Add(dep);
        }
        _deploymentsByModel = byModel.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ProviderDeployment>)kvp.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProviderDeployment> GetAllDeployments()
        => _allDeployments;

    public IReadOnlyList<ProviderDeployment> GetDeploymentsForModel(string modelName)
        => _deploymentsByModel.TryGetValue(modelName, out var list)
            ? list
            : [];

    public CatalogModelRoute? GetRoute(string modelName)
        => _routes.TryGetValue(modelName, out var route) ? route : null;

    public ProviderDeployment? GetDeployment(string name)
        => _deployments.TryGetValue(name, out var dep) ? dep : null;

    public void ReportHealth(string deploymentName, bool healthy)
    {
        var state = _healthState.GetOrAdd(deploymentName, _ => new DeploymentHealth());

        lock (state)
        {
            state.LastChecked = DateTime.UtcNow;

            if (healthy)
            {
                state.ConsecutiveFailures = 0;
                state.IsHealthy = true;
            }
            else
            {
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures >= 3)
                {
                    state.IsHealthy = false;
                }
            }
        }
    }

    public bool IsHealthy(string deploymentName)
    {
        // If no health data recorded, assume healthy (optimistic start)
        if (!_healthState.TryGetValue(deploymentName, out var state))
            return true;

        lock (state)
        {
            return state.IsHealthy;
        }
    }

    /// <summary>
    /// Resets all health state (used for testing or catalog reinitialization).
    /// </summary>
    public void ResetHealth()
        => _healthState.Clear();

    private static ProviderDeployment MapToDeployment(ProviderDeploymentConfig config)
        => new()
        {
            Name = config.Name,
            ModelName = config.ModelName,
            Provider = config.Provider,
            Endpoint = config.Endpoint,
            ApiKey = config.ApiKey,
            Model = config.Model,
            Weight = config.Weight,
            Priority = config.Priority,
            MaxContextTokens = config.MaxContextTokens,
            Capabilities = config.Capabilities.ToArray(),
            CostPerToken = config.CostPerToken,
            Tags = config.Tags.ToArray(),
            Enabled = config.Enabled
        };

    private sealed class DeploymentHealth
    {
        public bool IsHealthy { get; set; } = true;
        public int ConsecutiveFailures { get; set; }
        public DateTime LastChecked { get; set; }
    }
}
