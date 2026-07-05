using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.OutputSavers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.OutputSavers;

/// <summary>P4.1: Caveman/Ponytail output savers append system prompts per request.</summary>
public sealed class OutputSaverChatClientTests
{
    private static (OutputSaverChatClient Client, Mock<IChatClient> Inner, List<ChatMessage> Captured) Create(OutputSaverOptions savers)
    {
        var captured = new List<ChatMessage>();
        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var monitor = new Mock<IOptionsMonitor<LlmGatewayOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(new LlmGatewayOptions { OutputSavers = savers });

        var client = new OutputSaverChatClient(inner.Object, monitor.Object, NullLogger<OutputSaverChatClient>.Instance);
        return (client, inner, captured);
    }

    [Fact]
    public async Task Disabled_PassesMessagesUnchanged()
    {
        var (client, _, captured) = Create(new OutputSaverOptions());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        captured.Should().ContainSingle();
    }

    [Fact]
    public async Task CavemanEnabled_AppendsTerseSystemPrompt()
    {
        var (client, _, captured) = Create(new OutputSaverOptions
        {
            Caveman = new OutputSaverToggle { Enabled = true, Level = "Full" }
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        captured.Should().HaveCount(2);
        var system = captured.Single(m => m.Role == ChatRole.System);
        system.Text.Should().Contain("terse");
    }

    [Fact]
    public async Task BothEnabled_AppendsBothPrompts()
    {
        var (client, _, captured) = Create(new OutputSaverOptions
        {
            Caveman = new OutputSaverToggle { Enabled = true, Level = "Ultra" },
            Ponytail = new OutputSaverToggle { Enabled = true, Level = "Lite" }
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        captured.Count(m => m.Role == ChatRole.System).Should().Be(2);
    }

    [Theory]
    [InlineData("Lite")]
    [InlineData("Full")]
    [InlineData("Ultra")]
    public void PromptText_ExistsForEveryLevel(string level)
    {
        OutputSaverPrompts.Caveman(level).Should().NotBeNullOrWhiteSpace();
        OutputSaverPrompts.Ponytail(level).Should().NotBeNullOrWhiteSpace();
    }
}
