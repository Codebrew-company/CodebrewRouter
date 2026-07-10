using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.LocalInference;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.LocalInference;

public class HybridLocalRemoteRoutingStrategyTests
{
    private static HybridLocalRemoteRoutingStrategy CreateStrategy(
        LocalInferenceOptions? options = null,
        IModelDistributionProvider? modelProvider = null,
        IRoutingStrategy? fallbackStrategy = null,
        ILogger<HybridLocalRemoteRoutingStrategy>? logger = null)
    {
        options ??= new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        modelProvider ??= new Mock<IModelDistributionProvider>().Object;
        fallbackStrategy ??= new Mock<IRoutingStrategy>().Object;
        logger ??= new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>().Object;

        return new HybridLocalRemoteRoutingStrategy(options, modelProvider, fallbackStrategy, logger);
    }

    private static List<ChatMessage> UserMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    // ── Local Model Available ──────────────────────────────────────────────

    [Fact]
    public async Task RoutesToLocalGemma_WhenEnabledAndModelAvailable()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/models/gemma-cached.gguf");

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.LocalGemma, result);
        mockProvider.Verify(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenLocalDisabled()
    {
        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = false, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
        mockFallback.Verify(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenModelPathNullOrEmpty()
    {
        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = string.Empty };
        var strategy = CreateStrategy(options, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
    }

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenProviderReturnsNull()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
    }

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenProviderReturnsEmptyString()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
    }

    // ── Provider Exceptions ────────────────────────────────────────────────

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenProviderThrowsException()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model provider error"));

        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
        mockFallback.Verify(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FallsBackToRemoteProvider_WhenProviderThrowsIOException()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("Cache directory inaccessible"));

        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task PropagatesCancellation_WhenProviderCancelled()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Provider was cancelled"));

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => strategy.ResolveAsync(UserMessages("Hello"), cts.Token));
    }

    // ── Message Handling ────────────────────────────────────────────────────

    [Fact]
    public async Task HandlesEmptyMessageList_RoutsToLocal_IfAvailable()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/models/gemma-cached.gguf");

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object);

        var result = await strategy.ResolveAsync([], CancellationToken.None);

        Assert.Equal(RouteDestination.LocalGemma, result);
    }

    [Fact]
    public async Task HandlesNullMessages_RoutesToFallback()
    {
        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = false };
        var strategy = CreateStrategy(options, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync([], CancellationToken.None);

        Assert.Equal(RouteDestination.OllamaRouter, result);
    }

    // ── Unexpected Errors ──────────────────────────────────────────────────

    [Fact]
    public async Task HandlesUnexpectedException_FallsBackToRemote()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RouteDestination.OllamaRouter);

        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = CreateStrategy(options, modelProvider: mockProvider.Object, fallbackStrategy: mockFallback.Object);

        var result = await strategy.ResolveAsync(UserMessages("Hello"));

        Assert.Equal(RouteDestination.OllamaRouter, result);
        mockFallback.Verify(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandlesUnexpectedExceptionInFallback_RaisesError()
    {
        var mockFallback = new Mock<IRoutingStrategy>();
        mockFallback
            .Setup(s => s.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fallback failed"));

        var options = new LocalInferenceOptions { Enabled = false };
        var strategy = CreateStrategy(options, fallbackStrategy: mockFallback.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.ResolveAsync(UserMessages("Hello")));
    }

    // ── Logging ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogsDebugMessage_WhenRoutingToLocalGemma()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/models/gemma-cached.gguf");

        var mockLogger = new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>();
        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = new HybridLocalRemoteRoutingStrategy(
            options,
            mockProvider.Object,
            new Mock<IRoutingStrategy>().Object,
            mockLogger.Object);

        await strategy.ResolveAsync(UserMessages("Hello"));

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Local Gemma model available")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogsWarningMessage_WhenProviderFails()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        mockProvider
            .Setup(p => p.EnsureModelAvailableAsync("/models/gemma.gguf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        var mockLogger = new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>();
        var options = new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        var strategy = new HybridLocalRemoteRoutingStrategy(
            options,
            mockProvider.Object,
            new Mock<IRoutingStrategy>().Object,
            mockLogger.Object);

        await strategy.ResolveAsync(UserMessages("Hello"));

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to verify")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
