using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Verifies that <see cref="CodebrewRouterChatClient"/> routes to the correct provider
/// for every <see cref="TaskType"/>, and that fallback chains work correctly when
/// the primary provider fails.
///
/// All tests are pure unit tests — no HTTP server or real LLM credentials are needed.
/// Configuration is wired through <see cref="LlmGatewayOptions"/> and
/// <see cref="CodebrewRouterOptions"/> exactly as production code does.
/// </summary>
public class CodebrewRouterPerProviderTests
{
    // ── Default FallbackRules from CodebrewRouterOptions ──────────────────────
    //
    //   Coding:                GithubModels → AzureFoundry → FoundryLocal
    //   Reasoning / Research
    //   Creative / DataAnalysis
    //   VisionObjectDetection
    //   General:              AzureFoundry → GithubModels → FoundryLocal

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static List<ChatMessage> UserMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

    private static Mock<IChatClient> ClientReturning(string text)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TextResponse(text));
        return mock;
    }

    private static Mock<IChatClient> ClientThrowing()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Provider unavailable"));
        return mock;
    }

    private static Mock<ITaskClassifier> ClassifierFor(TaskType taskType)
    {
        var mock = new Mock<ITaskClassifier>();
        mock.Setup(c => c.ClassifyAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(taskType);
        return mock;
    }

    /// <summary>
    /// Creates a <see cref="CodebrewRouterChatClient"/> with explicitly provided options,
    /// so every test controls exactly which providers are "configured" and which DI keys exist.
    /// </summary>
    private static CodebrewRouterChatClient CreateRouter(
        IChatClient innerClient,
        ITaskClassifier classifier,
        IServiceProvider serviceProvider,
        CodebrewRouterOptions? routerOptions = null,
        LlmGatewayOptions? gatewayOptions = null)
    {
        var opts        = Options.Create(routerOptions ?? new CodebrewRouterOptions());
        var gatewayOpts = Options.Create(gatewayOptions ?? new LlmGatewayOptions());
        var logger      = new Mock<ILogger<CodebrewRouterChatClient>>().Object;
        return new CodebrewRouterChatClient(innerClient, classifier, opts, gatewayOpts, serviceProvider, logger);
    }

    /// <summary>
    /// Builds a <see cref="LlmGatewayOptions"/> where AzureFoundry, GithubModels, and
    /// FoundryLocal are all "configured" so CodebrewRouterChatClient.IsProviderConfigured
    /// returns true for all three.
    /// </summary>
    private static LlmGatewayOptions AllProvidersConfigured() =>
        new()
        {
            Providers =
            {
                AzureFoundry  = { Endpoint = "https://test.openai.azure.com/", Model = "gpt-4o", ApiKey = "test-key" },
                FoundryLocal  = { Endpoint = "http://127.0.0.1:58484",         Model = "Phi-4-mini-instruct-cuda-gpu:5" },
                GithubModels  = { Endpoint = "https://models.inference.ai.azure.com", Model = "gpt-4o-mini", ApiKey = "test-github-pat" },
                OllamaLocal   = { BaseUrl  = "http://127.0.0.1:11434",         Model = "gemma4:e4b" }
            }
        };

    // ── TaskType.Coding → GithubModels first ─────────────────────────────────

    [Fact]
    public async Task Coding_UsesGithubModels_WhenItSucceeds()
    {
        var githubModels = ClientReturning("GitHub Models response");
        var azureFoundry = ClientReturning("AzureFoundry response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Write a sorting algorithm"));

        Assert.Equal("GitHub Models response", result.Text);
        githubModels.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        azureFoundry.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Coding_FallsBackToAzureFoundry_WhenGithubModelsFails()
    {
        var githubModels = ClientThrowing();
        var azureFoundry = ClientReturning("AzureFoundry fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Debug this code"));

        Assert.Equal("AzureFoundry fallback", result.Text);
        githubModels.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        azureFoundry.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Coding_FallsBackToFoundryLocal_WhenGithubModelsAndAzureFoundryFail()
    {
        var githubModels = ClientThrowing();
        var azureFoundry = ClientThrowing();
        var foundryLocal = ClientReturning("FoundryLocal fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("FoundryLocal", foundryLocal.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Refactor this class"));

        Assert.Equal("FoundryLocal fallback", result.Text);
    }

    [Fact]
    public async Task Coding_FallsBackToInnerClient_WhenAllProvidersFail()
    {
        var githubModels = ClientThrowing();
        var azureFoundry = ClientThrowing();
        var foundryLocal = ClientThrowing();
        var innerClient  = ClientReturning("Inner fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("FoundryLocal", foundryLocal.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            innerClient.Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Implement a linked list"));

        Assert.Equal("Inner fallback", result.Text);
    }

    // ── TaskType.Reasoning → AzureFoundry first ───────────────────────────────

    [Fact]
    public async Task Reasoning_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry reasoning response");
        var githubModels = ClientReturning("GithubModels response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Reasoning).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Prove the Pythagorean theorem"));

        Assert.Equal("AzureFoundry reasoning response", result.Text);
        azureFoundry.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        githubModels.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Reasoning_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels reasoning fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Reasoning).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Solve this logic puzzle"));

        Assert.Equal("GithubModels reasoning fallback", result.Text);
    }

    [Fact]
    public async Task Reasoning_FallsBackToFoundryLocal_WhenAzureFoundryAndGithubModelsFail()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientThrowing();
        var foundryLocal = ClientReturning("FoundryLocal reasoning fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("FoundryLocal", foundryLocal.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Reasoning).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Deduce the answer step by step"));

        Assert.Equal("FoundryLocal reasoning fallback", result.Text);
    }

    // ── TaskType.Research → AzureFoundry first ────────────────────────────────

    [Fact]
    public async Task Research_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry research response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Research).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Survey the literature on transformers"));

        Assert.Equal("AzureFoundry research response", result.Text);
    }

    [Fact]
    public async Task Research_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels research fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Research).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Compare React and Angular in depth"));

        Assert.Equal("GithubModels research fallback", result.Text);
    }

    // ── TaskType.General → AzureFoundry first ─────────────────────────────────

    [Fact]
    public async Task General_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry general response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.General).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Tell me something interesting"));

        Assert.Equal("AzureFoundry general response", result.Text);
    }

    [Fact]
    public async Task General_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels general fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.General).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("What is the weather like?"));

        Assert.Equal("GithubModels general fallback", result.Text);
    }

    [Fact]
    public async Task General_FallsBackToFoundryLocal_WhenAzureFoundryAndGithubModelsFail()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientThrowing();
        var foundryLocal = ClientReturning("FoundryLocal general fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("FoundryLocal", foundryLocal.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.General).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Hello, how are you?"));

        Assert.Equal("FoundryLocal general fallback", result.Text);
    }

    // ── TaskType.Creative → AzureFoundry first ────────────────────────────────

    [Fact]
    public async Task Creative_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry creative response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Creative).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Write a poem about autumn"));

        Assert.Equal("AzureFoundry creative response", result.Text);
    }

    [Fact]
    public async Task Creative_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels creative fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Creative).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Write a short story about space"));

        Assert.Equal("GithubModels creative fallback", result.Text);
    }

    // ── TaskType.DataAnalysis → AzureFoundry first ────────────────────────────

    [Fact]
    public async Task DataAnalysis_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry data analysis response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.DataAnalysis).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Analyze this dataset for trends"));

        Assert.Equal("AzureFoundry data analysis response", result.Text);
    }

    [Fact]
    public async Task DataAnalysis_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels data analysis fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.DataAnalysis).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Write a SQL query to aggregate orders"));

        Assert.Equal("GithubModels data analysis fallback", result.Text);
    }

    // ── TaskType.VisionObjectDetection → AzureFoundry first ──────────────────

    [Fact]
    public async Task VisionObjectDetection_UsesAzureFoundry_WhenItSucceeds()
    {
        var azureFoundry = ClientReturning("AzureFoundry vision response");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.VisionObjectDetection).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Detect objects in this image"));

        Assert.Equal("AzureFoundry vision response", result.Text);
    }

    [Fact]
    public async Task VisionObjectDetection_FallsBackToGithubModels_WhenAzureFoundryFails()
    {
        var azureFoundry = ClientThrowing();
        var githubModels = ClientReturning("GithubModels vision fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.VisionObjectDetection).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("Describe what is in this screenshot"));

        Assert.Equal("GithubModels vision fallback", result.Text);
    }

    // ── Provider configuration gating ─────────────────────────────────────────

    /// <summary>
    /// When GithubModels has no API key, IsProviderConfigured returns false and it is skipped.
    /// Coding falls through to AzureFoundry (second in the chain).
    /// </summary>
    [Fact]
    public async Task Coding_SkipsGithubModels_WhenApiKeyNotConfigured()
    {
        var githubModels = ClientReturning("GitHub Models (should NOT be called)");
        var azureFoundry = ClientReturning("AzureFoundry selected");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        // GithubModels has no ApiKey → IsProviderConfigured returns false → skipped
        var gatewayOptions = AllProvidersConfigured();
        gatewayOptions.Providers.GithubModels.ApiKey = null;

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: gatewayOptions);

        var result = await router.GetResponseAsync(UserMessages("Fix this bug"));

        Assert.Equal("AzureFoundry selected", result.Text);
        githubModels.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When AzureFoundry has no endpoint, it is skipped.
    /// General task falls through to GithubModels (second in chain).
    /// </summary>
    [Fact]
    public async Task General_SkipsAzureFoundry_WhenEndpointNotConfigured()
    {
        var azureFoundry = ClientReturning("AzureFoundry (should NOT be called)");
        var githubModels = ClientReturning("GithubModels selected");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        // AzureFoundry has no endpoint → IsProviderConfigured returns false → skipped
        var gatewayOptions = AllProvidersConfigured();
        gatewayOptions.Providers.AzureFoundry.Endpoint = "";

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.General).Object,
            sp,
            gatewayOptions: gatewayOptions);

        var result = await router.GetResponseAsync(UserMessages("Hello"));

        Assert.Equal("GithubModels selected", result.Text);
        azureFoundry.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When all named providers are unconfigured, CodebrewRouter falls back to InnerClient.
    /// </summary>
    [Fact]
    public async Task AnyTaskType_FallsBackToInnerClient_WhenAllProvidersUnconfigured()
    {
        var innerClient = ClientReturning("Inner fallback");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", ClientThrowing().Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", ClientThrowing().Object)
            .AddKeyedSingleton<IChatClient>("FoundryLocal", ClientThrowing().Object)
            .BuildServiceProvider();

        // All endpoints/keys cleared — no provider passes IsProviderConfigured
        var gatewayOptions = new LlmGatewayOptions();
        gatewayOptions.Providers.AzureFoundry.Endpoint  = "";
        gatewayOptions.Providers.GithubModels.ApiKey    = null;
        gatewayOptions.Providers.FoundryLocal.Endpoint  = "";

        var router = CreateRouter(
            innerClient.Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: gatewayOptions);

        var result = await router.GetResponseAsync(UserMessages("write code"));

        Assert.Equal("Inner fallback", result.Text);
    }

    // ── Streaming: TaskType.Coding → GithubModels first ──────────────────────

    [Fact]
    public async Task Coding_Streaming_YieldsChunksFromGithubModels_WhenItSucceeds()
    {
        var u1 = new ChatResponseUpdate(ChatRole.Assistant, "def ");
        var u2 = new ChatResponseUpdate(ChatRole.Assistant, "fizzbuzz");

        var githubModels = new Mock<IChatClient>();
        githubModels.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(StreamOf(u1, u2));

        var azureFoundry = new Mock<IChatClient>();

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in router.GetStreamingResponseAsync(UserMessages("FizzBuzz in Python")))
            chunks.Add(chunk);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("def ", chunks[0].Text);
        Assert.Equal("fizzbuzz", chunks[1].Text);

        azureFoundry.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Coding_Streaming_FallsBackToAzureFoundry_WhenGithubModelsFailsOnFirstChunk()
    {
        var successUpdate = new ChatResponseUpdate(ChatRole.Assistant, "AzureFoundry streamed");

        var githubModels = new Mock<IChatClient>();
        githubModels.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingStream());

        var azureFoundry = new Mock<IChatClient>();
        azureFoundry.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(StreamOf(successUpdate));

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in router.GetStreamingResponseAsync(UserMessages("implement merge sort")))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("AzureFoundry streamed", chunks[0].Text);
    }

    [Fact]
    public async Task General_Streaming_YieldsChunksFromAzureFoundry_WhenItSucceeds()
    {
        var u1 = new ChatResponseUpdate(ChatRole.Assistant, "Hello ");
        var u2 = new ChatResponseUpdate(ChatRole.Assistant, "World");

        var azureFoundry = new Mock<IChatClient>();
        azureFoundry.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(StreamOf(u1, u2));

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", azureFoundry.Object)
            .BuildServiceProvider();

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.General).Object,
            sp,
            gatewayOptions: AllProvidersConfigured());

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in router.GetStreamingResponseAsync(UserMessages("say hello")))
            chunks.Add(chunk);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hello ", chunks[0].Text);
        Assert.Equal("World", chunks[1].Text);
    }

    // ── Custom fallback rules ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that custom FallbackRules override the defaults.
    /// When a custom rule maps Coding to only FoundryLocal, that provider is used first.
    /// </summary>
    [Fact]
    public async Task Coding_RespectsCustomFallbackRules_WhenOverridden()
    {
        var foundryLocal = ClientReturning("FoundryLocal via custom rule");
        var githubModels = ClientReturning("GithubModels (should NOT be called)");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("FoundryLocal", foundryLocal.Object)
            .AddKeyedSingleton<IChatClient>("GithubModels", githubModels.Object)
            .BuildServiceProvider();

        var customOptions = new CodebrewRouterOptions
        {
            FallbackRules = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Coding"]  = ["FoundryLocal"],
                ["General"] = ["AzureFoundry", "FoundryLocal"]
            }
        };

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.Coding).Object,
            sp,
            routerOptions: customOptions,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("write a test"));

        Assert.Equal("FoundryLocal via custom rule", result.Text);
        githubModels.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When a TaskType has no matching rule but "General" exists,
    /// CodebrewRouter falls back to the General chain.
    /// </summary>
    [Fact]
    public async Task UnknownTaskType_UsesGeneralChain_WhenPresent()
    {
        var generalProvider = ClientReturning("General chain used");

        var sp = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>("AzureFoundry", generalProvider.Object)
            .BuildServiceProvider();

        var customOptions = new CodebrewRouterOptions
        {
            FallbackRules = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // No "DataAnalysis" rule — should fall back to General
                ["General"] = ["AzureFoundry"]
            }
        };

        var router = CreateRouter(
            new Mock<IChatClient>().Object,
            ClassifierFor(TaskType.DataAnalysis).Object,
            sp,
            routerOptions: customOptions,
            gatewayOptions: AllProvidersConfigured());

        var result = await router.GetResponseAsync(UserMessages("analyze this dataset"));

        Assert.Equal("General chain used", result.Text);
    }

    // ── Streaming helpers ─────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamOf(
        params ChatResponseUpdate[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingStream()
    {
        await Task.Yield();
        throw new InvalidOperationException("Provider stream failed on first chunk");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
