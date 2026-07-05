namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// One entry in <see cref="LocalInferenceOptions.ModelTiers"/>: a local model variant
/// eligible when the host's total memory meets <see cref="MinTotalMemoryGb"/>.
/// </summary>
public sealed class LocalModelTierOptions
{
    /// <summary>Display name used in startup logs (e.g. "gemma-4-12b").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Minimum total host memory (GB) required for this tier to be eligible.
    /// 0 marks the floor tier that is always eligible.
    /// </summary>
    public int MinTotalMemoryGb { get; set; }

    /// <summary>
    /// Local file path or remote URL to the model file.
    /// Same semantics as <see cref="LocalInferenceOptions.ModelPath"/>.
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;
}
