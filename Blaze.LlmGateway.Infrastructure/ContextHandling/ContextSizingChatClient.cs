using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

/// <summary>
/// Middleware that enforces a context window on every request:
/// counts tokens, compacts when over budget, throws
/// <see cref="ContextOverflowException"/> when even compaction is insufficient.
/// </summary>
public sealed class ContextSizingChatClient : DelegatingChatClient
{
    private readonly ITokenCounter _tokenCounter;
    private readonly IContextCompactor _compactor;
    private readonly IOptions<ContextSizingOptions> _options;
    private readonly int _contextWindowTokens;
    private readonly int _reservedOutputTokens;
    private readonly string _modelId;
    private readonly ILogger<ContextSizingChatClient> _logger;

    public ContextSizingChatClient(
        IChatClient innerClient,
        ITokenCounter tokenCounter,
        IContextCompactor compactor,
        IOptions<ContextSizingOptions> options,
        int contextWindowTokens,
        int reservedOutputTokens,
        string modelId,
        ILogger<ContextSizingChatClient> logger) : base(innerClient)
    {
        _tokenCounter = tokenCounter;
        _compactor = compactor;
        _options = options;
        _contextWindowTokens = contextWindowTokens;
        _reservedOutputTokens = reservedOutputTokens;
        _modelId = modelId;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fittedMessages = await EnsureFitsAsync(chatMessages, options, cancellationToken);
        return await InnerClient.GetResponseAsync(fittedMessages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamingImpl(chatMessages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamingImpl(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fittedMessages = await EnsureFitsAsync(chatMessages, options, cancellationToken);

        await foreach (var update in InnerClient.GetStreamingResponseAsync(fittedMessages, options, cancellationToken))
            yield return update;
    }

    private async Task<IList<ChatMessage>> EnsureFitsAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var messages = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();

        if (!_options.Value.Enabled)
            return messages;

        var reserved = options?.MaxOutputTokens ?? _reservedOutputTokens;
        var budget = _contextWindowTokens - reserved;

        if (budget <= 0)
        {
            _logger.LogWarning(
                "Context window {Window} - reserved {Reserved} = non-positive budget for model {ModelId}; throwing overflow",
                _contextWindowTokens, reserved, _modelId);
            throw new ContextOverflowException(_modelId, int.MaxValue, budget, []);
        }

        var tokenCount = _tokenCounter.CountTokens(messages, _modelId);
        if (tokenCount <= budget)
        {
            _logger.LogDebug(
                "Prompt fits: {Tokens}/{Budget} tokens for model {ModelId}",
                tokenCount, budget, _modelId);
            return messages;
        }

        _logger.LogInformation(
            "Prompt ({Tokens} tokens) exceeds budget ({Budget}) for model {ModelId}; compacting",
            tokenCount, budget, _modelId);

        var compactionResult = await _compactor.CompactAsync(messages, budget, _modelId, cancellationToken);

        if (compactionResult.CompactedTokenCount <= budget)
        {
            _logger.LogInformation(
                "Compaction succeeded for model {ModelId}: {Original}→{Compacted} tokens",
                _modelId, compactionResult.OriginalTokenCount, compactionResult.CompactedTokenCount);
            return compactionResult.Messages;
        }

        _logger.LogWarning(
            "Compaction insufficient for model {ModelId}: {Compacted} tokens still > budget {Budget}",
            _modelId, compactionResult.CompactedTokenCount, budget);

        throw new ContextOverflowException(
            _modelId,
            compactionResult.CompactedTokenCount,
            budget,
            []);
    }
}
