using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

public sealed class ModelAvailabilityHeartbeatService(
    IServiceProvider serviceProvider,
    IOptions<LlmGatewayOptions> options,
    AzureFoundryModelDiscovery azureFoundryModelDiscovery,
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

        // Then probe to update with discovered/validated state
        await ProbeAzureFoundryAsync(models, providers, checkedAt, cancellationToken);
        await ProbeLmStudioAsync(models, providers, checkedAt, cancellationToken);
        await ProbeChatProviderAsync(
            providerKey: "FoundryLocal",
            modelId: _options.Providers.FoundryLocal.Model,
            endpoint: _options.Providers.FoundryLocal.Endpoint,
            ownedBy: "openai",
            isConfigured: IsFoundryLocalConfigured(_options.Providers.FoundryLocal),
            checkedAt,
            models,
            providers,
            cancellationToken);
        await ProbeChatProviderAsync(
            providerKey: "GithubModels",
            modelId: _options.Providers.GithubModels.Model,
            endpoint: _options.Providers.GithubModels.Endpoint,
            ownedBy: "github",
            isConfigured: IsGithubModelsConfigured(_options.Providers.GithubModels),
            checkedAt,
            models,
            providers,
            cancellationToken);
        await ProbeChatProviderAsync(
            providerKey: "OllamaLocal",
            modelId: _options.Providers.OllamaLocal.Model,
            endpoint: _options.Providers.OllamaLocal.BaseUrl,
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
        AddConfiguredModel(
            models,
            providers,
            "AzureFoundry",
            _options.Providers.AzureFoundry.Model,
            "openai",
            _options.Providers.AzureFoundry.Endpoint,
            IsAzureFoundryConfigured(_options.Providers.AzureFoundry),
            checkedAt);
        AddConfiguredModel(
            models,
            providers,
            "FoundryLocal",
            _options.Providers.FoundryLocal.Model,
            "openai",
            _options.Providers.FoundryLocal.Endpoint,
            IsFoundryLocalConfigured(_options.Providers.FoundryLocal),
            checkedAt);
        AddConfiguredModel(
            models,
            providers,
            "GithubModels",
            _options.Providers.GithubModels.Model,
            "github",
            _options.Providers.GithubModels.Endpoint,
            IsGithubModelsConfigured(_options.Providers.GithubModels),
            checkedAt);
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

    private async Task ProbeAzureFoundryAsync(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var azureOptions = _options.Providers.AzureFoundry;
        if (!IsAzureFoundryConfigured(azureOptions))
        {
            return;
        }

        try
        {
            var discoveredModels = new List<AvailableModel>();
            
            if (!string.IsNullOrWhiteSpace(azureOptions.Endpoint))
            {
                using var timeoutCts = CreateTimeoutToken(cancellationToken);
                var discoveryResult = await azureFoundryModelDiscovery.TryDiscoverModelsAsync(
                    azureOptions.Endpoint,
                    azureOptions.ApiKey,
                    timeoutCts.Token);

                if (discoveryResult.Success && discoveryResult.Models.Count > 0)
                {
                    discoveredModels = discoveryResult.Models.ToList();
                    logger.LogDebug("Discovered {Count} models from Azure Foundry", discoveredModels.Count);
                }
            }

            // Probe chat with configured model to validate provider health
            using var chatTimeoutCts = CreateTimeoutToken(cancellationToken);
            using var scope = serviceProvider.CreateScope();
            var client = scope.ServiceProvider.GetRequiredKeyedService<IChatClient>("AzureFoundry");
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                chatTimeoutCts.Token);

            // Chat probe succeeded — mark provider and models as enabled
            providers.Add(new ProviderAvailabilitySnapshot("AzureFoundry", true, null, checkedAt));
            
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
                if (!discoveredModels.Any(m => string.Equals(m.Id, azureOptions.Model, StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(azureOptions.Model))
                {
                    models.Add(new AvailableModel(
                        azureOptions.Model,
                        "AzureFoundry",
                        "openai",
                        "configured",
                        azureOptions.Endpoint,
                        Enabled: true,
                        LastCheckedUtc: checkedAt));
                }
            }
            else
            {
                // No discovered models — fall back to configured model
                models.Add(new AvailableModel(
                    azureOptions.Model,
                    "AzureFoundry",
                    "openai",
                    "configured",
                    azureOptions.Endpoint,
                    Enabled: true,
                    LastCheckedUtc: checkedAt));
            }

            logger.LogDebug(
                "Availability probe succeeded for AzureFoundry with model {Model}. Response length: {Length}",
                azureOptions.Model,
                response.Text?.Length ?? 0);
        }
        catch (Exception ex)
        {
            var error = GetErrorMessage(ex);
            logger.LogWarning(ex, "AzureFoundry availability probe failed: {Error}", error);
            providers.Add(new ProviderAvailabilitySnapshot("AzureFoundry", false, error, checkedAt));
            models.Add(new AvailableModel(
                azureOptions.Model,
                "AzureFoundry",
                "openai",
                "configured",
                azureOptions.Endpoint,
                Enabled: false,
                ErrorMessage: error,
                LastCheckedUtc: checkedAt));
        }
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

    private async Task ProbeChatProviderAsync(
        string providerKey,
        string modelId,
        string? endpoint,
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

        try
        {
            using var timeoutCts = CreateTimeoutToken(cancellationToken);
            using var scope = serviceProvider.CreateScope();
            var client = scope.ServiceProvider.GetRequiredKeyedService<IChatClient>(providerKey);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                timeoutCts.Token);

            providers.Add(new ProviderAvailabilitySnapshot(providerKey, true, null, checkedAt));
            models.Add(new AvailableModel(
                modelId,
                providerKey,
                ownedBy,
                "configured",
                endpoint,
                Enabled: true,
                LastCheckedUtc: checkedAt));

            logger.LogDebug(
                "Availability probe succeeded for {Provider} with model {Model}. Response length: {Length}",
                providerKey,
                modelId,
                response.Text?.Length ?? 0);
        }
        catch (Exception ex)
        {
            var error = GetErrorMessage(ex);
            if (IsOptionalLocalProvider(providerKey))
            {
                logger.LogInformation(
                    "Availability probe failed for optional local provider {Provider}: {Error}",
                    providerKey,
                    error);
            }
            else
            {
                logger.LogWarning(ex, "Availability probe failed for {Provider}: {Error}", providerKey, error);
            }

            providers.Add(new ProviderAvailabilitySnapshot(providerKey, false, error, checkedAt));
            models.Add(new AvailableModel(
                modelId,
                providerKey,
                ownedBy,
                "configured",
                endpoint,
                Enabled: false,
                ErrorMessage: error,
                LastCheckedUtc: checkedAt));
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

    private static bool IsAzureFoundryConfigured(AzureFoundryOptions options)
        => !string.IsNullOrWhiteSpace(options.Model) &&
           !string.IsNullOrWhiteSpace(options.ApiKey) &&
           (!string.IsNullOrWhiteSpace(options.ResponsesEndpoint) ||
            !string.IsNullOrWhiteSpace(options.Endpoint));

    private static bool IsFoundryLocalConfigured(FoundryLocalOptions options)
        => options.Enabled &&
           !string.IsNullOrWhiteSpace(options.Endpoint) &&
           !string.IsNullOrWhiteSpace(options.Model);

    private static bool IsGithubModelsConfigured(GithubModelsOptions options)
        => !string.IsNullOrWhiteSpace(options.Endpoint) &&
           !string.IsNullOrWhiteSpace(options.Model) &&
           !string.IsNullOrWhiteSpace(options.ApiKey);

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
