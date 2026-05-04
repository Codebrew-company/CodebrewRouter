using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.LocalInference.Tests;

public class LocalInferenceHealthManagerTests
{
    private readonly Mock<ILocalModelAvailability> _mockLocalAvailability;
    private readonly Mock<ICodebrewRouterDiscoveryService> _mockRemoteDiscovery;
    private readonly Mock<ILogger<LocalInferenceHealthManager>> _mockLogger;
    private readonly LocalInferenceHealthManager _manager;

    private readonly Subject<ModelAvailabilityChanged> _localAvailabilitySubject;
    private readonly Subject<DiscoveryChanged> _remoteDiscoverySubject;

    public LocalInferenceHealthManagerTests()
    {
        _mockLocalAvailability = new Mock<ILocalModelAvailability>();
        _mockRemoteDiscovery = new Mock<ICodebrewRouterDiscoveryService>();
        _mockLogger = new Mock<ILogger<LocalInferenceHealthManager>>();

        _localAvailabilitySubject = new Subject<ModelAvailabilityChanged>();
        _remoteDiscoverySubject = new Subject<DiscoveryChanged>();

        _mockLocalAvailability
            .Setup(x => x.ObserveAvailabilityChanges())
            .Returns(_localAvailabilitySubject.AsObservable());

        _mockRemoteDiscovery
            .Setup(x => x.ObserveDiscoveryChanges())
            .Returns(_remoteDiscoverySubject.AsObservable());

        _manager = new LocalInferenceHealthManager(
            _mockLocalAvailability.Object,
            _mockRemoteDiscovery.Object,
            _mockLogger.Object);
    }

    private LocalModelInfo CreateLocalModel(string name)
    {
        return new LocalModelInfo
        {
            Name = name,
            Path = $"/models/{name}",
            ModelType = "mistral",
            LoadedAtUtc = DateTime.UtcNow,
            FileSizeBytes = 4000000000
        };
    }

    private DiscoveryChanged CreateDiscoveryChange(RemoteDiscoveryResult result, RemoteDiscoveryResult? previous, string reason)
    {
        return new DiscoveryChanged
        {
            Result = result,
            PreviousResult = previous,
            Reason = reason,
            ChangedAtUtc = DateTime.UtcNow
        };
    }

    [Fact]
    public void GetStatus_InitiallyUnavailable()
    {
        var status = _manager.GetStatus();
        Assert.Equal(HealthStatus.Unavailable, status);
    }

    [Fact]
    public async Task LocalAvailable_RemoteOnline_TransitionsToHealthy()
    {
        var localAvailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localAvailable);

        var remoteOnline = new RemoteDiscoveryResult(
            Models: new[] { new RemoteModelInfo { Name = "gpt-4", Provider = "OpenAI", IsAvailable = true } },
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: true);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOnline, null, "Discovery successful"));

        await Task.Delay(100);

        Assert.Equal(HealthStatus.Healthy, _manager.GetStatus());
    }

    [Fact]
    public async Task LocalAvailable_RemoteOffline_TransitionsToDegraded()
    {
        var localAvailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localAvailable);

        var offlineResult = new RemoteDiscoveryResult(
            Models: Array.Empty<RemoteModelInfo>(),
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false,
            ErrorMessage: "Endpoint unreachable");

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(offlineResult, null, "Discovery failed"));

        await Task.Delay(100);

        Assert.Equal(HealthStatus.Degraded, _manager.GetStatus());
    }

    [Fact]
    public async Task LocalUnavailable_RemoteOnline_TransitionsToDegraded()
    {
        var localUnavailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = true,
            IsAvailable = false,
            Reason = "Model not found",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localUnavailable);

        var onlineResult = new RemoteDiscoveryResult(
            Models: new[] { new RemoteModelInfo { Name = "gpt-4", Provider = "OpenAI", IsAvailable = true } },
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: true);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(onlineResult, null, "Discovery successful"));

        await Task.Delay(100);

        Assert.Equal(HealthStatus.Degraded, _manager.GetStatus());
    }

    [Fact]
    public async Task BothUnavailable_TransitionsToUnavailable()
    {
        var localUnavailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = true,
            IsAvailable = false,
            Reason = "Model not found",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localUnavailable);

        var offlineResult = new RemoteDiscoveryResult(
            Models: Array.Empty<RemoteModelInfo>(),
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false,
            ErrorMessage: "Endpoint unreachable");

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(offlineResult, null, "Discovery failed"));

        await Task.Delay(100);

        Assert.Equal(HealthStatus.Unavailable, _manager.GetStatus());
    }

    [Fact]
    public async Task ConcurrentAvailabilityChanges_HandlesProperly()
    {
        for (int i = 0; i < 5; i++)
        {
            var evt = new ModelAvailabilityChanged
            {
                Model = CreateLocalModel($"model-{i}"),
                WasAvailable = false,
                IsAvailable = i % 2 == 0,
                Reason = "Availability changed",
                ChangedAtUtc = DateTime.UtcNow
            };
            _localAvailabilitySubject.OnNext(evt);
        }

        await Task.Delay(200);

        // Assert no crash occurred
        Assert.NotNull(_manager.GetStatus());
    }

    [Fact]
    public async Task IHealthCheck_HealthyStatus_ReturnsHealthyHealthStatus()
    {
        var localAvailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localAvailable);

        var remoteOnline = new RemoteDiscoveryResult(
            Models: new[] { new RemoteModelInfo { Name = "gpt-4", Provider = "OpenAI", IsAvailable = true } },
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: true);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOnline, null, "Discovery successful"));

        await Task.Delay(100);

        var result = await _manager.CheckHealthAsync(default, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task IHealthCheck_DegradedStatus_ReturnsDegradedHealthStatus()
    {
        var localAvailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localAvailable);

        var remoteOffline = new RemoteDiscoveryResult(
            Models: Array.Empty<RemoteModelInfo>(),
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOffline, null, "Discovery failed"));

        await Task.Delay(100);

        var result = await _manager.CheckHealthAsync(default, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task GetDiagnostics_ReturnsCurrentState()
    {
        var local = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(local);

        var remote = new RemoteDiscoveryResult(
            Models: new[]
            {
                new RemoteModelInfo { Name = "gpt-4", Provider = "OpenAI", IsAvailable = true },
                new RemoteModelInfo { Name = "claude-3", Provider = "Anthropic", IsAvailable = true }
            },
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: true);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remote, null, "Discovery successful"));

        await Task.Delay(100);

        var diagnostics = _manager.GetDiagnostics();

        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.LocalModelAvailable);
        Assert.True(diagnostics.RemoteDiscoveryOnline);
        Assert.Equal(2, diagnostics.RemoteModelCount);
    }

    [Fact]
    public async Task TransitionSequence_UnavailableToHealthyToDegraded()
    {
        Assert.Equal(HealthStatus.Unavailable, _manager.GetStatus());

        var localAvailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = false,
            IsAvailable = true,
            Reason = "Model loaded",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localAvailable);
        await Task.Delay(100);

        Assert.Equal(HealthStatus.Degraded, _manager.GetStatus());

        var remoteOnline = new RemoteDiscoveryResult(
            Models: new[] { new RemoteModelInfo { Name = "gpt-4", Provider = "OpenAI", IsAvailable = true } },
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: true);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOnline, null, "Discovery successful"));
        await Task.Delay(100);

        Assert.Equal(HealthStatus.Healthy, _manager.GetStatus());

        var remoteOffline = new RemoteDiscoveryResult(
            Models: Array.Empty<RemoteModelInfo>(),
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOffline, remoteOnline, "Remote endpoint unreachable"));
        await Task.Delay(100);

        Assert.Equal(HealthStatus.Degraded, _manager.GetStatus());
    }

    [Fact]
    public async Task UnavailableStatus_ReturnsUnavailableHealthStatus()
    {
        var localUnavailable = new ModelAvailabilityChanged
        {
            Model = CreateLocalModel("mistral-7b"),
            WasAvailable = true,
            IsAvailable = false,
            Reason = "Model not found",
            ChangedAtUtc = DateTime.UtcNow
        };
        _localAvailabilitySubject.OnNext(localUnavailable);

        var remoteOffline = new RemoteDiscoveryResult(
            Models: Array.Empty<RemoteModelInfo>(),
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false);

        _remoteDiscoverySubject.OnNext(CreateDiscoveryChange(remoteOffline, null, "Discovery failed"));

        await Task.Delay(100);

        var result = await _manager.CheckHealthAsync(default, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, result.Status);
    }
}
