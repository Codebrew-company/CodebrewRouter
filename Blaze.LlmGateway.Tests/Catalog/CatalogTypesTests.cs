using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Tests.Catalog;

public class ProviderDeploymentTests
{
    [Fact]
    public void Create_WithRequiredFields_SetsProperties()
    {
        var dep = new ProviderDeployment
        {
            Name = "azure-gpt4-mini",
            ModelName = "gpt-4o-mini",
            Provider = "AzureFoundry"
        };

        Assert.Equal("azure-gpt4-mini", dep.Name);
        Assert.Equal("gpt-4o-mini", dep.ModelName);
        Assert.Equal("AzureFoundry", dep.Provider);
    }

    [Fact]
    public void Create_HasSensibleDefaults()
    {
        var dep = new ProviderDeployment
        {
            Name = "test",
            ModelName = "test-model",
            Provider = "TestProvider"
        };

        Assert.Equal(1, dep.Weight);
        Assert.Equal(10, dep.Priority);
        Assert.Equal(4096, dep.MaxContextTokens);
        Assert.Equal(0, dep.CostPerToken);
        Assert.True(dep.Enabled);
        Assert.Empty(dep.Capabilities);
        Assert.Empty(dep.Tags);
    }

    [Fact]
    public void Create_WithAllProperties_StoresCorrectly()
    {
        var dep = new ProviderDeployment
        {
            Name = "ollama-gemma4",
            ModelName = "gemma4-local",
            Provider = "Ollama",
            Endpoint = "http://192.168.16.12:11434",
            ApiKey = "",
            Model = "gemma4:e4b",
            Weight = 2,
            Priority = 1,
            MaxContextTokens = 32768,
            Capabilities = ["chat", "tools"],
            CostPerToken = 0.0001,
            Tags = ["local", "quick"],
            Enabled = false
        };

        Assert.Equal("http://192.168.16.12:11434", dep.Endpoint);
        Assert.Equal("gemma4:e4b", dep.Model);
        Assert.Equal(2, dep.Weight);
        Assert.Equal(1, dep.Priority);
        Assert.Equal(32768, dep.MaxContextTokens);
        Assert.Equal(["chat", "tools"], dep.Capabilities);
        Assert.Equal(0.0001, dep.CostPerToken);
        Assert.Equal(["local", "quick"], dep.Tags);
        Assert.False(dep.Enabled);
    }

    [Fact]
    public void Record_SupportsStructuralEquality()
    {
        var a = new ProviderDeployment { Name = "x", ModelName = "y", Provider = "z" };
        var b = new ProviderDeployment { Name = "x", ModelName = "y", Provider = "z" };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Record_SupportsWithExpression()
    {
        var original = new ProviderDeployment { Name = "a", ModelName = "m", Provider = "p" };
        var modified = original with { Weight = 5 };
        Assert.Equal(5, modified.Weight);
        Assert.Equal("a", modified.Name); // unchanged
    }
}

public class CatalogModelRouteTests
{
    [Fact]
    public void Create_WithRequiredFields_SetsProperties()
    {
        var route = new CatalogModelRoute
        {
            ModelName = "gpt-4o-mini",
            Strategy = "latency",
            Deployments = ["azure-gpt4-mini"]
        };

        Assert.Equal("gpt-4o-mini", route.ModelName);
        Assert.Equal("latency", route.Strategy);
        Assert.Equal(["azure-gpt4-mini"], route.Deployments);
    }

    [Fact]
    public void Default_Fallbacks_IsEmpty()
    {
        var route = new CatalogModelRoute
        {
            ModelName = "m",
            Strategy = "round_robin",
            Deployments = ["d1"]
        };

        Assert.Empty(route.Fallbacks);
    }

    [Fact]
    public void Record_PropertiesAreCorrect()
    {
        var a = new CatalogModelRoute { ModelName = "m", Strategy = "s", Deployments = ["d1"] };
        Assert.Equal("m", a.ModelName);
        Assert.Equal("s", a.Strategy);
        Assert.Equal(["d1"], a.Deployments);
        Assert.Empty(a.Fallbacks);
    }
}

public class RoutingContextTests
{
    [Fact]
    public void Create_SetsAllProperties()
    {
        var cts = new CancellationTokenSource();
        var ctx = new RoutingContext(
            ModelId: "test-model",
            EstimatedInputTokens: 1500,
            StreamingRequested: true,
            ToolsRequested: false,
            VisionRequested: true,
            CancellationToken: cts.Token);

        Assert.Equal("test-model", ctx.ModelId);
        Assert.Equal(1500, ctx.EstimatedInputTokens);
        Assert.True(ctx.StreamingRequested);
        Assert.False(ctx.ToolsRequested);
        Assert.True(ctx.VisionRequested);
        Assert.Equal(cts.Token, ctx.CancellationToken);
    }

    [Fact]
    public void Record_SupportsStructuralEquality()
    {
        var cts = new CancellationTokenSource();
        var a = new RoutingContext("m", 100, false, false, false, cts.Token);
        var b = new RoutingContext("m", 100, false, false, false, cts.Token);
        Assert.Equal(a, b);
    }
}

public class ProviderCatalogOptionsTests
{
    [Fact]
    public void Create_HasSensibleDefaults()
    {
        var opts = new ProviderCatalogOptions();
        Assert.Equal("round_robin", opts.DefaultRoutingStrategy);
        Assert.Equal("failover", opts.DefaultFallbackStrategy);
        Assert.Equal(30, opts.HealthCheckIntervalSeconds);
        Assert.Equal(3, opts.UnhealthyThreshold);
        Assert.Equal(30, opts.RecoveryIntervalSeconds);
        Assert.Empty(opts.Deployments);
        Assert.Empty(opts.ModelRouting);
    }

    [Fact]
    public void ModelRouting_UsesOrdinalIgnoreCase()
    {
        var opts = new ProviderCatalogOptions();
        opts.ModelRouting["GPT-4o-Mini"] = new ModelRouteConfig();
        Assert.True(opts.ModelRouting.ContainsKey("gpt-4o-mini"));
    }
}

public class LlmGatewayOptionsProviderCatalogTests
{
    [Fact]
    public void LlmGatewayOptions_HasProviderCatalogSection()
    {
        var opts = new LlmGatewayOptions();
        Assert.NotNull(opts.ProviderCatalog);
        Assert.Equal("round_robin", opts.ProviderCatalog.DefaultRoutingStrategy);
    }

    [Fact]
    public void RoundTrip_ProviderCatalogDeployments()
    {
        var opts = new LlmGatewayOptions();
        opts.ProviderCatalog.Deployments.Add(new ProviderDeploymentConfig
        {
            Name = "azure-gpt4-mini",
            ModelName = "gpt-4o-mini",
            Provider = "AzureFoundry",
            Weight = 3
        });

        Assert.Single(opts.ProviderCatalog.Deployments);
        Assert.Equal("azure-gpt4-mini", opts.ProviderCatalog.Deployments[0].Name);
        Assert.Equal(3, opts.ProviderCatalog.Deployments[0].Weight);
    }
}

public class VirtualModelOptionsCatalogModelTests
{
    [Fact]
    public void VirtualModelOptions_CanSetCatalogModel()
    {
        var opts = new VirtualModelOptions
        {
            ModelId = "codebrewSharpClient",
            CatalogModel = "deepseek-v4-pro"
        };
        Assert.Equal("deepseek-v4-pro", opts.CatalogModel);
    }

    [Fact]
    public void CatalogModel_DefaultsToNull()
    {
        var opts = new VirtualModelOptions { ModelId = "test" };
        Assert.Null(opts.CatalogModel);
    }

    [Fact]
    public void ToCodebrewRouterOptions_DoesNotPreserveCatalogModel()
    {
        var vm = new VirtualModelOptions
        {
            ModelId = "test",
            CatalogModel = "gpt-4o-mini"
        };
        var cb = vm.ToCodebrewRouterOptions();
        // CodebrewRouterOptions doesn't have CatalogModel — it's virtual-model-only
        Assert.NotNull(cb);
    }

    [Fact]
    public void GetEffectiveVirtualModels_PropagatesCatalogModel()
    {
        var opts = new LlmGatewayOptions();
        opts.CodebrewRouter.Enabled = false; // don't let the default codebrewRouter interfere
        opts.VirtualModels["yardly"] = new VirtualModelOptions
        {
            ModelId = "yardly",
            CatalogModel = "gpt-4o-mini"
        };

        var effective = opts.GetEffectiveVirtualModels();
        var yardly = Assert.Single(effective);
        Assert.Equal("gpt-4o-mini", yardly.CatalogModel);
    }
}
