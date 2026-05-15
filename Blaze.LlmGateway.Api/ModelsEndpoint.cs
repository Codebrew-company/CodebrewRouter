using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Handler for GET /v1/models endpoint.
/// Returns available models and their providers.
/// </summary>
public static class ModelsEndpoint
{
    /// <summary>Handle model listing requests</summary>
    public static async Task<IResult> HandleAsync(
        IModelCatalog modelCatalog,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> options,
        CancellationToken cancellationToken)
    {
        var sourceModels = options.Value.OfflineOnly
            ? availabilityRegistry.GetModels(includeUnavailable: true)
            : await modelCatalog.GetAvailableModelsAsync(cancellationToken);

        var models = sourceModels
            .Select(model => new ModelInfo(
                Id: model.Id,
                Object: "model",
                Provider: model.Provider,
                OwnedBy: model.OwnedBy,
                Source: model.Source,
                Enabled: model.Enabled,
                ErrorMessage: model.ErrorMessage))
            .ToList();

        var response = new ModelsResponse(
            Object: "list",
            Data: models
        );

        return Results.Json(response);
    }

    /// <summary>Handle CodebrewRouter-specific model listing requests.</summary>
    public static async Task<IResult> HandleCodebrewRouterAsync(
        IModelCatalog modelCatalog,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> options,
        CancellationToken cancellationToken)
        => await HandleVirtualModelAsync(
            string.IsNullOrWhiteSpace(options.Value.CodebrewRouter.ModelId)
                ? "codebrewRouter"
                : options.Value.CodebrewRouter.ModelId,
            modelCatalog,
            availabilityRegistry,
            options,
            cancellationToken);

    /// <summary>Handle virtual model detail requests.</summary>
    public static async Task<IResult> HandleVirtualModelAsync(
        string modelId,
        IModelCatalog modelCatalog,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> options,
        CancellationToken cancellationToken)
    {
        var virtualModel = options.Value.FindVirtualModel(modelId);
        if (virtualModel is null)
        {
            return Results.NotFound(new ErrorResponse(
                new ErrorDetail(
                    $"The virtual model '{modelId}' is disabled or not configured.",
                    "not_found",
                    "model_not_found")));
        }

        var offlineOnly = options.Value.OfflineOnly;
        var providerKeys = virtualModel.FallbackRules.Values
            .SelectMany(providers => GetEffectiveFallbackProviders(providers, offlineOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var registryModels = availabilityRegistry.GetModels(includeUnavailable: true);
        var sourceModels = registryModels.Count > 0
            ? registryModels
            : await modelCatalog.GetAvailableModelsAsync(cancellationToken);
        var backingModels = sourceModels
            .Where(model => providerKeys.Contains(model.Provider))
            .Select(model => new CodebrewRouterBackingModel(
                Id: model.Id,
                Object: "model",
                Provider: model.Provider,
                OwnedBy: model.OwnedBy,
                Source: model.Source,
                Enabled: model.Enabled,
                ErrorMessage: model.ErrorMessage))
            .OrderBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var availability = availabilityRegistry.FindModel(virtualModel.ModelId, includeUnavailable: true);
        var fallbackRules = virtualModel.FallbackRules
            .OrderBy(rule => rule.Key, StringComparer.OrdinalIgnoreCase)
            .Select(rule => new CodebrewRouterFallbackRule(
                TaskType: rule.Key,
                Providers: GetEffectiveFallbackProviders(rule.Value, offlineOnly)
                    .ToArray()))
            .ToList();

        var response = new CodebrewRouterModelsResponse(
            Id: virtualModel.ModelId,
            Object: "model",
            Provider: virtualModel.Provider,
            OwnedBy: virtualModel.OwnedBy,
            Source: virtualModel.Source,
            Enabled: availability?.Enabled ?? false,
            ErrorMessage: availability?.ErrorMessage,
            BackingModels: backingModels,
            FallbackRules: fallbackRules);

        return Results.Json(response);
    }

    private static IEnumerable<string> GetEffectiveFallbackProviders(
        IEnumerable<string> providers,
        bool offlineOnly)
    {
        var effectiveProviders = providers.Where(provider => !string.IsNullOrWhiteSpace(provider));
        return offlineOnly
            ? effectiveProviders.Where(provider => string.Equals(provider, "LocalGemma", StringComparison.OrdinalIgnoreCase))
            : effectiveProviders;
    }

    /// <summary>Handle full model/provider diagnostics requests.</summary>
    public static IResult HandleDiagnosticsAsync(ModelAvailabilityRegistry registry)
    {
        var models = registry.GetModels(includeUnavailable: true)
            .Select(model => new ModelDiagnosticsInfo(
                Id: model.Id,
                Provider: model.Provider,
                OwnedBy: model.OwnedBy,
                Source: model.Source,
                Endpoint: model.Endpoint,
                Enabled: model.Enabled,
                ErrorMessage: model.ErrorMessage,
                LastCheckedUtc: model.LastCheckedUtc))
            .OrderBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var providers = registry.GetProviders()
            .Select(provider => new ProviderDiagnosticsInfo(
                Provider: provider.Provider,
                Enabled: provider.Enabled,
                ErrorMessage: provider.ErrorMessage,
                LastCheckedUtc: provider.LastCheckedUtc))
            .ToList();

        var status = providers.Count == 0 || providers.All(provider => !provider.Enabled)
            ? "unhealthy"
            : providers.Any(provider => !provider.Enabled) || models.Any(model => !model.Enabled)
                ? "degraded"
                : "healthy";

        var checkedAt = providers
            .Select(provider => provider.LastCheckedUtc)
            .Concat(models.Where(model => model.LastCheckedUtc.HasValue).Select(model => model.LastCheckedUtc!.Value))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return Results.Json(new ModelDiagnosticsResponse(status, checkedAt, models, providers));
    }
}
