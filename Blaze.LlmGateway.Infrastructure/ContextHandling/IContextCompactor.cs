using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

public interface IContextCompactor
{
    Task<ContextCompactionResult> CompactAsync(
        IList<ChatMessage> messages,
        int targetTokenCount,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}

public sealed record ContextCompactionResult(
    IList<ChatMessage> Messages,
    int OriginalTokenCount,
    int CompactedTokenCount,
    bool WasCompacted,
    string Strategy);
