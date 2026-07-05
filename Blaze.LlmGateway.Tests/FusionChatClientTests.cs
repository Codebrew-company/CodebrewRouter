using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests;

public sealed class FusionChatClientTests
{
    private static readonly UsageDetails ProposerUsage = new() { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 };
    private static readonly UsageDetails AggregatorUsage = new() { InputTokenCount = 5, OutputTokenCount = 8, TotalTokenCount = 13 };

    [Fact]
    public async Task Fusion_WithThreeProposers_AggregatesAllReferencesAndReturnsSynthesis()
    {
        var p1 = new RecordingChatClient("answer one", ProposerUsage);
        var p2 = new RecordingChatClient("answer two", ProposerUsage);
        var p3 = new RecordingChatClient("answer three", ProposerUsage);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);
        var inner = new RecordingChatClient("INNER", null);

        var client = BuildClient(inner, aggregator, ("P1", p1), ("P2", p2), ("P3", p3), ("AGG", aggregator));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "the question")]);

        response.Text.Should().Be("SYNTHESIZED");
        inner.CallCount.Should().Be(0, "aggregation succeeded so the router fallback is never used");

        aggregator.LastMessages.Should().NotBeNull();
        var systemMessage = aggregator.LastMessages![0];
        systemMessage.Role.Should().Be(ChatRole.System);
        systemMessage.Text.Should().Contain("Responses from models:");
        systemMessage.Text.Should().Contain("answer one").And.Contain("answer two").And.Contain("answer three");
    }

    [Fact]
    public async Task Fusion_SumsUsageAcrossProposersAndAggregator()
    {
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);
        var client = BuildClient(
            new RecordingChatClient("INNER", null),
            aggregator,
            ("P1", new RecordingChatClient("a", ProposerUsage)),
            ("P2", new RecordingChatClient("b", ProposerUsage)),
            ("P3", new RecordingChatClient("c", ProposerUsage)),
            ("AGG", aggregator));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        // 3 proposers (10/20/30) + aggregator (5/8/13)
        response.Usage!.InputTokenCount.Should().Be(35);
        response.Usage!.OutputTokenCount.Should().Be(68);
        response.Usage!.TotalTokenCount.Should().Be(103);
    }

    [Fact]
    public async Task Fusion_WhenOnlyOneProposerSucceeds_SkipsAggregationAndReturnsThatProposal()
    {
        var good = new RecordingChatClient("the only answer", ProposerUsage);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);
        var inner = new RecordingChatClient("INNER", null);

        var client = BuildClient(inner, aggregator,
            ("P1", good),
            ("P2", new ThrowingChatClient()),
            ("P3", new ThrowingChatClient()),
            ("AGG", aggregator));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        response.Text.Should().Be("the only answer");
        aggregator.CallCount.Should().Be(0, "a single proposal must not be aggregated");
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Fusion_WhenNoProposerSucceeds_FallsBackToRouter()
    {
        var inner = new RecordingChatClient("ROUTER ANSWER", null);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);

        var client = BuildClient(inner, aggregator,
            ("P1", new ThrowingChatClient()),
            ("P2", new ThrowingChatClient()),
            ("P3", new ThrowingChatClient()),
            ("AGG", aggregator));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        response.Text.Should().Be("ROUTER ANSWER");
        inner.CallCount.Should().Be(1);
        aggregator.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Fusion_WhenToolsPresent_BypassesFanOutAndDelegatesToRouter()
    {
        var inner = new RecordingChatClient("ROUTER ANSWER", null);
        var p1 = new RecordingChatClient("a", ProposerUsage);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);

        var client = BuildClient(inner, aggregator, ("P1", p1), ("P2", new RecordingChatClient("b", ProposerUsage)), ("P3", new RecordingChatClient("c", ProposerUsage)), ("AGG", aggregator));

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "noop", "noop")] };
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options);

        response.Text.Should().Be("ROUTER ANSWER");
        inner.CallCount.Should().Be(1);
        p1.CallCount.Should().Be(0, "tools must never be fanned out to proposers");
    }

    [Fact]
    public async Task Fusion_WhenDisabled_DelegatesEveryRequestToRouter()
    {
        var inner = new RecordingChatClient("ROUTER ANSWER", null);
        var p1 = new RecordingChatClient("a", ProposerUsage);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);

        var client = BuildClient(inner, aggregator,
            fusionEnabled: false,
            keyed: [("P1", p1), ("P2", new RecordingChatClient("b", ProposerUsage)), ("P3", new RecordingChatClient("c", ProposerUsage)), ("AGG", aggregator)]);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        response.Text.Should().Be("ROUTER ANSWER");
        inner.CallCount.Should().Be(1);
        p1.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Fusion_Streaming_AggregatesAndEmitsUsageChunk()
    {
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);
        var client = BuildClient(
            new RecordingChatClient("INNER", null),
            aggregator,
            ("P1", new RecordingChatClient("a", ProposerUsage)),
            ("P2", new RecordingChatClient("b", ProposerUsage)),
            ("P3", new RecordingChatClient("c", ProposerUsage)),
            ("AGG", aggregator));

        var text = "";
        var sawUsage = false;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]))
        {
            text += update.Text;
            if (update.Contents.Any(c => c is UsageContent))
            {
                sawUsage = true;
            }
        }

        text.Should().Contain("SYNTHESIZED");
        sawUsage.Should().BeTrue("proposer usage is surfaced as a trailing usage chunk");
    }

    [Fact]
    public async Task Fusion_WhenCallerCancels_PropagatesCancellationAndDoesNotReEnterRouter()
    {
        var inner = new RecordingChatClient("ROUTER ANSWER", null);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);

        var client = BuildClient(inner, aggregator,
            ("P1", new TokenHonoringChatClient()),
            ("P2", new TokenHonoringChatClient()),
            ("P3", new TokenHonoringChatClient()),
            ("AGG", aggregator));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.CallCount.Should().Be(0, "a caller cancel must not trigger a fresh router round-trip");
    }

    [Fact]
    public async Task Fusion_WhenOfflineOnly_BypassesFanOutAndDelegatesToRouter()
    {
        var inner = new RecordingChatClient("ROUTER ANSWER", null);
        var p1 = new RecordingChatClient("a", ProposerUsage);
        var aggregator = new RecordingChatClient("SYNTHESIZED", AggregatorUsage);

        var client = BuildClient(inner, aggregator,
            fusionEnabled: true,
            offlineOnly: true,
            keyed: [("P1", p1), ("P2", new RecordingChatClient("b", ProposerUsage)), ("P3", new RecordingChatClient("c", ProposerUsage)), ("AGG", aggregator)]);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        response.Text.Should().Be("ROUTER ANSWER");
        inner.CallCount.Should().Be(1);
        p1.CallCount.Should().Be(0, "offline mode must not fan out to cloud proposers (ADR-0008)");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FusionChatClient BuildClient(
        IChatClient inner,
        IChatClient aggregator,
        params (string Key, IChatClient Client)[] keyed)
        => BuildClient(inner, aggregator, fusionEnabled: true, offlineOnly: false, keyed);

    private static FusionChatClient BuildClient(
        IChatClient inner,
        IChatClient aggregator,
        bool fusionEnabled,
        (string Key, IChatClient Client)[] keyed)
        => BuildClient(inner, aggregator, fusionEnabled, offlineOnly: false, keyed);

    private static FusionChatClient BuildClient(
        IChatClient inner,
        IChatClient aggregator,
        bool fusionEnabled,
        bool offlineOnly,
        (string Key, IChatClient Client)[] keyed)
    {
        var services = new ServiceCollection();
        foreach (var (key, chatClient) in keyed)
        {
            services.AddKeyedSingleton(key, chatClient);
        }
        var serviceProvider = services.BuildServiceProvider();

        var fusionOptions = new FusionOptions
        {
            Enabled = fusionEnabled,
            MaxProposers = 3,
            MinProposers = 2,
            ProposerTimeoutSeconds = 10,
            StragglerGraceSeconds = 2,
            ProposerSetsByTaskClass = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["General"] = ["P1", "P2", "P3"]
            },
            AggregatorByTaskClass = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["General"] = ["AGG"]
            }
        };

        return new FusionChatClient(
            inner,
            new FixedTaskClassifier(TaskType.General),
            Options.Create(fusionOptions),
            Options.Create(new LlmGatewayOptions { OfflineOnly = offlineOnly }),
            serviceProvider,
            NullLogger<FusionChatClient>.Instance);
    }

    private sealed class RecordingChatClient(string text, UsageDetails? usage) : IChatClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = chatMessages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = usage
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = chatMessages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("proposer unavailable");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("proposer unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TokenHonoringChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

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
}
