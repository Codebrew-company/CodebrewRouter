using Blaze.LlmGateway.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Blaze.LlmGateway.Tests.Security;

public sealed class ProviderCertificateValidationPolicyTests
{
    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Production", false, false)]
    public void ShouldAllowInvalidProviderCertificates_RequiresDevelopmentAndExplicitFlag(
        string environmentName,
        bool configured,
        bool expected)
    {
        var environment = new TestHostEnvironment { EnvironmentName = environmentName };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:AllowInvalidProviderCertificates"] = configured.ToString()
            })
            .Build();

        ProviderCertificateValidationPolicy
            .ShouldAllowInvalidProviderCertificates(environment, configuration)
            .Should().Be(expected);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
