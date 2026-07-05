using System.Net;
using System.Net.Http.Headers;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.Auth;

/// <summary>
/// P0.1/P0.2: /v1 API-key enforcement + per-key rate limiting.
/// Default-deny is by path prefix, so every /v1 route (present and future) is covered.
/// </summary>
public sealed class ApiKeyAuthTests
{
    private static readonly AdminApiKey TestKey = new(
        Id: Ids.New("key"),
        TenantId: "tenant_test",
        Name: "test",
        Key: $"cbr_{Guid.NewGuid():N}",
        AllowedModels: ["codebrewRouter"],
        AllowCloud: false,
        Scopes: ["chat"],
        CreatedAt: DateTimeOffset.UtcNow);

    private static WebApplicationFactory<ApiProgram> CreateFactory(
        bool? requireApiKey,
        bool seedKey = true,
        int requestsPerMinutePerKey = 0)
        => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                RemoveServicesByType(services, typeof(IChatClient));
                DisableLocalGemmaWarmup(services);

                var mockChatClient = new Mock<IChatClient>();
                mockChatClient
                    .Setup(c => c.GetResponseAsync(
                        It.IsAny<IEnumerable<ChatMessage>>(),
                        It.IsAny<ChatOptions>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")]));
                services.AddSingleton(mockChatClient.Object);

                foreach (var (dest, _) in OpenCodeGoModels.ModelNames)
                {
                    services.AddKeyedSingleton<IChatClient>(dest.ToString(), mockChatClient.Object);
                }

                var store = new InMemoryProtocolStore();
                if (seedKey)
                {
                    store.SaveApiKeyAsync(TestKey).GetAwaiter().GetResult();
                }

                RemoveServicesByType(services, typeof(IProtocolStore));
                services.AddSingleton<IProtocolStore>(store);

                services.PostConfigure<LlmGatewayOptions>(options =>
                {
                    options.Auth.RequireApiKey = requireApiKey;
                    options.Auth.RequestsPerMinutePerKey = requestsPerMinutePerKey;
                });
            });
        });

    [Fact]
    public async Task MissingKey_Returns401_WithOpenAiErrorBody()
    {
        using var factory = CreateFactory(requireApiKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_api_key");
    }

    [Fact]
    public async Task InvalidKey_Returns401()
    {
        using var factory = CreateFactory(requireApiKey: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "cbr_wrong");

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidBearerKey_Returns200()
    {
        using var factory = CreateFactory(requireApiKey: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestKey.Key);

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidXApiKeyHeader_Returns200()
    {
        using var factory = CreateFactory(requireApiKey: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", TestKey.Key);

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherV1Routes_InheritProtection_DefaultDeny()
    {
        using var factory = CreateFactory(requireApiKey: true);
        using var client = factory.CreateClient();

        // Two unrelated /v1 endpoints — both covered by the same path-prefix guard.
        (await client.GetAsync("/v1/models/diagnostics")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/v1/chat/completions", JsonContent.Create(new { model = "x", messages = Array.Empty<object>() })))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RequireApiKeyFalse_DevBypass_Allows()
    {
        using var factory = CreateFactory(requireApiKey: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AutoMode_NoKeysMinted_Allows()
    {
        using var factory = CreateFactory(requireApiKey: null, seedKey: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AutoMode_KeyExists_Enforces()
    {
        using var factory = CreateFactory(requireApiKey: null, seedKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PerKeyRateLimit_SecondRequest_Returns429WithRetryAfter()
    {
        using var factory = CreateFactory(requireApiKey: true, requestsPerMinutePerKey: 1);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestKey.Key);

        (await client.GetAsync("/v1/models")).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.GetAsync("/v1/models");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.Should().ContainKey("Retry-After");
        (await second.Content.ReadAsStringAsync()).Should().Contain("rate_limit_exceeded");
    }

    [Fact]
    public async Task AdminKeyList_RedactsKeyMaterial()
    {
        using var factory = CreateFactory(requireApiKey: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/keys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(TestKey.Key);
        body.Should().Contain(TestKey.Key[..8]);
    }

    private static void RemoveServicesByType(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var descriptor in descriptors)
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
