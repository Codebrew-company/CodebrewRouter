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
/// P4.4: a new OpenAI-compatible provider is onboarded from ProviderCatalog config
/// alone — no code registration required.
/// </summary>
public sealed class ConfigDrivenProviderRegistrationTests
{
    private static ServiceProvider Build(params ProviderDeploymentConfig[] deployments)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Options.Create(new LlmGatewayOptions
        {
            ProviderCatalog = new ProviderCatalogOptions { Deployments = [.. deployments] }
        }));
        services.AddLlmProviders();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void NewProviderInCatalogConfig_GetsKeyedChatClient_WithZeroCode()
    {
        using var provider = Build(new ProviderDeploymentConfig
        {
            Name = "acme-dep",
            ModelName = "acme-model",
            Provider = "AcmeAI",
            Endpoint = "http://127.0.0.1:9999/v1",
            Model = "acme-chat",
            MaxContextTokens = 8192,
            Enabled = true
        });

        var client = provider.GetKeyedService<IChatClient>("AcmeAI");
        client.Should().NotBeNull("a config-only provider must be routable without code changes");
    }

    [Fact]
    public void DisabledOrEndpointlessDeployments_AreNotRegistered()
    {
        using var provider = Build(
            new ProviderDeploymentConfig { Name = "off", Provider = "DisabledAI", Endpoint = "http://x/v1", Enabled = false },
            new ProviderDeploymentConfig { Name = "local", Provider = "NoEndpointAI", Endpoint = "", Enabled = true });

        provider.GetKeyedService<IChatClient>("DisabledAI").Should().BeNull();
        provider.GetKeyedService<IChatClient>("NoEndpointAI").Should().BeNull();
    }

    [Fact]
    public void CodeRegisteredProviders_AreNotDoubleRegistered()
    {
        // "DerpYardly" is registered in code; a catalog entry with the same provider
        // key must not shadow it. Resolving must not throw.
        using var provider = Build(new ProviderDeploymentConfig
        {
            Name = "derpyardly-dupe",
            Provider = "DerpYardly",
            Endpoint = "http://127.0.0.1:1234/v1",
            Model = "x",
            Enabled = true
        });

        var client = provider.GetKeyedService<IChatClient>("DerpYardly");
        client.Should().NotBeNull();
    }
}
