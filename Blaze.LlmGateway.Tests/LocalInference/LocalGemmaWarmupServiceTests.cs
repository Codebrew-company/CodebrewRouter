using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests.LocalInference;

public sealed class LocalGemmaWarmupServiceTests
{
    [Fact]
    public async Task StartAsync_WhenLocalInferenceDisabled_SkipsWarmup()
    {
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = false,
                WarmupEnabled = true,
                BlockStartupUntilWarm = true
            },
            state,
            logger);

        await service.StartAsync(CancellationToken.None);

        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Skipped);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.SkipTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenModelPathMissingAndStartupDoesNotBlock_MarksFailedForReadiness()
    {
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = "",
                BlockStartupUntilWarm = false
            },
            state,
            logger);

        await service.StartAsync(CancellationToken.None);

        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.FailTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenModelPathMissingAndStartupBlocks_ThrowsAndMarksFailed()
    {
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = "",
                BlockStartupUntilWarm = true
            },
            state,
            logger);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.FailTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenModelLoaded_PrimesOneTokenAndMarksReady()
    {
        var fakeClient = new FakeWarmupChatClient();
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                WarmupPrompt = "ready",
                WarmupMaxOutputTokens = 1,
                WarmupTimeoutSeconds = 5,
                BlockStartupUntilWarm = true
            },
            state,
            logger,
            fakeClient);

        await service.StartAsync(CancellationToken.None);

        fakeClient.StreamingCalls.Should().Be(1);
        fakeClient.LastMessages.Should().ContainSingle()
            .Which.Text.Should().Be("ready");
        fakeClient.LastOptions.Should().NotBeNull();
        fakeClient.LastOptions!.MaxOutputTokens.Should().Be(1);
        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Ready);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.ReadyTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenPrimingTimesOutAndStartupDoesNotBlock_MarksFailedWithoutThrowing()
    {
        var fakeClient = new FakeWarmupChatClient { HangUntilCancelled = true };
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                WarmupTimeoutSeconds = 0,
                BlockStartupUntilWarm = false
            },
            state,
            logger,
            fakeClient);

        await service.StartAsync(CancellationToken.None);

        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.FailTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenPrimingFailsAndStartupBlocks_Throws()
    {
        var fakeClient = new FakeWarmupChatClient
        {
            StreamingException = new InvalidOperationException("warmup failed")
        };
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                BlockStartupUntilWarm = true
            },
            state,
            logger,
            fakeClient);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsWarmupStatusForAspireReadiness()
    {
        var state = new LocalGemmaWarmupState();

        var initial = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        initial.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        initial.Data["status"].Should().Be(LocalGemmaWarmupStatus.NotStarted.ToString());

        state.Update(LocalGemmaWarmupStatus.Downloading, "https://hf.co/model.gguf", "downloading", TimeSpan.FromMilliseconds(1));
        var downloading = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        downloading.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        downloading.Data["status"].Should().Be(LocalGemmaWarmupStatus.Downloading.ToString());

        state.Update(LocalGemmaWarmupStatus.Loading, "C:/models/gemma4.gguf", "loading", TimeSpan.FromMilliseconds(1));
        var loading = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        loading.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);

        state.Update(LocalGemmaWarmupStatus.Priming, "C:/models/gemma4.gguf", "priming", TimeSpan.FromMilliseconds(2));
        var priming = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        priming.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);

        state.Update(LocalGemmaWarmupStatus.Ready, "C:/models/gemma4.gguf", "ready", TimeSpan.FromMilliseconds(12));
        var ready = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        ready.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
        ready.Data["status"].Should().Be(LocalGemmaWarmupStatus.Ready.ToString());

        state.Update(LocalGemmaWarmupStatus.Skipped, "C:/models/gemma4.gguf", "disabled", TimeSpan.FromMilliseconds(2));
        var skipped = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        skipped.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);

        state.Update(LocalGemmaWarmupStatus.Failed, "C:/models/gemma4.gguf", "failed", TimeSpan.FromMilliseconds(3));
        var failed = await state.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        failed.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        failed.Data["status"].Should().Be(LocalGemmaWarmupStatus.Failed.ToString());
    }

    [Fact]
    public async Task StartAsync_WhenModelLoaded_CallsEnsureLoadedAsyncBeforePrime()
    {
        var fakeClient = new FakeWarmupChatClient();
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                WarmupPrompt = "ready",
                WarmupMaxOutputTokens = 1,
                WarmupTimeoutSeconds = 5,
                BlockStartupUntilWarm = true
            },
            state,
            logger,
            fakeClient);

        await service.StartAsync(CancellationToken.None);

        fakeClient.EnsureLoadedCalls.Should().Be(1);
        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Ready);
    }

    [Fact]
    public async Task StartAsync_WhenEnsureLoadedAsyncFailsAndStartupDoesNotBlock_MarksFailedWithoutThrowing()
    {
        var fakeClient = new FakeWarmupChatClient
        {
            EnsureLoadedAsyncException = new InvalidOperationException("download failed")
        };
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                BlockStartupUntilWarm = false
            },
            state,
            logger,
            fakeClient);

        await service.StartAsync(CancellationToken.None);

        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
        logger.Messages.Should().Contain(message => message.StartsWith(LocalWarmupLog.FailTag, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenEnsureLoadedAsyncFailsAndStartupBlocks_Throws()
    {
        var fakeClient = new FakeWarmupChatClient
        {
            EnsureLoadedAsyncException = new InvalidOperationException("download failed")
        };
        var state = new LocalGemmaWarmupState();
        var logger = new CapturingLogger<LocalGemmaWarmupService>();
        var service = CreateService(
            new LocalInferenceOptions
            {
                Enabled = true,
                WarmupEnabled = true,
                ModelPath = fakeClient.ModelPath!,
                BlockStartupUntilWarm = true
            },
            state,
            logger,
            fakeClient);

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*download failed*");
        state.Snapshot.Status.Should().Be(LocalGemmaWarmupStatus.Failed);
    }

    private static LocalGemmaWarmupService CreateService(
        LocalInferenceOptions options,
        LocalGemmaWarmupState state,
        ILogger<LocalGemmaWarmupService> logger,
        IChatClient? client = null)
    {
        var services = new ServiceCollection();
        if (client is not null)
        {
            services.AddKeyedSingleton("LocalGemma", client);
        }

        var provider = services.BuildServiceProvider();
        return new LocalGemmaWarmupService(provider, Options.Create(options), state, logger);
    }

    private sealed class FakeWarmupChatClient : IChatClient, ILocalGemmaModelState
    {
        public string? ModelPath { get; init; } = "C:/models/gemma-4-e4b-it-q4_k_m.gguf";

        public bool IsModelLoaded { get; init; } = true;

        public int StreamingCalls { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public bool HangUntilCancelled { get; init; }

        public Exception? StreamingException { get; init; }

        public Exception? EnsureLoadedAsyncException { get; init; }

        public int EnsureLoadedCalls { get; private set; }

        public Task EnsureLoadedAsync(
            CancellationToken cancellationToken = default,
            Action? onModelFileReady = null)
        {
            EnsureLoadedCalls++;
            if (EnsureLoadedAsyncException is not null) throw EnsureLoadedAsyncException;
            onModelFileReady?.Invoke();
            return Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            LastMessages = chatMessages.ToArray();
            LastOptions = options;

            if (StreamingException is not null)
            {
                throw StreamingException;
            }

            if (HangUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
