using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.Quota;

/// <summary>
/// P3.2: multi-credential pool for one provider (9router auth.js pattern). Each inner
/// client is the same provider with a different API key. Selection is fill-first
/// (priority order) or sticky round-robin (rotate after N consecutive uses). A key that
/// fails with a rate-limit signal gets an individual exponential cooldown so one
/// benched credential never benches the others.
/// </summary>
public sealed class CredentialPoolChatClient : IChatClient
{
    private const int ConsecutiveUsesBeforeRotate = 5;

    private readonly IReadOnlyList<IChatClient> _clients;
    private readonly bool _roundRobin;
    private readonly string _providerKey;
    private readonly IModelLockRegistry _locks;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private int _cursor;
    private int _consecutiveUses;

    public CredentialPoolChatClient(
        IReadOnlyList<IChatClient> clients,
        string credentialStrategy,
        string providerKey,
        ILogger<CredentialPoolChatClient> logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(clients.Count);
        _clients = clients;
        _roundRobin = string.Equals(credentialStrategy, "round-robin", StringComparison.OrdinalIgnoreCase);
        _providerKey = providerKey;
        _locks = new ModelLockRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelLockRegistry>.Instance);
        _logger = logger;
    }

    /// <summary>Ordered credential indexes to try for this request: unlocked first, in strategy order.</summary>
    private List<int> SelectionOrder()
    {
        List<int> order;
        lock (_gate)
        {
            if (_roundRobin)
            {
                _consecutiveUses++;
                if (_consecutiveUses > ConsecutiveUsesBeforeRotate)
                {
                    _cursor = (_cursor + 1) % _clients.Count;
                    _consecutiveUses = 1;
                }

                order = [.. Enumerable.Range(0, _clients.Count).Select(i => (_cursor + i) % _clients.Count)];
            }
            else
            {
                order = [.. Enumerable.Range(0, _clients.Count)];
            }
        }

        // Unlocked credentials first; locked ones stay as a last resort.
        return [.. order.OrderBy(index => _locks.IsLocked(LockKey(index), out _) ? 1 : 0)];
    }

    private string LockKey(int index) => $"{_providerKey}#cred{index}";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        Exception? lastError = null;

        foreach (var index in SelectionOrder())
        {
            try
            {
                var response = await _clients[index].GetResponseAsync(messageList, options, cancellationToken);
                _locks.ReportSuccess(LockKey(index));
                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                var message = ex.GetBaseException().Message;
                _locks.ReportFailure(LockKey(index), message);
                _logger.LogWarning(
                    "[CRED-POOL] {ProviderKey} credential #{Index} failed ({Message}); trying next",
                    _providerKey, index, message);
            }
        }

        throw lastError ?? new InvalidOperationException($"Credential pool for '{_providerKey}' has no usable credentials.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamingImpl(messages.ToList(), options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamingImpl(
        List<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var index in SelectionOrder())
        {
            // First-chunk probe per credential: a key that fails before streaming
            // starts fails over silently; a mid-stream failure surfaces to the caller.
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            ChatResponseUpdate? firstChunk = null;
            try
            {
                enumerator = _clients[index]
                    .GetStreamingResponseAsync(messages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                if (await enumerator.MoveNextAsync())
                {
                    firstChunk = enumerator.Current;
                }
            }
            catch (OperationCanceledException)
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }

                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _locks.ReportFailure(LockKey(index), ex.GetBaseException().Message);
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }

                continue;
            }

            _locks.ReportSuccess(LockKey(index));
            try
            {
                if (firstChunk is not null)
                {
                    yield return firstChunk;
                    while (await enumerator!.MoveNextAsync())
                    {
                        yield return enumerator.Current;
                    }
                }

                yield break;
            }
            finally
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync();
                }
            }
        }

        throw lastError ?? new InvalidOperationException($"Credential pool for '{_providerKey}' has no usable credentials.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : _clients[0].GetService(serviceType, serviceKey);

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }
}
