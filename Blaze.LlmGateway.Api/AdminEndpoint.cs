namespace Blaze.LlmGateway.Api;

public static class AdminEndpoint
{
    public static async Task<AdminApiKey> CreateKeyAsync(
        AdminCreateApiKeyRequest request,
        IProtocolStore store,
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
        return key;
    }

    public static async Task<object> ListKeysAsync(IProtocolStore store, CancellationToken cancellationToken)
        => new { @object = "list", data = await store.ListApiKeysAsync(cancellationToken) };

    public static async Task<IResult> DeleteKeyAsync(string keyId, IProtocolStore store, CancellationToken cancellationToken)
        => await store.DeleteApiKeyAsync(keyId, cancellationToken)
            ? Results.Json(new { id = keyId, @object = "api_key.deleted", deleted = true })
            : Results.NotFound(new ErrorResponse(new ErrorDetail($"API key '{keyId}' was not found.", "not_found", "api_key_not_found")));

    public static SpendSummary Spend(string? keyId)
        => new("spend.summary", keyId, 0, 0, 0m);

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
}
