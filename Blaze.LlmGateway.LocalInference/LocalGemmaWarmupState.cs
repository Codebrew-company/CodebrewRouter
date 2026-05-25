using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blaze.LlmGateway.LocalInference;

using AspNetHealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

public enum LocalGemmaWarmupStatus
{
    NotStarted = 0,
    Downloading = 1,
    Loading = 2,
    Priming = 3,
    Ready = 4,
    Skipped = 5,
    Failed = 6
}

public sealed record LocalGemmaWarmupSnapshot(
    LocalGemmaWarmupStatus Status,
    string? ModelPath,
    string Message,
    DateTime UpdatedAtUtc,
    long? ElapsedMilliseconds);

public sealed class LocalGemmaWarmupState : IHealthCheck
{
    private readonly object _lock = new();
    private LocalGemmaWarmupSnapshot _snapshot = new(
        LocalGemmaWarmupStatus.NotStarted,
        null,
        "Local Gemma warmup has not started.",
        DateTime.UtcNow,
        null);

    public LocalGemmaWarmupSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public void Update(
        LocalGemmaWarmupStatus status,
        string? modelPath,
        string message,
        TimeSpan? elapsed = null)
    {
        lock (_lock)
        {
            _snapshot = new LocalGemmaWarmupSnapshot(
                status,
                modelPath,
                message,
                DateTime.UtcNow,
                elapsed is null ? null : (long)elapsed.Value.TotalMilliseconds);
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot;
        var healthStatus = snapshot.Status switch
        {
            LocalGemmaWarmupStatus.Ready => AspNetHealthStatus.Healthy,
            LocalGemmaWarmupStatus.Skipped => AspNetHealthStatus.Healthy,
            LocalGemmaWarmupStatus.Failed => AspNetHealthStatus.Unhealthy,
            _ => AspNetHealthStatus.Unhealthy
        };

        var data = new Dictionary<string, object>
        {
            ["status"] = snapshot.Status.ToString(),
            ["model_path"] = snapshot.ModelPath ?? "",
            ["message"] = snapshot.Message,
            ["updated_utc"] = snapshot.UpdatedAtUtc.ToString("O")
        };

        if (snapshot.ElapsedMilliseconds is not null)
        {
            data["elapsed_ms"] = snapshot.ElapsedMilliseconds.Value;
        }

        return Task.FromResult(new HealthCheckResult(
            healthStatus,
            snapshot.Message,
            data: data));
    }
}
