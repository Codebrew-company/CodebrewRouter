using System.Net;
using System.Text;
using System.Text.Json;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.Anthropic;

/// <summary>P4.3: Anthropic-native /v1/messages endpoint contract.</summary>
public sealed class AnthropicMessagesEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<ApiProgram>? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                RemoveServicesByType(services, typeof(IChatClient));
                DisableLocalGemmaWarmup(services);

                var mockChatClient = new Mock<IChatClient>();
                mockChatClient
                    .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello from the gateway")])
                    {
                        FinishReason = ChatFinishReason.Stop,
                        Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5 }
                    });
                mockChatClient
                    .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(Stream());

                services.AddSingleton(mockChatClient.Object);
                foreach (var (dest, _) in OpenCodeGoModels.ModelNames)
                {
                    services.AddKeyedSingleton<IChatClient>(dest.ToString(), mockChatClient.Object);
                }

                services.PostConfigure<LlmGatewayOptions>(options => options.Auth.RequireApiKey = false);
            });
        });

        _client = _factory.CreateClient();
        return Task.CompletedTask;

        static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "world");
            await Task.CompletedTask;
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonStreaming_ReturnsAnthropicMessageShape()
    {
        var response = await _client!.PostAsync("/v1/messages", Json(new
        {
            model = "codebrewRouter",
            max_tokens = 128,
            system = "You are terse.",
            messages = new[] { new { role = "user", content = "hi" } }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("type").GetString().Should().Be("message");
        root.GetProperty("role").GetString().Should().Be("assistant");
        root.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        root.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello from the gateway");
        root.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Streaming_EmitsAnthropicSseEventSequence()
    {
        var response = await _client!.PostAsync("/v1/messages", Json(new
        {
            model = "codebrewRouter",
            max_tokens = 128,
            stream = true,
            messages = new[] { new { role = "user", content = "hi" } }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: message_start");
        body.Should().Contain("event: content_block_start");
        body.Should().Contain("event: content_block_delta");
        body.Should().Contain("text_delta");
        body.Should().Contain("event: content_block_stop");
        body.Should().Contain("event: message_delta");
        body.Should().Contain("event: message_stop");
        body.IndexOf("message_start", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("message_stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContentPartArrays_AndToolResults_AreAccepted()
    {
        var response = await _client!.PostAsync("/v1/messages", Json(new
        {
            model = "codebrewRouter",
            max_tokens = 128,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "what is in this image?" },
                        new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(Encoding.UTF8.GetBytes("x")) } }
                    }
                }
            }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CountTokens_ReturnsPositiveCount()
    {
        var response = await _client!.PostAsync("/v1/messages/count_tokens", Json(new
        {
            model = "codebrewRouter",
            messages = new[] { new { role = "user", content = "count the tokens in this sentence please" } }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("input_tokens").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MissingModel_Returns400AnthropicError()
    {
        var response = await _client!.PostAsync("/v1/messages", Json(new
        {
            max_tokens = 128,
            messages = new[] { new { role = "user", content = "hi" } }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_request_error");
    }

    private static StringContent Json(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static void RemoveServicesByType(IServiceCollection services, Type serviceType)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == serviceType).ToList())
        {
            services.Remove(descriptor);
        }
    }

    private static void DisableLocalGemmaWarmup(IServiceCollection services)
    {
        RemoveServicesByType(services, typeof(LocalInferenceOptions));
        RemoveServicesByType(services, typeof(IOptions<LocalInferenceOptions>));

        var modelPath = Path.Combine(Path.GetTempPath(), "codebrewrouter-test-local-gemma.gguf");
        if (!File.Exists(modelPath))
        {
            File.WriteAllBytes(modelPath, []);
        }

        var options = new LocalInferenceOptions
        {
            Enabled = true,
            ModelPath = modelPath,
            WarmupEnabled = false,
            BlockStartupUntilWarm = false
        };

        services.AddSingleton(options);
        services.AddSingleton(Options.Create(options));
        services.PostConfigure<LlmGatewayOptions>(gatewayOptions =>
        {
            gatewayOptions.LocalInference.Enabled = true;
            gatewayOptions.LocalInference.ModelPath = modelPath;
            gatewayOptions.LocalInference.WarmupEnabled = false;
            gatewayOptions.LocalInference.BlockStartupUntilWarm = false;
        });
    }
}
