namespace Blaze.LlmGateway.Api.Auth;

/// <summary>
/// In-memory cache over the API keys stored in <see cref="IProtocolStore"/> so /v1 requests
/// never hit the store per request. Invalidated explicitly on key CRUD.
/// </summary>
public sealed class ApiKeyCache(IProtocolStore store)
{
    private volatile Dictionary<string, AdminApiKey>? _byKeyMaterial;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>Returns the key record for the presented key material, or null when invalid.</summary>
    public async ValueTask<AdminApiKey?> ValidateAsync(string presentedKey, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.GetValueOrDefault(presentedKey);
    }

    /// <summary>True when at least one API key has been minted.</summary>
    public async ValueTask<bool> AnyKeysAsync(CancellationToken cancellationToken)
        => (await GetSnapshotAsync(cancellationToken)).Count > 0;

    /// <summary>Drops the snapshot; the next request reloads from the store.</summary>
    public void Invalidate() => _byKeyMaterial = null;

    private async ValueTask<Dictionary<string, AdminApiKey>> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = _byKeyMaterial;
        if (snapshot is not null)
        {
            return snapshot;
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _byKeyMaterial;
            if (snapshot is not null)
            {
                return snapshot;
            }

            var keys = await store.ListApiKeysAsync(cancellationToken);
            snapshot = new Dictionary<string, AdminApiKey>(StringComparer.Ordinal);
            foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k.Key)))
            {
                snapshot[key.Key] = key;
            }
            _byKeyMaterial = snapshot;
            return snapshot;
        }
        finally
        {
            _reloadGate.Release();
        }
    }
}
