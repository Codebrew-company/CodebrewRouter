using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class LlmGatewayOptionsTests
{
    [Fact]
    public void OllamaLocalOptions_DefaultsToRemoteGemmaRouter()
    {
        var options = new LlmGatewayOptions();

        Assert.Equal("http://192.168.16.12:11434", options.Providers.OllamaLocal.BaseUrl);
        Assert.Equal("gemma4:e4b", options.Providers.OllamaLocal.Model);
    }

    [Fact]
    public void FoundryLocalOptions_DefaultsToDisabledPhiMiniAlias()
    {
        var options = new LlmGatewayOptions();

        Assert.False(options.Providers.FoundryLocal.Enabled);
        Assert.Equal("http://127.0.0.1:58484", options.Providers.FoundryLocal.Endpoint);
        Assert.Equal("phi-4-mini", options.Providers.FoundryLocal.Model);
    }

    [Fact]
    public void LmStudioOptions_DefaultsToLocalOpenAiCompatibleEndpoint()
    {
        var options = new LlmGatewayOptions();

        Assert.Equal("http://192.168.16.56:1234/v1", options.Providers.LmStudio.Endpoint);
        Assert.Equal("local-model", options.Providers.LmStudio.Model);
        Assert.Equal("notneeded", options.Providers.LmStudio.ApiKey);
    }

    [Fact]
    public void FoundryConfigurationAliases_MapsCopilotEnvironmentVariables_WhenGatewayKeysAreMissing()
    {
        const string endpoint = "https://example-foundry.openai.azure.com/";
        const string responsesEndpoint = "https://example-foundry.services.ai.azure.com/api/projects/example/openai/v1/responses";
        const string apiKey = "test-api-key";
        const string model = "gpt-4o-test";

        Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_AZURE_BASE_URL", endpoint);
        Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_RESPONSES_ENDPOINT", responsesEndpoint);
        Environment.SetEnvironmentVariable("COPILOT_AZURE_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_DEFAULT_MODEL", model);

        try
        {
            var configuration = new ConfigurationManager();

            Blaze.LlmGateway.Api.FoundryConfigurationAliases.AddFoundryEnvironmentAliases(configuration);

            Assert.Equal(endpoint, configuration["LlmGateway:Providers:AzureFoundry:Endpoint"]);
            Assert.Equal(responsesEndpoint, configuration["LlmGateway:Providers:AzureFoundry:ResponsesEndpoint"]);
            Assert.Equal(apiKey, configuration["LlmGateway:Providers:AzureFoundry:ApiKey"]);
            Assert.Equal(model, configuration["LlmGateway:Providers:AzureFoundry:Model"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_AZURE_BASE_URL", null);
            Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_RESPONSES_ENDPOINT", null);
            Environment.SetEnvironmentVariable("COPILOT_AZURE_API_KEY", null);
            Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_DEFAULT_MODEL", null);
        }
    }

    [Fact]
    public void FoundryConfigurationAliases_DoesNotOverrideExplicitGatewayKeys()
    {
        Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_AZURE_BASE_URL", "https://alias.example/");

        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:Providers:AzureFoundry:Endpoint"] = "https://explicit.example/"
            });

            Blaze.LlmGateway.Api.FoundryConfigurationAliases.AddFoundryEnvironmentAliases(configuration);

            Assert.Equal("https://explicit.example/", configuration["LlmGateway:Providers:AzureFoundry:Endpoint"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_FOUNDRY_AZURE_BASE_URL", null);
        }
    }

    [Fact]
    public void FoundryConfigurationAliases_MapsFoundryLocalConnectionString_WhenGatewayKeysAreMissing()
    {
        const string connectionString = "Endpoint=http://127.0.0.1:60123/;DeploymentId=foundryLocalChat;ApiKey=notneeded";

        Environment.SetEnvironmentVariable("ConnectionStrings__foundryLocalChat", connectionString);

        try
        {
            var configuration = new ConfigurationManager();

            Blaze.LlmGateway.Api.FoundryConfigurationAliases.AddFoundryEnvironmentAliases(configuration);

            Assert.Equal("http://127.0.0.1:60123/", configuration["LlmGateway:Providers:FoundryLocal:Endpoint"]);
            Assert.Equal("notneeded", configuration["LlmGateway:Providers:FoundryLocal:ApiKey"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__foundryLocalChat", null);
        }
    }

    [Fact]
    public void FoundryConfigurationAliases_MapsAspireFoundryLocalConnectionString_OverridesAppsettings()
    {
        // Exact format Aspire's Foundry Local resource emits.
        const string connectionString = "Endpoint=http://127.0.0.1:55428/v1;Key=OPENAI_API_KEY;Deployment=foundryLocalChat;Model=Phi-4-mini-instruct-cuda-gpu:5";

        Environment.SetEnvironmentVariable("ConnectionStrings__foundryLocalChat", connectionString);

        try
        {
            var configuration = new ConfigurationManager();
            // Pre-populate stale appsettings-like values; Aspire connection string must override them.
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:Providers:FoundryLocal:Endpoint"] = "http://127.0.0.1:58484",
                ["LlmGateway:Providers:FoundryLocal:Model"] = "stale-model",
            });

            Blaze.LlmGateway.Api.FoundryConfigurationAliases.AddFoundryEnvironmentAliases(configuration);

            Assert.Equal("http://127.0.0.1:55428/v1", configuration["LlmGateway:Providers:FoundryLocal:Endpoint"]);
            Assert.Equal("OPENAI_API_KEY", configuration["LlmGateway:Providers:FoundryLocal:ApiKey"]);
            Assert.Equal("Phi-4-mini-instruct-cuda-gpu:5", configuration["LlmGateway:Providers:FoundryLocal:Model"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__foundryLocalChat", null);
        }
    }

    [Fact]
    public async Task ModelCatalogService_IncludesConfiguredCodebrewBackingProviders()
    {
        var options = new LlmGatewayOptions
        {
            Providers =
            {
                GithubModels = { ApiKey = "test-token" }
            }
        };
        var service = CreateModelCatalogService(options);

        var models = await service.GetAvailableModelsAsync();

        Assert.Contains(models, model => model.Provider == "CodebrewRouter" && model.Id == "codebrewRouter");
        Assert.DoesNotContain(models, model => model.Provider == "FoundryLocal");
        Assert.Contains(models, model => model.Provider == "OllamaLocal" && model.Id == "gemma4:e4b");
        Assert.Contains(models, model => model.Provider == "LmStudio" && model.Id == "local-model");
        Assert.Contains(models, model => model.Provider == "GithubModels" && model.Id == "gpt-4o-mini");
    }

    [Fact]
    public async Task ModelCatalogService_SkipsGithubModels_WhenApiKeyMissing()
    {
        var service = CreateModelCatalogService(new LlmGatewayOptions());

        var models = await service.GetAvailableModelsAsync();

        Assert.DoesNotContain(models, model => model.Provider == "GithubModels");
    }

    [Fact]
    public async Task ModelCatalogService_SkipsUnavailableModels_FromAvailabilityRegistry()
    {
        var registry = new ModelAvailabilityRegistry();
        var checkedAt = DateTimeOffset.UtcNow;
        registry.UpdateSnapshot(
            [
                new AvailableModel("gpt-4o", "AzureFoundry", "openai", "configured", Enabled: true, LastCheckedUtc: checkedAt),
                new AvailableModel("Phi-4-mini-instruct-cuda-gpu:5", "FoundryLocal", "openai", "configured", Enabled: false, ErrorMessage: "Connection refused", LastCheckedUtc: checkedAt),
                new AvailableModel("codebrewRouter", "CodebrewRouter", "codebrew", "virtual", Enabled: true, LastCheckedUtc: checkedAt)
            ],
            [
                new ProviderAvailabilitySnapshot("AzureFoundry", true, null, checkedAt),
                new ProviderAvailabilitySnapshot("FoundryLocal", false, "Connection refused", checkedAt),
                new ProviderAvailabilitySnapshot("CodebrewRouter", true, null, checkedAt)
            ]);

        var service = CreateModelCatalogService(new LlmGatewayOptions(), registry);

        var models = await service.GetAvailableModelsAsync();

        Assert.Contains(models, model => model.Provider == "AzureFoundry");
        Assert.Contains(models, model => model.Provider == "CodebrewRouter");
        Assert.DoesNotContain(models, model => model.Provider == "FoundryLocal");
    }

    [Fact]
    public void AddLlmProviders_UsesFoundryResponsesClient_WhenResponsesEndpointIsConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new LlmGatewayOptions
        {
            Providers =
            {
                AzureFoundry =
                {
                    ApiKey = "test-key",
                    Model = "gpt-5.4",
                    ResponsesEndpoint = "https://example-foundry.services.ai.azure.com/api/projects/example/openai/v1/responses"
                },
                GithubModels = { ApiKey = "test-key" }
            }
        }));
        services.AddLlmProviders();
        services.AddSingleton(new Mock<ITokenCounter>().Object);
        services.AddSingleton(new Mock<IContextCompactor>().Object);
        services.AddSingleton(Options.Create(new ContextSizingOptions()));

        using var provider = services.BuildServiceProvider();

        var chatClient = provider.GetRequiredKeyedService<IChatClient>("AzureFoundry");

        Assert.NotNull(chatClient);
    }

    private static ModelCatalogService CreateModelCatalogService(LlmGatewayOptions options, ModelAvailabilityRegistry? registry = null)
    {
        return new ModelCatalogService(
            registry ?? CreateRegistryFromOptions(options),
            new Mock<ILogger<ModelCatalogService>>().Object);
    }

    private static ModelAvailabilityRegistry CreateRegistryFromOptions(LlmGatewayOptions options)
    {
        var registry = new ModelAvailabilityRegistry();
        var checkedAt = DateTimeOffset.UtcNow;
        var models = new List<AvailableModel>();
        var providers = new List<ProviderAvailabilitySnapshot>();

        if (!string.IsNullOrWhiteSpace(options.Providers.AzureFoundry.Endpoint) &&
            !string.IsNullOrWhiteSpace(options.Providers.AzureFoundry.Model))
        {
            models.Add(new AvailableModel(options.Providers.AzureFoundry.Model, "AzureFoundry", "openai", "configured", options.Providers.AzureFoundry.Endpoint, Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("AzureFoundry", true, null, checkedAt));
        }

        if (options.Providers.FoundryLocal.Enabled &&
            !string.IsNullOrWhiteSpace(options.Providers.FoundryLocal.Endpoint) &&
            !string.IsNullOrWhiteSpace(options.Providers.FoundryLocal.Model))
        {
            models.Add(new AvailableModel(options.Providers.FoundryLocal.Model, "FoundryLocal", "openai", "configured", options.Providers.FoundryLocal.Endpoint, Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("FoundryLocal", true, null, checkedAt));
        }

        if (!string.IsNullOrWhiteSpace(options.Providers.GithubModels.Endpoint) &&
            !string.IsNullOrWhiteSpace(options.Providers.GithubModels.Model) &&
            !string.IsNullOrWhiteSpace(options.Providers.GithubModels.ApiKey))
        {
            models.Add(new AvailableModel(options.Providers.GithubModels.Model, "GithubModels", "github", "configured", options.Providers.GithubModels.Endpoint, Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("GithubModels", true, null, checkedAt));
        }

        if (!string.IsNullOrWhiteSpace(options.Providers.OllamaLocal.BaseUrl) &&
            !string.IsNullOrWhiteSpace(options.Providers.OllamaLocal.Model))
        {
            models.Add(new AvailableModel(options.Providers.OllamaLocal.Model, "OllamaLocal", "ollama", "configured", options.Providers.OllamaLocal.BaseUrl, Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("OllamaLocal", true, null, checkedAt));
        }

        if (!string.IsNullOrWhiteSpace(options.Providers.LmStudio.Endpoint) &&
            !string.IsNullOrWhiteSpace(options.Providers.LmStudio.Model))
        {
            models.Add(new AvailableModel(options.Providers.LmStudio.Model, "LmStudio", "lmstudio", "configured", options.Providers.LmStudio.Endpoint, Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("LmStudio", true, null, checkedAt));
        }

        if (options.CodebrewRouter.Enabled && !string.IsNullOrWhiteSpace(options.CodebrewRouter.ModelId))
        {
            models.Add(new AvailableModel(options.CodebrewRouter.ModelId, "CodebrewRouter", "codebrew", "virtual", Enabled: true, LastCheckedUtc: checkedAt));
            providers.Add(new ProviderAvailabilitySnapshot("CodebrewRouter", true, null, checkedAt));
        }

        registry.UpdateSnapshot(models, providers);
        return registry;
    }
}
