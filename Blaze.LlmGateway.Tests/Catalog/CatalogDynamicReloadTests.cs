using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blaze.LlmGateway.Tests.Catalog;

/// <summary>
/// Tests for the InMemoryProviderCatalog dynamic reload functionality.
/// Verifies that the catalog can be hot-reloaded while preserving health state
/// and maintaining thread safety.
/// </summary>
public sealed class CatalogDynamicReloadTests
{
    private static InMemoryProviderCatalog CreateCatalog(Action<ProviderCatalogOptions>? configure = null)
    {
        var opts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "model-1", Provider = "ProviderA", Enabled = true },
                new() { Name = "dep-b", ModelName = "model-1", Provider = "ProviderB", Enabled = true },
            ],
            ModelRouting =
            {
                ["model-1"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["dep-a", "dep-b"],
                    Fallbacks = []
                }
            }
        };
        configure?.Invoke(opts);
        return new InMemoryProviderCatalog(opts);
    }

    // ── Basic reload: new deployments appear ──────────────────────────

    [Fact]
    public void Reload_AddsNewDeployments()
    {
        var catalog = CreateCatalog();
        Assert.Equal(2, catalog.GetAllDeployments().Count);
        Assert.Null(catalog.GetDeployment("dep-c"));

        var newOpts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "model-1", Provider = "ProviderA", Enabled = true },
                new() { Name = "dep-b", ModelName = "model-1", Provider = "ProviderB", Enabled = true },
                new() { Name = "dep-c", ModelName = "model-2", Provider = "ProviderC", Enabled = true },
            ],
            ModelRouting =
            {
                ["model-1"] = new() { Strategy = "round_robin", Deployments = ["dep-a", "dep-b"], Fallbacks = [] },
                ["model-2"] = new() { Strategy = "round_robin", Deployments = ["dep-c"], Fallbacks = [] }
            }
        };

        catalog.Reload(newOpts);

        Assert.Equal(3, catalog.GetAllDeployments().Count);
        Assert.NotNull(catalog.GetDeployment("dep-c"));
        Assert.Equal("model-2", catalog.GetDeployment("dep-c")!.ModelName);
    }

    // ── Reload: deployments removed ──────────────────────────────────

    [Fact]
    public void Reload_RemovesOldDeployments()
    {
        var catalog = CreateCatalog();
        Assert.NotNull(catalog.GetDeployment("dep-b"));

        var newOpts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "model-1", Provider = "ProviderA", Enabled = true },
            ],
            ModelRouting =
            {
                ["model-1"] = new() { Strategy = "round_robin", Deployments = ["dep-a"], Fallbacks = [] }
            }
        };

        catalog.Reload(newOpts);

        Assert.Equal(1, catalog.GetAllDeployments().Count);
        Assert.NotNull(catalog.GetDeployment("dep-a"));
        Assert.Null(catalog.GetDeployment("dep-b"));
    }

    // ── Reload: health state preserved for surviving deployments ─────

    [Fact]
    public void Reload_PreservesHealthStateForSurvivingDeployments()
    {
        var catalog = CreateCatalog();

        // Mark dep-a as unhealthy
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        Assert.False(catalog.IsHealthy("dep-a"));

        // Reload with both deployments still present
        var newOpts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "model-1", Provider = "ProviderA", Enabled = true },
                new() { Name = "dep-b", ModelName = "model-1", Provider = "ProviderB", Enabled = true },
            ],
            ModelRouting =
            {
                ["model-1"] = new() { Strategy = "round_robin", Deployments = ["dep-a", "dep-b"], Fallbacks = [] }
            }
        };

        catalog.Reload(newOpts);

        // dep-a should still be unhealthy
        Assert.False(catalog.IsHealthy("dep-a"));
        // dep-b should still be healthy
        Assert.True(catalog.IsHealthy("dep-b"));
    }

    // ── Reload: stale health state cleaned ───────────────────────────

    [Fact]
    public void Reload_CleansUpStaleHealthState()
    {
        var catalog = CreateCatalog();

        // Mark both as unhealthy
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-a", false);
        catalog.ReportHealth("dep-b", false);
        catalog.ReportHealth("dep-b", false);
        catalog.ReportHealth("dep-b", false);

        // Reload with only dep-a remaining
        var newOpts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            Deployments =
            [
                new() { Name = "dep-a", ModelName = "model-1", Provider = "ProviderA", Enabled = true },
            ],
            ModelRouting =
            {
                ["model-1"] = new() { Strategy = "round_robin", Deployments = ["dep-a"], Fallbacks = [] }
            }
        };

        catalog.Reload(newOpts);

        // dep-a stays unhealthy
        Assert.False(catalog.IsHealthy("dep-a"));
        // dep-b should now be "healthy" (not found → optimistic default)
        Assert.True(catalog.IsHealthy("dep-b"));
    }

    // ── Reload: routes added and removed ─────────────────────────────

    [Fact]
    public void Reload_UpdatesRoutes()
    {
        var catalog = CreateCatalog();
        Assert.NotNull(catalog.GetRoute("model-1"));
        Assert.Null(catalog.GetRoute("new-model"));

        var newOpts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "latency",
            Deployments =
            [
                new() { Name = "dep-z", ModelName = "new-model", Provider = "ProviderZ", Enabled = true },
            ],
            ModelRouting =
            {
                ["new-model"] = new() { Strategy = "latency", Deployments = ["dep-z"], Fallbacks = [] }
            }
        };

        catalog.Reload(newOpts);

        Assert.Null(catalog.GetRoute("model-1"));
        var newRoute = catalog.GetRoute("new-model");
        Assert.NotNull(newRoute);
        Assert.Equal("latency", newRoute!.Strategy);
    }

    // ── Thread safety: concurrent reads during reload ────────────────

    [Fact]
    public async Task Reload_ConcurrentReads_DoesNotThrow()
    {
        var catalog = CreateCatalog();

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 10000; i++)
            {
                _ = catalog.GetAllDeployments();
                _ = catalog.GetDeployment("dep-a");
                _ = catalog.GetRoute("model-1");
                _ = catalog.GetDeploymentsForModel("model-1");
            }
        });

        var reloadTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                var opts = new ProviderCatalogOptions
                {
                    DefaultRoutingStrategy = "round_robin",
                    Deployments =
                    [
                        new() { Name = "dep-x", ModelName = "model-x", Provider = "ProviderX", Enabled = true },
                    ],
                    ModelRouting =
                    {
                        ["model-x"] = new() { Strategy = "round_robin", Deployments = ["dep-x"], Fallbacks = [] }
                    }
                };
                catalog.Reload(opts);
            }
        });

        await Task.WhenAll(readTask, reloadTask);

        // If we got here without exceptions, the test passes
        Assert.True(true);
    }

    // ── Reload preserves default options ─────────────────────────────

    [Fact]
    public void Reload_EmptyOptions_ProducesEmptyCatalog()
    {
        var catalog = CreateCatalog();
        Assert.Equal(2, catalog.GetAllDeployments().Count);

        catalog.Reload(new ProviderCatalogOptions());

        Assert.Empty(catalog.GetAllDeployments());
        Assert.Null(catalog.GetRoute("model-1"));
    }
}
