namespace Blaze.LlmGateway.Tests.Provider;

using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.Provider;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Blaze.LlmGateway.LocalInference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class CodebrewRouterProviderIntegrationTests
{
    private CodebrewRouterProviderOptions CreateValidOptions()
        => new()
        {
            LocalEndpoint = "http://localhost:11434",
            RemoteDiscoveryEndpoint = "http://localhost:5273",
            HealthChecksEnabled = true,
            TestMode = false
        };

    [Fact]
    public void Mobile_Scenario_MinimalConfiguration_Registers()
    {
        // Arrange: Mobile (MAUI) with just LocalEndpoint
        var services = new ServiceCollection();
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://192.168.1.100:11434"
        };

        // Act: Simple registration
        services.AddCodebrewRouterProvider(options).Build();
        var sp = services.BuildServiceProvider();

        // Assert: Builder completed without error and options are registered
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Equal("http://192.168.1.100:11434", registeredOptions.LocalEndpoint);
    }

    [Fact]
    public void Desktop_Scenario_FullChain_Completes()
    {
        // Arrange: Desktop with full feature stack
        var services = new ServiceCollection();
        var options = CreateValidOptions();

        // Act: Builder chain
        services
            .AddCodebrewRouterProvider(options)
            .WithHealthChecks()
            .WithDiscovery()
            .WithRouting()
            .Build();

        var sp = services.BuildServiceProvider();

        // Assert: Options registered and no exceptions thrown
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Equal("http://localhost:11434", registeredOptions.LocalEndpoint);
    }

    [Fact]
    public void Aspire_Scenario_WithConfiguration_RegistersOptions()
    {
        // Arrange: Aspire-style configuration
        var services = new ServiceCollection();
        var options = CreateValidOptions();

        // Act: Use new provider with builder
        services
            .AddCodebrewRouterProvider(options)
            .WithHealthChecks()
            .WithDiscovery()
            .Build();

        var sp = services.BuildServiceProvider();

        // Assert: Options preserved
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.NotNull(registeredOptions.RemoteDiscoveryEndpoint);
    }

    [Fact]
    public void CustomRoutingStrategy_Registration_Succeeds()
    {
        // Arrange: Custom strategy injection
        var services = new ServiceCollection();
        var options = CreateValidOptions();

        // Act: Register custom strategy
        services
            .AddCodebrewRouterProvider(options)
            .WithRoutingStrategy<CustomTestRoutingStrategy>()
            .Build();

        var sp = services.BuildServiceProvider();

        // Assert: No exceptions; options registered
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
    }

    [Fact]
    public void HealthCheckDisabled_ViaOptions_DoesNotThrow()
    {
        // Arrange: Options with health check disabled
        var services = new ServiceCollection();
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://localhost:11434",
            HealthChecksEnabled = false
        };

        // Act
        services.AddCodebrewRouterProvider(options).Build();
        var sp = services.BuildServiceProvider();

        // Assert: Options registered despite health check disabled
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.False(registeredOptions.HealthChecksEnabled);
    }

    [Fact]
    public void DegradedState_MobileNoRemoteDiscovery_RegistersSuccessfully()
    {
        // Arrange: Mobile with only local endpoint (no remote discovery)
        var services = new ServiceCollection();
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://localhost:11434",
            RemoteDiscoveryEndpoint = null  // No remote
        };

        // Act
        services.AddCodebrewRouterProvider(options).Build();
        var sp = services.BuildServiceProvider();

        // Assert: Registration succeeds; options reflect degraded state
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Null(registeredOptions.RemoteDiscoveryEndpoint);
    }

    /// <summary>
    /// Test-only routing strategy for validation.
    /// </summary>
    private class CustomTestRoutingStrategy : IRoutingStrategy
    {
        public Task<RouteDestination> ResolveAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
            => Task.FromResult(RouteDestination.LocalGemma);
    }
}

