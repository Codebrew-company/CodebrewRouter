using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.Providers.Oauth;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blaze.LlmGateway.Tests.Infrastructure;

/// <summary>
/// P5b: OAuth subscription reuse — opt-in, off by default. Verifies the token registry
/// rotates the live credential, the proactive refresh service refreshes tokens within
/// their lead window, and DI registration only happens when the gate is on.
/// </summary>
public sealed class SubscriptionOAuthTests
{
    // ── token registry ───────────────────────────────────────────────────────

    [Fact]
    public void Registry_SetToken_RotatesLiveCredentialInPlace()
    {
        var registry = new SubscriptionTokenRegistry();
        var credential = registry.GetOrCreateCredential("Prov");

        registry.SetToken(new SubscriptionTokenRecord("Prov", "tok-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)));

        // Same credential instance the chat client holds now carries the new token.
        credential.Deconstruct(out var key1);
        key1.Should().Be("tok-1");

        registry.SetToken(new SubscriptionTokenRecord("Prov", "tok-2", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)));
        credential.Deconstruct(out var key2);
        key2.Should().Be("tok-2");
    }

    [Fact]
    public void Registry_NeedsRefresh_RespectsLeadWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new SubscriptionTokenRecord("P", "t", "r", now.AddMinutes(10));

        record.NeedsRefresh(now, TimeSpan.FromMinutes(5)).Should().BeFalse("10 min out, 5 min lead");
        record.NeedsRefresh(now, TimeSpan.FromMinutes(11)).Should().BeTrue("lead exceeds remaining life");
        record.NeedsRefresh(now.AddMinutes(20), TimeSpan.FromMinutes(5)).Should().BeTrue("already expired");
    }

    [Fact]
    public void Registry_RefreshableRecords_ExcludesTokenlessAndUnexpiring()
    {
        var registry = new SubscriptionTokenRegistry();
        registry.SetToken(new SubscriptionTokenRecord("HasRefresh", "a", "r", DateTimeOffset.UtcNow.AddHours(1)));
        registry.SetToken(new SubscriptionTokenRecord("NoRefresh", "b", null, DateTimeOffset.UtcNow.AddHours(1)));

        registry.RefreshableRecords().Should().ContainSingle().Which.ProviderName.Should().Be("HasRefresh");
    }

    // ── proactive refresh service ─────────────────────────────────────────────

    [Fact]
    public async Task RefreshService_RefreshesTokenWithinLeadWindow()
    {
        var time = new TestClock(DateTimeOffset.Parse("2026-07-06T00:00:00Z"));
        var registry = new SubscriptionTokenRegistry();
        // Expires in 3 minutes; lead is 5 → due for refresh.
        registry.SetToken(new SubscriptionTokenRecord("Claude", "old", "refresh-old", time.GetUtcNow().AddMinutes(3)));

        var oauth = new FakeOAuthClient(newAccessToken: "new", expiresIn: TimeSpan.FromHours(1), time);
        var options = SubscriptionOptionsWith(new SubscriptionProviderOptions
        {
            Name = "Claude",
            Kind = SubscriptionAuthKind.OAuth,
            OAuth = new SubscriptionOAuthOptions { RefreshLeadMinutes = 5, TokenEndpoint = "https://x/token", ClientId = "c" }
        });

        var service = new SubscriptionOAuthTokenRefreshService(
            registry, oauth, options, NullLogger<SubscriptionOAuthTokenRefreshService>.Instance, time);

        var refreshed = await service.RefreshDuePassAsync(time.GetUtcNow());

        refreshed.Should().Be(1);
        registry.GetRecord("Claude")!.AccessToken.Should().Be("new");
        registry.GetOrCreateCredential("Claude").Deconstruct(out var rotated);
        rotated.Should().Be("new");
    }

    [Fact]
    public async Task RefreshService_SkipsTokenOutsideLeadWindow()
    {
        var time = new TestClock(DateTimeOffset.Parse("2026-07-06T00:00:00Z"));
        var registry = new SubscriptionTokenRegistry();
        // Expires in 30 minutes; lead 5 → not yet due.
        registry.SetToken(new SubscriptionTokenRecord("Claude", "old", "refresh-old", time.GetUtcNow().AddMinutes(30)));

        var oauth = new FakeOAuthClient("new", TimeSpan.FromHours(1), time);
        var service = new SubscriptionOAuthTokenRefreshService(
            registry, oauth,
            SubscriptionOptionsWith(new SubscriptionProviderOptions
            {
                Name = "Claude",
                Kind = SubscriptionAuthKind.OAuth,
                OAuth = new SubscriptionOAuthOptions { RefreshLeadMinutes = 5 }
            }),
            NullLogger<SubscriptionOAuthTokenRefreshService>.Instance, time);

        (await service.RefreshDuePassAsync(time.GetUtcNow())).Should().Be(0);
        registry.GetRecord("Claude")!.AccessToken.Should().Be("old");
    }

    // ── HTTP refresh client ───────────────────────────────────────────────────

    [Fact]
    public async Task HttpOAuthClient_RefreshGrant_ParsesNewToken()
    {
        var time = new TestClock(DateTimeOffset.Parse("2026-07-06T00:00:00Z"));
        var handler = new StubHandler("""{"access_token":"fresh","refresh_token":"r2","expires_in":3600}""");
        var httpClient = new HttpClient(handler);
        var client = new HttpSubscriptionOAuthClient(httpClient, time);

        var provider = new SubscriptionProviderOptions
        {
            Name = "Claude",
            Kind = SubscriptionAuthKind.OAuth,
            OAuth = new SubscriptionOAuthOptions { TokenEndpoint = "https://auth.example.com/token", ClientId = "cid" }
        };
        var current = new SubscriptionTokenRecord("Claude", "old", "r1", time.GetUtcNow());

        var updated = await client.RefreshAsync(provider, current);

        updated.Should().NotBeNull();
        updated!.AccessToken.Should().Be("fresh");
        updated.RefreshToken.Should().Be("r2");
        updated.ExpiresAt.Should().Be(time.GetUtcNow().AddHours(1));
        handler.LastRequestBody.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=r1");
    }

    [Fact]
    public async Task HttpOAuthClient_NoRefreshToken_ReturnsNull()
    {
        var client = new HttpSubscriptionOAuthClient(new HttpClient(new StubHandler("{}")));
        var provider = new SubscriptionProviderOptions
        {
            Kind = SubscriptionAuthKind.OAuth,
            OAuth = new SubscriptionOAuthOptions { TokenEndpoint = "https://x/token" }
        };

        var result = await client.RefreshAsync(provider, new SubscriptionTokenRecord("P", "a", null, DateTimeOffset.UtcNow));

        result.Should().BeNull("no refresh token → nothing to exchange");
    }

    // ── DI registration gate ──────────────────────────────────────────────────

    [Fact]
    public void GateOff_ByDefault_RegistersNoOAuthInfra()
    {
        using var provider = BuildDi(new SubscriptionOptions
        {
            Enabled = false,
            Providers = [OAuthProvider("ClaudeOAuth")]
        });

        provider.GetService<SubscriptionTokenRegistry>().Should().BeNull();
        provider.GetServices<IHostedService>().OfType<SubscriptionOAuthTokenRefreshService>().Should().BeEmpty();
        provider.GetKeyedService<IChatClient>("ClaudeOAuth").Should().BeNull();
    }

    [Fact]
    public void GateOn_RegistersRegistry_RefreshService_AndKeyedClient()
    {
        using var provider = BuildDi(new SubscriptionOptions
        {
            Enabled = true,
            Providers = [OAuthProvider("ClaudeOAuth")]
        });

        provider.GetService<SubscriptionTokenRegistry>().Should().NotBeNull();
        provider.GetServices<IHostedService>().OfType<SubscriptionOAuthTokenRefreshService>().Should().ContainSingle();
        provider.GetKeyedService<IChatClient>("ClaudeOAuth").Should().NotBeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static SubscriptionProviderOptions OAuthProvider(string name) => new()
    {
        Name = name,
        Kind = SubscriptionAuthKind.OAuth,
        Endpoint = "https://api.anthropic.com/v1",
        Model = "claude-opus",
        TosNote = "OAuth reuse may violate the provider's ToS.",
        OAuth = new SubscriptionOAuthOptions { ClientId = "cid", TokenEndpoint = "https://x/token" }
    };

    private static IOptions<LlmGatewayOptions> SubscriptionOptionsWith(params SubscriptionProviderOptions[] providers)
        => Options.Create(new LlmGatewayOptions
        {
            Providers = new ProvidersOptions
            {
                Subscription = new SubscriptionOptions { Enabled = true, Providers = [.. providers] }
            }
        });

    private static ServiceProvider BuildDi(SubscriptionOptions subscription)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Options.Create(new LlmGatewayOptions
        {
            Providers = new ProvidersOptions { Subscription = subscription }
        }));
        services.AddSubscriptionOAuth();
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeOAuthClient(string newAccessToken, TimeSpan expiresIn, TimeProvider time) : ISubscriptionOAuthClient
    {
        public Task<SubscriptionTokenRecord?> RefreshAsync(
            SubscriptionProviderOptions provider,
            SubscriptionTokenRecord current,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SubscriptionTokenRecord?>(current with
            {
                AccessToken = newAccessToken,
                ExpiresAt = time.GetUtcNow() + expiresIn
            });
    }
}
