using System.ClientModel;
using System.Collections.Concurrent;

namespace Blaze.LlmGateway.Infrastructure.Providers.Oauth;

/// <summary>
/// One subscription account's OAuth token state.
/// </summary>
public sealed record SubscriptionTokenRecord(
    string ProviderName,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt)
{
    /// <summary>True when the token is within <paramref name="lead"/> of expiry (or past it).</summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan lead) => now + lead >= ExpiresAt;
}

/// <summary>
/// Holds live OAuth credentials for subscription providers (P5b). Each provider gets a
/// single mutable <see cref="ApiKeyCredential"/> that the downstream OpenAI-compatible
/// client is built against; the refresh service rotates the token in place via
/// <see cref="ApiKeyCredential.Update"/>, so the client always sends a current token
/// without being rebuilt. Only populated when the subscription gate is on and a
/// provider has completed OAuth login. Thread-safe.
/// </summary>
public sealed class SubscriptionTokenRegistry
{
    private sealed class Entry
    {
        public required ApiKeyCredential Credential { get; init; }
        public required SubscriptionTokenRecord Record { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the mutable credential for a provider, creating it (seeded with the
    /// current access token, or a placeholder until login completes) if absent.
    /// The downstream chat client is built against this exact instance.
    /// </summary>
    public ApiKeyCredential GetOrCreateCredential(string providerName)
        => _entries.GetOrAdd(providerName, _ => new Entry
        {
            Credential = new ApiKeyCredential("awaiting-oauth-login"),
            Record = new SubscriptionTokenRecord(providerName, string.Empty, null, DateTimeOffset.MinValue)
        }).Credential;

    /// <summary>Stores/updates a provider's token and rotates the live credential in place.</summary>
    public void SetToken(SubscriptionTokenRecord record)
    {
        var entry = _entries.GetOrAdd(record.ProviderName, _ => new Entry
        {
            Credential = new ApiKeyCredential(record.AccessToken.Length == 0 ? "awaiting-oauth-login" : record.AccessToken),
            Record = record
        });

        entry.Record = record;
        if (!string.IsNullOrEmpty(record.AccessToken))
        {
            entry.Credential.Update(record.AccessToken);
        }
    }

    public SubscriptionTokenRecord? GetRecord(string providerName)
        => _entries.TryGetValue(providerName, out var entry) ? entry.Record : null;

    /// <summary>All records with a refresh token and a real expiry — refresh candidates.</summary>
    public IReadOnlyList<SubscriptionTokenRecord> RefreshableRecords()
        => [.. _entries.Values
            .Select(e => e.Record)
            .Where(r => !string.IsNullOrEmpty(r.RefreshToken) && r.ExpiresAt > DateTimeOffset.MinValue)];
}
