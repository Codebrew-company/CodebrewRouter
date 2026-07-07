using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blaze.LlmGateway.Tests.Infrastructure;

/// <summary>
/// P5a: subscription upstream providers register as keyed OpenAI-compatible clients
/// only when the master gate (LlmGateway:Providers:Subscription:Enabled) is on. With
/// the gate off — the default — no subscription clients exist (fresh-install contract).
/// </summary>
public sealed class SubscriptionProviderRegistrationTests
{
    private static ServiceProvider Build(SubscriptionOptions subscription)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Options.Create(new LlmGatewayOptions
        {
            Providers = new ProvidersOptions { Subscription = subscription }
        }));
        services.AddLlmProviders();
        return services.BuildServiceProvider();
    }

    private static SubscriptionProviderOptions ApiKeyProvider(string name) => new()
    {
        Name = name,
        Kind = SubscriptionAuthKind.ApiKey,
        Endpoint = "https://api.example.com/v1",
        Model = "example-model",
        ApiKey = "sk-test",
        TosNote = "Reusing this subscription may violate the provider's ToS."
    };

    [Fact]
    public void GateOff_ByDefault_RegistersNoSubscriptionClients()
    {
        var options = new SubscriptionOptions { Providers = [ApiKeyProvider("AcmeSub")] };
        options.Enabled.Should().BeFalse("the master gate must default to off");

        using var provider = Build(options);

        provider.GetKeyedService<IChatClient>("AcmeSub")
            .Should().BeNull("with the gate off, no subscription upstream is registered");
    }

    [Fact]
    public void GateOn_ApiKeyProvider_GetsKeyedChatClient()
    {
        using var provider = Build(new SubscriptionOptions
        {
            Enabled = true,
            Providers = [ApiKeyProvider("AnthropicApi")]
        });

        provider.GetKeyedService<IChatClient>("AnthropicApi")
            .Should().NotBeNull("an enabled API-key subscription provider is routable");
    }

    [Fact]
    public void GateOn_OAuthProvider_IsNotRegisteredHere()
    {
        // OAuth-kind providers are registered by the OAuth token service (P5b), not P5a.
        using var provider = Build(new SubscriptionOptions
        {
            Enabled = true,
            Providers =
            [
                new SubscriptionProviderOptions
                {
                    Name = "ClaudeOAuth",
                    Kind = SubscriptionAuthKind.OAuth,
                    Endpoint = "https://api.anthropic.com/v1",
                    Model = "claude",
                    OAuth = new SubscriptionOAuthOptions { ClientId = "cid", TokenEndpoint = "https://x/token" }
                }
            ]
        });

        provider.GetKeyedService<IChatClient>("ClaudeOAuth")
            .Should().BeNull("OAuth-kind subscription providers are wired by P5b, not the API-key path");
    }

    [Fact]
    public void GateOn_PerProviderDisabled_IsSkipped()
    {
        var disabled = ApiKeyProvider("DisabledSub");
        disabled.Enabled = false;

        using var provider = Build(new SubscriptionOptions { Enabled = true, Providers = [disabled] });

        provider.GetKeyedService<IChatClient>("DisabledSub").Should().BeNull();
    }

    [Fact]
    public void GateOn_MissingEndpointOrModel_IsSkipped()
    {
        using var provider = Build(new SubscriptionOptions
        {
            Enabled = true,
            Providers =
            [
                new SubscriptionProviderOptions { Name = "NoEndpoint", Model = "m", ApiKey = "k" },
                new SubscriptionProviderOptions { Name = "NoModel", Endpoint = "https://x/v1", ApiKey = "k" }
            ]
        });

        provider.GetKeyedService<IChatClient>("NoEndpoint").Should().BeNull();
        provider.GetKeyedService<IChatClient>("NoModel").Should().BeNull();
    }
}
