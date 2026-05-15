using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests;

public sealed class ModelSelectionResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenOfflineOnlyAndCodebrewRouterRequested_ReturnsCodebrewRouterWithoutCatalogLookup()
    {
        var services = new ServiceCollection();
        var localGemma = new StubChatClient();
        var codebrewRouter = new StubChatClient();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", localGemma);
        services.AddKeyedSingleton<IChatClient>("CodebrewRouter", codebrewRouter);
        var provider = services.BuildServiceProvider();

        var catalog = new ThrowingModelCatalog();
        var resolver = new ModelSelectionResolver(
            provider,
            catalog,
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = new CodebrewRouterOptions { ModelId = "codebrewRouter" }
            }),
            new StubTokenCounter(),
            new NoopContextCompactor(),
            Options.Create(new ContextSizingOptions()),
            NullLogger<ModelSelectionResolver>.Instance,
            NullLogger<ContextSizingChatClient>.Instance);

        var resolved = await resolver.ResolveAsync("codebrewRouter");

        Assert.Same(codebrewRouter, resolved);
        Assert.False(catalog.WasCalled);
    }

    [Fact]
    public async Task ResolveAsync_WhenOfflineOnlyAndDirectModelRequested_ReturnsLocalGemmaWithoutCatalogLookup()
    {
        var services = new ServiceCollection();
        var localGemma = new StubChatClient();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", localGemma);
        var provider = services.BuildServiceProvider();

        var catalog = new ThrowingModelCatalog();
        var resolver = new ModelSelectionResolver(
            provider,
            catalog,
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = new CodebrewRouterOptions { ModelId = "codebrewRouter" }
            }),
            new StubTokenCounter(),
            new NoopContextCompactor(),
            Options.Create(new ContextSizingOptions()),
            NullLogger<ModelSelectionResolver>.Instance,
            NullLogger<ContextSizingChatClient>.Instance);

        var resolved = await resolver.ResolveAsync("local-gemma");

        Assert.Same(localGemma, resolved);
        Assert.False(catalog.WasCalled);
    }

    [Fact]
    public async Task ResolveAsync_WhenOfflineOnlyAndConfiguredVirtualModelRequested_ReturnsCodebrewRouterWithoutCatalogLookup()
    {
        var services = new ServiceCollection();
        var localGemma = new StubChatClient();
        var codebrewRouter = new StubChatClient();
        services.AddKeyedSingleton<IChatClient>("LocalGemma", localGemma);
        services.AddKeyedSingleton<IChatClient>("CodebrewRouter", codebrewRouter);
        var provider = services.BuildServiceProvider();

        var catalog = new ThrowingModelCatalog();
        var resolver = new ModelSelectionResolver(
            provider,
            catalog,
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = true,
                CodebrewRouter = new CodebrewRouterOptions { ModelId = "codebrewRouter" },
                VirtualModels =
                {
                    ["yardly"] = new VirtualModelOptions
                    {
                        ModelId = "yardly",
                        FallbackRules =
                        {
                            ["General"] = ["LocalGemma"]
                        }
                    }
                }
            }),
            new StubTokenCounter(),
            new NoopContextCompactor(),
            Options.Create(new ContextSizingOptions()),
            NullLogger<ModelSelectionResolver>.Instance,
            NullLogger<ContextSizingChatClient>.Instance);

        var resolved = await resolver.ResolveAsync("yardly");

        Assert.Same(codebrewRouter, resolved);
        Assert.False(catalog.WasCalled);
    }

    [Fact]
    public async Task ResolveAsync_WhenConfiguredVirtualModelRequested_ReturnsCodebrewRouterWithoutCatalogLookup()
    {
        var services = new ServiceCollection();
        var codebrewRouter = new StubChatClient();
        services.AddKeyedSingleton<IChatClient>("CodebrewRouter", codebrewRouter);
        var provider = services.BuildServiceProvider();

        var catalog = new ThrowingModelCatalog();
        var resolver = new ModelSelectionResolver(
            provider,
            catalog,
            Options.Create(new LlmGatewayOptions
            {
                OfflineOnly = false,
                VirtualModels =
                {
                    ["yardly"] = new VirtualModelOptions
                    {
                        ModelId = "yardly",
                        FallbackRules =
                        {
                            ["General"] = ["LmStudio"]
                        }
                    }
                }
            }),
            new StubTokenCounter(),
            new NoopContextCompactor(),
            Options.Create(new ContextSizingOptions()),
            NullLogger<ModelSelectionResolver>.Instance,
            NullLogger<ContextSizingChatClient>.Instance);

        var resolved = await resolver.ResolveAsync("yardly");

        Assert.Same(codebrewRouter, resolved);
        Assert.False(catalog.WasCalled);
    }

    private sealed class ThrowingModelCatalog : IModelCatalog
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Offline mode must not query the model catalog.");
        }

        public Task<AvailableModel?> FindByIdAsync(string modelId, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Offline mode must not query the model catalog.");
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "local")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "local");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubTokenCounter : ITokenCounter
    {
        public int CountTokens(IEnumerable<ChatMessage> messages, string? modelId = null) => 0;
    }
}
