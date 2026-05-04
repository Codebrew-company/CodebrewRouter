using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Manages health state through explicit state machine combining local and remote availability.
/// Subscribes to both ILocalModelAvailability and ICodebrewRouterDiscoveryService events.
/// Transitions: Healthy ↔ Degraded ↔ Unavailable.
/// Implements IHealthCheck for Aspire integration.
/// </summary>
public class LocalInferenceHealthManager : ILocalInferenceHealthManager, IDisposable
{
    private readonly ILocalModelAvailability _localAvailability;
    private readonly ICodebrewRouterDiscoveryService _remoteDiscovery;
    private readonly ILogger<LocalInferenceHealthManager> _logger;
    private readonly Subject<HealthStatusChanged> _healthChangedSubject;

    private HealthStatus _currentStatus = HealthStatus.Unavailable;
    private HealthDiagnostics _currentDiagnostics;
    private DateTime _lastTransitionUtc = DateTime.UtcNow;
    private DateTime _lastCheckedUtc = DateTime.UtcNow;
    private DateTime _lastEventReceivedUtc = DateTime.UtcNow;
    private bool _localModelAvailable;
    private bool _remoteDiscoveryOnline;
    private int _remoteModelCount;
    private readonly object _stateLock = new();
    private IDisposable? _localAvailabilitySubscription;
    private IDisposable? _remoteDiscoverySubscription;

    private const int EventTimeoutSeconds = 300; // 5 minutes

    public LocalInferenceHealthManager(
        ILocalModelAvailability localAvailability,
        ICodebrewRouterDiscoveryService remoteDiscovery,
        ILogger<LocalInferenceHealthManager> logger)
    {
        _localAvailability = localAvailability ?? throw new ArgumentNullException(nameof(localAvailability));
        _remoteDiscovery = remoteDiscovery ?? throw new ArgumentNullException(nameof(remoteDiscovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthChangedSubject = new Subject<HealthStatusChanged>();
        _currentDiagnostics = CreateDiagnostics(HealthStatus.Unavailable);

        // Subscribe to availability changes
        SubscribeToAvailabilityEvents();
    }

    public HealthStatus GetStatus()
    {
        lock (_stateLock)
        {
            return _currentStatus;
        }
    }

    public HealthDiagnostics GetDiagnostics()
    {
        lock (_stateLock)
        {
            return _currentDiagnostics;
        }
    }

    public (HealthStatus Status, HealthDiagnostics Diagnostics) GetHealthSync()
    {
        lock (_stateLock)
        {
            return (_currentStatus, _currentDiagnostics);
        }
    }

    public IObservable<HealthStatusChanged> ObserveHealthChanges()
    {
        return _healthChangedSubject.AsObservable();
    }

    public async Task RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Refreshing health state");
        await UpdateHealthStateAsync();
    }

    /// <summary>
    /// IHealthCheck implementation for Aspire integration.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var (status, diagnostics) = GetHealthSync();

        var healthCheckStatus = status switch
        {
            HealthStatus.Healthy => HealthStatus.Healthy,
            HealthStatus.Degraded => HealthStatus.Degraded,
            HealthStatus.Unavailable => HealthStatus.Unavailable,
            _ => HealthStatus.Unavailable
        };

        var healthStatus = status switch
        {
            HealthStatus.Healthy => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            HealthStatus.Degraded => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            HealthStatus.Unavailable => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            _ => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy
        };

        var result = new HealthCheckResult(
            healthStatus,
            description: diagnostics.Description,
            data: new Dictionary<string, object>
            {
                { "status", diagnostics.Status.ToString() },
                { "local_available", diagnostics.LocalModelAvailable },
                { "remote_online", diagnostics.RemoteDiscoveryOnline },
                { "remote_model_count", diagnostics.RemoteModelCount },
                { "last_checked", diagnostics.LastCheckedUtc.ToString("O") },
                { "message", diagnostics.Message ?? "No issues" }
            });

        return Task.FromResult(result);
    }

    public void Dispose()
    {
        _localAvailabilitySubscription?.Dispose();
        _remoteDiscoverySubscription?.Dispose();
        _healthChangedSubject?.Dispose();
    }

    /// <summary>
    /// Subscribes to availability and discovery events.
    /// </summary>
    private void SubscribeToAvailabilityEvents()
    {
        try
        {
            _localAvailabilitySubscription = _localAvailability.ObserveAvailabilityChanges()
                .Subscribe(
                    evt => HandleLocalAvailabilityChanged(evt),
                    err => _logger.LogError(err, "Error in local availability observable"));

            _remoteDiscoverySubscription = _remoteDiscovery.ObserveDiscoveryChanges()
                .Subscribe(
                    evt => HandleRemoteDiscoveryChanged(evt),
                    err => _logger.LogError(err, "Error in remote discovery observable"));

            _logger.LogInformation("Subscribed to availability and discovery events");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to availability events");
        }
    }

    /// <summary>
    /// Handles local model availability change events.
    /// </summary>
    private void HandleLocalAvailabilityChanged(ModelAvailabilityChanged evt)
    {
        try
        {
            _logger.LogInformation(
                "Local availability changed: {Model} ({WasAvailable} -> {IsAvailable})",
                evt.Model.Name, evt.WasAvailable, evt.IsAvailable);

            lock (_stateLock)
            {
                _localModelAvailable = evt.IsAvailable;
                _lastEventReceivedUtc = DateTime.UtcNow;
            }

            // Trigger health update
            _ = UpdateHealthStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling local availability change");
        }
    }

    /// <summary>
    /// Handles remote discovery result change events.
    /// </summary>
    private void HandleRemoteDiscoveryChanged(DiscoveryChanged evt)
    {
        try
        {
            _logger.LogInformation(
                "Remote discovery changed: {ModelCount} models (online={IsOnline})",
                evt.Result.Models.Count, evt.Result.IsOnline);

            lock (_stateLock)
            {
                _remoteDiscoveryOnline = evt.Result.IsOnline;
                _remoteModelCount = evt.Result.Models.Count;
                _lastEventReceivedUtc = DateTime.UtcNow;
            }

            // Trigger health update
            _ = UpdateHealthStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling remote discovery change");
        }
    }

    /// <summary>
    /// Updates health state based on current local and remote availability.
    /// Implements state machine logic.
    /// </summary>
    private async Task UpdateHealthStateAsync()
    {
        try
        {
            HealthStatus newStatus;
            string reason;

            lock (_stateLock)
            {
                _lastCheckedUtc = DateTime.UtcNow;

                // Check for timeout - if no events for N seconds, degrade
                var timeSinceLastEvent = DateTime.UtcNow - _lastEventReceivedUtc;
                if (timeSinceLastEvent > TimeSpan.FromSeconds(EventTimeoutSeconds))
                {
                    newStatus = HealthStatus.Degraded;
                    reason = $"No events received for {timeSinceLastEvent.TotalSeconds:F0}s (timeout)";
                }
                // State machine logic
                else if (_localModelAvailable && _remoteDiscoveryOnline)
                {
                    newStatus = HealthStatus.Healthy;
                    reason = "Local available AND remote online";
                }
                else if (_localModelAvailable && !_remoteDiscoveryOnline)
                {
                    newStatus = HealthStatus.Degraded;
                    reason = "Local available BUT remote offline";
                }
                else if (!_localModelAvailable && _remoteDiscoveryOnline)
                {
                    // Could still route to remote
                    newStatus = HealthStatus.Degraded;
                    reason = "Local unavailable BUT remote online";
                }
                else
                {
                    newStatus = HealthStatus.Unavailable;
                    reason = "Both local and remote unavailable";
                }

                // Transition if status changed
                if (newStatus != _currentStatus)
                {
                    var diagnostics = CreateDiagnostics(newStatus);
                    FireHealthStatusChanged(_currentStatus, newStatus, reason, diagnostics);
                    _currentStatus = newStatus;
                    _currentDiagnostics = diagnostics;
                    _lastTransitionUtc = DateTime.UtcNow;
                    _logger.LogWarning(
                        "Health status transitioned: {OldStatus} -> {NewStatus}. Reason: {Reason}",
                        _currentStatus, newStatus, reason);
                }
                else
                {
                    _currentDiagnostics = CreateDiagnostics(_currentStatus);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating health state");
        }
    }

    /// <summary>
    /// Creates diagnostics object for given status.
    /// </summary>
    private HealthDiagnostics CreateDiagnostics(HealthStatus status)
    {
        var description = status switch
        {
            HealthStatus.Healthy => "Local inference healthy: model available and remote accessible",
            HealthStatus.Degraded => "Local inference degraded: reduced capability available",
            HealthStatus.Unavailable => "Local inference unavailable: no models accessible",
            _ => "Unknown health status"
        };

        return new HealthDiagnostics
        {
            Status = status,
            Description = description,
            LocalModelAvailable = _localModelAvailable,
            RemoteDiscoveryOnline = _remoteDiscoveryOnline,
            RemoteModelCount = _remoteModelCount,
            LastCheckedUtc = _lastCheckedUtc,
            LastTransitionUtc = _lastTransitionUtc,
            Message = $"Status: {status}, Local: {_localModelAvailable}, Remote: {_remoteDiscoveryOnline}"
        };
    }

    /// <summary>
    /// Fires health status changed event to subscribers.
    /// </summary>
    private void FireHealthStatusChanged(
        HealthStatus oldStatus,
        HealthStatus newStatus,
        string reason,
        HealthDiagnostics diagnostics)
    {
        try
        {
            var evt = new HealthStatusChanged
            {
                PreviousStatus = oldStatus,
                NewStatus = newStatus,
                Reason = reason,
                TransitionAtUtc = DateTime.UtcNow,
                Diagnostics = diagnostics
            };

            _healthChangedSubject.OnNext(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error firing health status changed event");
        }
    }
}
