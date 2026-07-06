using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace Blaze.LlmGateway.Tests.LocalInference;

public class RuntimeDownloadModelProviderTests
{
    private readonly LocalInferenceOptions _defaultOptions = new()
    {
        Enabled = true,
        ModelPath = "model.gguf",
        CacheDirectory = Path.Combine(Path.GetTempPath(), $"llm-cache-{Guid.NewGuid():N}"),
        EnableChecksumValidation = true,
        DownloadTimeoutSeconds = 30,
        CircuitBreakerCooldownMinutes = 1,
        MaxContextTokens = 2048
    };

    private readonly ILogger<RuntimeDownloadModelProvider> _logger =
        new Mock<ILogger<RuntimeDownloadModelProvider>>().Object;

    [Fact]
    public async Task EnsureModelAvailableAsync_WithLocalPath_ReturnsPathWhenFileExists()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var mockHttp = new Mock<HttpClient>();
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);

            // Act
            var result = await provider.EnsureModelAvailableAsync(tempFile);

            // Assert
            result.Should().Be(Path.GetFullPath(tempFile));
            File.Exists(result).Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithLocalPath_ThrowsWhenFileNotFound()
    {
        // Arrange
        var mockHttp = new Mock<HttpClient>();
        var options = Options.Create(_defaultOptions);
        var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.gguf");

        // Act & Assert
        await provider.Invoking(p => p.EnsureModelAvailableAsync(nonExistentPath))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Local model file not found*");
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithNullOrEmptyUrl_ThrowsArgumentException()
    {
        // Arrange
        var mockHttp = new Mock<HttpClient>();
        var options = Options.Create(_defaultOptions);
        var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);

        // Act & Assert
        await provider.Invoking(p => p.EnsureModelAvailableAsync(null!))
            .Should()
            .ThrowAsync<ArgumentException>();

        await provider.Invoking(p => p.EnsureModelAvailableAsync(string.Empty))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithRemoteUrl_DownloadsAndCachesModel()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/gemma-2b.gguf";
            var testContent = "mock model data"u8.ToArray();
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testContent)
            };

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(mockResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act
            var result = await provider.EnsureModelAvailableAsync(modelUrl);

            // Assert
            result.Should().NotBeNullOrWhiteSpace();
            File.Exists(result).Should().BeTrue();
            var savedContent = await File.ReadAllBytesAsync(result);
            savedContent.Should().Equal(testContent);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithRemoteUrl_CacheHitReturnsExistingFile()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/gemma-2b.gguf";
            var fileName = Path.GetFileName(new Uri(modelUrl).AbsolutePath);
            var cachedPath = Path.Combine(cacheDir, fileName);
            var cachedContent = "cached model data"u8.ToArray();
            await File.WriteAllBytesAsync(cachedPath, cachedContent);

            var mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(mockHandler.Object);
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act
            var result = await provider.EnsureModelAvailableAsync(modelUrl);

            // Assert
            result.Should().Be(cachedPath);
            mockHandler.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithRemoteUrl_DownloadFailureThrowsAndOpensCircuitBreaker()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/notfound.gguf";
            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

            var httpClient = new HttpClient(mockHandler.Object);
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act & Assert - first call should fail
            await provider.Invoking(p => p.EnsureModelAvailableAsync(modelUrl))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*Failed to download model*");

            // Second call should also fail due to circuit breaker
            await provider.Invoking(p => p.EnsureModelAvailableAsync(modelUrl))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*temporarily disabled*");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithDownloadTimeout_ThrowsOperationCanceledAndOpensCircuitBreaker()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/slow.gguf";
            var options = Options.Create(new LocalInferenceOptions
            {
                CacheDirectory = cacheDir,
                DownloadTimeoutSeconds = 1,
                CircuitBreakerCooldownMinutes = 1
            });

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    // Simulate delay longer than timeout
                    await Task.Delay(5000, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });

            var httpClient = new HttpClient(mockHandler.Object) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act & Assert
            await provider.Invoking(p => p.EnsureModelAvailableAsync(modelUrl))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*timeout*");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_WithCancellation_ThrowsOperationCanceledAndOpensCircuitBreaker()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/slow.gguf";
            var options = Options.Create(new LocalInferenceOptions
            {
                CacheDirectory = cacheDir,
                DownloadTimeoutSeconds = 30,
                CircuitBreakerCooldownMinutes = 1
            });

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
                {
                    await Task.Delay(5000, ct); // Will be cancelled
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });

            var httpClient = new HttpClient(mockHandler.Object) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            // Act & Assert
            await provider.Invoking(p => p.EnsureModelAvailableAsync(modelUrl, cts.Token))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*timeout*");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCachedModelPathAsync_WithNonExistentUrl_ReturnsNull()
    {
        // Arrange
        var mockHttp = new Mock<HttpClient>();
        var options = Options.Create(_defaultOptions);
        var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);
        var modelUrl = "https://huggingface.co/models/nonexistent.gguf";

        // Act
        var result = await provider.GetCachedModelPathAsync(modelUrl);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCachedModelPathAsync_WithCachedUrl_ReturnsCachedPath()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/gemma-2b.gguf";
            var fileName = Path.GetFileName(new Uri(modelUrl).AbsolutePath);
            var cachedPath = Path.Combine(cacheDir, fileName);
            await File.WriteAllTextAsync(cachedPath, "mock data");

            var mockHttp = new Mock<HttpClient>();
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);

            // Act
            var result = await provider.GetCachedModelPathAsync(modelUrl);

            // Assert
            result.Should().Be(cachedPath);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCachedModelPathAsync_WithLocalPath_ReturnsNull()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var mockHttp = new Mock<HttpClient>();
            var options = Options.Create(_defaultOptions);
            var provider = new RuntimeDownloadModelProvider(mockHttp.Object, options, _logger);

            // Act
            var result = await provider.GetCachedModelPathAsync(tempFile);

            // Assert
            result.Should().BeNull("Local paths should not be treated as cached URLs");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EnsureModelAvailableAsync_CreatesDirectoryWhenMissing()
    {
        // Arrange
        var cacheDir = Path.Combine(Path.GetTempPath(), $"llm-cache-new-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            
            var modelUrl = "https://huggingface.co/models/gemma-2b.gguf";
            var testContent = "mock model data"u8.ToArray();
            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(testContent)
            };

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(mockResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var options = Options.Create(new LocalInferenceOptions { CacheDirectory = cacheDir });
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act
            var result = await provider.EnsureModelAvailableAsync(modelUrl);

            // Assert
            Directory.Exists(cacheDir).Should().BeTrue();
            File.Exists(result).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public void Constructor_ValidatesHttpClient()
    {
        // Act & Assert
        var options = Options.Create(_defaultOptions);
        this.Invoking(_ => new RuntimeDownloadModelProvider(null!, options, _logger))
            .Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("httpClient");
    }

    [Fact]
    public void Constructor_ValidatesOptions()
    {
        // Act & Assert
        var mockHttp = new Mock<HttpClient>();
        this.Invoking(_ => new RuntimeDownloadModelProvider(mockHttp.Object, null!, _logger))
            .Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    public void Constructor_ValidatesLogger()
    {
        // Act & Assert
        var mockHttp = new Mock<HttpClient>();
        var options = Options.Create(_defaultOptions);
        this.Invoking(_ => new RuntimeDownloadModelProvider(mockHttp.Object, options, null!))
            .Should()
            .Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public async Task CircuitBreakerCooldownExpires_AllowsRetry()
    {
        // Arrange
        var cacheDir = _defaultOptions.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        try
        {
            var modelUrl = "https://huggingface.co/models/retry-model.gguf";
            var testContent = "retry success"u8.ToArray();
            var callCount = 0;

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns((HttpRequestMessage req, CancellationToken ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // First call fails
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    }

                    // Second call succeeds
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(testContent)
                    });
                });

            var httpClient = new HttpClient(mockHandler.Object);
            var options = Options.Create(new LocalInferenceOptions
            {
                CacheDirectory = cacheDir,
                CircuitBreakerCooldownMinutes = 0 // Cooldown expires immediately
            });
            var provider = new RuntimeDownloadModelProvider(httpClient, options, _logger);

            // Act - first call fails
            await provider.Invoking(p => p.EnsureModelAvailableAsync(modelUrl))
                .Should()
                .ThrowAsync<InvalidOperationException>();

            // Give cooldown time to "expire"
            await Task.Delay(100);

            // Second call should succeed
            var result = await provider.EnsureModelAvailableAsync(modelUrl);

            // Assert
            result.Should().NotBeNullOrWhiteSpace();
            File.Exists(result).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
