using System.Threading.Channels;
using System.Text.RegularExpressions;
using Blaze.LlmGateway.Core.Configuration;
using LMKit.Model;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LMKit.TextGeneration.Events;
using LMKit.TextGeneration.Sampling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Blaze.LlmGateway.LocalInference;

internal sealed class LmKitLocalGemmaRuntime : ILocalGemmaRuntime
{
    private readonly LocalInferenceOptions _options;
    private readonly ILogger? _logger;
    private readonly LM _model;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    public LmKitLocalGemmaRuntime(
        LocalInferenceOptions options,
        string localModelPath,
        ILogger? logger = null)
    {
        _options = options;
        _logger = logger;

        try
        {
            var loadingOptions = new LM.LoadingOptions
            {
                LoadTensors = true
            };

            _model = new LM(
                localModelPath,
                loadingOptions: loadingOptions,
                loadingProgress: progress =>
                {
                    _logger?.LogDebug("LM-Kit loading progress for '{ModelPath}': {Progress:P0}", localModelPath, progress);
                    return true;
                });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(BuildLoadFailureMessage(localModelPath, ex), ex);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        if (messages.Count == 0)
        {
            yield break;
        }

        var nonSystemMessages = messages.Where(message => message.Role != ChatRole.System).ToList();
        if (nonSystemMessages.Count == 0)
        {
            yield break;
        }

        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            using var conversation = CreateConversation(messages);
            ConfigureConversation(conversation, messages, options);

            var prompt = BuildPromptWithHistory(messages);
            var updates = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            var streamFilter = new ChannelTextFilter();

            void HandleAfterTokenSampling(object? _, AfterTokenSamplingEventArgs eventArgs)
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.TextChunk))
                {
                    foreach (var visibleChunk in streamFilter.Append(eventArgs.TextChunk))
                    {
                        updates.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, visibleChunk));
                    }
                }
            }

            conversation.AfterTokenSampling += HandleAfterTokenSampling;

            var generationTask = Task.Run(async () =>
            {
                try
                {
                    await conversation.SubmitAsync(prompt, cancellationToken);
                    foreach (var visibleChunk in streamFilter.Complete())
                    {
                        updates.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, visibleChunk));
                    }
                    updates.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    updates.Writer.TryComplete(ex);
                }
            }, cancellationToken);

            try
            {
                await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return update;
                }

                await generationTask;
            }
            finally
            {
                conversation.AfterTokenSampling -= HandleAfterTokenSampling;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _inferenceLock.Dispose();
        _model.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ConfigureConversation(
        MultiTurnConversation conversation,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        conversation.MaximumCompletionTokens = Math.Max(1, options?.MaxOutputTokens ?? 512);
        conversation.SamplingMode = new RandomSampling
        {
            Temperature = options?.Temperature ?? _options.Temperature,
            TopP = options?.TopP ?? _options.TopP
        };

        var systemPromptParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            systemPromptParts.Add(_options.SystemPrompt);
        }

        systemPromptParts.AddRange(
            messages
                .Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))!);

        if (systemPromptParts.Count > 0)
        {
            conversation.SystemPrompt = string.Join(Environment.NewLine + Environment.NewLine, systemPromptParts);
        }

        foreach (var controlToken in ChannelTextFilter.ControlTokens)
        {
            conversation.StopSequences.Add(controlToken);
        }

    }

    private MultiTurnConversation CreateConversation(IReadOnlyList<ChatMessage> messages)
        => new(_model, ResolveContextSize());

    internal static string BuildPromptWithHistory(IReadOnlyList<ChatMessage> messages)
    {
        var nonSystemMessages = messages.Where(message => message.Role != ChatRole.System).ToArray();
        if (nonSystemMessages.Length == 0)
        {
            return string.Empty;
        }

        var currentPrompt = nonSystemMessages[^1].Text ?? string.Empty;
        var historyEntries = BuildSeededHistoryEntries(messages);
        if (historyEntries.Count == 0)
        {
            return currentPrompt;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Conversation so far:");
        foreach (var (role, content) in historyEntries)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            builder.Append(MapRoleLabel(role));
            builder.Append(": ");
            builder.AppendLine(content);
        }

        builder.AppendLine();
        builder.AppendLine("Current request:");
        builder.Append(currentPrompt);
        return builder.ToString();
    }

    internal static IReadOnlyList<(AuthorRole Role, string Content)> BuildSeededHistoryEntries(
        IReadOnlyList<ChatMessage> messages)
        => messages
            .Where(message => message.Role != ChatRole.System)
            .Take(Math.Max(0, messages.Count(message => message.Role != ChatRole.System) - 1))
            .Select(message => (Role: MapRole(message.Role), Content: message.Text ?? string.Empty))
            .ToArray();

    private int ResolveContextSize()
        => _options.MaxContextTokens > 0 ? _options.MaxContextTokens : -1;

    private static AuthorRole MapRole(ChatRole role)
        => role == ChatRole.Assistant
            ? AuthorRole.Assistant
            : role == ChatRole.Tool
                ? AuthorRole.Tool
                : AuthorRole.User;

    private static string MapRoleLabel(AuthorRole role)
        => role == AuthorRole.Assistant
            ? "Assistant"
            : role == AuthorRole.Tool
                ? "Tool"
                : "User";

    internal static string BuildLoadFailureMessage(string modelPath, Exception ex)
    {
        var compatibilityHint = TryFormatCompatibilityHint(ex);
        if (compatibilityHint is not null)
        {
            return
                $"LM-Kit could not load local Gemma model '{modelPath}' because its native llama.cpp backend rejected the model format. " +
                $"{compatibilityHint} " +
                "This usually means the installed LM-Kit native backend is older than the Gemma 4 quantization you downloaded. " +
                "Update LM-Kit to the newest package/backend build or switch LlmGateway:LocalInference:ModelPath to an LM-Kit-verified model that your current backend supports.";
        }

        return $"Failed to load local Gemma model from '{modelPath}' via LM-Kit.";
    }

    private static string? TryFormatCompatibilityHint(Exception ex)
    {
        var details = ex.ToString();

        var typeMatch = Regex.Match(
            details,
            @"invalid ggml type\s+(?<type>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!typeMatch.Success)
        {
            return null;
        }

        var tensorMatch = Regex.Match(
            details,
            @"tensor\s+'(?<tensor>[^']+)'",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var tensor = tensorMatch.Success ? $"Tensor '{tensorMatch.Groups["tensor"].Value}' " : "A tensor ";
        return $"{tensor}uses unsupported GGML type {typeMatch.Groups["type"].Value}.";
    }

    private sealed class ChannelTextFilter
    {
        private const string ChannelMarker = "<|channel>";
        internal static readonly string[] ControlTokens =
        [
            "<turn|>",
            "<end_of_turn>",
            "<|eot_id|>",
            "<|endoftext|>",
            "<|im_end|>",
            "</s>"
        ];
        private static readonly string[] PlainThinkingPreludePrefixes =
        [
            "Here's a thinking process",
            "Here is a thinking process",
            "I'll think through",
            "I will think through",
            "Let's think through"
        ];
        private static readonly string[] PlainFinalMarkers =
        [
            "Final response:",
            "Final answer:"
        ];
        private static readonly string[] KnownChannels = ["thought", "analysis", "reasoning", "scratchpad", "final", "assistant", "answer", "response", "output"];
        private static readonly HashSet<string> VisibleChannels = new(StringComparer.OrdinalIgnoreCase)
        {
            "final",
            "assistant",
            "answer",
            "response",
            "output"
        };
        private readonly StringBuilder _buffer = new();
        private readonly StringBuilder _suppressed = new();
        private string? _activeChannel;
        private bool _sawExplicitChannel;
        private bool _emittedVisibleText;
        private bool _suppressPlainThinkingPrelude;
        private bool _sawStopControlToken;

        public IEnumerable<string> Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk) || _sawStopControlToken)
            {
                yield break;
            }

            _buffer.Append(chunk);

            while (true)
            {
                var markerIndex = IndexOf(_buffer, ChannelMarker);
                if (markerIndex < 0)
                {
                    foreach (var text in FlushWithoutMarker())
                    {
                        yield return text;
                    }

                    yield break;
                }

                if (markerIndex > 0)
                {
                    var prefix = _buffer.ToString(0, markerIndex);
                    if (ShouldEmitCurrentChannelText(prefix))
                    {
                        _emittedVisibleText = true;
                        yield return prefix;
                    }
                    else
                    {
                        RememberSuppressed(prefix);
                    }

                    _buffer.Remove(0, markerIndex);
                }

                if (!TryConsumeMarker())
                {
                    yield break;
                }
            }
        }

        private IEnumerable<string> FlushWithoutMarker()
        {
            foreach (var visibleText in FlushPlainThinkingPrelude())
            {
                yield return visibleText;
            }

            if (_sawStopControlToken || _suppressPlainThinkingPrelude || ShouldWaitForPlainThinkingPreludeDecision())
            {
                yield break;
            }

            if (TryConsumeControlTokenPrefix(out var prefixBeforeStop))
            {
                foreach (var text in EmitOrSuppress(prefixBeforeStop))
                {
                    yield return text;
                }

                yield break;
            }

            var trailingMarkerLength = LongestMarkerPrefixAtEnd();
            var trailingControlTokenLength = LongestControlTokenPrefixAtEnd();
            var flushLength = _buffer.Length - Math.Max(trailingMarkerLength, trailingControlTokenLength);
            if (flushLength <= 0)
            {
                yield break;
            }

            var textToFlush = _buffer.ToString(0, flushLength);
            _buffer.Remove(0, flushLength);

            foreach (var text in EmitOrSuppress(textToFlush))
            {
                yield return text;
            }
        }

        private IEnumerable<string> EmitOrSuppress(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            if (ShouldEmitCurrentChannelText(text))
            {
                _emittedVisibleText = true;
                yield return text;
            }
            else
            {
                RememberSuppressed(text);
            }
        }

        public IEnumerable<string> Complete()
        {
            foreach (var text in FlushWithoutMarker())
            {
                yield return text;
            }

            if (_sawStopControlToken || _suppressPlainThinkingPrelude)
            {
                yield break;
            }

            if (TryConsumeControlTokenPrefix(out var prefixBeforeStop))
            {
                foreach (var text in EmitOrSuppress(prefixBeforeStop))
                {
                    yield return text;
                }

                yield break;
            }

            if (_buffer.Length == 0)
            {
                if (!_emittedVisibleText && _suppressed.Length > 0)
                {
                    yield return _suppressed.ToString();
                }
                yield break;
            }

            var remaining = _buffer.ToString();
            _buffer.Clear();

            if (ShouldEmitCurrentChannelText(remaining))
            {
                _emittedVisibleText = true;
                yield return remaining;
                yield break;
            }

            if (!_emittedVisibleText)
            {
                RememberSuppressed(remaining);
                if (_suppressed.Length > 0)
                {
                    yield return _suppressed.ToString();
                }
                yield break;
            }
        }

        private IEnumerable<string> FlushPlainThinkingPrelude()
        {
            if (_sawExplicitChannel || _emittedVisibleText)
            {
                yield break;
            }

            if (!_suppressPlainThinkingPrelude)
            {
                var buffered = _buffer.ToString();
                if (StartsWithPlainThinkingPrelude(buffered))
                {
                    _suppressPlainThinkingPrelude = true;
                }
                else
                {
                    yield break;
                }
            }

            while (_suppressPlainThinkingPrelude)
            {
                var buffered = _buffer.ToString();
                if (TryFindFinalMarker(buffered, out var markerIndex, out var markerLength))
                {
                    RememberSuppressed(buffered[..(markerIndex + markerLength)]);
                    _buffer.Remove(0, markerIndex + markerLength);
                    TrimLeadingWhitespace(_buffer);
                    _suppressPlainThinkingPrelude = false;

                    if (_buffer.Length > 0)
                    {
                        var visible = _buffer.ToString();
                        _buffer.Clear();
                        _emittedVisibleText = true;
                        yield return visible;
                    }

                    yield break;
                }

                var retainLength = Math.Min(_buffer.Length, LongestFinalMarkerLength() - 1);
                var suppressLength = _buffer.Length - retainLength;
                if (suppressLength > 0)
                {
                    RememberSuppressed(_buffer.ToString(0, suppressLength));
                    _buffer.Remove(0, suppressLength);
                }

                yield break;
            }
        }

        private bool TryConsumeMarker()
        {
            if (_buffer.Length <= ChannelMarker.Length)
            {
                return false;
            }

            var remainder = _buffer.ToString(ChannelMarker.Length, _buffer.Length - ChannelMarker.Length);
            foreach (var knownChannel in KnownChannels)
            {
                if (!remainder.StartsWith(knownChannel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var consumedLength = ChannelMarker.Length + knownChannel.Length;
                if (_buffer.Length < consumedLength)
                {
                    return false;
                }

                if (_buffer.Length > consumedLength)
                {
                    var delimiter = _buffer[consumedLength];
                    if (delimiter is '\r' or '\n')
                    {
                        consumedLength++;
                        if (delimiter == '\r' && _buffer.Length > consumedLength && _buffer[consumedLength] == '\n')
                        {
                            consumedLength++;
                        }
                    }
                }

                _buffer.Remove(0, consumedLength);
                _activeChannel = knownChannel;
                _sawExplicitChannel = true;
                return true;
            }

            var newlineIndex = IndexOfNewline(_buffer, ChannelMarker.Length);
            if (newlineIndex < 0)
            {
                return false;
            }

            var channelValue = _buffer.ToString(ChannelMarker.Length, newlineIndex - ChannelMarker.Length).Trim();
            _buffer.Remove(0, newlineIndex + 1);
            _activeChannel = string.IsNullOrWhiteSpace(channelValue) ? null : channelValue;
            _sawExplicitChannel = true;
            return true;
        }

        private bool ShouldEmitCurrentChannelText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (!_sawExplicitChannel)
            {
                return true;
            }

            return _activeChannel is not null && VisibleChannels.Contains(_activeChannel);
        }

        private void RememberSuppressed(string text)
        {
            if (string.IsNullOrEmpty(text) || _emittedVisibleText)
            {
                return;
            }

            _suppressed.Append(text);
        }

        private int LongestMarkerPrefixAtEnd()
        {
            var max = Math.Min(_buffer.Length, ChannelMarker.Length - 1);
            for (var length = max; length > 0; length--)
            {
                var suffix = _buffer.ToString(_buffer.Length - length, length);
                if (ChannelMarker.StartsWith(suffix, StringComparison.Ordinal))
                {
                    return length;
                }
            }

            return 0;
        }

        private bool TryConsumeControlTokenPrefix(out string prefixBeforeStop)
        {
            var earliestIndex = -1;
            foreach (var controlToken in ControlTokens)
            {
                var index = _buffer.ToString().IndexOf(controlToken, StringComparison.Ordinal);
                if (index >= 0 && (earliestIndex < 0 || index < earliestIndex))
                {
                    earliestIndex = index;
                }
            }

            if (earliestIndex < 0)
            {
                prefixBeforeStop = string.Empty;
                return false;
            }

            prefixBeforeStop = _buffer.ToString(0, earliestIndex);
            _buffer.Clear();
            _sawStopControlToken = true;
            return true;
        }

        private int LongestControlTokenPrefixAtEnd()
        {
            var max = Math.Min(_buffer.Length, ControlTokens.Max(token => token.Length) - 1);
            for (var length = max; length > 0; length--)
            {
                var suffix = _buffer.ToString(_buffer.Length - length, length);
                if (ControlTokens.Any(token => token.StartsWith(suffix, StringComparison.Ordinal)))
                {
                    return length;
                }
            }

            return 0;
        }

        private static int IndexOf(StringBuilder builder, string value)
            => builder.ToString().IndexOf(value, StringComparison.Ordinal);

        private bool ShouldWaitForPlainThinkingPreludeDecision()
        {
            if (_sawExplicitChannel || _emittedVisibleText || _buffer.Length == 0)
            {
                return false;
            }

            var buffered = _buffer.ToString();
            return PlainThinkingPreludePrefixes.Any(prefix =>
                prefix.StartsWith(buffered, StringComparison.OrdinalIgnoreCase));
        }

        private static bool StartsWithPlainThinkingPrelude(string text)
            => PlainThinkingPreludePrefixes.Any(prefix =>
                text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static bool TryFindFinalMarker(string text, out int markerIndex, out int markerLength)
        {
            foreach (var marker in PlainFinalMarkers)
            {
                markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex >= 0)
                {
                    markerLength = marker.Length;
                    return true;
                }
            }

            markerIndex = -1;
            markerLength = 0;
            return false;
        }

        private static int LongestFinalMarkerLength()
            => PlainFinalMarkers.Max(marker => marker.Length);

        private static void TrimLeadingWhitespace(StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[0]))
            {
                builder.Remove(0, 1);
            }
        }

        private static int IndexOfNewline(StringBuilder builder, int startIndex)
        {
            for (var i = startIndex; i < builder.Length; i++)
            {
                var ch = builder[i];
                if (ch == '\n')
                {
                    return i;
                }

                if (ch == '\r')
                {
                    if (i + 1 < builder.Length && builder[i + 1] == '\n')
                    {
                        return i + 1;
                    }

                    return i;
                }
            }

            return -1;
        }
    }
}
