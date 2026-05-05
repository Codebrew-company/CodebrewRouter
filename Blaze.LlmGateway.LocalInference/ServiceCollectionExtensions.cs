using System;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.Provider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        "Use AddCodebrewRouterProvider(CodebrewRouterProviderOptions) instead. " +
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

        // Register LocalGemmaChatClient as keyed service "LocalGemma"
        services.AddKeyedSingleton<Microsoft.Extensions.AI.IChatClient>("LocalGemma", (sp, _) =>
        {
            return new LocalGemmaChatClient();
        });

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
                tags: ["local-inference", "readiness"]);

        return services;
    }
}
