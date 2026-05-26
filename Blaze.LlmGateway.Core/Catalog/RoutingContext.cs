namespace Blaze.LlmGateway.Core.Catalog;

/// <summary>
/// Contextual information about the current request used by routing strategies
/// to make informed deployment selections.
/// </summary>
/// <param name="ModelId">The resolved virtual model ID or raw model name from the request.</param>
/// <param name="EstimatedInputTokens">Estimated token count of the current input prompt.</param>
/// <param name="StreamingRequested">Whether the client requested a streaming response.</param>
/// <param name="ToolsRequested">Whether the request includes tool/function definitions.</param>
/// <param name="VisionRequested">Whether the request includes image inputs.</param>
/// <param name="CancellationToken">Cancellation token for the current request.</param>
public sealed record RoutingContext(
    string ModelId,
    int EstimatedInputTokens,
    bool StreamingRequested,
    bool ToolsRequested,
    bool VisionRequested,
    CancellationToken CancellationToken);
