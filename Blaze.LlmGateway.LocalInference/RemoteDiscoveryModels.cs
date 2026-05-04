namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Information about a remote model available via CodebrewRouter.
/// </summary>
public class RemoteModelInfo
{
    /// <summary>
    /// The model identifier (e.g., "gpt-4", "claude-3-sonnet").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The provider name (e.g., "OpenAI", "Azure", "Anthropic").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Approximate token limit for this model (input + output).
    /// </summary>
    public int? TokenLimit { get; init; }

    /// <summary>
    /// Optional cost information (e.g., "0.03/1K tokens").
    /// </summary>
    public string? CostInfo { get; init; }

    /// <summary>
    /// Whether the model is currently available in CodebrewRouter.
    /// </summary>
    public bool IsAvailable { get; init; }
}

/// <summary>
/// Result of discovering models from remote CodebrewRouter.
/// </summary>
public record RemoteDiscoveryResult(
    IReadOnlyList<RemoteModelInfo> Models,
    DateTime DiscoveredAtUtc,
    bool IsOnline,
    string? ErrorMessage = null);

/// <summary>
/// Event fired when remote discovery results change.
/// </summary>
public class DiscoveryChanged
{
    /// <summary>
    /// The new discovery result.
    /// </summary>
    public required RemoteDiscoveryResult Result { get; init; }

    /// <summary>
    /// The previous discovery result (if any).
    /// </summary>
    public RemoteDiscoveryResult? PreviousResult { get; init; }

    /// <summary>
    /// Human-readable reason for the change.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// When the change was detected (UTC).
    /// </summary>
    public required DateTime ChangedAtUtc { get; init; }
}
