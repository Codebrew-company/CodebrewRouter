using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Integration tests for the GitHub Models (gpt-4o-mini) provider.
/// All tests are mocked — no GitHub Personal Access Token is required.
/// Real integration tests are included as [Fact(Skip = ...)] stubs with setup instructions.
/// </summary>
public class GithubModelsIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptorsToRemove = services
                        .Where(d => d.ServiceType == typeof(IChatClient))
                        .ToList();
                    foreach (var descriptor in descriptorsToRemove)
                        services.Remove(descriptor);

                    var mockChatClient = new Mock<IChatClient>();

                    // Simulate GitHub Models (gpt-4o-mini) streaming response
                    mockChatClient
                        .Setup(c => c.GetStreamingResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .Returns(StreamingGithubModelsResponse());

                    // Simulate GitHub Models (gpt-4o-mini) non-streaming response
                    mockChatClient
                        .Setup(c => c.GetResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new ChatResponse(
                            [new ChatMessage(ChatRole.Assistant,
                                "GitHub Models gpt-4o-mini response: Here is my answer to your coding question.")]));

                    services.AddSingleton(mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("AzureFoundry", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("FoundryLocal", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("GithubModels", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("OllamaLocal", mockChatClient.Object);
                });
            });

        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    // ── Non-streaming ─────────────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies the endpoint accepts chat completions requests targeting GitHub Models.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_MockedGpt4oMini_ReturnsResponse()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "Write a Python function to reverse a string." } },
            temperature = 0.2,
            max_tokens = 200
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("choices", out var choices));
        Assert.True(choices.GetArrayLength() > 0);
    }

    /// <summary>MOCKED: Verifies non-streaming response contains all required OpenAI-compatible fields.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_NonStreaming_HasRequiredFields()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "Debug this loop." } },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        Assert.True(json.RootElement.TryGetProperty("id", out _), "Missing 'id'");
        Assert.True(json.RootElement.TryGetProperty("object", out var obj), "Missing 'object'");
        Assert.Equal("chat.completion", obj.GetString());
        Assert.True(json.RootElement.TryGetProperty("created", out _), "Missing 'created'");
        Assert.True(json.RootElement.TryGetProperty("choices", out var choices), "Missing 'choices'");
        var choice = choices.EnumerateArray().First();
        Assert.True(choice.TryGetProperty("message", out var message));
        Assert.True(message.TryGetProperty("role", out var role));
        Assert.Equal("assistant", role.GetString());
        Assert.True(json.RootElement.TryGetProperty("usage", out _), "Missing 'usage'");
    }

    /// <summary>MOCKED: Verifies GitHub Models works with a typical coding-focused system prompt.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_WithCodingSystemPrompt_Succeeds()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = "You are an expert software engineer. Be precise and concise." },
                new { role = "user",   content = "How do I implement a binary search in C#?" }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>MOCKED: Verifies GitHub Models works with a multi-turn conversation.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_MultiTurnConversation_Succeeds()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "user",      content = "What is a class in C#?" },
                new { role = "assistant", content = "A class is a blueprint for creating objects..." },
                new { role = "user",      content = "Can you show an example?" }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies streaming SSE response from GitHub Models.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_Streaming_ReturnsSSEStream()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "Implement FizzBuzz in Python." } },
            stream = true
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("[DONE]"));
    }

    /// <summary>MOCKED: Verifies streaming chunks have valid OpenAI chunk shape.</summary>
    [Fact]
    public async Task GithubModels_ChatCompletions_Streaming_ChunksHaveCorrectShape()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "Say hello." } },
            stream = true
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();

        var dataLines = body.Split('\n')
            .Where(l => l.StartsWith("data: ") && !l.Contains("[DONE]"))
            .ToList();
        foreach (var line in dataLines)
        {
            var jsonStr = line["data: ".Length..];
            var json = JsonDocument.Parse(jsonStr);
            Assert.True(json.RootElement.TryGetProperty("object", out var obj));
            Assert.Equal("chat.completion.chunk", obj.GetString());
            Assert.True(json.RootElement.TryGetProperty("choices", out var choices));
            var choice = choices.EnumerateArray().First();
            Assert.True(choice.TryGetProperty("delta", out _));
        }
    }

    // ── Models endpoint ───────────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies models endpoint returns HTTP 200.</summary>
    [Fact]
    public async Task GithubModels_ModelsEndpoint_ReturnsOk()
    {
        var response = await _client!.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>MOCKED: Verifies CodebrewRouter virtual model always appears in catalog.</summary>
    [Fact]
    public async Task GithubModels_ModelsEndpoint_ContainsCodebrewRouter()
    {
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        Assert.True(json.RootElement.TryGetProperty("data", out var data));
        var ids = data.EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("codebrewRouter", ids);
    }

    // ── Completions endpoint ──────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies legacy /v1/completions endpoint works with gpt-4o-mini.</summary>
    [Fact]
    public async Task GithubModels_Completions_MockedGpt4oMini_ReturnsText()
    {
        var request = new
        {
            model = "gpt-4o-mini",
            prompt = "The best programming language for data science is",
            max_tokens = 20
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("choices", out var choices));
        Assert.True(choices.GetArrayLength() > 0);
    }

    // ── Real integration test stub ────────────────────────────────────────────

    /// <summary>
    /// REAL INTEGRATION TEST — requires a GitHub Personal Access Token with model access.
    /// Prerequisites:
    ///   1. Create a GitHub PAT with Models scope at https://github.com/settings/tokens
    ///   2. Store the token:
    ///      dotnet user-secrets set "Parameters:github-models-api-key" "<your-PAT>" --project Blaze.LlmGateway.AppHost
    ///   3. Remove the [Fact(Skip = ...)] attribute and use [Fact].
    /// </summary>
    [Fact(Skip = "Requires a GitHub PAT with model access. See instructions in the XML doc comment.")]
    public async Task GithubModels_RealIntegration_ChatCompletions_Succeeds()
    {
        Assert.True(true); // Placeholder — remove [Skip] and add real assertions once credentials are set.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamingGithubModelsResponse()
    {
        var chunks = new[] { "GitHub ", "Models ", "gpt-4o-mini ", "response ", "to your ", "coding ", "request." };
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(5);
        }
    }
}
