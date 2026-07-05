using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure;

/// <summary>
/// The <c>fusion</c> virtual model: a MoA-Lite mixture-of-agents client. Fans one prompt out to
/// several proposer models in parallel, then synthesizes their answers with a single aggregator
/// model (the Together "Mixture-of-Agents" pattern, one proposer layer + one aggregation pass).
/// </summary>
/// <remarks>
/// <para>Inner client is the <c>CodebrewRouter</c> keyed client. Every bypass or fallback path
/// (tools present, JSON mode, too few proposers, zero proposer successes, or fusion disabled)
/// delegates to it, reusing the entire clean → classify → per-provider → failover pipeline.</para>
/// <para>Only the aggregator call is streamed; proposers run non-streaming and are collected with
/// a K-of-N soft deadline so one slow local model never dominates latency.</para>
/// </remarks>
public sealed class FusionChatClient(
    IChatClient innerClient,
    ITaskClassifier taskClassifier,
    IOptions<FusionOptions> options,
    IOptions<LlmGatewayOptions> gatewayOptions,
    IServiceProvider serviceProvider,
    ILogger<FusionChatClient> logger) : DelegatingChatClient(innerClient)
{
    // Together MoA aggregator prompt (verbatim from togethercomputer/MoA utils.py).
    private const string AggregatorSystemPrompt =
        "You have been provided with a set of responses from various open-source models to the " +
        "latest user query. Your task is to synthesize these responses into a single, high-quality " +
        "response. It is crucial to critically evaluate the information provided in these responses, " +
        "recognizing that some of it may be biased or incorrect. Your response should not simply " +
        "replicate the given answers but should offer a refined, accurate, and comprehensive reply " +
        "to the instruction. Ensure your response is well-structured, coherent, and adheres to the " +
        "highest standards of accuracy and reliability.";

    private FusionOptions Options => options.Value;
    private LlmGatewayOptions GatewayOptions => gatewayOptions.Value;
    private bool Verbose => GatewayOptions.VerboseRouteLogging;

    private void Log(object @event, LogLevel? level = null) => RouterLog.Write(logger, @event, level);

    private string ModelName(string providerKey)
        => Enum.TryParse<RouteDestination>(providerKey, out var dest)
           && OpenCodeGoModels.ModelNames.TryGetValue(dest, out var modelName)
            ? modelName
            : providerKey;

    // ── Non-streaming ──────────────────────────────────────────────────────────

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();

        var plan = await PlanAsync(messageList, options, cancellationToken);
        if (!plan.ShouldFuse)
        {
            return await base.GetResponseAsync(messageList, options, cancellationToken);
        }

        var proposals = await RunProposersAsync(plan.Proposers, messageList, options, cancellationToken);

        // A caller cancel surfaces as failed proposals; propagate it instead of re-entering the router.
        cancellationToken.ThrowIfCancellationRequested();

        if (proposals.Count == 0)
        {
            Log(new RouterFusionResultEvent("CodebrewRouter", "no proposer succeeded; delegating to router"), LogLevel.Warning);
            return await base.GetResponseAsync(messageList, options, cancellationToken);
        }

        if (proposals.Count == 1)
        {
            Log(new RouterFusionResultEvent(ModelName(proposals[0].Key), "single proposer; aggregation skipped"));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, proposals[0].Text))
            {
                ModelId = "fusion",
                Usage = SumUsage(proposals.Select(p => p.Usage))
            };
        }

        var aggregatorMessages = BuildAggregatorMessages(messageList, proposals, plan.Task);
        var aggregatorOptions = BuildAggregatorOptions(options);

        foreach (var aggKey in plan.Aggregators)
        {
            var client = serviceProvider.GetKeyedService<IChatClient>(aggKey);
            if (client is null)
            {
                continue;
            }

            try
            {
                var response = await client.GetResponseAsync(aggregatorMessages, aggregatorOptions, cancellationToken);
                Log(new RouterFusionResultEvent(ModelName(aggKey), $"aggregated {proposals.Count} proposals"));
                response.ModelId = "fusion";
                response.Usage = SumUsage(proposals.Select(p => p.Usage).Append(response.Usage));
                return response;
            }
            catch (Exception ex)
            {
                Log(new RouterFailEvent(0, aggKey, ModelName(aggKey), ex.GetBaseException().Message), LogLevel.Warning);
            }
        }

        // Every aggregator failed — surface the best single proposal rather than error out.
        Log(new RouterFusionResultEvent(ModelName(proposals[0].Key), "all aggregators failed; returning first proposal"), LogLevel.Warning);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, proposals[0].Text))
        {
            ModelId = "fusion",
            Usage = SumUsage(proposals.Select(p => p.Usage))
        };
    }

    // ── Streaming ──────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();

        var plan = await PlanAsync(messageList, options, cancellationToken);
        if (!plan.ShouldFuse)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        // Fan out in the background; emit keep-alive pings so clients/proxies don't drop the
        // connection during the silent proposer phase.
        var fanoutTask = RunProposersAsync(plan.Proposers, messageList, options, cancellationToken);
        while (!fanoutTask.IsCompleted)
        {
            var winner = await Task.WhenAny(fanoutTask, Task.Delay(KeepAliveInterval, cancellationToken));
            if (winner != fanoutTask)
            {
                // ponytail: empty-delta keep-alive. Upgrade path: SSE `: ping` comment frames at the endpoint.
                yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty);
            }
        }

        var proposals = await fanoutTask;

        // A caller cancel surfaces as failed proposals; propagate it instead of re-entering the router.
        cancellationToken.ThrowIfCancellationRequested();

        if (proposals.Count == 0)
        {
            Log(new RouterFusionResultEvent("CodebrewRouter", "no proposer succeeded; delegating to router"), LogLevel.Warning);
            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        if (proposals.Count == 1)
        {
            Log(new RouterFusionResultEvent(ModelName(proposals[0].Key), "single proposer; aggregation skipped"));
            yield return new ChatResponseUpdate(ChatRole.Assistant, proposals[0].Text) { ModelId = "fusion" };
            yield return UsageUpdate(SumUsage(proposals.Select(p => p.Usage)));
            yield break;
        }

        var aggregatorMessages = BuildAggregatorMessages(messageList, proposals, plan.Task);
        var aggregatorOptions = BuildAggregatorOptions(options);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        string? chosenAggregator = null;
        foreach (var aggKey in plan.Aggregators)
        {
            var client = serviceProvider.GetKeyedService<IChatClient>(aggKey);
            if (client is null)
            {
                continue;
            }

            var probe = await TryGetFirstChunkAsync(client, aggregatorMessages, aggregatorOptions, cancellationToken);
            if (probe.Success)
            {
                chosenAggregator = aggKey;
                Log(new RouterFusionResultEvent(ModelName(aggKey), $"aggregated {proposals.Count} proposals (streaming)"));
                probe.FirstChunk.ModelId = "fusion";
                yield return probe.FirstChunk;
                enumerator = probe.Enumerator;
                break;
            }

            Log(new RouterFailEvent(0, aggKey, ModelName(aggKey), probe.FailureReason ?? "aggregator probe failed"), LogLevel.Warning);
        }

        if (enumerator is null)
        {
            // Every aggregator failed before its first token — fall back to the best proposal.
            Log(new RouterFusionResultEvent(ModelName(proposals[0].Key), "all aggregators failed; returning first proposal"), LogLevel.Warning);
            yield return new ChatResponseUpdate(ChatRole.Assistant, proposals[0].Text) { ModelId = "fusion" };
            yield return UsageUpdate(SumUsage(proposals.Select(p => p.Usage)));
            yield break;
        }

        // try/finally guarantees the aggregator enumerator is disposed on every exit path — natural
        // completion, mid-stream throw, and (critically) when the SSE consumer abandons the fusion
        // stream while it is parked at `yield return`.
        try
        {
            while (true)
            {
                bool hasMore = false;
                var failed = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception)
                {
                    Log(new RouterMidstreamFailEvent(chosenAggregator!, ModelName(chosenAggregator!)), LogLevel.Warning);
                    failed = true;
                }

                if (failed || !hasMore)
                {
                    break;
                }

                var update = enumerator.Current;
                update.ModelId = "fusion";
                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // ponytail: proposer usage emitted as a trailing usage chunk; the aggregator's own usage
        // streams through its updates. Total across the request = sum of both usage contents.
        yield return UsageUpdate(SumUsage(proposals.Select(p => p.Usage)));
    }

    // ── Planning ───────────────────────────────────────────────────────────────

    private sealed record FusionPlan(bool ShouldFuse, TaskType Task, IReadOnlyList<string> Proposers, IReadOnlyList<string> Aggregators);

    private async Task<FusionPlan> PlanAsync(IList<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var opts = Options;

        // Bypass to the CodebrewRouter pipeline (offline-safe, ADR-0008 default-deny) when fusion is
        // off or offline-only mode is active, and when the request cannot be fused: tool-calls and
        // JSON mode cannot be synthesized across divergent proposals.
        if (!opts.Enabled
            || GatewayOptions.OfflineOnly
            || (options?.Tools is { Count: > 0 })
            || options?.ResponseFormat is ChatResponseFormatJson)
        {
            return new FusionPlan(false, TaskType.General, [], []);
        }

        var taskType = await taskClassifier.ClassifyAsync(messages, ct);
        taskType = TaskClassificationHelper.ReclassifyForMedia(taskType, messages);
        var typeKey = taskType.ToString();

        var proposerChain =
            opts.ProposerSetsByTaskClass.TryGetValue(typeKey, out var set) && set.Length > 0 ? set
            : opts.ProposerSetsByTaskClass.TryGetValue("General", out var general) ? general
            : [];

        var proposers = proposerChain
            .Where(key => serviceProvider.GetKeyedService<IChatClient>(key) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(opts.MaxProposers)
            .ToArray();

        if (proposers.Length < opts.MinProposers)
        {
            if (Verbose)
            {
                Log(new RouterSelectEvent("fusion", typeKey, $"bypass: only {proposers.Length} proposer(s) available"));
            }
            return new FusionPlan(false, taskType, [], []);
        }

        var aggregators =
            (opts.AggregatorByTaskClass.TryGetValue(typeKey, out var aggSet) && aggSet.Length > 0 ? aggSet
             : opts.AggregatorByTaskClass.TryGetValue("General", out var aggGeneral) && aggGeneral.Length > 0 ? aggGeneral
             : [proposers[0]])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (Verbose)
        {
            Log(new RouterSelectEvent("fusion", typeKey, "fusion"));
            Log(new RouterFusionEvent(proposers, aggregators.Length > 0 ? ModelName(aggregators[0]) : "auto"));
        }

        return new FusionPlan(true, taskType, proposers, aggregators);
    }

    // ── Proposer fan-out (K-of-N soft deadline) ─────────────────────────────────

    private sealed record Proposal(string Key, bool Success, string Text, UsageDetails? Usage);

    private async Task<List<Proposal>> RunProposersAsync(
        IReadOnlyList<string> proposerKeys,
        IList<ChatMessage> messages,
        ChatOptions? callerOptions,
        CancellationToken ct)
    {
        var opts = Options;
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hardCts.CancelAfter(TimeSpan.FromSeconds(opts.ProposerTimeoutSeconds));

        var proposerOptions = BuildProposerOptions(callerOptions);
        var remaining = proposerKeys
            .Select(key => RunOneProposerAsync(key, messages, proposerOptions, hardCts.Token))
            .ToList();

        var results = new List<Proposal>();
        var successes = 0;
        var gracePosted = false;

        // Separate source for the straggler-grace timer so we can stop it deterministically when
        // the loop drains — otherwise a still-pending timer could fire hardCts.Cancel() after the
        // method's `using` disposes it (unobserved ObjectDisposedException on a background thread).
        using var graceTimerCts = new CancellationTokenSource();
        try
        {
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining);
                remaining.Remove(done);

                var proposal = await done; // RunOneProposerAsync never throws
                if (proposal.Success)
                {
                    results.Add(proposal);
                    successes++;
                }

                // Once we have enough answers, give stragglers a short grace window then cancel them.
                if (!gracePosted && successes >= opts.MinProposers && remaining.Count > 0)
                {
                    gracePosted = true;
                    _ = Task.Delay(TimeSpan.FromSeconds(opts.StragglerGraceSeconds), graceTimerCts.Token)
                        .ContinueWith(
                            _ => { try { hardCts.Cancel(); } catch (ObjectDisposedException) { } },
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnRanToCompletion,
                            TaskScheduler.Default);
                }
            }
        }
        finally
        {
            graceTimerCts.Cancel(); // stop a pending grace timer so its continuation never runs
        }

        return results;
    }

    private async Task<Proposal> RunOneProposerAsync(
        string key,
        IList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        var client = serviceProvider.GetKeyedService<IChatClient>(key);
        if (client is null)
        {
            return new Proposal(key, false, string.Empty, null);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, options, ct);
            sw.Stop();
            var text = response.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                Log(new RouterSkipEvent(0, key, ModelName(key), 0, 0, "empty_proposal"));
                return new Proposal(key, false, string.Empty, response.Usage);
            }

            Log(new RouterSuccessEvent(0, key, ModelName(key), "fusion-proposer", response.FinishReason?.ToString(),
                (int?)response.Usage?.InputTokenCount, (int?)response.Usage?.OutputTokenCount, sw.ElapsedMilliseconds));
            return new Proposal(key, true, text, response.Usage);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log(new RouterSkipEvent(0, key, ModelName(key), 0, 0, ex.GetBaseException().Message));
            return new Proposal(key, false, string.Empty, null);
        }
    }

    // ── Aggregation message assembly ────────────────────────────────────────────

    private IList<ChatMessage> BuildAggregatorMessages(
        IList<ChatMessage> original,
        IReadOnlyList<Proposal> proposals,
        TaskType taskType)
    {
        var maxRefChars = Options.MaxReferenceChars;

        // Per-request shuffle (anti positional-bias). Stable within a process run; the seed uses
        // System.HashCode so order is not reproducible across restarts — which the design does not need.
        var references = proposals.Select(p => p.Text).ToArray();
        Shuffle(references, ShuffleSeed(original, taskType));

        var block = new StringBuilder();
        block.Append(AggregatorSystemPrompt);
        block.Append("\n\nResponses from models:\n");
        for (var i = 0; i < references.Length; i++)
        {
            var reference = references[i];
            if (reference.Length > maxRefChars)
            {
                // Surrogate-safe cut: never split a surrogate pair (would emit an invalid lone unit).
                var cut = maxRefChars;
                if (char.IsHighSurrogate(reference[cut - 1]))
                {
                    cut--;
                }
                reference = reference[..cut];
            }
            block.Append(i + 1).Append(". ").Append(reference).Append('\n');
        }

        var referenceBlock = block.ToString();
        var result = new List<ChatMessage>(original.Count + 1);

        // Together behavior: append the reference block to the caller's system message (preserving
        // its constraints) or prepend a new system message when none exists.
        var index = 0;
        if (original.Count > 0 && original[0].Role == ChatRole.System)
        {
            var existing = original[0].Text ?? string.Empty;
            result.Add(new ChatMessage(ChatRole.System, existing + "\n\n" + referenceBlock));
            index = 1;
        }
        else
        {
            result.Add(new ChatMessage(ChatRole.System, referenceBlock));
        }

        for (; index < original.Count; index++)
        {
            result.Add(original[index]);
        }

        return result;
    }

    private ChatOptions BuildProposerOptions(ChatOptions? caller)
    {
        var proposerOptions = caller?.Clone() ?? new ChatOptions();
        proposerOptions.Temperature ??= Options.ProposerTemperature;
        proposerOptions.MaxOutputTokens = Options.ProposerMaxOutputTokens;
        proposerOptions.Tools = null; // proposers answer in prose; tools bypass fusion entirely
        return proposerOptions;
    }

    private ChatOptions BuildAggregatorOptions(ChatOptions? caller)
    {
        var aggregatorOptions = caller?.Clone() ?? new ChatOptions();
        aggregatorOptions.Temperature = Options.AggregatorTemperature;
        aggregatorOptions.Tools = null;
        return aggregatorOptions;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    private static ChatResponseUpdate UsageUpdate(UsageDetails usage)
        => new() { Contents = [new UsageContent(usage)], ModelId = "fusion" };

    private static UsageDetails SumUsage(IEnumerable<UsageDetails?> usages)
    {
        long input = 0, output = 0, total = 0;
        foreach (var usage in usages)
        {
            if (usage is null)
            {
                continue;
            }
            var i = usage.InputTokenCount ?? 0;
            var o = usage.OutputTokenCount ?? 0;
            input += i;
            output += o;
            total += usage.TotalTokenCount ?? (i + o);
        }
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total };
    }

    private static int ShuffleSeed(IList<ChatMessage> messages, TaskType taskType)
    {
        var hash = new HashCode();
        hash.Add((int)taskType);
        hash.Add(messages.Count);
        hash.Add(messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static void Shuffle(string[] items, int seed)
    {
        var rng = new Random(seed);
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static async Task<FirstChunkResult> TryGetFirstChunkAsync(
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var enumerator = client.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                await enumerator.DisposeAsync();
                return FirstChunkResult.Failed("aggregator produced no chunks");
            }
            return new FirstChunkResult(true, enumerator.Current, enumerator, null);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            return FirstChunkResult.Failed(ex.GetBaseException().Message);
        }
    }

    private sealed record FirstChunkResult(
        bool Success,
        ChatResponseUpdate FirstChunk,
        IAsyncEnumerator<ChatResponseUpdate>? Enumerator,
        string? FailureReason)
    {
        public static FirstChunkResult Failed(string reason) => new(false, default!, null, reason);
    }
}
