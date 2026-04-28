using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure;

/// <summary>
/// Task-aware virtual LLM router registered under the keyed DI key <c>"CodebrewRouter"</c>.
/// When a caller sends <c>model: "codebrewRouter"</c> the <see cref="ModelSelectionResolver"/>
/// resolves to this client, which:
/// <list type="number">
///   <item>Optimizes the last user message via <see cref="IPromptCleaner"/> (gemma4:e4b).</item>
///   <item>Classifies the (cleaned) conversation into a <see cref="TaskType"/>.</item>
///   <item>Looks up the ordered provider fallback chain from <see cref="CodebrewRouterOptions.FallbackRules"/>.</item>
///   <item>Tries each provider in sequence with the cleaned messages; on any exception the next is attempted.</item>
///   <item>Falls back to <see cref="DelegatingChatClient.InnerClient"/> (AzureFoundry) when all providers fail.</item>
/// </list>
/// The cleaner runs once per request; the cleaned message list is shared by the classifier
/// and every downstream provider attempt so the optimization benefit reaches the paid call.
/// </summary>
public sealed class CodebrewRouterChatClient(
    IChatClient innerClient,
    ITaskClassifier taskClassifier,
    IPromptCleaner promptCleaner,
    IOptions<CodebrewRouterOptions> options,
    IOptions<LlmGatewayOptions> gatewayOptions,
    IModelAvailabilityRegistry availabilityRegistry,
    IServiceProvider serviceProvider,
    ILogger<CodebrewRouterChatClient> logger) : DelegatingChatClient(innerClient)
{
    private CodebrewRouterOptions Options => options.Value;
    private LlmGatewayOptions GatewayOptions => gatewayOptions.Value;

    // ── Non-streaming ─────────────────────────────────────────────────────────

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var cleanedMessages = await CleanMessagesAsync(messageList, cancellationToken);
        var (taskType, providers) = await ResolveAsync(cleanedMessages, cancellationToken);

        for (var i = 0; i < providers.Length; i++)
        {
            var key = providers[i];
            var client = serviceProvider.GetKeyedService<IChatClient>(key);
            if (client is null)
            {
                logger.LogDebug("⚠️ codebrewRouter provider '{Key}' not registered — skipping", key);
                continue;
            }

            logger.LogInformation("🎯 codebrewRouter trying {Key} (attempt {Attempt}/{Total}) for {TaskType}",
                key, i + 1, providers.Length, taskType);
            try
            {
                var response = await client.GetResponseAsync(cleanedMessages, options, cancellationToken);
                logger.LogInformation("✅ codebrewRouter succeeded with {Key} for {TaskType}", key, taskType);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "⚠️ codebrewRouter provider {Key} failed: {Message}. Trying next.", key, ex.Message);
            }
        }

        logger.LogWarning("⚠️ codebrewRouter all providers exhausted for {TaskType} — using InnerClient", taskType);
        return await InnerClient.GetResponseAsync(cleanedMessages, options, cancellationToken);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var cleanedMessages = await CleanMessagesAsync(messageList, cancellationToken);
        var (taskType, providers) = await ResolveAsync(cleanedMessages, cancellationToken);

        for (var i = 0; i < providers.Length; i++)
        {
            var key = providers[i];
            var client = serviceProvider.GetKeyedService<IChatClient>(key);
            if (client is null)
            {
                logger.LogDebug("⚠️ codebrewRouter provider '{Key}' not registered — skipping", key);
                continue;
            }

            logger.LogInformation("🎯 codebrewRouter streaming: trying {Key} (attempt {Attempt}/{Total}) for {TaskType}",
                key, i + 1, providers.Length, taskType);

            var result = await TryGetFirstChunkAsync(client, cleanedMessages, options, cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning("⚠️ codebrewRouter streaming provider {Key} failed before first chunk. Trying next.", key);
                continue;
            }

            logger.LogInformation("✅ codebrewRouter streaming succeeded with {Key} for {TaskType}", key, taskType);

            yield return result.FirstChunk;

            var enumerator = result.Enumerator!;
            while (true)
            {
                bool hasMore = false;
                bool streamFailed = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "⚠️ codebrewRouter mid-stream failure from {Key} — ending stream", key);
                    streamFailed = true;
                }

                if (streamFailed || !hasMore)
                {
                    await enumerator.DisposeAsync();
                    yield break;
                }

                yield return enumerator.Current;
            }
        }

        logger.LogWarning("⚠️ codebrewRouter all streaming providers exhausted for {TaskType} — probing InnerClient", taskType);
        var innerResult = await TryGetFirstChunkAsync(InnerClient, cleanedMessages, options, cancellationToken);
        if (!innerResult.Success)
        {
            logger.LogError("❌ codebrewRouter InnerClient also failed for {TaskType} — all providers exhausted", taskType);
            throw new InvalidOperationException(
                $"All streaming providers (including InnerClient fallback) failed for task {taskType}.");
        }

        yield return innerResult.FirstChunk;
        var innerEnumerator = innerResult.Enumerator!;
        while (true)
        {
            bool hasMore = false;
            bool streamFailed = false;
            try
            {
                hasMore = await innerEnumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "⚠️ codebrewRouter InnerClient mid-stream failure for {TaskType} — ending stream", taskType);
                streamFailed = true;
            }

            if (streamFailed || !hasMore)
            {
                await innerEnumerator.DisposeAsync();
                yield break;
            }

            yield return innerEnumerator.Current;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a copy of <paramref name="messages"/> with the last <see cref="ChatRole.User"/>
    /// message replaced by the cleaner-optimized text. If the cleaner returns the original
    /// text (no-op cleaner, short prompt, validation failure, or open circuit) this returns
    /// the input list unchanged.
    /// </summary>
    private async Task<IList<ChatMessage>> CleanMessagesAsync(
        IList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        // Find the last user message — that's the only one we rewrite.
        int lastUserIdx = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                lastUserIdx = i;
                break;
            }
        }

        if (lastUserIdx < 0)
            return messages;

        var lastUser = messages[lastUserIdx];
        var originalText = lastUser.Text;
        if (string.IsNullOrEmpty(originalText))
            return messages;

        var cleaned = await promptCleaner.CleanAsync(originalText, cancellationToken);
        if (ReferenceEquals(cleaned, originalText) || string.Equals(cleaned, originalText, StringComparison.Ordinal))
            return messages;

        // Build a shallow copy with the rewritten message swapped in. Preserve the
        // original ChatRole, AuthorName, and any AdditionalProperties on the user message.
        var rewritten = new ChatMessage(lastUser.Role, cleaned)
        {
            AuthorName = lastUser.AuthorName,
            AdditionalProperties = lastUser.AdditionalProperties,
            MessageId = lastUser.MessageId
        };

        var copy = new List<ChatMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
            copy.Add(i == lastUserIdx ? rewritten : messages[i]);

        return copy;
    }

    private async Task<(TaskType TaskType, string[] Providers)> ResolveAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var taskType = await taskClassifier.ClassifyAsync(messages, cancellationToken);
        logger.LogInformation("🧠 codebrewRouter classified task as {TaskType}", taskType);

        var typeKey = taskType.ToString();
        var providers =
            Options.FallbackRules.TryGetValue(typeKey, out var chain) && chain.Length > 0 ? chain
            : Options.FallbackRules.TryGetValue("General", out var general) ? general
            : [];

        var configuredProviders = providers.Where(IsProviderConfigured).ToArray();

        return (taskType, configuredProviders);
    }

    private bool IsProviderConfigured(string providerKey)
    {
        var providers = GatewayOptions.Providers;

        return providerKey switch
        {
            "AzureFoundry" => HasValue(providers.AzureFoundry.Endpoint) && HasValue(providers.AzureFoundry.Model) && availabilityRegistry.IsProviderAvailable("AzureFoundry"),
            "FoundryLocal" => HasValue(providers.FoundryLocal.Endpoint) && HasValue(providers.FoundryLocal.Model) && availabilityRegistry.IsProviderAvailable("FoundryLocal"),
            "GithubModels" => HasValue(providers.GithubModels.Endpoint) && HasValue(providers.GithubModels.Model) && HasValue(providers.GithubModels.ApiKey) && availabilityRegistry.IsProviderAvailable("GithubModels"),
            "OllamaLocal" => HasValue(providers.OllamaLocal.BaseUrl) && HasValue(providers.OllamaLocal.Model) && availabilityRegistry.IsProviderAvailable("OllamaLocal"),
            _ => true
        };
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Regular async method (NOT an iterator) that tries to obtain the first chunk of a
    /// streaming response. Because this is not an iterator, try/catch works normally.
    /// Returns a success flag, the first chunk, and the still-open enumerator so the
    /// caller can continue streaming without restarting.
    /// </summary>
    private static async Task<FirstChunkResult> TryGetFirstChunkAsync(
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var enumerator = client.GetStreamingResponseAsync(messages, options, ct)
                               .GetAsyncEnumerator(ct);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                await enumerator.DisposeAsync();
                return FirstChunkResult.Failed;
            }

            return new FirstChunkResult(true, enumerator.Current, enumerator);
        }
        catch
        {
            await enumerator.DisposeAsync();
            return FirstChunkResult.Failed;
        }
    }

    private sealed record FirstChunkResult(
        bool Success,
        ChatResponseUpdate FirstChunk,
        IAsyncEnumerator<ChatResponseUpdate>? Enumerator)
    {
        public static readonly FirstChunkResult Failed = new(false, default!, null);
    }
}
