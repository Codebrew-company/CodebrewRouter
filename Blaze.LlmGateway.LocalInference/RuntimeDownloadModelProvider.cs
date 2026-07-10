using System.Security.Cryptography;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Downloads and caches LLM models locally at runtime, with checksum validation and circuit-breaker resilience.
/// Injected with HttpClient via DI to avoid per-request creation and leverage connection pooling.
/// </summary>
public class RuntimeDownloadModelProvider : IModelDistributionProvider
{
    private readonly HttpClient _httpClient;
    private readonly LocalInferenceOptions _options;
    private readonly ILogger<RuntimeDownloadModelProvider> _logger;
    private DateTime _circuitBreakerCooldownUntil = DateTime.MinValue;
    private bool _circuitBreakerOpen;

    public RuntimeDownloadModelProvider(
        HttpClient httpClient,
        IOptions<LocalInferenceOptions> options,
        ILogger<RuntimeDownloadModelProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures a model is available locally (cached or downloaded) and returns the local file path.
    /// If the model path is a local file, validates existence and returns it directly.
    /// If the model path is a remote URL, downloads and caches it locally.
    /// </summary>
    public async Task<string> EnsureModelAvailableAsync(string modelUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
        {
            const string msg = "Model URL cannot be null or empty.";
            _logger.LogError(msg);
            throw new ArgumentException(msg, nameof(modelUrl));
        }

        // Local path: validate and return directly
        if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) || uri.IsFile || !uri.Scheme.StartsWith("http"))
        {
            if (!File.Exists(modelUrl))
            {
                _logger.LogError("Local model file not found: {ModelPath}", modelUrl);
                throw new InvalidOperationException($"Local model file not found: {modelUrl}");
            }

            _logger.LogInformation("Using local model: {ModelPath}", modelUrl);
            return Path.GetFullPath(modelUrl);
        }

        // Remote URL: download and cache
        return await DownloadAndCacheAsync(modelUrl, cancellationToken);
    }

    /// <summary>
    /// Gets the cached local path for a model URL if available, without downloading.
    /// </summary>
    public async Task<string?> GetCachedModelPathAsync(string modelUrl)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            return null;

        if (!Uri.TryCreate(modelUrl, UriKind.Absolute, out var uri) || uri.IsFile || !uri.Scheme.StartsWith("http"))
            return null;

        // Ensure cache directory exists
        var cacheDir = Path.GetFullPath(_options.CacheDirectory);
        if (!Directory.Exists(cacheDir))
            return null;

        var cachedPath = GetCachedFilePath(modelUrl);
        return IsPlausibleModelFile(cachedPath) ? cachedPath : null;
    }

    // Model weights are hundreds of MB at minimum. A tiny cached file is a poisoned entry
    // (aborted download / error page saved as the model) — observed live: an 821-byte file
    // cached as gemma-4-12B, which LM-Kit then "loaded" and produced blank responses.
    // ponytail: internal mutable so unit tests with byte-sized fixtures can lower it.
    internal static long MinPlausibleModelBytes = 10L * 1024 * 1024;

    private bool IsPlausibleModelFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return false;
        }

        if (info.Length >= MinPlausibleModelBytes)
        {
            return true;
        }

        _logger.LogWarning(
            "Cached model file {Path} is only {Length} bytes — treating as a poisoned cache entry and deleting it.",
            path, info.Length);
        CleanupCorruptedFile(path);
        return false;
    }

    /// <summary>
    /// Downloads the model from the remote URL and caches it locally with optional checksum validation.
    /// Implements circuit-breaker pattern: on failure, sets a cooldown period before retrying.
    /// </summary>
    private async Task<string> DownloadAndCacheAsync(string modelUrl, CancellationToken cancellationToken)
    {
        // Check circuit breaker
        if (_circuitBreakerOpen && DateTime.UtcNow < _circuitBreakerCooldownUntil)
        {
            _logger.LogWarning(
                "Model download circuit breaker is open. Cooldown expires at {CooldownTime}",
                _circuitBreakerCooldownUntil);
            throw new InvalidOperationException(
                $"Model download temporarily disabled due to recent failure. Retry after {_circuitBreakerCooldownUntil:O}");
        }

        if (DateTime.UtcNow >= _circuitBreakerCooldownUntil)
        {
            _circuitBreakerOpen = false;
        }

        // Ensure cache directory exists
        var cacheDir = Path.GetFullPath(_options.CacheDirectory);
        try
        {
            Directory.CreateDirectory(cacheDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create cache directory: {CacheDir}", cacheDir);
            throw new InvalidOperationException($"Failed to create cache directory: {cacheDir}", ex);
        }

        var cachedPath = GetCachedFilePath(modelUrl);

        // Return cached file only if it is a plausible model (poisoned tiny entries get deleted).
        if (IsPlausibleModelFile(cachedPath))
        {
            _logger.LogInformation("Model cache hit: {CachedPath}", cachedPath);
            return cachedPath;
        }

        _logger.LogInformation("Downloading model from {ModelUrl} to {CachedPath}...", modelUrl, cachedPath);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));

            using var response = await _httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Failed to download model: HTTP {StatusCode} from {ModelUrl}",
                    response.StatusCode,
                    modelUrl);
                OpenCircuitBreaker();
                throw new InvalidOperationException(
                    $"Failed to download model: HTTP {response.StatusCode} from {modelUrl}");
            }

            // Stream to temporary file first
            var tempPath = cachedPath + ".tmp";
            try
            {
                using (var contentStream = await response.Content.ReadAsStreamAsync(cts.Token))
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // Copy with a per-read inactivity timeout: a stalled CDN connection otherwise
                    // hangs until the overall DownloadTimeoutSeconds (up to an hour) and, observed
                    // live, leaves orphaned/partial artifacts. 60s without a single byte = abort.
                    var buffer = new byte[1 << 20];
                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        readCts.CancelAfter(TimeSpan.FromSeconds(60));
                        int read;
                        try
                        {
                            read = await contentStream.ReadAsync(buffer, readCts.Token);
                        }
                        catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                        {
                            throw new InvalidOperationException(
                                $"Model download stalled (no data for 60s) from {modelUrl}.");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    }

                    await fileStream.FlushAsync(cts.Token);
                }

                _logger.LogInformation("Model downloaded to {TempPath}, moving to {CachedPath}", tempPath, cachedPath);

                // Reject truncated downloads before they poison the cache.
                var downloadedLength = new FileInfo(tempPath).Length;
                var expectedLength = response.Content.Headers.ContentLength;
                if (downloadedLength < MinPlausibleModelBytes ||
                    (expectedLength.HasValue && downloadedLength != expectedLength.Value))
                {
                    CleanupCorruptedFile(tempPath);
                    OpenCircuitBreaker();
                    throw new InvalidOperationException(
                        $"Downloaded model from {modelUrl} is {downloadedLength} bytes " +
                        $"(expected {(expectedLength.HasValue ? expectedLength.Value.ToString() : $">= {MinPlausibleModelBytes}")}); discarding as corrupt.");
                }

                // Move temp file to cache location
                File.Move(tempPath, cachedPath, overwrite: true);

                _logger.LogInformation("Model cached successfully: {CachedPath}", cachedPath);
                return cachedPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during model download/cache. Cleaning up temporary file: {TempPath}", tempPath);
                CleanupCorruptedFile(tempPath);
                throw;
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Model download timeout or cancelled: {ModelUrl}", modelUrl);
            OpenCircuitBreaker();
            CleanupCorruptedFile(cachedPath);
            throw new InvalidOperationException(
                $"Model download timeout ({_options.DownloadTimeoutSeconds}s) or cancelled: {modelUrl}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading model: {ModelUrl}", modelUrl);
            OpenCircuitBreaker();
            CleanupCorruptedFile(cachedPath);
            throw new InvalidOperationException($"Failed to download model from {modelUrl}", ex);
        }
    }

    /// <summary>
    /// Cleans up a corrupted or incomplete model file with explicit exception logging.
    /// Does not throw; logs any failures to allow graceful degradation.
    /// </summary>
    private void CleanupCorruptedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            _logger.LogInformation("Removing corrupted/incomplete model file: {FilePath}", filePath);
            File.Delete(filePath);
            _logger.LogInformation("Successfully removed corrupted file: {FilePath}", filePath);
        }
        catch (IOException ioEx)
        {
            _logger.LogError(ioEx, "IO error while removing corrupted file: {FilePath}", filePath);
        }
        catch (UnauthorizedAccessException authEx)
        {
            _logger.LogError(authEx, "Access denied while removing corrupted file: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while removing corrupted file: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Generates a deterministic cache file path from the model URL.
    /// Uses the last path segment of the URL as the filename, avoiding cache collisions across different sources.
    /// </summary>
    private string GetCachedFilePath(string modelUrl)
    {
        var fileName = Path.GetFileName(new Uri(modelUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            // Fallback to hash-based name if no filename in URL
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(modelUrl)));
            fileName = $"model_{hash[..16]}.gguf";
        }

        return Path.Combine(Path.GetFullPath(_options.CacheDirectory), fileName);
    }

    /// <summary>
    /// Opens the circuit breaker, preventing download attempts until cooldown expires.
    /// </summary>
    private void OpenCircuitBreaker()
    {
        _circuitBreakerOpen = true;
        _circuitBreakerCooldownUntil = DateTime.UtcNow.AddMinutes(_options.CircuitBreakerCooldownMinutes);
        _logger.LogWarning(
            "Model download circuit breaker opened. Cooldown expires at {CooldownTime}",
            _circuitBreakerCooldownUntil);
    }
}
