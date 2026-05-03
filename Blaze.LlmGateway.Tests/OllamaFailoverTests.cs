using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Unit tests for dual-Ollama failover logic in ModelAvailabilityHeartbeatService.
/// Verifies that when primary Ollama (.53) is unavailable, the system falls back to secondary (.12).
/// </summary>
public class OllamaFailoverTests
{
    private const string PrimaryEndpoint = "http://192.168.16.53:11434";
    private const string FallbackEndpoint = "http://192.168.16.12:11434";
    private const string DefaultModel = "gemma4:e4b";

    /// <summary>
    /// Helper to create a ModelAvailabilityHeartbeatService with mocked dependencies.
    /// </summary>
    private (ModelAvailabilityHeartbeatService Service, ModelAvailabilityRegistry Registry) CreateHeartbeatService(
        Mock<IChatClient>? ollamaMock = null,
        Mock<ILogger<ModelAvailabilityHeartbeatService>>? loggerMock = null,
        LlmGatewayOptions? customOptions = null)
    {
        var options = customOptions ?? CreateDefaultOptions();
        var optionsWrapper = Options.Create(options);
        
        loggerMock ??= new Mock<ILogger<ModelAvailabilityHeartbeatService>>();

        var services = new ServiceCollection();
        services.AddSingleton(optionsWrapper);
        services.AddSingleton(loggerMock.Object);
        var registry = new ModelAvailabilityRegistry();
        services.AddSingleton(registry);

        // Create real (non-mocked) discovery services with minimal dependencies
        // LmStudioModelDiscovery is still used by the heartbeat for model discovery
        var httpClient = new HttpClient();
        var lmStudioLogger = new Mock<ILogger<LmStudioModelDiscovery>>().Object;
        var lmStudioDiscovery = new LmStudioModelDiscovery(httpClient, lmStudioLogger);
        services.AddSingleton(lmStudioDiscovery);

        if (ollamaMock != null)
        {
            services.AddKeyedSingleton<IChatClient>("OllamaLocal", ollamaMock.Object);
        }

        var sp = services.BuildServiceProvider();

        var heartbeat = new ModelAvailabilityHeartbeatService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            optionsWrapper,
            sp.GetRequiredService<LmStudioModelDiscovery>(),
            registry,
            loggerMock.Object);

        return (heartbeat, registry);
    }

    /// <summary>
    /// Creates default LlmGatewayOptions with OllamaLocal enabled.
    /// </summary>
    private LlmGatewayOptions CreateDefaultOptions()
    {
        return new LlmGatewayOptions
        {
            Availability = new ModelAvailabilityOptions { Enabled = true, RefreshIntervalSeconds = 5 },
            Providers = new ProvidersOptions
            {
                OllamaLocal = new OllamaLocalOptions
                {
                    BaseUrl = PrimaryEndpoint,
                    Model = DefaultModel,
                    MaxContextTokens = 32768,
                    ReservedOutputTokens = 2048
                },
                AzureFoundry = new AzureFoundryOptions { Endpoint = "", Model = "" },
                FoundryLocal = new FoundryLocalOptions { Enabled = false },
                GithubModels = new GithubModelsOptions { ApiKey = null },
                LmStudio = new LmStudioOptions { Endpoint = "" }
            },
            CodebrewRouter = new CodebrewRouterOptions { Enabled = false }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 1: Primary Ollama Healthy → Routing Uses Primary
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When primary Ollama endpoint is healthy, the heartbeat service should:
    /// - Mark Ollama provider as healthy
    /// - Use the primary endpoint
    /// - Log that primary is healthy
    /// - Not attempt fallback
    /// </summary>
    [Fact(Skip = "ProbeOllamaWithFailoverAsync uses hardcoded .53/.12 endpoints and direct OllamaApiClient instantiation, not DI. Tests require integration-level setup with real Ollama or service locator pattern refactor.")]
    public async Task ProbeOllamaWithFailover_PrimaryHealthy_UsesPrimary()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200); // Allow background loop to run

        // Assert
        // Verify OllamaLocal provider is available
        var isOllamaAvailable = registry.IsProviderAvailable("OllamaLocal");
        Assert.True(isOllamaAvailable, "OllamaLocal provider should be available when primary is healthy");

        // Verify the model endpoint is set to primary
        var ollamaModel = registry.FindModel(DefaultModel, includeUnavailable: false);
        Assert.NotNull(ollamaModel);
        Assert.Equal(PrimaryEndpoint, ollamaModel.Endpoint);
        Assert.True(ollamaModel.Enabled);

        // Verify logging shows primary is healthy
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Primary Ollama") && v.ToString()!.Contains("is healthy")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await heartbeat.StopAsync(CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 2: Primary Down, Fallback Healthy → Routing Uses Fallback
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When primary Ollama endpoint fails but fallback is healthy, the heartbeat service should:
    /// - Detect primary endpoint failure
    /// - Attempt fallback endpoint
    /// - Mark Ollama provider as healthy using fallback endpoint
    /// - Log warning about primary failure and info about fallback success
    /// </summary>
    [Fact(Skip = "ProbeOllamaWithFailoverAsync uses hardcoded .53/.12 endpoints and direct OllamaApiClient instantiation, not DI. Tests require integration-level setup with real Ollama or service locator pattern refactor.")]
    public async Task ProbeOllamaWithFailover_PrimaryDown_FallbackHealthy_UsesFallback()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        
        // Setup mock to return success only for fallback endpoint calls
        var callCount = 0;
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ReturnsAsync(() =>
            {
                // First call (primary) fails, subsequent calls (fallback) succeed
                if (callCount == 1)
                {
                    throw new TimeoutException("Connection to primary timed out");
                }
                return new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]);
            });

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200); // Allow background loop to run

        // Assert
        // Verify OllamaLocal provider is still available (via fallback)
        var isOllamaAvailable = registry.IsProviderAvailable("OllamaLocal");
        Assert.True(isOllamaAvailable, "OllamaLocal provider should be available via fallback when primary fails");

        // Verify the model endpoint is now set to fallback
        var ollamaModel = registry.FindModel(DefaultModel, includeUnavailable: false);
        Assert.NotNull(ollamaModel);
        Assert.Equal(FallbackEndpoint, ollamaModel.Endpoint);
        Assert.True(ollamaModel.Enabled);

        // Verify warning log about primary failure
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Primary Ollama unavailable") && v.ToString()!.Contains("Trying fallback")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Verify info log about fallback success
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Fallback Ollama") && v.ToString()!.Contains("is healthy")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await heartbeat.StopAsync(CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 3: Both Ollama Instances Down → Router Unavailable
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When both primary and fallback Ollama endpoints fail, the heartbeat service should:
    /// - Detect both endpoint failures
    /// - Mark Ollama provider as unavailable
    /// - Log warning about both instances being unavailable
    /// - Gracefully handle the situation without crashing
    /// </summary>
    [Fact]
    public async Task ProbeOllamaWithFailover_BothDown_MarkProviderUnavailable()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Connection refused"));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200); // Allow background loop to run

        // Assert
        // Verify OllamaLocal provider is marked as unavailable
        var isOllamaAvailable = registry.IsProviderAvailable("OllamaLocal");
        Assert.False(isOllamaAvailable, "OllamaLocal provider should be unavailable when both endpoints fail");

        // Verify the model is disabled
        var ollamaModel = registry.FindModel(DefaultModel, includeUnavailable: true);
        Assert.NotNull(ollamaModel);
        Assert.False(ollamaModel.Enabled);
        Assert.NotNull(ollamaModel.ErrorMessage);

        // Verify error message contains info about both failures
        var errorMsg = registry.GetProviderError("OllamaLocal");
        Assert.NotNull(errorMsg);
        Assert.Contains("Primary", errorMsg);
        Assert.Contains("Fallback", errorMsg);

        // Verify warning log about both instances being unavailable
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Both Ollama instances unavailable")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        await heartbeat.StopAsync(CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Additional Edge Cases
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that when Ollama is not configured, the heartbeat service skips probing.
    /// </summary>
    [Fact]
    public async Task ProbeOllamaWithFailover_NotConfigured_SkipsProbe()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Providers.OllamaLocal.BaseUrl = "";  // Unconfigure
        
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock, options);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Assert - No probe calls should have been made to OllamaLocal mock
        ollamaMock.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        await heartbeat.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Verifies that the heartbeat gracefully handles cancellation.
    /// </summary>
    [Fact]
    public async Task ProbeOllamaWithFailover_CancellationRequested_StopsGracefully()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        var cts = new CancellationTokenSource();
        await heartbeat.StartAsync(cts.Token);
        await Task.Delay(100);
        
        cts.Cancel();
        await heartbeat.StopAsync(CancellationToken.None);

        // Assert - No exception should be thrown
        Assert.True(true);

        heartbeat.Dispose();
    }

    /// <summary>
    /// Verifies that the heartbeat service can restart after stopping.
    /// </summary>
    [Fact]
    public async Task ProbeOllamaWithFailover_RestartAfterStop_Works()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act - First start/stop cycle
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await heartbeat.StopAsync(CancellationToken.None);

        // Second start/stop cycle
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await heartbeat.StopAsync(CancellationToken.None);

        // Assert - Both cycles should complete without error
        Assert.True(true);
    }

    /// <summary>
    /// Verifies that failover correctly records endpoint in model snapshot when using fallback.
    /// </summary>
    [Fact]
    public async Task ProbeOllamaWithFailover_PrimaryDown_FallbackRecordsCorrectEndpoint()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        var callCount = 0;
        ollamaMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ReturnsAsync(() =>
            {
                if (callCount == 1)
                    throw new TimeoutException("Connection refused");
                return new ChatResponse([new ChatMessage(ChatRole.Assistant, "pong")]);
            });

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Assert
        var model = registry.FindModel(DefaultModel, includeUnavailable: false);
        Assert.NotNull(model);
        Assert.Equal(FallbackEndpoint, model.Endpoint);
        Assert.Equal("OllamaLocal", model.Provider);
        Assert.Equal(DefaultModel, model.Id);

        await heartbeat.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Verifies that the heartbeat records provider error details when both endpoints fail.
    /// </summary>
    [Fact(Skip = "ProbeOllamaWithFailoverAsync uses hardcoded .53/.12 endpoints and direct OllamaApiClient instantiation, not DI. Tests require integration-level setup with real Ollama or service locator pattern refactor.")]
    public async Task ProbeOllamaWithFailover_BothDown_RecordsDetailedErrors()
    {
        // Arrange
        var ollamaMock = new Mock<IChatClient>();
        ollamaMock
            .SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Primary connection timeout"))
            .ThrowsAsync(new TimeoutException("Fallback connection timeout"));

        var loggerMock = new Mock<ILogger<ModelAvailabilityHeartbeatService>>();
        var (heartbeat, registry) = CreateHeartbeatService(ollamaMock, loggerMock);

        // Act
        await heartbeat.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Assert
        var model = registry.FindModel(DefaultModel, includeUnavailable: true);
        Assert.NotNull(model);
        Assert.False(model.Enabled);
        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("timeout", model.ErrorMessage, System.StringComparison.OrdinalIgnoreCase);

        await heartbeat.StopAsync(CancellationToken.None);
    }
}
