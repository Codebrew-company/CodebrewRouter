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
/// Integration tests for the OllamaLocal (gemma4:e4b) provider.
/// All tests are mocked — no running Ollama instance is required.
/// Real integration tests are included as [Fact(Skip = ...)] stubs with setup instructions.
/// </summary>
public class OllamaLocalIntegrationTests : IAsyncLifetime
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

                    // Simulate OllamaLocal (gemma4:e4b) streaming response
                    mockChatClient
                        .Setup(c => c.GetStreamingResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .Returns(StreamingOllamaLocalResponse());

                    // Simulate OllamaLocal (gemma4:e4b) non-streaming response
                    mockChatClient
                        .Setup(c => c.GetResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new ChatResponse(
                            [new ChatMessage(ChatRole.Assistant,
                                "OllamaLocal gemma4:e4b response: I am a locally-hosted open model, no data leaves your machine.")]));

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

    /// <summary>MOCKED: Verifies the endpoint accepts chat completions requests targeting OllamaLocal.</summary>
    [Fact]
    public async Task OllamaLocal_ChatCompletions_MockedGemma4_ReturnsResponse()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[] { new { role = "user", content = "What is the capital of France?" } }
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
    public async Task OllamaLocal_ChatCompletions_NonStreaming_HasRequiredFields()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[] { new { role = "user", content = "Summarize the French Revolution." } },
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

    /// <summary>MOCKED: Verifies OllamaLocal works with a privacy-focused use case (local processing emphasis).</summary>
    [Fact]
    public async Task OllamaLocal_ChatCompletions_PrivacyFocusedRequest_Succeeds()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[]
            {
                new { role = "system", content = "You run completely locally. No data is sent externally." },
                new { role = "user",   content = "Summarize this private document: 'Q4 revenue was $5M.'" }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>MOCKED: Verifies OllamaLocal handles a simple general chat request.</summary>
    [Fact]
    public async Task OllamaLocal_ChatCompletions_GeneralChat_Succeeds()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[] { new { role = "user", content = "Tell me a fun fact." } },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>MOCKED: Verifies streaming SSE response from OllamaLocal.</summary>
    [Fact]
    public async Task OllamaLocal_ChatCompletions_Streaming_ReturnsSSEStream()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[] { new { role = "user", content = "Explain a neural network in simple terms." } },
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
    public async Task OllamaLocal_ChatCompletions_Streaming_ChunksHaveCorrectShape()
    {
        var request = new
        {
            model = "gemma4:e4b",
            messages = new[] { new { role = "user", content = "Greet me." } },
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
    public async Task OllamaLocal_ModelsEndpoint_ReturnsOk()
    {
        var response = await _client!.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>MOCKED: Verifies CodebrewRouter virtual model always appears in catalog.</summary>
    [Fact]
    public async Task OllamaLocal_ModelsEndpoint_ContainsCodebrewRouter()
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

    /// <summary>MOCKED: Verifies legacy /v1/completions endpoint works with gemma4 model.</summary>
    [Fact]
    public async Task OllamaLocal_Completions_MockedGemma4_ReturnsText()
    {
        var request = new
        {
            model = "gemma4:e4b",
            prompt = "Once upon a time in a small village",
            max_tokens = 30
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
    /// REAL INTEGRATION TEST — requires a running Ollama instance with gemma4:e4b loaded.
    /// Prerequisites:
    ///   1. Install Ollama: https://ollama.com/download
    ///   2. Pull and start the model:
    ///      ollama pull gemma4:e4b
    ///   3. Confirm Ollama is running at the configured base URL (default: http://192.168.16.12:11434):
    ///      dotnet user-secrets set "LlmGateway:Providers:OllamaLocal:BaseUrl" "http://localhost:11434" --project Blaze.LlmGateway.Api
    ///      dotnet user-secrets set "LlmGateway:Providers:OllamaLocal:Model"   "gemma4:e4b" --project Blaze.LlmGateway.Api
    ///   4. Remove the [Fact(Skip = ...)] attribute and use [Fact].
    /// </summary>
    [Fact(Skip = "Requires a running Ollama instance with gemma4:e4b loaded. See instructions in the XML doc comment.")]
    public async Task OllamaLocal_RealIntegration_ChatCompletions_Succeeds()
    {
        Assert.True(true); // Placeholder — remove [Skip] and add real assertions once Ollama is running.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamingOllamaLocalResponse()
    {
        var chunks = new[] { "OllamaLocal ", "gemma4:e4b ", "local ", "streaming ", "response." };
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Delay(5);
        }
    }
}
