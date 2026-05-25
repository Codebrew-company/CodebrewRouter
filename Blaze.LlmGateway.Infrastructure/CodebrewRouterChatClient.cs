using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure;

public sealed class CodebrewRouterChatClient(
    IChatClient innerClient,
    ITaskClassifier taskClassifier,
    IPromptCleaner promptCleaner,
    IContextCompactor contextCompactor,
    Blaze.LlmGateway.Infrastructure.TokenCounting.ITokenCounter tokenCounter,
    IOptions<CodebrewRouterOptions> options,
    IOptions<LlmGatewayOptions> gatewayOptions,
    IModelAvailabilityRegistry availabilityRegistry,
    IServiceProvider serviceProvider,
    ILogger<CodebrewRouterChatClient> logger) : DelegatingChatClient(innerClient)
{
    private const string EmptyProviderResponseReason = "Provider completed without emitting a response chunk.";
    private const string EmptyVisibleResponseMessage =
        "I couldn't produce a visible response. Please try again or rephrase.";

    private CodebrewRouterOptions Options => options.Value;
    private LlmGatewayOptions GatewayOptions => gatewayOptions.Value;

    private void Log(object @event, LogLevel? level = null)
        => RouterLog.Write(logger, @event, level);

    private static string ResolveModelName(string providerKey, LlmGatewayOptions gatewayOptions)
    {
        if (Enum.TryParse<RouteDestination>(providerKey, out var dest)
            && OpenCodeGoModels.ModelNames.TryGetValue(dest, out var modelName))
        {
            return modelName;
        }

        return providerKey switch
        {
            "LmStudio"    => gatewayOptions.Providers.LmStudio.Model,
            "OllamaRouter" => gatewayOptions.Providers.OllamaRouter.Model,
            _ => providerKey
        };
    }

    private string ModelName(string providerKey) => ResolveModelName(providerKey, GatewayOptions);

    // ── Non-streaming ─────────────────────────────────────────────────────────

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var activeOptions = ResolveVirtualModelOptions(options);

        Log(new RouterStartEvent(messageList.Count));

        var cleanSw = System.Diagnostics.Stopwatch.StartNew();
        var cleanedMessages = await CleanMessagesAsync(messageList, cancellationToken);
        cleanSw.Stop();
        Log(new RouterCleanEvent(
            messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Length ?? 0,
            cleanedMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Length ?? 0,
            cleanSw.ElapsedMilliseconds));

        var resolveSw = System.Diagnostics.Stopwatch.StartNew();
        var (taskType, providers, tokenCount) = await ResolveAsync(cleanedMessages, activeOptions, cancellationToken);
        resolveSw.Stop();
        var chain = string.Join(", ", providers.Select(ModelName));

        Log(new RouterResolveEvent(
            taskType.ToString(), tokenCount, providers.Length, chain, resolveSw.ElapsedMilliseconds));

        var providerFailures = new List<string>();
        for (var i = 0; i < providers.Length; i++)
        {
            var key = providers[i];
            var model = ModelName(key);
            var client = serviceProvider.GetKeyedService<IChatClient>(key);
            if (client is null)
            {
                Log(new RouterSkipEvent(i + 1, key, model, 0, 0, "not_registered"));
                providerFailures.Add($"{key}: provider is not registered.");
                continue;
            }

            var providerMessages = await PrepareMessagesForProviderAsync(i + 1, key, cleanedMessages, activeOptions, options, cancellationToken);
            if (providerMessages is null)
            {
                providerFailures.Add($"{key}: context is too large for the provider.");
                continue;
            }

            var attemptSw = System.Diagnostics.Stopwatch.StartNew();
            Log(new RouterTryEvent(i + 1, providers.Length, key, model, taskType.ToString()));
            try
            {
                var response = await client.GetResponseAsync(providerMessages, options, cancellationToken);
                attemptSw.Stop();

                Log(new RouterSuccessEvent(
                    i + 1, key, model, taskType.ToString(),
                    response.FinishReason?.ToString(),
                    (int?)response.Usage?.InputTokenCount,
                    (int?)response.Usage?.OutputTokenCount,
                    attemptSw.ElapsedMilliseconds));

                return response;
            }
            catch (Exception ex)
            {
                attemptSw.Stop();
                Log(new RouterFailEvent(i + 1, key, model, ex.Message), LogLevel.Warning);
                providerFailures.Add($"{key}: {ex.GetBaseException().Message}");
            }
        }

        Log(new RouterExhaustedEvent(providers.Length, taskType.ToString(), "LmStudio"), LogLevel.Warning);

        var exhaustedMessage = BuildProviderUnavailableMessage(activeOptions, taskType, providers, providerFailures);
        if (GatewayOptions.OfflineOnly)
        {
            throw new InvalidOperationException(exhaustedMessage);
        }

        var innerMessages = await PrepareMessagesForProviderAsync(providers.Length + 1, "LmStudio", cleanedMessages, activeOptions, options, cancellationToken)
            ?? cleanedMessages;
        return await InnerClient.GetResponseAsync(innerMessages, options, cancellationToken);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var globalSw = System.Diagnostics.Stopwatch.StartNew();
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var activeOptions = ResolveVirtualModelOptions(options);

        Log(new RouterStartEvent(messageList.Count));

        var cleanSw = System.Diagnostics.Stopwatch.StartNew();
        var cleanedMessages = await CleanMessagesAsync(messageList, cancellationToken);
        cleanSw.Stop();
        Log(new RouterCleanEvent(
            messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Length ?? 0,
            cleanedMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Length ?? 0,
            cleanSw.ElapsedMilliseconds));

        var resolveSw = System.Diagnostics.Stopwatch.StartNew();
        var (taskType, providers, tokenCount) = await ResolveAsync(cleanedMessages, activeOptions, cancellationToken);
        resolveSw.Stop();
        var chain = string.Join(", ", providers.Select(ModelName));

        Log(new RouterResolveEvent(
            taskType.ToString(), tokenCount, providers.Length, chain, resolveSw.ElapsedMilliseconds));

        var providerFailures = new List<string>();
        var emptyCompletionFailures = 0;
        for (var i = 0; i < providers.Length; i++)
        {
            var key = providers[i];
            var model = ModelName(key);

            var client = serviceProvider.GetKeyedService<IChatClient>(key);
            if (client is null)
            {
                Log(new RouterSkipEvent(i + 1, key, model, 0, 0, "not_registered"));
                providerFailures.Add($"{key}: provider is not registered.");
                continue;
            }

            var providerMessages = await PrepareMessagesForProviderAsync(i + 1, key, cleanedMessages, activeOptions, options, cancellationToken);
            if (providerMessages is null)
            {
                providerFailures.Add($"{key}: context is too large for the provider.");
                continue;
            }

            Log(new RouterTryEvent(i + 1, providers.Length, key, model, taskType.ToString()));

            var chunkSw = System.Diagnostics.Stopwatch.StartNew();
            var result = await TryGetFirstChunkWithVisibleRecoveryAsync(client, providerMessages, options, cancellationToken);
            chunkSw.Stop();

            Log(new RouterProbeEvent(i + 1, key, model, chunkSw.ElapsedMilliseconds, result.Success));

            if (!result.Success)
            {
                var reason = result.FailureReason ?? "First chunk probe failed";
                Log(new RouterFailEvent(i + 1, key, model, reason), LogLevel.Warning);
                if (result.IsEmptyCompletion)
                {
                    emptyCompletionFailures++;
                }

                providerFailures.Add($"{key}: {reason}");
                continue;
            }

            Log(new RouterSuccessEvent(
                i + 1, key, model, taskType.ToString(), null, null, null, chunkSw.ElapsedMilliseconds));

            yield return result.FirstChunk;

            var chunkCount = 1;
            var enumerator = result.Enumerator!;
            while (true)
            {
                bool hasMore = false;
                bool streamFailed = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception)
                {
                    Log(new RouterMidstreamFailEvent(key, model), LogLevel.Warning);
                    streamFailed = true;
                }

                if (streamFailed || !hasMore)
                {
                    await enumerator.DisposeAsync();
                    if (!streamFailed)
                    {
                        Log(new RouterStreamCompleteEvent(chunkCount, key, model, taskType.ToString(), globalSw.ElapsedMilliseconds));
                    }
                    yield break;
                }

                chunkCount++;
                yield return enumerator.Current;
            }
        }

        Log(new RouterExhaustedEvent(providers.Length, taskType.ToString(), "LmStudio"), LogLevel.Warning);

        var exhaustedMessage = BuildProviderUnavailableMessage(activeOptions, taskType, providers, providerFailures);
        if (GatewayOptions.OfflineOnly)
        {
            if (ShouldReturnEmptyVisibleResponse(providerFailures.Count, emptyCompletionFailures))
            {
                yield return CreateEmptyVisibleResponseUpdate();
                Log(new RouterStreamCompleteEvent(
                    1,
                    "CodebrewRouter",
                    activeOptions.ModelId,
                    taskType.ToString(),
                    globalSw.ElapsedMilliseconds));
                yield break;
            }

            throw new InvalidOperationException(exhaustedMessage);
        }

        var innerMessages = await PrepareMessagesForProviderAsync(providers.Length + 1, "LmStudio", cleanedMessages, activeOptions, options, cancellationToken)
            ?? cleanedMessages;
        var innerResult = await TryGetFirstChunkWithVisibleRecoveryAsync(InnerClient, innerMessages, options, cancellationToken);
        if (!innerResult.Success)
        {
            var innerReason = innerResult.FailureReason ?? "InnerClient probe failed";
            Log(new RouterFailEvent(0, "LmStudio", "InnerClient", innerReason), LogLevel.Error);
            if (innerResult.IsEmptyCompletion)
            {
                yield return CreateEmptyVisibleResponseUpdate();
                Log(new RouterStreamCompleteEvent(
                    1,
                    "CodebrewRouter",
                    activeOptions.ModelId,
                    taskType.ToString(),
                    globalSw.ElapsedMilliseconds));
                yield break;
            }

            throw new InvalidOperationException(
                $"{exhaustedMessage} InnerClient: {innerReason}");
        }

        yield return innerResult.FirstChunk;

        var innerChunkCount = 1;
        var innerEnumerator = innerResult.Enumerator!;
        while (true)
        {
            bool hasMore = false;
            bool streamFailed = false;
            try
            {
                hasMore = await innerEnumerator.MoveNextAsync();
            }
            catch (Exception)
            {
                Log(new RouterMidstreamFailEvent("LmStudio", "InnerClient"), LogLevel.Warning);
                streamFailed = true;
            }

            if (streamFailed || !hasMore)
            {
                await innerEnumerator.DisposeAsync();
                if (!streamFailed)
                {
                    Log(new RouterStreamCompleteEvent(innerChunkCount, "LmStudio", "InnerClient", taskType.ToString(), globalSw.ElapsedMilliseconds));
                }
                yield break;
            }

            innerChunkCount++;
            yield return innerEnumerator.Current;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IList<ChatMessage>> CleanMessagesAsync(
        IList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
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

        string cleaned;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            cleaned = await promptCleaner.CleanAsync(originalText, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Prompt cleaner timed out after 3 seconds; using original prompt");
            cleaned = originalText;
        }

        if (ReferenceEquals(cleaned, originalText) || string.Equals(cleaned, originalText, StringComparison.Ordinal))
            return messages;

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

    private CodebrewRouterOptions ResolveVirtualModelOptions(ChatOptions? requestOptions)
    {
        if (GatewayOptions.FindVirtualModel(requestOptions?.ModelId) is { } profile)
        {
            return profile.ToCodebrewRouterOptions();
        }

        return Options;
    }

    private async Task<(TaskType TaskType, string[] Providers, int TokenCount)> ResolveAsync(
        IEnumerable<ChatMessage> messages,
        CodebrewRouterOptions activeOptions,
        CancellationToken cancellationToken)
    {
        var tokenCount = tokenCounter.CountTokens(messages);
        var taskType = await taskClassifier.ClassifyAsync(messages, cancellationToken);

        var typeKey = taskType.ToString();
        var providers =
            activeOptions.FallbackRules.TryGetValue(typeKey, out var chain) && chain.Length > 0 ? chain
            : activeOptions.FallbackRules.TryGetValue("General", out var general) ? general
            : [];

        var configuredProviders = providers.Where(IsProviderConfigured).ToArray();

        return (taskType, configuredProviders, tokenCount);
    }

    private async Task<IList<ChatMessage>?> PrepareMessagesForProviderAsync(
        int attempt,
        string providerKey,
        IList<ChatMessage> messages,
        CodebrewRouterOptions activeOptions,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        if (!TryGetProviderContextBudget(providerKey, out var contextBudget))
        {
            return messages;
        }

        var currentTokenCount = tokenCounter.CountTokens(messages, contextBudget.ModelId);
        var inputBudget = CalculateInputBudget(contextBudget, options);

        Log(new RouterContextBudgetEvent(
            attempt, providerKey, ModelName(providerKey), currentTokenCount, inputBudget, contextBudget.MaxContextTokens));

        if (currentTokenCount <= inputBudget)
        {
            return messages;
        }

        var compactionRatio = Math.Clamp(activeOptions.ContextCompaction.TargetBudgetRatio, 0.1d, 1.0d);
        var compactionTarget = Math.Max(1, (int)Math.Floor(inputBudget * compactionRatio));
        var compactionResult = await contextCompactor.CompactAsync(messages, compactionTarget, contextBudget.ModelId, cancellationToken);
        if (compactionResult.WasCompacted && compactionResult.CompactedTokenCount <= inputBudget)
        {
            Log(new RouterCompactEvent(attempt, providerKey, compactionResult.OriginalTokenCount, compactionResult.CompactedTokenCount));
            return compactionResult.Messages;
        }

        Log(new RouterSkipEvent(attempt, providerKey, ModelName(providerKey),
            compactionResult.WasCompacted ? compactionResult.CompactedTokenCount : currentTokenCount,
            inputBudget,
            "context_too_large"));

        return null;
    }

    private bool IsProviderConfigured(string providerKey)
    {
        var providers = GatewayOptions.Providers;

        return providerKey switch
        {
            "LmStudio" => HasValue(providers.LmStudio.Endpoint) && HasValue(providers.LmStudio.Model) && availabilityRegistry.IsProviderAvailable("LmStudio"),

            var k when k.StartsWith("OpenCodeGo_", StringComparison.OrdinalIgnoreCase)
                => HasValue(providers.OpenCodeGo.ApiKey) && availabilityRegistry.IsProviderAvailable(k),

            _ => true
        };
    }

    private bool TryGetProviderContextBudget(string providerKey, out ProviderContextBudget budget)
    {
        var providers = GatewayOptions.Providers;

        switch (providerKey)
        {
            case "LmStudio":
                budget = new ProviderContextBudget(
                    providers.LmStudio.Model,
                    providers.LmStudio.MaxContextTokens,
                    providers.LmStudio.ReservedOutputTokens);
                return HasValue(providers.LmStudio.Model) && providers.LmStudio.MaxContextTokens > 0;

            case var k when k.StartsWith("OpenCodeGo_", StringComparison.OrdinalIgnoreCase):
                budget = new ProviderContextBudget(
                    k,
                    providers.OpenCodeGo.MaxContextTokens,
                    providers.OpenCodeGo.ReservedOutputTokens);
                return HasValue(providers.OpenCodeGo.ApiKey) && providers.OpenCodeGo.MaxContextTokens > 0;

            default:
                budget = default;
                return false;
        }
    }

    private static int CalculateInputBudget(ProviderContextBudget budget, ChatOptions? options)
    {
        var reservedOutputTokens = Math.Max(0, options?.MaxOutputTokens ?? budget.ReservedOutputTokens);
        return Math.Max(1, budget.MaxContextTokens - reservedOutputTokens);
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

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
                return FirstChunkResult.EmptyCompletion();
            }

            return new FirstChunkResult(true, enumerator.Current, enumerator);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            return FirstChunkResult.Failed(ex.GetBaseException().Message);
        }
    }

    private static async Task<FirstChunkResult> TryGetFirstChunkWithVisibleRecoveryAsync(
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var result = await TryGetFirstChunkAsync(client, messages, options, ct);
        if (result.Success || !result.IsEmptyCompletion)
        {
            return result;
        }

        var retryMessages = BuildVisibleResponseRetryMessages(messages);
        return await TryGetFirstChunkAsync(client, retryMessages, options, ct);
    }

    private static IList<ChatMessage> BuildVisibleResponseRetryMessages(IList<ChatMessage> messages)
    {
        const string instruction =
            "Your previous generation produced no visible assistant text after hidden reasoning was removed. " +
            "Produce only the final visible answer for the user's latest request. " +
            "Do not include a thinking process, analysis, scratchpad, hidden reasoning, or channel markers.";

        var retryMessages = new List<ChatMessage>(messages.Count + 1);
        var index = 0;
        while (index < messages.Count && messages[index].Role == ChatRole.System)
        {
            retryMessages.Add(messages[index]);
            index++;
        }

        retryMessages.Add(new ChatMessage(ChatRole.System, instruction));

        for (; index < messages.Count; index++)
        {
            retryMessages.Add(messages[index]);
        }

        return retryMessages;
    }

    private string BuildProviderUnavailableMessage(
        CodebrewRouterOptions activeOptions,
        TaskType taskType,
        IReadOnlyCollection<string> providers,
        IReadOnlyCollection<string> providerFailures)
    {
        var modelId = string.IsNullOrWhiteSpace(activeOptions.ModelId) ? "codebrewRouter" : activeOptions.ModelId;
        var providerChain = providers.Count == 0
            ? "no configured providers"
            : string.Join(", ", providers);
        var details = providerFailures.Count == 0
            ? $"Provider chain: {providerChain}."
            : string.Join("; ", providerFailures);

        return $"Model '{modelId}' is currently unavailable: no backing provider succeeded for task {taskType}. {details}";
    }

    private static bool ShouldReturnEmptyVisibleResponse(int providerFailureCount, int emptyCompletionFailures)
        => emptyCompletionFailures > 0 && emptyCompletionFailures == providerFailureCount;

    private static ChatResponseUpdate CreateEmptyVisibleResponseUpdate()
        => new(ChatRole.Assistant, EmptyVisibleResponseMessage)
        {
            FinishReason = ChatFinishReason.Stop
        };

    private sealed record FirstChunkResult(
        bool Success,
        ChatResponseUpdate FirstChunk,
        IAsyncEnumerator<ChatResponseUpdate>? Enumerator,
        string? FailureReason = null,
        bool IsEmptyCompletion = false)
    {
        public static FirstChunkResult EmptyCompletion()
            => new(false, default!, null, EmptyProviderResponseReason, true);

        public static FirstChunkResult Failed(string reason) => new(false, default!, null, reason);
    }

    private readonly record struct ProviderContextBudget(
        string ModelId,
        int MaxContextTokens,
        int ReservedOutputTokens);
}
