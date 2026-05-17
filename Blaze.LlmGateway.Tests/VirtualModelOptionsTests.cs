using Blaze.LlmGateway.Core.Configuration;
using FluentAssertions;

namespace Blaze.LlmGateway.Tests;

public sealed class VirtualModelOptionsTests
{
    [Fact]
    public void FindVirtualModel_WhenCodebrewSharpClientExtendsCodebrewRouter_InheritsRouterFallbacksAndContext()
    {
        var options = new LlmGatewayOptions
        {
            CodebrewRouter = new CodebrewRouterOptions
            {
                ModelId = "codebrewRouter",
                FallbackRules =
                {
                    ["General"] = ["LocalGemma"],
                    ["Coding"] = ["OpenCodeGo_DeepSeekV4Pro", "LocalGemma"]
                },
                ContextCompaction = new ContextCompactionOptions
                {
                    TargetBudgetRatio = 0.72d,
                    PreserveMostRecentMessages = 8
                }
            },
            VirtualModels =
            {
                ["codebrewSharpClient"] = new VirtualModelOptions
                {
                    ModelId = "codebrewSharpClient",
                    Extends = "codebrewRouter",
                    SystemPrompt = "You are a C# assistant."
                }
            }
        };

        var codebrewSharpClient = options.FindVirtualModel("codebrewSharpClient");

        codebrewSharpClient.Should().NotBeNull();
        codebrewSharpClient!.Extends.Should().Be("codebrewRouter");
        codebrewSharpClient.FallbackRules["General"].Should().Equal("LocalGemma");
        codebrewSharpClient.FallbackRules["Coding"].Should().Equal("OpenCodeGo_DeepSeekV4Pro", "LocalGemma");
        codebrewSharpClient.ContextCompaction.Should().NotBeNull();
        codebrewSharpClient.ContextCompaction!.TargetBudgetRatio.Should().Be(0.72d);
        codebrewSharpClient.ContextCompaction.PreserveMostRecentMessages.Should().Be(8);
    }
}
