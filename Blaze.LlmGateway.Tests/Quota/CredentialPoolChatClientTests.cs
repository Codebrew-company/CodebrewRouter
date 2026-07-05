using Blaze.LlmGateway.Infrastructure.Quota;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.Quota;

/// <summary>P3.2: multi-credential pool — rotation + per-credential rate-limit failover.</summary>
public sealed class CredentialPoolChatClientTests
{
    private static Mock<IChatClient> Ok(string reply)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, reply)]));
        mock.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(reply));
        return mock;

        static async IAsyncEnumerable<ChatResponseUpdate> Stream(string reply)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.CompletedTask;
        }
    }

    private static Mock<IChatClient> RateLimited()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HTTP 429 Too Many Requests"));
        mock.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Throw());
        return mock;

        static async IAsyncEnumerable<ChatResponseUpdate> Throw()
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("HTTP 429 Too Many Requests");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private static readonly ChatMessage Prompt = new(ChatRole.User, "hi");

    [Fact]
    public async Task FillFirst_RateLimitedFirstKey_FailsOverToSecond()
    {
        var first = RateLimited();
        var second = Ok("from-second");
        var pool = new CredentialPoolChatClient(
            [first.Object, second.Object], "fill-first", "TestProvider",
            NullLogger<CredentialPoolChatClient>.Instance);

        var response = await pool.GetResponseAsync([Prompt]);

        response.Text.Should().Be("from-second");
    }

    [Fact]
    public async Task LockedCredential_IsSkippedOnNextRequest()
    {
        var first = RateLimited();
        var second = Ok("from-second");
        var pool = new CredentialPoolChatClient(
            [first.Object, second.Object], "fill-first", "TestProvider",
            NullLogger<CredentialPoolChatClient>.Instance);

        await pool.GetResponseAsync([Prompt]);   // locks credential #0
        await pool.GetResponseAsync([Prompt]);   // must go straight to #1

        first.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Once, "a locked credential must be skipped pre-emptively");
        second.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Streaming_FailsOverBeforeFirstChunk()
    {
        var pool = new CredentialPoolChatClient(
            [RateLimited().Object, Ok("streamed").Object], "fill-first", "TestProvider",
            NullLogger<CredentialPoolChatClient>.Instance);

        var text = "";
        await foreach (var update in pool.GetStreamingResponseAsync([Prompt]))
        {
            text += update.Text;
        }

        text.Should().Be("streamed");
    }

    [Fact]
    public async Task AllCredentialsFailing_ThrowsLastError()
    {
        var pool = new CredentialPoolChatClient(
            [RateLimited().Object, RateLimited().Object], "fill-first", "TestProvider",
            NullLogger<CredentialPoolChatClient>.Instance);

        var act = () => pool.GetResponseAsync([Prompt]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*429*");
    }

    [Fact]
    public async Task RoundRobin_RotatesAfterConsecutiveUses()
    {
        var first = Ok("a");
        var second = Ok("b");
        var pool = new CredentialPoolChatClient(
            [first.Object, second.Object], "round-robin", "TestProvider",
            NullLogger<CredentialPoolChatClient>.Instance);

        for (var i = 0; i < 8; i++)
        {
            await pool.GetResponseAsync([Prompt]);
        }

        // 8 requests with rotate-after-5: both credentials must have been used.
        first.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        second.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
