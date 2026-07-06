namespace Blaze.LlmGateway.Infrastructure.Provider;

using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Sockets;

/// <summary>
/// Implementation of ICodebrewRouterProviderBuilder.
/// Manages feature registration, validation, and DI wiring for CodebrewRouterProvider.
/// </summary>
internal class CodebrewRouterProviderBuilder : ICodebrewRouterProviderBuilder
{
    private readonly IServiceCollection _services;
    private readonly CodebrewRouterProviderOptions _options;

    // Feature registration state (track dependencies)
    private HealthCheckOptions? _healthCheckOptions;
    private DiscoveryOptions? _discoveryOptions;
    private RoutingOptions? _routingOptions;
    private Func<IServiceProvider, IRoutingStrategy>? _customStrategyFactory;
    private Func<IServiceProvider, IChatClient>? _customLocalClientFactory;

    public CodebrewRouterProviderBuilder(
        IServiceCollection services,
        CodebrewRouterProviderOptions options)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ICodebrewRouterProviderBuilder WithHealthChecks(
        Action<HealthCheckOptions>? configure = null)
    {
        _healthCheckOptions = new HealthCheckOptions();
        configure?.Invoke(_healthCheckOptions);
        return this;
    }

    public ICodebrewRouterProviderBuilder WithDiscovery(
        Action<DiscoveryOptions>? configure = null)
    {
        _discoveryOptions = new DiscoveryOptions();
        configure?.Invoke(_discoveryOptions);
        return this;
    }

    public ICodebrewRouterProviderBuilder WithRouting(
        Action<RoutingOptions>? configure = null)
    {
        _routingOptions = new RoutingOptions();
        configure?.Invoke(_routingOptions);
        return this;
    }

    public ICodebrewRouterProviderBuilder WithRoutingStrategy<TStrategy>(
        Func<IServiceProvider, TStrategy>? factory = null)
        where TStrategy : class, IRoutingStrategy
    {
        _customStrategyFactory = (sp) => factory?.Invoke(sp) ?? sp.GetRequiredService<TStrategy>();
        return this;
    }

    public ICodebrewRouterProviderBuilder WithLocalClient<TClient>(
        Func<IServiceProvider, TClient>? factory = null)
        where TClient : class, IChatClient
    {
        _customLocalClientFactory = (sp) => factory?.Invoke(sp) ?? sp.GetRequiredService<TClient>();
        return this;
    }

    public void Validate()
    {
        var errors = new List<ValidationError>();

        // Check: LocalEndpoint is required and valid URI
        if (string.IsNullOrWhiteSpace(_options.LocalEndpoint))
        {
            errors.Add(new ValidationError(
                "MISSING_LOCAL_ENDPOINT",
                "LocalEndpoint is required.",
                "Set CodebrewRouterProviderOptions.LocalEndpoint to a valid HTTP/HTTPS URI (e.g., 'http://localhost:11434')"));
        }
        else if (!Uri.TryCreate(_options.LocalEndpoint, UriKind.Absolute, out _))
        {
            errors.Add(new ValidationError(
                "INVALID_LOCAL_ENDPOINT",
                $"LocalEndpoint '{_options.LocalEndpoint}' is not a valid URI.",
                "Use absolute HTTP/HTTPS URIs only (e.g., 'http://192.168.1.100:11434')"));
        }

        // Check: RemoteDiscoveryEndpoint (if set) is valid URI
        if (!string.IsNullOrWhiteSpace(_options.RemoteDiscoveryEndpoint))
        {
            if (!Uri.TryCreate(_options.RemoteDiscoveryEndpoint, UriKind.Absolute, out _))
            {
                errors.Add(new ValidationError(
                    "INVALID_DISCOVERY_ENDPOINT",
                    $"RemoteDiscoveryEndpoint '{_options.RemoteDiscoveryEndpoint}' is not a valid URI.",
                    "Use absolute HTTP/HTTPS URIs only, or set to null to disable discovery"));
            }
        }

        // Check: Polling interval is reasonable (>= 10 seconds)
        if (_options.DiscoveryPollingIntervalSeconds < 10)
        {
            errors.Add(new ValidationError(
                "INVALID_POLLING_INTERVAL",
                $"DiscoveryPollingIntervalSeconds ({_options.DiscoveryPollingIntervalSeconds}s) is too short.",
                "Set to at least 10 seconds to avoid excessive polling"));
        }

        // Check: Circuit breaker threshold is positive
        if (_options.CircuitBreakerFailureThreshold <= 0)
        {
            errors.Add(new ValidationError(
                "INVALID_CIRCUIT_BREAKER_THRESHOLD",
                $"CircuitBreakerFailureThreshold ({_options.CircuitBreakerFailureThreshold}) must be > 0.",
                "Use the default value (5) or another positive integer"));
        }

        if (errors.Count > 0)
        {
            throw new CodebrewRouterProviderValidationException("Provider validation failed", errors.ToList());
        }
    }

    public async Task<ValidationResult> ValidateAsync()
    {
        var errors = new List<ValidationError>();

        // Sync validation first
        try
        {
            Validate();
        }
        catch (CodebrewRouterProviderValidationException ex)
        {
            errors.AddRange(ex.ValidationErrors);
        }

        // Async checks: TCP reachability on local endpoint
        try
        {
            if (Uri.TryCreate(_options.LocalEndpoint, UriKind.Absolute, out var uri) && uri.Host != "localhost")
            {
                using (var client = new TcpClient())
                {
                    var timeout = TimeSpan.FromSeconds(5);
                    var task = client.ConnectAsync(uri.Host, uri.Port);
                    if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
                    {
                        errors.Add(new ValidationError(
                            "LOCAL_ENDPOINT_UNREACHABLE",
                            $"Local endpoint '{_options.LocalEndpoint}' is not reachable (TCP timeout).",
                            "Verify the endpoint is running and accessible from this machine"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ValidationError(
                "LOCAL_ENDPOINT_CHECK_FAILED",
                $"Failed to check local endpoint connectivity: {ex.Message}",
                "This may be a temporary network issue; check again later"));
        }

        // Async checks: HTTP connectivity on discovery endpoint
        if (!string.IsNullOrWhiteSpace(_options.RemoteDiscoveryEndpoint))
        {
            try
            {
                using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var response = await httpClient.GetAsync(_options.RemoteDiscoveryEndpoint + "/health",
                        HttpCompletionOption.ResponseHeadersRead);
                    // Validation only fails on exception, not on non-success status
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ValidationError(
                    "DISCOVERY_ENDPOINT_UNREACHABLE",
                    $"Remote discovery endpoint '{_options.RemoteDiscoveryEndpoint}' is not reachable: {ex.Message}",
                    "Verify the endpoint is running, or set RemoteDiscoveryEndpoint to null to disable discovery"));
            }
        }

        if (errors.Count > 0)
        {
            return ValidationResult.Failure(errors.ToArray());
        }

        return ValidationResult.Success();
    }

    public IServiceCollection Build()
    {
        // Run validation first
        Validate();

        // Register core options
        _services.AddSingleton(_options);
        _services.AddSingleton(Options.Create(_options));

        // Register Phase 1 services will be registered by LocalInference.AddLocalInferenceServices
        // or directly if caller doesn't use LocalInference layer
        // For now, this is a placeholder for the Phase 1 services that would be registered
        
        return _services;
    }
}
