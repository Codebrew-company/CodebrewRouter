using Aspire.Hosting.ApplicationModel;
using Blaze.LlmGateway.AppHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class AppHostCompositionTests
{
    [Fact]
    public void Build_DoesNotThrow_WhenFoundryLocalIsEnabled()
    {
        using var app = AppHostComposition.Build([]);

        Assert.NotNull(app);
    }

    [Fact]
    public void AppHostComposition_WiresLocalInferenceWarmupEnvironment()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "AppHostComposition.cs"));

        Assert.Contains("LlmGateway__LocalInference__ModelPath", source);
        Assert.Contains("LlmGateway__LocalInference__CacheDirectory", source);
        Assert.Contains("LlmGateway__LocalInference__DownloadTimeoutSeconds", source);
        Assert.Contains("LlmGateway__LocalInference__WarmupEnabled", source);
        Assert.Contains("LlmGateway__LocalInference__BlockStartupUntilWarm", source);
        Assert.Contains("LlmGateway__LocalInference__WarmupTimeoutSeconds", source);
    }

    [Fact]
    public void AppHostComposition_DelaysDevUiResourcesUntilApiIsReady()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "AppHostComposition.cs"));

        // openwebui + scalar are containers that must NOT WaitFor the host gateway (it trips
        // Aspire's container tunnel); they reach it via host.docker.internal / its own UIs.
        Assert.DoesNotContain("AddScalarApiReference", source);
        Assert.Contains("host.docker.internal", source);
    }

    [Fact]
    public void AgentDevUi_DefaultsToCodebrewSharpClientVirtualModel()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "AppHostComposition.cs"));
        var appHostConfig = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "appsettings.json"));
        var agentSource = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "devui-agents", "gateway_agent", "agent.py"));

        Assert.Contains("DevUI:AgentModel", source);
        Assert.Contains("\"BLAZE_GATEWAY_MODEL\", agentDevUiModel", source);
        Assert.Contains("\"AgentModel\": \"codebrewSharpClient\"", appHostConfig);
        Assert.Contains("os.environ.get(\"BLAZE_GATEWAY_MODEL\", \"codebrewSharpClient\")", agentSource);
    }

    [Fact]
    public void OpenWebUi_UsesCurrentReleaseTagFromConfiguration()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "AppHostComposition.cs"));
        var appHostConfig = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "appsettings.json"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        const string openWebUiRelease = "v0.10.2";

        Assert.Contains("DevUI:OpenWebUIImageTag", source);
        Assert.Contains($"\"OpenWebUIImageTag\": \"{openWebUiRelease}\"", appHostConfig);
        Assert.Contains($"ghcr.io/open-webui/open-webui:{openWebUiRelease}", readme);
    }

    [Fact]
    public async Task OpenWebUi_IsCreatedInCodebrewRouterDockerDesktopGroup()
    {
        using var app = AppHostComposition.Build([]);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var openWebUi = model.Resources.Single(resource => resource.Name == "openwebui");
        var annotation = openWebUi.Annotations
            .OfType<ContainerRuntimeArgsCallbackAnnotation>()
            .Single();
        var args = new List<object>();

        await annotation.Callback(new ContainerRuntimeArgsCallbackContext(args, CancellationToken.None));

        Assert.Equal(
            [
                "--label", "com.docker.compose.project=CodebrewRouter",
                "--label", "com.docker.compose.service=openwebui",
                "--label", "com.docker.compose.container-number=1"
            ],
            args);
    }

    [Fact]
    public void ServiceDefaults_ReadinessEndpointTreatsDegradedAsNotReady()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.ServiceDefaults", "Extensions.cs"));

        Assert.Contains("ResultStatusCodes", source);
        Assert.Contains("[HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable", source);
    }

    [Fact]
    public void DefaultLocalInferenceConfig_UsesGemma4RemoteBootstrap()
    {
        var root = FindRepositoryRoot();
        var appHostConfig = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.AppHost", "appsettings.json"));
        var apiConfig = File.ReadAllText(Path.Combine(root, "Blaze.LlmGateway.Api", "appsettings.json"));
        const string gemma4Url =
            "https://huggingface.co/lm-kit/gemma-4-e4b-instruct-lmk/resolve/main/Gemma-4-E4B-It-7.5B-Q4_K_M.lmk";

        Assert.Contains($"\"ModelPath\": \"{gemma4Url}\"", appHostConfig);
        Assert.Contains("\"CacheDirectory\": \".llm-cache\"", appHostConfig);
        Assert.Contains("\"DownloadTimeoutSeconds\": 3600", appHostConfig);
        Assert.Contains("\"BlockStartupUntilWarm\": true", appHostConfig);

        Assert.Contains($"\"ModelPath\": \"{gemma4Url}\"", apiConfig);
        Assert.Contains("\"CacheDirectory\": \".llm-cache\"", apiConfig);
        Assert.Contains("\"DownloadTimeoutSeconds\": 3600", apiConfig);
        Assert.Contains("\"BlockStartupUntilWarm\": true", apiConfig);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Blaze.LlmGateway.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
