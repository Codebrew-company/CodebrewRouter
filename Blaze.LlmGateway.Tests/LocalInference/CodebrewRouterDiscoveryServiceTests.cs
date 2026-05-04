using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Blaze.LlmGateway.LocalInference.Tests;

public class CodebrewRouterDiscoveryServiceTests
{
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<CodebrewRouterDiscoveryService>> _mockLogger;
    private readonly CodebrewRouterDiscoveryService _service;

    public CodebrewRouterDiscoveryServiceTests()
    {
        _mockHttpHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        _mockLogger = new Mock<ILogger<CodebrewRouterDiscoveryService>>();
        _service = new CodebrewRouterDiscoveryService(
            _httpClient,
            _mockLogger.Object,
            "http://localhost:8080");
    }

    [Fact]
    public async Task DiscoverModelsAsync_SuccessfulDiscovery_ReturnsModels()
    {
        // Arrange
        var responseJson = @"{""data"": [{""id"": ""gpt-4""}, {""id"": ""claude-3-sonnet""}]}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsOnline);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("gpt-4", result.Models[0].Name);
        Assert.Equal("claude-3-sonnet", result.Models[1].Name);
    }

    [Fact]
    public async Task DiscoverModelsAsync_ReturnsCached_WithinTtl()
    {
        // Arrange
        var responseJson = @"{""data"": [{""id"": ""gpt-4""}]}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result1 = await _service.DiscoverModelsAsync();
        var result2 = await _service.DiscoverModelsAsync();

        // Assert
        Assert.Equal(result1.DiscoveredAtUtc, result2.DiscoveredAtUtc);
        // HttpHandler should only be called once due to cache
        _mockHttpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverModelsAsync_HttpError_ReturnsCachedOrOffline()
    {
        // Arrange
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.False(result.IsOnline);
        Assert.NotNull(result.ErrorMessage);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task DiscoverModelsAsync_Timeout_ReturnsOffline()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.False(result.IsOnline);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DiscoverModelsAsync_NetworkError_ReturnsOffline()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.False(result.IsOnline);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DiscoverModelsAsync_InvalidJson_ReturnsEmptyModels()
    {
        // Arrange
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("not valid json")
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.True(result.IsOnline); // 200 response means online
        Assert.Empty(result.Models); // But no models parsed from invalid JSON
    }

    [Fact]
    public void GetCachedDiscovery_ReturnsNull_WhenNotCached()
    {
        // Act
        var result = _service.GetCachedDiscovery();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedDiscovery_ReturnsCachedResult_AfterDiscovery()
    {
        // Arrange
        var responseJson = @"{""data"": [{""id"": ""gpt-4""}]}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        await _service.DiscoverModelsAsync();
        var cached = _service.GetCachedDiscovery();

        // Assert
        Assert.NotNull(cached);
        Assert.True(cached.IsOnline);
        Assert.Single(cached.Models);
    }

    [Fact]
    public async Task ObserveDiscoveryChanges_FiresEvent_OnSuccessfulDiscovery()
    {
        // Arrange
        var responseJson = @"{""data"": [{""id"": ""gpt-4""}]}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var events = new List<DiscoveryChanged>();
        var subscription = _service.ObserveDiscoveryChanges()
            .Subscribe(evt => events.Add(evt));

        // Act
        await _service.DiscoverModelsAsync();

        // Assert
        Assert.NotEmpty(events);
        Assert.Contains("successfully", events[0].Reason.ToLowerInvariant());

        subscription.Dispose();
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterMultipleFailures()
    {
        // Arrange
        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection error"));

        // Act - trigger multiple failures (default is 5 before breaking)
        for (int i = 0; i < 5; i++)
        {
            var result = await _service.DiscoverModelsAsync();
            Assert.False(result.IsOnline);
        }

        // Try again - should be blocked by circuit breaker
        var finalResult = await _service.DiscoverModelsAsync();

        // Assert
        Assert.False(finalResult.IsOnline);
        Assert.NotNull(finalResult.ErrorMessage);
    }

    [Fact]
    public async Task ExtractProvider_IdentifiesProviders_Correctly()
    {
        // Arrange
        var responseJson = @"{""data"": [
            {""id"": ""gpt-4""},
            {""id"": ""claude-3-sonnet""},
            {""id"": ""gemini-pro""},
            {""id"": ""ollama-llama2""}
        ]}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.Equal("OpenAI", result.Models[0].Provider);
        Assert.Equal("Anthropic", result.Models[1].Provider);
        Assert.Equal("Google", result.Models[2].Provider);
        Assert.Equal("Ollama", result.Models[3].Provider);
    }

    [Fact]
    public async Task EmptyModelsResponse_ReturnsEmptyList()
    {
        // Arrange
        var responseJson = @"{""data"": []}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.True(result.IsOnline);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task MissingDataProperty_ReturnsEmptyList()
    {
        // Arrange
        var responseJson = @"{""invalid"": ""response""}";
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var result = await _service.DiscoverModelsAsync();

        // Assert
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        // Act
        _service.Dispose();

        // Assert - should not throw on second dispose
        _service.Dispose();
    }
}
