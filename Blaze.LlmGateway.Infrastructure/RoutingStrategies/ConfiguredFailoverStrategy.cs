using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies;

/// <summary>
/// Configuration-based failover strategy that maps each destination to a list of fallback providers.
/// </summary>
public class ConfiguredFailoverStrategy(
    IOptions<LlmGatewayOptions> options,
    ILogger<ConfiguredFailoverStrategy> logger) : IFailoverStrategy
{
    private readonly Dictionary<RouteDestination, List<RouteDestination>> _failoverChains = [];

    public IReadOnlyList<RouteDestination> GetFailoverChainAsync(RouteDestination primary)
    {
        if (_failoverChains.TryGetValue(primary, out var chain))
        {
            logger.LogDebug("Failover chain for {Primary}: {Fallbacks}", 
                primary, string.Join(" → ", chain));
            return chain.AsReadOnly();
        }

        logger.LogDebug("No failover chain configured for {Primary}; using empty fallback", primary);
        return Array.Empty<RouteDestination>();
    }

    /// <summary>Build failover chains from configuration during DI setup.</summary>
    public void Initialize()
    {
        var routingConfig = options.Value.Routing;
        if (routingConfig?.FailoverChains == null || routingConfig.FailoverChains.Count == 0)
        {
            logger.LogWarning("No failover chains configured in appsettings");
            return;
        }

        foreach (var kvp in routingConfig.FailoverChains)
        {
            if (Enum.TryParse<RouteDestination>(kvp.Key, ignoreCase: true, out var destination))
            {
                var fallbacks = kvp.Value
                    .Where(f => Enum.TryParse<RouteDestination>(f, ignoreCase: true, out _))
                    .Select(f => Enum.Parse<RouteDestination>(f, ignoreCase: true))
                    .ToList();

                _failoverChains[destination] = fallbacks;
                logger.LogInformation("Registered failover chain for {Destination}: {Fallbacks}", 
                    destination, string.Join(" → ", fallbacks));
            }
        }
    }
}
