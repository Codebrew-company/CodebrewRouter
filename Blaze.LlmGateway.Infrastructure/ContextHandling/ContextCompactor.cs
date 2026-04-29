using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

/// <summary>
/// Reduces oversized chat histories by preserving system prompts and the most recent
/// turns verbatim, while optionally summarizing older non-system turns via OllamaLocal.
/// </summary>
public sealed class ContextCompactor(
    IChatClient? summarizerClient,
    ITokenCounter tokenCounter,
    IOptions<ContextCompactionOptions> options,
    ILogger<ContextCompactor> logger) : IContextCompactor
{
    private const string DefaultSystemPrompt = """
        You compact earlier chat turns into a concise factual summary for another LLM.

        STRICT RULES:
        - Output only the summary text. No markdown, headings, or commentary.
        - Preserve requirements, constraints, decisions, file paths, identifiers, URLs, errors, tool results, and unresolved questions.
        - Do not invent new information.
        - Focus on facts needed to continue the task after older turns are removed.
        - Keep the summary shorter than the source conversation.
        """;

    private readonly ContextCompactionOptions _options = options.Value;

    public async Task<ContextCompactionResult> CompactAsync(
        IList<ChatMessage> messages,
        int targetTokenCount,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var originalTokenCount = tokenCounter.CountTokens(messages, modelId);
        if (!_options.Enabled || originalTokenCount <= targetTokenCount || messages.Count < _options.MinMessagesToCompact)
        {
            return new ContextCompactionResult(messages, originalTokenCount, originalTokenCount, false, "skipped");
        }

        var nonSystemIndexes = messages
            .Select((message, index) => (message, index))
            .Where(pair => pair.message.Role != ChatRole.System)
            .Select(pair => pair.index)
            .ToArray();

        var preserveCount = Math.Clamp(_options.PreserveMostRecentMessages, 1, nonSystemIndexes.Length);
        var skippedIndexes = nonSystemIndexes.Take(Math.Max(0, nonSystemIndexes.Length - preserveCount)).ToHashSet();
        if (skippedIndexes.Count == 0)
        {
            return new ContextCompactionResult(messages, originalTokenCount, originalTokenCount, false, "skipped");
        }

        var skippedMessages = skippedIndexes
            .OrderBy(index => index)
            .Select(index => messages[index])
            .ToArray();

        var summary = await TrySummarizeAsync(skippedMessages, cancellationToken);
        var candidate = BuildCandidate(messages, skippedIndexes, summary);
        candidate = TrimToBudget(candidate, targetTokenCount, modelId);

        var compactedTokenCount = tokenCounter.CountTokens(candidate, modelId);
        var wasCompacted = compactedTokenCount < originalTokenCount;

        if (wasCompacted)
        {
            logger.LogInformation(
                "Context compacted {OriginalTokens} -> {CompactedTokens} tokens using {Strategy}",
                originalTokenCount,
                compactedTokenCount,
                string.IsNullOrWhiteSpace(summary) ? "prune" : "summary+prune");
        }

        return new ContextCompactionResult(
            candidate,
            originalTokenCount,
            compactedTokenCount,
            wasCompacted,
            string.IsNullOrWhiteSpace(summary) ? "prune" : "summary+prune");
    }

    private async Task<string?> TrySummarizeAsync(
        IReadOnlyList<ChatMessage> skippedMessages,
        CancellationToken cancellationToken)
    {
        if (summarizerClient is null || skippedMessages.Count == 0)
        {
            return null;
        }

        var source = string.Join(
            "\n",
            skippedMessages.Select(message => $"[{message.Role}] {message.Text}".TrimEnd()));

        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            var response = await summarizerClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, string.IsNullOrWhiteSpace(_options.SystemPrompt) ? DefaultSystemPrompt : _options.SystemPrompt),
                    new ChatMessage(ChatRole.User, source)
                ],
                new ChatOptions
                {
                    MaxOutputTokens = _options.SummaryMaxOutputTokens,
                    Temperature = _options.SummaryTemperature
                },
                cancellationToken);

            var summary = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(summary) || summary.Length >= source.Length
                ? null
                : summary;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Context summarization failed; falling back to pruning only");
            return null;
        }
    }

    private IList<ChatMessage> BuildCandidate(
        IList<ChatMessage> messages,
        ISet<int> skippedIndexes,
        string? summary)
    {
        var firstSkippedIndex = skippedIndexes.Min();
        var candidate = new List<ChatMessage>(messages.Count - skippedIndexes.Count + (string.IsNullOrWhiteSpace(summary) ? 0 : 1));
        var summaryInserted = false;

        for (var i = 0; i < messages.Count; i++)
        {
            if (skippedIndexes.Contains(i))
            {
                if (!summaryInserted && i == firstSkippedIndex && !string.IsNullOrWhiteSpace(summary))
                {
                    candidate.Add(new ChatMessage(ChatRole.System, $"Conversation summary of earlier turns:\n{summary}")
                    {
                        AuthorName = "context-compactor"
                    });
                    summaryInserted = true;
                }

                continue;
            }

            candidate.Add(messages[i]);
        }

        return candidate;
    }

    private IList<ChatMessage> TrimToBudget(
        IList<ChatMessage> candidate,
        int targetTokenCount,
        string? modelId)
    {
        var working = candidate.ToList();
        var currentCount = tokenCounter.CountTokens(working, modelId);
        while (currentCount > targetTokenCount)
        {
            var removableIndex = FindRemovableIndex(working);
            if (removableIndex < 0)
            {
                break;
            }

            working.RemoveAt(removableIndex);
            currentCount = tokenCounter.CountTokens(working, modelId);
        }

        return working;
    }

    private static int FindRemovableIndex(IList<ChatMessage> messages)
    {
        var lastUserIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                lastUserIndex = i;
                break;
            }
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (i == lastUserIndex)
            {
                continue;
            }

            if (messages[i].Role != ChatRole.System)
            {
                return i;
            }
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].AuthorName, "context-compactor", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
