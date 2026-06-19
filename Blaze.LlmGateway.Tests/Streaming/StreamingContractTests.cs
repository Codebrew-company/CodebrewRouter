using System.Runtime.CompilerServices;
using System.Text;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Blaze.LlmGateway.Tests.Streaming;

/// <summary>
/// Contract tests for SSE streaming through the POST /v1/chat/completions endpoint.
/// Verifies the SSE wire format, Content-Type headers, chunk structure, 
/// and error handling during streaming. Tests exercise both the legacy 
/// CodebrewRouter path and the catalog routing path.
/// </summary>
public sealed class StreamingContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "Hello, World!")])
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 5,
                    OutputTokenCount = 3,
                    TotalTokenCount = 8
                }
            });

        // Setup streaming with predictable chunks
        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> msgs, ChatOptions opts, CancellationToken ct) =>
                PredictableStream());

        _factory = new WebApplicationFactory<Blaze.LlmGateway.Api.ApiProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    RemoveServicesByType(services, typeof(IChatClient));
                    DisableLocalGemmaWarmup(services);

                    // Register mock chat client for all providers
                    services.AddSingleton(mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("LmStudio", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("AzureFoundry", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("GithubModels", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("OpenRouter", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("CodebrewRouter", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("LocalGemma", mockChatClient.Object);

                    foreach (var (dest, _) in OpenCodeGoModels.ModelNames)
                    {
                        services.AddKeyedSingleton<IChatClient>(dest.ToString(), mockChatClient.Object);
                    }

                    services.AddSingleton<IModelCatalog>(new FakeModelCatalog());

                    services.PostConfigure<LlmGatewayOptions>(options =>
                    {
                        options.Providers.OpenCodeGo.ApiKey = "sk-test";
                    });
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

    // ── Core SSE format contract ─────────────────────────────────────

    [Fact]
    public async Task Streaming_ReturnsSseContentType()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Streaming_ResponseHasNoCacheHeader()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        // SSE responses should disable caching
        Assert.True(response.Headers.Contains("Cache-Control"));
        var cacheControl = response.Headers.GetValues("Cache-Control").First();
        Assert.Contains("no-cache", cacheControl);
    }

    [Fact]
    public async Task Streaming_ResponseHasConnectionKeepAlive()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.True(response.Headers.Contains("Connection"));
        Assert.Contains("keep-alive", response.Headers.GetValues("Connection"));
    }

    [Fact]
    public async Task Streaming_ResponseHasXAccelBufferingNoHeader()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        // Should disable proxy buffering
        Assert.True(response.Headers.Contains("X-Accel-Buffering"));
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").First());
    }

    // ── SSE data format ──────────────────────────────────────────────

    [Fact]
    public async Task Streaming_AllLinesStartWithDataPrefix()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);
        var sseText = await response.Content.ReadAsStringAsync();
        var lines = sseText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.StartsWith("data: ", line);
        }
    }

    [Fact]
    public async Task Streaming_EndsWithDoneMarker()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);
        var sseText = await response.Content.ReadAsStringAsync();

        Assert.Contains("data: [DONE]", sseText);
    }

    [Fact]
    public async Task Streaming_ChunksContainObjectField()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);
        var sseText = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"object\":\"chat.completion.chunk\"", sseText);
    }

    // ── First chunk has role ────────────────────────────────────────

    [Fact]
    public async Task Streaming_FirstChunkContainsRoleAssistant()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);
        var sseText = await response.Content.ReadAsStringAsync();
        var lines = sseText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // First data line should contain a role
        var firstDataLine = lines.FirstOrDefault(l => l.StartsWith("data: ") && l != "data: [DONE]");
        Assert.NotNull(firstDataLine);
        Assert.Contains("\"role\":\"assistant\"", firstDataLine);
    }

    // ── Non-streaming vs streaming path ──────────────────────────────

    [Fact]
    public async Task NonStreaming_ReturnsJsonResponse()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[{"role":"user","content":"Hello"}],"stream":false}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"object\":\"chat.completion\"", body);
        Assert.Contains("\"id\":\"chatcmpl-", body);
    }

    [Fact]
    public async Task Streaming_WithCatalogModel_AlsoReturnsSse()
    {
        // Use a model name that goes through catalog routing. If the catalog
        // doesn't match, it falls through to the CodebrewRouter path. Either
        // way we should get valid SSE.
        var requestBody = new StringContent(
            """{"model":"gemma-local","messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var sseText = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", sseText);
    }

    // ── Error cases ──────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_MissingModel_ReturnsBadRequest()
    {
        var requestBody = new StringContent(
            """{"messages":[{"role":"user","content":"Hello"}],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Streaming_EmptyMessages_ReturnsBadRequest()
    {
        var requestBody = new StringContent(
            """{"model":"codebrewRouter","messages":[],"stream":true}""",
            Encoding.UTF8, "application/json");

        var response = await _client!.PostAsync("/v1/chat/completions", requestBody);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> PredictableStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield(); // Force async so the mock truly streams
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello")
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = "test-model"
        };
        yield return new ChatResponseUpdate(ChatRole.Assistant, ", World!")
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = "test-model"
        };
    }

    private static void RemoveServicesByType(IServiceCollection services, Type serviceType)
    {
        var descriptors = services
            .Where(s => IsMatch(s, serviceType))
            .ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }

    private static bool IsMatch(ServiceDescriptor descriptor, Type serviceType)
    {
        if (descriptor.ServiceType == serviceType)
            return true;

        // Also match DelegatingChatClient and concrete implementations
        if (serviceType == typeof(IChatClient) &&
            (descriptor.ServiceType.IsAssignableTo(typeof(IChatClient)) ||
             typeof(IChatClient).IsAssignableFrom(descriptor.ServiceType)))
            return true;

        return false;
    }

    private static void DisableLocalGemmaWarmup(IServiceCollection services)
    {
        // The warmup service blocks startup waiting for local Gemma.
        // In integration tests with a mock client we skip it entirely.
        var warmupDescriptors = services
            .Where(s => s.ImplementationType?.Name is not null &&
                        (s.ImplementationType.Name.Contains("Warmup", StringComparison.OrdinalIgnoreCase) ||
                         s.ImplementationType.Name.Contains("Gemma", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var descriptor in warmupDescriptors)
            services.Remove(descriptor);
    }

    private sealed class FakeModelCatalog : IModelCatalog
    {
        private readonly AvailableModel[] _models = new[]
        {
            new AvailableModel(
                Id: "codebrewRouter",
                Provider: "CodebrewRouter",
                OwnedBy: "codebrew",
                Source: "virtual"
            ),
            new AvailableModel(
                Id: "codebrewSharpClient",
                Provider: "CodebrewRouter",
                OwnedBy: "codebrew",
                Source: "virtual"
            )
        };

        public Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AvailableModel>>(_models);

        public Task<AvailableModel?> FindByIdAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult(_models.FirstOrDefault(m => m.Id == modelId));
    }
}
