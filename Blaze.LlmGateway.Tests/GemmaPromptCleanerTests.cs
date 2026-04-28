using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class GemmaPromptCleanerTests
{
    private const string LongPrompt =
        "Hi there, I was wondering if you could please help me figure out how to read a file in C#? " +
        "I tried File.ReadAllText but I'm not sure if that's the right one. Thank you so much!";

    private static GemmaPromptCleaner Make(IChatClient routerClient, PromptCleanupOptions? options = null) =>
        new(routerClient,
            Options.Create(options ?? new PromptCleanupOptions()),
            NullLogger<GemmaPromptCleaner>.Instance);

    private static Mock<IChatClient> RouterReturning(string text)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        return mock;
    }

    [Fact]
    public async Task CleanAsync_returns_cleaned_text_on_success()
    {
        var router = RouterReturning("How do I read a file in C#?");
        var cleaner = Make(router.Object);

        var result = await cleaner.CleanAsync(LongPrompt);

        Assert.Equal("How do I read a file in C#?", result);
        router.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanAsync_skips_short_prompts_without_calling_router()
    {
        var router = RouterReturning("ignored");
        var cleaner = Make(router.Object, new PromptCleanupOptions { MinLengthChars = 1000 });

        var result = await cleaner.CleanAsync(LongPrompt);

        Assert.Equal(LongPrompt, result);
        router.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanAsync_returns_original_when_router_throws_and_opens_circuit()
    {
        var router = new Mock<IChatClient>();
        router.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ollama unreachable"));
        var cleaner = Make(router.Object);

        var first = await cleaner.CleanAsync(LongPrompt);
        var second = await cleaner.CleanAsync(LongPrompt);

        Assert.Equal(LongPrompt, first);
        Assert.Equal(LongPrompt, second);
        // After the circuit opens (first call), subsequent calls must short-circuit.
        router.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanAsync_returns_original_when_router_returns_empty()
    {
        var router = RouterReturning("");
        var cleaner = Make(router.Object);

        var result = await cleaner.CleanAsync(LongPrompt);

        Assert.Equal(LongPrompt, result);
    }

    [Fact]
    public async Task CleanAsync_returns_original_when_router_inflates_prompt()
    {
        // Cleaner returns text > 1.5× original — must be rejected as a bad rewrite.
        var inflated = new string('x', LongPrompt.Length * 2);
        var router = RouterReturning(inflated);
        var cleaner = Make(router.Object);

        var result = await cleaner.CleanAsync(LongPrompt);

        Assert.Equal(LongPrompt, result);
    }

    [Fact]
    public async Task CleanAsync_returns_original_for_whitespace_input()
    {
        var router = RouterReturning("ignored");
        var cleaner = Make(router.Object);

        var result = await cleaner.CleanAsync("   ");

        Assert.Equal("   ", result);
        router.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // (original.Length, cleaned text, expected-cleaned-returned?)
    [InlineData("the original prompt that is reasonably long", "shorter rewrite", true)]
    [InlineData("the original prompt that is reasonably long", "", false)]
    [InlineData("the original prompt that is reasonably long", "   ", false)]
    [InlineData("short", "this is way too long to be a valid rewrite of a five-char prompt", false)]
    public async Task IsValidRewrite_validates_correctly(string original, string cleaned, bool expected)
    {
        var router = RouterReturning(cleaned);
        var cleanerInstance = Make(router.Object, new PromptCleanupOptions { MinLengthChars = 0 });
        var result = await cleanerInstance.CleanAsync(original);

        if (expected)
            Assert.Equal(cleaned, result);
        else
            Assert.Equal(original, result);
    }
}
