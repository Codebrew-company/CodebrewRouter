using System.Net;
using Blaze.LlmGateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Blaze.LlmGateway.Tests.Dashboard;

/// <summary>
/// P4.5: the dashboard's CLI-onboarding surface is served publicly (the static shell;
/// data still hides behind the admin guard) and carries copy-paste recipes for the
/// supported coding tools.
/// </summary>
public sealed class DashboardOnboardingTests
{
    private static WebApplicationFactory<ApiProgram> CreateFactory()
        => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LlmGateway:LocalInference:Enabled", "false");
            builder.UseSetting("LlmGateway:LocalInference:WarmupEnabled", "false");
            builder.UseSetting("LlmGateway:LocalInference:BlockStartupUntilWarm", "false");
        });

    [Fact]
    public async Task Dashboard_ServesOnboardingRecipesForEveryTool()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await response.Content.ReadAsStringAsync();

        foreach (var tool in new[] { "Claude Code", "Codex CLI", "Cursor", "Cline", "OpenCode", "GitHub Copilot CLI" })
        {
            html.Should().Contain(tool, $"the onboarding page must show a recipe for {tool}");
        }

        html.Should().Contain("ANTHROPIC_BASE_URL", "Claude Code connects via ANTHROPIC_BASE_URL");
        html.Should().Contain("OPENAI_BASE_URL", "OpenAI-compatible tools connect via OPENAI_BASE_URL");
        html.Should().Contain("button.copy", "recipes are copy-paste with a copy control");
    }
}
