namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for the provider catalog and routing strategy engine.
/// Binds from <c>LlmGateway:ProviderCatalog</c> in appsettings.
/// </summary>
public class ProviderCatalogOptions
{
    public const string SectionName = "ProviderCatalog";

    /// <summary>Default routing strategy when a model route doesn't specify one.</summary>
    public string DefaultRoutingStrategy { get; set; } = "round_robin";

    /// <summary>Default fallback strategy. Only "failover" is supported in Phase 1.</summary>
    public string DefaultFallbackStrategy { get; set; } = "failover";

    /// <summary>Interval between health probe cycles in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>Consecutive failures before a deployment is marked unhealthy.</summary>
    public int UnhealthyThreshold { get; set; } = 3;

    /// <summary>Interval between recovery probes in seconds.</summary>
    public int RecoveryIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Health check method used by <c>HealthProbeService</c>.
    /// <list type="bullet">
    /// <item><c>ping</c> — send a short chat completion (\"ping\" with 1 token) through each deployment's keyed <c>IChatClient</c>.</item>
    /// <item><c>none</c> — disable proactive health probes entirely; rely on the circuit breaker (request failures) for health tracking.</item>
    /// <item><c>head</c> — (future) HTTP HEAD to the deployment endpoint. Not implemented yet.</item>
    /// </list>
    /// </summary>
    public string HealthCheckMethod { get; set; } = "ping";

    /// <summary>All registered provider deployments.</summary>
    public List<ProviderDeploymentConfig> Deployments { get; set; } = [];

    /// <summary>Per-model routing configuration.</summary>
    public Dictionary<string, ModelRouteConfig> ModelRouting { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configurable properties of a provider deployment (the serializable form of <c>ProviderDeployment</c>).
/// </summary>
public class ProviderDeploymentConfig
{
    public string Name { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int Weight { get; set; } = 1;
    public int Priority { get; set; } = 10;
    public int MaxContextTokens { get; set; } = 4096;
    public List<string> Capabilities { get; set; } = [];
    public double CostPerToken { get; set; } = 0;
    public List<string> Tags { get; set; } = [];
    public int MaxRequestsPerMinute { get; set; } = 0;
    public int MaxTokensPerMinute { get; set; } = 0;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Configurable routing properties for a single model (the serializable form of <c>CatalogModelRoute</c>).
/// </summary>
public class ModelRouteConfig
{
    /// <summary>Routing strategy name for this model.</summary>
    public string Strategy { get; set; } = "round_robin";

    /// <summary>Ordered list of deployment names to use for this model.</summary>
    public List<string> Deployments { get; set; } = [];

    /// <summary>Ordered list of fallback deployment names.</summary>
    public List<string> Fallbacks { get; set; } = [];
}
