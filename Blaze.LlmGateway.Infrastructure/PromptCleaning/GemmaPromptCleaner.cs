using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.PromptCleaning;

/// <summary>
/// <see cref="IPromptCleaner"/> backed by the keyed <c>OllamaLocal</c> client (gemma4:e4b
/// by default). Mirrors the circuit-breaker behaviour of <c>OllamaTaskClassifier</c>:
/// any exception opens the circuit for <see cref="PromptCleanupOptions.CooldownMinutes"/>,
/// during which the cleaner short-circuits to passthrough.
/// </summary>
public sealed class GemmaPromptCleaner : IPromptCleaner
{
    private const string DefaultSystemPrompt = """
        You rewrite user prompts to be as short and unambiguous as possible while preserving the user's intent.

        STRICT RULES:
        - Output ONLY the rewritten prompt. No preamble, no quotes, no explanation, no markdown fence.
        - Preserve VERBATIM: code snippets, file paths, identifiers, URLs, error messages, and any quoted strings.
        - Preserve any explicit format requirement (e.g. "respond in JSON", "one-line answer", "list of 3").
        - Strip filler ("please", "I was wondering", "could you maybe"), greetings, repeated context, and hedging.
        - Do NOT answer the prompt. Do NOT add new information. Do NOT change the language.
        - The rewrite MUST be shorter than the original. If you cannot shorten it without losing meaning, return the original unchanged.
        """;

    private readonly IChatClient _routerClient;
    private readonly PromptCleanupOptions _options;
    private readonly ILogger<GemmaPromptCleaner> _logger;
    private readonly TimeSpan _cooldown;
    private readonly string _systemPrompt;

    private DateTimeOffset? _circuitOpenedAt;

    public GemmaPromptCleaner(
        IChatClient routerClient,
        IOptions<PromptCleanupOptions> options,
        ILogger<GemmaPromptCleaner> logger)
    {
        _routerClient = routerClient;
        _options = options.Value;
        _logger = logger;
        _cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));
        _systemPrompt = string.IsNullOrWhiteSpace(_options.SystemPrompt)
            ? DefaultSystemPrompt
            : _options.SystemPrompt!;
    }

    public async Task<string> CleanAsync(string original, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(original))
            return original;

        if (original.Length < _options.MinLengthChars)
        {
            _logger.LogDebug("GemmaPromptCleaner: skipping (length {Len} < {Min})",
                original.Length, _options.MinLengthChars);
            return original;
        }

        if (_circuitOpenedAt is { } openedAt && DateTimeOffset.UtcNow - openedAt < _cooldown)
        {
            return original;
        }

        try
        {
            var messages = new[]
            {
                new ChatMessage(ChatRole.System, _systemPrompt),
                new ChatMessage(ChatRole.User, original)
            };

            var opts = new ChatOptions
            {
                MaxOutputTokens = _options.MaxOutputTokens,
                Temperature = _options.Temperature
            };

            var response = await _routerClient.GetResponseAsync(messages, opts, cancellationToken);
            var cleaned = response.Text?.Trim() ?? "";

            if (!IsValidRewrite(original, cleaned))
            {
                _logger.LogDebug("GemmaPromptCleaner: rewrite rejected (orig={OrigLen}, cleaned={CleanLen}) — using original",
                    original.Length, cleaned.Length);
                return original;
            }

            _logger.LogInformation("✂️ GemmaPromptCleaner trimmed prompt {OrigLen} → {CleanLen} chars",
                original.Length, cleaned.Length);
            return cleaned;
        }
        catch (Exception ex)
        {
            if (_circuitOpenedAt is null)
            {
                _logger.LogWarning(ex, "GemmaPromptCleaner failed — opening circuit for {Cooldown}; passing prompts through unchanged",
                    _cooldown);
            }
            _circuitOpenedAt = DateTimeOffset.UtcNow;
            return original;
        }
    }

    /// <summary>
    /// Defensive guard: a "valid" rewrite must be non-empty and not pathologically
    /// longer than the original. 1.5× catches the common failure modes (echoed
    /// instructions, added preamble) while allowing minor restructures.
    /// </summary>
    internal static bool IsValidRewrite(string original, string cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        if (cleaned.Length > (int)(original.Length * 1.5))
            return false;

        return true;
    }
}
