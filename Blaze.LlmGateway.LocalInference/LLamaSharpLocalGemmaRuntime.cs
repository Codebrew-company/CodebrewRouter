using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.LocalInference;

internal sealed class LLamaSharpLocalGemmaRuntime : ILocalGemmaRuntime
{
    private static readonly object NativeBackendConfigurationLock = new();
    private static bool nativeBackendConfigured;
    private static Action<bool> configureCuda = static enabled => NativeLibraryConfig.Instance.WithCuda(enabled);

    private readonly LocalInferenceOptions _options;
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    public LLamaSharpLocalGemmaRuntime(LocalInferenceOptions options, string localModelPath)
    {
        _options = options;

        try
        {
            ConfigureNativeBackendForCpu();

            var modelParams = new ModelParams(localModelPath)
            {
                ContextSize = (uint)Math.Max(1, options.MaxContextTokens),
                GpuLayerCount = 0,
                UseMemorymap = true,
            };

            if (options.ThreadCount > 0)
            {
                modelParams.Threads = (uint)options.ThreadCount;
                modelParams.BatchThreads = (uint)options.ThreadCount;
            }

            _weights = LLamaWeights.LoadFromFile(modelParams);
            _context = new LLamaContext(_weights, modelParams);
            _executor = new InteractiveExecutor(_context);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load Gemma model from '{localModelPath}'", ex);
        }
    }

    internal static void ConfigureNativeBackendForCpu()
    {
        lock (NativeBackendConfigurationLock)
        {
            if (nativeBackendConfigured) return;

            configureCuda(false);
            nativeBackendConfigured = true;
        }
    }

    internal static void ResetNativeBackendConfigurationForTests(Action<bool>? configureCudaOverride = null)
    {
        lock (NativeBackendConfigurationLock)
        {
            nativeBackendConfigured = false;
            configureCuda = configureCudaOverride ?? (static enabled => NativeLibraryConfig.Instance.WithCuda(enabled));
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        if (messages.Count == 0)
        {
            yield break;
        }

        var history = messages.Take(messages.Count - 1).ToList();
        var prompt = FormatConversation(history, messages[^1].Text ?? "");
        var inferenceParams = new InferenceParams
        {
            Temperature = options?.Temperature ?? _options.Temperature,
            TopP = options?.TopP ?? _options.TopP,
            MaxTokens = options?.MaxOutputTokens ?? 512,
        };

        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, token);
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await Task.Run(() =>
        {
            try
            {
                _context.Dispose();
            }
            finally
            {
                _weights.Dispose();
                _inferenceLock.Dispose();
            }
        });

        _disposed = true;
    }

    private static string FormatConversation(IEnumerable<ChatMessage> history, string currentPrompt)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var msg in history)
        {
            if (msg.Role == ChatRole.User)
            {
                sb.Append("User: ");
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                sb.Append("Assistant: ");
            }

            sb.AppendLine(msg.Text ?? "");
        }

        sb.Append("User: ");
        sb.AppendLine(currentPrompt);
        sb.Append("Assistant: ");

        return sb.ToString();
    }
}
