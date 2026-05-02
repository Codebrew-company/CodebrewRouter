using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace Blaze.LlmGateway.Api;

public sealed class ModelAvailabilityHeartbeatService(
    IServiceProvider serviceProvider,
    IOptions<LlmGatewayOptions> options,
    LmStudioModelDiscovery lmStudioModelDiscovery,
    ModelAvailabilityRegistry registry,
    ILogger<ModelAvailabilityHeartbeatService> logger) : IHostedService, IDisposable
{
    private readonly LlmGatewayOptions _options = options.Value;
    private CancellationTokenSource? _loopCts;
    private PeriodicTimer? _timer;
    private Task? _backgroundLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Availability.Enabled)
        {
            logger.LogInformation("Model availability heartbeat disabled; treating configured models as available.");
            await RefreshSnapshotAsync(cancellationToken, probeProviders: false);
            return;
        }

        logger.LogInformation("Starting model availability heartbeat.");
        // Seed configured models initially (disabled state) for startup visibility.
        // RunLoopAsync fires the first real probe immediately before initial timer tick,
        // which will update these seeds to enabled/disabled based on actual health.
        await RefreshSnapshotAsync(cancellationToken, probeProviders: false);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.Availability.RefreshIntervalSeconds)));
        _backgroundLoop = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is null || _backgroundLoop is null)
        {
            return;
        }

        _loopCts.Cancel();

        try
        {
            await _backgroundLoop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loopCts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (_timer is null)
        {
            return;
        }

        try
        {
            // Initial live probe runs immediately so real provider status is available soon after startup.
            await RefreshSnapshotAsync(cancellationToken, probeProviders: true);

            while (await _timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshSnapshotAsync(cancellationToken, probeProviders: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken, bool probeProviders)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var models = new List<AvailableModel>();
        var providers = new List<ProviderAvailabilitySnapshot>();

        if (!probeProviders)
        {
            SeedConfiguredModels(models, providers, checkedAt);
            registry.UpdateSnapshot(models, providers);
            return;
        }

        // Seed configured models first for fallback/visibility
        SeedConfiguredModels(models, providers, checkedAt);

        // Probe local models only (local-BYOK approach)
        await ProbeLmStudioAsync(models, providers, checkedAt, cancellationToken);
        await ProbeOllamaWithFailoverAsync(
            modelId: _options.Providers.OllamaLocal.Model,
            ownedBy: "ollama",
            isConfigured: IsOllamaLocalConfigured(_options.Providers.OllamaLocal),
            checkedAt,
            models,
            providers,
            cancellationToken);

        AddCodebrewRouterModel(models, providers, checkedAt);
        registry.UpdateSnapshot(models, providers);

        var enabledModels = models.Count(model => model.Enabled);
        var disabledModels = models.Count - enabledModels;
        logger.LogInformation(
            "Model availability heartbeat refreshed: {EnabledCount} enabled, {DisabledCount} disabled",
            enabledModels,
            disabledModels);
    }

    private void SeedConfiguredModels(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt)
    {
        // Seed local-only models (BYOK approach — no cloud providers)
        AddConfiguredModel(
            models,
            providers,
            "OllamaLocal",
            _options.Providers.OllamaLocal.Model,
            "ollama",
            _options.Providers.OllamaLocal.BaseUrl,
            IsOllamaLocalConfigured(_options.Providers.OllamaLocal),
            checkedAt);
        AddConfiguredModel(
            models,
            providers,
            "LmStudio",
            _options.Providers.LmStudio.Model,
            "lmstudio",
            _options.Providers.LmStudio.Endpoint,
            IsLmStudioConfigured(_options.Providers.LmStudio),
            checkedAt);

        AddCodebrewRouterModel(models, providers, checkedAt);
    }

    private async Task ProbeLmStudioAsync(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var lmStudioOptions = _options.Providers.LmStudio;
        if (!IsLmStudioConfigured(lmStudioOptions))
        {
            return;
        }

        try
        {
            var discoveredModels = new List<AvailableModel>();

            if (!string.IsNullOrWhiteSpace(lmStudioOptions.Endpoint))
            {
                using var timeoutCts = CreateTimeoutToken(cancellationToken);
                var discoveryResult = await lmStudioModelDiscovery.TryDiscoverModelsAsync(
                    lmStudioOptions.Endpoint,
                    lmStudioOptions.ApiKey,
                    timeoutCts.Token);

                if (discoveryResult.Success && discoveryResult.Models.Count > 0)
                {
                    discoveredModels = discoveryResult.Models.ToList();
                    logger.LogDebug("Discovered {Count} models from LM Studio", discoveredModels.Count);
                }
            }

            // Probe chat with configured model to validate provider health
            using var chatTimeoutCts = CreateTimeoutToken(cancellationToken);
            using var scope = serviceProvider.CreateScope();
            var client = scope.ServiceProvider.GetRequiredKeyedService<IChatClient>("LmStudio");
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                chatTimeoutCts.Token);

            // Chat probe succeeded — mark provider and models as enabled
            providers.Add(new ProviderAvailabilitySnapshot("LmStudio", true, null, checkedAt));

            if (discoveredModels.Count > 0)
            {
                // Add discovered models as enabled
                foreach (var discoveredModel in discoveredModels)
                {
                    models.Add(discoveredModel with
                    {
                        Enabled = true,
                        ErrorMessage = null,
                        LastCheckedUtc = checkedAt
                    });
                }

                // If configured model is not in discovered list, add it too for backward compat
                if (!discoveredModels.Any(m => string.Equals(m.Id, lmStudioOptions.Model, StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(lmStudioOptions.Model))
                {
                    models.Add(new AvailableModel(
                        lmStudioOptions.Model,
                        "LmStudio",
                        "lmstudio",
                        "configured",
                        lmStudioOptions.Endpoint,
                        Enabled: true,
                        LastCheckedUtc: checkedAt));
                }
            }
            else
            {
                // No discovered models — fall back to configured model
                models.Add(new AvailableModel(
                    lmStudioOptions.Model,
                    "LmStudio",
                    "lmstudio",
                    "configured",
                    lmStudioOptions.Endpoint,
                    Enabled: true,
                    LastCheckedUtc: checkedAt));
            }

            logger.LogDebug(
                "Availability probe succeeded for LmStudio with model {Model}. Response length: {Length}",
                lmStudioOptions.Model,
                response.Text?.Length ?? 0);
        }
        catch (Exception ex)
        {
            var error = GetErrorMessage(ex);
            if (IsOptionalLocalProvider("LmStudio"))
            {
                logger.LogInformation(
                    "Availability probe failed for optional local provider LmStudio: {Error}",
                    error);
            }
            else
            {
                logger.LogWarning(ex, "LmStudio availability probe failed: {Error}", error);
            }

            providers.Add(new ProviderAvailabilitySnapshot("LmStudio", false, error, checkedAt));
            models.Add(new AvailableModel(
                lmStudioOptions.Model,
                "LmStudio",
                "lmstudio",
                "configured",
                lmStudioOptions.Endpoint,
                Enabled: false,
                ErrorMessage: error,
                LastCheckedUtc: checkedAt));
        }
    }

    private async Task ProbeOllamaWithFailoverAsync(
        string modelId,
        string ownedBy,
        bool isConfigured,
        DateTimeOffset checkedAt,
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        CancellationToken cancellationToken)
    {
        if (!isConfigured || string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        const string primaryEndpoint = "http://192.168.16.53:11434";
        const string fallbackEndpoint = "http://192.168.16.12:11434";
        const string providerKey = "OllamaLocal";

        // Try primary endpoint first
        logger.LogInformation("Probing primary Ollama @ {PrimaryEndpoint}", primaryEndpoint);
        var (primaryHealthy, primaryError) = await TryProbeOllamaEndpointAsync(
            providerKey,
            primaryEndpoint,
            cancellationToken);

        if (primaryHealthy)
        {
            logger.LogInformation("Primary Ollama {PrimaryEndpoint} is healthy", primaryEndpoint);
            providers.Add(new ProviderAvailabilitySnapshot(providerKey, true, null, checkedAt));
            models.Add(new AvailableModel(
                modelId,
                providerKey,
                ownedBy,
                "configured",
                primaryEndpoint,
                Enabled: true,
                LastCheckedUtc: checkedAt));
            return;
        }

        // Primary failed, try fallback
        logger.LogWarning(
            "Primary Ollama unavailable ({PrimaryEndpoint}): {PrimaryError}. Trying fallback @ {FallbackEndpoint}",
            primaryEndpoint,
            primaryError,
            fallbackEndpoint);

        var (fallbackHealthy, fallbackError) = await TryProbeOllamaEndpointAsync(
            providerKey,
            fallbackEndpoint,
            cancellationToken);

        if (fallbackHealthy)
        {
            logger.LogInformation("Fallback Ollama {FallbackEndpoint} is healthy", fallbackEndpoint);
            providers.Add(new ProviderAvailabilitySnapshot(providerKey, true, null, checkedAt));
            models.Add(new AvailableModel(
                modelId,
                providerKey,
                ownedBy,
                "configured",
                fallbackEndpoint,
                Enabled: true,
                LastCheckedUtc: checkedAt));
            return;
        }

        // Both failed
        var bothFailedError = $"Primary ({primaryError}); Fallback ({fallbackError})";
        logger.LogWarning(
            "Both Ollama instances unavailable. Primary ({PrimaryEndpoint}): {PrimaryError}. Fallback ({FallbackEndpoint}): {FallbackError}",
            primaryEndpoint,
            primaryError,
            fallbackEndpoint,
            fallbackError);

        providers.Add(new ProviderAvailabilitySnapshot(providerKey, false, bothFailedError, checkedAt));
        models.Add(new AvailableModel(
            modelId,
            providerKey,
            ownedBy,
            "configured",
            fallbackEndpoint, // Use fallback as the last-known endpoint
            Enabled: false,
            ErrorMessage: bothFailedError,
            LastCheckedUtc: checkedAt));
    }

    private async Task<(bool Healthy, string Error)> TryProbeOllamaEndpointAsync(
        string providerKey,
        string ollamaEndpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CreateTimeoutToken(cancellationToken);
            
            // Create a temporary OllamaApiClient for the specified endpoint and probe with the configured model.
            // OllamaApiClient(Uri endpoint, string model) constructor creates a client targeting that endpoint.
            var ollamaClient = (IChatClient)new OllamaApiClient(new Uri(ollamaEndpoint), _options.Providers.OllamaLocal.Model);

            var response = await ollamaClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                timeoutCts.Token);

            logger.LogDebug(
                "Ollama probe succeeded for {Endpoint} with model {Model}. Response length: {Length}",
                ollamaEndpoint,
                _options.Providers.OllamaLocal.Model,
                response.Text?.Length ?? 0);

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            var error = GetErrorMessage(ex);
            logger.LogDebug("Ollama probe failed for {Endpoint}: {Error}", ollamaEndpoint, error);
            return (false, error);
        }
    }

    private void AddCodebrewRouterModel(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt)
    {
        var codebrewOptions = _options.CodebrewRouter;
        if (!codebrewOptions.Enabled || string.IsNullOrWhiteSpace(codebrewOptions.ModelId))
        {
            return;
        }

        var availableProviders = models
            .Where(model => model.Enabled)
            .Select(model => model.Provider)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasBackingProvider = codebrewOptions.FallbackRules.Values
            .SelectMany(providers => providers)
            .Any(provider => availableProviders.Contains(provider));

        models.Add(new AvailableModel(
            codebrewOptions.ModelId,
            "CodebrewRouter",
            "codebrew",
            "virtual",
            Enabled: hasBackingProvider,
            ErrorMessage: hasBackingProvider ? null : "No backing provider is currently available.",
            LastCheckedUtc: checkedAt));
        providers.Add(new ProviderAvailabilitySnapshot(
            "CodebrewRouter",
            hasBackingProvider,
            hasBackingProvider ? null : "No backing provider is currently available.",
            checkedAt));
    }

    private void AddConfiguredModel(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        string providerKey,
        string modelId,
        string ownedBy,
        string? endpoint,
        bool isConfigured,
        DateTimeOffset checkedAt)
    {
        if (!isConfigured || string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        providers.Add(new ProviderAvailabilitySnapshot(providerKey, true, null, checkedAt));
        models.Add(new AvailableModel(
            modelId,
            providerKey,
            ownedBy,
            "configured",
            endpoint,
            Enabled: true,
            LastCheckedUtc: checkedAt));
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.Availability.StartupProbeTimeoutSeconds)));
        return timeoutCts;
    }

    private static string GetErrorMessage(Exception exception)
        => exception.GetBaseException().Message;

    private static bool IsOllamaLocalConfigured(OllamaLocalOptions options)
        => !string.IsNullOrWhiteSpace(options.BaseUrl) &&
           !string.IsNullOrWhiteSpace(options.Model);

    private static bool IsLmStudioConfigured(LmStudioOptions options)
        => !string.IsNullOrWhiteSpace(options.Endpoint) &&
           !string.IsNullOrWhiteSpace(options.Model);

    private static bool IsOptionalLocalProvider(string providerKey)
        => string.Equals(providerKey, "FoundryLocal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerKey, "OllamaLocal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerKey, "LmStudio", StringComparison.OrdinalIgnoreCase);
}
