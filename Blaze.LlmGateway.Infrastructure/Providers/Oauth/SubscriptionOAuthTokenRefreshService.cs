using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.Providers.Oauth;

/// <summary>
/// Proactively refreshes subscription OAuth tokens before they expire (9router's
/// tokenRefresh pattern: refresh a configurable lead time — default 5 min — ahead of
/// expiry). Only registered when the subscription gate is on. Reactive refresh on a
/// live 401/403 is handled separately by the per-provider chat client.
/// </summary>
public sealed class SubscriptionOAuthTokenRefreshService(
    SubscriptionTokenRegistry registry,
    ISubscriptionOAuthClient oauthClient,
    IOptions<LlmGatewayOptions> options,
    ILogger<SubscriptionOAuthTokenRefreshService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>How often the proactive sweep runs.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        do
        {
            try
            {
                await RefreshDuePassAsync(_time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subscription OAuth token refresh sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One refresh pass: refresh every token within its provider's lead window of expiry.
    /// Returns the number of tokens refreshed. Exposed for deterministic testing.
    /// </summary>
    public async Task<int> RefreshDuePassAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var providers = options.Value.Providers.Subscription.Providers
            .Where(p => p.Kind == SubscriptionAuthKind.OAuth)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var refreshed = 0;
        foreach (var record in registry.RefreshableRecords())
        {
            if (!providers.TryGetValue(record.ProviderName, out var provider))
            {
                continue;
            }

            var lead = TimeSpan.FromMinutes(Math.Max(0, provider.OAuth?.RefreshLeadMinutes ?? 5));
            if (!record.NeedsRefresh(now, lead))
            {
                continue;
            }

            var updated = await oauthClient.RefreshAsync(provider, record, cancellationToken);
            if (updated is not null)
            {
                registry.SetToken(updated);
                refreshed++;
                logger.LogInformation(
                    "Refreshed subscription token for '{Provider}'; next expiry {ExpiresAt:u}",
                    record.ProviderName, updated.ExpiresAt);
            }
            else
            {
                logger.LogWarning("Subscription token refresh for '{Provider}' did not return a new token", record.ProviderName);
            }
        }

        return refreshed;
    }
}
