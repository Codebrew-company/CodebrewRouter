using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

public static class ContextSizingChatClientBuilderExtensions
{
    /// <summary>
    /// Inserts <see cref="ContextSizingChatClient"/> into the chat-client pipeline.
    /// Call this inside a <c>AddKeyedSingleton&lt;IChatClient&gt;</c> factory after
    /// <c>.AsBuilder()</c>, before <c>.Build()</c>.
    /// </summary>
    public static ChatClientBuilder UseContextSizing(
        this ChatClientBuilder builder,
        ITokenCounter tokenCounter,
        IContextCompactor compactor,
        IOptions<ContextSizingOptions> sizingOptions,
        int contextWindowTokens,
        int reservedOutputTokens,
        string modelId,
        ILogger<ContextSizingChatClient> logger)
        => builder.Use(inner => new ContextSizingChatClient(
               inner,
               tokenCounter,
               compactor,
               sizingOptions,
               contextWindowTokens,
               reservedOutputTokens,
               modelId,
               logger));
}
