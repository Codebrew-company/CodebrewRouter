# Offline Gemma Warmup And Provider Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `LocalGemma` materialize a local or remote GGUF model source at the provider level, load/warm Gemma before API readiness, and keep Scalar/OpenWebUI/Agent DevUI waiting until the model is ready.

**Architecture:** `LocalGemmaChatClient` becomes a lazy resident provider facade. It uses `IModelDistributionProvider` to resolve `ModelPath` into a local GGUF path, then creates an internal LLamaSharp runtime that owns native model/context/executor resources. `LocalGemmaWarmupService` calls the provider facade before priming, and Aspire waits on API readiness.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, LLamaSharp, Aspire AppHost, xUnit, FluentAssertions, Moq.

---

## Current Worktree Context

The worktree already has partial implementation edits in these files:

- `Blaze.LlmGateway.LocalInference/ILocalGemmaModelState.cs`
- `Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs`
- `Blaze.LlmGateway.LocalInference/LocalGemmaWarmupState.cs`
- `Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs`
- `Blaze.LlmGateway.LocalInference/LocalModelLog.cs`
- `Blaze.LlmGateway.AppHost/AppHostComposition.cs`
- `Blaze.LlmGateway.Tests/AppHostCompositionTests.cs`
- `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaWarmupServiceTests.cs`
- `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaChatClientProviderTests.cs`

Do not revert these edits. Treat them as a partial draft and refine them into the structure below.

## File Structure

- Create: `Blaze.LlmGateway.LocalInference/ILocalGemmaRuntime.cs`
  - Internal seam for the loaded model runtime so provider loading can be unit-tested without a real GGUF.
- Create: `Blaze.LlmGateway.LocalInference/LLamaSharpLocalGemmaRuntime.cs`
  - Owns `LLamaWeights`, `LLamaContext`, `InteractiveExecutor`, inference locking, prompt formatting, and native disposal.
- Modify: `Blaze.LlmGateway.LocalInference/Blaze.LlmGateway.LocalInference.csproj`
  - Grants test assembly access to internal runtime seams.
- Modify: `Blaze.LlmGateway.LocalInference/ILocalGemmaModelState.cs`
  - Exposes `EnsureLoadedAsync(...)` for warmup/readiness.
- Modify: `Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs`
  - Resolves local/remote model source, logs provider materialization, creates one resident runtime, delegates chat calls.
- Create/Modify: `Blaze.LlmGateway.LocalInference/LocalModelLog.cs`
  - Defines `[LOCAL-MODEL-*]` log tags.
- Modify: `Blaze.LlmGateway.LocalInference/LocalGemmaWarmupState.cs`
  - Adds `Downloading` status.
- Modify: `Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs`
  - Injects `IModelDistributionProvider` and logger into keyed `"LocalGemma"`; warmup calls `EnsureLoadedAsync`.
- Modify: `Blaze.LlmGateway.AppHost/AppHostComposition.cs`
  - Passes cache/download env vars and gates Scalar with `.WaitFor(api)`.
- Modify: `Docs/engineering/logging-contract.md`
  - Documents `[LOCAL-MODEL-*]`.
- Modify: `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaChatClientProviderTests.cs`
  - Unit-tests materialization, cache hit, download, streaming after load, and concurrency through a fake runtime factory.
- Modify: `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaWarmupServiceTests.cs`
  - Unit-tests `Downloading`, `EnsureLoadedAsync`, and failure behavior.
- Modify: `Blaze.LlmGateway.Tests/AppHostCompositionTests.cs`
  - Asserts env vars and Scalar/OpenWebUI/Agent DevUI wait behavior.
- Modify: `Blaze.LlmGateway.Tests/RouterLoggingContractTests.cs`
  - Asserts `[LOCAL-MODEL-*]` tags are local-only and documented.

---

### Task 1: Add A Testable Loaded Runtime Boundary

**Files:**
- Create: `Blaze.LlmGateway.LocalInference/ILocalGemmaRuntime.cs`
- Create: `Blaze.LlmGateway.LocalInference/LLamaSharpLocalGemmaRuntime.cs`
- Modify: `Blaze.LlmGateway.LocalInference/Blaze.LlmGateway.LocalInference.csproj`
- Modify: `Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs`

- [ ] **Step 1: Grant tests access to internal runtime seams**

Add this item group to `Blaze.LlmGateway.LocalInference/Blaze.LlmGateway.LocalInference.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Blaze.LlmGateway.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Create the runtime interface**

Add `Blaze.LlmGateway.LocalInference/ILocalGemmaRuntime.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.LocalInference;

internal interface ILocalGemmaRuntime : IAsyncDisposable
{
    IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Move LLamaSharp ownership into a runtime class**

Create `Blaze.LlmGateway.LocalInference/LLamaSharpLocalGemmaRuntime.cs` by moving the existing LLamaSharp fields and prompt/inference code out of `LocalGemmaChatClient`:

```csharp
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using LLama;
using LLama.Common;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.LocalInference;

internal sealed class LLamaSharpLocalGemmaRuntime : ILocalGemmaRuntime
{
    private readonly LocalInferenceOptions _options;
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    public LLamaSharpLocalGemmaRuntime(LocalInferenceOptions options, string localModelPath)
    {
        _options = options;

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

        try
        {
            _weights = LLamaWeights.LoadFromFile(modelParams);
            _context = new LLamaContext(_weights, modelParams);
            _executor = new InteractiveExecutor(_context);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load Gemma model from '{localModelPath}'", ex);
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
            _context.Dispose();
            _weights.Dispose();
            _inferenceLock.Dispose();
        });
        _disposed = true;
    }

    private static string FormatConversation(IEnumerable<ChatMessage> history, string currentPrompt)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var msg in history)
        {
            if (msg.Role == ChatRole.User) sb.Append("User: ");
            else if (msg.Role == ChatRole.Assistant) sb.Append("Assistant: ");
            sb.AppendLine(msg.Text ?? "");
        }

        sb.Append("User: ");
        sb.AppendLine(currentPrompt);
        sb.Append("Assistant: ");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run a build to reveal refactor breakage**

Run:

```powershell
dotnet build E:\src\CodebrewRouter\Blaze.LlmGateway.slnx
```

Expected: build may fail because `LocalGemmaChatClient` still owns the old LLamaSharp fields. Use the failures to complete Task 2.

---

### Task 2: Implement Provider-Level Materialization In LocalGemma

**Files:**
- Modify: `Blaze.LlmGateway.LocalInference/ILocalGemmaModelState.cs`
- Modify: `Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs`
- Modify: `Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs`
- Create/Modify: `Blaze.LlmGateway.LocalInference/LocalModelLog.cs`
- Test: `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaChatClientProviderTests.cs`

- [ ] **Step 1: Write the provider facade tests**

Update `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaChatClientProviderTests.cs` so it uses a fake runtime factory instead of relying on LLamaSharp failure:

```csharp
private sealed class FakeRuntime : ILocalGemmaRuntime
{
    public int StreamingCalls { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamingCalls++;
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Fact]
public async Task EnsureLoadedAsync_WhenRemoteUrlAndNoCacheHit_DownloadsOnceAndLoadsResolvedPath()
{
    var remoteUrl = "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf";
    var downloadedPath = "C:/cache/gemma-4-E4B-it-Q4_K_M.gguf";
    var provider = new Mock<IModelDistributionProvider>();
    provider.Setup(p => p.GetCachedModelPathAsync(remoteUrl)).ReturnsAsync((string?)null);
    provider.Setup(p => p.EnsureModelAvailableAsync(remoteUrl, It.IsAny<CancellationToken>())).ReturnsAsync(downloadedPath);

    var loadedPaths = new List<string>();
    var client = new LocalGemmaChatClient(
        new LocalInferenceOptions { Enabled = true, ModelPath = remoteUrl },
        provider.Object,
        logger: null,
        runtimeFactory: (opts, path) =>
        {
            loadedPaths.Add(path);
            return new FakeRuntime();
        });

    await client.EnsureLoadedAsync();

    client.IsModelLoaded.Should().BeTrue();
    client.ModelPath.Should().Be(downloadedPath);
    loadedPaths.Should().Equal(downloadedPath);
    provider.Verify(p => p.GetCachedModelPathAsync(remoteUrl), Times.Once);
    provider.Verify(p => p.EnsureModelAvailableAsync(remoteUrl, It.IsAny<CancellationToken>()), Times.Once);
}
```

Also include tests for:

```csharp
// disabled: throws and provider is not called
// empty ModelPath: throws and provider is not called
// local path: calls EnsureModelAvailableAsync(localPath, ct)
// remote cache hit: calls GetCachedModelPathAsync and skips EnsureModelAvailableAsync
// GetStreamingResponseAsync: calls EnsureLoadedAsync then delegates to FakeRuntime
// concurrency: Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.EnsureLoadedAsync())) creates one FakeRuntime
```

- [ ] **Step 2: Verify the new tests fail before implementation**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter LocalGemmaChatClientProviderTests
```

Expected: FAIL because `LocalGemmaChatClient` does not yet expose the fake-runtime constructor and/or does not use the runtime boundary.

- [ ] **Step 3: Update the model state contract**

Ensure `Blaze.LlmGateway.LocalInference/ILocalGemmaModelState.cs` contains:

```csharp
namespace Blaze.LlmGateway.LocalInference;

public interface ILocalGemmaModelState
{
    string? ModelPath { get; }

    bool IsModelLoaded { get; }

    Task EnsureLoadedAsync(
        CancellationToken cancellationToken = default,
        Action? onModelFileReady = null);
}
```

- [ ] **Step 4: Ensure provider model log tags exist**

Ensure `Blaze.LlmGateway.LocalInference/LocalModelLog.cs` contains the five local model tags and no `[ROUTER-*]` tags:

```csharp
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

public static class LocalModelLog
{
    public const string ResolveTag = "[LOCAL-MODEL-RESOLVE]";
    public const string CacheHitTag = "[LOCAL-MODEL-CACHE-HIT]";
    public const string DownloadStartTag = "[LOCAL-MODEL-DOWNLOAD-START]";
    public const string DownloadReadyTag = "[LOCAL-MODEL-DOWNLOAD-READY]";
    public const string DownloadFailTag = "[LOCAL-MODEL-DOWNLOAD-FAIL]";

    public static void Resolve(ILogger logger, string? modelPath, string resolvedPath)
        => logger.LogInformation("{Tag} Local model resolved. ModelPath={ModelPath}, ResolvedPath={ResolvedPath}", ResolveTag, modelPath, resolvedPath);

    public static void CacheHit(ILogger logger, string modelUrl, string cachedPath)
        => logger.LogInformation("{Tag} Local model cache hit. ModelUrl={ModelUrl}, CachedPath={CachedPath}", CacheHitTag, modelUrl, cachedPath);

    public static void DownloadStart(ILogger logger, string modelUrl, string cacheDirectory)
        => logger.LogInformation("{Tag} Starting local model download. ModelUrl={ModelUrl}, CacheDirectory={CacheDirectory}", DownloadStartTag, modelUrl, cacheDirectory);

    public static void DownloadReady(ILogger logger, string modelUrl, string resolvedPath, long elapsedMilliseconds)
        => logger.LogInformation("{Tag} Local model download ready. ModelUrl={ModelUrl}, ResolvedPath={ResolvedPath}, ElapsedMs={ElapsedMs}", DownloadReadyTag, modelUrl, resolvedPath, elapsedMilliseconds);

    public static void DownloadFail(ILogger logger, string modelUrl, Exception exception)
        => logger.LogError(exception, "{Tag} Local model download failed. ModelUrl={ModelUrl}", DownloadFailTag, modelUrl);
}
```

- [ ] **Step 5: Refactor LocalGemmaChatClient into a provider facade**

Update `Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs` so it owns no LLamaSharp native objects directly. Its key members should match this shape:

```csharp
private readonly LocalInferenceOptions _options;
private readonly IModelDistributionProvider? _modelProvider;
private readonly ILogger<LocalGemmaChatClient>? _logger;
private readonly Func<LocalInferenceOptions, string, ILocalGemmaRuntime> _runtimeFactory;
private readonly SemaphoreSlim _loadLock = new(1, 1);
private ILocalGemmaRuntime? _runtime;
private string? _resolvedModelPath;
private readonly string? _unavailableReason;

public string? ModelPath => _resolvedModelPath ?? _options.ModelPath;

public bool IsModelLoaded => _runtime is not null;
```

Add this internal constructor for tests:

```csharp
internal LocalGemmaChatClient(
    LocalInferenceOptions options,
    IModelDistributionProvider? modelProvider,
    ILogger<LocalGemmaChatClient>? logger,
    Func<LocalInferenceOptions, string, ILocalGemmaRuntime> runtimeFactory)
    : base(new NoOpChatClientWithMetadata())
{
    _options = options;
    _modelProvider = modelProvider;
    _logger = logger;
    _runtimeFactory = runtimeFactory;

    if (!options.Enabled)
    {
        _unavailableReason = "LocalGemma is not loaded because local LLamaSharp inference is disabled.";
    }
    else if (string.IsNullOrWhiteSpace(options.ModelPath))
    {
        _unavailableReason =
            "LocalGemma is not loaded because LlmGateway:LocalInference:ModelPath is not configured. " +
            "Set it to a local Gemma GGUF file or a Hugging Face GGUF URL.";
    }
}
```

Add the public DI constructor:

```csharp
public LocalGemmaChatClient(
    LocalInferenceOptions options,
    IModelDistributionProvider? modelProvider = null,
    ILogger<LocalGemmaChatClient>? logger = null)
    : this(options, modelProvider, logger, static (opts, path) => new LLamaSharpLocalGemmaRuntime(opts, path))
{
}
```

Implement `EnsureLoadedAsync` with a single load lock:

```csharp
public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default, Action? onModelFileReady = null)
{
    if (_runtime is not null) return;
    if (_unavailableReason is not null) throw new InvalidOperationException(_unavailableReason);

    await _loadLock.WaitAsync(cancellationToken);
    try
    {
        if (_runtime is not null) return;

        var resolvedPath = await ResolveModelPathAsync(cancellationToken);
        onModelFileReady?.Invoke();
        _runtime = _runtimeFactory(_options, resolvedPath);
        _resolvedModelPath = resolvedPath;
    }
    finally
    {
        _loadLock.Release();
    }
}
```

Implement path resolution:

```csharp
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
```

- [ ] **Step 6: Delegate chat calls to the loaded runtime**

In `GetStreamingResponseAsync`, call `EnsureLoadedAsync(cancellationToken)` before streaming and delegate:

```csharp
await EnsureLoadedAsync(cancellationToken);
var runtime = _runtime ?? throw new InvalidOperationException("LocalGemma model load did not produce a runtime.");

await foreach (var update in runtime.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
{
    yield return update;
}
```

In `DisposeAsync`, dispose `_runtime` and `_loadLock`:

```csharp
public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    if (_runtime is not null) await _runtime.DisposeAsync();
    _loadLock.Dispose();
    _disposed = true;
    GC.SuppressFinalize(this);
}
```

- [ ] **Step 7: Wire DI to use provider-level LocalGemma**

In both keyed `"LocalGemma"` registrations in `Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs`, use:

```csharp
services.AddKeyedSingleton<IChatClient>("LocalGemma", (sp, _) =>
{
    var opts = sp.GetRequiredService<IOptions<LocalInferenceOptions>>().Value;
    var provider = sp.GetRequiredService<IModelDistributionProvider>();
    var logger = sp.GetService<ILogger<LocalGemmaChatClient>>();
    return new LocalGemmaChatClient(opts, provider, logger);
});
```

- [ ] **Step 8: Run focused provider tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter LocalGemmaChatClientProviderTests
```

Expected: PASS.

- [ ] **Step 9: Commit provider facade**

Run:

```powershell
git -C E:\src\CodebrewRouter add Blaze.LlmGateway.LocalInference/Blaze.LlmGateway.LocalInference.csproj Blaze.LlmGateway.LocalInference/ILocalGemmaRuntime.cs Blaze.LlmGateway.LocalInference/LLamaSharpLocalGemmaRuntime.cs Blaze.LlmGateway.LocalInference/ILocalGemmaModelState.cs Blaze.LlmGateway.LocalInference/LocalGemmaChatClient.cs Blaze.LlmGateway.LocalInference/LocalModelLog.cs Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs Blaze.LlmGateway.Tests/LocalInference/LocalGemmaChatClientProviderTests.cs
git -C E:\src\CodebrewRouter commit -m "feat: materialize local gemma models at provider level"
```

---

### Task 3: Refresh Warmup Readiness For Downloading And Provider Load

**Files:**
- Modify: `Blaze.LlmGateway.LocalInference/LocalGemmaWarmupState.cs`
- Modify: `Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs`
- Test: `Blaze.LlmGateway.Tests/LocalInference/LocalGemmaWarmupServiceTests.cs`

- [ ] **Step 1: Write warmup tests for provider loading**

Ensure `FakeWarmupChatClient` in `LocalGemmaWarmupServiceTests.cs` implements:

```csharp
public Task EnsureLoadedAsync(CancellationToken cancellationToken = default, Action? onModelFileReady = null)
{
    EnsureLoadedCalls++;
    if (EnsureLoadedAsyncException is not null) throw EnsureLoadedAsyncException;
    onModelFileReady?.Invoke();
    return Task.CompletedTask;
}
```

Ensure tests assert:

```csharp
fakeClient.EnsureLoadedCalls.Should().Be(1);
state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Ready);
```

And failure behavior:

```csharp
await service.StartAsync(CancellationToken.None);
state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.FailTag, StringComparison.Ordinal));
```

- [ ] **Step 2: Add the Downloading readiness state**

Update `LocalGemmaWarmupStatus`:

```csharp
public enum LocalGemmaWarmupStatus
{
    NotStarted = 0,
    Downloading = 1,
    Loading = 2,
    Priming = 3,
    Ready = 4,
    Skipped = 5,
    Failed = 6
}
```

The existing health mapping remains:

```csharp
var healthStatus = snapshot.Status switch
{
    LocalGemmaWarmupStatus.Ready => AspNetHealthStatus.Healthy,
    LocalGemmaWarmupStatus.Skipped => AspNetHealthStatus.Healthy,
    LocalGemmaWarmupStatus.Failed => AspNetHealthStatus.Unhealthy,
    _ => AspNetHealthStatus.Unhealthy
};
```

- [ ] **Step 3: Make warmup call the provider load before prime**

In `LocalGemmaWarmupService.StartAsync`, after resolving `modelState`, replace direct `IsModelLoaded` checking with:

```csharp
state.Update(LocalGemmaWarmupStatus.Downloading, opts.ModelPath, "Resolving local Gemma model source.", stopwatch.Elapsed);

await modelState.EnsureLoadedAsync(cancellationToken, () =>
{
    state.Update(LocalGemmaWarmupStatus.Loading, modelState.ModelPath, "Loading local Gemma model into LLamaSharp.", stopwatch.Elapsed);
});

LocalWarmupLog.Load(logger, modelState.ModelPath, modelState.IsModelLoaded, stopwatch.ElapsedMilliseconds);

if (!modelState.IsModelLoaded)
{
    throw new InvalidOperationException($"Local Gemma model was not loaded from '{modelState.ModelPath ?? opts.ModelPath}'.");
}
```

- [ ] **Step 4: Run focused warmup tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter LocalGemmaWarmupServiceTests
```

Expected: PASS.

- [ ] **Step 5: Commit warmup readiness**

Run:

```powershell
git -C E:\src\CodebrewRouter add Blaze.LlmGateway.LocalInference/LocalGemmaWarmupState.cs Blaze.LlmGateway.LocalInference/ServiceCollectionExtensions.cs Blaze.LlmGateway.Tests/LocalInference/LocalGemmaWarmupServiceTests.cs
git -C E:\src\CodebrewRouter commit -m "feat: gate warmup on provider model materialization"
```

---

### Task 4: Gate Aspire Dev Resources And Pass Download Config

**Files:**
- Modify: `Blaze.LlmGateway.AppHost/AppHostComposition.cs`
- Test: `Blaze.LlmGateway.Tests/AppHostCompositionTests.cs`

- [ ] **Step 1: Add AppHost tests for config and Scalar wait**

In `AppHostCompositionTests.cs`, keep the existing env var assertions and make the wait assertion explicit:

```csharp
Assert.Contains("LlmGateway__LocalInference__CacheDirectory", source);
Assert.Contains("LlmGateway__LocalInference__DownloadTimeoutSeconds", source);
Assert.Contains("builder.AddScalarApiReference()", source);
Assert.Contains(".WithApiReference(api)", source);
Assert.Contains(".WaitFor(api)", source);
```

- [ ] **Step 2: Run AppHost tests and verify the Scalar wait assertion fails**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter AppHostCompositionTests
```

Expected before implementation: FAIL if Scalar is not chained to `.WaitFor(api)`.

- [ ] **Step 3: Pass provider download settings into the API**

In `AppHostComposition.cs`, read:

```csharp
var localInferenceCacheDirectory = builder.Configuration.GetValue<string>(
    "LlmGateway:LocalInference:CacheDirectory") ?? ".llm-cache";
var localInferenceDownloadTimeoutSeconds = builder.Configuration.GetValue(
    "LlmGateway:LocalInference:DownloadTimeoutSeconds",
    3600);
```

Add env vars to `api`:

```csharp
.WithEnvironment("LlmGateway__LocalInference__CacheDirectory", localInferenceCacheDirectory)
.WithEnvironment("LlmGateway__LocalInference__DownloadTimeoutSeconds", localInferenceDownloadTimeoutSeconds.ToString())
```

- [ ] **Step 4: Gate Scalar on API readiness**

Replace the current Scalar setup with:

```csharp
builder.AddScalarApiReference()
    .WithApiReference(api)
    .WaitFor(api);
```

OpenWebUI and Agent DevUI should keep their existing `.WaitFor(api)` calls.

- [ ] **Step 5: Run AppHost tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter AppHostCompositionTests
```

Expected: PASS.

- [ ] **Step 6: Commit Aspire gating**

Run:

```powershell
git -C E:\src\CodebrewRouter add Blaze.LlmGateway.AppHost/AppHostComposition.cs Blaze.LlmGateway.Tests/AppHostCompositionTests.cs
git -C E:\src\CodebrewRouter commit -m "feat: gate dev ui resources on warmed api"
```

---

### Task 5: Update Logging Contract Coverage

**Files:**
- Modify: `Docs/engineering/logging-contract.md`
- Modify: `Blaze.LlmGateway.Tests/RouterLoggingContractTests.cs`
- Test: `Blaze.LlmGateway.Tests/RouterLoggingContractTests.cs`

- [ ] **Step 1: Extend logging contract tests for local model tags**

Add a `LocalModelTags` theory data set:

```csharp
public static TheoryData<string> LocalModelTags =>
    new()
    {
        LocalModelLog.ResolveTag,
        LocalModelLog.CacheHitTag,
        LocalModelLog.DownloadStartTag,
        LocalModelLog.DownloadReadyTag,
        LocalModelLog.DownloadFailTag
    };
```

Add a namespace test:

```csharp
[Theory]
[MemberData(nameof(LocalModelTags))]
public void LocalModelTags_DoNotUseRouterNamespace(string tag)
{
    tag.Should().StartWith("[LOCAL-MODEL-");
    tag.Should().NotStartWith("[ROUTER-");
}
```

Add documentation coverage:

```csharp
[Fact]
public void LocalModelTags_AreDocumentedInLoggingContract()
{
    var root = FindRepositoryRoot();
    var contract = File.ReadAllText(Path.Combine(root, "Docs", "engineering", "logging-contract.md"));

    foreach (var tag in LocalModelTags.Select(row => (string)row[0]))
    {
        contract.Should().Contain(tag);
    }
}
```

- [ ] **Step 2: Verify logging tests fail until docs are updated**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter RouterLoggingContractTests
```

Expected before docs update: FAIL because `[LOCAL-MODEL-*]` tags are not documented.

- [ ] **Step 3: Document local model tags**

In `Docs/engineering/logging-contract.md`, add a local model startup/materialization section containing:

```markdown
### Local Model Materialization

Provider-level local model source/cache events use `[LOCAL-MODEL-*]` tags. They are not request-routing events and must not be emitted through `RouterLog.Write(...)`.

- `[LOCAL-MODEL-RESOLVE]` - local path or model source resolved to an absolute GGUF path.
- `[LOCAL-MODEL-CACHE-HIT]` - remote model URL was already cached locally.
- `[LOCAL-MODEL-DOWNLOAD-START]` - remote model download started.
- `[LOCAL-MODEL-DOWNLOAD-READY]` - remote model download completed and cached path is ready.
- `[LOCAL-MODEL-DOWNLOAD-FAIL]` - remote model download failed.
```

- [ ] **Step 4: Run logging contract tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter RouterLoggingContractTests
```

Expected: PASS.

- [ ] **Step 5: Commit logging contract**

Run:

```powershell
git -C E:\src\CodebrewRouter add Blaze.LlmGateway.LocalInference/LocalModelLog.cs Docs/engineering/logging-contract.md Blaze.LlmGateway.Tests/RouterLoggingContractTests.cs
git -C E:\src\CodebrewRouter commit -m "test: cover local model logging contract"
```

---

### Task 6: Final Verification And Offline Configuration Notes

**Files:**
- Verify: `Blaze.LlmGateway.Api/appsettings.LocalInference.json`
- Verify: `Blaze.LlmGateway.AppHost/appsettings.json`
- Verify: `Docs/superpowers/plans/2026-05-07-offline-gemma-warmup-aspire-readiness.md`

- [ ] **Step 1: Keep checked-in defaults non-crashing when no model is configured**

Verify base checked-in config still has:

```json
"ModelPath": "",
"BlockStartupUntilWarm": false
```

in `Blaze.LlmGateway.AppHost/appsettings.json` so local development without a GGUF can start and report unhealthy readiness instead of crashing.

- [ ] **Step 2: Record the real offline model URL for local use**

Use this local developer config value when you want startup to download Gemma 4 Q4_K_M:

```json
"ModelPath": "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
"CacheDirectory": "E:\\models\\codebrewrouter",
"WarmupEnabled": true,
"BlockStartupUntilWarm": true,
"DownloadTimeoutSeconds": 3600,
"WarmupTimeoutSeconds": 120
```

Do not commit a machine-specific secret or private path if the repo uses a local-only config file for developer overrides.

- [ ] **Step 3: Run focused local inference tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter "FullyQualifiedName~LocalInference|FullyQualifiedName~LocalGemma"
```

Expected: PASS.

- [ ] **Step 4: Run AppHost and logging tests**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter "AppHostCompositionTests|RouterLoggingContractTests"
```

Expected: PASS.

- [ ] **Step 5: Run full test suite**

Run:

```powershell
dotnet test E:\src\CodebrewRouter\Blaze.LlmGateway.slnx
```

Expected: PASS with only intentionally skipped tests.

- [ ] **Step 6: Run warn-as-error build**

Run:

```powershell
dotnet build E:\src\CodebrewRouter\Blaze.LlmGateway.slnx -warnaserror
```

Expected: PASS with 0 warnings and 0 errors.

- [ ] **Step 7: Inspect final diff**

Run:

```powershell
git -C E:\src\CodebrewRouter status --short
git -C E:\src\CodebrewRouter diff --stat
```

Expected: only intended provider-level Gemma download, warmup readiness, Aspire gating, tests, docs, and this refreshed plan are changed. Existing unrelated `.claude/worktrees/admiring-mestorf-6edc93` state remains untouched.

- [ ] **Step 8: Commit final verification notes if this plan changed during implementation**

Run only if the implementation required changes to this plan:

```powershell
git -C E:\src\CodebrewRouter add Docs/superpowers/plans/2026-05-07-offline-gemma-warmup-aspire-readiness.md
git -C E:\src\CodebrewRouter commit -m "docs: refresh offline gemma provider download plan"
```
