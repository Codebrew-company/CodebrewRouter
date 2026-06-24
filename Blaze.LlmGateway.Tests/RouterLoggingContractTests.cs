using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Routing;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests;

public sealed class RouterLoggingContractTests
{
    public static TheoryData<object, string, LogLevel> RouterEvents =>
        new()
        {
            { new RouterStartEvent(2), "[ROUTER-START]", LogLevel.Information },
            { new RouterCleanEvent(100, 80, 12), "[ROUTER-CLEAN]", LogLevel.Information },
            { new RouterResolveEvent("General", 42, 1, "LocalGemma", 3), "[ROUTER-RESOLVE]", LogLevel.Information },
            { new RouterContextBudgetEvent(1, "LocalGemma", "local-gemma", 42, 1024, 4096), "[ROUTER-CONTEXT]", LogLevel.Debug },
            { new RouterCompactEvent(1, "LocalGemma", 1200, 900), "[ROUTER-COMPACT]", LogLevel.Information },
            { new RouterSkipEvent(1, "LocalGemma", "local-gemma", 5000, 4096, "context_too_large"), "[ROUTER-SKIP]", LogLevel.Warning },
            { new RouterTryEvent(1, 1, "LocalGemma", "local-gemma", "General"), "[ROUTER-TRY]", LogLevel.Information },
            { new RouterProbeEvent(1, "LocalGemma", "local-gemma", 25, true), "[ROUTER-PROBE]", LogLevel.Information },
            { new RouterSuccessEvent(1, "LocalGemma", "local-gemma", "General", "Stop", 12, 4, 75), "[ROUTER-SUCCESS]", LogLevel.Information },
            { new RouterFailEvent(1, "LocalGemma", "local-gemma", "failed"), "[ROUTER-FAIL]", LogLevel.Warning },
            { new RouterExhaustedEvent(1, "General", "LocalGemma"), "[ROUTER-EXHAUSTED]", LogLevel.Warning },
            { new RouterMidstreamFailEvent("LocalGemma", "local-gemma"), "[ROUTER-MIDSTREAM-FAIL]", LogLevel.Warning },
            { new RouterStreamCompleteEvent(3, "LocalGemma", "local-gemma", "General", 100), "[ROUTER-STREAM-COMPLETE]", LogLevel.Information },
            { new RouterSelectEvent("auto", "General", "auto-fallback"), "[ROUTER-SELECT]", LogLevel.Information },
            { new RouterDeployEvent("hermes-yardly", "primary pool", 3), "[ROUTER-DEPLOY]", LogLevel.Information },
            { new RouterHealthEvent("hermes-lead", "healthy", "passed all filters"), "[ROUTER-HEALTH]", LogLevel.Information },
            { new RouterFallbackEvent("OpenCodeGo", "LocalGemma", "timeout", 2), "[ROUTER-FALLBACK]", LogLevel.Information },
            { new RouterFusionEvent(["model-a", "model-b"], "model-a"), "[ROUTER-FUSION]", LogLevel.Information },
            { new RouterFusionResultEvent("model-a", "fastest response"), "[ROUTER-FUSION-RESULT]", LogLevel.Information },
        };

    public static TheoryData<string> LocalWarmupTags =>
        new()
        {
            LocalWarmupLog.StartTag,
            LocalWarmupLog.LoadTag,
            LocalWarmupLog.PrimeTag,
            LocalWarmupLog.ReadyTag,
            LocalWarmupLog.SkipTag,
            LocalWarmupLog.FailTag
        };

    public static TheoryData<string> LocalModelTags =>
        new()
        {
            LocalModelLog.ResolveTag,
            LocalModelLog.CacheHitTag,
            LocalModelLog.DownloadStartTag,
            LocalModelLog.DownloadReadyTag,
            LocalModelLog.DownloadFailTag
        };

    [Theory]
    [MemberData(nameof(RouterEvents))]
    public void Write_FormatsRouterEventWithExactTagAndDefaultLevel(
        object routerEvent,
        string expectedTag,
        LogLevel expectedLevel)
    {
        var logger = new CapturingLogger();

        RouterLog.Write(logger, routerEvent);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(expectedLevel);
        entry.Message.Should().StartWith(expectedTag);
        entry.Message.Should().Contain(routerEvent.GetType().Name);
    }

    [Theory]
    [MemberData(nameof(LocalWarmupTags))]
    public void LocalWarmupTags_DoNotUseRouterNamespace(string tag)
    {
        tag.Should().StartWith("[LOCAL-WARMUP-");
        tag.Should().NotStartWith("[ROUTER-");
    }

    [Theory]
    [MemberData(nameof(LocalModelTags))]
    public void LocalModelTags_DoNotUseRouterNamespace(string tag)
    {
        tag.Should().StartWith("[LOCAL-MODEL-");
        tag.Should().NotStartWith("[ROUTER-");
    }

    [Fact]
    public void LocalWarmupTags_AreDocumentedInLoggingContract()
    {
        var root = FindRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(root, "Docs", "engineering", "logging-contract.md"));

        var tags = new[]
        {
            LocalWarmupLog.StartTag,
            LocalWarmupLog.LoadTag,
            LocalWarmupLog.PrimeTag,
            LocalWarmupLog.ReadyTag,
            LocalWarmupLog.SkipTag,
            LocalWarmupLog.FailTag
        };

        foreach (var tag in tags)
        {
            contract.Should().Contain(tag);
        }
    }

    [Fact]
    public void LocalModelTags_AreDocumentedInLoggingContract()
    {
        var root = FindRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(root, "Docs", "engineering", "logging-contract.md"));

        var tags = new[]
        {
            LocalModelLog.ResolveTag,
            LocalModelLog.CacheHitTag,
            LocalModelLog.DownloadStartTag,
            LocalModelLog.DownloadReadyTag,
            LocalModelLog.DownloadFailTag
        };

        foreach (var tag in tags)
        {
            contract.Should().Contain(tag);
        }
    }

    [Fact]
    public async Task CodebrewRouterStreaming_EmitsLifecycleTags()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", new SingleChunkChatClient());
        var serviceProvider = services.BuildServiceProvider();

        var logger = new CapturingLogger<CodebrewRouterChatClient>();
        var client = new CodebrewRouterChatClient(
            new SingleChunkChatClient(),
            new FixedTaskClassifier(TaskType.General),
            new NoopPromptCleaner(),
            new NoopContextCompactor(),
            new FixedTokenCounter(42),
            Options.Create(new CodebrewRouterOptions
            {
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }),
            Options.Create(new LlmGatewayOptions()),
            new AlwaysAvailableRegistry(),
            serviceProvider,
            logger);

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello")
        };

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: CancellationToken.None))
        {
        }

        var messagesByTag = logger.Entries.Select(entry => entry.Message).ToArray();
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-START]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-CLEAN]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-RESOLVE]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-TRY]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-PROBE]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-SUCCESS]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-STREAM-COMPLETE]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCompletionsEndpointNonStreaming_EmitsRouterTagsForDirectProvider()
    {
        var logger = new CapturingLogger<ChatCompletionRequest>();
        var services = new ServiceCollection()
            .AddSingleton<ILogger<ChatCompletionRequest>>(logger)
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        var selectedClient = new SingleChunkChatClient();
        var request = new ChatCompletionRequest(
            "codebrewRouter",
            [new ChatMessageDto("user", "hello")],
            Stream: false);

        await ChatCompletionsEndpoint.HandleAsync(
            request,
            new SingleChunkChatClient(),
            new FixedModelSelectionResolver(selectedClient),
            Options.Create(new LlmGatewayOptions()),
            httpContext,
            CancellationToken.None);

        var messagesByTag = logger.Entries.Select(entry => entry.Message).ToArray();
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-START]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-RESOLVE]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-TRY]", StringComparison.Ordinal));
        messagesByTag.Should().Contain(message => message.StartsWith("[ROUTER-SUCCESS]", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentArtifacts_ReferenceRouterAndAgentLoggingContract()
    {
        var root = FindRepositoryRoot();
        var contractPath = Path.Combine(root, "Docs", "engineering", "logging-contract.md");
        var contract = File.ReadAllText(contractPath);

        foreach (var tag in RouterEvents.Select(row => (string)row[1]))
        {
            contract.Should().Contain(tag);
        }

        var agentTags = new[]
        {
            "[AGENT-START]",
            "[AGENT-PLAN]",
            "[AGENT-ACTION]",
            "[AGENT-HANDOFF]",
            "[AGENT-RESULT]",
            "[AGENT-FAIL]",
            "[AGENT-COMPLETE]"
        };
        foreach (var tag in agentTags)
        {
            contract.Should().Contain(tag);
        }

        var artifacts = new[]
        {
            Path.Combine(root, "AGENTS.md"),
            Path.Combine(root, ".agents", "skills", "codebrewrouter-logging-contract", "SKILL.md"),
            Path.Combine(root, ".github", "agents", "codebrewrouter-logging.agent.md"),
            Path.Combine(root, ".opencode", "agents", "codebrewrouter-logging.md"),
            Path.Combine(root, ".opencode", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, ".claude", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, ".github", "plugins", "codebrewrouter-logging", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, "plugins", "codebrewrouter-logging", "commands", "codebrewrouter-logging.md"),
        };

        foreach (var artifact in artifacts)
        {
            File.Exists(artifact).Should().BeTrue($"{artifact} should exist");
            File.ReadAllText(artifact).Should().Contain("Docs/engineering/logging-contract.md");
        }

        artifacts.Should().OnlyContain(artifact => Path.GetFullPath(artifact).StartsWith(root, StringComparison.OrdinalIgnoreCase));
        File.ReadAllText(Path.Combine(root, ".agents", "skills", "codebrewrouter-logging-contract", "SKILL.md"))
            .Should().Contain("project-level only");
        File.ReadAllText(Path.Combine(root, ".github", "agents", "codebrewrouter-logging.agent.md"))
            .Should().Contain("project-level only");
        File.ReadAllText(Path.Combine(root, ".opencode", "agents", "codebrewrouter-logging.md"))
            .Should().Contain("project-level only");
    }

    [Fact]
    public void CommandArtifacts_AreProjectLocalAndExposeLoggingGuardianCommand()
    {
        var root = FindRepositoryRoot();
        var commandArtifacts = new[]
        {
            Path.Combine(root, ".opencode", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, ".claude", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, ".github", "plugins", "codebrewrouter-logging", "commands", "codebrewrouter-logging.md"),
            Path.Combine(root, "plugins", "codebrewrouter-logging", "commands", "codebrewrouter-logging.md"),
        };

        foreach (var commandArtifact in commandArtifacts)
        {
            File.Exists(commandArtifact).Should().BeTrue($"{commandArtifact} should exist");
            var command = File.ReadAllText(commandArtifact);
            command.Should().Contain("project-level only");
            command.Should().Contain("Docs/engineering/logging-contract.md");
            command.Should().Contain("/codebrewrouter-logging");
        }

        File.ReadAllText(Path.Combine(root, ".opencode", "commands", "codebrewrouter-logging.md"))
            .Should().Contain("agent: codebrewrouter-logging");

        var copilotPluginManifest = Path.Combine(root, ".github", "plugins", "codebrewrouter-logging", "plugin.json");
        File.Exists(copilotPluginManifest).Should().BeTrue($"{copilotPluginManifest} should exist");
        var copilotPlugin = File.ReadAllText(copilotPluginManifest);
        copilotPlugin.Should().Contain("\"name\": \"codebrewrouter-logging\"");
        copilotPlugin.Should().Contain("\"commands\": \"commands/\"");
        copilotPlugin.Should().Contain("\"agents\": \"agents/\"");

        var codexPluginManifest = Path.Combine(root, "plugins", "codebrewrouter-logging", ".codex-plugin", "plugin.json");
        File.Exists(codexPluginManifest).Should().BeTrue($"{codexPluginManifest} should exist");
        File.ReadAllText(codexPluginManifest)
            .Should().Contain("\"name\": \"codebrewrouter-logging\"");

        var codexMarketplace = Path.Combine(root, ".agents", "plugins", "marketplace.json");
        File.Exists(codexMarketplace).Should().BeTrue($"{codexMarketplace} should exist");
        File.ReadAllText(codexMarketplace)
            .Should().Contain("./plugins/codebrewrouter-logging");

        commandArtifacts.Should().OnlyContain(
            commandArtifact => Path.GetFullPath(commandArtifact).StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

    private class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

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
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class SingleChunkChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok")
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTaskClassifier(TaskType taskType) : ITaskClassifier
    {
        public Task<TaskType> ClassifyAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
            => Task.FromResult(taskType);
    }

    private sealed class FixedTokenCounter(int tokenCount) : ITokenCounter
    {
        public int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null) => tokenCount;
    }

    private sealed class AlwaysAvailableRegistry : IModelAvailabilityRegistry
    {
        public IReadOnlyList<AvailableModel> GetModels(bool includeUnavailable = false) => [];

        public AvailableModel? FindModel(string modelId, bool includeUnavailable = false) => null;

        public bool IsProviderAvailable(string provider) => true;

        public string? GetProviderError(string provider) => null;
    }

    private sealed class FixedModelSelectionResolver(IChatClient? client) : IModelSelectionResolver
    {
        public Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult(client);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Blaze.LlmGateway.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
