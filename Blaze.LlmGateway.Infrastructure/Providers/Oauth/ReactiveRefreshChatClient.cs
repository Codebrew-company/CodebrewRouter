using System.ClientModel;
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.Providers.Oauth;

/// <summary>
/// Wraps a subscription OAuth provider's chat client with reactive token recovery:
/// on a 401/403 it refreshes the access token once (which rotates the shared
/// credential in place) and retries the request a single time (9router's pattern).
/// Streaming refresh only applies before the first chunk is yielded.
/// </summary>
public sealed class ReactiveRefreshChatClient(
    IChatClient innerClient,
    SubscriptionTokenRegistry registry,
    ISubscriptionOAuthClient oauthClient,
    SubscriptionProviderOptions provider,
    ILogger logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InnerClient.GetResponseAsync(chatMessages, options, cancellationToken);
        }
        catch (ClientResultException ex) when (IsAuthFailure(ex))
        {
            if (!await TryRefreshAsync(cancellationToken))
            {
                throw;
            }

            logger.LogInformation("Retrying '{Provider}' after reactive OAuth refresh", provider.Name);
            return await InnerClient.GetResponseAsync(chatMessages, options, cancellationToken);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamWithRecoveryAsync(chatMessages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithRecoveryAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (enumerator, hasFirst) = await StartStreamAsync(chatMessages, options, cancellationToken);
        if (!hasFirst || enumerator is null)
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }

            yield break;
        }

        try
        {
            yield return enumerator.Current;
            while (await enumerator.MoveNextAsync())
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>
    /// Opens the stream and pulls the first chunk; on a 401/403 before any chunk,
    /// refreshes the token once and reopens. Returns the live enumerator positioned on
    /// its first element, or (null, false) when the stream is empty.
    /// </summary>
    private async Task<(IAsyncEnumerator<ChatResponseUpdate>? Enumerator, bool HasFirst)> StartStreamAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            var enumerator = InnerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            if (await enumerator.MoveNextAsync())
            {
                return (enumerator, true);
            }

            await enumerator.DisposeAsync();
            return (null, false);
        }
        catch (ClientResultException ex) when (IsAuthFailure(ex))
        {
            if (!await TryRefreshAsync(cancellationToken))
            {
                throw;
            }

            logger.LogInformation("Retrying '{Provider}' stream after reactive OAuth refresh", provider.Name);
        }

        var retry = InnerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (await retry.MoveNextAsync())
        {
            return (retry, true);
        }

        await retry.DisposeAsync();
        return (null, false);
    }

    private static bool IsAuthFailure(ClientResultException ex) => ex.Status is 401 or 403;

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var current = registry.GetRecord(provider.Name);
        if (current is null)
        {
            return false;
        }

        var updated = await oauthClient.RefreshAsync(provider, current, cancellationToken);
        if (updated is null)
        {
            logger.LogWarning("Reactive OAuth refresh for '{Provider}' failed", provider.Name);
            return false;
        }

        registry.SetToken(updated);
        return true;
    }
}
