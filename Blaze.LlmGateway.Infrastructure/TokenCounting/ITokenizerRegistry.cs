using Microsoft.ML.Tokenizers;

namespace Blaze.LlmGateway.Infrastructure.TokenCounting;

/// <summary>
/// Provides tokenizers for LLM models, supporting native tokenizers or graceful fallback.
/// </summary>
public interface ITokenizerRegistry
{
    /// <summary>
    /// Returns the tokenizer for a given model ID.
    /// Returns null if the model's native tokenizer is unavailable (graceful degradation);
    /// the caller should use a fallback encoding (e.g., cl100k_base from gpt-4o).
    /// Never throws exceptions.
    /// </summary>
    /// <param name="modelId">The model ID (e.g., "deepseek-v4-pro", "qwen3.5-plus", "kimi-k2.5").</param>
    /// <returns>
    /// A Tokenizer instance if available, or null if the model should fall back to default encoding.
    /// </returns>
    Tokenizer? GetTokenizer(string modelId);

    /// <summary>
    /// Returns human-readable accuracy metadata for a model.
    /// Used for logging when fallback encoding is used.
    /// </summary>
    /// <param name="modelId">The model ID.</param>
    /// <returns>
    /// Accuracy description, e.g., "native tokenizer", "HuggingFace JSON", or "gpt-4o fallback (~80% accuracy)".
    /// </returns>
    string GetAccuracyMetadata(string modelId);
}
