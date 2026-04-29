using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

public sealed class NoopContextCompactor : IContextCompactor
{
    public Task<ContextCompactionResult> CompactAsync(
        IList<ChatMessage> messages,
        int targetTokenCount,
        string? modelId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ContextCompactionResult(messages, 0, 0, false, "disabled"));
}
