using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Blaze.LlmGateway.Infrastructure.TokenCounting;

/// <summary>
/// A token counter using Microsoft.ML.Tokenizers and cl100k_base (gpt-4o) encoding by default.
/// Supports dynamic tokenization by model ID.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly ConcurrentDictionary<string, Tokenizer> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultModelId;

    public TiktokenTokenCounter(string defaultModelId = "gpt-4o")
    {
        _defaultModelId = defaultModelId ?? throw new ArgumentNullException(nameof(defaultModelId));
    }

    public int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null)
    {
        var targetModel = string.IsNullOrWhiteSpace(modelId) ? _defaultModelId : modelId;
        
        var tokenizer = _tokenizers.GetOrAdd(targetModel, model => 
        {
            try
            {
                return TiktokenTokenizer.CreateForModel(model);
            }
            catch
            {
                // Fallback to the default model if Tiktoken doesn't recognize the model ID
                return TiktokenTokenizer.CreateForModel(_defaultModelId);
            }
        });

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
}
