using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.LocalInference;

internal interface ILocalGemmaRuntime : IAsyncDisposable
{
    IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
