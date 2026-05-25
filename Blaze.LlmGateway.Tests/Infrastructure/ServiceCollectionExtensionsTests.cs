namespace Blaze.LlmGateway.Tests.Infrastructure;

using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.Provider;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Blaze.LlmGateway.LocalInference;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ServiceCollectionExtensionsTests
{
    private IServiceCollection _services;

    public ServiceCollectionExtensionsTests()
    {
        _services = new ServiceCollection();
    }

    [Fact]
    public void AddCodebrewRouterProvider_ReturnsBuilder()
    {
        // Arrange
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11434",
            TestMode = true
        };

        // Act
        var result = _services.AddCodebrewRouterProvider(options);

        // Assert
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void AddCodebrewRouterProvider_WithNullServices_ThrowsArgumentNullException()
    {
        // Arrange
        IServiceCollection services = null!;
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11434"
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            services.AddCodebrewRouterProvider(options));
    }

    [Fact]
    public void AddCodebrewRouterProvider_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        CodebrewRouterProviderOptions options = null!;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            _services.AddCodebrewRouterProvider(options));
    }

    [Fact]
    public void AddCodebrewRouterProvider_RegistersOptions()
    {
        // Arrange
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11434",
            TestMode = true
        };

        // Act
        _services
            .AddCodebrewRouterProvider(options)
            .Build();
        var sp = _services.BuildServiceProvider();

        // Assert - Verify the options were registered
        var registeredOptions = sp.GetService<CodebrewRouterProviderOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Equal("http://127.0.0.1:11434", registeredOptions.LocalEndpoint);
        Assert.True(registeredOptions.TestMode);
    }

    [Fact]
    public void AddCodebrewRouterProvider_AllowsFluentChaining()
    {
        // Arrange
        var options = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11434",
            TestMode = true
        };

        // Act
        var result = _services
            .AddCodebrewRouterProvider(options)
            .WithHealthChecks()
            .WithDiscovery();

        // Assert
        Assert.NotNull(result);
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(result);
    }

    [Fact]
    public void AddCodebrewRouterProvider_MultipleCallsAllowed()
    {
        // Arrange
        var options1 = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11434",
            TestMode = true
        };
        var options2 = new CodebrewRouterProviderOptions
        {
            LocalEndpoint = "http://127.0.0.1:11435",
            TestMode = true
        };

        // Act
        var builder1 = _services.AddCodebrewRouterProvider(options1);
        var builder2 = _services.AddCodebrewRouterProvider(options2);

        // Assert
        Assert.NotNull(builder1);
        Assert.NotNull(builder2);
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(builder1);
        Assert.IsAssignableFrom<ICodebrewRouterProviderBuilder>(builder2);
    }

    [Fact]
    public void AddLlmInfrastructure_WhenTaskClassificationDisabled_RegistersKeywordClassifier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModelAvailabilityRegistry, AlwaysAvailableRegistry>();
        services.Configure<LlmGatewayOptions>(options =>
        {
            options.TaskClassification.Enabled = false;
        });

        services.AddLlmInfrastructure();
        var sp = services.BuildServiceProvider();

        var classifier = sp.GetRequiredService<ITaskClassifier>();

        Assert.IsType<KeywordTaskClassifier>(classifier);
    }

    private sealed class AlwaysAvailableRegistry : IModelAvailabilityRegistry
    {
        public IReadOnlyList<AvailableModel> GetModels(bool includeUnavailable = false) => [];
        public AvailableModel? FindModel(string modelId, bool includeUnavailable = false) => null;
        public bool IsProviderAvailable(string provider) => true;
        public string? GetProviderError(string provider) => null;
    }
}
