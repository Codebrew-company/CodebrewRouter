namespace Blaze.LlmGateway.LocalInference.Provider;

using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.Provider;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ILogger<CodebrewRouterProviderBuilder>? _logger;

    // Feature registration state (track dependencies)
    private bool _discoveryRegistered;
    private bool _routingRegistered;
    private Infrastructure.Provider.HealthCheckOptions? _healthCheckOptions;
    private Infrastructure.Provider.DiscoveryOptions? _discoveryOptions;
    private Infrastructure.Provider.RoutingOptions? _routingOptions;
    private Func<IServiceProvider, IRoutingStrategy>? _customStrategyFactory;
    private Func<IServiceProvider, IChatClient>? _customLocalClientFactory;

    public CodebrewRouterProviderBuilder(
        IServiceCollection services,
        CodebrewRouterProviderOptions options)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = services.BuildServiceProvider()
            .GetService<ILogger<CodebrewRouterProviderBuilder>>();
    }

    public ICodebrewRouterProviderBuilder WithHealthChecks(
        Action<Infrastructure.Provider.HealthCheckOptions>? configure = null)
    {
        _healthCheckOptions = new Infrastructure.Provider.HealthCheckOptions();
        configure?.Invoke(_healthCheckOptions);
        _logger?.LogInformation("Health checks enabled");
        return this;
    }

    public ICodebrewRouterProviderBuilder WithDiscovery(
        Action<Infrastructure.Provider.DiscoveryOptions>? configure = null)
    {
        _discoveryOptions = new Infrastructure.Provider.DiscoveryOptions();
        configure?.Invoke(_discoveryOptions);
        _discoveryRegistered = true;
        _logger?.LogInformation("Remote discovery enabled");
        return this;
    }

    public ICodebrewRouterProviderBuilder WithRouting(
        Action<Infrastructure.Provider.RoutingOptions>? configure = null)
    {
        _routingOptions = new Infrastructure.Provider.RoutingOptions();
        configure?.Invoke(_routingOptions);
        _routingRegistered = true;
        _logger?.LogInformation("Routing strategy enabled");
        return this;
    }

    public ICodebrewRouterProviderBuilder WithRoutingStrategy<TStrategy>(
        Func<IServiceProvider, TStrategy>? factory = null)
        where TStrategy : class, IRoutingStrategy
    {
        _customStrategyFactory = (sp) => factory?.Invoke(sp) ?? sp.GetRequiredService<TStrategy>();
        _logger?.LogInformation("Custom routing strategy registered: {StrategyType}", typeof(TStrategy).Name);
        return this;
    }

    public ICodebrewRouterProviderBuilder WithLocalClient<TClient>(
        Func<IServiceProvider, TClient>? factory = null)
        where TClient : class, IChatClient
    {
        _customLocalClientFactory = (sp) => factory?.Invoke(sp) ?? sp.GetRequiredService<TClient>();
        _logger?.LogInformation("Custom local client registered: {ClientType}", typeof(TClient).Name);
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
                    "Use absolute HTTP/HTTPS URIs or set to null to disable discovery"));
            }
        }

        // Check: Option values are sensible
        if (_options.CacheAvailabilityTtlSeconds < 1)
        {
            errors.Add(new ValidationError(
                "INVALID_CACHE_TTL",
                "CacheAvailabilityTtlSeconds must be >= 1.",
                "Use a reasonable cache duration (e.g., 60 seconds)"));
        }

        if (_options.DiscoveryPollingIntervalSeconds < 5)
        {
            errors.Add(new ValidationError(
                "INVALID_POLLING_INTERVAL",
                "DiscoveryPollingIntervalSeconds must be >= 5.",
                "Use a reasonable polling interval (e.g., 300 seconds)"));
        }

        // Check: Routing dependency (requires discovery)
        if (_routingRegistered && !_discoveryRegistered && string.IsNullOrWhiteSpace(_options.RemoteDiscoveryEndpoint))
        {
            errors.Add(new ValidationError(
                "ROUTING_REQUIRES_DISCOVERY",
                "Routing strategy requires discovery to be enabled.",
                "Call .WithDiscovery() before .WithRouting(), or configure RemoteDiscoveryEndpoint in options"));
        }

        if (errors.Count > 0)
        {
            throw new CodebrewRouterProviderValidationException(
                $"Configuration validation failed with {errors.Count} error(s).",
                errors);
        }

        _logger?.LogInformation("Configuration validation passed");
    }

    public async Task<ValidationResult> ValidateAsync()
    {
        var errors = new List<ValidationError>();

        // Run synchronous validation first
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
            _logger?.LogWarning(ex, "Local endpoint connectivity check failed");
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
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.LogWarning("Discovery endpoint returned {StatusCode}", response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Discovery endpoint connectivity check failed");
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

        _logger?.LogInformation("Async validation passed");
        return ValidationResult.Success();
    }

    public IServiceCollection Build()
    {
        // Run validation first
        Validate();

        _logger?.LogInformation("Building CodebrewRouterProvider DI configuration");

        // Register core options
        _services.AddSingleton(_options);
        _services.AddSingleton(Options.Create(_options));

        // Register Phase 1 services
        _services.AddSingleton<ILocalModelAvailability, LocalModelAvailabilityService>();
        _services.AddSingleton<ICodebrewRouterDiscoveryService, CodebrewRouterDiscoveryService>();
        _services.AddSingleton<ILocalInferenceHealthManager, LocalInferenceHealthManager>();

        // Register health check (always, but can be disabled via options)
        if (_healthCheckOptions?.Enabled != false && _options.HealthChecksEnabled)
        {
            _services.AddHealthChecks()
                .AddCheck<LocalInferenceHealthManager>(
                    "codebrewrouter-provider",
                    _healthCheckOptions?.FailureStatus ?? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                    tags: ["provider", "readiness"]);
            _logger?.LogInformation("Health check registered");
        }

        // Register local chat client (default or custom)
        if (_customLocalClientFactory != null)
        {
            _services.AddKeyedSingleton<IChatClient>("LocalGemma", (sp, _) => _customLocalClientFactory(sp));
        }
        else
        {
            _services.AddKeyedSingleton<IChatClient>("LocalGemma", (sp, _) => 
                new LocalGemmaChatClient());
        }

        // Register routing strategy (default or custom)
        var routingStrategy = _customStrategyFactory != null
            ? new Func<IServiceProvider, IRoutingStrategy>(sp => _customStrategyFactory(sp))
            : new Func<IServiceProvider, IRoutingStrategy>(sp => 
            {
                var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>();
                var modelProvider = sp.GetRequiredService<IModelDistributionProvider>();
                var fallbackStrategy = new KeywordRoutingStrategy(
                    sp.GetService<ILogger<KeywordRoutingStrategy>>()!);
                var logger = sp.GetService<ILogger<HybridLocalRemoteRoutingStrategy>>();
                return new HybridLocalRemoteRoutingStrategy(opts.Value, modelProvider, fallbackStrategy, logger!);
            });

        _services.AddSingleton<IRoutingStrategy>(sp => routingStrategy(sp));

        _logger?.LogInformation("CodebrewRouterProvider DI configuration complete");
        return _services;
    }
}
