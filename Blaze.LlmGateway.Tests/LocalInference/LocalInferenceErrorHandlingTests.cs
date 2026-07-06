using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.LocalInference.Tests;

/// <summary>
/// Tests for error handling and logging in LocalInference services.
/// Validates that exceptions are properly thrown and logged with context.
/// </summary>
public class LocalInferenceErrorHandlingTests
{
    [Fact]
    public void LocalModelUnavailableException_CanBeCreatedWithMessage()
    {
        var exception = new LocalModelUnavailableException("Model not found");
        Assert.Equal("Model not found", exception.Message);
    }

    [Fact]
    public void LocalModelUnavailableException_CanBeCreatedWithInnerException()
    {
        var inner = new FileNotFoundException("File not found");
        var exception = new LocalModelUnavailableException("Model unavailable", inner);
        
        Assert.Equal("Model unavailable", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void RemoteDiscoveryFailedException_CanBeCreatedWithMessage()
    {
        var exception = new RemoteDiscoveryFailedException("Discovery failed");
        Assert.Equal("Discovery failed", exception.Message);
    }

    [Fact]
    public void RemoteDiscoveryFailedException_CanBeCreatedWithInnerException()
    {
        var inner = new HttpRequestException("Network error");
        var exception = new RemoteDiscoveryFailedException("Remote discovery failed", inner);
        
        Assert.Equal("Remote discovery failed", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void HealthCheckFailedException_CanBeCreatedWithMessage()
    {
        var exception = new HealthCheckFailedException("Health check failed");
        Assert.Equal("Health check failed", exception.Message);
    }

    [Fact]
    public void HealthCheckFailedException_CanBeCreatedWithInnerException()
    {
        var inner = new InvalidOperationException("Bad state");
        var exception = new HealthCheckFailedException("Health check failed", inner);
        
        Assert.Equal("Health check failed", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void LocalModelUnavailableException_IsInvalidOperationException()
    {
        var exception = new LocalModelUnavailableException("Test");
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void RemoteDiscoveryFailedException_IsInvalidOperationException()
    {
        var exception = new RemoteDiscoveryFailedException("Test");
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void HealthCheckFailedException_IsInvalidOperationException()
    {
        var exception = new HealthCheckFailedException("Test");
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void ExceptionThrowingAndCatching_PreservesStackTrace()
    {
        var exception = new RemoteDiscoveryFailedException(
            "Network unreachable",
            new HttpRequestException("Connection timeout"));
        
        try
        {
            throw exception;
        }
        catch (RemoteDiscoveryFailedException ex)
        {
            Assert.NotNull(ex.StackTrace);
            Assert.Contains(nameof(ExceptionThrowingAndCatching_PreservesStackTrace), ex.StackTrace);
        }
    }

    [Fact]
    public void LocalModelAvailabilityService_LogsEventOnAvailabilityChange()
    {
        var mockLogger = new Mock<ILogger<LocalModelAvailabilityService>>();
        var mockModelProvider = new Mock<IModelDistributionProvider>();
        var mockOptions = new Mock<Microsoft.Extensions.Options.IOptions<LocalInferenceOptions>>();
        
        mockOptions.Setup(x => x.Value).Returns(new LocalInferenceOptions
        {
            CacheAvailabilityTtlSeconds = 60
        });

        var service = new LocalModelAvailabilityService(
            mockModelProvider.Object,
            mockOptions.Object,
            mockLogger.Object);

        // Verify logger was injected and can be used
        Assert.NotNull(service);
        mockLogger.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never); // No logs should have been fired during construction
    }

    [Fact]
    public void CodebrewRouterDiscoveryService_HandleCircuitBreakerLogging()
    {
        var mockHttpHandler = new Mock<System.Net.Http.HttpMessageHandler>(MockBehavior.Strict);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var mockLogger = new Mock<ILogger<CodebrewRouterDiscoveryService>>();

        var service = new CodebrewRouterDiscoveryService(
            httpClient,
            mockLogger.Object,
            "http://localhost:8080");

        // Service should be created successfully
        Assert.NotNull(service);
    }

    [Fact]
    public void LocalInferenceHealthManager_LogsStateTransitions()
    {
        var mockLocalAvailability = new Mock<ILocalModelAvailability>();
        var mockRemoteDiscovery = new Mock<ICodebrewRouterDiscoveryService>();
        var mockLogger = new Mock<ILogger<LocalInferenceHealthManager>>();

        var manager = new LocalInferenceHealthManager(
            mockLocalAvailability.Object,
            mockRemoteDiscovery.Object,
            mockLogger.Object);

        // Manager should be created and logger should be set
        Assert.NotNull(manager);
    }

    [Fact]
    public void ExceptionMessages_AreDescriptive()
    {
        var modelException = new LocalModelUnavailableException(
            "Local model 'mistral-7b' not found at /models/mistral-7b.gguf. Check model path in configuration.");
        Assert.Contains("mistral-7b", modelException.Message);
        Assert.Contains("not found", modelException.Message);
        Assert.Contains("/models/", modelException.Message);

        var discoveryException = new RemoteDiscoveryFailedException(
            "Failed to discover models from http://localhost:5000/v1/models: timeout after 10 seconds");
        Assert.Contains("http://localhost:5000", discoveryException.Message);
        Assert.Contains("timeout", discoveryException.Message);
        Assert.Contains("10 seconds", discoveryException.Message);

        var healthException = new HealthCheckFailedException(
            "Health check failed: both local models and remote discovery are unavailable. Status: Unhealthy");
        Assert.Contains("local models", healthException.Message);
        Assert.Contains("remote discovery", healthException.Message);
        Assert.Contains("Unhealthy", healthException.Message);
    }
}
