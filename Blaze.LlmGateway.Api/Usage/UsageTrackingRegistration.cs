using Blaze.LlmGateway.Core.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api.UsageTracking;

public static class UsageTrackingRegistration
{
    /// <summary>
    /// Wraps a resolver-selected keyed client with the usage ledger for one request.
    /// Endpoints call this because keyed clients bypass the DI-decorated unkeyed router.
    /// </summary>
    public static IChatClient WrapForRequest(IChatClient client, IServiceProvider services)
    {
        if (client is UsageTrackingChatClient)
        {
            return client;
        }

        // Fail-open: the ledger must never break a request (or a bare-bones test host).
        var store = services.GetService<IProtocolStore>();
        var accessor = services.GetService<IHttpContextAccessor>();
        var logger = services.GetService<ILogger<UsageTrackingChatClient>>();
        return store is null || accessor is null || logger is null
            ? client
            : new UsageTrackingChatClient(client, store, accessor, services.GetService<IProviderCatalog>(), logger);
    }

    /// <summary>
    /// Decorates the unkeyed router <see cref="IChatClient"/> with the usage ledger.
    /// Call after AddLlmInfrastructure so the router registration exists.
    /// </summary>
    public static IServiceCollection AddUsageTracking(this IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d =>
            d.ServiceType == typeof(IChatClient) && !d.IsKeyedService && d.ImplementationFactory is not null);
        if (descriptor?.ImplementationFactory is not { } innerFactory)
        {
            return services;
        }

        services.Remove(descriptor);
        services.AddSingleton<IChatClient>(sp => new UsageTrackingChatClient(
            (IChatClient)innerFactory(sp),
            sp.GetRequiredService<IProtocolStore>(),
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetService<IProviderCatalog>(),
            sp.GetRequiredService<ILogger<UsageTrackingChatClient>>()));
        return services;
    }
}
