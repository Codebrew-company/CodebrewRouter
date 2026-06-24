using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.Catalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests.Infrastructure;

public class HermesProviderRegistrationTests
{
    [Fact]
    public void HermesProfiles_RegisterAsKeyedChatClients()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        
        // Register required dependencies for ContextSizingChatClient
        var mockTokenCounter = new Mock<Blaze.LlmGateway.Infrastructure.TokenCounting.ITokenCounter>();
        var mockCompactor = new Mock<IContextCompactor>();
        
        services.AddSingleton(mockTokenCounter.Object);
        services.AddSingleton(mockCompactor.Object);
        services.AddSingleton<ILogger<MockChatClient>>(NullLogger<MockChatClient>.Instance);
        services.AddSingleton<ILogger<ContextSizingChatClient>>(NullLogger<ContextSizingChatClient>.Instance);

        var options = new LlmGatewayOptions();
        options.Providers.Hermes = new HermesProviderOptions
        {
            Host = "localhost",
            ApiKey = "test-key",
            Profiles = new Dictionary<string, HermesProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                { "derp-coder", new HermesProfileOptions { Port = 8644, Enabled = true } },
                { "derp-trainer", new HermesProfileOptions { Port = 8643, Enabled = false } },
                { "gemma-mlx", new HermesProfileOptions 
                    { 
                        Endpoint = "https://api.openai.com/v1", 
                        Model = "gpt-4o", 
                        Enabled = true 
                    } 
                }
            }
        };

        services.Configure<LlmGatewayOptions>(opts =>
        {
            opts.Providers.Hermes = options.Providers.Hermes;
        });

        // Act
        services.AddLlmProviders();
        var sp = services.BuildServiceProvider();

        // Assert
        // "derp-coder" is enabled, so it should be registered
        var derpCoderClient = sp.GetKeyedService<IChatClient>("Hermes_DerpCoder");
        Assert.NotNull(derpCoderClient);

        // "derp-trainer" is disabled, so resolving it should fall back to MockChatClient or return null
        // Inside our registration, if it's disabled, we return MockChatClient
        var derpTrainerClient = sp.GetKeyedService<IChatClient>("Hermes_DerpTrainer");
        Assert.Null(derpTrainerClient); // It was not registered in the loop because Enabled was false

        // "gemma-mlx" is enabled, so it should be registered
        var gemmaMlxClient = sp.GetKeyedService<IChatClient>("Hermes_GemmaMlx");
        Assert.NotNull(gemmaMlxClient);
    }

    [Fact]
    public void HermesCatalogPopulator_GeneratesCorrectDeployments()
    {
        // Arrange
        var hermesOpts = new HermesProviderOptions
        {
            Host = "localhost",
            ApiKey = "global-key",
            Profiles = new Dictionary<string, HermesProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                { "default", new HermesProfileOptions { Port = 8642, Enabled = true } },
                { "derp-coder", new HermesProfileOptions 
                    { 
                        Port = 8644, 
                        Enabled = true,
                        ApiKey = "coder-specific-key"
                    } 
                },
                { "disabled-profile", new HermesProfileOptions { Port = 9999, Enabled = false } },
                { "cloud-mlx", new HermesProfileOptions 
                    { 
                        Endpoint = "https://api.openai.com/v1", 
                        Model = "gpt-4o", 
                        Enabled = true,
                        Capabilities = ["chat", "vision"]
                    } 
                }
            }
        };

        // Act
        var deployments = HermesCatalogPopulator.GenerateDeployments(hermesOpts);

        // Assert
        Assert.Equal(3, deployments.Count);

        // Verify Default profile deployment
        var defaultDep = Assert.Single(deployments, d => d.Name == "hermes-default-gw");
        Assert.Equal("hermes-default", defaultDep.ModelName);
        Assert.Equal("Hermes_Default", defaultDep.Provider);
        Assert.Equal("http://localhost:8642/v1", defaultDep.Endpoint);
        Assert.Equal("global-key", defaultDep.ApiKey);
        Assert.Null(defaultDep.Model);

        // Verify DerpCoder profile deployment with overriden API key
        var coderDep = Assert.Single(deployments, d => d.Name == "hermes-derp-coder-gw");
        Assert.Equal("hermes-derp-coder", coderDep.ModelName);
        Assert.Equal("Hermes_DerpCoder", coderDep.Provider);
        Assert.Equal("http://localhost:8644/v1", coderDep.Endpoint);
        Assert.Equal("coder-specific-key", coderDep.ApiKey);
        Assert.Null(coderDep.Model);

        // Verify cloud-mlx profile deployment with overrides
        var cloudDep = Assert.Single(deployments, d => d.Name == "hermes-cloud-mlx-gw");
        Assert.Equal("hermes-cloud-mlx", cloudDep.ModelName);
        Assert.Equal("Hermes_CloudMlx", cloudDep.Provider);
        Assert.Equal("https://api.openai.com/v1", cloudDep.Endpoint);
        Assert.Equal("gpt-4o", cloudDep.Model);
        Assert.Contains("chat", cloudDep.Capabilities);
        Assert.Contains("vision", cloudDep.Capabilities);
    }

    [Fact]
    public async Task HermesHealthProbe_HeadMethod_ReportsHealthy()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        var serviceProviderMock = new Mock<IServiceProvider>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<HealthProbeService>>();

        var deployment = new ProviderDeployment
        {
            Name = "hermes-default-gw",
            ModelName = "hermes-default",
            Provider = "Hermes_Default",
            Endpoint = "http://localhost:8642/v1"
        };

        catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        catalogMock.Setup(c => c.ReportHealth("hermes-default-gw", true));

        var catalogOptions = Options.Create(new ProviderCatalogOptions
        {
            HealthCheckMethod = "head",
            HealthCheckIntervalSeconds = 1
        });

        var gatewayOptions = Options.Create(new LlmGatewayOptions
        {
            Providers = new ProvidersOptions
            {
                Hermes = new HermesProviderOptions
                {
                    HealthCheckPath = "/health"
                }
            }
        });

        var factory = CreateMockHttpClientFactory(HttpStatusCode.OK);

        var service = new HealthProbeService(
            catalogMock.Object,
            serviceProviderMock.Object,
            catalogOptions,
            gatewayOptions,
            factory,
            loggerMock.Object);

        // Act
        await service.ProbeAsync(default);

        // Assert
        catalogMock.Verify(c => c.ReportHealth("hermes-default-gw", true), Times.Once);
    }

    [Fact]
    public async Task HermesHealthProbe_HeadMethod_ReportsUnhealthy_OnHttpError()
    {
        // Arrange
        var catalogMock = new Mock<IProviderCatalog>(MockBehavior.Strict);
        var serviceProviderMock = new Mock<IServiceProvider>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<HealthProbeService>>();

        var deployment = new ProviderDeployment
        {
            Name = "hermes-default-gw",
            ModelName = "hermes-default",
            Provider = "Hermes_Default",
            Endpoint = "http://localhost:8642/v1"
        };

        catalogMock.Setup(c => c.GetAllDeployments()).Returns([deployment]);
        catalogMock.Setup(c => c.ReportHealth("hermes-default-gw", false));

        var catalogOptions = Options.Create(new ProviderCatalogOptions
        {
            HealthCheckMethod = "head",
            HealthCheckIntervalSeconds = 1
        });

        var gatewayOptions = Options.Create(new LlmGatewayOptions
        {
            Providers = new ProvidersOptions
            {
                Hermes = new HermesProviderOptions
                {
                    HealthCheckPath = "/health"
                }
            }
        });

        var factory = CreateMockHttpClientFactory(HttpStatusCode.InternalServerError);

        var service = new HealthProbeService(
            catalogMock.Object,
            serviceProviderMock.Object,
            catalogOptions,
            gatewayOptions,
            factory,
            loggerMock.Object);

        // Act
        await service.ProbeAsync(default);

        // Assert
        catalogMock.Verify(c => c.ReportHealth("hermes-default-gw", false), Times.Once);
    }

    private IHttpClientFactory CreateMockHttpClientFactory(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler(statusCode);
        var client = new HttpClient(handler);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        return factoryMock.Object;
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
