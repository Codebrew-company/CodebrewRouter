using Blaze.LlmGateway.Core.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace Blaze.LlmGateway.Infrastructure.Provider;

/// <summary>
/// Extension methods for registering CodebrewRouterProvider into DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register CodebrewRouterProvider with default configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Configuration options.</param>
    /// <returns>Builder for fluent configuration (optional).</returns>
    /// <remarks>
    /// Minimum usage (mobile):
    ///   services.AddCodebrewRouterProvider(options);
    /// 
    /// Full usage (desktop/Aspire):
    ///   services.AddCodebrewRouterProvider(options)
    ///       .WithHealthChecks()
    ///       .WithDiscovery()
    ///       .WithRouting()
    ///       .Build();
    /// </remarks>
    public static ICodebrewRouterProviderBuilder AddCodebrewRouterProvider(
        this IServiceCollection services,
        CodebrewRouterProviderOptions options)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (options == null) throw new ArgumentNullException(nameof(options));

        var builder = new CodebrewRouterProviderBuilder(services, options);

        // If not in test mode, register default features
        if (!options.TestMode)
        {
            builder
                .WithHealthChecks()
                .WithDiscovery()
                .WithRouting()
                .Build();
        }
        else
        {
            // Test mode: skip defaults, let caller decide
            // Must still call Build() before DI is complete
        }

        return builder;
    }
}
