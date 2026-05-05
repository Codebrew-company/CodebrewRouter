namespace Blaze.LlmGateway.Tests.Provider;

using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Infrastructure.Provider;
using Blaze.LlmGateway.LocalInference;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class CodebrewRouterProviderBuilderTests
{
    private IServiceCollection CreateServiceCollection()
        => new ServiceCollection();

    private CodebrewRouterProviderOptions CreateValidOptions()
        => new()
        {
            LocalEndpoint = "http://localhost:11434",
            RemoteDiscoveryEndpoint = "http://localhost:5273"
        };

    private CodebrewRouterProviderOptions CreateValidTestOptions()
    {
        var opts = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://localhost:11434",
            RemoteDiscoveryEndpoint = "http://localhost:5273"
        };
        opts.TestMode = true;
        return opts;
    }

    [Fact]
    public void WithHealthChecks_ReturnsSelf_AllowsChaining()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = CreateValidTestOptions();

        // Act
        var builder = services.AddCodebrewRouterProvider(options);
        var result = builder.WithHealthChecks();

        // Assert
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void WithDiscovery_ReturnsSelf_AllowsChaining()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = CreateValidTestOptions();

        // Act
        var builder = services.AddCodebrewRouterProvider(options);
        var result = builder.WithDiscovery();

        // Assert
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void WithRouting_ReturnsSelf_AllowsChaining()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = CreateValidTestOptions();

        // Act
        var builder = services.AddCodebrewRouterProvider(options);
        var result = builder.WithRouting();

        // Assert
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void FluentChain_AllMethodsCallable()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = CreateValidTestOptions();

        // Act
        var result = services
            .AddCodebrewRouterProvider(options)
            .WithHealthChecks()
            .WithDiscovery()
            .WithRouting();

        // Assert
        Assert.NotNull(result);
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void AddCodebrewRouterProvider_WithValidOptions_RegistersOptions()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = CreateValidTestOptions();

        // Act
        services
            .AddCodebrewRouterProvider(options)
            .Build();
        
        var sp = services.BuildServiceProvider();

        // Assert - Verify the options were registered
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Equal("http://localhost:11434", registeredOptions.LocalEndpoint);
        Assert.True(registeredOptions.TestMode);
    }

    [Fact]
    public void Build_WithInvalidOptions_ThrowsValidationException()
    {
        // Arrange
        var services = CreateServiceCollection();
        var options = new CodebrewRouterProviderOptions 
        { 
            LocalEndpoint = "", 
            TestMode = true 
        };

        // Act & Assert
        var builder = services.AddCodebrewRouterProvider(options);
        Assert.Throws<CodebrewRouterProviderValidationException>(() => builder.Build());
    }

    [Fact]
    public void AddCodebrewRouterProvider_WithNullServices_ThrowsArgumentNullException()
    {
        // Arrange
        IServiceCollection services = null!;
        var options = CreateValidOptions();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            services.AddCodebrewRouterProvider(options));
    }

    [Fact]
    public void AddCodebrewRouterProvider_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var services = CreateServiceCollection();
        CodebrewRouterProviderOptions options = null!;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            services.AddCodebrewRouterProvider(options));
    }

}
