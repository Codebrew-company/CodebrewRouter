using Blaze.LlmGateway.Api.Auth;

namespace Blaze.LlmGateway.Api;

public static class AdminEndpoint
{
    public static async Task<AdminApiKey> CreateKeyAsync(
        AdminCreateApiKeyRequest request,
        IProtocolStore store,
        ApiKeyCache keyCache,
        CancellationToken cancellationToken)
    {
        var key = new AdminApiKey(
            Id: Ids.New("key"),
            TenantId: string.IsNullOrWhiteSpace(request.TenantId) ? "tenant_default" : request.TenantId!,
            Name: string.IsNullOrWhiteSpace(request.Name) ? "default" : request.Name!,
            Key: $"cbr_{Guid.NewGuid():N}",
            AllowedModels: request.AllowedModels ?? ["codebrewRouter"],
            AllowCloud: request.AllowCloud,
            Scopes: request.Scopes ?? ["chat", "responses", "a2a"],
            CreatedAt: DateTimeOffset.UtcNow);

        await store.SaveApiKeyAsync(key, cancellationToken);
        keyCache.Invalidate();

        // Full key material is returned exactly once — at mint time.
        return key;
    }

    /// <summary>
    /// Lists keys with the key material redacted (GHSA-vjc7 lesson: read APIs never
    /// return plaintext secrets). The full key is only visible in the mint response.
    /// </summary>
    public static async Task<object> ListKeysAsync(IProtocolStore store, CancellationToken cancellationToken)
    {
        var keys = await store.ListApiKeysAsync(cancellationToken);
        return new
        {
            @object = "list",
            data = keys.Select(key => key with { Key = Redact(key.Key) }).ToArray()
        };
    }

    public static async Task<IResult> DeleteKeyAsync(
        string keyId,
        IProtocolStore store,
        ApiKeyCache keyCache,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteApiKeyAsync(keyId, cancellationToken);
        if (deleted)
        {
            keyCache.Invalidate();
        }

        return deleted
            ? Results.Json(new { id = keyId, @object = "api_key.deleted", deleted = true })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"API key '{keyId}' was not found.", "not_found", "api_key_not_found")));
    }

    public static Task<SpendSummary> SpendAsync(string? keyId, IProtocolStore store, CancellationToken cancellationToken)
        => store.GetUsageSummaryAsync(keyId, cancellationToken);

    public static async Task<object> UsageHistoryAsync(
        int? limit,
        int? offset,
        string? keyId,
        IProtocolStore store,
        CancellationToken cancellationToken)
        => new
        {
            @object = "list",
            data = await store.ListUsageAsync(
                Math.Clamp(limit ?? 100, 1, 1000),
                Math.Max(offset ?? 0, 0),
                keyId,
                cancellationToken)
        };

    public static async Task<object> UsageChartAsync(int? days, IProtocolStore store, CancellationToken cancellationToken)
        => new
        {
            @object = "list",
            data = await store.GetUsageDailyAsync(Math.Clamp(days ?? 30, 1, 365), cancellationToken)
        };

    public static async Task<object> RecentRoutesAsync(IProtocolStore store, CancellationToken cancellationToken)
        => new { @object = "list", data = await store.ListRouteDecisionsAsync(cancellationToken: cancellationToken) };

    public static async Task<object> AssetsAsync(IProtocolStore store, CancellationToken cancellationToken)
        => new { @object = "list", data = await store.ListAssetsAsync(cancellationToken) };

    public static async Task<object> SyncAssetsAsync(IProtocolStore store, CancellationToken cancellationToken)
        => new
        {
            @object = "asset_sync",
            status = "completed",
            data = await store.ListAssetsAsync(cancellationToken)
        };

    private static string Redact(string key)
        => key.Length <= 12 ? "****" : $"{key[..8]}…{key[^4..]}";
}
