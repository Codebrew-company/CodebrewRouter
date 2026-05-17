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

    /// <summary>Task-type fallback chains used by the CodebrewRouter backing client.</summary>
    public Dictionary<string, string[]> FallbackRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Context compaction behavior for this virtual model.</summary>
    public ContextCompactionOptions? ContextCompaction { get; set; }
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
            FallbackRules = CloneFallbackRules(options.FallbackRules),
            ContextCompaction = options.ContextCompaction
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
            FallbackRules = profile.FallbackRules.Count > 0
                ? CloneFallbackRules(profile.FallbackRules)
                : CloneFallbackRules(defaults.FallbackRules),
            ContextCompaction = profile.ContextCompaction ?? defaults.ContextCompaction
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
}
