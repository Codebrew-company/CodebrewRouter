using Blaze.LlmGateway.Core.TaskRouting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.TaskClassification;

/// <summary>
/// Keyword-based <see cref="ITaskClassifier"/> used as the zero-latency fallback when
/// the Ollama-backed classifier is unavailable. Inspects the last user message for
/// task-specific signal words and returns the best matching <see cref="TaskType"/>.
/// Always succeeds — never throws.
/// </summary>
public sealed class KeywordTaskClassifier(ILogger<KeywordTaskClassifier> logger) : ITaskClassifier
{
    // Ordered: more specific patterns before broader ones.
    private static readonly (string[] Keywords, TaskType Type)[] Rules =
    [
        // Speech / audio (check before vision — "transcribe this recording" must not fall through)
        (["transcribe", "transcription", "audio", "speech", "voice note", "voice memo",
          "recording", "spoken", "listen to", "text to speech", "speech to text"], TaskType.Speech),

        // Vision / object detection (check before "research" which contains "analyze")
        (["image", "photo", "picture", "detect", "object in", "identify object",
          "vision", "screenshot", "what is in this", "describe this image", "look at this"], TaskType.VisionObjectDetection),

        // Coding
        (["code", " function ", "implement", "debug", "algorithm", "refactor",
          "unit test", " class ", "interface ", "method ", "bug fix", "compile", "syntax",
          "write a program", "write code", "snippet", "repository"], TaskType.Coding),

        // Reasoning / math / logic
        (["reason", "prove", "proof", "logic", "math", "theorem", "deduce", "infer",
          "step by step", "think through", "calculate", "equation", "formula", "solve"], TaskType.Reasoning),

        // Data analysis
        (["dataset", "csv", "chart", "statistics", "correlation", "regression", "trend",
          "analysis", " sql ", "query", "spreadsheet", "aggregate", "pivot table"], TaskType.DataAnalysis),

        // Research
        (["research", "comprehensive", "survey", "literature", "overview", "compare",
          "what are the", "explain in detail", "history of", "background on",
          "summarize", "summarise", "deep dive"], TaskType.Research),

        // Creative
        (["write a story", "short story", "poem", "creative", "fiction", "essay",
          "narrative", "blog post", "script", "dialogue", "write about"], TaskType.Creative),
    ];

    public Task<TaskType> ClassifyAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var lastUserMessage = messages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.Text
            ?.ToLowerInvariant()
            ?? "";

        foreach (var (keywords, taskType) in Rules)
        {
            if (keywords.Any(kw => lastUserMessage.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogDebug("Keyword classifier matched TaskType={TaskType} from message preview: '{Preview}'",
                    taskType, lastUserMessage.Length > 60 ? lastUserMessage[..60] + "…" : lastUserMessage);
                return Task.FromResult(taskType);
            }
        }

        logger.LogDebug("Keyword classifier found no match — defaulting to General");
        return Task.FromResult(TaskType.General);
    }
}
