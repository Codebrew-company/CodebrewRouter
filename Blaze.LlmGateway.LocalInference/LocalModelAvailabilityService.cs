using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Manages local model availability with TTL-based caching, thread-safe access, and observable events.
/// Wraps RuntimeDownloadModelProvider to track model state across the application.
/// </summary>
public class LocalModelAvailabilityService : ILocalModelAvailability, IDisposable
{
    private readonly IModelDistributionProvider _provider;
    private readonly LocalInferenceOptions _options;
    private readonly ILogger<LocalModelAvailabilityService> _logger;
    private readonly Subject<ModelAvailabilityChanged> _availabilityChangedSubject;
    private readonly ConcurrentDictionary<string, CachedAvailability> _cache;
    private readonly ReaderWriterLockSlim _cacheLock;

    private record CachedAvailability(
        LocalModelInfo? Model,
        DateTime CachedAtUtc,
        bool IsAvailable);

    public LocalModelAvailabilityService(
        IModelDistributionProvider provider,
        IOptions<LocalInferenceOptions> options,
        ILogger<LocalModelAvailabilityService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _availabilityChangedSubject = new Subject<ModelAvailabilityChanged>();
        _cache = new ConcurrentDictionary<string, CachedAvailability>();
        _cacheLock = new ReaderWriterLockSlim();
    }

    /// <summary>
    /// Configuration in seconds for how long to cache availability state.
    /// Can be overridden via options or dependency injection if needed.
    /// </summary>
    private TimeSpan CacheTtl => TimeSpan.FromSeconds(_options.CacheAvailabilityTtlSeconds ?? 60);

    public async Task<bool> IsAvailableAsync(string modelUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            return false;

        var normalized = NormalizeModelUrl(modelUrl);

        // Check cache first
        _cacheLock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(normalized, out var cached) && !IsCacheExpired(cached))
            {
                _logger.LogDebug("Cache hit for model availability: {ModelUrl}", normalized);
                return cached.IsAvailable;
            }
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }

        // Cache miss or expired - check actual availability
        _logger.LogDebug("Cache miss for model availability: {ModelUrl}", normalized);
        return await CheckAndCacheAvailabilityAsync(normalized, cancellationToken);
    }

    public async Task<LocalModelInfo> EnsureAvailableAsync(string modelUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            throw new ArgumentException("Model URL cannot be null or empty.", nameof(modelUrl));

        var normalized = NormalizeModelUrl(modelUrl);

        try
        {
            // Get cached availability if fresh
            _cacheLock.EnterReadLock();
            try
            {
                if (_cache.TryGetValue(normalized, out var cached) && !IsCacheExpired(cached))
                {
                    if (cached.Model != null && cached.IsAvailable)
                    {
                        _logger.LogDebug("Using cached available model: {ModelUrl}", normalized);
                        return cached.Model;
                    }
                }
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            // Download or verify availability
            var localPath = await _provider.EnsureModelAvailableAsync(normalized, cancellationToken);
            var model = CreateModelInfo(normalized, localPath);

            // Update cache with success
            var wasAvailable = GetCachedAvailability(normalized) != null;
            UpdateCache(normalized, model, isAvailable: true);

            if (!wasAvailable)
            {
                FireAvailabilityChangedEvent(model, wasAvailable: false, "Model ensured available");
            }

            _logger.LogInformation("Model ensured available: {ModelUrl} -> {LocalPath}", normalized, localPath);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure model available: {ModelUrl}", normalized);

            // Update cache with failure
            var wasAvailable = GetCachedAvailability(normalized) != null;
            if (wasAvailable)
            {
                var lastModel = GetCachedAvailability(normalized)!;
                UpdateCache(normalized, null, isAvailable: false);
                FireAvailabilityChangedEvent(lastModel, wasAvailable: true, $"Availability check failed: {ex.Message}");
            }

            throw new InvalidOperationException(
                $"Failed to ensure model {normalized} is available: {ex.Message}", ex);
        }
    }

    public LocalModelInfo? GetCachedAvailability(string modelUrl)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            return null;

        var normalized = NormalizeModelUrl(modelUrl);

        _cacheLock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(normalized, out var cached) && !IsCacheExpired(cached))
            {
                _logger.LogDebug("Retrieving cached model info: {ModelUrl}", normalized);
                return cached.Model;
            }
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }

        return null;
    }

    public IObservable<ModelAvailabilityChanged> ObserveAvailabilityChanges()
    {
        return _availabilityChangedSubject.AsObservable();
    }

    public void Dispose()
    {
        _availabilityChangedSubject?.Dispose();
        _cacheLock?.Dispose();
    }

    /// <summary>
    /// Checks actual model availability and updates the cache.
    /// </summary>
    private async Task<bool> CheckAndCacheAvailabilityAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        try
        {
            var cachedPath = await _provider.GetCachedModelPathAsync(normalizedUrl);
            if (cachedPath != null)
            {
                var model = CreateModelInfo(normalizedUrl, cachedPath);
                var wasAvailable = GetCachedAvailability(normalizedUrl) != null;

                UpdateCache(normalizedUrl, model, isAvailable: true);

                if (!wasAvailable)
                {
                    FireAvailabilityChangedEvent(model, wasAvailable: false, "Model cached and available");
                }

                _logger.LogInformation("Model availability confirmed: {ModelUrl}", normalizedUrl);
                return true;
            }

            // Get model BEFORE clearing cache so we can fire the event with the model details
            var lastModel = GetCachedAvailability(normalizedUrl);
            UpdateCache(normalizedUrl, null, isAvailable: false);

            if (lastModel != null)
            {
                FireAvailabilityChangedEvent(lastModel, wasAvailable: true, "Model no longer available");
            }

            _logger.LogWarning("Model not available: {ModelUrl}", normalizedUrl);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking model availability: {ModelUrl}", normalizedUrl);
            UpdateCache(normalizedUrl, null, isAvailable: false);
            return false;
        }
    }

    /// <summary>
    /// Creates LocalModelInfo from a model URL and local path.
    /// </summary>
    private LocalModelInfo CreateModelInfo(string modelUrl, string localPath)
    {
        var fileInfo = new FileInfo(localPath);
        var modelType = ExtractModelType(modelUrl);
        var modelName = Path.GetFileNameWithoutExtension(localPath);

        return new LocalModelInfo
        {
            Name = modelName,
            Path = Path.GetFullPath(localPath),
            ModelType = modelType,
            LoadedAtUtc = DateTime.UtcNow,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileChecksum = null // Could be enhanced with actual checksum computation
        };
    }

    /// <summary>
    /// Extracts model type from URL or path (e.g., "gemma" from "gemma-2b-it.gguf").
    /// </summary>
    private string ExtractModelType(string modelUrl)
    {
        var fileName = Path.GetFileName(new Uri(modelUrl, UriKind.RelativeOrAbsolute).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return "unknown";

        // Extract first word (before hyphen or underscore)
        var words = fileName.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[0].ToLowerInvariant() : "unknown";
    }

    /// <summary>
    /// Normalizes model URLs for consistent cache lookups.
    /// </summary>
    private string NormalizeModelUrl(string modelUrl)
    {
        return Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : Path.GetFullPath(modelUrl);
    }

    /// <summary>
    /// Checks if a cached entry has expired based on TTL.
    /// </summary>
    private bool IsCacheExpired(CachedAvailability cached)
    {
        return DateTime.UtcNow - cached.CachedAtUtc > CacheTtl;
    }

    /// <summary>
    /// Updates the availability cache with write lock.
    /// </summary>
    private void UpdateCache(string modelUrl, LocalModelInfo? model, bool isAvailable)
    {
        _cacheLock.EnterWriteLock();
        try
        {
            var entry = new CachedAvailability(model, DateTime.UtcNow, isAvailable);
            _cache.AddOrUpdate(modelUrl, entry, (_, _) => entry);
            _logger.LogDebug("Cache updated for model: {ModelUrl} (Available={IsAvailable})", modelUrl, isAvailable);
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Fires availability changed event to subscribers.
    /// Exceptions in subscriber callbacks do not propagate.
    /// </summary>
    private void FireAvailabilityChangedEvent(LocalModelInfo model, bool wasAvailable, string reason)
    {
        try
        {
            var evt = new ModelAvailabilityChanged
            {
                Model = model,
                WasAvailable = wasAvailable,
                IsAvailable = !wasAvailable,
                Reason = reason,
                ChangedAtUtc = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Model availability changed: {ModelName} ({WasAvailable} -> {IsAvailable}). Reason: {Reason}",
                model.Name, wasAvailable, !wasAvailable, reason);

            _availabilityChangedSubject.OnNext(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error firing availability changed event for model: {ModelName}", model.Name);
            // Do not propagate: this is an event notification, not a critical path
        }
    }
}
