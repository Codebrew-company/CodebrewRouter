using Blaze.LlmGateway.AppHost;
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
        var normalizedSource = source.Replace("\r\n", "\n");

        Assert.Contains(
            "builder.AddScalarApiReference()\n            .WithApiReference(api)\n            .WaitFor(api)",
            normalizedSource);
        Assert.True(
            CountOccurrences(source, ".WaitFor(api)") >= 3,
            "Scalar, OpenWebUI, and Agent DevUI should all wait for API readiness.");
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

        const string openWebUiRelease = "v0.9.5";

        Assert.Contains("DevUI:OpenWebUIImageTag", source);
        Assert.Contains($"\"OpenWebUIImageTag\": \"{openWebUiRelease}\"", appHostConfig);
        Assert.Contains($"ghcr.io/open-webui/open-webui:{openWebUiRelease}", readme);
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
