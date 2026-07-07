using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Infrastructure.Providers.Oauth;

/// <summary>
/// Performs OAuth token operations against a subscription provider's token endpoint —
/// the refresh-token grant used by the background refresh service and reactive 401/403
/// recovery. Login (device/PKCE authorization) is initiated from the dashboard; this
/// client owns the token-exchange half. No detection evasion — a plain RFC 6749 client.
/// </summary>
public interface ISubscriptionOAuthClient
{
    /// <summary>
    /// Exchanges a refresh token for a new access token. Returns the new record, or
    /// null if the provider has no OAuth config / no refresh token / the grant fails.
    /// </summary>
    Task<SubscriptionTokenRecord?> RefreshAsync(
        SubscriptionProviderOptions provider,
        SubscriptionTokenRecord current,
        CancellationToken cancellationToken = default);
}

public sealed class HttpSubscriptionOAuthClient(
    HttpClient httpClient,
    TimeProvider? timeProvider = null) : ISubscriptionOAuthClient
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<SubscriptionTokenRecord?> RefreshAsync(
        SubscriptionProviderOptions provider,
        SubscriptionTokenRecord current,
        CancellationToken cancellationToken = default)
    {
        if (provider.OAuth is not { } oauth
            || string.IsNullOrWhiteSpace(oauth.TokenEndpoint)
            || string.IsNullOrEmpty(current.RefreshToken))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken!,
            ["client_id"] = oauth.ClientId,
        };
        if (!string.IsNullOrWhiteSpace(oauth.ClientSecret))
        {
            form["client_secret"] = oauth.ClientSecret!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            return null;
        }

        var expiresIn = payload.ExpiresIn > 0 ? payload.ExpiresIn : 3600;
        return current with
        {
            AccessToken = payload.AccessToken!,
            // Providers may or may not rotate the refresh token; keep the old one if absent.
            RefreshToken = string.IsNullOrWhiteSpace(payload.RefreshToken) ? current.RefreshToken : payload.RefreshToken,
            ExpiresAt = _time.GetUtcNow().AddSeconds(expiresIn),
        };
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
