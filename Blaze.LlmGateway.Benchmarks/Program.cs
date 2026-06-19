using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

BenchmarkRunner.Run<RoutingStrategyBenchmarks>();
BenchmarkRunner.Run<CatalogLookupBenchmarks>();

// ──────────────────────────────────────────────────────────────
// 1. Routing strategy selection speed
// ──────────────────────────────────────────────────────────────

[SimpleJob, MemoryDiagnoser]
public class RoutingStrategyBenchmarks
{
    private readonly IReadOnlyList<ProviderDeployment> _twoDeployments;
    private readonly IReadOnlyList<ProviderDeployment> _tenDeployments;
    private readonly RoutingContext _context;
    private readonly IProviderCatalog _catalogTwo;
    private readonly IProviderCatalog _catalogTen;

    private readonly RoundRobinStrategy _roundRobin2;
    private readonly ShuffleStrategy _shuffle2;
    private readonly LatencyStrategy _latency2;
    private readonly CostStrategy _cost2;
    private readonly LeastBusyStrategy _leastBusy2;
    private readonly RoundRobinStrategy _roundRobin10;
    private readonly ShuffleStrategy _shuffle10;

    public RoutingStrategyBenchmarks()
    {
        _twoDeployments = [CreateDeployment("dep-a", 3), CreateDeployment("dep-b", 1)];
        _tenDeployments = Enumerable.Range(0, 10)
            .Select(i => CreateDeployment($"dep-{i}", i + 1))
            .ToList();

        _catalogTwo = BuildCatalog(_twoDeployments);
        _catalogTen = BuildCatalog(_tenDeployments);

        _roundRobin2 = new RoundRobinStrategy(_catalogTwo);
        _shuffle2 = new ShuffleStrategy(_catalogTwo);
        _latency2 = new LatencyStrategy(_catalogTwo);
        _cost2 = new CostStrategy(_catalogTwo);
        _leastBusy2 = new LeastBusyStrategy(_catalogTwo);
        _roundRobin10 = new RoundRobinStrategy(_catalogTen);
        _shuffle10 = new ShuffleStrategy(_catalogTen);

        _context = new RoutingContext("test-model", 1000, true, false, false, CancellationToken.None);
    }

    [Benchmark] public ProviderDeployment? RoundRobin_2() => _roundRobin2.Select(_twoDeployments, _context);
    [Benchmark] public ProviderDeployment? Shuffle_2() => _shuffle2.Select(_twoDeployments, _context);
    [Benchmark] public ProviderDeployment? Latency_2() => _latency2.Select(_twoDeployments, _context);
    [Benchmark] public ProviderDeployment? Cost_2() => _cost2.Select(_twoDeployments, _context);
    [Benchmark] public ProviderDeployment? LeastBusy_2() => _leastBusy2.Select(_twoDeployments, _context);
    [Benchmark] public ProviderDeployment? RoundRobin_10() => _roundRobin10.Select(_tenDeployments, _context);
    [Benchmark] public ProviderDeployment? Shuffle_10() => _shuffle10.Select(_tenDeployments, _context);

    private static InMemoryProviderCatalog BuildCatalog(IReadOnlyList<ProviderDeployment> deps)
    {
        var configs = deps.Select(d => new ProviderDeploymentConfig
        {
            Name = d.Name,
            ModelName = d.ModelName,
            Provider = d.Provider,
            Weight = d.Weight,
            Priority = d.Priority,
            MaxContextTokens = d.MaxContextTokens,
            Capabilities = [.. d.Capabilities],
            CostPerToken = d.CostPerToken,
            Enabled = d.Enabled,
        }).ToList();

        return new InMemoryProviderCatalog(new ProviderCatalogOptions
        {
            Deployments = configs,
        });
    }

    private static ProviderDeployment CreateDeployment(string name, int weight = 1) =>
        new()
        {
            Name = name,
            ModelName = "test-model",
            Provider = "Mock",
            Weight = weight,
            Priority = 10,
            MaxContextTokens = 4096,
            Capabilities = ["chat"],
            Enabled = true,
        };
}

// ──────────────────────────────────────────────────────────────
// 2. Catalog lookup speed
// ──────────────────────────────────────────────────────────────

[SimpleJob, MemoryDiagnoser]
public class CatalogLookupBenchmarks
{
    private readonly InMemoryProviderCatalog _catalogSmall;
    private readonly InMemoryProviderCatalog _catalogLarge;

    public CatalogLookupBenchmarks()
    {
        _catalogSmall = new InMemoryProviderCatalog(new ProviderCatalogOptions
        {
            Deployments =
            [
                new ProviderDeploymentConfig
                {
                    Name = "dep-a", ModelName = "model-1", Provider = "Mock",
                    Weight = 1, Priority = 10, MaxContextTokens = 4096,
                    Capabilities = ["chat"], Enabled = true,
                },
                new ProviderDeploymentConfig
                {
                    Name = "dep-b", ModelName = "model-1", Provider = "Mock",
                    Weight = 1, Priority = 10, MaxContextTokens = 4096,
                    Capabilities = ["chat"], Enabled = true,
                },
            ],
            ModelRouting = new Dictionary<string, ModelRouteConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["model-1"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["dep-a", "dep-b"],
                },
            },
        });

        var largeDeployments = Enumerable.Range(0, 100)
            .Select(i => new ProviderDeploymentConfig
            {
                Name = $"dep-{i}",
                ModelName = $"model-{i % 10}",
                Provider = "Mock",
                Weight = 1, Priority = 10, MaxContextTokens = 4096,
                Capabilities = ["chat"], Enabled = true,
            })
            .ToList();

        var largeRoutes = new Dictionary<string, ModelRouteConfig>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 10; i++)
        {
            largeRoutes[$"model-{i}"] = new ModelRouteConfig
            {
                Strategy = "round_robin",
                Deployments = Enumerable.Range(i * 10, 10).Select(j => $"dep-{j}").ToList(),
            };
        }

        _catalogLarge = new InMemoryProviderCatalog(new ProviderCatalogOptions
        {
            Deployments = largeDeployments,
            ModelRouting = largeRoutes,
        });
    }

    [Benchmark] public IReadOnlyList<ProviderDeployment> GetAllDeployments_Small() => _catalogSmall.GetAllDeployments();
    [Benchmark] public IReadOnlyList<ProviderDeployment> GetAllDeployments_Large() => _catalogLarge.GetAllDeployments();
    [Benchmark] public IReadOnlyList<ProviderDeployment> GetDeploymentsForModel_Small() => _catalogSmall.GetDeploymentsForModel("model-1");
    [Benchmark] public IReadOnlyList<ProviderDeployment> GetDeploymentsForModel_Large() => _catalogLarge.GetDeploymentsForModel("model-5");
    [Benchmark] public CatalogModelRoute? GetRoute_Small() => _catalogSmall.GetRoute("model-1");
    [Benchmark] public CatalogModelRoute? GetRoute_Large() => _catalogLarge.GetRoute("model-5");
    [Benchmark] public ProviderDeployment? GetDeploymentByName_Small() => _catalogSmall.GetDeployment("dep-a");
    [Benchmark] public ProviderDeployment? GetDeploymentByName_Large() => _catalogLarge.GetDeployment("dep-50");
    [Benchmark] public bool IsHealthy_Small() => _catalogSmall.IsHealthy("dep-a");
    [Benchmark] public bool IsHealthy_Large() => _catalogLarge.IsHealthy("dep-50");
    [Benchmark] public void ReportHealth_Small() => _catalogSmall.ReportHealth("dep-a", true);
    [Benchmark] public void ReportHealth_Large() => _catalogLarge.ReportHealth("dep-50", true);
}
