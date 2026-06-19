using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// In-memory provider catalog populated from <c>ProviderCatalogOptions</c> at startup.
/// Thread-safe for concurrent reads and health state updates.
///
/// Supports dynamic reload via <see cref="Reload"/> — atomically swaps the internal
/// dictionary references while preserving health state for deployments that still exist
/// after the reload.
/// </summary>
public sealed class InMemoryProviderCatalog : IProviderCatalog
{
    private volatile ImmutableCatalog _current;
    private readonly ConcurrentDictionary<string, DeploymentHealth> _healthState = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemoryProviderCatalog>? _logger;

    public InMemoryProviderCatalog(ProviderCatalogOptions options, ILogger<InMemoryProviderCatalog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _current = BuildImmutableCatalog(options);
    }

    public IReadOnlyList<ProviderDeployment> GetAllDeployments()
        => _current.AllDeployments;

    public IReadOnlyList<ProviderDeployment> GetDeploymentsForModel(string modelName)
        => _current.DeploymentsByModel.TryGetValue(modelName, out var list)
            ? list
            : [];

    public CatalogModelRoute? GetRoute(string modelName)
        => _current.Routes.TryGetValue(modelName, out var route) ? route : null;

    public ProviderDeployment? GetDeployment(string name)
        => _current.Deployments.TryGetValue(name, out var dep) ? dep : null;

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

    /// <summary>
    /// Atomically reloads the catalog from the given options, preserving health
    /// state for deployments that still exist after the reload.
    /// </summary>
    /// <param name="options">The new <see cref="ProviderCatalogOptions"/>.</param>
    public void Reload(ProviderCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var newCatalog = BuildImmutableCatalog(options);

        // Clean up health state for deployments that no longer exist
        var newDeploymentNames = new HashSet<string>(newCatalog.Deployments.Keys, StringComparer.OrdinalIgnoreCase);
        var staleNames = _healthState.Keys
            .Where(k => !newDeploymentNames.Contains(k))
            .ToList();

        foreach (var stale in staleNames)
        {
            _healthState.TryRemove(stale, out _);
        }

        // Atomically swap the catalog
        var old = Interlocked.Exchange(ref _current, newCatalog);

        _logger?.LogInformation(
            "Provider catalog reloaded: {Deployments} deployments, {Routes} routes. {StaleCount} stale health entries cleaned.",
            newDeploymentNames.Count, newCatalog.Routes.Count, staleNames.Count);
    }

    private static ImmutableCatalog BuildImmutableCatalog(ProviderCatalogOptions options)
    {
        // Build deployment lookup by name
        var deployments = new Dictionary<string, ProviderDeployment>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in options.Deployments)
        {
            var dep = MapToDeployment(config);
            deployments[dep.Name] = dep;
        }

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

        // Build model→deployments index from deployment ModelName
        var byModel = new Dictionary<string, List<ProviderDeployment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dep in deployments.Values)
        {
            if (!byModel.ContainsKey(dep.ModelName))
                byModel[dep.ModelName] = [];
            byModel[dep.ModelName].Add(dep);
        }

        var deploymentsByModel = byModel.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ProviderDeployment>)kvp.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

        return new ImmutableCatalog(
            deployments,
            deployments.Values.ToArray(),
            routes,
            deploymentsByModel);
    }

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
            MaxRequestsPerMinute = config.MaxRequestsPerMinute,
            MaxTokensPerMinute = config.MaxTokensPerMinute,
            Enabled = config.Enabled
        };

    /// <summary>
    /// Immutable snapshot of the catalog state, swapped atomically on reload.
    /// </summary>
    private sealed class ImmutableCatalog
    {
        public IReadOnlyDictionary<string, ProviderDeployment> Deployments { get; }
        public IReadOnlyList<ProviderDeployment> AllDeployments { get; }
        public IReadOnlyDictionary<string, CatalogModelRoute> Routes { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<ProviderDeployment>> DeploymentsByModel { get; }

        public ImmutableCatalog(
            IReadOnlyDictionary<string, ProviderDeployment> deployments,
            IReadOnlyList<ProviderDeployment> allDeployments,
            IReadOnlyDictionary<string, CatalogModelRoute> routes,
            IReadOnlyDictionary<string, IReadOnlyList<ProviderDeployment>> deploymentsByModel)
        {
            Deployments = deployments;
            AllDeployments = allDeployments;
            Routes = routes;
            DeploymentsByModel = deploymentsByModel;
        }
    }

    private sealed class DeploymentHealth
    {
        public bool IsHealthy { get; set; } = true;
        public int ConsecutiveFailures { get; set; }
        public DateTime LastChecked { get; set; }
    }
}
