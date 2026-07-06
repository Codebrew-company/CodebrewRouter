using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.Routing;

namespace Blaze.LlmGateway.Infrastructure;

/// <summary>
/// Wraps an Ollama client with automatic failover between primary and fallback endpoints.
/// Receives cached clients via constructor to prevent resource leaks.
/// On first request failure, queries health state, tries alternate endpoint, and updates health state.
/// </summary>
public sealed class OllamaFailoverClient : DelegatingChatClient
{
    private readonly IOllamaHealthState _healthState;
    private readonly IChatClient _fallbackClient;
    private readonly ILogger<OllamaFailoverClient> _logger;
    private string _currentEndpoint;
    private readonly string _primaryEndpoint;
    private readonly string _fallbackEndpoint;

    public OllamaFailoverClient(
        IChatClient primaryClient,
        IChatClient fallbackClient,
        IOllamaHealthState healthState,
        string primaryEndpoint,
        string fallbackEndpoint,
        ILogger<OllamaFailoverClient> logger)
        : base(primaryClient)
    {
        _fallbackClient = fallbackClient;
        _healthState = healthState;
        _logger = logger;
        _currentEndpoint = primaryEndpoint;
        _primaryEndpoint = primaryEndpoint;
        _fallbackEndpoint = fallbackEndpoint;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await InnerClient.GetResponseAsync(messages, options, cancellationToken);
            _healthState.MarkEndpointHealthy(_currentEndpoint);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ Ollama request failed on {Endpoint}; trying failover", _currentEndpoint);
            return await FailoverAndRetryAsync(messages, options, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerable<ChatResponseUpdate>? stream = null;
        
        try
        {
            stream = InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ Ollama streaming failed on {Endpoint}; trying failover", _currentEndpoint);
            stream = null;
        }

        if (stream != null)
        {
            await foreach (var update in StreamWithHealthCheckAsync(stream, cancellationToken))
            {
                yield return update;
            }
        }
        else
        {
            await foreach (var update in FailoverAndRetryStreamAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithHealthCheckAsync(
        IAsyncEnumerable<ChatResponseUpdate> stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = stream.GetAsyncEnumerator(cancellationToken);

        // Try to get first chunk to probe connection
        if (!await enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException("No response from Ollama");
        }

        _healthState.MarkEndpointHealthy(_currentEndpoint);
        yield return enumerator.Current;

        while (await enumerator.MoveNextAsync())
        {
            yield return enumerator.Current;
        }
    }

    private async Task<ChatResponse> FailoverAndRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        _healthState.MarkEndpointUnhealthy(_currentEndpoint, new Exception("Request failed"));
        var (newEndpoint, _) = _healthState.GetHealthyEndpoint();

        if (newEndpoint == _currentEndpoint)
        {
            throw new InvalidOperationException("No healthy Ollama endpoint available for failover");
        }

        _logger.LogInformation("🔄 Failover: switching from {Old} to {New}", _currentEndpoint, newEndpoint);
        _currentEndpoint = newEndpoint;

        try
        {
            var response = await _fallbackClient.GetResponseAsync(messages, options, cancellationToken);
            _healthState.MarkEndpointHealthy(_currentEndpoint);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failover also failed on {Endpoint}", _currentEndpoint);
            _healthState.MarkEndpointUnhealthy(_currentEndpoint, ex);
            throw;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> FailoverAndRetryStreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _healthState.MarkEndpointUnhealthy(_currentEndpoint, new Exception("Streaming request failed"));
        var (newEndpoint, _) = _healthState.GetHealthyEndpoint();

        if (newEndpoint == _currentEndpoint)
        {
            throw new InvalidOperationException("No healthy Ollama endpoint available for failover");
        }

        _logger.LogInformation("🔄 Failover: switching from {Old} to {New}", _currentEndpoint, newEndpoint);
        _currentEndpoint = newEndpoint;

        await foreach (var update in _fallbackClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        _healthState.MarkEndpointHealthy(_currentEndpoint);
    }
}
