using Blaze.LlmGateway.Core.TaskRouting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.TaskClassification;

/// <summary>
/// <see cref="ITaskClassifier"/> that delegates classification to the local OllamaLocal
/// "router" model with a tight token budget. Falls back to <see cref="KeywordTaskClassifier"/>
/// on any failure (Ollama unavailable, timeout, unrecognised response).
/// </summary>
public sealed class OllamaTaskClassifier(
    IChatClient routerClient,
    KeywordTaskClassifier fallback,
    ILogger<OllamaTaskClassifier> logger) : ITaskClassifier
{
    private static readonly string[] ValidTaskTypes = Enum.GetNames<TaskType>();

    // Circuit breaker: once Ollama fails (404/connection refused/etc.), stop trying
    // until the cooldown elapses. Eliminates repeat 404s on every request.
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _circuitOpenedAt;

    private static readonly string SystemPrompt = $"""
        You are a task classifier. Based on the user's message, classify the task into exactly one of these categories.
        Respond with ONLY the single category name (no punctuation, no explanation):
        {string.Join(", ", Enum.GetNames<TaskType>())}

        Classification guide:
        - Reasoning: math, proofs, logic, deduction, multi-step analysis
        - Coding: code generation, debugging, refactoring, programming tasks
        - Research: deep research, literature survey, comprehensive topic overview
        - VisionObjectDetection: image description, object detection, visual analysis
        - Creative: stories, poems, essays, fiction, blog posts, creative writing
        - DataAnalysis: data/CSV/SQL analysis, statistics, charts, trends
        - General: anything else or ambiguous
        """;

    public async Task<TaskType> ClassifyAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        // Circuit-breaker fast-path: if Ollama recently failed, skip the call entirely.
        if (_circuitOpenedAt is { } openedAt && DateTimeOffset.UtcNow - openedAt < CooldownDuration)
        {
            return await fallback.ClassifyAsync(messages, cancellationToken);
        }

        try
        {
            var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lastUserMessage))
            {
                logger.LogDebug("OllamaTaskClassifier: empty user message — delegating to keyword fallback");
                return await fallback.ClassifyAsync(messages, cancellationToken);
            }

            var classifyMessages = new[]
            {
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, lastUserMessage)
            };

            var opts = new ChatOptions { MaxOutputTokens = 5, Temperature = 0f };
            var response = await routerClient.GetResponseAsync(classifyMessages, opts, cancellationToken);
            var responseText = response.Text?.Trim() ?? "";

            // Tier 1: exact match
            if (Enum.TryParse<TaskType>(responseText, ignoreCase: true, out var exact))
            {
                logger.LogInformation("OllamaTaskClassifier exact match → {TaskType}", exact);
                return exact;
            }

            // Tier 2: substring match
            var partial = ValidTaskTypes.FirstOrDefault(t =>
                responseText.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (partial is not null && Enum.TryParse<TaskType>(partial, out var matched))
            {
                logger.LogInformation("OllamaTaskClassifier partial match → {TaskType} (response: '{Response}')", matched, responseText);
                return matched;
            }

            logger.LogWarning("OllamaTaskClassifier unrecognised response '{Response}' — falling back to keyword classifier", responseText);
        }
        catch (Exception ex)
        {
            // Open the circuit so we don't keep hitting an unavailable Ollama. Log once at warning,
            // subsequent skips will be silent until the cooldown elapses.
            if (_circuitOpenedAt is null)
            {
                logger.LogWarning(ex, "OllamaTaskClassifier call failed — opening circuit for {Cooldown}; falling back to keyword classifier", CooldownDuration);
            }
            _circuitOpenedAt = DateTimeOffset.UtcNow;
        }

        return await fallback.ClassifyAsync(messages, cancellationToken);
    }
}
