using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace Blaze.LlmGateway.Infrastructure.TokenCounting;

/// <summary>
/// A token counter using Microsoft.ML.Tokenizers and cl100k_base (gpt-4o) encoding by default.
/// Supports dynamic tokenization by model ID via ITokenizerRegistry.
/// Gracefully falls back to default encoding if a model's native tokenizer is unavailable.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly ConcurrentDictionary<string, Tokenizer> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultModelId;
    private readonly ITokenizerRegistry? _registry;
    private readonly ILogger<TiktokenTokenCounter> _logger;

    public TiktokenTokenCounter(
        string defaultModelId = "gpt-4o",
        ITokenizerRegistry? registry = null,
        ILogger<TiktokenTokenCounter>? logger = null)
    {
        _defaultModelId = defaultModelId ?? throw new ArgumentNullException(nameof(defaultModelId));
        _registry = registry;
        _logger = logger ?? new NoOpLogger();
    }

    public int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null)
    {
        var targetModel = string.IsNullOrWhiteSpace(modelId) ? _defaultModelId : modelId;
        
        var tokenizer = _tokenizers.GetOrAdd(targetModel, model => ResolveTokenizer(model));

        int count = 0;
        foreach (var message in messages)
        {
            // Add a small overhead per message (e.g., for role tokens and structural tokens)
            count += 4; 
            
            if (!string.IsNullOrEmpty(message.Text))
            {
                count += tokenizer.CountTokens(message.Text);
            }
            
            // Note: If images or other media types are present, they are not counted here.
        }
        
        // Add overhead for the assistant reply prime
        count += 3;
        
        return count;
    }

    /// <summary>
    /// Resolves the tokenizer for a model, with graceful fallback.
    /// Strategy:
    /// 1. If registry is available, try to get native/HF tokenizer
    /// 2. If registry returns null or unavailable, use default encoding (cl100k_base)
    /// 3. Always log warnings when falling back
    /// </summary>
    private Tokenizer ResolveTokenizer(string modelId)
    {
        try
        {
            // If registry is available, try to get model-specific tokenizer
            if (_registry != null)
            {
                var registryTokenizer = _registry.GetTokenizer(modelId);
                if (registryTokenizer != null)
                {
                    _logger.LogDebug("Using native tokenizer for model '{ModelId}'.", modelId);
                    return registryTokenizer;
                }

                // Tokenizer not available; log warning and use fallback
                var metadata = _registry.GetAccuracyMetadata(modelId);
                _logger.LogWarning(
                    "Tokenizer not available for model '{ModelId}'. Accuracy: {Accuracy}. Using fallback encoding (cl100k_base).",
                    modelId, metadata);
            }
            else
            {
                // No registry; try Tiktoken directly
                try
                {
                    return TiktokenTokenizer.CreateForModel(modelId);
                }
                catch
                {
                    // Tiktoken doesn't recognize the model; use default
                    _logger.LogWarning(
                        "Tiktoken does not recognize model '{ModelId}'. Using default encoding (cl100k_base).",
                        modelId);
                }
            }

            // Fallback to default encoding (gpt-4o)
            return TiktokenTokenizer.CreateForModel(_defaultModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error resolving tokenizer for model '{ModelId}'. Using default encoding.", modelId);
            // Even if something goes wrong, try to return a working tokenizer
            try
            {
                return TiktokenTokenizer.CreateForModel(_defaultModelId);
            }
            catch
            {
                // Last resort: throw (this is truly exceptional)
                throw new InvalidOperationException(
                    $"Failed to resolve any tokenizer for model '{modelId}' and could not fall back to default '{_defaultModelId}'.",
                    ex);
            }
        }
    }

    /// <summary>
    /// No-op logger for when no logger is provided.
    /// </summary>
    private sealed class NoOpLogger : ILogger<TiktokenTokenCounter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
