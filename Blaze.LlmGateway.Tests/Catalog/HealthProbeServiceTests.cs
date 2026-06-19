using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Blaze.LlmGateway.Tests.Catalog;

public class HealthProbeServiceTests
{
    private readonly Mock<IProviderCatalog> _catalogMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<ILogger<HealthProbeService>> _loggerMock;
    private readonly ProviderCatalogOptions _options;

    public HealthProbeServiceTests()
    {
        _catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        _serviceProviderMock = new Mock<IServiceProvider>(MockBehavior.Strict);
        _loggerMock = new Mock<ILogger<HealthProbeService>>();
        _options = new ProviderCatalogOptions
        {
            HealthCheckIntervalSeconds = 1
        };
    }

    private HealthProbeService CreateService()
    {
        var optionsMock = new Mock<IOptions<ProviderCatalogOptions>>();
        optionsMock.Setup(o => o.Value).Returns(_options);
        return new HealthProbeService(
            _catalogMock.Object,
            _serviceProviderMock.Object,
            optionsMock.Object,
            _loggerMock.Object);
    }

    private static Mock<IChatClient> CreateMockChatClient()
    {
        var mock = new Mock<IChatClient>(MockBehavior.Strict);
        mock.Setup(c => c.Dispose());
        return mock;
    }

    private void SetupKeyedService(string providerName, IChatClient? client)
    {
        _serviceProviderMock
            .As<IKeyedServiceProvider>()
            .Setup(sp => sp.GetKeyedService(typeof(IChatClient), providerName))
            .Returns(client);
    }

    // ── Test 1: Successful probe reports healthy ──────────────────────────

    [Fact]
    public async Task ProbeAsync_WhenProbeSucceeds_ReportsHealthy()
    {
        var deployment = new ProviderDeployment
        {
            Name = "test-deploy",
            ModelName = "test-model",
            Provider = "TestProvider"
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        _catalogMock.Setup(c => c.ReportHealth("test-deploy", true));

        var chatClient = CreateMockChatClient();
        chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));

        SetupKeyedService("TestProvider", chatClient.Object);

        var service = CreateService();
        await service.ProbeAsync(default);

        _catalogMock.Verify(c => c.ReportHealth("test-deploy", true), Times.Once);
    }

    // ── Test 2: Exception during probe reports unhealthy ──────────────────

    [Fact]
    public async Task ProbeAsync_WhenProbeThrows_ReportsUnhealthy()
    {
        var deployment = new ProviderDeployment
        {
            Name = "faulty-deploy",
            ModelName = "test-model",
            Provider = "FaultyProvider"
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        _catalogMock.Setup(c => c.ReportHealth("faulty-deploy", false));

        var chatClient = CreateMockChatClient();
        chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        SetupKeyedService("FaultyProvider", chatClient.Object);

        var service = CreateService();
        await service.ProbeAsync(default);

        _catalogMock.Verify(c => c.ReportHealth("faulty-deploy", false), Times.Once);
    }

    // ── Test 3: Timeout during probe reports unhealthy ────────────────────

    [Fact]
    public async Task ProbeAsync_WhenProbeTimesOut_ReportsUnhealthy()
    {
        var deployment = new ProviderDeployment
        {
            Name = "slow-deploy",
            ModelName = "test-model",
            Provider = "SlowProvider"
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        _catalogMock.Setup(c => c.ReportHealth("slow-deploy", false));

        var chatClient = CreateMockChatClient();
        chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<ChatMessage[], ChatOptions?, CancellationToken>(async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong"));
            });

        SetupKeyedService("SlowProvider", chatClient.Object);

        var service = CreateService();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await service.ProbeAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        _catalogMock.Verify(c => c.ReportHealth("slow-deploy", false), Times.Once);
    }

    // ── Test 4: No keyed client for provider skips deployment ─────────────

    [Fact]
    public async Task ProbeAsync_WhenNoKeyedClientForProvider_SkipsDeployment()
    {
        var deployment = new ProviderDeployment
        {
            Name = "unregistered-deploy",
            ModelName = "test-model",
            Provider = "UnknownProvider"
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);

        SetupKeyedService("UnknownProvider", null);

        var service = CreateService();
        await service.ProbeAsync(default);

        _catalogMock.Verify(c => c.ReportHealth(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    // ── Test 5: ExecuteAsync probes all deployments and loops ─────────────

    [Fact]
    public async Task ExecuteAsync_ProbesAllDeploymentsAndStopsOnCancellation()
    {
        var deployments = new[]
        {
            new ProviderDeployment { Name = "deploy-a", ModelName = "m1", Provider = "ProviderA" },
            new ProviderDeployment { Name = "deploy-b", ModelName = "m2", Provider = "ProviderB" }
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns(deployments);
        _catalogMock.Setup(c => c.ReportHealth("deploy-a", true));
        _catalogMock.Setup(c => c.ReportHealth("deploy-b", true));

        var chatClientA = CreateMockChatClient();
        chatClientA
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));

        var chatClientB = CreateMockChatClient();
        chatClientB
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));

        SetupKeyedService("ProviderA", chatClientA.Object);
        SetupKeyedService("ProviderB", chatClientB.Object);

        var service = CreateService();

        using var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _catalogMock.Verify(c => c.ReportHealth("deploy-a", true), Times.AtLeastOnce);
        _catalogMock.Verify(c => c.ReportHealth("deploy-b", true), Times.AtLeastOnce);
    }

    // ── Test 6: ExecuteAsync uses configured interval ─────────────────────

    [Fact]
    public async Task ExecuteAsync_WaitsConfiguredIntervalBetweenCycles()
    {
        _options.HealthCheckIntervalSeconds = 1;

        var deployment = new ProviderDeployment
        {
            Name = "cycle-deploy",
            ModelName = "test-model",
            Provider = "CycleProvider"
        };
        _catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        _catalogMock.Setup(c => c.ReportHealth("cycle-deploy", true));

        var chatClient = CreateMockChatClient();
        chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));

        SetupKeyedService("CycleProvider", chatClient.Object);

        var service = CreateService();

        using var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(3.5));
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }

        _catalogMock.Verify(c => c.ReportHealth("cycle-deploy", true), Times.AtLeast(2));
    }
}
