using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.OutputSavers;

/// <summary>
/// P4.1 output token savers (9router Caveman/Ponytail parity). When enabled, appends
/// a terse-output (Caveman) and/or YAGNI-codegen (Ponytail) system prompt to every
/// request. Reads options per request so dashboard/config toggles apply live.
/// </summary>
public sealed class OutputSaverChatClient(
    IChatClient innerClient,
    IOptionsMonitor<LlmGatewayOptions> options,
    ILogger<OutputSaverChatClient> logger) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
        => InnerClient.GetResponseAsync(Apply(chatMessages), chatOptions, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
        => InnerClient.GetStreamingResponseAsync(Apply(chatMessages), chatOptions, cancellationToken);

    private IEnumerable<ChatMessage> Apply(IEnumerable<ChatMessage> messages)
    {
        var savers = options.CurrentValue.OutputSavers;
        if (!savers.Caveman.Enabled && !savers.Ponytail.Enabled)
        {
            return messages;
        }

        var list = messages.ToList();
        if (savers.Caveman.Enabled)
        {
            list.Add(new ChatMessage(ChatRole.System, OutputSaverPrompts.Caveman(savers.Caveman.Level)));
        }

        if (savers.Ponytail.Enabled)
        {
            list.Add(new ChatMessage(ChatRole.System, OutputSaverPrompts.Ponytail(savers.Ponytail.Level)));
        }

        logger.LogDebug(
            "[OUTPUT-SAVER] applied caveman={Caveman}({CavemanLevel}) ponytail={Ponytail}({PonytailLevel})",
            savers.Caveman.Enabled, savers.Caveman.Level, savers.Ponytail.Enabled, savers.Ponytail.Level);
        return list;
    }
}

/// <summary>Prompt text per saver and level (Lite/Full/Ultra), ported from 9router's savers.</summary>
public static class OutputSaverPrompts
{
    public static string Caveman(string level) => Normalize(level) switch
    {
        "lite" =>
            "Response style: be concise. Drop pleasantries, filler, and hedging. Keep all technical " +
            "substance, code blocks, and exact error messages unchanged.",
        "ultra" =>
            "Response style: maximum terseness. Fragments only. Drop articles, filler, pleasantries, " +
            "hedging, recaps. Shortest true answer wins. Technical terms exact. Code blocks and quoted " +
            "errors unchanged. Never pad.",
        _ =>
            "Response style: terse like a smart caveman. Drop articles (a/an/the), filler words " +
            "(just/really/basically/actually), pleasantries, and hedging. Sentence fragments are fine. " +
            "Prefer short synonyms. All technical substance stays; code blocks and exact error messages " +
            "unchanged. Write code, commits, and security warnings in normal prose."
    };

    public static string Ponytail(string level) => Normalize(level) switch
    {
        "lite" =>
            "Code style: prefer the simplest solution that works. Avoid speculative abstractions and " +
            "unrequested scaffolding. Shortest working diff wins.",
        "ultra" =>
            "Code style: extreme YAGNI. Before writing anything ask: does this need to exist? Stdlib " +
            "first, then existing deps; never add a dependency for what a few lines can do. No interfaces " +
            "with one implementation, no factories for one product, no config for constants. Deletion over " +
            "addition. Never simplify away input validation, error handling, security, or requested behavior.",
        _ =>
            "Code style: lazy senior developer (efficient, not careless). Stop at the first rung that " +
            "holds: skip it entirely (YAGNI), use stdlib, use a native platform feature, use an installed " +
            "dependency, make it one line — only then write minimal new code. No unrequested abstractions " +
            "or boilerplate. Boring over clever. Fewest files, shortest working diff. Never simplify away " +
            "input validation, error handling that prevents data loss, security measures, or anything " +
            "explicitly requested."
    };

    private static string Normalize(string level) => level.Trim().ToLowerInvariant();
}
