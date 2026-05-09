using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Blaze.LlmGateway.Tests.LocalInference;

public sealed class LocalGemmaChatClientProviderTests
{
    [Fact]
    public async Task EnsureLoadedAsync_WhenDisabled_ThrowsImmediatelyWithoutCallingProvider()
    {
        var provider = new Mock<IModelDistributionProvider>(MockBehavior.Strict);
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = false, ModelPath = "https://hf.co/model.gguf" },
            provider);

        var act = () => client.EnsureLoadedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenModelPathEmpty_ThrowsImmediatelyWithoutCallingProvider()
    {
        var provider = new Mock<IModelDistributionProvider>(MockBehavior.Strict);
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = "" },
            provider);

        var act = () => client.EnsureLoadedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ModelPath*");
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenLocalPath_CallsProviderWithLocalPathAndLoadsResolvedPath()
    {
        var localPath = CreateTempModelPath();
        var resolvedPath = CreateTempModelPath();
        var loadedPaths = new List<string>();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedPath);
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = localPath },
            provider,
            loadedPaths);

        try
        {
            await client.EnsureLoadedAsync();

            client.IsModelLoaded.Should().BeTrue();
            client.ModelPath.Should().Be(resolvedPath);
            loadedPaths.Should().Equal(resolvedPath);
            provider.Verify(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(localPath);
            File.Delete(resolvedPath);
        }
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenRemoteUrlAndCacheHit_UsesCachedPathWithoutDownloading()
    {
        const string remoteUrl = "https://huggingface.co/lm-kit/model.lmk";
        var cachedPath = CreateTempModelPath();
        var loadedPaths = new List<string>();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.GetCachedModelPathAsync(remoteUrl))
            .ReturnsAsync(cachedPath);
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = remoteUrl },
            provider,
            loadedPaths);

        try
        {
            await client.EnsureLoadedAsync();

            client.IsModelLoaded.Should().BeTrue();
            client.ModelPath.Should().Be(cachedPath);
            loadedPaths.Should().Equal(cachedPath);
            provider.Verify(p => p.GetCachedModelPathAsync(remoteUrl), Times.Once);
            provider.Verify(p => p.EnsureModelAvailableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(cachedPath);
        }
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenRemoteUrlAndNoCacheHit_DownloadsOnceAndLoadsResolvedPath()
    {
        const string remoteUrl = "https://huggingface.co/lm-kit/gemma-4-e4b-instruct-lmk/resolve/main/Gemma-4-E4B-It-7.5B-Q4_K_M.lmk";
        var downloadedPath = CreateTempModelPath();
        var loadedPaths = new List<string>();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.GetCachedModelPathAsync(remoteUrl))
            .ReturnsAsync((string?)null);
        provider
            .Setup(p => p.EnsureModelAvailableAsync(remoteUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadedPath);
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = remoteUrl },
            provider,
            loadedPaths);

        try
        {
            await client.EnsureLoadedAsync();

            client.IsModelLoaded.Should().BeTrue();
            client.ModelPath.Should().Be(downloadedPath);
            loadedPaths.Should().Equal(downloadedPath);
            provider.Verify(p => p.GetCachedModelPathAsync(remoteUrl), Times.Once);
            provider.Verify(p => p.EnsureModelAvailableAsync(remoteUrl, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(downloadedPath);
        }
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenProviderThrowsOnDownload_PropagatesException()
    {
        const string remoteUrl = "https://huggingface.co/model.lmk";
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.GetCachedModelPathAsync(remoteUrl))
            .ReturnsAsync((string?)null);
        provider
            .Setup(p => p.EnsureModelAvailableAsync(remoteUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("circuit breaker open"));
        var client = CreateClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = remoteUrl },
            provider);

        var act = () => client.EnsureLoadedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*circuit breaker*");
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenNoProviderAndFileNotFound_ThrowsConfigurationError()
    {
        var client = new LocalGemmaChatClient("/nonexistent/path/gemma4.lmk");

        var act = () => client.EnsureLoadedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*LocalGemma*model file*not found*");
    }

    [Fact]
    public async Task EnsureLoadedAsync_InvokesOnModelFileReadyCallbackBeforeRuntimeFactory()
    {
        var localPath = CreateTempModelPath();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPath);
        var callbackInvoked = false;
        var callbackWasInvokedBeforeFactory = false;
        var client = new LocalGemmaChatClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = localPath },
            provider.Object,
            logger: null,
            runtimeFactory: (_, _) =>
            {
                callbackWasInvokedBeforeFactory = callbackInvoked;
                return new FakeRuntime();
            });

        try
        {
            await client.EnsureLoadedAsync(onModelFileReady: () => callbackInvoked = true);

            callbackWasInvokedBeforeFactory.Should().BeTrue("warmup state must advance to Loading before runtime load");
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LoadsThenDelegatesToRuntime()
    {
        var localPath = CreateTempModelPath();
        var runtime = new FakeRuntime();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPath);
        var client = new LocalGemmaChatClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = localPath },
            provider.Object,
            logger: null,
            runtimeFactory: (_, _) => runtime);

        var updates = new List<ChatResponseUpdate>();
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                updates.Add(update);
            }

            runtime.StreamingCalls.Should().Be(1);
            updates.Should().ContainSingle().Which.Text.Should().Be("ok");
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenCalledConcurrently_CreatesSingleRuntime()
    {
        var localPath = CreateTempModelPath();
        var provider = new Mock<IModelDistributionProvider>();
        provider
            .Setup(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPath);
        var runtimeFactoryCalls = 0;
        var client = new LocalGemmaChatClient(
            new LocalInferenceOptions { Enabled = true, ModelPath = localPath },
            provider.Object,
            logger: null,
            runtimeFactory: (_, _) =>
            {
                Interlocked.Increment(ref runtimeFactoryCalls);
                return new FakeRuntime();
            });

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.EnsureLoadedAsync()));

            runtimeFactoryCalls.Should().Be(1);
            provider.Verify(p => p.EnsureModelAvailableAsync(localPath, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    private static LocalGemmaChatClient CreateClient(
        LocalInferenceOptions options,
        Mock<IModelDistributionProvider> provider,
        List<string>? loadedPaths = null,
        Func<LocalInferenceOptions, string, ILocalGemmaRuntime>? runtimeFactory = null)
        => new(
            options,
            provider.Object,
            logger: null,
            runtimeFactory: runtimeFactory ?? new Func<LocalInferenceOptions, string, ILocalGemmaRuntime>((_, path) =>
            {
                loadedPaths?.Add(path);
                return new FakeRuntime();
            }));

    private static string CreateTempModelPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lmk");
        File.WriteAllText(path, "model");
        return path;
    }

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
}
