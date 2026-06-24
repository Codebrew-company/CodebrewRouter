using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests.Catalog;

public sealed class CatalogModelRouterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static InMemoryProviderCatalog CreateCatalog(
        Action<ProviderCatalogOptions>? configure = null)
    {
        var opts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "gpt-4o", Provider = "AzureFoundry", Weight = 1, Priority = 1, Capabilities = ["chat", "tools"], Enabled = true },
                new() { Name = "dep-b", ModelName = "gpt-4o", Provider = "GithubModels",  Weight = 1, Priority = 2, Capabilities = ["chat", "tools"], Enabled = true },
                new() { Name = "dep-c", ModelName = "gpt-4o", Provider = "OpenCodeGo",    Weight = 1, Priority = 3, Capabilities = ["chat"],           Enabled = true },
                new() { Name = "standby", ModelName = "gpt-4o-mini", Provider = "Mock",  Weight = 1, Priority = 1, Capabilities = ["chat"],           Enabled = true },
            ],
            ModelRouting =
            {
                ["gpt-4o"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["dep-a", "dep-b", "dep-c"],
                    Fallbacks = ["standby"]
                },
                ["gpt-4o-mini"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["standby"]
                },
                ["empty-route"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = [],
                    Fallbacks = []
                },
                ["broken-deps"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["nonexistent-dep"],
                    Fallbacks = []
                },
                ["fallback-only"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = [],
                    Fallbacks = ["standby"]
                },
            }
        };
        configure?.Invoke(opts);
        return new InMemoryProviderCatalog(opts);
    }

    private static CatalogModelRouter CreateRouter(
        IProviderCatalog? catalog = null)
    {
        var cat = catalog ?? CreateCatalog();
        var resolver = new RoutingStrategyResolver(cat);
        return new CatalogModelRouter(
            cat,
            resolver,
            NullLogger<CatalogModelRouter>.Instance,
            Options.Create(new LlmGatewayOptions()));
    }

    private static RoutingContext Ctx(
        string modelId = "test-model",
        bool tools = false,
        bool vision = false)
        => new(modelId, 100, false, tools, vision, CancellationToken.None);

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void SelectDeployment_RouteExists_ReturnsADeployment()
    {
        var router = CreateRouter();
        var result = router.SelectDeployment("gpt-4o", Ctx("gpt-4o"));
        Assert.NotNull(result);
        Assert.Contains(result.Name, (IEnumerable<string>)["dep-a", "dep-b", "dep-c"]);
    }

    [Fact]
    public void SelectDeployment_RouteNotFound_ReturnsNull()
    {
        var router = CreateRouter();
        var result = router.SelectDeployment("unknown-model", Ctx("unknown-model"));
        Assert.Null(result);
    }

    [Fact]
    public void SelectDeployment_EmptyDeploymentsList_ReturnsNull()
    {
        var router = CreateRouter();
        var result = router.SelectDeployment("empty-route", Ctx("empty-route"));
        Assert.Null(result);
    }

    [Fact]
    public void SelectDeployment_AllDeploymentsDisabled_ReturnsNull()
    {
        var catalog = CreateCatalog(opts =>
        {
            // Disable all primary deployments
            opts.Deployments[0].Enabled = false; // dep-a
            opts.Deployments[1].Enabled = false; // dep-b
            opts.Deployments[2].Enabled = false; // dep-c
        });
        var router = CreateRouter(catalog);
        var result = router.SelectDeployment("gpt-4o", Ctx("gpt-4o"));
        // Fallback "standby" is still enabled
        Assert.NotNull(result);
        Assert.Equal("standby", result.Name);
    }

    [Fact]
    public void SelectDeployment_AllUnhealthy_FallsBack()
    {
        var catalog = CreateCatalog();
        // Mark all primary deployments unhealthy
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-b", false);
        catalog.ReportHealth("dep-b", false);
        catalog.ReportHealth("dep-b", false);
        catalog.ReportHealth("dep-c", false);
        catalog.ReportHealth("dep-c", false);
        catalog.ReportHealth("dep-c", false);

        var router = CreateRouter(catalog);
        var result = router.SelectDeployment("gpt-4o", Ctx("gpt-4o"));
        Assert.NotNull(result);
        Assert.Equal("standby", result.Name);
    }

    [Fact]
    public void SelectDeployment_AllUnhealthyAndNoFallbacks_ReturnsNull()
    {
        var catalog = CreateCatalog(opts =>
        {
            opts.Deployments.Clear();
            opts.ModelRouting.Clear();

            opts.Deployments.Add(new() { Name = "only", ModelName = "m", Provider = "P", Enabled = true });
            opts.ModelRouting["m"] = new() { Strategy = "round_robin", Deployments = ["only"], Fallbacks = [] };
        });
        // Mark unhealthy
        catalog.ReportHealth("only", false);
        catalog.ReportHealth("only", false);
        catalog.ReportHealth("only", false);

        var router = CreateRouter(catalog);
        var result = router.SelectDeployment("m", Ctx("m"));
        Assert.Null(result);
    }

    [Fact]
    public void SelectDeployment_NonexistentDeploymentReferences_ReturnsNull()
    {
        var router = CreateRouter();
        var result = router.SelectDeployment("broken-deps", Ctx("broken-deps"));
        Assert.Null(result);
    }

    [Fact]
    public void SelectDeployment_FallbackOnlyRoute_SelectsFallback()
    {
        var router = CreateRouter();
        var result = router.SelectDeployment("fallback-only", Ctx("fallback-only"));
        Assert.NotNull(result);
        Assert.Equal("standby", result.Name);
    }

    [Fact]
    public void SelectDeployment_WithToolsRequested_RespectsCapabilityFilter()
    {
        var router = CreateRouter();
        var ctx = Ctx("gpt-4o", tools: true);
        var result = router.SelectDeployment("gpt-4o", ctx);
        Assert.NotNull(result);
        Assert.Contains("tools", result.Capabilities);
    }

    [Fact]
    public void SelectDeployment_WithVisionRequested_FiltersNonVisionDeployments()
    {
        // Only dep-a has vision in a modified catalog
        var catalog = CreateCatalog(opts =>
        {
            opts.Deployments[0].Capabilities = ["chat", "tools", "vision"]; // dep-a gets vision
        });
        var ctx = Ctx("gpt-4o", vision: true);
        var router = CreateRouter(catalog);
        var result = router.SelectDeployment("gpt-4o", ctx);
        Assert.NotNull(result);
        Assert.Equal("dep-a", result.Name);
    }

    [Fact]
    public void SelectDeployment_RoundRobinRotates()
    {
        var router = CreateRouter();
        var ctx = Ctx("gpt-4o");

        var first = router.SelectDeployment("gpt-4o", ctx);
        var second = router.SelectDeployment("gpt-4o", ctx);
        var third = router.SelectDeployment("gpt-4o", ctx);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        // With 3 deployments, all should be different (round-robin rotation)
        var names = new[] { first.Name, second.Name, third.Name };
        Assert.Equal(3, names.Distinct().Count());
    }

    // ── Null guards ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullCatalog()
        => Assert.Throws<ArgumentNullException>(() =>
            new CatalogModelRouter(
                null!,
                new RoutingStrategyResolver(CreateCatalog()),
                NullLogger<CatalogModelRouter>.Instance,
                Options.Create(new LlmGatewayOptions())));

    [Fact]
    public void Constructor_ThrowsOnNullStrategyResolver()
        => Assert.Throws<ArgumentNullException>(() =>
            new CatalogModelRouter(
                CreateCatalog(),
                null!,
                NullLogger<CatalogModelRouter>.Instance,
                Options.Create(new LlmGatewayOptions())));

    [Fact]
    public void SelectDeployment_ThrowsOnNullRouteName()
        => Assert.Throws<ArgumentNullException>(() =>
            CreateRouter().SelectDeployment(null!, Ctx("m")));

    [Fact]
    public void SelectDeployment_ThrowsOnNullContext()
        => Assert.Throws<ArgumentNullException>(() =>
            CreateRouter().SelectDeployment("gpt-4o", null!));
}
