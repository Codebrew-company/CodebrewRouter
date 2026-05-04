namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Health status enum for LocalInferenceHealthManager.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// Everything working: local model available and remote discovery accessible.
    /// Or local available while waiting for remote discovery.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Local model available but remote discovery is failing.
    /// System is degraded but still functional.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// Local model not available and remote not accessible.
    /// System cannot function.
    /// </summary>
    Unavailable = 2
}

/// <summary>
/// Diagnostic information about current health state.
/// </summary>
public class HealthDiagnostics
{
    /// <summary>
    /// Current health status.
    /// </summary>
    public required HealthStatus Status { get; init; }

    /// <summary>
    /// Human-readable description of current status.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether local model is available.
    /// </summary>
    public required bool LocalModelAvailable { get; init; }

    /// <summary>
    /// Whether remote discovery endpoint is reachable.
    /// </summary>
    public required bool RemoteDiscoveryOnline { get; init; }

    /// <summary>
    /// Count of remote models available (from last successful discovery).
    /// </summary>
    public required int RemoteModelCount { get; init; }

    /// <summary>
    /// When health was last checked (UTC).
    /// </summary>
    public required DateTime LastCheckedUtc { get; init; }

    /// <summary>
    /// Timestamp when last state transition occurred (UTC).
    /// </summary>
    public required DateTime LastTransitionUtc { get; init; }

    /// <summary>
    /// Optional message about the current state.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Event fired when health status changes.
/// </summary>
public class HealthStatusChanged
{
    /// <summary>
    /// Previous health status.
    /// </summary>
    public required HealthStatus PreviousStatus { get; init; }

    /// <summary>
    /// New health status.
    /// </summary>
    public required HealthStatus NewStatus { get; init; }

    /// <summary>
    /// Reason for the transition.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// When the transition occurred (UTC).
    /// </summary>
    public required DateTime TransitionAtUtc { get; init; }

    /// <summary>
    /// Full diagnostics at time of transition.
    /// </summary>
    public required HealthDiagnostics Diagnostics { get; init; }
}
