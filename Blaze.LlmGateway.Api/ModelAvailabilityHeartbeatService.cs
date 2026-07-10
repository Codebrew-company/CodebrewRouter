using Blaze.LlmGateway.Core;
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
    IOptions<LlmGatewayOptions> options,
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

        logger.LogDebug("🔄 Refreshing model availability snapshot (probeProviders: {ProbeProviders})", probeProviders);

        if (!probeProviders)
        {
            logger.LogDebug("  ├─ Seeding configured models (no probe)");
            SeedConfiguredModels(models, providers, checkedAt);
            registry.UpdateSnapshot(models, providers);
            return;
        }

        // Seed configured models first for fallback/visibility
        logger.LogDebug("  ├─ Seeding configured models");
        SeedConfiguredModels(models, providers, checkedAt);
        if (_options.OfflineOnly)
        {
            logger.LogInformation("Offline-only mode active; skipping external provider probes.");
            registry.UpdateSnapshot(models, providers);
            return;
        }

        // Probe local models only (local-BYOK approach)
        logger.LogDebug("  ├─ Probing Ollama Router with failover");
        await ProbeOllamaWithFailoverAsync(
            modelId: _options.Providers.OllamaRouter.Model,
            ownedBy: "ollama",
            isConfigured: !string.IsNullOrWhiteSpace(_options.Providers.OllamaRouter.Model),
            checkedAt,
            models,
            providers,
            cancellationToken);

        logger.LogDebug("  ├─ Adding configured virtual models");
        AddVirtualModels(models, providers, checkedAt);

        // Probe OpenCode Go cloud endpoint
        logger.LogDebug("  ├─ Probing OpenCode Go");
        await ProbeOpenCodeGoAsync(models, providers, checkedAt, cancellationToken);
        
        registry.UpdateSnapshot(models, providers);

        var enabledModels = models.Count(model => model.Enabled);
        var disabledModels = models.Count - enabledModels;
        logger.LogInformation(
            "✅ Model availability snapshot refreshed: {EnabledCount} enabled, {DisabledCount} disabled, Total: {TotalCount}",
            enabledModels,
            disabledModels,
            models.Count);
        
        foreach (var model in models)
        {
            logger.LogDebug("  ├─ Model '{ModelId}' ({Provider}): {Status}", 
                model.Id, model.OwnedBy, model.Enabled ? "✅ enabled" : "❌ disabled");
        }
    }

    private void SeedConfiguredModels(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt)
    {
        // Seed local-only models (BYOK approach — no cloud providers)
        AddLocalGemmaModel(models, providers, checkedAt);

        if (_options.OfflineOnly)
        {
            AddVirtualModels(models, providers, checkedAt);
            return;
        }

        AddConfiguredModel(
            models,
            providers,
            "OllamaRouter",
            _options.Providers.OllamaRouter.Model,
            "ollama",
            _options.Providers.OllamaRouter.PrimaryEndpoint,
            !string.IsNullOrWhiteSpace(_options.Providers.OllamaRouter.Model),
            checkedAt);

        AddVirtualModels(models, providers, checkedAt);

        // Seed all 14 OpenCodeGo models (enabled initially since API key is present;
        // the live probe will flip them to disabled if the endpoint is unreachable)
        if (!string.IsNullOrWhiteSpace(_options.Providers.OpenCodeGo.ApiKey))
        {
            foreach (var (dest, modelName) in OpenCodeGoModels.ModelNames)
            {
                providers.Add(new ProviderAvailabilitySnapshot(dest.ToString(), true, null, checkedAt));
                models.Add(new AvailableModel(
                    modelName,
                    dest.ToString(),
                    "opencode-go",
                    "cloud",
                    _options.Providers.OpenCodeGo.BaseUrl,
                    Enabled: true,
                    LastCheckedUtc: checkedAt));
            }
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
            var ollamaClient = (IChatClient)new OllamaApiClient(new Uri(ollamaEndpoint), _options.Providers.OllamaRouter.Model);

            var response = await ollamaClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1, Temperature = 0f },
                timeoutCts.Token);

            logger.LogDebug(
                "Ollama probe succeeded for {Endpoint} with model {Model}. Response length: {Length}",
                ollamaEndpoint,
                _options.Providers.OllamaRouter.Model,
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

    private void AddVirtualModels(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt)
    {
        var virtualModels = _options.GetEffectiveVirtualModels();
        if (virtualModels.Count == 0)
        {
            return;
        }

        var availableProviders = models
            .Where(model => model.Enabled)
            .Select(model => model.Provider)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var anyVirtualModelAvailable = false;
        var providerErrors = new List<string>();

        foreach (var virtualModel in virtualModels)
        {
            var fallbackProviders = virtualModel.FallbackRules.Values
                .SelectMany(providers => providers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var hasBackingProvider = fallbackProviders.Any(provider => availableProviders.Contains(provider));
            anyVirtualModelAvailable |= hasBackingProvider;

            var unavailableProviderReasons = fallbackProviders
                .Where(provider => !availableProviders.Contains(provider))
                .Select(provider => FormatUnavailableProviderReason(provider, providers))
                .ToArray();
            var unavailableReason = hasBackingProvider
                ? null
                : BuildNoBackingProviderReason(unavailableProviderReasons);

            if (unavailableReason is not null)
            {
                providerErrors.Add($"{virtualModel.ModelId}: {unavailableReason}");
            }

            models.Add(new AvailableModel(
                virtualModel.ModelId,
                virtualModel.Provider,
                virtualModel.OwnedBy,
                virtualModel.Source,
                Enabled: hasBackingProvider,
                ErrorMessage: unavailableReason,
                LastCheckedUtc: checkedAt));
        }

        providers.Add(new ProviderAvailabilitySnapshot(
            "CodebrewRouter",
            anyVirtualModelAvailable,
            providerErrors.Count == 0 ? null : string.Join("; ", providerErrors),
            checkedAt));
    }

    private void AddLocalGemmaModel(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt)
    {
        var unavailableReason = GetLocalGemmaUnavailableReason(_options.LocalInference);
        if (unavailableReason is not null)
        {
            providers.Add(new ProviderAvailabilitySnapshot("LocalGemma", false, unavailableReason, checkedAt));
            models.Add(new AvailableModel(
                "local-gemma",
                "LocalGemma",
                "lmkit",
                "configured",
                _options.LocalInference.ModelPath,
                Enabled: false,
                ErrorMessage: unavailableReason,
                LastCheckedUtc: checkedAt));
            return;
        }

        AddConfiguredModel(
            models,
            providers,
            "LocalGemma",
            "local-gemma",
            "lmkit",
            _options.LocalInference.ModelPath,
            isConfigured: true,
            checkedAt);
    }

    private static string? GetLocalGemmaUnavailableReason(LocalInferenceOptions options)
    {
        if (!options.Enabled)
        {
            return "LocalGemma is not loaded because local inference is disabled.";
        }

        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return "LocalGemma is not loaded because LlmGateway:LocalInference:ModelPath is not configured. Set it to a local Gemma GGUF file or a Hugging Face GGUF URL.";
        }

        if (Uri.TryCreate(options.ModelPath, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (!File.Exists(options.ModelPath))
        {
            return $"LocalGemma is not loaded because configured LlmGateway:LocalInference:ModelPath '{options.ModelPath}' does not exist.";
        }

        return null;
    }

    private static string FormatUnavailableProviderReason(
        string provider,
        IEnumerable<ProviderAvailabilitySnapshot> providerSnapshots)
    {
        var snapshot = providerSnapshots.LastOrDefault(candidate =>
            string.Equals(candidate.Provider, provider, StringComparison.OrdinalIgnoreCase));

        return snapshot is null
            ? $"{provider}: provider is not configured."
            : $"{provider}: {snapshot.ErrorMessage ?? "provider is unavailable."}";
    }

    private static string BuildNoBackingProviderReason(IReadOnlyCollection<string> providerReasons)
        => providerReasons.Count == 0
            ? "No backing provider is currently available."
            : $"No backing provider is currently available. {string.Join("; ", providerReasons)}";

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

    private async Task ProbeOpenCodeGoAsync(
        ICollection<AvailableModel> models,
        ICollection<ProviderAvailabilitySnapshot> providers,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var opts = _options.Providers.OpenCodeGo;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            logger.LogDebug("  ├─ OpenCode Go not configured (no API key); skipping probe");
            return;
        }

        logger.LogInformation("🔍 Probing OpenCode Go at {BaseUrl}", opts.BaseUrl);
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
            using var timeoutCts = CreateTimeoutToken(cancellationToken);
            var response = await http.GetAsync(
                $"{opts.BaseUrl.TrimEnd('/')}/models", timeoutCts.Token);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("✅ OpenCode Go probe succeeded");
                foreach (var (dest, modelName) in OpenCodeGoModels.ModelNames)
                {
                    providers.Add(new ProviderAvailabilitySnapshot(dest.ToString(), true, null, checkedAt));
                    models.Add(new AvailableModel(
                        modelName,
                        dest.ToString(),
                        "opencode-go",
                        "cloud",
                        opts.BaseUrl,
                        Enabled: true,
                        LastCheckedUtc: checkedAt));
                }
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            logger.LogWarning("⚠️ OpenCode Go probe returned {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "⚠️ OpenCode Go probe failed: {Error}", GetErrorMessage(ex));
        }
    }
}
