namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

/// <summary>
/// Thrown when a prompt cannot fit in the target model's context window even after compaction.
/// <see cref="LlmRoutingChatClient"/> catches this and attempts to route to a provider with a
/// larger window. If no provider fits, the exception propagates so the API layer can return 413.
/// </summary>
public sealed class ContextOverflowException : Exception
{
    /// <summary>Model ID that was attempted.</summary>
    public string ModelId { get; }

    /// <summary>Token count that could not be reduced further.</summary>
    public int RequiredTokens { get; }

    /// <summary>Input budget (context window minus reserved output tokens) for <see cref="ModelId"/>.</summary>
    public int Budget { get; }

    /// <summary>Provider keys that have already been tried and rejected.</summary>
    public IReadOnlyList<string> AttemptedDestinations { get; }

    public ContextOverflowException(
        string modelId,
        int requiredTokens,
        int budget,
        IReadOnlyList<string> attemptedDestinations)
        : base($"Context overflow for model '{modelId}': {requiredTokens} tokens required but budget is {budget}.")
    {
        ArgumentNullException.ThrowIfNull(attemptedDestinations);
        ModelId = modelId;
        RequiredTokens = requiredTokens;
        Budget = budget;
        AttemptedDestinations = attemptedDestinations;
    }

    /// <summary>
    /// Returns a new exception with <paramref name="destination"/> appended to
    /// <see cref="AttemptedDestinations"/>. Used by the failover loop to track
    /// which providers have been tried.
    /// </summary>
    public ContextOverflowException WithAttempted(string destination) =>
        new(ModelId, RequiredTokens, Budget, [.. AttemptedDestinations, destination]);
}
