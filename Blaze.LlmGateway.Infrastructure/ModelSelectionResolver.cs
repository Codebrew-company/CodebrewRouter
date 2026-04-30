using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.ModelCatalog;
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
    ILogger<ContextSizingChatClient> sizingLogger) : IModelSelectionResolver
{
    public async Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = await modelCatalog.FindByIdAsync(modelId, cancellationToken);
        if (model is null)
        {
            return null;
        }

        if (string.Equals(model.Provider, "OllamaLocal", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.Endpoint))
        {
            var configuredOllama = gatewayOptions.Value.Providers.OllamaLocal;
            if (string.Equals(model.Id, configuredOllama.Model, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Resolving configured Ollama keyed client for model {ModelId}", modelId);
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
}
