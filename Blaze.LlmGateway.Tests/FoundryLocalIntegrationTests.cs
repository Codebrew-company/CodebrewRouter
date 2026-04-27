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
/// Integration tests for the FoundryLocal (Phi-4-mini-instruct) provider.
/// All tests are mocked — no running Foundry Local instance is required.
/// Real integration tests are included as [Fact(Skip = ...)] stubs with setup instructions.
/// </summary>
public class FoundryLocalIntegrationTests : IAsyncLifetime
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

                    // Simulate FoundryLocal (Phi-4-mini) streaming response
                    mockChatClient
                        .Setup(c => c.GetStreamingResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .Returns(StreamingFoundryLocalResponse());

                    // Simulate FoundryLocal (Phi-4-mini) non-streaming response
                    mockChatClient
                        .Setup(c => c.GetResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new ChatResponse(
                            [new ChatMessage(ChatRole.Assistant,
                                "FoundryLocal Phi-4-mini response: I am a compact but capable local model.")]));

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

    /// <summary>MOCKED: Verifies the endpoint accepts chat completions requests targeting FoundryLocal model.</summary>
    [Fact]
    public async Task FoundryLocal_ChatCompletions_MockedPhi4Mini_ReturnsResponse()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            messages = new[] { new { role = "user", content = "What is 2 + 2?" } },
            temperature = 0.1,
            max_tokens = 50
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
    public async Task FoundryLocal_ChatCompletions_NonStreaming_HasRequiredFields()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            messages = new[] { new { role = "user", content = "Explain gravity briefly." } },
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
        Assert.True(json.RootElement.TryGetProperty("choices", out _), "Missing 'choices'");
        Assert.True(json.RootElement.TryGetProperty("usage", out _), "Missing 'usage'");
    }

    /// <summary>MOCKED: Verifies FoundryLocal works with a system + user message pair.</summary>
    [Fact]
    public async Task FoundryLocal_ChatCompletions_WithSystemMessage_Succeeds()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            messages = new[]
            {
                new { role = "system", content = "You are a concise assistant." },
                new { role = "user", content = "Hello!" }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies streaming SSE response from FoundryLocal.</summary>
    [Fact]
    public async Task FoundryLocal_ChatCompletions_Streaming_ReturnsSSEStream()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            messages = new[] { new { role = "user", content = "Write one sentence about AI." } },
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
    public async Task FoundryLocal_ChatCompletions_Streaming_ChunksHaveCorrectShape()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            messages = new[] { new { role = "user", content = "Say hi." } },
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
    public async Task FoundryLocal_ModelsEndpoint_ReturnsOk()
    {
        var response = await _client!.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>MOCKED: Verifies CodebrewRouter virtual model always appears in catalog.</summary>
    [Fact]
    public async Task FoundryLocal_ModelsEndpoint_ContainsCodebrewRouter()
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

    /// <summary>MOCKED: Verifies legacy /v1/completions endpoint works with Phi-4-mini model.</summary>
    [Fact]
    public async Task FoundryLocal_Completions_MockedPhi4Mini_ReturnsText()
    {
        var request = new
        {
            model = "Phi-4-mini-instruct-cuda-gpu:5",
            prompt = "The capital of France is",
            max_tokens = 10
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
    /// REAL INTEGRATION TEST — requires a running Foundry Local instance.
    /// Prerequisites:
    ///   1. Install Foundry Local: winget install Microsoft.FoundryLocal
    ///   2. Start Foundry Local and load the model:
    ///      foundrylocal start phi-4-mini
    ///   3. Configure the endpoint (default: http://127.0.0.1:58484):
    ///      dotnet user-secrets set "LlmGateway:Providers:FoundryLocal:Endpoint" "http://127.0.0.1:58484" --project Blaze.LlmGateway.Api
    ///      dotnet user-secrets set "LlmGateway:Providers:FoundryLocal:Model" "Phi-4-mini-instruct-cuda-gpu:5" --project Blaze.LlmGateway.Api
    ///   4. Remove the [Fact(Skip = ...)] attribute and use [Fact].
    /// </summary>
    [Fact(Skip = "Requires a running Foundry Local instance with Phi-4-mini loaded. See instructions in the XML doc comment.")]
    public async Task FoundryLocal_RealIntegration_ChatCompletions_Succeeds()
    {
        Assert.True(true); // Placeholder — remove [Skip] and add real assertions once credentials are set.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamingFoundryLocalResponse()
    {
        var chunks = new[] { "FoundryLocal ", "Phi-4-mini ", "streaming ", "response." };
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(5);
        }
    }
}
