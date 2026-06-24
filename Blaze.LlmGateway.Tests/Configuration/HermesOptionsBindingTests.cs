using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Blaze.LlmGateway.Tests.Configuration;

public class HermesOptionsBindingTests
{
    [Fact]
    public void HermesOptions_BindFromConfiguration()
    {
        // Arrange
        var json = @"
        {
          ""LlmGateway"": {
            ""Providers"": {
              ""Hermes"": {
                ""Host"": ""10.0.0.5"",
                ""ApiKey"": ""global-test-key"",
                ""Profiles"": {
                  ""default"": { ""Port"": 8642, ""Enabled"": true },
                  ""derp-coder"": { 
                    ""Port"": 8644, 
                    ""Enabled"": false, 
                    ""Host"": ""192.168.1.10"", 
                    ""Model"": ""custom-model"",
                    ""Endpoint"": ""https://my-custom-endpoint.com/v1"",
                    ""ApiKey"": ""override-key"",
                    ""MaxContextTokens"": 64000,
                    ""Capabilities"": [""chat"", ""tools""]
                  }
                }
              }
            }
          }
        }";

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var gatewayOptions = new LlmGatewayOptions();

        // Act
        configuration.GetSection(LlmGatewayOptions.SectionName).Bind(gatewayOptions);

        // Assert
        var hermesOpts = gatewayOptions.Providers.Hermes;
        Assert.NotNull(hermesOpts);
        Assert.Equal("10.0.0.5", hermesOpts.Host);
        Assert.Equal("global-test-key", hermesOpts.ApiKey);
        Assert.Equal(2, hermesOpts.Profiles.Count);

        var defaultProfile = hermesOpts.Profiles["default"];
        Assert.Equal(8642, defaultProfile.Port);
        Assert.True(defaultProfile.Enabled);

        var derpCoderProfile = hermesOpts.Profiles["derp-coder"];
        Assert.Equal(8644, derpCoderProfile.Port);
        Assert.False(derpCoderProfile.Enabled);
        Assert.Equal("192.168.1.10", derpCoderProfile.Host);
        Assert.Equal("custom-model", derpCoderProfile.Model);
        Assert.Equal("https://my-custom-endpoint.com/v1", derpCoderProfile.Endpoint);
        Assert.Equal("override-key", derpCoderProfile.ApiKey);
        Assert.Equal(64000, derpCoderProfile.MaxContextTokens);
        Assert.Equal(2, derpCoderProfile.Capabilities.Length);
        Assert.Contains("chat", derpCoderProfile.Capabilities);
        Assert.Contains("tools", derpCoderProfile.Capabilities);
    }
}
