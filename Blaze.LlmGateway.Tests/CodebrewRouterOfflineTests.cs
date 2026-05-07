using System.Runtime.CompilerServices;
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
}
