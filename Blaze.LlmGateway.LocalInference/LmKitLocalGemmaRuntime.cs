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

        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            using var conversation = new MultiTurnConversation(_model, ResolveContextSize());
            ConfigureConversation(conversation, messages, options);

            var nonSystemMessages = messages.Where(message => message.Role != ChatRole.System).ToList();
            if (nonSystemMessages.Count == 0)
            {
                yield break;
            }

            var prompt = nonSystemMessages[^1].Text ?? string.Empty;
            var updates = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            void HandleAfterTokenSampling(object? _, AfterTokenSamplingEventArgs eventArgs)
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.TextChunk))
                {
                    updates.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, eventArgs.TextChunk));
                }
            }

            conversation.AfterTokenSampling += HandleAfterTokenSampling;

            var generationTask = Task.Run(async () =>
            {
                try
                {
                    await conversation.SubmitAsync(prompt, cancellationToken);
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

        var historyMessages = messages
            .Where(message => message.Role != ChatRole.System)
            .Take(Math.Max(0, messages.Count(message => message.Role != ChatRole.System) - 1));

        foreach (var message in historyMessages)
        {
            conversation.ChatHistory.AddMessage(MapRole(message.Role), message.Text ?? string.Empty);
        }
    }

    private int ResolveContextSize()
        => _options.MaxContextTokens > 0 ? _options.MaxContextTokens : -1;

    private static AuthorRole MapRole(ChatRole role)
        => role == ChatRole.Assistant
            ? AuthorRole.Assistant
            : role == ChatRole.Tool
                ? AuthorRole.Tool
                : AuthorRole.User;

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
}
