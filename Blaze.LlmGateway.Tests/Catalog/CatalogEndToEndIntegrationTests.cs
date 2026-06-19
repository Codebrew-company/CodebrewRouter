using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Blaze.LlmGateway.Tests.Catalog;

/// <summary>
/// End-to-end integration tests that validate the full catalog chain:
/// catalog → routing → health → deployment selection.
///
/// These tests build a real <see cref="InMemoryProviderCatalog"/> and
/// <see cref="CatalogModelRouter"/> wired together, then exercise the
/// complete flow from model name to selected deployment.
/// </summary>
public sealed class CatalogEndToEndIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static InMemoryProviderCatalog CreateCatalog(
        Action<ProviderCatalogOptions>? configure = null)
    {
        var opts = new ProviderCatalogOptions
        {
            DefaultRoutingStrategy = "round_robin",
            UnhealthyThreshold = 3,
            Deployments =
            [
                new() { Name = "primary-dep",   ModelName = "test-model", Provider = "AzureFoundry", Weight = 1, Priority = 1, Capabilities = ["chat", "tools"], Enabled = true },
                new() { Name = "fallback-dep",  ModelName = "test-model", Provider = "GithubModels",  Weight = 1, Priority = 2, Capabilities = ["chat", "tools"], Enabled = true },
            ],
            ModelRouting =
            {
                ["test-model"] = new()
                {
                    Strategy = "round_robin",
                    Deployments = ["primary-dep"],
                    Fallbacks = ["fallback-dep"]
                },
            }
        };
        configure?.Invoke(opts);
        return new InMemoryProviderCatalog(opts);
    }

    private static CatalogModelRouter CreateRouter(IProviderCatalog? catalog = null)
    {
        var cat = catalog ?? CreateCatalog();
        var resolver = new RoutingStrategyResolver(cat);
        return new CatalogModelRouter(cat, resolver, NullLogger<CatalogModelRouter>.Instance);
    }

    private static RoutingContext Ctx(string modelId = "test-model", bool tools = false, bool vision = false)
        => new(modelId, 100, false, tools, vision, CancellationToken.None);

    // ── Test 1: Healthy deployment selected ──────────────────────────────

    [Fact]
    public void SelectDeployment_WhenAllHealthy_SelectsPrimary()
    {
        var catalog = CreateCatalog();
        var router = CreateRouter(catalog);

        var result = router.SelectDeployment("test-model", Ctx());

        Assert.NotNull(result);
        Assert.Equal("primary-dep", result.Name);
    }

    // ── Test 2: Unhealthy deployment skipped, fallback used ──────────────

    [Fact]
    public void SelectDeployment_WhenPrimaryUnhealthy_SelectsFallback()
    {
        var catalog = CreateCatalog();
        var router = CreateRouter(catalog);

        // Mark primary unhealthy 3x (threshold=3)
        catalog.ReportHealth("primary-dep", false);
        catalog.ReportHealth("primary-dep", false);
        catalog.ReportHealth("primary-dep", false);

        Assert.False(catalog.IsHealthy("primary-dep"));

        var result = router.SelectDeployment("test-model", Ctx());

        Assert.NotNull(result);
        Assert.Equal("fallback-dep", result.Name);
    }

    // ── Test 3: CircuitBreakerChatClient throws when unhealthy ───────────

    [Fact]
    public async Task CircuitBreakerChatClient_WhenUnhealthy_ThrowsInvalidOperationException()
    {
        // Arrange
        var catalog = CreateCatalog();
        // Mark primary unhealthy
        catalog.ReportHealth("primary-dep", false);
        catalog.ReportHealth("primary-dep", false);
        catalog.ReportHealth("primary-dep", false);
        Assert.False(catalog.IsHealthy("primary-dep"));

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        var sut = new CircuitBreakerChatClient(innerMock.Object, catalog, "primary-dep");

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetResponseAsync(messages));

        Assert.Contains("primary-dep", ex.Message);
        Assert.Contains("circuit breaker", ex.Message, StringComparison.OrdinalIgnoreCase);
        innerMock.VerifyNoOtherCalls();
    }

    // ── Test 4: CatalogMetricsChatClient wraps and reports health ────────

    [Fact]
    public async Task CatalogMetricsChatClient_OnSuccess_ReportsHealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.IsHealthy("deploy-x")).Returns(true);
        catalogMock.Setup(c => c.ReportHealth("deploy-x", true));
        // LeastBusyStrategy.Release will be called on success
        catalogMock.Setup(c => c.ReportHealth("deploy-x", false)).Verifiable(Times.Never);

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"));
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var sut = new CatalogMetricsChatClient(innerMock.Object, catalogMock.Object, "deploy-x");

        // Act
        var result = await sut.GetResponseAsync(messages);

        // Assert
        Assert.Same(expectedResponse, result);
        catalogMock.Verify(c => c.ReportHealth("deploy-x", true), Times.Once);
    }

    [Fact]
    public async Task CatalogMetricsChatClient_OnFailure_ReportsUnhealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.IsHealthy("deploy-x")).Returns(true);
        catalogMock.Setup(c => c.ReportHealth("deploy-x", false));

        var innerMock = new Mock<IChatClient>(MockBehavior.Strict);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Provider down"));

        var sut = new CatalogMetricsChatClient(innerMock.Object, catalogMock.Object, "deploy-x");

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GetResponseAsync(messages));

        catalogMock.Verify(c => c.ReportHealth("deploy-x", false), Times.Once);
    }

    // ── Test 5: HealthProbeService probes and reports ────────────────────

    [Fact]
    public async Task HealthProbeService_ProbeDeployment_ReportsHealth()
    {
        // Arrange
        var deployment = new ProviderDeployment
        {
            Name = "probe-dep",
            ModelName = "test-model",
            Provider = "ProbeProvider"
        };

        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        catalogMock.Setup(c => c.ReportHealth("probe-dep", true));

        var chatClientMock = new Mock<IChatClient>(MockBehavior.Strict);
        chatClientMock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));
        chatClientMock.Setup(c => c.Dispose());

        var serviceProviderMock = new Mock<IServiceProvider>(MockBehavior.Strict);
        serviceProviderMock
            .As<IKeyedServiceProvider>()
            .Setup(sp => sp.GetKeyedService(typeof(IChatClient), "ProbeProvider"))
            .Returns(chatClientMock.Object);

        var options = Microsoft.Extensions.Options.Options.Create(new ProviderCatalogOptions
        {
            HealthCheckIntervalSeconds = 1
        });

        var logger = NullLogger<HealthProbeService>.Instance;

        var service = new HealthProbeService(
            catalogMock.Object,
            serviceProviderMock.Object,
            options,
            logger);

        // Act
        await service.ProbeAsync(CancellationToken.None);

        // Assert
        catalogMock.Verify(c => c.ReportHealth("probe-dep", true), Times.Once);
    }

    // ── Integration: Full pipeline through CatalogModelRouter ────────────

    [Fact]
    public void FullPipeline_ResolveViaRouter_HealthyToUnhealthyFallback()
    {
        // Build a populated catalog with a realistic model map
        var catalog = CreateCatalog(opts =>
        {
            // Add a third deployment for gemma-local
            opts.Deployments.Add(new ProviderDeploymentConfig
            {
                Name = "local-gemma-3b",
                ModelName = "gemma-local",
                Provider = "LocalGemma",
                Weight = 1,
                Priority = 10,
                MaxContextTokens = 4096,
                Capabilities = ["chat"],
                Tags = ["local", "offline"],
                Enabled = true
            });
            opts.ModelRouting["gemma-local"] = new ModelRouteConfig
            {
                Strategy = "round_robin",
                Deployments = ["local-gemma-3b"],
                Fallbacks = []
            };
        });

        var router = CreateRouter(catalog);

        // Resolve the gemma-local model (the P0.2 target)
        var result = router.SelectDeployment("gemma-local", Ctx("gemma-local"));
        Assert.NotNull(result);
        Assert.Equal("local-gemma-3b", result.Name);
        Assert.Equal("LocalGemma", result.Provider);

        // Also verify it is healthy by default
        Assert.True(catalog.IsHealthy("local-gemma-3b"));
    }
}
