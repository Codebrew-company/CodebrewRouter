using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Integration tests for context-size enforcement at the HTTP layer.
/// Replaces all real IChatClient registrations with a mock that throws ContextOverflowException
/// so the tests verify that the endpoint returns HTTP 413 with the correct JSON body shape —
/// without making any real LLM or network calls.
/// </summary>
public class ContextSizingIntegrationTests : IClassFixture<WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram>>
{
    private readonly HttpClient _client;

    public ContextSizingIntegrationTests(WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram> factory)
    {
        // A ContextOverflowException with representative values that all providers rejected.
        var overflow = new ContextOverflowException(
            modelId: "gpt-4o",
            requiredTokens: 9999,
            budget: 1,
            attemptedDestinations: ["AzureFoundry", "FoundryLocal", "GithubModels", "OllamaLocal"]);

        var mockChatClient = new Mock<IChatClient>();

        // Non-streaming path.
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(overflow);

        // Streaming path (used by TryGetFirstStreamingUpdateAsync).
        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ThrowingStreamingResponse(overflow));

        _client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Disable availability probe so the heartbeat service doesn't
                    // attempt real network calls during startup.
                    services.PostConfigure<LlmGatewayOptions>(opts => opts.Availability.Enabled = false);

                    // Remove ALL real IChatClient registrations (keyed and non-keyed).
                    var toRemove = services
                        .Where(d => d.ServiceType == typeof(IChatClient))
                        .ToList();
                    foreach (var d in toRemove)
                        services.Remove(d);

                    // Register the overflow-throwing mock for every slot the DI graph needs.
                    services.AddSingleton(mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("AzureFoundry", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("FoundryLocal",  mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("OllamaLocal",   mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("GithubModels",  mockChatClient.Object);

                    // Fake catalog / resolver so no real model-catalog calls are made.
                    services.AddSingleton<IModelCatalog>(new FakeModelCatalog());
                    services.AddSingleton<IModelSelectionResolver>(
                        new FakeModelSelectionResolver(mockChatClient.Object));
                });
            })
            .CreateClient();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostChatCompletion_ContextOverflowException_Returns413()
    {
        var request = new
        {
            model    = "gpt-4o",
            messages = new[] { new { role = "user", content = "Hello world" } },
            stream   = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("type").GetString()
            .Should().Be("context_length_exceeded");
        body.GetProperty("error").GetProperty("required_tokens").GetInt32()
            .Should().Be(9999);
        body.GetProperty("error").GetProperty("largest_window_budget").GetInt32()
            .Should().Be(1);
    }

    [Fact]
    public async Task PostChatCompletion_ContextOverflowException_ErrorBodyContainsAttemptedDestinations()
    {
        var request = new
        {
            model    = "gpt-4o",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream   = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var destinations = body
            .GetProperty("error")
            .GetProperty("attempted_destinations")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        destinations.Should().Contain("AzureFoundry");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingStreamingResponse(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // Unreachable code
        yield break;
#pragma warning restore CS0162
    }

    private sealed class FakeModelSelectionResolver(IChatClient client) : IModelSelectionResolver
    {
        public Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatClient?>(client);
    }

    private sealed class FakeModelCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AvailableModel>>([
                new AvailableModel("gpt-4o", "AzureFoundry", "openai", "configured")
            ]);

        public Task<AvailableModel?> FindByIdAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult<AvailableModel?>(
                new AvailableModel(modelId, "AzureFoundry", "openai", "configured"));
    }
}
