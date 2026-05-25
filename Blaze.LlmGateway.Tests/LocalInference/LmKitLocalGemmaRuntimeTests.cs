using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using LMKit.TextGeneration.Chat;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Tests.LocalInference;

public sealed class LmKitLocalGemmaRuntimeTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_StripsThoughtChannelContent()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "help me stop procrastinating") };
        var runtime = new FakeLmKitRuntime([
            "<|channel>thought\n",
            "internal reasoning here",
            "<|channel>final\n",
            "What time or kind of task do you procrastinate on most?"
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("What time or kind of task do you procrastinate on most?");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StripsThoughtChannelContent_WhenChannelHasNoNewlineDelimiter()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "help me stop procrastinating") };
        var runtime = new FakeLmKitRuntime([
            "<|channel>thoughtHere's a thinking process to construct the response.",
            "<|channel>finalWhat time or kind of task do you procrastinate on most?"
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("What time or kind of task do you procrastinate on most?");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StripsPlainEnglishThinkingPrelude_WhenFinalResponseMarkerAppears()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "explain options trading") };
        var runtime = new FakeLmKitRuntime([
            "Here's a thinking process to construct the explanation:",
            "1. Analyze the request. 2. Draft the simple analogy.",
            "\n\nFinal response:\n",
            "Options are contracts that give you the right, not the obligation, to buy or sell a stock."
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be(
            "Options are contracts that give you the right, not the obligation, to buy or sell a stock.");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_SuppressesPlainEnglishThinkingPrelude_WhenNoFinalResponseAppears()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "help me with procrastination") };
        var runtime = new FakeLmKitRuntime([
            "Here's a thinking process to construct the response:",
            "1. Analyze the Request: The user has a two-part request.",
            "2. Determine the Tone and Style."
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().BeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmitsAssistantChannelContent_WhenModelUsesAssistantChannel()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "write a helper method") };
        var runtime = new FakeLmKitRuntime([
            "<|channel>assistantHere's the helper method you asked for."
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("Here's the helper method you asked for.");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_FallsBackToResidualText_WhenNoVisibleChannelWasEmitted()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "explain the bug") };
        var runtime = new FakeLmKitRuntime([
            "<|channel>thoughtThis is the only text the model produced."
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("This is the only text the model produced.");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StripsEscapedTurnControlToken()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var runtime = new FakeLmKitRuntime([
            "Hello! How can I help?",
            "<turn|>"
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("Hello! How can I help?");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StripsEscapedTurnControlToken_WhenSplitAcrossChunks()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var runtime = new FakeLmKitRuntime([
            "Hello! How can I help?<tu",
            "rn|>"
        ]);

        var chunks = new List<string>();
        await foreach (var update in runtime.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("Hello! How can I help?");
    }

    [Fact]
    public void BuildSeededHistoryEntries_WithMultiTurnChat_UsesPriorNonSystemMessagesOnly()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "be concise"),
            new ChatMessage(ChatRole.User, "first user turn"),
            new ChatMessage(ChatRole.Assistant, "first assistant answer"),
            new ChatMessage(ChatRole.User, "current user turn")
        };

        var entries = LmKitLocalGemmaRuntime.BuildSeededHistoryEntries(messages);

        entries.Should().Equal(
            (AuthorRole.User, "first user turn"),
            (AuthorRole.Assistant, "first assistant answer"));
    }

    [Fact]
    public void BuildSeededHistoryEntries_WithOnlyCurrentPrompt_ReturnsEmptyEntries()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "be concise"),
            new ChatMessage(ChatRole.User, "current user turn")
        };

        var entries = LmKitLocalGemmaRuntime.BuildSeededHistoryEntries(messages);

        entries.Should().BeEmpty();
    }

    [Fact]
    public void BuildPromptWithHistory_FoldsPriorTurnsIntoPromptWithoutMutatingLmKitChatHistory()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "be concise"),
            new ChatMessage(ChatRole.User, "first user turn"),
            new ChatMessage(ChatRole.Assistant, "first assistant answer"),
            new ChatMessage(ChatRole.User, "current user turn")
        };

        var prompt = LmKitLocalGemmaRuntime.BuildPromptWithHistory(messages);

        prompt.Should().Contain("Conversation so far:");
        prompt.Should().Contain("User: first user turn");
        prompt.Should().Contain("Assistant: first assistant answer");
        prompt.Should().EndWith("current user turn");
    }

    [Fact]
    public void BuildLoadFailureMessage_WhenNativeBackendRejectsTensorType_ErrorIsActionable()
    {
        var exception = new InvalidOperationException(
            "Failed to load model. native: gguf_init_from_file_impl: tensor 'per_layer_token_embd.weight' has invalid ggml type 40. should be in [0, 40)");

        var message = LmKitLocalGemmaRuntime.BuildLoadFailureMessage("E:\\models\\gemma4.lmk", exception);

        message.Should().Contain("LM-Kit could not load local Gemma model");
        message.Should().Contain("per_layer_token_embd.weight");
        message.Should().Contain("unsupported GGML type 40");
        message.Should().Contain("Update LM-Kit");
    }

    [Fact]
    public void BuildLoadFailureMessage_WhenErrorIsUnrecognized_ReturnsGenericFallback()
    {
        var exception = new InvalidOperationException("General load failure.");

        var message = LmKitLocalGemmaRuntime.BuildLoadFailureMessage("E:\\models\\gemma4.lmk", exception);

        message.Should().Be("Failed to load local Gemma model from 'E:\\models\\gemma4.lmk' via LM-Kit.");
    }

    private sealed class FakeLmKitRuntime : ILocalGemmaRuntime
    {
        private readonly string[] _chunks;

        public FakeLmKitRuntime(string[] chunks)
        {
            _chunks = chunks;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var filterType = typeof(LmKitLocalGemmaRuntime).GetNestedType("ChannelTextFilter", System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ChannelTextFilter not found.");
            var filter = Activator.CreateInstance(filterType)
                ?? throw new InvalidOperationException("Could not create ChannelTextFilter.");
            var append = filterType.GetMethod("Append")
                ?? throw new InvalidOperationException("Append method not found.");

            foreach (var chunk in _chunks)
            {
                var visibleChunks = (IEnumerable<string>?)append.Invoke(filter, [chunk])
                    ?? [];

                foreach (var visibleChunk in visibleChunks)
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, visibleChunk);
                }
            }

            var complete = filterType.GetMethod("Complete")
                ?? throw new InvalidOperationException("Complete method not found.");
            var finalChunks = (IEnumerable<string>?)complete.Invoke(filter, null)
                ?? [];

            foreach (var finalChunk in finalChunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, finalChunk);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
