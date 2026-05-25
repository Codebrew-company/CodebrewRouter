using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
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
        // TEMPORARY: Disable OllamaLocal registration due to connectivity issues
        // When Ollama is running locally or remotely, re-enable this and update configuration
        /*
        // OllamaLocal — local Ollama container (backup for remote server)
        services.AddKeyedSingleton<IChatClient>("OllamaLocal", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OllamaLocal;
            var log = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            log.LogDebug("Initializing OllamaLocal keyed client: {BaseUrl}/{Model}", opts.BaseUrl, opts.Model);
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
        */

        // LmStudio — local OpenAI-compatible endpoint exposed by LM Studio at /v1.
        services.AddKeyedSingleton<IChatClient>("LmStudio", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.LmStudio;
            var log = sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();
            var logMock = sp.GetRequiredService<ILogger<MockChatClient>>();
            
            log.LogDebug("Initializing LmStudio keyed client: {Endpoint}/{Model}", opts.Endpoint, opts.Model);
            
            try
            {
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
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "⚠️ Failed to initialize LmStudio client; using MockChatClient for testing");
                return new MockChatClient(logMock);
            }
        });

        // ── OpenCode Go — cloud provider with 14 models ───────────────
        // Register one shared OpenAIClient (single HTTP connection pool) and
        // 14 keyed IChatClient wrappers that resolve it per-model.

        services.AddKeyedSingleton<OpenAIClient>("OpenCodeGo_Client", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OpenCodeGo;
            var apiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? "notneeded" : opts.ApiKey;
            return new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(opts.BaseUrl) });
        });

        var tokenCounterOcg = default(Blaze.LlmGateway.Infrastructure.TokenCounting.ITokenCounter);
        var compactorOcg   = default(IContextCompactor);
        IOptions<ContextSizingOptions>? sizingOptionsOcg = null;
        ILogger<ContextHandling.ContextSizingChatClient>? sizingLoggerOcg = null;

        foreach (var (dest, modelName) in OpenCodeGoModels.ModelNames)
        {
            var key = dest.ToString();
            services.AddKeyedSingleton<IChatClient>(key, (sp, _) =>
            {
                var client = sp.GetRequiredKeyedService<OpenAIClient>("OpenCodeGo_Client");
                var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OpenCodeGo;

                tokenCounterOcg ??= sp.GetRequiredService<TokenCounting.ITokenCounter>();
                compactorOcg   ??= sp.GetRequiredService<IContextCompactor>();
                sizingOptionsOcg ??= sp.GetRequiredService<IOptions<ContextSizingOptions>>();
                sizingLoggerOcg  ??= sp.GetRequiredService<ILogger<ContextHandling.ContextSizingChatClient>>();

                return client.GetChatClient(modelName).AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .UseContextSizing(tokenCounterOcg, compactorOcg, sizingOptionsOcg,
                        opts.MaxContextTokens, opts.ReservedOutputTokens, modelName, sizingLoggerOcg)
                    .Build();
            });
        }

        // Mock client for testing when infrastructure is unavailable
        services.AddKeyedSingleton<IChatClient>("Mock", (sp, _) =>
        {
            var log = sp.GetRequiredService<ILogger<MockChatClient>>();
            return new MockChatClient(log);
        });

        return services;
    }

    public static IServiceCollection AddLlmInfrastructure(this IServiceCollection services)
    {
        // Register thread-safe health state manager
        services.AddSingleton<IOllamaHealthState>(sp =>
        {
            // Create the health state manager
            var logger = sp.GetRequiredService<ILogger<OllamaHealthStateManager>>();
            var healthState = new OllamaHealthStateManager(logger);
            
            // CRITICAL: Pre-initialize endpoints IMMEDIATELY at DI registration time
            // This must happen before any keyed client tries to use the health state
            try
            {
                var ollamaRouterOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OllamaRouter;
                
                logger.LogInformation("🔄 Pre-initializing OllamaHealthStateManager at DI registration time");
                logger.LogInformation("  Primary endpoint: {Primary}", ollamaRouterOptions.PrimaryEndpoint);
                logger.LogInformation("  Fallback endpoint: {Fallback}", ollamaRouterOptions.FallbackEndpoint);
                
                healthState.SetEndpoints(
                    ollamaRouterOptions.PrimaryEndpoint,
                    ollamaRouterOptions.FallbackEndpoint);
                
                logger.LogInformation("✅ OllamaHealthStateManager initialized at DI time");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Failed to initialize OllamaHealthStateManager during DI registration");
                throw;
            }
            
            return healthState;
        });
        
        // Register model sync validator (used during startup)
        services.AddSingleton<OllamaModelSyncValidator>();

        // Register Ollama router clients (lazy-initialized on first access to avoid startup hang)
        services.AddKeyedSingleton<IChatClient>("OllamaRouter", (sp, _) =>
        {
            var ollamaRouterOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OllamaRouter;
            var healthState = sp.GetRequiredService<IOllamaHealthState>();
            var logger = sp.GetRequiredService<ILogger<OllamaFailoverClient>>();

            logger.LogInformation("📍 Lazy-initializing OllamaRouter keyed client");

            // Create OllamaApiClient instances with extended timeout for large models
            // .12 may be running gemma4:26b (26 billion parameters) which can take 30+ seconds per inference
            var longTimeout = TimeSpan.FromSeconds(180); // 3 minutes for very large models
            
            var primaryClient = OllamaClientExtensions.CreateWithTimeout(
                new Uri(ollamaRouterOptions.PrimaryEndpoint),
                ollamaRouterOptions.Model,
                longTimeout);

            var fallbackClient = OllamaClientExtensions.CreateWithTimeout(
                new Uri(ollamaRouterOptions.FallbackEndpoint),
                ollamaRouterOptions.Model,
                longTimeout);

            logger.LogInformation("✅ Ollama clients created with 180-second timeout for large model inference");

            // Create failover wrapper that uses cached clients
            var failoverClient = new OllamaFailoverClient(
                (IChatClient)primaryClient,
                (IChatClient)fallbackClient,
                healthState,
                ollamaRouterOptions.PrimaryEndpoint,
                ollamaRouterOptions.FallbackEndpoint,
                logger);

            return (IChatClient)failoverClient;
        });

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
            var keywordFallback = sp.GetRequiredService<KeywordRoutingStrategy>();
            var logger = sp.GetRequiredService<ILogger<OllamaMetaRoutingStrategy>>();
            
            // ⚠️ TEMPORARY: Skip OllamaRouter due to Ollama .12 hanging on inference requests
            // Until Ollama is fixed/restarted, use keyword-only routing to avoid 10+ second hangs
            logger.LogWarning("⚠️ OllamaRouter disabled due to upstream Ollama connectivity issues; using keyword-only routing");
            return keywordFallback;
            
            // Future: Re-enable when Ollama .12 is stable
            // try
            // {
            //     var routerClient = sp.GetKeyedService<IChatClient>("OllamaRouter");
            //     if (routerClient is not null)
            //     {
            //         logger.LogInformation("✅ OllamaRouter available; using meta-routing strategy with 10-second probe timeout");
            //         return new OllamaMetaRoutingStrategy(routerClient, keywordFallback, logger);
            //     }
            // }
            // catch (Exception ex)
            // {
            //     logger.LogWarning(ex, "⚠️ OllamaRouter initialization failed; falling back to keyword-only routing");
            // }
            // 
            // logger.LogInformation("⚠️ OllamaRouter not available; using keyword-only routing strategy as fallback");
            // return keywordFallback;
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
                GetConfiguredKeyedClient(sp, "LmStudio", HasValue(providerOptions.LmStudio.Model) && availabilityRegistry.IsProviderAvailable("LmStudio"))
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
        // Register tokenizer registry for OpenCodeGo and other models
        services.AddSingleton<TokenCounting.ITokenizerRegistry>(sp =>
            new TokenCounting.OpenCodeGoTokenizerRegistry(
                sp.GetRequiredService<ILogger<TokenCounting.OpenCodeGoTokenizerRegistry>>()));

        // Register token counter with registry support for graceful model-specific tokenization
        services.AddSingleton<TokenCounting.ITokenCounter>(sp =>
            new TokenCounting.TiktokenTokenCounter(
                defaultModelId: "gpt-4o",
                registry: sp.GetRequiredService<TokenCounting.ITokenizerRegistry>(),
                logger: sp.GetRequiredService<ILogger<TokenCounting.TiktokenTokenCounter>>()));

        // ── codebrewRouter virtual model ──────────────────────────────────────

        // Expose CodebrewRouterOptions from the nested LlmGatewayOptions property
        // so CodebrewRouterChatClient can receive IOptions<CodebrewRouterOptions> directly.
        services.AddSingleton<IOptions<CodebrewRouterOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.CodebrewRouter));

        // Same trick for PromptCleanupOptions so GemmaPromptCleaner can receive it directly.
        services.AddSingleton<IOptions<PromptCleanupOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.PromptCleanup));

        // Same trick for TaskClassificationOptions so OllamaTaskClassifier can receive it directly.
        services.AddSingleton<IOptions<TaskClassificationOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.TaskClassification));

        services.AddSingleton<IOptions<ContextCompactionOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.CodebrewRouter.ContextCompaction));

        services.AddSingleton<IOptions<ContextSizingOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.ContextSizing));

        // Prompt cleaner: Gemma-backed with cached OllamaRouter client
        // The cleaner is invoked by CodebrewRouterChatClient before classification and before the downstream LLM call.
        services.AddSingleton<IPromptCleaner>(sp =>
        {
            var cleanupOptions = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.PromptCleanup;
            
            if (!cleanupOptions.Enabled)
            {
                return new NoopPromptCleaner();
            }

            var ollamaRouter = sp.GetKeyedService<IChatClient>("OllamaRouter");
            if (ollamaRouter is null)
            {
                return new NoopPromptCleaner();
            }

            return new GemmaPromptCleaner(
                ollamaRouter,
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

            var ollamaLocal = sp.GetKeyedService<IChatClient>("OllamaLocal");
            if (ollamaLocal is null)
            {
                // OllamaLocal not available; skip context compaction
                return new NoopContextCompactor();
            }

            return new ContextCompactor(
                ollamaLocal,
                sp.GetRequiredService<TokenCounting.ITokenCounter>(),
                sp.GetRequiredService<IOptions<ContextCompactionOptions>>(),
                sp.GetRequiredService<ILogger<ContextCompactor>>());
        });

        // Task classifier: Ollama-backed with keyword fallback (zero-latency on Ollama outage)
        services.AddSingleton<ITaskClassifier>(sp =>
        {
            var classificationOptions = sp.GetRequiredService<IOptions<TaskClassificationOptions>>().Value;
            if (!classificationOptions.Enabled)
            {
                return new KeywordTaskClassifier(sp.GetRequiredService<ILogger<KeywordTaskClassifier>>());
            }

            var ollamaRouter = sp.GetKeyedService<IChatClient>("OllamaRouter");
            if (ollamaRouter is not null)
            {
                return new OllamaTaskClassifier(
                    ollamaRouter,
                    sp.GetRequiredService<IOptions<TaskClassificationOptions>>(),
                    sp.GetRequiredService<ILogger<OllamaTaskClassifier>>());
            }
            
            // Fallback: return keyword-only classifier when Ollama not available
            return new KeywordTaskClassifier(sp.GetRequiredService<ILogger<KeywordTaskClassifier>>());
        });

        // codebrewRouter keyed client — resolved by ModelSelectionResolver when model = "codebrewRouter"
        services.AddKeyedSingleton<IChatClient>("CodebrewRouter", (sp, _) =>
            (IChatClient)new CodebrewRouterChatClient(
                GetConfiguredKeyedClient(
                    sp,
                    "LmStudio",
                    HasValue(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.LmStudio.Model) &&
                    sp.GetRequiredService<IModelAvailabilityRegistry>().IsProviderAvailable("LmStudio"))
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

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
