namespace Blaze.LlmGateway.Core.Catalog;

/// <summary>
/// A single provider deployment registered in the provider catalog.
/// Represents one endpoint that can serve chat completions for a given model.
/// </summary>
public sealed record ProviderDeployment
{
    /// <summary>Unique name used to reference this deployment (e.g. "azure-gpt4-mini").</summary>
    public required string Name { get; init; }

    /// <summary>Logical model name this deployment serves (e.g. "gpt-4o-mini").</summary>
    public required string ModelName { get; init; }

    /// <summary>Provider key used by <c>ProviderResolver</c> to resolve the IChatClient (e.g. "AzureFoundry", "OpenCodeGo", "Ollama").</summary>
    public required string Provider { get; init; }

    /// <summary>Optional base URL for the provider API.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Optional API key reference (resolved at runtime from config or env-var).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Optional deployment-specific model name override sent to the provider.</summary>
    public string? Model { get; init; }

    /// <summary>Relative weight used by weighted strategies (shuffle). Default: 1.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>Priority for tie-breaking / ordering. Lower values = higher priority. Default: 10.</summary>
    public int Priority { get; init; } = 10;

    /// <summary>Maximum context tokens this deployment can handle.</summary>
    public int MaxContextTokens { get; init; } = 4096;

    /// <summary>Capability flags: "chat", "tools", "vision", "reasoning", etc.</summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>Cost per token in USD (for cost-based routing).</summary>
    public double CostPerToken { get; init; } = 0;

    /// <summary>Arbitrary tags for filtering and grouping (e.g. "primary", "low-latency", "local").</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>Max requests allowed per minute. 0 = unlimited. Used by rate limiting middleware.</summary>
    public int MaxRequestsPerMinute { get; init; } = 0;

    /// <summary>Max output tokens allowed per minute. 0 = unlimited. Used by rate limiting middleware.</summary>
    public int MaxTokensPerMinute { get; init; } = 0;

    /// <summary>Whether this deployment is active. When false, it is skipped by all strategies.</summary>
    public bool Enabled { get; init; } = true;
}
