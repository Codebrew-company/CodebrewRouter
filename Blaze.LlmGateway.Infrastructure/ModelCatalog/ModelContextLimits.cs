namespace Blaze.LlmGateway.Infrastructure.ModelCatalog;

/// <summary>
/// Curated context-window limits for well-known models.
/// Used as a fallback when a provider's config does not specify limits
/// (e.g. dynamically resolved Ollama models).
/// Most-specific entries must come before less-specific ones.
/// </summary>
public static class ModelContextLimits
{
    // (predicate, contextWindow, maxOutput)
    private static readonly (Func<string, bool> Match, int ContextWindow, int MaxOutput)[] Table =
    [
        (id => id.StartsWith("gpt-4o-mini",    StringComparison.OrdinalIgnoreCase), 128_000,   16_384),
        (id => id.StartsWith("gpt-4o",         StringComparison.OrdinalIgnoreCase), 128_000,   16_384),
        (id => id.Contains("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase), 1_048_576, 8_192),
        (id => id.StartsWith("llama3.1:",      StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
        (id => id.StartsWith("llama3.2:",      StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
        (id => id.StartsWith("phi-4",          StringComparison.OrdinalIgnoreCase), 16_384,    4_096),
        (id => id.StartsWith("qwen2.5:",       StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
    ];

    /// <summary>
    /// Returns <c>(ContextWindow, MaxOutput)</c> for <paramref name="modelId"/>,
    /// or <c>(null, null)</c> if the model is not in the curated table.
    /// </summary>
    public static (int? ContextWindow, int? MaxOutput) Lookup(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return (null, null);

        foreach (var (match, ctx, maxOut) in Table)
        {
            if (match(modelId))
                return (ctx, maxOut);
        }

        return (null, null);
    }
}
