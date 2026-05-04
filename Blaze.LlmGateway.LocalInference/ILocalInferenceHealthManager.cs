using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Manages health state of local inference, combining local availability and remote discovery.
/// Implements explicit state machine with health status transitions.
/// Compatible with Aspire health checks via IHealthCheck.
/// </summary>
public interface ILocalInferenceHealthManager : IHealthCheck
{
    /// <summary>
    /// Gets the current health status.
    /// </summary>
    /// <returns>HealthStatus enum value.</returns>
    HealthStatus GetStatus();

    /// <summary>
    /// Gets detailed diagnostics about current health state.
    /// </summary>
    /// <returns>HealthDiagnostics with full state information.</returns>
    HealthDiagnostics GetDiagnostics();

    /// <summary>
    /// Gets the current health status and diagnostics synchronously.
    /// </summary>
    /// <returns>Tuple of (status, diagnostics).</returns>
    (HealthStatus Status, HealthDiagnostics Diagnostics) GetHealthSync();

    /// <summary>
    /// Observes health status changes for all state transitions.
    /// </summary>
    /// <returns>Observable sequence of HealthStatusChanged events.</returns>
    IObservable<HealthStatusChanged> ObserveHealthChanges();

    /// <summary>
    /// Manually trigger a health check and update internal state.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task RefreshHealthAsync(CancellationToken cancellationToken = default);
}
