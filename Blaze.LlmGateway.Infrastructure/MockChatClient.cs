using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure;

public sealed class MockChatClient(ILogger<MockChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("🎭 MockChatClient responding...");
        var response = "Hello! I''m running in mock mode since the backend is unavailable. This is a test response.";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogInformation("🎭 MockChatClient streaming...");
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant };
        
        var response = "Mock response streaming";
        foreach (var word in response.Split(" "))
        {
            await Task.Delay(10, cancellationToken);
            yield return new ChatResponseUpdate { };
        }
        
        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
