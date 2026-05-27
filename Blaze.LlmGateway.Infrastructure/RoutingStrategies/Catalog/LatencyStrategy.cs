using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Catalog;
// Alias: resolve IRoutingStrategy from Core.Catalog (not the legacy one in parent namespace)
using CatRoutingStrategy = Blaze.LlmGateway.Core.Catalog.IRoutingStrategy;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

/// <summary>
/// Latency-based routing strategy that selects the deployment with the lowest recent average latency.
/// Maintains a sliding window of response times per deployment.
/// </summary>
public sealed class LatencyStrategy : CatRoutingStrategy
{
    private readonly IProviderCatalog _catalog;
    private readonly ConcurrentDictionary<string, List<double>> _latencyMeasurements = new(StringComparer.OrdinalIgnoreCase);
    private const int WindowSize = 10;

    public LatencyStrategy(IProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Name => "latency";

    /// <inheritdoc />
    public ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context)
    {
        // Filter through health-aware filter first
        var eligible = HealthAwareRoutingFilter.Filter(candidates, _catalog, context);

        if (eligible.Count == 0)
            return null;

        // Determine which eligible deployments have latency data
        var withData = new List<ProviderDeployment>();
        var withoutData = new List<ProviderDeployment>();

        for (var i = 0; i < eligible.Count; i++)
        {
            var dep = eligible[i];
            if (_latencyMeasurements.TryGetValue(dep.Name, out var measurements) && measurements.Count > 0)
            {
                withData.Add(dep);
            }
            else
            {
                withoutData.Add(dep);
            }
        }

        // If no deployments have latency data, fall back to Shuffle
        if (withData.Count == 0)
        {
            return FallbackToShuffle(eligible);
        }

        // If only some have data, consider only those with data
        var pool = withData;

        // Select the deployment with the lowest average latency
        ProviderDeployment? best = null;
        double bestAvg = double.MaxValue;

        for (var i = 0; i < pool.Count; i++)
        {
            var dep = pool[i];
            if (_latencyMeasurements.TryGetValue(dep.Name, out var measurements) && measurements.Count > 0)
            {
                double sum = 0;
                for (var j = 0; j < measurements.Count; j++)
                {
                    sum += measurements[j];
                }
                var avg = sum / measurements.Count;

                if (avg < bestAvg)
                {
                    bestAvg = avg;
                    best = dep;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Reports a latency measurement for a deployment. The strategy maintains a
    /// sliding window of the most recent <c>WindowSize</c> measurements.
    /// </summary>
    public void ReportLatency(string deploymentName, double latencyMs)
    {
        var measurements = _latencyMeasurements.GetOrAdd(deploymentName, _ => new List<double>(WindowSize));

        lock (measurements)
        {
            measurements.Add(latencyMs);
            if (measurements.Count > WindowSize)
            {
                measurements.RemoveAt(0);
            }
        }
    }

    private static ProviderDeployment? FallbackToShuffle(IReadOnlyList<ProviderDeployment> eligible)
    {
        // Uniform random fallback (ShuffleStrategy style)
        var idx = Random.Shared.Next(eligible.Count);
        return eligible[idx];
    }
}
