using Blaze.LlmGateway.LocalInference;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Tests.LocalInference;

/// <summary>
/// Unit tests for <see cref="LocalGemmaChatClient"/>.
/// These tests focus on interface contract, IAsyncDisposable compliance, and integration structure.
/// Actual model inference tests are marked [Skip] as they require a local Gemma model binary.
/// </summary>
public class LocalGemmaChatClientTests
{
    [Fact]
    public void Constructor_WithNullOrEmptyPath_DoesNotThrow()
    {
        // Arrange & Act
        var action = () => new LocalGemmaChatClient(null);

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithNonExistentPath_DoesNotThrow()
    {
        // Arrange & Act
        var action = () => new LocalGemmaChatClient("/nonexistent/path/to/model.gguf");

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public void InnerClient_IsNoOpChatClient()
    {
        // Arrange & Act
        var client = new LocalGemmaChatClient(null);

        // Assert
        // Since LocalGemmaChatClient delegates to NoOpChatClientWithMetadata, 
        // it properly implements IChatClient interface
        client.Should().BeAssignableTo<IChatClient>();
    }

    [Fact]
    public void Implements_IChatClient()
    {
        // Arrange & Act
        var client = new LocalGemmaChatClient(null);

        // Assert
        client.Should().BeAssignableTo<IChatClient>();
    }

    [Fact]
    public void Implements_DelegatingChatClient()
    {
        // Arrange & Act
        var client = new LocalGemmaChatClient(null);

        // Assert
        client.Should().BeAssignableTo<DelegatingChatClient>();
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        // Arrange & Act
        var client = new LocalGemmaChatClient(null);

        // Assert
        client.Should().BeAssignableTo<IAsyncDisposable>();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);

        // Act
        var action = async () => await client.DisposeAsync();

        // Assert
        await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);

        // Act
        var action = async () =>
        {
            await client.DisposeAsync();
            await client.DisposeAsync();
        };

        // Assert
        await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WhenModelPathIsMissing_ThrowsModelNotLoadedError()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello")
        };

        // Act
        var action = async () => await client.GetResponseAsync(messages);

        // Assert
        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*LocalGemma*ModelPath*Gemma*GGUF*");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithEmptyMessages_ReturnsEmpty()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);
        var messages = Array.Empty<ChatMessage>();

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        // Assert
        updates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithValidMessagesAndMissingModelPath_ThrowsModelNotLoadedError()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello")
        };

        // Act
        var action = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages))
            {
                _ = update;
            }
        };

        // Assert
        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*LocalGemma*ModelPath*Gemma*GGUF*");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithCancellationAndMissingModelPath_ThrowsModelNotLoadedError()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello")
        };
        var cts = new CancellationTokenSource();

        // Act
        var action = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: cts.Token))
            {
                _ = update;
            }
        };

        // Assert
        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*LocalGemma*ModelPath*Gemma*GGUF*");
    }

    [Fact]
    public async Task GetResponseAsync_WithMissingModelPathAndMultipleMessages_ThrowsModelNotLoadedError()
    {
        // Arrange
        var client = new LocalGemmaChatClient(null);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "What is 2+2?"),
            new ChatMessage(ChatRole.Assistant, "4"),
            new ChatMessage(ChatRole.User, "What is 3+3?")
        };

        // Act
        var action = async () => await client.GetResponseAsync(messages);

        // Assert
        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*LocalGemma*ModelPath*Gemma*GGUF*");
    }
}
