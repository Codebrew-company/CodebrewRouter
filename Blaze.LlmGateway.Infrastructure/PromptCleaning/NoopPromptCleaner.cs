namespace Blaze.LlmGateway.Infrastructure.PromptCleaning;

/// <summary>
/// <see cref="IPromptCleaner"/> that returns its input unchanged. Registered when
/// the prompt-cleanup feature is disabled or no Ollama-class router model is available.
/// </summary>
public sealed class NoopPromptCleaner : IPromptCleaner
{
    public Task<string> CleanAsync(string original, CancellationToken cancellationToken = default)
        => Task.FromResult(original);
}
