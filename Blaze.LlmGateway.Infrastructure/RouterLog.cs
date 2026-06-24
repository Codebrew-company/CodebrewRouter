using Blaze.LlmGateway.Core.Routing;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure;

public static class RouterLog
{
    public static string GetTag(object routerEvent) =>
        routerEvent switch
        {
            RouterStartEvent          => "[ROUTER-START]",
            RouterCleanEvent          => "[ROUTER-CLEAN]",
            RouterResolveEvent        => "[ROUTER-RESOLVE]",
            RouterContextBudgetEvent  => "[ROUTER-CONTEXT]",
            RouterCompactEvent        => "[ROUTER-COMPACT]",
            RouterSkipEvent           => "[ROUTER-SKIP]",
            RouterTryEvent            => "[ROUTER-TRY]",
            RouterProbeEvent          => "[ROUTER-PROBE]",
            RouterSuccessEvent        => "[ROUTER-SUCCESS]",
            RouterFailEvent           => "[ROUTER-FAIL]",
            RouterExhaustedEvent      => "[ROUTER-EXHAUSTED]",
            RouterMidstreamFailEvent  => "[ROUTER-MIDSTREAM-FAIL]",
            RouterStreamCompleteEvent => "[ROUTER-STREAM-COMPLETE]",
            RouterSelectEvent         => "[ROUTER-SELECT]",
            RouterDeployEvent         => "[ROUTER-DEPLOY]",
            RouterHealthEvent         => "[ROUTER-HEALTH]",
            RouterFallbackEvent       => "[ROUTER-FALLBACK]",
            RouterFusionEvent         => "[ROUTER-FUSION]",
            RouterFusionResultEvent   => "[ROUTER-FUSION-RESULT]",
            _ => "[ROUTER-UNKNOWN]"
        };

    public static LogLevel GetDefaultLevel(object routerEvent) =>
        routerEvent switch
        {
            RouterContextBudgetEvent => LogLevel.Debug,
            RouterSkipEvent => LogLevel.Warning,
            RouterFailEvent => LogLevel.Warning,
            RouterExhaustedEvent => LogLevel.Warning,
            RouterMidstreamFailEvent => LogLevel.Warning,
            _ => LogLevel.Information
        };

    public static void Write(ILogger logger, object routerEvent, LogLevel? level = null)
    {
        var resolvedLevel = level ?? GetDefaultLevel(routerEvent);
        if (!logger.IsEnabled(resolvedLevel))
        {
            return;
        }

        logger.Log(resolvedLevel, "{Tag} {@Event}", GetTag(routerEvent), routerEvent);
    }
}
