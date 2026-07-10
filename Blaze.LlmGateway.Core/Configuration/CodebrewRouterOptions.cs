namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for the <c>codebrewRouter</c> virtual model.
/// Binds from <c>LlmGateway:CodebrewRouter</c> in appsettings.
/// </summary>
public class CodebrewRouterOptions
{
    /// <summary>When false, the virtual model is omitted from the model catalog and DI registration is skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Model ID that appears in <c>GET /v1/models</c> and is matched by <c>ModelSelectionResolver</c>.</summary>
    public string ModelId { get; set; } = "codebrewRouter";

    /// <summary>
    /// Maps each <c>TaskType</c> name to an ordered list of provider DI keys to try in sequence.
    /// If a provider key is absent from keyed DI, it is skipped.
    /// If a provider throws, the next key in the list is tried.
    /// Falls back to <c>InnerClient</c> (LocalGemma) when all entries are exhausted.
    /// </summary>
    public Dictionary<string, string[]> FallbackRules { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reasoning"]             = ["OpenCodeGo_KimiK2_6", "OpenCodeGo_DeepSeekV4Pro", "OpenCodeGo_KimiK2_5", "OpenCodeGo_DeepSeekV4Flash"],
        ["Coding"]                = ["OpenCodeGo_DeepSeekV4Pro", "OpenCodeGo_Qwen3_6Plus", "OpenCodeGo_MiniMaxM2_7", "OpenCodeGo_MiMoV2_5Pro", "OpenCodeGo_DeepSeekV4Flash"],
        ["Research"]              = ["OpenCodeGo_KimiK2_6", "OpenCodeGo_GLM5_1", "OpenCodeGo_MiMoV2_5", "OpenCodeGo_MiniMaxM2_7", "OpenCodeGo_DeepSeekV4Flash"],
        ["VisionObjectDetection"] = ["OpenCodeGo_MiMoV2Omni", "OpenCodeGo_GLM5_1", "OpenCodeGo_MiMoV2_5", "OpenCodeGo_DeepSeekV4Flash"],
        ["Speech"]                = ["OpenCodeGo_MiMoV2Omni", "OpenCodeGo_GLM5_1", "OpenCodeGo_DeepSeekV4Flash"],
        ["Creative"]              = ["OpenCodeGo_GLM5_1", "OpenCodeGo_MiMoV2_5", "OpenCodeGo_GLM5", "OpenCodeGo_MiniMaxM2_5", "OpenCodeGo_DeepSeekV4Flash"],
        ["DataAnalysis"]          = ["OpenCodeGo_DeepSeekV4Pro", "OpenCodeGo_Qwen3_6Plus", "OpenCodeGo_MiniMaxM2_7", "OpenCodeGo_DeepSeekV4Flash"],
        ["General"]               = ["OpenCodeGo_Qwen3_5Plus", "OpenCodeGo_MiniMaxM2_5", "OpenCodeGo_Qwen3_6Plus", "OpenCodeGo_MiMoV2_5Pro", "OpenCodeGo_DeepSeekV4Flash"],
    };

    /// <summary>
    /// Controls how codebrewRouter reduces oversized multi-turn conversations before
    /// trying a downstream provider with a smaller effective input budget.
    /// </summary>
    public ContextCompactionOptions ContextCompaction { get; set; } = new();
}

public class ContextCompactionOptions
{
    /// <summary>Enables full-history compaction when a provider cannot fit the current prompt.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How aggressively to compact relative to the provider's input budget. Values below 1.0
    /// keep some headroom for provider-specific tokenization differences.
    /// </summary>
    public double TargetBudgetRatio { get; set; } = 0.85d;

    /// <summary>Do not compact tiny conversations; the overhead is not worth it.</summary>
    public int MinMessagesToCompact { get; set; } = 6;

    /// <summary>Always keep this many most-recent non-system messages verbatim.</summary>
    public int PreserveMostRecentMessages { get; set; } = 4;

    /// <summary>Caps the size of the generated history summary.</summary>
    public int SummaryMaxOutputTokens { get; set; } = 256;

    /// <summary>Keep summarization deterministic.</summary>
    public float SummaryTemperature { get; set; } = 0f;

    /// <summary>Optional override for the compactor system prompt.</summary>
    public string? SystemPrompt { get; set; }
}
