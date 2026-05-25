using System;
using System.Diagnostics;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.Provider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Extension methods for registering local inference services into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provider-backed local CodebrewRouter services used by the API.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCodebrewRouterLocalProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));

        var section = configuration.GetSection("LlmGateway:LocalInference");
        var localOptions = new LocalInferenceOptions();
        section.Bind(localOptions);

        var providerOptions = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = configuration["LlmGateway:Providers:OllamaRouter:PrimaryEndpoint"]
                ?? "http://127.0.0.1:11434",
            RemoteDiscoveryEndpoint = null,
            CacheAvailabilityTtlSeconds = localOptions.CacheAvailabilityTtlSeconds ?? 60,
            CircuitBreakerCooldownMinutes = localOptions.CircuitBreakerCooldownMinutes,
            HealthChecksEnabled = true,
            LocalModelPath = localOptions.ModelPath,
            CacheDirectory = localOptions.CacheDirectory,
            LocalMaxContextTokens = localOptions.MaxContextTokens,
            LocalThreadCount = localOptions.ThreadCount,
            TestMode = false
        };

        services.AddCodebrewRouterProvider(providerOptions);

        services.AddSingleton(localOptions);
        services.AddSingleton(Options.Create(localOptions));

        services.AddSingleton(sp => new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(localOptions.DownloadTimeoutSeconds)
        });

        services.AddSingleton<IModelDistributionProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>();
            var logger = sp.GetService<ILogger<RuntimeDownloadModelProvider>>()
                ?? new NullLogger<RuntimeDownloadModelProvider>();
            return new RuntimeDownloadModelProvider(httpClient, opts, logger);
        });

        services.AddSingleton<LocalGemmaWarmupState>();

        services.AddKeyedSingleton<IChatClient>("LocalGemma", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>().Value;
            var provider = sp.GetRequiredService<IModelDistributionProvider>();
            var logger = sp.GetService<ILogger<LocalGemmaChatClient>>();
            return new LocalGemmaChatClient(opts, provider, logger);
        });
        services.AddHostedService<LocalGemmaWarmupService>();

        services.AddSingleton<HybridRoutingStrategyFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>();
            var modelProvider = sp.GetRequiredService<IModelDistributionProvider>();
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? new NullLoggerFactory();
            var logger = sp.GetService<ILogger<HybridLocalRemoteRoutingStrategy>>()
                ?? loggerFactory.CreateLogger<HybridLocalRemoteRoutingStrategy>();

            return new HybridRoutingStrategyFactory(opts, modelProvider, null, loggerFactory, logger);
        });

        services.AddSingleton<ILocalModelAvailability, LocalModelAvailabilityService>();
        services.AddSingleton<ICodebrewRouterDiscoveryService, CodebrewRouterDiscoveryService>();
        services.AddSingleton<ILocalInferenceHealthManager, LocalInferenceHealthManager>();

        services.AddHealthChecks()
            .AddCheck<LocalInferenceHealthManager>(
                "local-inference",
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["local-inference", "readiness"])
            .Add(new HealthCheckRegistration(
                "local-gemma-warmup",
                sp => sp.GetRequiredService<LocalGemmaWarmupState>(),
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["local-inference", "readiness"]));

        return services;
    }

    /// <summary>
    /// Registers local inference services (LocalGemmaChatClient, RuntimeDownloadModelProvider, routing strategy).
    /// Also registers Phase 1 health management services: availability tracking, remote discovery, and health checks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// DEPRECATED: Use AddCodebrewRouterProvider(CodebrewRouterProviderOptions) instead.
    /// This method will be removed in v2.0 of Blaze.LlmGateway.
    /// 
    /// Currently forwards to the legacy implementation for backwards compatibility.
    /// </remarks>
    [Obsolete(
        "Use AddCodebrewRouterLocalProvider(IConfiguration) for API registration or AddCodebrewRouterProvider(CodebrewRouterProviderOptions) for custom hosts. " +
        "This method will be removed in v2.0 of Blaze.LlmGateway.",
        false)]
    public static IServiceCollection AddLocalInferenceServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));

        // Bind configuration from LlmGateway:LocalInference section
        var localInferenceSection = configuration.GetSection("LlmGateway:LocalInference");
        var options = new LocalInferenceOptions();
        localInferenceSection.Bind(options);

        // Register configuration as both concrete and IOptions
        services.AddSingleton(options);
        services.AddSingleton(Options.Create(options));

        // Register HttpClient directly as singleton (Rubber-Duck Fix #4)
        services.AddSingleton(sp =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(options.DownloadTimeoutSeconds)
            };
            return httpClient;
        });

        // Register model distribution provider as singleton (expensive initialization)
        services.AddSingleton<IModelDistributionProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>();
            var logger = sp.GetService<ILogger<RuntimeDownloadModelProvider>>() 
                ?? new NullLogger<RuntimeDownloadModelProvider>();
            return new RuntimeDownloadModelProvider(httpClient, opts, logger);
        });

        services.AddSingleton<LocalGemmaWarmupState>();

        // Register LocalGemmaChatClient as keyed service "LocalGemma"
        services.AddKeyedSingleton<Microsoft.Extensions.AI.IChatClient>("LocalGemma", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>().Value;
            var provider = sp.GetRequiredService<IModelDistributionProvider>();
            var logger = sp.GetService<ILogger<LocalGemmaChatClient>>();
            return new LocalGemmaChatClient(opts, provider, logger);
        });
        services.AddHostedService<LocalGemmaWarmupService>();

        // Register hybrid routing strategy factory
        services.AddSingleton<HybridRoutingStrategyFactory>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>();
            var modelProvider = sp.GetRequiredService<IModelDistributionProvider>();
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? new NullLoggerFactory();
            var logger = sp.GetService<ILogger<HybridLocalRemoteRoutingStrategy>>()
                ?? loggerFactory.CreateLogger<HybridLocalRemoteRoutingStrategy>();
            
            // routerClient is optional - pass null if not available
            return new HybridRoutingStrategyFactory(opts, modelProvider, null, loggerFactory, logger);
        });

        // Phase 1: Register availability and health management services
        services.AddSingleton<ILocalModelAvailability, LocalModelAvailabilityService>();
        services.AddSingleton<ICodebrewRouterDiscoveryService, CodebrewRouterDiscoveryService>();
        services.AddSingleton<ILocalInferenceHealthManager, LocalInferenceHealthManager>();

        // Register health check for local inference
        services.AddHealthChecks()
            .AddCheck<LocalInferenceHealthManager>(
                "local-inference",
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["local-inference", "readiness"])
            .Add(new HealthCheckRegistration(
                "local-gemma-warmup",
                sp => sp.GetRequiredService<LocalGemmaWarmupState>(),
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["local-inference", "readiness"]));

        return services;
    }
}

public sealed class LocalGemmaWarmupService(
    IServiceProvider serviceProvider,
    IOptions<LocalInferenceOptions> options,
    LocalGemmaWarmupState state,
    ILogger<LocalGemmaWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var stopwatch = Stopwatch.StartNew();

        if (!opts.Enabled)
        {
            const string reason = "Local inference is disabled.";
            state.Update(LocalGemmaWarmupStatus.Skipped, opts.ModelPath, reason, stopwatch.Elapsed);
            LocalWarmupLog.Skip(logger, reason, opts.ModelPath);
            return;
        }

        if (!opts.WarmupEnabled)
        {
            const string reason = "Local Gemma warmup is disabled.";
            state.Update(LocalGemmaWarmupStatus.Skipped, opts.ModelPath, reason, stopwatch.Elapsed);
            LocalWarmupLog.Skip(logger, reason, opts.ModelPath);
            return;
        }

        LocalWarmupLog.Start(logger, opts.ModelPath, opts.BlockStartupUntilWarm);

        if (string.IsNullOrWhiteSpace(opts.ModelPath))
        {
            const string reason = "Local inference model path is not configured.";
            if (opts.BlockStartupUntilWarm)
            {
                Fail(stopwatch, opts.ModelPath, reason, null);
                throw new InvalidOperationException(reason);
            }

            Fail(stopwatch, opts.ModelPath, reason, null);
            return;
        }

        try
        {
            var client = serviceProvider.GetKeyedService<IChatClient>("LocalGemma")
                ?? throw new InvalidOperationException("Keyed LocalGemma chat client is not registered.");
            var modelState = ResolveModelState(client)
                ?? throw new InvalidOperationException("LocalGemma chat client does not expose local model load state.");

            // Transition to Downloading: covers both download (if URL) and local runtime load.
            state.Update(LocalGemmaWarmupStatus.Downloading, opts.ModelPath, "Resolving local Gemma model source.", stopwatch.Elapsed);

            // EnsureLoadedAsync handles download (if URL) then runtime load.
            // The callback fires when the file is local and the runtime is about to start loading.
            await modelState.EnsureLoadedAsync(cancellationToken, () =>
            {
                state.Update(LocalGemmaWarmupStatus.Loading, modelState.ModelPath, "Loading local Gemma model into the local runtime.", stopwatch.Elapsed);
            });

            LocalWarmupLog.Load(logger, modelState.ModelPath, modelState.IsModelLoaded, stopwatch.ElapsedMilliseconds);

            state.Update(LocalGemmaWarmupStatus.Priming, modelState.ModelPath, "Priming local Gemma model.", stopwatch.Elapsed);

            var prompt = string.IsNullOrWhiteSpace(opts.WarmupPrompt) ? "ready" : opts.WarmupPrompt;
            var maxOutputTokens = Math.Max(1, opts.WarmupMaxOutputTokens);
            LocalWarmupLog.Prime(logger, prompt, maxOutputTokens);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, opts.WarmupTimeoutSeconds)));

            var chunks = 0;
            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            var chatOptions = new ChatOptions
            {
                MaxOutputTokens = maxOutputTokens,
                Temperature = 0.0f
            };

            await foreach (var _ in client.GetStreamingResponseAsync(messages, chatOptions, cts.Token))
            {
                chunks++;
                if (chunks >= maxOutputTokens)
                {
                    break;
                }
            }

            stopwatch.Stop();
            state.Update(LocalGemmaWarmupStatus.Ready, modelState.ModelPath, "Local Gemma warmup completed.", stopwatch.Elapsed);
            LocalWarmupLog.Ready(logger, modelState.ModelPath, chunks, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!opts.BlockStartupUntilWarm)
        {
            Fail(stopwatch, opts.ModelPath, ex.Message, ex);
        }
        catch (Exception ex)
        {
            Fail(stopwatch, opts.ModelPath, ex.Message, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ILocalGemmaModelState? ResolveModelState(IChatClient client)
        => client as ILocalGemmaModelState
           ?? client.GetService(typeof(ILocalGemmaModelState)) as ILocalGemmaModelState;

    private void Fail(Stopwatch stopwatch, string? modelPath, string reason, Exception? exception)
    {
        stopwatch.Stop();
        state.Update(LocalGemmaWarmupStatus.Failed, modelPath, reason, stopwatch.Elapsed);
        LocalWarmupLog.Fail(logger, reason, modelPath, exception);
    }
}
