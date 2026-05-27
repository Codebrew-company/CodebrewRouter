using System.Diagnostics;
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// Middleware that wraps a deployment's <see cref="IChatClient"/> to provide:
/// - Circuit-breaker check (delegates to <see cref="IProviderCatalog.IsHealthy"/>)
/// - Health observation reporting on success/failure
/// - Latency measurements fed to <see cref="LatencyStrategy"/>
/// - In-flight tracking for <see cref="LeastBusyStrategy"/>
///
/// This is the production replacement for the local pipeline — wraps around the
/// provider's keyed chat client after a catalog deployment is selected.
/// </summary>
public sealed class CatalogMetricsChatClient : DelegatingChatClient
{
    private readonly IProviderCatalog _catalog;
    private readonly string _deploymentName;
    private readonly LatencyStrategy? _latencyStrategy;
    private readonly LeastBusyStrategy? _leastBusyStrategy;

    public CatalogMetricsChatClient(
        IChatClient innerClient,
        IProviderCatalog catalog,
        string deploymentName,
        IRoutingStrategyResolver? strategyResolver = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        _catalog = catalog;
        _deploymentName = deploymentName;

        // Lazily resolve the strategies that need runtime feedback.
        if (strategyResolver is not null)
        {
            _latencyStrategy = ResolveStrategy<LatencyStrategy>(strategyResolver, "latency");
            _leastBusyStrategy = ResolveStrategy<LeastBusyStrategy>(strategyResolver, "least_busy");
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnhealthy();

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await InnerClient.GetResponseAsync(chatMessages, options, cancellationToken);
            RecordSuccess(sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure();
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
        ThrowIfUnhealthy();

        var sw = Stopwatch.StartNew();

        // await using compiles to try/finally — ensures the inner enumerator is
        // always disposed even when the consumer breaks early or cancellation fires.
        await using var enumerator = InnerClient
            .GetStreamingResponseAsync(chatMessages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        // Phase 1: Get the first chunk
        bool hasMore;
        try
        {
            hasMore = await enumerator.MoveNextAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure();
            throw;
        }

        // Empty stream — success
        if (!hasMore)
        {
            RecordSuccess(sw.ElapsedMilliseconds);
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
            catch (OperationCanceledException)
            {
                // Cancellation is not a deployment failure — just release in-flight count
                _leastBusyStrategy?.Release(_deploymentName);
                throw;
            }
            catch (Exception ex)
            {
                RecordFailure();
                throw;
            }

            if (!hasMore)
                break;

            yield return enumerator.Current;
        }

        // Stream completed successfully
        RecordSuccess(sw.ElapsedMilliseconds);
    }

    private void ThrowIfUnhealthy()
    {
        if (!_catalog.IsHealthy(_deploymentName))
        {
            throw new InvalidOperationException(
                $"Deployment '{_deploymentName}' is currently unavailable (circuit breaker open).");
        }
    }

    private void RecordSuccess(long elapsedMs)
    {
        _catalog.ReportHealth(_deploymentName, true);
        _latencyStrategy?.ReportLatency(_deploymentName, elapsedMs);
        _leastBusyStrategy?.Release(_deploymentName);
    }

    private void RecordFailure()
    {
        _catalog.ReportHealth(_deploymentName, false);
        _leastBusyStrategy?.Release(_deploymentName);
    }

    private static T? ResolveStrategy<T>(IRoutingStrategyResolver resolver, string name) where T : class
        => resolver.Resolve(name) as T;
}
