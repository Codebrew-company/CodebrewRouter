namespace Blaze.LlmGateway.Infrastructure.PromptCleaning;

/// <summary>
/// Rewrites a single user-message string into a tighter, more token-efficient form
/// while preserving the user's actual intent, constraints, and any verbatim content
/// (code, paths, identifiers, URLs, quoted strings, format requirements).
/// </summary>
/// <remarks>
/// Implementations must be safe to call concurrently. On any failure the implementation
/// must return the original input unchanged rather than throw — callers depend on this.
/// </remarks>
public interface IPromptCleaner
{
    Task<string> CleanAsync(string original, CancellationToken cancellationToken = default);
}
