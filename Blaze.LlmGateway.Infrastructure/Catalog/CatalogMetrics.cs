using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// OpenTelemetry Meter for catalog-level observability.
/// Exposes counters and histograms for:
/// - Chat request throughput and latency (per deployment)
/// - Health probe results
/// - Circuit breaker state transitions
/// </summary>
public static class CatalogMetrics
{
    private static readonly Meter Meter = new("Blaze.LlmGateway.Catalog", "1.0.0");

    /// <summary>Number of chat completions started (non-streaming + streaming).</summary>
    public static readonly Counter<long> ChatRequestsStarted = Meter.CreateCounter<long>(
        "catalog.chat.requests.started",
        description: "Number of chat requests started through the catalog pipeline.");

    /// <summary>Number of chat completions that succeeded.</summary>
    public static readonly Counter<long> ChatRequestsSucceeded = Meter.CreateCounter<long>(
        "catalog.chat.requests.succeeded",
        description: "Number of chat requests that completed successfully.");

    /// <summary>Number of chat completions that failed (any exception except cancellation).</summary>
    public static readonly Counter<long> ChatRequestsFailed = Meter.CreateCounter<long>(
        "catalog.chat.requests.failed",
        description: "Number of chat requests that failed with an error.");

    /// <summary>Number of chat requests rejected due to unhealthy deployment (circuit breaker open).</summary>
    public static readonly Counter<long> ChatRequestsRejected = Meter.CreateCounter<long>(
        "catalog.chat.requests.rejected",
        description: "Number of chat requests rejected because the deployment was unhealthy.");

    /// <summary>Chat request latency histogram in milliseconds.</summary>
    public static readonly Histogram<double> ChatRequestLatencyMs = Meter.CreateHistogram<double>(
        "catalog.chat.request.latency_ms",
        unit: "ms",
        description: "End-to-end latency of successful chat requests in milliseconds.");

    /// <summary>Health probe results: successful probes.</summary>
    public static readonly Counter<long> HealthProbesSucceeded = Meter.CreateCounter<long>(
        "catalog.health.probes.succeeded",
        description: "Number of successful health probes.");

    /// <summary>Health probe results: failed probes.</summary>
    public static readonly Counter<long> HealthProbesFailed = Meter.CreateCounter<long>(
        "catalog.health.probes.failed",
        description: "Number of failed health probes.");

    /// <summary>Health probe results: timed-out probes.</summary>
    public static readonly Counter<long> HealthProbesTimedOut = Meter.CreateCounter<long>(
        "catalog.health.probes.timed_out",
        description: "Number of health probes that timed out.");

    // ── Tag keys ──

    public const string TagDeployment = "deployment";
    public const string TagProvider = "provider";
    public const string TagModelName = "model_name";
    public const string TagStreaming = "streaming";
    public const string TagErrorType = "error_type";

    /// <summary>Builds standard tag list for a deployment.</summary>
    public static KeyValuePair<string, object?>[] TagsFor(
        string deployment,
        string? provider = null,
        string? modelName = null,
        bool? isStreaming = null)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new(TagDeployment, deployment)
        };

        if (provider is not null)
            tags.Add(new(TagProvider, provider));
        if (modelName is not null)
            tags.Add(new(TagModelName, modelName));
        if (isStreaming.HasValue)
            tags.Add(new(TagStreaming, isStreaming.Value ? "true" : "false"));

        return tags.ToArray();
    }
}
