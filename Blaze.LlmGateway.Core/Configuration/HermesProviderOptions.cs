namespace Blaze.LlmGateway.Core.Configuration;

public class HermesProviderOptions
{
    public const string SectionName = "Hermes";

    /// <summary>Hostname where Hermes gateways run. Default: localhost.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Shared API key for all Hermes profiles (API_SERVER_KEY).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Per-profile configuration keyed by profile name.</summary>
    public Dictionary<string, HermesProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default max context tokens (individual profile can override).</summary>
    public int MaxContextTokens { get; set; } = 128000;

    /// <summary>Reserved output tokens.</summary>
    public int ReservedOutputTokens { get; set; } = 16384;

    /// <summary>Path appended to each profile's base URL for health checks.</summary>
    public string HealthCheckPath { get; set; } = "/health";
}

public class HermesProfileOptions
{
    public int Port { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Optional override of the shared Hostname (e.g. remote MLX server).</summary>
    public string? Host { get; set; }

    /// <summary>Optional override of the shared API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional full endpoint URI override (e.g. "https://api.openai.com/v1"). If specified, Host and Port are ignored.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Optional backend model name override (e.g. "gpt-4o"). If specified, overrides the default sentinel.</summary>
    public string? Model { get; set; }

    /// <summary>Optional override of max context tokens.</summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>Capability tags for this profile.</summary>
    public string[] Capabilities { get; set; } = [];

    /// <summary>Tags for grouping/filtering.</summary>
    public string[] Tags { get; set; } = [];

    /// <summary>Rate limit: max requests per minute (0 = unlimited).</summary>
    public int MaxRequestsPerMinute { get; set; } = 0;

    /// <summary>Rate limit: max tokens per minute (0 = unlimited).</summary>
    public int MaxTokensPerMinute { get; set; } = 0;
}
