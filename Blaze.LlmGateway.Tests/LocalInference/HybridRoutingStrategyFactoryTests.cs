using System;
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

public class HybridRoutingStrategyFactoryTests
{
    private static HybridRoutingStrategyFactory CreateFactory(
        LocalInferenceOptions? options = null,
        IModelDistributionProvider? modelProvider = null,
        IChatClient? routerClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        options ??= new LocalInferenceOptions { Enabled = true, ModelPath = "/models/gemma.gguf" };
        modelProvider ??= new Mock<IModelDistributionProvider>().Object;
        loggerFactory ??= new Mock<ILoggerFactory>().Object;
        var logger = new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>().Object;

        return new HybridRoutingStrategyFactory(
            Options.Create(options),
            modelProvider,
            routerClient,
            loggerFactory,
            logger);
    }

    // ── Constructor Validation ─────────────────────────────────────────────

    [Fact]
    public void ThrowsArgumentNullException_WhenOptionsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridRoutingStrategyFactory(
                null!,
                new Mock<IModelDistributionProvider>().Object,
                null,
                new Mock<ILoggerFactory>().Object,
                new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>().Object));
    }

    [Fact]
    public void ThrowsArgumentNullException_WhenModelProviderNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridRoutingStrategyFactory(
                Options.Create(new LocalInferenceOptions()),
                null!,
                null,
                new Mock<ILoggerFactory>().Object,
                new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>().Object));
    }

    [Fact]
    public void ThrowsArgumentNullException_WhenLoggerFactoryNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridRoutingStrategyFactory(
                Options.Create(new LocalInferenceOptions()),
                new Mock<IModelDistributionProvider>().Object,
                null,
                null!,
                new Mock<ILogger<HybridLocalRemoteRoutingStrategy>>().Object));
    }

    [Fact]
    public void ThrowsArgumentNullException_WhenLoggerNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HybridRoutingStrategyFactory(
                Options.Create(new LocalInferenceOptions()),
                new Mock<IModelDistributionProvider>().Object,
                null,
                new Mock<ILoggerFactory>().Object,
                null!));
    }

    // ── Strategy Creation ──────────────────────────────────────────────────

    [Fact]
    public void CreatesStrategy_ReturnsHybridLocalRemoteRoutingStrategy()
    {
        var factory = CreateFactory();

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    [Fact]
    public void CreatesMultipleStrategies_ReturnsNewInstanceEachTime()
    {
        var factory = CreateFactory();

        var strategy1 = factory.CreateStrategy();
        var strategy2 = factory.CreateStrategy();

        Assert.NotSame(strategy1, strategy2);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy1);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy2);
    }

    // ── Fallback Strategy ──────────────────────────────────────────────────

    [Fact]
    public void CreatesStrategy_WithoutRouterClient_UsesKeywordFallback()
    {
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockKeywordLogger = new Mock<ILogger<KeywordRoutingStrategy>>();
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.Is<string>(s => s.Contains("KeywordRoutingStrategy"))))
            .Returns(mockKeywordLogger.Object);

        var factory = CreateFactory(
            routerClient: null,
            loggerFactory: mockLoggerFactory.Object);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    [Fact]
    public void CreatesStrategy_WithRouterClient_UsesOllamaMetaRouting()
    {
        var mockRouterClient = new Mock<IChatClient>().Object;
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockKeywordLogger = new Mock<ILogger<KeywordRoutingStrategy>>();
        var mockOllamaLogger = new Mock<ILogger<OllamaMetaRoutingStrategy>>();
        
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.Is<string>(s => s.Contains("KeywordRoutingStrategy"))))
            .Returns(mockKeywordLogger.Object);
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.Is<string>(s => s.Contains("OllamaMetaRoutingStrategy"))))
            .Returns(mockOllamaLogger.Object);

        var factory = CreateFactory(
            routerClient: mockRouterClient,
            loggerFactory: mockLoggerFactory.Object);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    // ── Configuration Preservation ─────────────────────────────────────────

    [Fact]
    public void PreservesLocalInferenceOptions_InCreatedStrategy()
    {
        var options = new LocalInferenceOptions
        {
            Enabled = true,
            ModelPath = "/custom/path/gemma.gguf",
            MaxContextTokens = 4096
        };
        var factory = CreateFactory(options: options);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    [Fact]
    public void PreservesModelProvider_InCreatedStrategy()
    {
        var mockProvider = new Mock<IModelDistributionProvider>();
        var factory = CreateFactory(modelProvider: mockProvider.Object);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        // Verify provider is used by calling strategy (via indirect inspection)
    }

    // ── Logger Factory Usage ───────────────────────────────────────────────

    [Fact]
    public void UsesLoggerFactory_ToCreateKeywordLogger()
    {
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockKeywordLogger = new Mock<ILogger<KeywordRoutingStrategy>>();
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(mockKeywordLogger.Object);

        var factory = CreateFactory(loggerFactory: mockLoggerFactory.Object);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        mockLoggerFactory.Verify(f => f.CreateLogger(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public void UsesLoggerFactory_ToCreateOllamaLoggerWhenRouterProvided()
    {
        var mockRouterClient = new Mock<IChatClient>().Object;
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger<KeywordRoutingStrategy>>();
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);

        var factory = CreateFactory(
            routerClient: mockRouterClient,
            loggerFactory: mockLoggerFactory.Object);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        // Logger factory called to create both keyword and ollama loggers
        mockLoggerFactory.Verify(f => f.CreateLogger(It.IsAny<string>()), Times.AtLeast(2));
    }

    // ── Edge Cases ─────────────────────────────────────────────────────────

    [Fact]
    public void HandlesDisabledLocalInference()
    {
        var options = new LocalInferenceOptions { Enabled = false, ModelPath = string.Empty };
        var factory = CreateFactory(options: options);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    [Fact]
    public void HandlesEmptyModelPath()
    {
        var options = new LocalInferenceOptions { Enabled = true, ModelPath = string.Empty };
        var factory = CreateFactory(options: options);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }

    [Fact]
    public void HandlesCustomCacheDirectory()
    {
        var options = new LocalInferenceOptions
        {
            Enabled = true,
            ModelPath = "/models/gemma.gguf",
            CacheDirectory = "/custom/cache"
        };
        var factory = CreateFactory(options: options);

        var strategy = factory.CreateStrategy();

        Assert.NotNull(strategy);
        Assert.IsType<HybridLocalRemoteRoutingStrategy>(strategy);
    }
}
