namespace Blaze.LlmGateway.Core.Provider;

/// <summary>
/// Configuration options for CodebrewRouterProvider.
/// Supports all deployment scenarios: mobile (MAUI), desktop, Aspire.
/// </summary>
public class CodebrewRouterProviderOptions
{
    /// <summary>
    /// Local inference endpoint (required).
    /// Examples: "http://localhost:11434" (Ollama), "http://192.168.1.100:11434" (LAN), "http://127.0.0.1:58484" (Foundry Local).
    /// </summary>
    public required string LocalEndpoint { get; set; }

    /// <summary>
    /// Remote model discovery endpoint (optional).
    /// If null, remote discovery is disabled.
    /// Example: "http://localhost:5273" (CodebrewRouter gateway).
    /// </summary>
    public string? RemoteDiscoveryEndpoint { get; set; }

    /// <summary>
    /// TTL for local model availability cache (seconds).
    /// Default: 60 seconds.
    /// </summary>
    public int CacheAvailabilityTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Polling interval for remote discovery (seconds).
    /// Default: 300 seconds (5 minutes).
    /// </summary>
    public int DiscoveryPollingIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// HTTP timeout for discovery requests (seconds).
    /// Default: 30 seconds.
    /// </summary>
    public int DiscoveryTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Circuit breaker failure threshold before cooldown.
    /// Default: 5 consecutive failures.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker cooldown duration (minutes).
    /// Default: 5 minutes.
    /// </summary>
    public int CircuitBreakerCooldownMinutes { get; set; } = 5;

    /// <summary>
    /// Enable health checks.
    /// Default: true (always registered, can be disabled).
    /// </summary>
    public bool HealthChecksEnabled { get; set; } = true;

    /// <summary>
    /// Health check event timeout (seconds) before assuming no event.
    /// Default: 300 seconds (5 minutes).
    /// </summary>
    public int HealthCheckEventTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Local GGUF file path or remote URL for the local Gemma model.
    /// </summary>
    public string LocalModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Directory used when <see cref="LocalModelPath"/> is a remote URL and must be cached.
    /// </summary>
    public string CacheDirectory { get; set; } = ".llm-cache";

    /// <summary>
    /// Friendly local profile name for diagnostics and model catalog entries.
    /// </summary>
    public string LocalModelProfile { get; set; } = "gemma-4-e4b-it";

    /// <summary>
    /// Maximum local context window passed to LLamaSharp.
    /// </summary>
    public int LocalMaxContextTokens { get; set; } = 8192;

    /// <summary>
    /// CPU thread count. Zero means LLamaSharp default selection.
    /// </summary>
    public int LocalThreadCount { get; set; } = 0;

    /// <summary>
    /// Number of model layers to offload to GPU. Zero keeps CPU-only behavior.
    /// </summary>
    public int LocalGpuLayerCount { get; set; } = 0;

    /// <summary>
    /// Test mode: skip health check subscriptions and initialization.
    /// Default: false (production mode).
    /// </summary>
    public bool TestMode { get; set; } = false;
}
