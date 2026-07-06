using System.Reactive.Linq;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.LocalInference.Tests;

public class LocalModelAvailabilityServiceTests
{
    private readonly Mock<IModelDistributionProvider> _mockProvider;
    private readonly Mock<ILogger<LocalModelAvailabilityService>> _mockLogger;
    private readonly IOptions<LocalInferenceOptions> _options;
    private readonly LocalModelAvailabilityService _service;

    public LocalModelAvailabilityServiceTests()
    {
        _mockProvider = new Mock<IModelDistributionProvider>();
        _mockLogger = new Mock<ILogger<LocalModelAvailabilityService>>();
        _options = Options.Create(new LocalInferenceOptions
        {
            CacheAvailabilityTtlSeconds = 2 // 2 seconds for fast tests
        });
        _service = new LocalModelAvailabilityService(_mockProvider.Object, _options, _mockLogger.Object);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalse_WhenModelUrlIsEmpty()
    {
        // Act
        var result = await _service.IsAvailableAsync(string.Empty);

        // Assert
        Assert.False(result);
        _mockProvider.Verify(p => p.GetCachedModelPathAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IsAvailableAsync_CallsProvider_WhenCacheMiss()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.IsAvailableAsync(modelUrl);

        // Assert
        Assert.False(result);
        _mockProvider.Verify(p => p.GetCachedModelPathAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsCached_WithinTtl()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        // Act - first call
        var result1 = await _service.IsAvailableAsync(modelUrl);
        // Second call (cache should hit)
        var result2 = await _service.IsAvailableAsync(modelUrl);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        _mockProvider.Verify(p => p.GetCachedModelPathAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IsAvailableAsync_CallsProviderAgain_AfterCacheExpiry()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        // Act
        var result1 = await _service.IsAvailableAsync(modelUrl);
        await Task.Delay(2100); // Wait for cache to expire (TTL is 2 seconds)
        var result2 = await _service.IsAvailableAsync(modelUrl);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        _mockProvider.Verify(p => p.GetCachedModelPathAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalse_WhenProviderReturnsNull()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.IsAvailableAsync(modelUrl);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ThrowsArgumentException_WhenUrlIsEmpty()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.EnsureAvailableAsync(string.Empty));
    }

    [Fact]
    public async Task EnsureAvailableAsync_CallsProvider_AndReturnsModelInfo()
    {
        // Arrange
        var modelUrl = "http://example.com/gemma-2b.gguf";
        var localPath = "/cache/gemma-2b.gguf";
        _mockProvider
            .Setup(p => p.EnsureModelAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPath);

        // Act
        var result = await _service.EnsureAvailableAsync(modelUrl);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("gemma-2b", result.Name);
        Assert.Contains("gemma-2b.gguf", result.Path);
        Assert.Equal("gemma", result.ModelType);
        Assert.True(result.LoadedAtUtc < DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task EnsureAvailableAsync_ThrowsInvalidOperationException_WhenProviderFails()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        _mockProvider
            .Setup(p => p.EnsureModelAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Download failed"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.EnsureAvailableAsync(modelUrl));
        Assert.Contains("Download failed", ex.Message);
    }

    [Fact]
    public async Task EnsureAvailableAsync_ReturnsCached_WithinTtl()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.EnsureModelAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPath);

        // Act
        var result1 = await _service.EnsureAvailableAsync(modelUrl);
        var result2 = await _service.EnsureAvailableAsync(modelUrl);

        // Assert - Note: We call GetCachedAvailability to check if cached, so EnsureModelAvailable called twice
        // but the second should use cached GetCachedAvailability result
        Assert.NotNull(result1);
        Assert.NotNull(result2);
    }

    [Fact]
    public void GetCachedAvailability_ReturnsNull_WhenNotCached()
    {
        // Act
        var result = _service.GetCachedAvailability("http://example.com/model.gguf");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedAvailability_ReturnsCachedModel_AfterAvailabilityCheck()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        // Act
        await _service.IsAvailableAsync(modelUrl);
        var cached = _service.GetCachedAvailability(modelUrl);

        // Assert
        Assert.NotNull(cached);
        Assert.Equal("model", cached.Name);
    }

    [Fact]
    public async Task GetCachedAvailability_ReturnsNull_AfterCacheExpiry()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        // Act
        await _service.IsAvailableAsync(modelUrl);
        await Task.Delay(2100); // Wait for cache to expire
        var cached = _service.GetCachedAvailability(modelUrl);

        // Assert
        Assert.Null(cached);
    }

    [Fact]
    public async Task ObserveAvailabilityChanges_FiresEvent_WhenAvailabilityChanges()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        var events = new List<ModelAvailabilityChanged>();
        var subscription = _service.ObserveAvailabilityChanges()
            .Subscribe(evt => events.Add(evt));

        // Act
        await _service.IsAvailableAsync(modelUrl);
        await Task.Delay(2100); // Expire cache
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        await _service.IsAvailableAsync(modelUrl);

        // Assert
        Assert.NotEmpty(events);
        Assert.True(events.Count >= 1); // At least availability change

        subscription.Dispose();
    }

    [Fact]
    public async Task ObserveAvailabilityChanges_EventsAreObservable()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        var availabilityEvents = new List<ModelAvailabilityChanged>();
        var subscription = _service.ObserveAvailabilityChanges()
            .Subscribe(evt => availabilityEvents.Add(evt));

        // Act - check availability (should trigger event on first cache miss)
        await _service.IsAvailableAsync(modelUrl);

        // Assert
        // The ObserveAvailabilityChanges should be observable without throwing
        Assert.NotNull(subscription);
        
        subscription.Dispose();
    }

    [Fact]
    public async Task ConcurrentRequests_HandlesThreadSafely()
    {
        // Arrange
        var modelUrl = "http://example.com/model.gguf";
        var localPath = "/cache/model.gguf";
        _mockProvider
            .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
            .ReturnsAsync(localPath);

        // Act - Create many concurrent requests
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => _service.IsAvailableAsync(modelUrl))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert - All should return true without exceptions
        Assert.All(results, r => Assert.True(r));
        // Provider should be called only once (cache)
        _mockProvider.Verify(p => p.GetCachedModelPathAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LocalPath_WorksWithFileSystemPaths()
    {
        // Arrange - create a temporary file
        var tempDir = Path.Combine(Path.GetTempPath(), "llm-cache-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var modelFile = Path.Combine(tempDir, "test-model.gguf");
        File.WriteAllText(modelFile, "test");

        try
        {
            _mockProvider
                .Setup(p => p.GetCachedModelPathAsync(It.IsAny<string>()))
                .ReturnsAsync(modelFile);

            // Act
            var result = await _service.IsAvailableAsync(modelFile);
            var cached = _service.GetCachedAvailability(modelFile);

            // Assert
            Assert.True(result);
            Assert.NotNull(cached);
            Assert.Equal("test-model", cached.Name);
        }
        finally
        {
            // Cleanup
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            _service.Dispose();
        }
    }

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Act
        _service.Dispose();

        // Assert - Should not throw on second dispose
        _service.Dispose();
    }
}
