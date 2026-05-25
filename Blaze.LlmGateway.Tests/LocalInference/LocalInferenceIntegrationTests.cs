using System.Net;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Provider;
using Blaze.LlmGateway.LocalInference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.LocalInference.Tests;

/// <summary>
/// Integration tests for local inference DI registration and end-to-end scenarios.
/// </summary>
public class LocalInferenceIntegrationTests
{
    private static IServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new LoggerFactory());
        services.AddLogging();
        return services;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Blaze.LlmGateway.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    [Fact]
    public void AddLocalInferenceServices_RegistersAllRequiredServices()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "true" },
                { "LlmGateway:LocalInference:ModelPath", "/models/gemma-2b.gguf" },
                { "LlmGateway:LocalInference:CacheDirectory", ".llm-cache" },
                { "LlmGateway:LocalInference:Temperature", "0.7" },
                { "LlmGateway:LocalInference:TopP", "0.9" }
            })
            .Build();

        // Act
        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Assert - All required services are resolvable
        var options = sp.GetRequiredService<LocalInferenceOptions>();
        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.Equal("/models/gemma-2b.gguf", options.ModelPath);

        var modelProvider = sp.GetRequiredService<IModelDistributionProvider>();
        Assert.NotNull(modelProvider);
        Assert.IsType<RuntimeDownloadModelProvider>(modelProvider);

        var factory = sp.GetRequiredService<HybridRoutingStrategyFactory>();
        Assert.NotNull(factory);

        var localGemmaChatClient = sp.GetKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma");
        Assert.NotNull(localGemmaChatClient);
    }

    [Fact]
    public void AddCodebrewRouterLocalProvider_RegistersLocalGemmaAndProviderOptions()
    {
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:LocalInference:Enabled"] = "true",
                ["LlmGateway:LocalInference:ModelPath"] = @"C:\models\gemma-4-e4b-it-q4_k_m.gguf",
                ["LlmGateway:LocalInference:CacheDirectory"] = ".llm-cache",
                ["LlmGateway:LocalInference:MaxContextTokens"] = "8192",
                ["LlmGateway:LocalInference:ThreadCount"] = "4",
                ["LlmGateway:Providers:OllamaRouter:PrimaryEndpoint"] = "http://127.0.0.1:11434"
            })
            .Build();

        services.AddCodebrewRouterLocalProvider(configuration);
        var sp = services.BuildServiceProvider();

        var providerOptions = sp.GetRequiredService<CodebrewRouterProviderOptions>();
        Assert.Equal(@"C:\models\gemma-4-e4b-it-q4_k_m.gguf", providerOptions.LocalModelPath);
        Assert.Equal(".llm-cache", providerOptions.CacheDirectory);
        Assert.Equal(8192, providerOptions.LocalMaxContextTokens);
        Assert.Equal(4, providerOptions.LocalThreadCount);

        var localGemma = sp.GetKeyedService<IChatClient>("LocalGemma");
        Assert.IsType<LocalGemmaChatClient>(localGemma);
    }

    [Fact]
    public void AddLocalInferenceServices_UsesDefaultOptionsWhenConfigurationMissing()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()) // Empty config
            .Build();

        // Act
        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Assert - Default options are used
        var options = sp.GetRequiredService<LocalInferenceOptions>();
        Assert.True(options.Enabled); // Default is true
        Assert.Empty(options.ModelPath); // Default is empty
        Assert.Equal(".llm-cache", options.CacheDirectory);
        Assert.Equal(3600, options.DownloadTimeoutSeconds);
        Assert.Equal(5, options.CircuitBreakerCooldownMinutes);
    }

    [Fact]
    public void AddLocalInferenceServices_LocalGemmaChatClientReturnsSameInstanceOnMultipleResolves()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "true" },
                { "LlmGateway:LocalInference:ModelPath", "/models/gemma-2b.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act
        var client1 = sp.GetKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma");
        var client2 = sp.GetKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma");

        // Assert - Same instance (singleton)
        Assert.Same(client1, client2);
    }

    [Fact]
    public void AddLocalInferenceServices_ConfiguresLocalGemmaWithModelPath()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:ModelPath", "E:/models/gemma-local.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act
        var client = sp.GetRequiredKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma");

        // Assert
        var localClient = Assert.IsType<LocalGemmaChatClient>(client);
        Assert.Equal("E:/models/gemma-local.gguf", localClient.ModelPath);
    }

    [Fact]
    public void AddLocalInferenceServices_ModelProviderReturnsSameInstanceOnMultipleResolves()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "true" },
                { "LlmGateway:LocalInference:ModelPath", "/models/gemma-2b.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act
        var provider1 = sp.GetRequiredService<IModelDistributionProvider>();
        var provider2 = sp.GetRequiredService<IModelDistributionProvider>();

        // Assert - Same instance (singleton)
        Assert.Same(provider1, provider2);
    }

    [Fact]
    public void AddLocalInferenceServices_HttpClientTimeoutMatchesDownloadTimeout()
    {
        // Arrange
        var services = CreateServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:DownloadTimeoutSeconds", "7200" }
            })
            .Build();

        // Act
        services.AddLocalInferenceServices(config);
        var sp = services.BuildServiceProvider();

        // Assert
        var options = sp.GetRequiredService<LocalInferenceOptions>();
        Assert.Equal(7200, options.DownloadTimeoutSeconds);

        // Verify HttpClient is configured with the timeout
        var httpClient = sp.GetRequiredService<HttpClient>();
        Assert.Equal(TimeSpan.FromSeconds(7200), httpClient.Timeout);
    }

    [Fact]
    public void HybridRoutingStrategyFactory_CreatesStrategy()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "true" },
                { "LlmGateway:LocalInference:ModelPath", "/models/gemma-2b.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act
        var factory = sp.GetRequiredService<HybridRoutingStrategyFactory>();
        var strategy = factory.CreateStrategy();

        // Assert - Strategy is created
        Assert.NotNull(strategy);
    }

    [Fact]
    public void LocalInferenceOptions_BindsFromConfiguration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "false" },
                { "LlmGateway:LocalInference:ModelPath", "https://example.com/model.gguf" },
                { "LlmGateway:LocalInference:CacheDirectory", "/custom/cache" },
                { "LlmGateway:LocalInference:EnableChecksumValidation", "false" },
                { "LlmGateway:LocalInference:DownloadTimeoutSeconds", "1800" },
                { "LlmGateway:LocalInference:CircuitBreakerCooldownMinutes", "10" },
                { "LlmGateway:LocalInference:ThreadCount", "4" },
                { "LlmGateway:LocalInference:MaxContextTokens", "4096" },
                { "LlmGateway:LocalInference:Temperature", "0.5" },
                { "LlmGateway:LocalInference:TopP", "0.8" },
                { "LlmGateway:LocalInference:SystemPrompt", "Custom system prompt" },
                { "LlmGateway:LocalInference:WarmupEnabled", "true" },
                { "LlmGateway:LocalInference:WarmupPrompt", "ready" },
                { "LlmGateway:LocalInference:WarmupMaxOutputTokens", "1" },
                { "LlmGateway:LocalInference:WarmupTimeoutSeconds", "120" },
                { "LlmGateway:LocalInference:BlockStartupUntilWarm", "true" }
            })
            .Build();

        // Act
        var options = configuration
            .GetSection("LlmGateway:LocalInference")
            .Get<LocalInferenceOptions>();

        // Assert - All properties bound correctly
        Assert.NotNull(options);
        Assert.False(options.Enabled);
        Assert.Equal("https://example.com/model.gguf", options.ModelPath);
        Assert.Equal("/custom/cache", options.CacheDirectory);
        Assert.False(options.EnableChecksumValidation);
        Assert.Equal(1800, options.DownloadTimeoutSeconds);
        Assert.Equal(10, options.CircuitBreakerCooldownMinutes);
        Assert.Equal(4, options.ThreadCount);
        Assert.Equal(4096, options.MaxContextTokens);
        Assert.Equal(0.5f, options.Temperature);
        Assert.Equal(0.8f, options.TopP);
        Assert.Equal("Custom system prompt", options.SystemPrompt);
        Assert.True(options.WarmupEnabled);
        Assert.Equal("ready", options.WarmupPrompt);
        Assert.Equal(1, options.WarmupMaxOutputTokens);
        Assert.Equal(120, options.WarmupTimeoutSeconds);
        Assert.True(options.BlockStartupUntilWarm);
    }

    [Fact]
    public void ApiDefaultLocalInferenceConfig_UsesGemma4RemoteBootstrap()
    {
        const string expectedModelPath =
            "https://huggingface.co/lm-kit/gemma-4-e4b-instruct-lmk/resolve/main/Gemma-4-E4B-It-7.5B-Q4_K_M.lmk";

        var root = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "Blaze.LlmGateway.Api", "appsettings.json"))
            .Build();

        var options = configuration
            .GetSection("LlmGateway:LocalInference")
            .Get<LocalInferenceOptions>() ?? new LocalInferenceOptions();

        Assert.True(options.WarmupEnabled);
        Assert.Equal(expectedModelPath, options.ModelPath);
        Assert.Equal(".llm-cache", options.CacheDirectory);
        Assert.Equal(3600, options.DownloadTimeoutSeconds);
        Assert.True(options.BlockStartupUntilWarm);
    }

    [Fact]
    public void RuntimeDownloadModelProvider_IsRegisteredAsSingleton()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:ModelPath", "/models/test.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act & Assert - Verify singleton registration
        var prov1 = sp.GetRequiredService<IModelDistributionProvider>();
        var prov2 = sp.GetRequiredService<IModelDistributionProvider>();
        
        // Within same scope, singletons are same instance
        Assert.Same(prov1, prov2);
    }

    [Fact]
    public void LocalGemmaChatClient_IsRegisteredAsKeyedService()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:ModelPath", "/models/test.gguf" }
            })
            .Build();

        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Act
        var client = sp.GetKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma");

        // Assert - Keyed service is registered and returns LocalGemmaChatClient
        Assert.NotNull(client);
        Assert.IsType<LocalGemmaChatClient>(client);
    }

    [Fact]
    public void ServiceCollection_BuildsSuccessfullyWithAllRegistrations()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", "true" },
                { "LlmGateway:LocalInference:ModelPath", "/models/gemma-2b.gguf" }
            })
            .Build();

        // Act & Assert - No exception should be thrown
        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp);

        // Verify all key services can be resolved
        Assert.NotNull(sp.GetRequiredService<LocalInferenceOptions>());
        Assert.NotNull(sp.GetRequiredService<IModelDistributionProvider>());
        Assert.NotNull(sp.GetRequiredService<HybridRoutingStrategyFactory>());
        Assert.NotNull(sp.GetKeyedService<Microsoft.Extensions.AI.IChatClient>("LocalGemma"));
    }

    [Fact]
    public void AddLocalInferenceServices_ConfiguresHttpClientWithTimeoutConfiguration()
    {
        // Arrange
        var services = CreateServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:DownloadTimeoutSeconds", "9000" }
            })
            .Build();

        // Act
        services.AddLocalInferenceServices(configuration);
        var sp = services.BuildServiceProvider();

        // Assert - HttpClient exists and is configured with timeout
        var httpClient = sp.GetRequiredService<HttpClient>();
        Assert.NotNull(httpClient);
        Assert.Equal(TimeSpan.FromSeconds(9000), httpClient.Timeout);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LocalInferenceOptions_BindsEnableAndChecksumValidationBooleans(
        bool enabled,
        bool checksumValidation)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "LlmGateway:LocalInference:Enabled", enabled.ToString() },
                { "LlmGateway:LocalInference:EnableChecksumValidation", checksumValidation.ToString() }
            })
            .Build();

        // Act
        var options = configuration
            .GetSection("LlmGateway:LocalInference")
            .Get<LocalInferenceOptions>() ?? new LocalInferenceOptions();

        // Assert
        Assert.Equal(enabled, options.Enabled);
        Assert.Equal(checksumValidation, options.EnableChecksumValidation);
    }
}
