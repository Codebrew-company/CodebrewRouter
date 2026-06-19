using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

namespace Blaze.LlmGateway.Tests.RoutingStrategies;

public class CatalogRoutingStrategyTests
{
    private static IProviderCatalog CreateCatalog()
    {
        var opts = new ProviderCatalogOptions
        {
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "test-model", Provider = "P1", Weight = 3, Priority = 1, MaxContextTokens = 128000, Capabilities = ["chat", "tools"], CostPerToken = 0.0001, Tags = ["primary"] },
                new() { Name = "dep-b", ModelName = "test-model", Provider = "P2", Weight = 1, Priority = 2, MaxContextTokens = 128000, Capabilities = ["chat", "tools"], CostPerToken = 0.0003, Tags = ["secondary"] },
                new() { Name = "dep-c", ModelName = "test-model", Provider = "P3", Weight = 1, Priority = 3, MaxContextTokens = 4096,  Capabilities = ["chat"],           CostPerToken = 0,       Tags = ["local"] },
                new() { Name = "dep-vision", ModelName = "vision-model", Provider = "P4", Weight = 1, Priority = 1, MaxContextTokens = 128000, Capabilities = ["chat", "vision"], CostPerToken = 0.0005, Tags = ["vision"] },
            ]
        };
        return new InMemoryProviderCatalog(opts);
    }

    private static RoutingContext Ctx(string modelId = "test-model", bool tools = false, bool vision = false)
        => new(modelId, 100, false, tools, vision, CancellationToken.None);

    private static IReadOnlyList<ProviderDeployment> AllDeps(IProviderCatalog catalog)
        => catalog.GetDeploymentsForModel("test-model");

    // ── HealthAwareRoutingFilter ────────────────────────────────────────

    [Fact]
    public void Filter_RemovesDisabledDeployments()
    {
        var catalog = CreateCatalog();
        var opts = new ProviderCatalogOptions
        {
            Deployments =
            [
                new() { Name = "enabled",  ModelName = "m", Provider = "P", Enabled = true },
                new() { Name = "disabled", ModelName = "m", Provider = "P", Enabled = false },
            ]
        };
        var cat = new InMemoryProviderCatalog(opts);
        var deps = cat.GetDeploymentsForModel("m");
        var result = HealthAwareRoutingFilter.Filter(deps, cat, Ctx());
        Assert.Single(result);
        Assert.Equal("enabled", result[0].Name);
    }

    [Fact]
    public void Filter_RemovesUnhealthyDeployments()
    {
        var catalog = CreateCatalog();
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);

        var eligible = HealthAwareRoutingFilter.Filter(AllDeps(catalog), catalog, Ctx());
        Assert.DoesNotContain(eligible, d => d.Name == "dep-a");
        Assert.Contains(eligible, d => d.Name == "dep-b");
    }

    [Fact]
    public void Filter_RemovesDeploymentsMissingToolsCapability()
    {
        var catalog = CreateCatalog();
        var deps = catalog.GetDeploymentsForModel("test-model");
        var ctx = Ctx(tools: true);
        var eligible = HealthAwareRoutingFilter.Filter(deps, catalog, ctx);
        Assert.DoesNotContain(eligible, d => d.Name == "dep-c"); // no "tools" capability
    }

    [Fact]
    public void Filter_RemovesDeploymentsMissingVisionCapability()
    {
        var catalog = CreateCatalog();
        var deps = catalog.GetDeploymentsForModel("test-model");
        var ctx = Ctx(vision: true);
        var eligible = HealthAwareRoutingFilter.Filter(deps, catalog, ctx);
        // Only dep-vision has "vision" capability but it's for "vision-model", not "test-model"
        Assert.All(eligible, d => Assert.Contains("vision", d.Capabilities));
    }

    [Fact]
    public void Filter_ReturnsAllWhenHealthyAndCapable()
    {
        var catalog = CreateCatalog();
        var eligible = HealthAwareRoutingFilter.Filter(AllDeps(catalog), catalog, Ctx());
        Assert.Equal(3, eligible.Count);
    }

    // ── RoundRobinStrategy ──────────────────────────────────────────────

    [Fact]
    public void RoundRobin_RotatesThroughDeployments()
    {
        var catalog = CreateCatalog();
        var strategy = new RoundRobinStrategy(catalog);
        var deps = AllDeps(catalog);

        var first = strategy.Select(deps, Ctx());
        var second = strategy.Select(deps, Ctx());
        var third = strategy.Select(deps, Ctx());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        // All three should be different (rotation)
        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(second.Name, third.Name);
        // Fourth call wraps back to first
        var fourth = strategy.Select(deps, Ctx());
        Assert.NotNull(fourth);
        Assert.Equal(first.Name, fourth.Name);
    }

    [Fact]
    public void RoundRobin_WithSingleDeployment_AlwaysReturnsIt()
    {
        var cat = new InMemoryProviderCatalog(new ProviderCatalogOptions
        {
            Deployments = [new() { Name = "only", ModelName = "m", Provider = "P" }],
            ModelRouting = new() { ["m"] = new() { Strategy = "round_robin", Deployments = ["only"] } }
        });
        var strategy = new RoundRobinStrategy(cat);
        var deps = cat.GetDeploymentsForModel("m");

        for (int i = 0; i < 5; i++)
            Assert.Equal("only", strategy.Select(deps, Ctx())?.Name);
    }

    [Fact]
    public void RoundRobin_EmptyList_ReturnsNull()
    {
        var strategy = new RoundRobinStrategy(CreateCatalog());
        Assert.Null(strategy.Select([], Ctx()));
    }

    // ── ShuffleStrategy ─────────────────────────────────────────────────

    [Fact]
    public void Shuffle_WithEqualWeights_ReturnsAllOverManyCalls()
    {
        var catalog = CreateCatalog();
        var strategy = new ShuffleStrategy(catalog);
        var deps = AllDeps(catalog);
        var seen = new HashSet<string>();

        for (int i = 0; i < 30; i++)
            seen.Add(strategy.Select(deps, Ctx())?.Name ?? "null");

        Assert.True(seen.Count >= 2); // probabalistic but highly likely with 30 iterations
    }

    [Fact]
    public void Shuffle_EmptyList_ReturnsNull()
    {
        var strategy = new ShuffleStrategy(CreateCatalog());
        Assert.Null(strategy.Select([], Ctx()));
    }

    // ── CostStrategy ────────────────────────────────────────────────────

    [Fact]
    public void Cost_SelectsCheapestDeployment()
    {
        var catalog = CreateCatalog();
        var strategy = new CostStrategy(catalog);
        var deps = AllDeps(catalog);

        var selected = strategy.Select(deps, Ctx());
        Assert.NotNull(selected);
        // dep-c costs 0, dep-a costs 0.0001, dep-b costs 0.0003
        Assert.Equal("dep-c", selected.Name);
    }

    [Fact]
    public void Cost_EmptyList_ReturnsNull()
    {
        var strategy = new CostStrategy(CreateCatalog());
        Assert.Null(strategy.Select([], Ctx()));
    }

    // ── LeastBusyStrategy ───────────────────────────────────────────────

    [Fact]
    public void LeastBusy_SelectsDeploymentWithFewestInFlight()
    {
        var catalog = CreateCatalog();
        var strategy = new LeastBusyStrategy(catalog);
        var deps = AllDeps(catalog);

        // First call — all 0 in-flight, picks first
        var first = strategy.Select(deps, Ctx());
        Assert.NotNull(first);

        // Second call — first is now busy (1 in-flight), should pick a different one
        var second = strategy.Select(deps, Ctx());
        Assert.NotNull(second);
        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public void LeastBusy_Release_DecrementsInFlight()
    {
        var catalog = CreateCatalog();
        var strategy = new LeastBusyStrategy(catalog);
        var deps = AllDeps(catalog);

        var first = strategy.Select(deps, Ctx());
        Assert.NotNull(first);

        strategy.Release(first.Name);

        // After release, all are equally loaded again
        var second = strategy.Select(deps, Ctx());
        Assert.NotNull(second);
        // Could pick any, just shouldn't throw
    }

    [Fact]
    public void LeastBusy_EmptyList_ReturnsNull()
    {
        var strategy = new LeastBusyStrategy(CreateCatalog());
        Assert.Null(strategy.Select([], Ctx()));
    }

    // ── LatencyStrategy ─────────────────────────────────────────────────

    [Fact]
    public void Latency_SelectsDeploymentWithLowestLatency()
    {
        var catalog = CreateCatalog();
        var strategy = new LatencyStrategy(catalog);
        var deps = AllDeps(catalog);

        // Report latencies: dep-a is fastest
        strategy.ReportLatency("dep-a", 10);
        strategy.ReportLatency("dep-b", 100);
        strategy.ReportLatency("dep-c", 50);
        strategy.ReportLatency("dep-a", 12);
        strategy.ReportLatency("dep-b", 90);

        var selected = strategy.Select(deps, Ctx());
        Assert.NotNull(selected);
        Assert.Equal("dep-a", selected.Name);
    }

    [Fact]
    public void Latency_WithNoData_FallsBackToShuffle()
    {
        var catalog = CreateCatalog();
        var strategy = new LatencyStrategy(catalog);
        var deps = AllDeps(catalog);

        // No latency data — should still return something (shuffle fallback)
        var selected = strategy.Select(deps, Ctx());
        Assert.NotNull(selected);
    }

    [Fact]
    public void Latency_EmptyList_ReturnsNull()
    {
        var strategy = new LatencyStrategy(CreateCatalog());
        Assert.Null(strategy.Select([], Ctx()));
    }
}
