using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace Blaze.LlmGateway.Infrastructure;

public sealed class ModelSelectionResolver(
    IServiceProvider serviceProvider,
    IModelCatalog modelCatalog,
    IOptions<LlmGatewayOptions> gatewayOptions,
    ITokenCounter tokenCounter,
    IContextCompactor compactor,
    IOptions<ContextSizingOptions> sizingOptions,
    ILogger<ModelSelectionResolver> logger,
    ILogger<ContextSizingChatClient> sizingLogger,
    CatalogModelRouter? catalogModelRouter = null,
    IRoutingStrategyResolver? strategyResolver = null) : IModelSelectionResolver
{
    public async Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (gatewayOptions.Value.OfflineOnly)
        {
            if (IsVirtualModel(modelId))
            {
                // Offline mode: check catalog binding first
                var vmOptions = gatewayOptions.Value.FindVirtualModel(modelId);
                if (vmOptions?.CatalogModel is not null && catalogModelRouter is not null)
                {
                    var deployment = catalogModelRouter.SelectDeployment(
                        vmOptions.CatalogModel,
                        new RoutingContext(modelId, 0, false, false, false, cancellationToken));

                    if (deployment is not null)
                    {
                        var client = serviceProvider.GetKeyedService<IChatClient>(deployment.Provider);
                        if (client is not null)
                        {
                            var catalog = serviceProvider.GetRequiredService<IProviderCatalog>();
                            client = new CatalogMetricsChatClient(
                                client, catalog, deployment.Name, strategyResolver);
                            logger.LogInformation(
                                "Offline-only mode active; resolved virtual model {ModelId} to catalog deployment {Deployment} ({Provider})",
                                modelId, deployment.Name, deployment.Provider);
                            return client;
                        }
                    }
                }

                var codebrewRouter = serviceProvider.GetKeyedService<IChatClient>("CodebrewRouter");
                if (codebrewRouter is not null)
                {
                    logger.LogInformation("Offline-only mode active; resolving virtual model {ModelId} to CodebrewRouter", modelId);
                    return codebrewRouter;
                }

                logger.LogWarning("Offline-only mode requested {ModelId}, but CodebrewRouter is not registered; falling back to LocalGemma", modelId);
            }

            logger.LogInformation("Offline-only mode active; resolving model {ModelId} to LocalGemma", modelId);
            return serviceProvider.GetKeyedService<IChatClient>("LocalGemma")
                ?? throw new InvalidOperationException("Offline-only mode is enabled, but the LocalGemma provider is not registered.");
        }

        if (IsVirtualModel(modelId))
        {
            // Phase 2: When CatalogModel is set, route through the provider catalog
            var vmOptions = gatewayOptions.Value.FindVirtualModel(modelId);
            if (vmOptions?.CatalogModel is not null && catalogModelRouter is not null)
            {
                logger.LogDebug(
                    "Virtual model {ModelId} has CatalogModel={CatalogModel}; routing through provider catalog",
                    modelId, vmOptions.CatalogModel);

                if (gatewayOptions.Value.VerboseRouteLogging)
                    RouterLog.Write(logger, new RouterSelectEvent(modelId, "catalog-delegate", $"catalog:{vmOptions.CatalogModel}"));

                var deployment = catalogModelRouter.SelectDeployment(
                    vmOptions.CatalogModel,
                    new RoutingContext(modelId, 0, false, false, false, cancellationToken));

                if (deployment is not null)
                {
                    var client = serviceProvider.GetKeyedService<IChatClient>(deployment.Provider);
                    if (client is not null)
                    {
                        var catalog = serviceProvider.GetRequiredService<IProviderCatalog>();
                        client = new CatalogMetricsChatClient(
                            client, catalog, deployment.Name, strategyResolver);
                        logger.LogDebug(
                            "Resolved virtual model {ModelId} to catalog deployment {Deployment} ({Provider})",
                            modelId, deployment.Name, deployment.Provider);

                        if (gatewayOptions.Value.VerboseRouteLogging)
                            RouterLog.Write(logger, new RouterDeployEvent(deployment.Name, $"catalog route: {vmOptions.CatalogModel}", 0));

                        return client;
                    }

                    logger.LogWarning(
                        "Catalog deployment {Deployment} selected for {ModelId}, but no IChatClient registered for provider {Provider}",
                        deployment.Name, modelId, deployment.Provider);
                    return null;
                }

                logger.LogWarning(
                    "Catalog model routing failed for virtual model {ModelId} (CatalogModel={CatalogModel}); falling back to CodebrewRouter",
                    modelId, vmOptions.CatalogModel);
            }

            var codebrewRouter = serviceProvider.GetKeyedService<IChatClient>("CodebrewRouter");
            if (codebrewRouter is not null)
            {
                logger.LogDebug("Resolving virtual model {ModelId} to CodebrewRouter", modelId);
                return codebrewRouter;
            }

            logger.LogWarning("Virtual model {ModelId} requested, but CodebrewRouter is not registered", modelId);
            return null;
        }

        var model = await modelCatalog.FindByIdAsync(modelId, cancellationToken);
        if (model is null)
        {
            logger.LogWarning("Model '{ModelId}' not found in catalog", modelId);
            return null;
        }

        if (string.Equals(model.Provider, "OllamaRouter", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.Endpoint))
        {
            var configuredOllama = gatewayOptions.Value.Providers.OllamaRouter;
            if (string.Equals(model.Id, configuredOllama.Model, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Resolving configured Ollama router keyed client for model {ModelId}", modelId);
                return serviceProvider.GetKeyedService<IChatClient>(model.Provider);
            }

            logger.LogDebug("Resolving dynamic Ollama client for model {ModelId}", modelId);

            // Resolve context window: curated table → provider config fallback
            var (curatedWindow, _) = ModelContextLimits.Lookup(modelId);
            var contextWindow  = curatedWindow ?? configuredOllama.MaxContextTokens;
            var reservedOutput = configuredOllama.ReservedOutputTokens;

            return ((IChatClient)new OllamaApiClient(new Uri(model.Endpoint), model.Id))
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    contextWindow, reservedOutput, modelId, sizingLogger)
                .Build();
        }

        logger.LogDebug("Resolving keyed client {Provider} for model {ModelId}", model.Provider, modelId);
        return serviceProvider.GetKeyedService<IChatClient>(model.Provider);
    }

    private bool IsVirtualModel(string modelId)
        => gatewayOptions.Value.FindVirtualModel(modelId) is not null;
}
