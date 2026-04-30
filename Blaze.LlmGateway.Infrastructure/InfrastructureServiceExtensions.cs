using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using System.ClientModel;

namespace Blaze.LlmGateway.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddLlmProviders(this IServiceCollection services)
    {
        // AzureFoundry — DefaultAzureCredential or ApiKey depending on config
        services.AddKeyedSingleton<IChatClient>("AzureFoundry", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.AzureFoundry;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();

            if (!string.IsNullOrWhiteSpace(opts.ResponsesEndpoint))
            {
                return new FoundryResponsesChatClient(
                        opts,
                        sp.GetRequiredService<ILogger<FoundryResponsesChatClient>>())
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .UseContextSizing(tokenCounter, compactor, sizingOptions,
                        opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                    .Build();
            }

            AzureOpenAIClient azureClient = string.IsNullOrWhiteSpace(opts.ApiKey)
                ? new AzureOpenAIClient(new Uri(opts.Endpoint), new DefaultAzureCredential())
                : new AzureOpenAIClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
            return azureClient.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });

        // FoundryLocal — Azure Foundry Local exposes an OpenAI-compatible endpoint at /v1.
        // Use OpenAIClient (NOT AzureOpenAIClient) so request paths stay OpenAI-shaped.
        services.AddKeyedSingleton<IChatClient>("FoundryLocal", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.FoundryLocal;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            var apiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? "notneeded" : opts.ApiKey;
            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(opts.Endpoint) });
            return client.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });

        // OllamaLocal — local Ollama container (backup for remote server)
        services.AddKeyedSingleton<IChatClient>("OllamaLocal", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OllamaLocal;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            return ((IChatClient)new OllamaApiClient(new Uri(opts.BaseUrl), opts.Model))
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });

        // GithubModels — GitHub Models API (OpenAI-compatible endpoint)
        services.AddKeyedSingleton<IChatClient>("GithubModels", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.GithubModels;
            if (string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                throw new InvalidOperationException("GithubModels requires API key in LlmGateway:Providers:GithubModels:ApiKey");
            }
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            // GitHub Models uses OpenAI-compatible API; use AzureOpenAIClient with custom endpoint
            var client = new AzureOpenAIClient(
                new Uri(opts.Endpoint),
                new AzureKeyCredential(opts.ApiKey));
            return client.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });

        // LmStudio — local OpenAI-compatible endpoint exposed by LM Studio at /v1.
        services.AddKeyedSingleton<IChatClient>("LmStudio", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.LmStudio;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            var apiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? "notneeded" : opts.ApiKey;
            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(opts.Endpoint) });
            return client.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });

        return services;
    }

    public static IServiceCollection AddLlmInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IModelSelectionResolver>(sp => new ModelSelectionResolver(
            sp,
            sp.GetRequiredService<IModelCatalog>(),
            sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
            sp.GetRequiredService<TokenCounting.ITokenCounter>(),
            sp.GetRequiredService<IContextCompactor>(),
            sp.GetRequiredService<IOptions<ContextSizingOptions>>(),
            sp.GetRequiredService<ILogger<ModelSelectionResolver>>(),
            sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>()));
        services.AddSingleton<KeywordRoutingStrategy>();
        services.AddSingleton<IRoutingStrategy>(sp =>
        {
            // Try to use OllamaLocal for meta-routing if available, otherwise use keyword-only routing
            var keywordFallback = sp.GetRequiredService<KeywordRoutingStrategy>();
            var logger = sp.GetRequiredService<ILogger<OllamaMetaRoutingStrategy>>();
            
            var routerClient = sp.GetKeyedService<IChatClient>("OllamaLocal");
            if (routerClient is not null)
            {
                return new OllamaMetaRoutingStrategy(routerClient, keywordFallback, logger);
            }
            
            // Ollama not available — use keyword routing directly
            logger.LogInformation("OllamaLocal not available; using keyword-only routing strategy");
            return keywordFallback;
        });

        // Register failover strategy with configuration
        services.AddSingleton<IFailoverStrategy>(sp =>
        {
            var strategy = new ConfiguredFailoverStrategy(
                sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
                sp.GetRequiredService<ILogger<ConfiguredFailoverStrategy>>());
            strategy.Initialize();
            return strategy;
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var providerOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers;
            var availabilityRegistry = sp.GetRequiredService<IModelAvailabilityRegistry>();
            var fallback =
                GetConfiguredKeyedClient(sp, "GithubModels", IsGithubModelsConfigured(providerOptions.GithubModels) && availabilityRegistry.IsProviderAvailable("GithubModels"))
                ?? GetConfiguredKeyedClient(sp, "AzureFoundry", IsAzureFoundryConfigured(providerOptions.AzureFoundry) && availabilityRegistry.IsProviderAvailable("AzureFoundry"))
                ?? GetConfiguredKeyedClient(sp, "FoundryLocal", IsFoundryLocalConfigured(providerOptions.FoundryLocal) && availabilityRegistry.IsProviderAvailable("FoundryLocal"))
                ?? GetConfiguredKeyedClient(sp, "LmStudio", IsLmStudioConfigured(providerOptions.LmStudio) && availabilityRegistry.IsProviderAvailable("LmStudio"))
                ?? (IChatClient)new UnavailableChatClient("No currently available LLM provider is available for the default chat client.");
             
            var strategy = sp.GetRequiredService<IRoutingStrategy>();
            var failoverStrategy = sp.GetRequiredService<IFailoverStrategy>();
            var routerLogger = sp.GetRequiredService<ILogger<LlmRoutingChatClient>>();

            // MCP tool injection disabled (server connection issues)
            // To re-enable: uncomment McpConnectionManager registration in Program.cs
            // and uncomment the McpToolDelegatingClient wrapper below

            IChatClient router = new LlmRoutingChatClient(
                fallback, sp, strategy, failoverStrategy, availabilityRegistry,
                sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
                sp.GetRequiredService<IOptions<ContextSizingOptions>>(),
                routerLogger);
            // Wrap with MCP layer if available:
            // var mcpManager = sp.GetRequiredService<McpConnectionManager>();
            // var mcpLogger = sp.GetRequiredService<ILogger<McpToolDelegatingClient>>();
            // return new McpToolDelegatingClient(router, mcpManager, mcpLogger);

            return router;
        });

        // ── Token Counting ────────────────────────────────────────────────────────
        services.AddSingleton<TokenCounting.ITokenCounter, TokenCounting.TiktokenTokenCounter>();

        // ── codebrewRouter virtual model ──────────────────────────────────────

        // Expose CodebrewRouterOptions from the nested LlmGatewayOptions property
        // so CodebrewRouterChatClient can receive IOptions<CodebrewRouterOptions> directly.
        services.AddSingleton<IOptions<CodebrewRouterOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.CodebrewRouter));

        // Same trick for PromptCleanupOptions so GemmaPromptCleaner can receive it directly.
        services.AddSingleton<IOptions<PromptCleanupOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.PromptCleanup));

        services.AddSingleton<IOptions<ContextCompactionOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.CodebrewRouter.ContextCompaction));

        services.AddSingleton<IOptions<ContextSizingOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.ContextSizing));

        // Prompt cleaner: Gemma-backed when feature enabled AND OllamaLocal keyed client
        // is registered; otherwise no-op. The cleaner is invoked by CodebrewRouterChatClient
        // before classification and before the downstream LLM call.
        services.AddSingleton<IPromptCleaner>(sp =>
        {
            var cleanupOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.PromptCleanup;
            var ollamaLocal = sp.GetKeyedService<IChatClient>("OllamaLocal");

            if (!cleanupOptions.Enabled || ollamaLocal is null)
            {
                return new NoopPromptCleaner();
            }

            return new GemmaPromptCleaner(
                ollamaLocal,
                sp.GetRequiredService<IOptions<PromptCleanupOptions>>(),
                sp.GetRequiredService<ILogger<GemmaPromptCleaner>>());
        });

        services.AddSingleton<IContextCompactor>(sp =>
        {
            var compactionOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.CodebrewRouter.ContextCompaction;
            if (!compactionOptions.Enabled)
            {
                return new NoopContextCompactor();
            }

            return new ContextCompactor(
                sp.GetKeyedService<IChatClient>("OllamaLocal"),
                sp.GetRequiredService<TokenCounting.ITokenCounter>(),
                sp.GetRequiredService<IOptions<ContextCompactionOptions>>(),
                sp.GetRequiredService<ILogger<ContextCompactor>>());
        });

        // Task classifier: Ollama-backed with keyword fallback (zero-latency on Ollama outage)
        services.AddSingleton<KeywordTaskClassifier>();
        services.AddSingleton<ITaskClassifier>(sp => new OllamaTaskClassifier(
            sp.GetRequiredKeyedService<IChatClient>("OllamaLocal"),
            sp.GetRequiredService<KeywordTaskClassifier>(),
            sp.GetRequiredService<ILogger<OllamaTaskClassifier>>()));

        // codebrewRouter keyed client — resolved by ModelSelectionResolver when model = "codebrewRouter"
        services.AddKeyedSingleton<IChatClient>("CodebrewRouter", (sp, _) =>
            (IChatClient)new CodebrewRouterChatClient(
                GetConfiguredKeyedClient(
                    sp,
                    "AzureFoundry",
                    IsAzureFoundryConfigured(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.AzureFoundry) &&
                    sp.GetRequiredService<IModelAvailabilityRegistry>().IsProviderAvailable("AzureFoundry"))
                ?? (IChatClient)new UnavailableChatClient("No currently available backing provider is available for codebrewRouter."),
                sp.GetRequiredService<ITaskClassifier>(),
                sp.GetRequiredService<IPromptCleaner>(),
                sp.GetRequiredService<IContextCompactor>(),
                sp.GetRequiredService<TokenCounting.ITokenCounter>(),
                sp.GetRequiredService<IOptions<CodebrewRouterOptions>>(),
                sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
                sp.GetRequiredService<IModelAvailabilityRegistry>(),
                sp,
                sp.GetRequiredService<ILogger<CodebrewRouterChatClient>>()));

        return services;
    }

    private static IChatClient? GetConfiguredKeyedClient(IServiceProvider sp, string key, bool isConfigured)
        => isConfigured ? sp.GetKeyedService<IChatClient>(key) : null;

    private static bool IsAzureFoundryConfigured(AzureFoundryOptions options)
        => HasValue(options.Model) &&
           (HasValue(options.ResponsesEndpoint) ||
            (HasValue(options.Endpoint) && HasValue(options.ApiKey)));

    private static bool IsFoundryLocalConfigured(FoundryLocalOptions options)
        => HasValue(options.Endpoint) && HasValue(options.Model);

    private static bool IsGithubModelsConfigured(GithubModelsOptions options)
        => HasValue(options.Endpoint) && HasValue(options.Model) && HasValue(options.ApiKey);

    private static bool IsLmStudioConfigured(LmStudioOptions options)
        => HasValue(options.Endpoint) && HasValue(options.Model);

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
