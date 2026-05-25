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

    [Fact]
    public void FindVirtualModel_ExposesAgenticCapabilitiesForToolsSkillsMcpAndMemory()
    {
        var options = new LlmGatewayOptions
        {
            CodebrewRouter = new CodebrewRouterOptions
            {
                ModelId = "codebrewRouter",
                FallbackRules =
                {
                    ["General"] = ["LocalGemma"]
                }
            },
            VirtualModels =
            {
                ["codebrewSharpClient"] = new VirtualModelOptions
                {
                    ModelId = "codebrewSharpClient",
                    Extends = "codebrewRouter",
                    AgentMode = "chat-client-agent",
                    Workflow = "single",
                    Capabilities = ["chat", "tools", "mcp", "skills", "memory"],
                    ToolSupport = true,
                    VisionSupport = false,
                    CloudRequired = false,
                    ContextWindow = 32768,
                    McpServers = ["microsoft-learn"],
                    Skills = ["awesome-copilot", "superpowers"],
                    Memory = new VirtualModelMemoryOptions
                    {
                        Enabled = true,
                        Scope = "developer+repo"
                    }
                }
            }
        };

        var model = options.FindVirtualModel("codebrewSharpClient");

        model.Should().NotBeNull();
        model!.AgentMode.Should().Be("chat-client-agent");
        model.Workflow.Should().Be("single");
        model.Capabilities.Should().Contain(["chat", "tools", "mcp", "skills", "memory"]);
        model.ToolSupport.Should().BeTrue();
        model.VisionSupport.Should().BeFalse();
        model.CloudRequired.Should().BeFalse();
        model.ContextWindow.Should().Be(32768);
        model.McpServers.Should().Equal("microsoft-learn");
        model.Skills.Should().Equal("awesome-copilot", "superpowers");
        model.Memory.Should().NotBeNull();
        model.Memory!.Enabled.Should().BeTrue();
        model.Memory.Scope.Should().Be("developer+repo");
    }
}
