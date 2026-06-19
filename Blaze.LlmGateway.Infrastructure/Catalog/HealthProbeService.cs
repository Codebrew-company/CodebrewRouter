using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// Background service that periodically probes all catalog deployments by sending
/// a short ping request through each deployment's keyed <see cref="IChatClient"/>.
/// Reports health observations back to <see cref="IProviderCatalog.ReportHealth"/>
/// and emits OpenTelemetry metrics via <see cref="CatalogMetrics"/>.
/// </summary>
public sealed class HealthProbeService : BackgroundService
{
    private readonly IProviderCatalog _catalog;
    private readonly IServiceProvider _serviceProvider;
    private readonly ProviderCatalogOptions _options;
    private readonly ILogger<HealthProbeService> _logger;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public HealthProbeService(
        IProviderCatalog catalog,
        IServiceProvider serviceProvider,
        IOptions<ProviderCatalogOptions> options,
        ILogger<HealthProbeService> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Skip health probes entirely when HealthCheckMethod is "none"
        if (string.Equals(_options.HealthCheckMethod, "none", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "HealthProbeService disabled (HealthCheckMethod={Method}); relying on circuit breaker",
                _options.HealthCheckMethod);
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        _logger.LogInformation("HealthProbeService started with {Interval}s check interval ({Method})",
            _options.HealthCheckIntervalSeconds, _options.HealthCheckMethod);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for the configured interval before each probe cycle
            await Task.Delay(
                TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds),
                stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                break;

            await ProbeAsync(stoppingToken);
        }

        _logger.LogInformation("HealthProbeService stopped");
    }

    /// <summary>
    /// Probes all catalog deployments by sending a ping through each deployment's
    /// keyed <see cref="IChatClient"/>. Reports healthy/unhealthy observations back
    /// to the catalog.
    ///
    /// IMPORTANT — Provider-key ambiguity (MVP limitation):
    /// The probe currently resolves the keyed IChatClient by <c>deployment.Provider</c>
    /// (e.g., "AzureFoundry"). This means multiple deployments that share the same
    /// provider key will be probed through the SAME IChatClient. For the MVP this is
    /// acceptable because each deployment typically maps to a different provider, but
    /// it should be refactored to register and resolve per-deployment keyed clients
    /// using <c>deployment.Name</c> when multi-deployment-per-provider is needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (also used for per-probe timeout).</param>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var deployments = _catalog.GetAllDeployments();

        _logger.LogDebug("Probing {DeploymentCount} deployment(s)", deployments.Count);

        foreach (var dep in deployments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProbeDeploymentAsync(dep, cancellationToken);
        }
    }

    private async Task ProbeDeploymentAsync(
        ProviderDeployment deployment,
        CancellationToken cancellationToken)
    {
        IChatClient? client = null;

        try
        {
            var keyedProvider = _serviceProvider as IKeyedServiceProvider;
            client = keyedProvider?.GetKeyedService(typeof(IChatClient), deployment.Provider) as IChatClient;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve keyed IChatClient for provider {Provider} (deployment {Deployment})",
                deployment.Provider, deployment.Name);
        }

        if (client is null)
        {
            _logger.LogDebug(
                "No keyed IChatClient registered for provider {Provider}; skipping deployment {Deployment}",
                deployment.Provider, deployment.Name);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1 },
                timeoutCts.Token);

            _catalog.ReportHealth(deployment.Name, healthy: true);
            CatalogMetrics.HealthProbesSucceeded.Add(1,
                CatalogMetrics.TagsFor(deployment.Name, deployment.Provider, deployment.ModelName).AsSpan());

            _logger.LogDebug("Health probe succeeded for deployment {Deployment}", deployment.Name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout (not shutdown) — mark unhealthy
            _catalog.ReportHealth(deployment.Name, healthy: false);
            CatalogMetrics.HealthProbesTimedOut.Add(1,
                CatalogMetrics.TagsFor(deployment.Name, deployment.Provider, deployment.ModelName).AsSpan());
            _logger.LogWarning("Health probe timed out for deployment {Deployment}", deployment.Name);
        }
        catch (Exception ex)
        {
            _catalog.ReportHealth(deployment.Name, healthy: false);
            CatalogMetrics.HealthProbesFailed.Add(1,
                CatalogMetrics.TagsFor(deployment.Name, deployment.Provider, deployment.ModelName).AsSpan());
            _logger.LogWarning(ex, "Health probe failed for deployment {Deployment}", deployment.Name);
        }
    }
}
