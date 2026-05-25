using System.Runtime.CompilerServices;
using System.Text.Json;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests;

public sealed class CodebrewRouterOfflineTests
{
    [Fact]
    public async Task Streaming_WhenOfflineLocalGemmaIsNotLoaded_ReportsLocalGemmaConfigurationError()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>(
            "LocalGemma",
            new ThrowingChatClient("LocalGemma is not loaded because LlmGateway:LocalInference:ModelPath is not configured. Set it to a local Gemma GGUF file."));
        var serviceProvider = services.BuildServiceProvider();

        var client = new CodebrewRouterChatClient(
            new ThrowingChatClient("No currently available backing provider is available for codebrewRouter."),
            new FixedTaskClassifier(TaskType.General),
            new NoopPromptCleaner(),
            new NoopContextCompactor(),
            new FixedTokenCounter(),
            Options.Create(new CodebrewRouterOptions
            {
                ModelId = "codebrewRouter",
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }),
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = new CodebrewRouterOptions { ModelId = "codebrewRouter" }
            }),
            new AlwaysAvailableRegistry(),
            serviceProvider,
            NullLogger<CodebrewRouterChatClient>.Instance);

        var action = async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                _ = update;
            }
        };

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*codebrewRouter*currently unavailable*LocalGemma*ModelPath*Gemma*GGUF*");
    }

    [Fact]
    public async Task Streaming_WhenOfflineLocalGemmaCompletesWithoutVisibleChunks_ReturnsRetryableAssistantMessage()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", new EmptyStreamingChatClient());
        var serviceProvider = services.BuildServiceProvider();

        var client = new CodebrewRouterChatClient(
            new ThrowingChatClient("No currently available backing provider is available for codebrewRouter."),
            new FixedTaskClassifier(TaskType.General),
            new NoopPromptCleaner(),
            new NoopContextCompactor(),
            new FixedTokenCounter(),
            Options.Create(new CodebrewRouterOptions
            {
                ModelId = "codebrewRouter",
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }),
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = new CodebrewRouterOptions { ModelId = "codebrewRouter" }
            }),
            new AlwaysAvailableRegistry(),
            serviceProvider,
            NullLogger<CodebrewRouterChatClient>.Instance);

        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Contain("couldn't produce a visible response");
    }

    [Fact]
    public async Task Streaming_WhenPlannerProviderFirstEmitsNoVisibleChunks_RetriesWithFinalAnswerInstruction()
    {
        var provider = new EmptyThenVisibleStreamingChatClient("Planner result.");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", provider);
        var serviceProvider = services.BuildServiceProvider();

        var routerOptions = new CodebrewRouterOptions
        {
            ModelId = "codebrewRouter",
            FallbackRules = new Dictionary<string, string[]>
            {
                ["General"] = ["LocalGemma"]
            }
        };

        var client = new CodebrewRouterChatClient(
            new ThrowingChatClient("No currently available backing provider is available for codebrewRouter."),
            new FixedTaskClassifier(TaskType.General),
            new NoopPromptCleaner(),
            new NoopContextCompactor(),
            new FixedTokenCounter(),
            Options.Create(routerOptions),
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = routerOptions,
                VirtualModels =
                {
                    ["codebrewPlanner"] = new VirtualModelOptions
                    {
                        ModelId = "codebrewPlanner",
                        Extends = "codebrewRouter",
                        SystemPrompt = "Return only the final useful plan."
                    }
                }
            }),
            new AlwaysAvailableRegistry(),
            serviceProvider,
            NullLogger<CodebrewRouterChatClient>.Instance);

        var chunks = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "make a plan")],
                           new ChatOptions { ModelId = "codebrewPlanner" }))
        {
            chunks.Add(update.Text ?? string.Empty);
        }

        string.Concat(chunks).Should().Be("Planner result.");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Select(message => message.Text ?? string.Empty)
            .Should().Contain(text => text.Contains("final visible answer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AvailabilitySeed_WhenLocalGemmaModelPathMissing_DisablesCodebrewRouterWithSpecificReason()
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var registry = new ModelAvailabilityRegistry();
        var options = Options.Create(new LlmGatewayOptions
        {
            OfflineOnly = true,
            LocalInference = new LocalInferenceOptions
            {
                Enabled = true,
                ModelPath = string.Empty
            },
            Availability = new ModelAvailabilityOptions
            {
                Enabled = false
            },
            CodebrewRouter = new CodebrewRouterOptions
            {
                Enabled = true,
                ModelId = "codebrewRouter",
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }
        });
        var heartbeat = new ModelAvailabilityHeartbeatService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new LmStudioModelDiscovery(new HttpClient(), NullLogger<LmStudioModelDiscovery>.Instance),
            registry,
            NullLogger<ModelAvailabilityHeartbeatService>.Instance);

        await heartbeat.StartAsync(CancellationToken.None);

        var localGemma = registry.FindModel("local-gemma", includeUnavailable: true);
        localGemma.Should().NotBeNull();
        localGemma!.Enabled.Should().BeFalse();
        localGemma.ErrorMessage.Should().Contain("ModelPath");

        var codebrewRouter = registry.FindModel("codebrewRouter", includeUnavailable: true);
        codebrewRouter.Should().NotBeNull();
        codebrewRouter!.Enabled.Should().BeFalse();
        codebrewRouter.ErrorMessage.Should().Contain("LocalGemma");
        codebrewRouter.ErrorMessage.Should().Contain("ModelPath");
    }

    [Fact]
    public async Task AvailabilitySeed_WhenLocalGemmaModelPathIsRemoteUrl_EnablesLocalGemmaAndCodebrewRouter()
    {
        const string gemma4Url =
            "https://huggingface.co/lm-kit/gemma-4-e4b-instruct-lmk/resolve/main/Gemma-4-E4B-It-7.5B-Q4_K_M.lmk";
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var registry = new ModelAvailabilityRegistry();
        var options = Options.Create(new LlmGatewayOptions
        {
            OfflineOnly = true,
            LocalInference = new LocalInferenceOptions
            {
                Enabled = true,
                ModelPath = gemma4Url
            },
            Availability = new ModelAvailabilityOptions
            {
                Enabled = false
            },
            CodebrewRouter = new CodebrewRouterOptions
            {
                Enabled = true,
                ModelId = "codebrewRouter",
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }
        });
        var heartbeat = new ModelAvailabilityHeartbeatService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new LmStudioModelDiscovery(new HttpClient(), NullLogger<LmStudioModelDiscovery>.Instance),
            registry,
            NullLogger<ModelAvailabilityHeartbeatService>.Instance);

        await heartbeat.StartAsync(CancellationToken.None);

        var localGemma = registry.FindModel("local-gemma", includeUnavailable: true);
        localGemma.Should().NotBeNull();
        localGemma!.Enabled.Should().BeTrue();
        localGemma.ErrorMessage.Should().BeNull();

        var codebrewRouter = registry.FindModel("codebrewRouter", includeUnavailable: true);
        codebrewRouter.Should().NotBeNull();
        codebrewRouter!.Enabled.Should().BeTrue();
        codebrewRouter.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ModelsEndpoint_WhenOfflineLocalGemmaUnavailable_ReturnsConfiguredModelsWithError()
    {
        var registry = CreateOfflineUnavailableRegistry();
        var result = await ModelsEndpoint.HandleAsync(
            new EmptyModelCatalog(),
            registry,
            Options.Create(CreateOfflineOptions()),
            CancellationToken.None);

        using var json = await ExecuteJsonAsync(result);
        var data = json.RootElement.GetProperty("data").EnumerateArray().ToArray();

        data.Should().NotBeEmpty();
        var codebrewRouter = data.Should()
            .Contain(model => model.GetProperty("id").GetString() == "codebrewRouter")
            .Subject;
        codebrewRouter.GetProperty("enabled").GetBoolean().Should().BeFalse();
        codebrewRouter.GetProperty("errorMessage").GetString().Should().Contain("LocalGemma");

        var localGemma = data.Should()
            .Contain(model => model.GetProperty("id").GetString() == "local-gemma")
            .Subject;
        localGemma.GetProperty("enabled").GetBoolean().Should().BeFalse();
        localGemma.GetProperty("errorMessage").GetString().Should().Contain("ModelPath");
    }

    [Fact]
    public async Task CodebrewRouterDetails_WhenLocalGemmaUnavailable_ReturnsConfiguredFallbackRule()
    {
        var registry = CreateOfflineUnavailableRegistry();
        var result = await ModelsEndpoint.HandleCodebrewRouterAsync(
            new EmptyModelCatalog(),
            registry,
            Options.Create(CreateOfflineOptions()),
            CancellationToken.None);

        using var json = await ExecuteJsonAsync(result);

        json.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("errorMessage").GetString().Should().Contain("LocalGemma");

        var generalRule = json.RootElement.GetProperty("fallbackRules")
            .EnumerateArray()
            .Single(rule => rule.GetProperty("taskType").GetString() == "General");
        generalRule.GetProperty("providers")
            .EnumerateArray()
            .Select(provider => provider.GetString())
            .Should()
            .Contain("LocalGemma");
    }

    private static LlmGatewayOptions CreateOfflineOptions()
        => new()
        {
            OfflineOnly = true,
            CodebrewRouter = new CodebrewRouterOptions
            {
                Enabled = true,
                ModelId = "codebrewRouter",
                FallbackRules = new Dictionary<string, string[]>
                {
                    ["General"] = ["LocalGemma"]
                }
            }
        };

    private static ModelAvailabilityRegistry CreateOfflineUnavailableRegistry()
    {
        var registry = new ModelAvailabilityRegistry();
        var checkedAt = DateTimeOffset.UtcNow;
        const string localGemmaError = "LocalGemma is not loaded because LlmGateway:LocalInference:ModelPath is not configured. Set it to a local Gemma GGUF file.";
        const string codebrewRouterError = $"No backing provider is currently available. LocalGemma: {localGemmaError}";

        registry.UpdateSnapshot(
            [
                new AvailableModel(
                    "local-gemma",
                    "LocalGemma",
                    "lmkit",
                    "configured",
                    Enabled: false,
                    ErrorMessage: localGemmaError,
                    LastCheckedUtc: checkedAt),
                new AvailableModel(
                    "codebrewRouter",
                    "CodebrewRouter",
                    "codebrew",
                    "virtual",
                    Enabled: false,
                    ErrorMessage: codebrewRouterError,
                    LastCheckedUtc: checkedAt)
            ],
            [
                new ProviderAvailabilitySnapshot("LocalGemma", false, localGemmaError, checkedAt),
                new ProviderAvailabilitySnapshot("CodebrewRouter", false, codebrewRouterError, checkedAt)
            ]);

        return registry;
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
                .BuildServiceProvider()
        };
        await using var body = new MemoryStream();
        httpContext.Response.Body = body;

        await result.ExecuteAsync(httpContext);

        body.Position = 0;
        return await JsonDocument.ParseAsync(body);
    }

    private sealed class ThrowingChatClient(string message) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
            {
                FinishReason = ChatFinishReason.Stop
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyThenVisibleStreamingChatClient(string visibleText) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(chatMessages.ToList());
            var text = Requests.Count == 1 ? string.Empty : visibleText;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(chatMessages.ToList());
            await Task.Yield();

            if (Requests.Count > 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, visibleText)
                {
                    FinishReason = ChatFinishReason.Stop
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTaskClassifier(TaskType taskType) : ITaskClassifier
    {
        public Task<TaskType> ClassifyAsync(
            IEnumerable<ChatMessage> messages,
            CancellationToken cancellationToken = default)
            => Task.FromResult(taskType);
    }

    private sealed class FixedTokenCounter : ITokenCounter
    {
        public int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null) => 0;
    }

    private sealed class AlwaysAvailableRegistry : IModelAvailabilityRegistry
    {
        public IReadOnlyList<AvailableModel> GetModels(bool includeUnavailable = false) => [];

        public AvailableModel? FindModel(string modelId, bool includeUnavailable = false) => null;

        public bool IsProviderAvailable(string provider) => true;

        public string? GetProviderError(string provider) => null;
    }

    private sealed class EmptyModelCatalog : IModelCatalog
    {
        public Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AvailableModel>>([]);

        public Task<AvailableModel?> FindByIdAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult<AvailableModel?>(null);
    }
}

