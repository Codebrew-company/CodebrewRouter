using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.TokenCounting;

/// <summary>
/// Provides token counting for chat messages.
/// </summary>
public interface ITokenCounter
{
    /// <summary>
    /// Counts the estimated number of tokens for the given chat messages.
    /// </summary>
    /// <param name="messages">The messages to count.</param>
    /// <param name="modelId">Optional model ID to use for tokenization. If null, a default (like gpt-4o) is used.</param>
    int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null);
}
