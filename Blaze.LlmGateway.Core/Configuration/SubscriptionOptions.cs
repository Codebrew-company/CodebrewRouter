namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// P5 — subscription upstream providers. Binds from <c>LlmGateway:Providers:Subscription</c>.
///
/// Master gate for the whole feature (both the ToS-clean API-key path P5a and the
/// opt-in OAuth path P5b). <see cref="Enabled"/> defaults to <c>false</c>: with the
/// gate off, no subscription clients are registered and no OAuth flows exist — the
/// acceptance criterion for a fresh install. Single-operator personal use only.
///
/// Permanent non-goals (never implemented here): MITM interception, tool cloaking,
/// TLS/JA3 or header spoofing, or any detection-evasion. Those are the source of half
/// of the incumbents' CVE surface and stay excluded by design.
/// </summary>
public sealed class SubscriptionOptions
{
    /// <summary>Master gate. Default <c>false</c> — off unless the owner opts in.</summary>
    public bool Enabled { get; set; }

    public List<SubscriptionProviderOptions> Providers { get; set; } = [];
}

public enum SubscriptionAuthKind
{
    /// <summary>ToS-clean pay-as-you-go / free-tier API key (P5a, default).</summary>
    ApiKey = 0,

    /// <summary>OAuth device/PKCE reuse of the owner's own subscription (P5b, opt-in).</summary>
    OAuth = 1,
}

public sealed class SubscriptionProviderOptions
{
    /// <summary>Keyed-client name, referenced from FallbackRules / ProviderCatalog / VirtualModels.</summary>
    public string Name { get; set; } = "";

    /// <summary>Per-provider enable flag (in addition to the master gate).</summary>
    public bool Enabled { get; set; } = true;

    public SubscriptionAuthKind Kind { get; set; } = SubscriptionAuthKind.ApiKey;

    /// <summary>OpenAI-compatible base URL for the upstream.</summary>
    public string Endpoint { get; set; } = "";

    public string Model { get; set; } = "";
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 4096;

    /// <summary>API key for <see cref="SubscriptionAuthKind.ApiKey"/>. First credential-pool entry.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Additional API keys — rotated as a credential pool (P3.2).</summary>
    public List<string> ApiKeys { get; set; } = [];

    /// <summary>OAuth configuration for <see cref="SubscriptionAuthKind.OAuth"/> (P5b).</summary>
    public SubscriptionOAuthOptions? OAuth { get; set; }

    /// <summary>
    /// Documented ToS / ban-risk note. Logged at registration so the operator is
    /// reminded that reusing a subscription may violate that provider's terms — a
    /// business/legal decision only the owner can make for the owner's own accounts.
    /// </summary>
    public string TosNote { get; set; } = "";

    public int MaxRequestsPerMinute { get; set; }
    public int MaxTokensPerMinute { get; set; }

    /// <summary>Dashboard grouping tag; "subscription" is always implied.</summary>
    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// OAuth device/PKCE settings for a subscription provider (P5b). Only consulted when
/// the provider's <see cref="SubscriptionProviderOptions.Kind"/> is
/// <see cref="SubscriptionAuthKind.OAuth"/> and the master gate is on.
/// </summary>
public sealed class SubscriptionOAuthOptions
{
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public List<string> Scopes { get; set; } = [];
    public string? RedirectUri { get; set; }

    /// <summary>PKCE is default-on; disable only for providers that reject it.</summary>
    public bool UsePkce { get; set; } = true;

    /// <summary>Refresh the access token this many minutes before expiry (9router: 5).</summary>
    public int RefreshLeadMinutes { get; set; } = 5;
}
