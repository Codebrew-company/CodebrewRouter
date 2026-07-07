using System.ClientModel;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Blaze.LlmGateway.Infrastructure.Providers.Oauth;

/// <summary>
/// P5b — OAuth subscription reuse (opt-in, off by default). Registers the token
/// registry, refresh client, proactive refresh service, and one keyed chat client per
/// OAuth-kind subscription provider — but ONLY when the master gate
/// (LlmGateway:Providers:Subscription:Enabled) is on and at least one OAuth provider is
/// configured. With the gate off (the default), none of this is registered: the flows
/// are absent, satisfying the fresh-install contract.
/// </summary>
public static class SubscriptionOAuthExtensions
{
    public static IServiceCollection AddSubscriptionOAuth(this IServiceCollection services)
    {
        using var spTemp = services.BuildServiceProvider();
        var subscription = spTemp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.Subscription;

        var oauthProviders = subscription.Enabled
            ? subscription.Providers.Where(p => p.Enabled
                && p.Kind == SubscriptionAuthKind.OAuth
                && !string.IsNullOrWhiteSpace(p.Name)
                && !string.IsNullOrWhiteSpace(p.Endpoint)
                && !string.IsNullOrWhiteSpace(p.Model)).ToList()
            : [];

        if (oauthProviders.Count == 0)
        {
            return services; // Gate off or no OAuth providers → register nothing.
        }

        services.AddSingleton<SubscriptionTokenRegistry>();
        services.AddHttpClient<ISubscriptionOAuthClient, HttpSubscriptionOAuthClient>();
        services.AddHostedService<SubscriptionOAuthTokenRefreshService>();

        foreach (var provider in oauthProviders)
        {
            var entry = provider;
            services.AddKeyedSingleton<IChatClient>(entry.Name, (sp, _) =>
            {
                var registry = sp.GetRequiredService<SubscriptionTokenRegistry>();
                var oauthClient = sp.GetRequiredService<ISubscriptionOAuthClient>();
                var logger = sp.GetRequiredService<ILogger<ReactiveRefreshChatClient>>();

                if (!string.IsNullOrWhiteSpace(entry.TosNote))
                {
                    logger.LogWarning("⚖️  Subscription OAuth provider '{Name}' ToS note: {Note}", entry.Name, entry.TosNote);
                }

                // The chat client is built against the registry's mutable credential;
                // proactive + reactive refresh rotate the token in place.
                var credential = registry.GetOrCreateCredential(entry.Name);
                var inner = new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(entry.Endpoint) })
                    .GetChatClient(entry.Model)
                    .AsIChatClient()
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();

                return new ReactiveRefreshChatClient(inner, registry, oauthClient, entry, logger);
            });
        }

        return services;
    }
}
