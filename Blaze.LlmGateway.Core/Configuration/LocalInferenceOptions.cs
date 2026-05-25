namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for local LLM inference via Gemma using LM-Kit.
/// Binds from <c>LlmGateway:LocalInference</c> in appsettings.
/// </summary>
public class LocalInferenceOptions
{
    /// <summary>
    /// When false, local inference is disabled and LocalGemmaChatClient falls back to NoOp.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Local file path or remote URL to the Gemma GGUF model file.
    /// If a remote URL, the model will be downloaded and cached locally.
    /// Example: "/models/gemma-2b-it-q4_k_m.gguf" or "https://huggingface.co/.../gemma-2b-it-q4_k_m.gguf"
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Directory where downloaded models are cached. Only used if <c>ModelPath</c> is a remote URL.
    /// Default: "{CurrentDirectory}/.llm-cache"
    /// </summary>
    public string CacheDirectory { get; set; } = ".llm-cache";

    /// <summary>
    /// When true, downloaded model files are validated against SHA256 checksum before use.
    /// If provided in model metadata, checksum must match or the file is treated as corrupted.
    /// Default: true.
    /// </summary>
    public bool EnableChecksumValidation { get; set; } = true;

    /// <summary>
    /// Maximum time (in seconds) to wait for a model download to complete.
    /// If exceeded, the download is cancelled and an exception is thrown.
    /// Default: 3600 (1 hour).
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// How long to leave the model downloader circuit open after a failure before retrying.
    /// Mirrors <c>OllamaTaskClassifier</c> pattern for circuit breaker resilience.
    /// Default: 5 minutes.
    /// </summary>
    public int CircuitBreakerCooldownMinutes { get; set; } = 5;

    /// <summary>
    /// Number of CPU cores to use for inference. If 0 or less, defaults to system logical core count.
    /// Default: 0 (auto-detect).
    /// </summary>
    public int ThreadCount { get; set; } = 0;

    /// <summary>
    /// Maximum context window size (in tokens) for the Gemma model.
    /// Typical values: 2048 for gemma-2b, 8192 for gemma-7b.
    /// Default: 2048.
    /// </summary>
    public int MaxContextTokens { get; set; } = 2048;

    /// <summary>
    /// Temperature for inference (0.0-2.0). Controls randomness of output.
    /// Default: 0.7.
    /// </summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// Top-p (nucleus sampling) parameter for output diversity.
    /// Default: 0.9.
    /// </summary>
    public float TopP { get; set; } = 0.9f;

    /// <summary>
    /// Optional system prompt to prepend to every Gemma conversation.
    /// When null, a minimal default system prompt is used.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// When true, the local Gemma provider is loaded and primed during application startup.
    /// Default: true.
    /// </summary>
    public bool WarmupEnabled { get; set; } = true;

    /// <summary>
    /// Prompt used for the startup warmup pass.
    /// Keep this short because it exists only to initialize the first inference path.
    /// Default: "ready".
    /// </summary>
    public string WarmupPrompt { get; set; } = "ready";

    /// <summary>
    /// Maximum tokens to request during startup warmup.
    /// Default: 1.
    /// </summary>
    public int WarmupMaxOutputTokens { get; set; } = 1;

    /// <summary>
    /// Maximum time in seconds to wait for startup warmup.
    /// Default: 120 seconds.
    /// </summary>
    public int WarmupTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// When true, startup fails if the configured local model cannot be loaded and primed.
    /// Default: true for offline-first operation.
    /// </summary>
    public bool BlockStartupUntilWarm { get; set; } = true;

    /// <summary>
    /// Cache TTL in seconds for model availability checks in LocalModelAvailabilityService.
    /// When a model is checked as available, the result is cached for this duration.
    /// Default: 60 seconds.
    /// </summary>
    public int? CacheAvailabilityTtlSeconds { get; set; } = 60;
}
