namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Abstraction for discovering remote LLM models available via CodebrewRouter.
/// Provides caching, observability, online detection, and resilience via circuit breaker.
/// </summary>
public interface ICodebrewRouterDiscoveryService
{
    /// <summary>
    /// Discovers available models from remote CodebrewRouter endpoint.
    /// Returns cached result if available and not expired.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>RemoteDiscoveryResult with available models and online status.</returns>
    Task<RemoteDiscoveryResult> DiscoverModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent cached discovery result without making HTTP calls.
    /// </summary>
    /// <returns>Cached RemoteDiscoveryResult if available; null otherwise.</returns>
    RemoteDiscoveryResult? GetCachedDiscovery();

    /// <summary>
    /// Observes discovery result changes for all model discovery events.
    /// </summary>
    /// <returns>Observable sequence of DiscoveryChanged events.</returns>
    IObservable<DiscoveryChanged> ObserveDiscoveryChanges();
}
