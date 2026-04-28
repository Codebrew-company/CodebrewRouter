namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for the gemma4:e4b-powered prompt-optimization pre-stage used by
/// <c>codebrewRouter</c>. Binds from <c>LlmGateway:PromptCleanup</c> in appsettings.
/// </summary>
public class PromptCleanupOptions
{
    /// <summary>
    /// When false, prompt cleanup is skipped entirely (a no-op cleaner is used).
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum tokens the cleaner model may produce. Cleaned prompts should always be
    /// shorter than the original; this caps runaway output. Default: 256.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 256;

    /// <summary>
    /// Sampling temperature for the cleaner model. Use 0 for deterministic rewrites.
    /// </summary>
    public float Temperature { get; set; } = 0f;

    /// <summary>
    /// Skip cleanup for prompts shorter than this (in characters). Avoids round-trip
    /// overhead for already-tight prompts. Default: 80.
    /// </summary>
    public int MinLengthChars { get; set; } = 80;

    /// <summary>
    /// How long to leave the cleaner circuit open after a failure before retrying.
    /// Mirrors <c>OllamaTaskClassifier</c>. Default: 5 minutes.
    /// </summary>
    public int CooldownMinutes { get; set; } = 5;

    /// <summary>
    /// Optional override for the cleaner system prompt. When null, the built-in
    /// default is used (preserves code, paths, identifiers, URLs, quoted strings).
    /// </summary>
    public string? SystemPrompt { get; set; }
}
