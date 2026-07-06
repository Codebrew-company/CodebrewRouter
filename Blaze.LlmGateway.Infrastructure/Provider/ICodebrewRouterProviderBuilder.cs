namespace Blaze.LlmGateway.Infrastructure.Provider;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;

/// <summary>
/// Fluent builder for CodebrewRouterProvider configuration.
/// All methods are optional; defaults enable full Phase 1 stack.
/// </summary>
public interface ICodebrewRouterProviderBuilder
{
    /// <summary>
    /// Enable and optionally configure health checks.
    /// </summary>
    /// <param name="configure">Optional callback to customize health check behavior.</param>
    /// <remarks>
    /// Health checks are always registered but can be disabled via HealthCheckOptions.Enabled = false.
    /// This maintains Aspire compatibility while allowing mobile to disable.
    /// </remarks>
    ICodebrewRouterProviderBuilder WithHealthChecks(
        Action<HealthCheckOptions>? configure = null);

    /// <summary>
    /// Enable and optionally configure remote model discovery.
    /// </summary>
    /// <param name="configure">Optional callback to customize discovery behavior.</param>
    /// <remarks>
    /// If RemoteDiscoveryEndpoint is null in options, discovery is inactive even when enabled here.
    /// </remarks>
    ICodebrewRouterProviderBuilder WithDiscovery(
        Action<DiscoveryOptions>? configure = null);

    /// <summary>
    /// Enable and optionally configure routing strategy.
    /// </summary>
    /// <param name="configure">Optional callback to customize routing behavior.</param>
    /// <remarks>
    /// Requires discovery to be registered first. Will throw in Build() if dependency violated.
    /// </remarks>
    ICodebrewRouterProviderBuilder WithRouting(
        Action<RoutingOptions>? configure = null);

    /// <summary>
    /// Replace the routing strategy with a custom implementation.
    /// </summary>
    /// <typeparam name="TStrategy">Custom routing strategy type (must implement IRoutingStrategy).</typeparam>
    /// <param name="factory">Optional factory to create the strategy. If null, uses DI resolution.</param>
    /// <remarks>
    /// Advanced scenario. Requires discovery to be registered first.
    /// </remarks>
    ICodebrewRouterProviderBuilder WithRoutingStrategy<TStrategy>(
        Func<IServiceProvider, TStrategy>? factory = null)
        where TStrategy : class, IRoutingStrategy;

    /// <summary>
    /// Replace the local inference client with a custom implementation.
    /// </summary>
    /// <typeparam name="TClient">Custom chat client type.</typeparam>
    /// <param name="factory">Optional factory to create the client. If null, uses DI resolution.</param>
    /// <remarks>
    /// Advanced scenario. Registered as keyed "LocalGemma" IChatClient.
    /// </remarks>
    ICodebrewRouterProviderBuilder WithLocalClient<TClient>(
        Func<IServiceProvider, TClient>? factory = null)
        where TClient : class, IChatClient;

    /// <summary>
    /// Run validation checks on configuration (synchronous).
    /// </summary>
    /// <remarks>
    /// Checks: endpoint format, required options presence, URI validity.
    /// Does NOT check connectivity (see ValidateAsync for async checks).
    /// Throws CodebrewRouterProviderValidationException if validation fails.
    /// </remarks>
    void Validate();

    /// <summary>
    /// Run comprehensive validation (asynchronous).
    /// </summary>
    /// <remarks>
    /// Checks: endpoint format, required options, URI validity, TCP reachability, HTTP connectivity.
    /// Returns ValidationResult (doesn't throw).
    /// Optional: call before Build() for eager diagnostics.
    /// </remarks>
    Task<ValidationResult> ValidateAsync();

    /// <summary>
    /// Finalize registration and return the service collection.
    /// Calls Validate() internally; throws on validation failure.
    /// </summary>
    IServiceCollection Build();
}

/// <summary>
/// Health check configuration options.
/// </summary>
public class HealthCheckOptions
{
    /// <summary>
    /// Enable health check.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Custom failure status threshold.
    /// Default: Unhealthy.
    /// </summary>
    public Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus FailureStatus 
        { get; set; } = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy;
}

/// <summary>
/// Remote discovery configuration options.
/// </summary>
public class DiscoveryOptions
{
    /// <summary>
    /// Custom polling interval (seconds).
    /// Overrides CodebrewRouterProviderOptions.DiscoveryPollingIntervalSeconds if set.
    /// </summary>
    public int? PollingIntervalSeconds { get; set; }

    /// <summary>
    /// Custom HTTP timeout (seconds).
    /// Overrides CodebrewRouterProviderOptions.DiscoveryTimeoutSeconds if set.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Custom circuit breaker failure threshold.
    /// Overrides CodebrewRouterProviderOptions.CircuitBreakerFailureThreshold if set.
    /// </summary>
    public int? CircuitBreakerFailureThreshold { get; set; }

    /// <summary>
    /// Custom circuit breaker cooldown (minutes).
    /// Overrides CodebrewRouterProviderOptions.CircuitBreakerCooldownMinutes if set.
    /// </summary>
    public int? CircuitBreakerCooldownMinutes { get; set; }
}

/// <summary>
/// Routing strategy configuration options.
/// </summary>
public class RoutingOptions
{
    /// <summary>
    /// Custom fallback strategy if primary routing fails.
    /// Default: KeywordRoutingStrategy.
    /// </summary>
    public Type? FallbackStrategyType { get; set; }

    /// <summary>
    /// Enable hybrid local/remote routing.
    /// Default: true.
    /// </summary>
    public bool EnableHybridRouting { get; set; } = true;
}
