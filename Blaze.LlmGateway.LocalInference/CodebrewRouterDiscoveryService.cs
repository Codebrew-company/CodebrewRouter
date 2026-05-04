using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Discovers remote LLM models available via CodebrewRouter's /v1/models endpoint.
/// Implements TTL caching, circuit breaker, online detection, and observable events.
/// </summary>
public class CodebrewRouterDiscoveryService : ICodebrewRouterDiscoveryService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CodebrewRouterDiscoveryService> _logger;
    private readonly Subject<DiscoveryChanged> _discoveryChangedSubject;

    private RemoteDiscoveryResult? _cachedResult;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private DateTime _circuitBreakerCooldownUntil = DateTime.MinValue;
    private bool _circuitBreakerOpen;
    private readonly object _cacheLock = new();

    private const int DefaultCacheTtlSeconds = 300; // 5 minutes
    private const int DefaultCircuitBreakerCooldownMinutes = 5;
    private const int DefaultRequestTimeoutSeconds = 30;
    private const int DefaultMaxFailuresBeforeBreakerOpen = 5;

    private int _consecutiveFailures;

    /// <summary>
    /// CodebrewRouter endpoint URL (e.g., "http://localhost:8080").
    /// Can be configured via dependency injection or configuration.
    /// </summary>
    private readonly string _codebrewRouterBaseUrl;

    public CodebrewRouterDiscoveryService(
        HttpClient httpClient,
        ILogger<CodebrewRouterDiscoveryService> logger,
        string codebrewRouterBaseUrl = "http://localhost:8080")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _codebrewRouterBaseUrl = codebrewRouterBaseUrl ?? throw new ArgumentNullException(nameof(codebrewRouterBaseUrl));
        _discoveryChangedSubject = new Subject<DiscoveryChanged>();
    }

    public async Task<RemoteDiscoveryResult> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        // Check circuit breaker
        if (_circuitBreakerOpen && DateTime.UtcNow < _circuitBreakerCooldownUntil)
        {
            _logger.LogWarning(
                "Discovery circuit breaker is open. Cooldown expires at {CooldownTime}",
                _circuitBreakerCooldownUntil);
            return GetCachedDiscoveryOrOffline();
        }

        if (DateTime.UtcNow >= _circuitBreakerCooldownUntil && _circuitBreakerOpen)
        {
            _circuitBreakerOpen = false;
            _consecutiveFailures = 0;
            _logger.LogInformation("Discovery circuit breaker recovered");
        }

        // Check cache
        lock (_cacheLock)
        {
            if (_cachedResult != null && !IsCacheExpired())
            {
                _logger.LogDebug("Returning cached discovery result");
                return _cachedResult;
            }
        }

        // Perform discovery
        return await PerformDiscoveryAsync(cancellationToken);
    }

    public RemoteDiscoveryResult? GetCachedDiscovery()
    {
        lock (_cacheLock)
        {
            if (_cachedResult != null && !IsCacheExpired())
            {
                _logger.LogDebug("Retrieving cached discovery result");
                return _cachedResult;
            }
        }
        return null;
    }

    public IObservable<DiscoveryChanged> ObserveDiscoveryChanges()
    {
        return _discoveryChangedSubject.AsObservable();
    }

    public void Dispose()
    {
        _discoveryChangedSubject?.Dispose();
    }

    /// <summary>
    /// Performs the actual HTTP discovery call to CodebrewRouter.
    /// </summary>
    private async Task<RemoteDiscoveryResult> PerformDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildModelsEndpointUrl();
            _logger.LogDebug("Discovering models from {Url}", url);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds));

            var response = await _httpClient.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to discover models: HTTP {StatusCode} from {Url}",
                    response.StatusCode, url);
                return HandleDiscoveryFailure("HTTP error: " + response.StatusCode);
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            var models = ParseModelsResponse(content);

            var result = new RemoteDiscoveryResult(
                Models: models,
                DiscoveredAtUtc: DateTime.UtcNow,
                IsOnline: true,
                ErrorMessage: null);

            UpdateCacheAndFireEvent(result, "Models discovered successfully");
            _consecutiveFailures = 0;
            _logger.LogInformation("Successfully discovered {ModelCount} remote models", models.Count);

            return result;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Discovery timeout or cancelled");
            return HandleDiscoveryFailure($"Timeout after {DefaultRequestTimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error during discovery: {Message}", ex.Message);
            return HandleDiscoveryFailure($"HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during discovery: {Message}", ex.Message);
            return HandleDiscoveryFailure($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the OpenAI-format /v1/models response.
    /// </summary>
    private IReadOnlyList<RemoteModelInfo> ParseModelsResponse(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataArray))
            {
                _logger.LogWarning("Response missing 'data' array");
                return [];
            }

            var models = new List<RemoteModelInfo>();
            foreach (var modelElement in dataArray.EnumerateArray())
            {
                if (modelElement.TryGetProperty("id", out var idProp))
                {
                    var name = idProp.GetString() ?? "unknown";
                    var provider = ExtractProvider(name);

                    var model = new RemoteModelInfo
                    {
                        Name = name,
                        Provider = provider,
                        TokenLimit = null,
                        CostInfo = null,
                        IsAvailable = true
                    };

                    models.Add(model);
                }
            }

            _logger.LogInformation("Parsed {ModelCount} models from discovery response", models.Count);
            return models;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse discovery response as JSON");
            return [];
        }
    }

    /// <summary>
    /// Handles discovery failure, managing circuit breaker and returning cached/offline result.
    /// </summary>
    private RemoteDiscoveryResult HandleDiscoveryFailure(string errorMessage)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= DefaultMaxFailuresBeforeBreakerOpen)
        {
            OpenCircuitBreaker();
        }

        return GetCachedDiscoveryOrOffline(errorMessage);
    }

    /// <summary>
    /// Returns cached result if available, otherwise returns offline result.
    /// </summary>
    private RemoteDiscoveryResult GetCachedDiscoveryOrOffline(string? errorMessage = null)
    {
        lock (_cacheLock)
        {
            if (_cachedResult != null)
            {
                _logger.LogInformation("Returning stale cached discovery (online={IsOnline})", _cachedResult.IsOnline);
                return _cachedResult with { IsOnline = false, ErrorMessage = errorMessage };
            }
        }

        // No cache available - return offline result
        _logger.LogWarning("No cached discovery available, returning offline result");
        return new RemoteDiscoveryResult(
            Models: [],
            DiscoveredAtUtc: DateTime.UtcNow,
            IsOnline: false,
            ErrorMessage: errorMessage ?? "CodebrewRouter is offline");
    }

    /// <summary>
    /// Extracts provider name from model identifier.
    /// </summary>
    private string ExtractProvider(string modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return "unknown";

        var lowerName = modelName.ToLowerInvariant();
        if (lowerName.Contains("gpt") || lowerName.Contains("openai"))
            return "OpenAI";
        if (lowerName.Contains("claude") || lowerName.Contains("anthropic"))
            return "Anthropic";
        if (lowerName.Contains("gemini") || lowerName.Contains("google"))
            return "Google";
        if (lowerName.Contains("ollama"))
            return "Ollama";
        if (lowerName.Contains("azure"))
            return "Azure";

        return "Unknown";
    }

    /// <summary>
    /// Builds the full models endpoint URL.
    /// </summary>
    private string BuildModelsEndpointUrl()
    {
        var baseUrl = _codebrewRouterBaseUrl.TrimEnd('/');
        return $"{baseUrl}/v1/models";
    }

    /// <summary>
    /// Checks if cached result has expired.
    /// </summary>
    private bool IsCacheExpired()
    {
        return DateTime.UtcNow - _cachedAtUtc > TimeSpan.FromSeconds(DefaultCacheTtlSeconds);
    }

    /// <summary>
    /// Updates cache and fires discovery changed event.
    /// </summary>
    private void UpdateCacheAndFireEvent(RemoteDiscoveryResult result, string reason)
    {
        RemoteDiscoveryResult? previousResult;
        lock (_cacheLock)
        {
            previousResult = _cachedResult;
            _cachedResult = result;
            _cachedAtUtc = DateTime.UtcNow;
        }

        try
        {
            var evt = new DiscoveryChanged
            {
                Result = result,
                PreviousResult = previousResult,
                Reason = reason,
                ChangedAtUtc = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Discovery result changed: {ModelCount} models. Reason: {Reason}",
                result.Models.Count, reason);

            _discoveryChangedSubject.OnNext(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error firing discovery changed event");
        }
    }

    /// <summary>
    /// Opens the circuit breaker, preventing discovery attempts until cooldown expires.
    /// </summary>
    private void OpenCircuitBreaker()
    {
        _circuitBreakerOpen = true;
        _circuitBreakerCooldownUntil = DateTime.UtcNow.AddMinutes(DefaultCircuitBreakerCooldownMinutes);
        _logger.LogWarning(
            "Discovery circuit breaker opened after {FailureCount} failures. Cooldown until {CooldownTime}",
            _consecutiveFailures, _circuitBreakerCooldownUntil);
    }
}
