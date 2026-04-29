using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Verifies the gemma4:e4b prompt-cleanup pre-stage is wired correctly into
/// <see cref="CodebrewRouterChatClient"/>: the cleaner runs once per request, the cleaned
/// message is forwarded to both the classifier and the downstream provider, and prior
/// turns are passed through untouched.
/// </summary>
public class CodebrewRouterChatClientPromptCleanupTests
{
    private const string OriginalUserText =
        "Hi! I was wondering, please could you possibly help me figure out the right way " +
        "to read a file in C# without using async, like for a small config file? Thanks!";
    private const string CleanedUserText = "Read a small config file in C# synchronously.";

    private static IList<ChatMessage>? _classifierSawMessages;
    private static IList<ChatMessage>? _providerSawMessages;

    private sealed class StubCleaner(string returnText) : IPromptCleaner
    {
        public int CallCount { get; private set; }
        public Task<string> CleanAsync(string original, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(returnText);
        }
    }

    private static IModelAvailabilityRegistry AllAvailable()
    {
        var reg = new ModelAvailabilityRegistry();
        reg.UpdateSnapshot([], [
            new ProviderAvailabilitySnapshot("AzureFoundry", true, null, DateTimeOffset.UtcNow),
            new ProviderAvailabilitySnapshot("FoundryLocal", true, null, DateTimeOffset.UtcNow),
            new ProviderAvailabilitySnapshot("GithubModels", true, null, DateTimeOffset.UtcNow),
            new ProviderAvailabilitySnapshot("OllamaLocal", true, null, DateTimeOffset.UtcNow),
        ]);
        return reg;
    }

    private static LlmGatewayOptions AllProvidersConfigured() => new()
    {
        Providers = new ProvidersOptions
        {
            AzureFoundry = new AzureFoundryOptions { Endpoint = "https://x", Model = "gpt-x", ApiKey = "k" },
            FoundryLocal = new FoundryLocalOptions { Endpoint = "http://localhost", Model = "phi" },
            GithubModels = new GithubModelsOptions { Endpoint = "https://x", Model = "gpt-x", ApiKey = "k" }
        }
    };

    private static (CodebrewRouterChatClient router, Mock<IChatClient> azure, Mock<ITaskClassifier> classifier)
        BuildRouter(IPromptCleaner cleaner)
    {
        _classifierSawMessages = null;
        _providerSawMessages = null;

        var classifier = new Mock<ITaskClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, CancellationToken>((m, _) => _classifierSawMessages = m.ToList())
            .ReturnsAsync(TaskType.General);

        var azure = new Mock<IChatClient>();
        azure
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((m, _, _) => _providerSawMessages = m.ToList())
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azure.Object)
            .BuildServiceProvider();

        var inner = new Mock<IChatClient>().Object;
        var tokenCounter = new Mock<Blaze.LlmGateway.Infrastructure.TokenCounting.ITokenCounter>().Object;
        var router = new CodebrewRouterChatClient(
            inner,
            classifier.Object,
            cleaner,
            new NoopContextCompactor(),
            tokenCounter,
            Options.Create(new CodebrewRouterOptions()),
            Options.Create(AllProvidersConfigured()),
            AllAvailable(),
            sp,
            NullLogger<CodebrewRouterChatClient>.Instance);

        return (router, azure, classifier);
    }

    [Fact]
    public async Task Cleaner_replaces_last_user_message_for_classifier_and_provider()
    {
        var cleaner = new StubCleaner(CleanedUserText);
        var (router, azure, classifier) = BuildRouter(cleaner);

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, OriginalUserText)]);

        Assert.Equal(1, cleaner.CallCount);
        Assert.NotNull(_classifierSawMessages);
        Assert.NotNull(_providerSawMessages);
        Assert.Equal(CleanedUserText, _classifierSawMessages![^1].Text);
        Assert.Equal(CleanedUserText, _providerSawMessages![^1].Text);
    }

    [Fact]
    public async Task Cleaner_preserves_prior_turns_unchanged()
    {
        var cleaner = new StubCleaner(CleanedUserText);
        var (router, _, _) = BuildRouter(cleaner);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "you are helpful"),
            new(ChatRole.User, "earlier question"),
            new(ChatRole.Assistant, "earlier answer"),
            new(ChatRole.User, OriginalUserText),
        };

        await router.GetResponseAsync(messages);

        Assert.NotNull(_providerSawMessages);
        Assert.Equal(4, _providerSawMessages!.Count);
        Assert.Equal("you are helpful", _providerSawMessages[0].Text);
        Assert.Equal("earlier question", _providerSawMessages[1].Text);
        Assert.Equal("earlier answer", _providerSawMessages[2].Text);
        Assert.Equal(CleanedUserText, _providerSawMessages[3].Text);
    }

    [Fact]
    public async Task Cleaner_runs_only_once_per_request()
    {
        var cleaner = new StubCleaner(CleanedUserText);
        var (router, _, _) = BuildRouter(cleaner);

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, OriginalUserText)]);

        Assert.Equal(1, cleaner.CallCount);
    }

    [Fact]
    public async Task Noop_cleaner_leaves_original_message_intact()
    {
        var (router, _, _) = BuildRouter(new NoopPromptCleaner());

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, OriginalUserText)]);

        Assert.NotNull(_providerSawMessages);
        Assert.Equal(OriginalUserText, _providerSawMessages![^1].Text);
    }

    [Fact]
    public async Task Cleaner_returning_original_does_not_clone_message_list()
    {
        // When cleaner returns the same string, downstream still sees the original text.
        var cleaner = new StubCleaner(OriginalUserText);
        var (router, _, _) = BuildRouter(cleaner);

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, OriginalUserText)]);

        Assert.NotNull(_providerSawMessages);
        Assert.Equal(OriginalUserText, _providerSawMessages![^1].Text);
    }
}
