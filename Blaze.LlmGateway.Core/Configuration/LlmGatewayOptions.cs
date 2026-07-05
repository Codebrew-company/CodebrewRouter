namespace Blaze.LlmGateway.Core.Configuration;

public class LlmGatewayOptions
{
    public const string SectionName = "LlmGateway";

    /// <summary>
    /// When true, model selection bypasses provider discovery/routing and sends all requests
    /// to the local LLamaSharp-backed provider.
    /// </summary>
    public bool OfflineOnly { get; set; }

    public ProvidersOptions Providers { get; set; } = new();
    public RoutingOptions Routing { get; set; } = new();
    public LocalInferenceOptions LocalInference { get; set; } = new();
    public CodebrewRouterOptions CodebrewRouter { get; set; } = new();
    public FusionOptions Fusion { get; set; } = new();
    public Dictionary<string, VirtualModelOptions> VirtualModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ModelAvailabilityOptions Availability { get; set; } = new();
    public PromptCleanupOptions PromptCleanup { get; set; } = new();
    public TaskClassificationOptions TaskClassification { get; set; } = new();
    public ContextSizingOptions ContextSizing { get; set; } = new();
    public ProviderCatalogOptions ProviderCatalog { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();

    /// <summary>
    /// Per-provider rate limits keyed by the keyed-DI provider name (e.g. "LmStudio",
    /// "OpenCodeGo_DeepSeekV4Pro"). Applied as a token-bucket wrapper around the provider client.
    /// </summary>
    public Dictionary<string, ProviderRateLimitOptions> RateLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public OutputSaverOptions OutputSavers { get; set; } = new();
    public ToolOutputCompressionOptions ToolOutputCompression { get; set; } = new();

    /// <summary>
    /// When true, all route-decision log events ([ROUTER-SELECT], [ROUTER-DEPLOY], [ROUTER-HEALTH],
    /// [ROUTER-FALLBACK], [ROUTER-FUSION], [ROUTER-FUSION-RESULT]) are emitted at Information level.
    /// When false (default), only errors and warnings are logged above Debug.
    /// </summary>
    public bool VerboseRouteLogging { get; set; }
}

public class ProvidersOptions
{
    public OllamaRouterOptions OllamaRouter { get; set; } = new();
    public LmStudioOptions LmStudio { get; set; } = new();
    public OpenCodeGoOptions OpenCodeGo { get; set; } = new();
    public DerpYardlyOptions DerpYardly { get; set; } = new();
    public HermesProviderOptions Hermes { get; set; } = new();
}

public class OllamaRouterOptions
{
    /// <summary>
    /// Primary Ollama router endpoint (e.g., http://192.168.16.53:11434).
    /// Used for prompt cleanup and task classification.
    /// </summary>
    public string PrimaryEndpoint { get; set; } = "http://192.168.16.53:11434";

    /// <summary>
    /// Fallback Ollama router endpoint (e.g., http://192.168.16.12:11434).
    /// Used when primary is unhealthy.
    /// </summary>
    public string FallbackEndpoint { get; set; } = "http://192.168.16.12:11434";

    /// <summary>
    /// Router model name. Both primary and fallback MUST have this model installed.
    /// </summary>
    public string Model { get; set; } = "gemma4:e4b";

    /// <summary>
    /// Maximum context tokens for router (used by prompt cleanup + classification).
    /// </summary>
    public int MaxContextTokens { get; set; } = 32768;

    /// <summary>
    /// Reserved output tokens for router responses.
    /// </summary>
    public int ReservedOutputTokens { get; set; } = 2048;
}

public class LmStudioOptions
{
    public string Endpoint { get; set; } = "http://192.168.16.56:1234/v1";
    public string Model { get; set; } = "local-model";
    /// <summary>LM Studio usually accepts any non-empty API key for its local OpenAI-compatible endpoint.</summary>
    public string ApiKey { get; set; } = "notneeded";
    public int MaxContextTokens { get; set; } = 32768;
    public int ReservedOutputTokens { get; set; } = 2048;
}

public class OpenCodeGoOptions
{
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/go/v1";
    public string ApiKey { get; set; } = "";
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 16384;
}

public class RoutingOptions
{
    /// <summary>Name of the Ollama model used to route requests (the meta-router).</summary>
    public string RouterModel { get; set; } = "router";
    /// <summary>Fallback destination when meta-routing fails.</summary>
    public string FallbackDestination { get; set; } = nameof(RouteDestination.OllamaRouter);
    /// <summary>Circuit-breaker cooldown (minutes) after the meta-router model fails or times out.</summary>
    public int CircuitBreakerCooldownMinutes { get; set; } = 5;
    /// <summary>Failover chains: maps primary destination to list of fallback providers to try if primary fails.</summary>
    public Dictionary<string, List<string>> FailoverChains { get; set; } = new()
    {
        { "OllamaRouter", ["LmStudio"] },
        { "LmStudio", ["OllamaRouter"] }
    };
}

public class ModelAvailabilityOptions
{
    public bool Enabled { get; set; } = true;
    public int StartupProbeTimeoutSeconds { get; set; } = 2;
    public int RefreshIntervalSeconds { get; set; } = 60;
}

public class DerpYardlyOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8651/v1";
    public string Model { get; set; } = "derp-yardly";
    public string ApiKey { get; set; } = "";
    public int MaxContextTokens { get; set; } = 32768;
    public int ReservedOutputTokens { get; set; } = 2048;
}

/// <summary>
/// API-key authentication for the public /v1 surface (default-deny; 9router CVE-2026-46339 lesson).
/// </summary>
public class AuthOptions
{
    /// <summary>
    /// Whether /v1 requires a valid gateway API key.
    /// null (default) = auto: enforced as soon as at least one API key has been minted.
    /// true = always enforced (requests fail closed when no keys exist).
    /// false = explicit dev bypass; /v1 is open.
    /// </summary>
    public bool? RequireApiKey { get; set; }

    /// <summary>Requests per minute allowed per API key on /v1. 0 = unlimited.</summary>
    public int RequestsPerMinutePerKey { get; set; }

    /// <summary>
    /// Requests per minute per client socket IP when key auth is not enforced.
    /// Identity comes from the TCP source address, never X-Forwarded-For (CVE-2026-55501 lesson).
    /// 0 = unlimited.
    /// </summary>
    public int RequestsPerMinutePerIp { get; set; }
}

public class ProviderRateLimitOptions
{
    public int RequestsPerMinute { get; set; }
    public int TokensPerMinute { get; set; }
}

/// <summary>
/// Output token savers (9router Caveman/Ponytail parity): system prompts appended per request
/// that make responses terse (Caveman) and code lazy/YAGNI (Ponytail).
/// </summary>
public class OutputSaverOptions
{
    public OutputSaverToggle Caveman { get; set; } = new();
    public OutputSaverToggle Ponytail { get; set; } = new();
}

public class OutputSaverToggle
{
    public bool Enabled { get; set; }

    /// <summary>Lite, Full, or Ultra.</summary>
    public string Level { get; set; } = "Full";
}

/// <summary>
/// RTK-style compression of tool-role message content (input token saver, fail-open).
/// </summary>
public class ToolOutputCompressionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Tool outputs shorter than this are never touched.</summary>
    public int MinLengthChars { get; set; } = 2000;

    /// <summary>Hard cap applied by the smart-truncate filter.</summary>
    public int MaxLengthChars { get; set; } = 24000;
}
