using Blaze.LlmGateway.Core.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api.UsageTracking;

public static class UsageTrackingRegistration
{
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
