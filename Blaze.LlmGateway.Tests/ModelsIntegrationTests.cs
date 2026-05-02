using System.Text.Json;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Integration tests for GET /v1/models endpoint.
/// Tests model discovery, provider detection, and response structure.
/// </summary>
public class ModelsIntegrationTests : IAsyncLifetime
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
                    RemoveServicesByType(services, typeof(IChatClient));

                    var mockChatClient = new Mock<IChatClient>();
                    mockChatClient
                        .Setup(c => c.GetStreamingResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .Returns(GetStreamingResponse());
                    
                    mockChatClient
                        .Setup(c => c.GetResponseAsync(
                            It.IsAny<IEnumerable<ChatMessage>>(),
                            It.IsAny<ChatOptions>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new ChatResponse(
                            [new ChatMessage(ChatRole.Assistant, "response")]));

                    services.AddSingleton(mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("AzureFoundry", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("FoundryLocal", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("GithubModels", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("OllamaLocal", mockChatClient.Object);
                    services.AddKeyedSingleton<IChatClient>("LmStudio", mockChatClient.Object);
                    services.AddSingleton<IModelCatalog>(new FakeModelCatalog());
                    
                    // Configure LM Studio with a valid model for seeding/fallback rules
                    // (Endpoint set empty to prevent discovery network calls in tests)
                    services.Configure<LlmGatewayOptions>(options =>
                    {
                        options.Providers.LmStudio.Endpoint = "";
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

    [Fact]
    public async Task Models_GetRequest_ReturnsJsonList()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        Assert.True(json.RootElement.TryGetProperty("object", out var objectProp));
        Assert.Equal("list", objectProp.GetString());
        Assert.True(json.RootElement.TryGetProperty("data", out var dataProp));
        Assert.Equal(JsonValueKind.Array, dataProp.ValueKind);
    }

    [Fact]
    public async Task Models_ResponseStructure_HasCorrectFormat()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("object", out _));
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("id", out _), "Model missing 'id'");
            Assert.True(model.TryGetProperty("object", out _), "Model missing 'object'");
            Assert.True(model.TryGetProperty("provider", out _), "Model missing 'provider'");
            Assert.True(model.TryGetProperty("source", out _), "Model missing 'source'");
        }
    }

    [Fact]
    public async Task Models_DataArray_NotEmpty()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));
        var modelCount = data.GetArrayLength();
        Assert.True(modelCount > 0, "Models list should not be empty");
    }

    [Fact]
    public async Task Models_ContainsKnownProviders()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));
        var providers = new HashSet<string>();

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("provider", out var provider))
            {
                providers.Add(provider.GetString() ?? "");
            }
        }

        var knownProviders = new[] { "AzureFoundry", "FoundryLocal", "GithubModels", "OllamaLocal", "LmStudio" };
        var hasKnownProvider = providers.Any(p => knownProviders.Contains(p));
        Assert.True(hasKnownProvider, $"No known providers found. Found: {string.Join(", ", providers)}");
    }

    [Fact]
    public async Task Models_EachModelHasObject_EqualToModel()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("object", out var objectProp));
            Assert.Equal("model", objectProp.GetString());
        }
    }

    [Fact]
    public async Task Models_IdField_NotEmpty()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("id", out var idProp));
            var id = idProp.GetString();
            Assert.False(string.IsNullOrWhiteSpace(id), "Model ID should not be empty");
        }
    }

    [Fact]
    public async Task Models_ProviderField_NotEmpty()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("provider", out var providerProp));
            var provider = providerProp.GetString();
            Assert.False(string.IsNullOrWhiteSpace(provider), "Provider should not be empty");
        }
    }

    [Fact]
    public async Task Models_ContentType_IsApplicationJson()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");

        // Assert
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Models_ResponseIsValidJson()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        // Should not throw
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.NotNull(json);
    }

    [Fact]
    public async Task Models_MultipleModels_AllHaveConsistentStructure()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        int count = 0;
        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("id", out _));
            Assert.True(model.TryGetProperty("object", out _));
            Assert.True(model.TryGetProperty("provider", out _));
            count++;
        }

        Assert.True(count > 0, "Should have at least one model");
    }

    [Fact]
    public async Task Models_OwnedByField_IsOptional()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));
        var modelCount = data.GetArrayLength();
        Assert.True(modelCount > 0);
    }

    [Fact]
    public async Task Models_ProviderNames_AreValid()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        var validProviders = new[] { "AzureFoundry", "FoundryLocal", "GithubModels", "OllamaLocal", "LmStudio", "CodebrewRouter" };

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("provider", out var providerProp));
            var provider = providerProp.GetString();
            Assert.Contains(provider, validProviders);
        }
    }

    [Fact]
    public async Task Models_ConsistentObjectProperty_AcrossAllModels()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Assert
        Assert.True(json.RootElement.TryGetProperty("data", out var data));

        foreach (var model in data.EnumerateArray())
        {
            Assert.True(model.TryGetProperty("object", out var obj));
            Assert.Equal("model", obj.GetString());
        }
    }

    [Fact]
    public async Task Models_ResponseStatus_IsOk()
    {
        // Act
        var response = await _client!.GetAsync("/v1/models");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CodebrewRouterModelDetails_ReturnsVirtualModelMetadata()
    {
        var response = await _client!.GetAsync("/v1/models/codebrewRouter");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("codebrewRouter", json.RootElement.GetProperty("id").GetString());
        Assert.Equal("model", json.RootElement.GetProperty("object").GetString());
        Assert.Equal("CodebrewRouter", json.RootElement.GetProperty("provider").GetString());
        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task CodebrewRouterModelDetails_ReturnsConfiguredFallbackRules()
    {
        var response = await _client!.GetAsync("/v1/models/codebrewRouter");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var fallbackRules = json.RootElement.GetProperty("fallbackRules");
        Assert.True(fallbackRules.GetArrayLength() > 0);

        var generalRule = fallbackRules.EnumerateArray()
            .Single(rule => string.Equals(rule.GetProperty("taskType").GetString(), "General", StringComparison.OrdinalIgnoreCase));
        var providers = generalRule.GetProperty("providers")
            .EnumerateArray()
            .Select(provider => provider.GetString())
            .ToArray();

        Assert.Contains("AzureFoundry", providers);
        Assert.Contains("LmStudio", providers);
        Assert.DoesNotContain("FoundryLocal", providers);
    }

    [Fact]
    public async Task CodebrewRouterModelDetails_ReturnsBackingProviderModels()
    {
        var response = await _client!.GetAsync("/v1/models/codebrewRouter");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var backingModels = json.RootElement.GetProperty("backingModels");
        Assert.True(backingModels.GetArrayLength() > 0);

        var providers = backingModels.EnumerateArray()
            .Select(model => model.GetProperty("provider").GetString())
            .ToHashSet();

        Assert.Contains("AzureFoundry", providers);
        Assert.Contains("LmStudio", providers);
        Assert.DoesNotContain("FoundryLocal", providers);
    }

    [Fact]
    public async Task ModelDiagnostics_ReturnsUnavailableProvidersWithReasons()
    {
        var registry = _factory!.Services.GetRequiredService<Blaze.LlmGateway.Api.ModelAvailabilityRegistry>();
        var checkedAt = DateTimeOffset.UtcNow;
        registry.UpdateSnapshot(
            [
                new AvailableModel("gpt-4o", "AzureFoundry", "openai", "configured", "https://example", Enabled: true, LastCheckedUtc: checkedAt),
                new AvailableModel("local-model", "LmStudio", "lmstudio", "configured", "http://192.168.16.56:1234/v1", Enabled: false, ErrorMessage: "Connection refused", LastCheckedUtc: checkedAt)
            ],
            [
                new Blaze.LlmGateway.Api.ProviderAvailabilitySnapshot("AzureFoundry", true, null, checkedAt),
                new Blaze.LlmGateway.Api.ProviderAvailabilitySnapshot("LmStudio", false, "Connection refused", checkedAt)
            ]);

        var response = await _client!.GetAsync("/v1/models/diagnostics");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("degraded", json.RootElement.GetProperty("status").GetString());

        var lmStudio = json.RootElement.GetProperty("models")
            .EnumerateArray()
            .Single(model => model.GetProperty("provider").GetString() == "LmStudio");

        Assert.False(lmStudio.GetProperty("enabled").GetBoolean());
        Assert.Equal("Connection refused", lmStudio.GetProperty("errorMessage").GetString());
    }

    private static void RemoveServicesByType(IServiceCollection services, Type serviceType)
    {
        var descriptors = services.Where(d => d.ServiceType == serviceType).ToList();
        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponse()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "response");
        await Task.CompletedTask;
    }

    private sealed class FakeModelCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AvailableModel>>([
                new AvailableModel("gpt-4o", "AzureFoundry", "openai", "configured"),
                new AvailableModel("Phi-4-mini-instruct-cuda-gpu:5", "FoundryLocal", "openai", "configured"),
                new AvailableModel("gemma4:e4b", "OllamaLocal", "ollama", "live"),
                new AvailableModel("local-model", "LmStudio", "lmstudio", "configured")
            ]);

        public Task<AvailableModel?> FindByIdAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult<AvailableModel?>(null);
    }
}
