using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// MEAI adapter for local Gemma inference.
/// The provider materializes a local or remote model source lazily, then keeps one LM-Kit runtime resident.
/// </summary>
public sealed class LocalGemmaChatClient : DelegatingChatClient, ILocalGemmaModelState, IAsyncDisposable
{
    private readonly LocalInferenceOptions _options;
    private readonly IModelDistributionProvider? _modelProvider;
    private readonly ILogger<LocalGemmaChatClient>? _logger;
    private readonly Func<LocalInferenceOptions, string, ILocalGemmaRuntime> _runtimeFactory;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly string? _unavailableReason;
    private ILocalGemmaRuntime? _runtime;
    private string? _resolvedModelPath;
    private bool _disposed;

    public LocalGemmaChatClient()
        : this((string?)null)
    {
    }

    public LocalGemmaChatClient(string? modelPath)
        : this(new LocalInferenceOptions { ModelPath = modelPath ?? string.Empty })
    {
    }

    public LocalGemmaChatClient(
        LocalInferenceOptions options,
        IModelDistributionProvider? modelProvider = null,
        ILogger<LocalGemmaChatClient>? logger = null)
        : this(options, modelProvider, logger, null)
    {
    }

    internal LocalGemmaChatClient(
        LocalInferenceOptions options,
        IModelDistributionProvider? modelProvider,
        ILogger<LocalGemmaChatClient>? logger,
        Func<LocalInferenceOptions, string, ILocalGemmaRuntime>? runtimeFactory = null)
        : base(new NoOpChatClientWithMetadata())
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modelProvider = modelProvider;
        _logger = logger;
        _runtimeFactory = runtimeFactory ?? DefaultRuntimeFactory(logger);

        if (!options.Enabled)
        {
            _unavailableReason = "LocalGemma is not loaded because local inference is disabled.";
        }
        else if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            _unavailableReason =
                "LocalGemma is not loaded because LlmGateway:LocalInference:ModelPath is not configured. " +
                "Set it to a local Gemma GGUF file or a Hugging Face GGUF URL.";
        }
    }

    public string? ModelPath => _resolvedModelPath ?? _options.ModelPath;

    public bool IsModelLoaded => _runtime is not null;

    public async Task EnsureLoadedAsync(
        CancellationToken cancellationToken = default,
        Action? onModelFileReady = null)
    {
        if (_runtime is not null) return;

        if (_unavailableReason is not null)
        {
            throw new InvalidOperationException(_unavailableReason);
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is not null) return;

            var resolvedPath = await ResolveModelPathAsync(cancellationToken);
            _resolvedModelPath = resolvedPath;
            onModelFileReady?.Invoke();
            _runtime = _runtimeFactory(_options, resolvedPath);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        if (messages.Count == 0)
        {
            yield break;
        }

        await EnsureLoadedAsync(cancellationToken);
        var runtime = _runtime
            ?? throw new InvalidOperationException("LocalGemma model load did not produce a runtime.");

        await foreach (var update in runtime.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var accumulatedText = new StringBuilder();

        await foreach (var update in GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            accumulatedText.Append(update.Text);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, accumulatedText.ToString()))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }

        _loadLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task<string> ResolveModelPathAsync(CancellationToken cancellationToken)
    {
        var modelPath = _options.ModelPath;
        var isRemoteUrl = modelPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || modelPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (_modelProvider is null)
        {
            if (!File.Exists(modelPath))
            {
                throw new InvalidOperationException(
                    $"LocalGemma model file not found: '{modelPath}'. Configure LlmGateway:LocalInference:ModelPath to a local Gemma GGUF file or a Hugging Face GGUF URL.");
            }

            var fullPath = Path.GetFullPath(modelPath);
            if (_logger is not null) LocalModelLog.Resolve(_logger, modelPath, fullPath);
            return fullPath;
        }

        if (isRemoteUrl)
        {
            var cached = await _modelProvider.GetCachedModelPathAsync(modelPath);
            if (cached is not null)
            {
                if (_logger is not null) LocalModelLog.CacheHit(_logger, modelPath, cached);
                return cached;
            }

            if (_logger is not null) LocalModelLog.DownloadStart(_logger, modelPath, _options.CacheDirectory);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var downloaded = await _modelProvider.EnsureModelAvailableAsync(modelPath, cancellationToken);
                stopwatch.Stop();
                if (_logger is not null) LocalModelLog.DownloadReady(_logger, modelPath, downloaded, stopwatch.ElapsedMilliseconds);
                return downloaded;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (_logger is not null) LocalModelLog.DownloadFail(_logger, modelPath, ex);
                throw;
            }
        }

        var resolved = await _modelProvider.EnsureModelAvailableAsync(modelPath, cancellationToken);
        if (_logger is not null) LocalModelLog.Resolve(_logger, modelPath, resolved);
        return resolved;
    }

    private static Func<LocalInferenceOptions, string, ILocalGemmaRuntime> DefaultRuntimeFactory(ILogger<LocalGemmaChatClient>? logger)
        => (options, resolvedModelPath) => new LmKitLocalGemmaRuntime(options, resolvedModelPath, logger);
}

internal sealed class NoOpChatClientWithMetadata : IChatClient
{
    public ChatClientMetadata Metadata => new();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))
        {
            ModelId = "gemma-local",
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
