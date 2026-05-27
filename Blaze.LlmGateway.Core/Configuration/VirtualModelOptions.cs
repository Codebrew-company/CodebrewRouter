namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Configuration for an OpenAI-compatible virtual model exposed by the gateway.
/// </summary>
public class VirtualModelOptions
{
    /// <summary>When false, the virtual model is omitted from discovery and cannot be resolved.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Model ID that appears in <c>GET /v1/models</c> and can be used in chat requests.</summary>
    public string ModelId { get; set; } = "";

    /// <summary>Provider label exposed in model discovery.</summary>
    public string Provider { get; set; } = "CodebrewRouter";

    /// <summary>Owner label exposed in model discovery.</summary>
    public string OwnedBy { get; set; } = "codebrew";

    /// <summary>Source label exposed in model discovery.</summary>
    public string Source { get; set; } = "virtual";

    /// <summary>Optional virtual model ID that this profile inherits router behavior from.</summary>
    public string? Extends { get; set; }

    /// <summary>Optional system prompt prepended to every request for this virtual model.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Response contract used by protocol adapters to shape model output.</summary>
    public string ResponseContract { get; set; } = VirtualModelResponseContracts.NaturalLanguage;

    /// <summary>Agent runtime mode used to expose this model internally.</summary>
    public string AgentMode { get; set; } = "chat-client-agent";

    /// <summary>Workflow shape used by this virtual model, for example single, sequential, or concurrent.</summary>
    public string Workflow { get; set; } = "single";

    /// <summary>Portable capability tags advertised in model discovery.</summary>
    public string[] Capabilities { get; set; } = ["chat"];

    /// <summary>Whether this profile can accept OpenAI-compatible tool definitions.</summary>
    public bool ToolSupport { get; set; }

    /// <summary>Whether this profile can accept image/content-part inputs.</summary>
    public bool VisionSupport { get; set; }

    /// <summary>Whether this profile requires cloud egress by default.</summary>
    public bool CloudRequired { get; set; }

    /// <summary>Advertised context window for this profile when known.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>MCP server IDs enabled for this profile.</summary>
    public string[] McpServers { get; set; } = [];

    /// <summary>Skill pack IDs enabled for this profile.</summary>
    public string[] Skills { get; set; } = [];

    /// <summary>Memory behavior for this profile.</summary>
    public VirtualModelMemoryOptions? Memory { get; set; }

    /// <summary>Task-type fallback chains used by the CodebrewRouter backing client.</summary>
    public Dictionary<string, string[]> FallbackRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Context compaction behavior for this virtual model.</summary>
    public ContextCompactionOptions? ContextCompaction { get; set; }

    /// <summary>
    /// When set, this virtual model resolves through the provider catalog instead of
    /// the old keyed-DI path. The value is a model name defined in <c>ProviderCatalog.ModelRouting</c>.
    /// </summary>
    public string? CatalogModel { get; set; }
}

public class VirtualModelMemoryOptions
{
    public bool Enabled { get; set; }

    public string Scope { get; set; } = "developer+repo";

    public string? Provider { get; set; }

    public string[] Collections { get; set; } = [];
}

public static class VirtualModelOptionsExtensions
{
    public static IReadOnlyList<VirtualModelOptions> GetEffectiveVirtualModels(this LlmGatewayOptions options)
    {
        var profiles = new List<VirtualModelOptions>();
        if (options.CodebrewRouter.Enabled && !string.IsNullOrWhiteSpace(options.CodebrewRouter.ModelId))
        {
            profiles.Add(options.CodebrewRouter.ToVirtualModelOptions());
        }

        foreach (var (key, configured) in options.VirtualModels)
        {
            var profile = configured.ToEffectiveVirtualModelOptions(key, options.CodebrewRouter);
            if (profile.Enabled && !string.IsNullOrWhiteSpace(profile.ModelId))
            {
                profiles.Add(profile);
            }
        }

        return profiles
            .GroupBy(profile => profile.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
    }

    public static VirtualModelOptions? FindVirtualModel(this LlmGatewayOptions options, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return options.GetEffectiveVirtualModels()
            .FirstOrDefault(profile => string.Equals(profile.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
    }

    public static VirtualModelOptions ToVirtualModelOptions(this CodebrewRouterOptions options)
        => new()
        {
            Enabled = options.Enabled,
            ModelId = options.ModelId,
            Provider = "CodebrewRouter",
            OwnedBy = "codebrew",
            Source = "virtual",
            Extends = null,
            ResponseContract = VirtualModelResponseContracts.NaturalLanguage,
            AgentMode = "chat-client-agent",
            Workflow = "single",
            Capabilities = ["chat", "routing", "tools"],
            ToolSupport = true,
            VisionSupport = false,
            CloudRequired = false,
            ContextWindow = null,
            McpServers = [],
            Skills = [],
            Memory = new VirtualModelMemoryOptions { Enabled = false, Scope = "developer+repo" },
            FallbackRules = CloneFallbackRules(options.FallbackRules),
            ContextCompaction = options.ContextCompaction,
            CatalogModel = null
        };

    public static CodebrewRouterOptions ToCodebrewRouterOptions(this VirtualModelOptions profile)
        => new()
        {
            Enabled = profile.Enabled,
            ModelId = profile.ModelId,
            FallbackRules = CloneFallbackRules(profile.FallbackRules),
            ContextCompaction = profile.ContextCompaction ?? new ContextCompactionOptions()
        };

    private static VirtualModelOptions ToEffectiveVirtualModelOptions(
        this VirtualModelOptions profile,
        string key,
        CodebrewRouterOptions defaults)
        => new()
        {
            Enabled = profile.Enabled,
            ModelId = string.IsNullOrWhiteSpace(profile.ModelId) ? key : profile.ModelId,
            Provider = string.IsNullOrWhiteSpace(profile.Provider) ? "CodebrewRouter" : profile.Provider,
            OwnedBy = string.IsNullOrWhiteSpace(profile.OwnedBy) ? "codebrew" : profile.OwnedBy,
            Source = string.IsNullOrWhiteSpace(profile.Source) ? "virtual" : profile.Source,
            Extends = NormalizeExtends(profile.Extends, defaults),
            SystemPrompt = profile.SystemPrompt,
            ResponseContract = string.IsNullOrWhiteSpace(profile.ResponseContract)
                ? VirtualModelResponseContracts.NaturalLanguage
                : profile.ResponseContract,
            AgentMode = string.IsNullOrWhiteSpace(profile.AgentMode) ? "chat-client-agent" : profile.AgentMode,
            Workflow = string.IsNullOrWhiteSpace(profile.Workflow) ? "single" : profile.Workflow,
            Capabilities = profile.Capabilities.Length > 0 ? profile.Capabilities.ToArray() : ["chat"],
            ToolSupport = profile.ToolSupport,
            VisionSupport = profile.VisionSupport,
            CloudRequired = profile.CloudRequired,
            ContextWindow = profile.ContextWindow,
            McpServers = profile.McpServers.ToArray(),
            Skills = profile.Skills.ToArray(),
            Memory = CloneMemory(profile.Memory),
            FallbackRules = profile.FallbackRules.Count > 0
                ? CloneFallbackRules(profile.FallbackRules)
                : CloneFallbackRules(defaults.FallbackRules),
            ContextCompaction = profile.ContextCompaction ?? defaults.ContextCompaction,
            CatalogModel = profile.CatalogModel
        };

    private static string? NormalizeExtends(string? extends, CodebrewRouterOptions defaults)
    {
        if (string.IsNullOrWhiteSpace(extends))
        {
            return null;
        }

        return string.Equals(extends, defaults.ModelId, StringComparison.OrdinalIgnoreCase)
            ? defaults.ModelId
            : extends;
    }

    private static Dictionary<string, string[]> CloneFallbackRules(IDictionary<string, string[]> rules)
        => rules.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private static VirtualModelMemoryOptions? CloneMemory(VirtualModelMemoryOptions? options)
        => options is null
            ? null
            : new VirtualModelMemoryOptions
            {
                Enabled = options.Enabled,
                Scope = options.Scope,
                Provider = options.Provider,
                Collections = options.Collections.ToArray()
            };
}

public static class VirtualModelResponseContracts
{
    public const string NaturalLanguage = "natural-language";
    public const string YardlyJson = "yardly-json";

    public static bool RequiresYardlyJson(VirtualModelOptions? profile)
        => string.Equals(profile?.ResponseContract, YardlyJson, StringComparison.OrdinalIgnoreCase);
}
