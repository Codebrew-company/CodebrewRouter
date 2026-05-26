using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Catalog;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// Middleware that checks <see cref="IProviderCatalog.IsHealthy"/> before forwarding
/// a request to the inner client. Reports success/failure health observations back to
/// the catalog so the circuit-breaker state can be maintained.
///
/// Short-circuits (throws <see cref="InvalidOperationException"/>) when the deployment
/// is unhealthy. Rethrows all exceptions after reporting, except
/// <see cref="OperationCanceledException"/> which is never caught.
/// </summary>
public sealed class CircuitBreakerChatClient : DelegatingChatClient
{
    private readonly IProviderCatalog _catalog;
    private readonly string _deploymentName;

    public CircuitBreakerChatClient(
        IChatClient innerClient,
        IProviderCatalog catalog,
        string deploymentName) : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        _catalog = catalog;
        _deploymentName = deploymentName;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.IsHealthy(_deploymentName))
        {
            throw new InvalidOperationException(
                $"Deployment '{_deploymentName}' is currently unavailable (circuit breaker open).");
        }

        try
        {
            var response = await InnerClient.GetResponseAsync(chatMessages, options, cancellationToken);
            _catalog.ReportHealth(_deploymentName, true);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _catalog.ReportHealth(_deploymentName, false);
            throw;
        }
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
        if (!_catalog.IsHealthy(_deploymentName))
        {
            throw new InvalidOperationException(
                $"Deployment '{_deploymentName}' is currently unavailable (circuit breaker open).");
        }

        var enumerator = InnerClient.GetStreamingResponseAsync(chatMessages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        Exception? exception = null;
        bool hasMore;

        // Phase 1: Get the first chunk (can use try-catch here since no yield yet)
        try
        {
            hasMore = await enumerator.MoveNextAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _catalog.ReportHealth(_deploymentName, false);
            await enumerator.DisposeAsync();
            throw;
        }

        // Empty stream — report healthy and stop
        if (!hasMore)
        {
            await enumerator.DisposeAsync();
            _catalog.ReportHealth(_deploymentName, true);
            yield break;
        }

        // Phase 2: Yield first chunk
        yield return enumerator.Current;

        // Phase 3: Consume remaining chunks
        while (true)
        {
            try
            {
                hasMore = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _catalog.ReportHealth(_deploymentName, false);
                exception = ex;
                break;
            }

            if (!hasMore)
            {
                break;
            }

            yield return enumerator.Current;
        }

        await enumerator.DisposeAsync();

        // Re-throw any mid-stream exception after disposing the enumerator
        if (exception is not null)
        {
            throw exception;
        }

        // Stream completed successfully
        _catalog.ReportHealth(_deploymentName, true);
    }
}
