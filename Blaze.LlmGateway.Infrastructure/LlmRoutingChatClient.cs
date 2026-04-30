using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure;

public class LlmRoutingChatClient : DelegatingChatClient
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRoutingStrategy _routingStrategy;
    private readonly IFailoverStrategy _failoverStrategy;
    private readonly IModelAvailabilityRegistry _availabilityRegistry;
    private readonly IOptions<LlmGatewayOptions> _gatewayOptions;
    private readonly IOptions<ContextSizingOptions> _sizingOptions;
    private readonly ILogger<LlmRoutingChatClient> _logger;

    public LlmRoutingChatClient(
        IChatClient innerClient,
        IServiceProvider serviceProvider,
        IRoutingStrategy routingStrategy,
        IFailoverStrategy failoverStrategy,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IOptions<ContextSizingOptions> sizingOptions,
        ILogger<LlmRoutingChatClient> logger) : base(innerClient)
    {
        _serviceProvider = serviceProvider;
        _routingStrategy = routingStrategy;
        _failoverStrategy = failoverStrategy;
        _availabilityRegistry = availabilityRegistry;
        _gatewayOptions = gatewayOptions;
        _sizingOptions = sizingOptions;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("🔀 ResolveTargetClient: Getting routing decision...");
        var targetClient = await ResolveTargetClientAsync(chatMessages, cancellationToken);
        _logger.LogInformation("🎯 Routing request to: {TargetClient}", targetClient.GetType().Name);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await targetClient.GetResponseAsync(chatMessages, options, cancellationToken);
            sw.Stop();
            _logger.LogInformation("✅ Provider responded in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return response;
        }
        catch (ContextOverflowException coe)
        {
            _logger.LogWarning(
                "⚠️ Context overflow on primary provider ({ModelId}): {Required} tokens > budget {Budget}; attempting size-aware failover",
                coe.ModelId, coe.RequiredTokens, coe.Budget);
            return await TryFailoverAsync(chatMessages, options, cancellationToken, coe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Provider failed; attempting failover...");
            return await TryFailoverAsync(chatMessages, options, cancellationToken);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingResponseAsyncImpl(chatMessages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsyncImpl(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogDebug("🔀 ResolveTargetClient: Getting routing decision...");
        var targetClient = await ResolveTargetClientAsync(chatMessages, cancellationToken);
        _logger.LogInformation("🎯 Routing streaming request to: {TargetClient}", targetClient.GetType().Name);

        var result = await TryGetFirstChunkAsync(targetClient, chatMessages, options, cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("⚠️ Primary provider failed before first chunk — attempting failover chain...");

            var overflow = result.ThrownException as ContextOverflowException;
            await foreach (var update in TryFailoverStreamingAsync(chatMessages, options, cancellationToken, overflow))
                yield return update;

            yield break;
        }

        yield return result.FirstChunk;
        _logger.LogDebug("  ├─ First chunk received");

        var enumerator = result.Enumerator!;
        var chunkCount = 1;
        while (true)
        {
            bool hasMore = false;
            bool streamFailed = false;
            try
            {
                hasMore = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Mid-stream failure from primary provider — ending stream");
                streamFailed = true;
            }

            if (streamFailed || !hasMore)
            {
                await enumerator.DisposeAsync();
                _logger.LogInformation("✅ Streaming complete - {ChunkCount} updates", chunkCount);
                yield break;
            }

            chunkCount++;
            yield return enumerator.Current;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Failover (non-streaming)
    // ──────────────────────────────────────────────────────────────

    private async Task<ChatResponse> TryFailoverAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken,
        ContextOverflowException? overflow = null)
    {
        var destination = await _routingStrategy.ResolveAsync(chatMessages, cancellationToken);
        var fallbackChain = _failoverStrategy.GetFailoverChainAsync(destination);
        ContextOverflowException? lastOverflow = overflow;

        foreach (var fallback in fallbackChain)
        {
            if (!_availabilityRegistry.IsProviderAvailable(fallback.ToString()))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} is marked unavailable; skipping", fallback);
                continue;
            }

            // Skip providers whose context window is too small for the known required token count.
            if (lastOverflow is not null && !CanFit(fallback.ToString(), lastOverflow.RequiredTokens, options))
            {
                _logger.LogDebug(
                    "  ├─ Failover provider {Fallback} window too small ({Required} tokens > budget); skipping",
                    fallback, lastOverflow.RequiredTokens);
                lastOverflow = lastOverflow.WithAttempted(fallback.ToString());
                continue;
            }

            _logger.LogInformation("🔄 Trying failover provider: {Fallback}", fallback);
            var fallbackClient = _serviceProvider.GetKeyedService<IChatClient>(fallback.ToString());
            if (fallbackClient is null)
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} not registered; skipping", fallback);
                continue;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await fallbackClient.GetResponseAsync(chatMessages, options, cancellationToken);
                sw.Stop();
                _logger.LogInformation("✅ Failover succeeded with {Fallback} in {ElapsedMs}ms", fallback, sw.ElapsedMilliseconds);
                return response;
            }
            catch (ContextOverflowException coe)
            {
                _logger.LogWarning("  ├─ Failover provider {Fallback} context overflow ({Required} > {Budget}); trying next",
                    fallback, coe.RequiredTokens, coe.Budget);
                lastOverflow = (lastOverflow ?? coe).WithAttempted(fallback.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "  ├─ Failover provider {Fallback} also failed", fallback);
            }
        }

        _logger.LogError("❌ All failover providers exhausted");

        if (lastOverflow is not null)
            throw lastOverflow;

        throw new InvalidOperationException("All providers in failover chain failed");
    }

    // ──────────────────────────────────────────────────────────────
    // Failover (streaming)
    // ──────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ChatResponseUpdate> TryFailoverStreamingAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ContextOverflowException? overflow = null)
    {
        var destination = await _routingStrategy.ResolveAsync(chatMessages, cancellationToken);
        var fallbackChain = _failoverStrategy.GetFailoverChainAsync(destination);
        ContextOverflowException? lastOverflow = overflow;

        foreach (var fallback in fallbackChain)
        {
            if (!_availabilityRegistry.IsProviderAvailable(fallback.ToString()))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} is marked unavailable; skipping", fallback);
                continue;
            }

            if (lastOverflow is not null && !CanFit(fallback.ToString(), lastOverflow.RequiredTokens, options))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} window too small; skipping", fallback);
                lastOverflow = lastOverflow.WithAttempted(fallback.ToString());
                continue;
            }

            _logger.LogInformation("🔄 Trying failover provider (streaming): {Fallback}", fallback);
            var fallbackClient = _serviceProvider.GetKeyedService<IChatClient>(fallback.ToString());
            if (fallbackClient is null)
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} not registered; skipping", fallback);
                continue;
            }

            var probe = await TryGetFirstChunkAsync(fallbackClient, chatMessages, options, cancellationToken);
            if (!probe.Success)
            {
                if (probe.ThrownException is ContextOverflowException coe)
                {
                    _logger.LogWarning("  ├─ Failover provider {Fallback} context overflow; trying next", fallback);
                    lastOverflow = (lastOverflow ?? coe).WithAttempted(fallback.ToString());
                }
                else
                {
                    _logger.LogWarning("  ├─ Failover provider {Fallback} failed before first chunk; trying next", fallback);
                }
                continue;
            }

            _logger.LogInformation("✅ Failover streaming: first chunk received from {Fallback}", fallback);
            yield return probe.FirstChunk;

            var enumerator = probe.Enumerator!;
            int chunkCount = 1;
            while (true)
            {
                bool hasMore = false;
                bool midStreamFailed = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "  ├─ Failover provider {Fallback} failed mid-stream after {ChunkCount} chunks; ending", fallback, chunkCount);
                    midStreamFailed = true;
                }

                if (midStreamFailed || !hasMore)
                {
                    await enumerator.DisposeAsync();
                    _logger.LogInformation("✅ Failover streaming complete with {Fallback} ({ChunkCount} chunks)", fallback, chunkCount);
                    yield break;
                }

                chunkCount++;
                yield return enumerator.Current;
            }
        }

        _logger.LogError("❌ All failover providers exhausted (streaming); throwing error");

        if (lastOverflow is not null)
            throw lastOverflow;

        throw new InvalidOperationException("All providers in failover chain failed during streaming");
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private async Task<IChatClient> ResolveTargetClientAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("🔀 Routing strategy resolving...");
        var destination = await _routingStrategy.ResolveAsync(messages, cancellationToken);
        _logger.LogDebug("  ├─ Routing strategy decided: {Destination}", destination);

        if (!_availabilityRegistry.IsProviderAvailable(destination.ToString()))
        {
            _logger.LogWarning("❌ Destination '{Destination}' is currently unavailable. Deferring to failover chain.", destination);
            return new UnavailableChatClient($"Provider '{destination}' is currently unavailable.");
        }

        var client = _serviceProvider.GetKeyedService<IChatClient>(destination.ToString());
        if (client is null)
        {
            _logger.LogWarning("❌ No client registered for destination '{Destination}'. Using fallback to InnerClient.", destination);
            return InnerClient;
        }

        _logger.LogDebug("  └─ Found registered client for {Destination}", destination);
        return client;
    }

    /// <summary>
    /// Returns true if <paramref name="destination"/> has a context window large enough
    /// to accommodate <paramref name="requiredTokens"/>. Unknown destinations are optimistically accepted.
    /// </summary>
    private bool CanFit(string destination, int requiredTokens, ChatOptions? options)
    {
        var providers = _gatewayOptions.Value.Providers;
        var (maxContext, reservedOutput) = destination switch
        {
            "AzureFoundry" => (providers.AzureFoundry.MaxContextTokens, providers.AzureFoundry.ReservedOutputTokens),
            "FoundryLocal"  => (providers.FoundryLocal.MaxContextTokens,  providers.FoundryLocal.ReservedOutputTokens),
            "OllamaLocal"   => (providers.OllamaLocal.MaxContextTokens,   providers.OllamaLocal.ReservedOutputTokens),
            "GithubModels"  => (providers.GithubModels.MaxContextTokens,  providers.GithubModels.ReservedOutputTokens),
            "LmStudio"      => (providers.LmStudio.MaxContextTokens,      providers.LmStudio.ReservedOutputTokens),
            _ => (int.MaxValue, 0)   // unknown → optimistic
        };
        var reserved = options?.MaxOutputTokens ?? reservedOutput;
        return requiredTokens <= (maxContext - reserved);
    }

    /// <summary>
    /// Probes for the first streaming chunk without committing.
    /// Catches <see cref="ContextOverflowException"/> into <see cref="FirstChunkResult.ThrownException"/>
    /// so the caller can distinguish overflow from ordinary connection failures.
    /// </summary>
    private static async Task<FirstChunkResult> TryGetFirstChunkAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var enumerator = client.GetStreamingResponseAsync(messages, options, ct)
                               .GetAsyncEnumerator(ct);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                await enumerator.DisposeAsync();
                return FirstChunkResult.Failed;
            }
            return new FirstChunkResult(true, enumerator.Current, enumerator);
        }
        catch (ContextOverflowException ex)
        {
            await enumerator.DisposeAsync();
            return new FirstChunkResult(false, ThrownException: ex);
        }
        catch
        {
            await enumerator.DisposeAsync();
            return FirstChunkResult.Failed;
        }
    }
}

/// <summary>Encapsulates the result of probing for the first streaming chunk.</summary>
internal record FirstChunkResult(
    bool Success,
    ChatResponseUpdate FirstChunk = default!,
    IAsyncEnumerator<ChatResponseUpdate>? Enumerator = null,
    Exception? ThrownException = null)
{
    public static readonly FirstChunkResult Failed = new(false);
}
