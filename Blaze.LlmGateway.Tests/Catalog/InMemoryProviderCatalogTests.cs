using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;

namespace Blaze.LlmGateway.Tests.Catalog;

public class InMemoryProviderCatalogTests
{
    [Fact]
    public void EmptyCatalog_ReturnsNoDeployments()
    {
        var opts = new ProviderCatalogOptions();
        var catalog = new InMemoryProviderCatalog(opts);

        Assert.Empty(catalog.GetAllDeployments());
        Assert.Empty(catalog.GetDeploymentsForModel("anything"));
        Assert.Null(catalog.GetRoute("anything"));
        Assert.Null(catalog.GetDeployment("anything"));
    }

    [Fact]
    public void PopulatedFromConfig_ReturnsAllDeployments()
    {
        var opts = CreateSampleOptions();
        var catalog = new InMemoryProviderCatalog(opts);

        var all = catalog.GetAllDeployments();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetDeploymentsForModel_ReturnsCorrectDeployments()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        var gptDeploys = catalog.GetDeploymentsForModel("gpt-4o-mini");
        Assert.Single(gptDeploys);
        Assert.Equal("azure-gpt4-mini", gptDeploys[0].Name);

        var deepseekDeploys = catalog.GetDeploymentsForModel("deepseek-v4-pro");
        Assert.Single(deepseekDeploys);
        Assert.Equal("opencode-deepseek", deepseekDeploys[0].Name);
    }

    [Fact]
    public void GetDeploymentsForModel_UnknownModel_ReturnsEmpty()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());
        Assert.Empty(catalog.GetDeploymentsForModel("nonexistent-model"));
    }

    [Fact]
    public void GetRoute_ReturnsConfiguredRoute()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        var route = catalog.GetRoute("gpt-4o-mini");
        Assert.NotNull(route);
        Assert.Equal("latency", route.Strategy);
        Assert.Equal(["azure-gpt4-mini"], route.Deployments);
        Assert.Equal(["opencode-deepseek"], route.Fallbacks);
    }

    [Fact]
    public void GetRoute_UnknownModel_ReturnsNull()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());
        Assert.Null(catalog.GetRoute("nonexistent"));
    }

    [Fact]
    public void GetRoute_WithoutExplicitStrategy_UsesDefault()
    {
        var opts = CreateSampleOptions();
        opts.ModelRouting["custom-model"] = new ModelRouteConfig
        {
            Deployments = ["azure-gpt4-mini"]
        };
        var catalog = new InMemoryProviderCatalog(opts);

        var route = catalog.GetRoute("custom-model");
        Assert.NotNull(route);
        Assert.Equal("round_robin", route.Strategy); // default strategy
    }

    [Fact]
    public void GetDeployment_ByName_ReturnsCorrectDeployment()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        var dep = catalog.GetDeployment("azure-gpt4-mini");
        Assert.NotNull(dep);
        Assert.Equal("AzureFoundry", dep.Provider);
        Assert.Equal(3, dep.Weight);
    }

    [Fact]
    public void GetDeployment_UnknownName_ReturnsNull()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());
        Assert.Null(catalog.GetDeployment("nonexistent"));
    }

    [Fact]
    public void IsHealthy_NoHealthData_ReturnsTrue()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());
        Assert.True(catalog.IsHealthy("anything"));
    }

    [Fact]
    public void ReportHealth_MarksUnhealthyAfterThreeFailures()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        Assert.True(catalog.IsHealthy("azure-gpt4-mini"));

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.True(catalog.IsHealthy("azure-gpt4-mini")); // only 1 failure

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.True(catalog.IsHealthy("azure-gpt4-mini")); // only 2 failures

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.False(catalog.IsHealthy("azure-gpt4-mini")); // 3 failures → unhealthy
    }

    [Fact]
    public void ReportHealth_HealthyReport_ResetsFailureCount()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: true); // recovery

        Assert.True(catalog.IsHealthy("azure-gpt4-mini"));

        // Should take 3 more failures to go unhealthy again
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.True(catalog.IsHealthy("azure-gpt4-mini")); // only 2 since reset
    }

    [Fact]
    public void HealthState_IsIndependentPerDeployment()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.False(catalog.IsHealthy("azure-gpt4-mini"));

        // Other deployment unaffected
        Assert.True(catalog.IsHealthy("opencode-deepseek"));
    }

    [Fact]
    public void ResetHealth_ClearsAllState()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());

        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        catalog.ReportHealth("azure-gpt4-mini", healthy: false);
        Assert.False(catalog.IsHealthy("azure-gpt4-mini"));

        catalog.ResetHealth();
        Assert.True(catalog.IsHealthy("azure-gpt4-mini"));
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        var catalog = new InMemoryProviderCatalog(CreateSampleOptions());
        var parallelOps = 100;

        Parallel.For(0, parallelOps, i =>
        {
            // Mix of reads and writes
            catalog.GetAllDeployments();
            catalog.GetDeployment("azure-gpt4-mini");
            catalog.GetDeploymentsForModel("gpt-4o-mini");
            catalog.GetDeploymentsForModel("deepseek-v4-pro");
            catalog.GetDeploymentsForModel("gemma4-local");
            catalog.GetRoute("gpt-4o-mini");
            catalog.IsHealthy("azure-gpt4-mini");
            catalog.ReportHealth("azure-gpt4-mini", healthy: i % 5 != 0);
            catalog.ReportHealth("opencode-deepseek", healthy: true);
        });

        // Should complete without exceptions
        Assert.True(true);
    }

    [Fact]
    public void Deployment_AllPropertiesRoundTrip()
    {
        var opts = CreateSampleOptions();
        var catalog = new InMemoryProviderCatalog(opts);

        var dep = catalog.GetDeployment("ollama-gemma4");
        Assert.NotNull(dep);
        Assert.Equal("gemma4-local", dep.ModelName);
        Assert.Equal("Ollama", dep.Provider);
        Assert.Equal("http://192.168.16.12:11434", dep.Endpoint);
        Assert.Equal("gemma4:e4b", dep.Model);
        Assert.Equal(1, dep.Weight);
        Assert.Equal(1, dep.Priority);
        Assert.Equal(32768, dep.MaxContextTokens);
        Assert.Equal(["chat"], dep.Capabilities);
        Assert.Equal(["local", "quick"], dep.Tags);
        Assert.True(dep.Enabled);
    }

    private static ProviderCatalogOptions CreateSampleOptions()
        => new()
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new()
                {
                    Name = "azure-gpt4-mini",
                    ModelName = "gpt-4o-mini",
                    Provider = "AzureFoundry",
                    Endpoint = "https://eastus.api.cognitive.microsoft.com",
                    ApiKey = "",
                    Model = "gpt-4o-mini",
                    Weight = 3,
                    Priority = 1,
                    MaxContextTokens = 128000,
                    Capabilities = ["chat", "tools", "vision"],
                    CostPerToken = 0.00000015,
                    Tags = ["primary", "low-latency"]
                },
                new()
                {
                    Name = "opencode-deepseek",
                    ModelName = "deepseek-v4-pro",
                    Provider = "OpenCodeGo",
                    Endpoint = "https://opencode.ai/zen/go/v1",
                    ApiKey = "",
                    Model = "deepseek-v4-pro",
                    Weight = 1,
                    Priority = 2,
                    MaxContextTokens = 128000,
                    Capabilities = ["chat", "tools", "reasoning"],
                    CostPerToken = 0.000002,
                    Tags = ["coding", "reasoning"]
                },
                new()
                {
                    Name = "ollama-gemma4",
                    ModelName = "gemma4-local",
                    Provider = "Ollama",
                    Endpoint = "http://192.168.16.12:11434",
                    ApiKey = "",
                    Model = "gemma4:e4b",
                    Weight = 1,
                    Priority = 1,
                    MaxContextTokens = 32768,
                    Capabilities = ["chat"],
                    CostPerToken = 0,
                    Tags = ["local", "quick"]
                }
            ],
            ModelRouting = new(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o-mini"] = new()
                {
                    Strategy = "latency",
                    Deployments = ["azure-gpt4-mini"],
                    Fallbacks = ["opencode-deepseek"]
                },
                ["deepseek-v4-pro"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["opencode-deepseek"],
                    Fallbacks = ["ollama-gemma4"]
                },
                ["gemma4-local"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["ollama-gemma4", "azure-gpt4-mini"],
                    Fallbacks = []
                }
            }
        };
}
