using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.TaskRouting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.TaskClassification;

/// <summary>
/// <see cref="ITaskClassifier"/> that delegates classification to the cached OllamaRouter
/// client with a tight token budget. Falls back to <see cref="ClassifyByKeyword"/>
/// on any failure (Ollama unavailable, timeout, unrecognised response).
/// </summary>
public sealed class OllamaTaskClassifier : ITaskClassifier
{
    private readonly IChatClient _cachedRouterClient;  // Reused for all requests
    private readonly TaskClassificationOptions _options;
    private readonly ILogger<OllamaTaskClassifier> _logger;
    private readonly TimeSpan _cooldown;

    private DateTimeOffset? _circuitOpenedAt;

    public OllamaTaskClassifier(
        IChatClient cachedRouterClient,  // Injected cached client from DI ("OllamaRouter" keyed)
        IOptions<TaskClassificationOptions> options,
        ILogger<OllamaTaskClassifier> logger)
    {
        _cachedRouterClient = cachedRouterClient;
        _options = options.Value;
        _logger = logger;
        _cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));
    }

    public async Task<TaskType> ClassifyAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();

        if (messageList.Count == 0)
            return TaskType.General;

        if (_circuitOpenedAt is { } openedAt && DateTimeOffset.UtcNow - openedAt < _cooldown)
        {
            _logger.LogWarning("Circuit breaker open for task classifier; using keyword fallback");
            return ClassifyByKeyword(messageList);
        }

        try
        {
            var systemPrompt = @"Classify this task into ONE of: Coding, Reasoning, VisionObjectDetection, Speech, Research, Creative, DataAnalysis, General.
Respond with ONLY the task type, nothing else.";

            var classifyMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, systemPrompt)
            };
            classifyMessages.AddRange(messageList);

            var opts = new ChatOptions { Temperature = 0, MaxOutputTokens = 50 };

            // USE CACHED CLIENT (no new creation)
            var response = await _cachedRouterClient.GetResponseAsync(classifyMessages, opts, cancellationToken);
            var classification = response.Text?.Trim() ?? "General";

            var taskType = ParseTaskType(classification);
            _logger.LogInformation("🎯 Task classified as: {TaskType}", taskType);
            return taskType;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task classifier timed out; using keyword fallback");
            return ClassifyByKeyword(messageList);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task classifier failed; opening circuit and using keyword fallback");
            _circuitOpenedAt = DateTimeOffset.UtcNow;
            return ClassifyByKeyword(messageList);
        }
    }

    private TaskType ParseTaskType(string text)
    {
        return text.Trim().ToLowerInvariant() switch
        {
            "coding" => TaskType.Coding,
            "reasoning" => TaskType.Reasoning,
            "visionobjectdetection" => TaskType.VisionObjectDetection,
            "speech" => TaskType.Speech,
            "research" => TaskType.Research,
            "creative" => TaskType.Creative,
            "dataanalysis" => TaskType.DataAnalysis,
            _ => TaskType.General
        };
    }

    private TaskType ClassifyByKeyword(IList<ChatMessage> messages)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var lower = lastUserMessage.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("function") || lower.Contains("bug"))
            return TaskType.Coding;
        if (lower.Contains("image") || lower.Contains("vision") || lower.Contains("screenshot"))
            return TaskType.VisionObjectDetection;
        if (lower.Contains("audio") || lower.Contains("speech") || lower.Contains("transcribe") || lower.Contains("voice"))
            return TaskType.Speech;
        if (lower.Contains("research") || lower.Contains("paper") || lower.Contains("study"))
            return TaskType.Research;
        if (lower.Contains("creative") || lower.Contains("story") || lower.Contains("write"))
            return TaskType.Creative;
        if (lower.Contains("data") || lower.Contains("analyze") || lower.Contains("chart"))
            return TaskType.DataAnalysis;

        return TaskType.General;
    }
}
