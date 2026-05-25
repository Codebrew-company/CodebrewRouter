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
    public Dictionary<string, VirtualModelOptions> VirtualModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ModelAvailabilityOptions Availability { get; set; } = new();
    public PromptCleanupOptions PromptCleanup { get; set; } = new();
    public TaskClassificationOptions TaskClassification { get; set; } = new();
    public ContextSizingOptions ContextSizing { get; set; } = new();
}

public class ProvidersOptions
{
    public OllamaRouterOptions OllamaRouter { get; set; } = new();
    public LmStudioOptions LmStudio { get; set; } = new();
    public OpenCodeGoOptions OpenCodeGo { get; set; } = new();
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
