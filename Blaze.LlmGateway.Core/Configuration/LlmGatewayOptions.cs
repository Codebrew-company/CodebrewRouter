namespace Blaze.LlmGateway.Core.Configuration;

public class LlmGatewayOptions
{
    public const string SectionName = "LlmGateway";

    public ProvidersOptions Providers { get; set; } = new();
    public RoutingOptions Routing { get; set; } = new();
    public CodebrewRouterOptions CodebrewRouter { get; set; } = new();
    public ModelAvailabilityOptions Availability { get; set; } = new();
    public PromptCleanupOptions PromptCleanup { get; set; } = new();
}

public class ProvidersOptions
{
    public AzureFoundryOptions AzureFoundry { get; set; } = new();
    public FoundryLocalOptions FoundryLocal { get; set; } = new();
    public OllamaLocalOptions OllamaLocal { get; set; } = new();
    public GithubModelsOptions GithubModels { get; set; } = new();
}

public class AzureFoundryOptions
{
    public string Endpoint { get; set; } = "";
    public string ResponsesEndpoint { get; set; } = "";
    public string Model { get; set; } = "gpt-4o";
    /// <summary>Optional. If absent, DefaultAzureCredential is used.</summary>
    public string? ApiKey { get; set; }
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 4096;
}

public class FoundryLocalOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:58484";
    public string Model { get; set; } = "Phi-4-mini-instruct-cuda-gpu:5";
    /// <summary>Foundry Local uses "notneeded" as the API key.</summary>
    public string ApiKey { get; set; } = "notneeded";
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 4096;
}

public class OllamaLocalOptions
{
    public string BaseUrl { get; set; } = "http://192.168.16.12:11434";
    public string Model { get; set; } = "gemma4:e4b";
    public int MaxContextTokens { get; set; } = 32768;
    public int ReservedOutputTokens { get; set; } = 2048;
}

public class GithubModelsOptions
{
    public string Endpoint { get; set; } = "https://models.inference.ai.azure.com";
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>GitHub Models API key (personal access token with model access).</summary>
    public string? ApiKey { get; set; }
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 4096;
}

public class RoutingOptions
{
    /// <summary>Name of the Ollama model used to route requests (the meta-router).</summary>
    public string RouterModel { get; set; } = "router";
    /// <summary>Fallback destination when meta-routing fails.</summary>
    public string FallbackDestination { get; set; } = nameof(RouteDestination.AzureFoundry);
    /// <summary>Failover chains: maps primary destination to list of fallback providers to try if primary fails.</summary>
    public Dictionary<string, List<string>> FailoverChains { get; set; } = new()
    {
        { "AzureFoundry", ["FoundryLocal"] },
        { "FoundryLocal", ["AzureFoundry"] }
    };
}

public class ModelAvailabilityOptions
{
    public bool Enabled { get; set; } = true;
    public int StartupProbeTimeoutSeconds { get; set; } = 5;
    public int RefreshIntervalSeconds { get; set; } = 60;
}
