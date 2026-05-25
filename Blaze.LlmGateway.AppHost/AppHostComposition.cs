using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.Aspire;

namespace Blaze.LlmGateway.AppHost;

public static class AppHostComposition
{
    public static DistributedApplication Build(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var aspireLogger = loggerFactory.CreateLogger("Blaze.LlmGateway.AppHost");
        aspireLogger.LogInformation("🔵 Aspire Orchestration starting...");
        aspireLogger.LogDebug("  ├─ Environment: {Environment}", builder.Environment.EnvironmentName);
        aspireLogger.LogDebug("  ├─ Wiring resources and dependencies");

        // ── Parameter-based secrets and non-secret provider settings ──
        var ollamaLocalBaseUrl = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:BaseUrl",
            "http://localhost:11434");
        var ollamaLocalModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:Model",
            "gemma4:e4b");
        var lmStudioEndpoint = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Endpoint",
            "http://192.168.16.56:1234/v1");
        var lmStudioModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Model",
            "local-model");
        var openCodeGoApiKey = builder.Configuration.GetValue<string>(
            "LlmGateway:Providers:OpenCodeGo:ApiKey") ?? "";
        var localInferenceModelPath = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:ModelPath") ?? "";
        var localInferenceCacheDirectory = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:CacheDirectory") ?? ".llm-cache";
        var localInferenceDownloadTimeoutSeconds = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:DownloadTimeoutSeconds",
            3600);
        var localInferenceSystemPrompt = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:SystemPrompt") ?? "";
        var localInferenceWarmupEnabled = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:WarmupEnabled",
            true);
        var localInferenceBlockStartupUntilWarm = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:BlockStartupUntilWarm",
            true);
        var localInferenceWarmupTimeoutSeconds = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:WarmupTimeoutSeconds",
            120);

        // Gateway API listen URLs — controls which interfaces/ports Kestrel binds to.
        // Leave empty to use Kestrel defaults (localhost only from launchSettings.json).
        // Set to "http://0.0.0.0:5022" to expose the gateway on all LAN interfaces.
        var gatewayListenUrls = builder.Configuration.GetValue<string?>("Gateway:ListenUrls");

        // ── API project — wire all resources ──
        aspireLogger.LogInformation("  ├─ Wiring API project with environment variables...");
        var api = builder.AddProject<Projects.Blaze_LlmGateway_Api>("api")
            .WithHttpEndpoint(port: 5022, name: "http")
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__BaseUrl", ollamaLocalBaseUrl)
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__Model", ollamaLocalModel)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Endpoint", lmStudioEndpoint)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Model", lmStudioModel)
            .WithEnvironment("LlmGateway__Providers__OpenCodeGo__ApiKey", openCodeGoApiKey)
            .WithEnvironment("LlmGateway__LocalInference__ModelPath", localInferenceModelPath)
            .WithEnvironment("LlmGateway__LocalInference__CacheDirectory", localInferenceCacheDirectory)
            .WithEnvironment("LlmGateway__LocalInference__DownloadTimeoutSeconds", localInferenceDownloadTimeoutSeconds.ToString())
            .WithEnvironment("LlmGateway__LocalInference__SystemPrompt", localInferenceSystemPrompt)
            .WithEnvironment("LlmGateway__LocalInference__WarmupEnabled", localInferenceWarmupEnabled.ToString())
            .WithEnvironment("LlmGateway__LocalInference__BlockStartupUntilWarm", localInferenceBlockStartupUntilWarm.ToString())
            .WithEnvironment("LlmGateway__LocalInference__WarmupTimeoutSeconds", localInferenceWarmupTimeoutSeconds.ToString());

        aspireLogger.LogDebug("  ├─ API environment configuration:");
        aspireLogger.LogDebug("  │  ├─ OllamaLocal: {Url} ({Model})", ollamaLocalBaseUrl, ollamaLocalModel);
        aspireLogger.LogDebug("  │  ├─ LmStudio: {Endpoint} ({Model})", lmStudioEndpoint, lmStudioModel);
        aspireLogger.LogDebug(
            "  │  ├─ LocalInference warmup: Enabled={WarmupEnabled}, BlockStartupUntilWarm={BlockStartupUntilWarm}, TimeoutSeconds={TimeoutSeconds}",
            localInferenceWarmupEnabled,
            localInferenceBlockStartupUntilWarm,
            localInferenceWarmupTimeoutSeconds);
        aspireLogger.LogDebug(
            "  │  ├─ LocalInference runtime=LMKit, cache={CacheDirectory}, download timeout={DownloadTimeoutSeconds}s",
            localInferenceCacheDirectory,
            localInferenceDownloadTimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(gatewayListenUrls))
        {
            api.WithEnvironment("ASPNETCORE_URLS", gatewayListenUrls);
            aspireLogger.LogInformation("  ├─ Gateway listen URLs overridden: {Urls}", gatewayListenUrls);
        }

        api.WithUrl("/", "Gateway Home")
           .WithUrls(ctx =>
           {
               var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
               ctx.Urls.RemoveAll(u => !seen.Add($"{u.DisplayText ?? string.Empty}|{u.Url}"));
           });


        var enableOpenWebUi = builder.Configuration.GetValue("DevUI:OpenWebUI", defaultValue: true);
        var openWebUiImageTag = builder.Configuration.GetValue("DevUI:OpenWebUIImageTag", "v0.9.5");

        if (enableOpenWebUi)
        {
            aspireLogger.LogInformation("  ├─ Open WebUI: enabled (requires Docker Desktop), image tag {ImageTag}", openWebUiImageTag);

            _ = builder.AddContainer("openwebui", "ghcr.io/open-webui/open-webui", openWebUiImageTag)
                .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
                .WithVolume("blaze-openwebui-data", "/app/backend/data")
                .WithEnvironment("WEBUI_AUTH", "False")
                .WithEnvironment("ENABLE_OLLAMA_API", "False")
                .WithEnvironment(ctx =>
                {
                    var apiEndpoint = api.GetEndpoint("http");
                    ctx.EnvironmentVariables["OPENAI_API_BASE_URL"] =
                        ReferenceExpression.Create($"{apiEndpoint}/v1");
                    ctx.EnvironmentVariables["OPENAI_API_KEY"] = "sk-blaze-openwebui";
                })
                .WaitFor(api);
        }
        else
        {
            aspireLogger.LogInformation("  ├─ Open WebUI: disabled (set DevUI:OpenWebUI=true to enable)");
        }

        var enableAgentDevUi = builder.Configuration.GetValue("DevUI:AgentFramework", defaultValue: false);
        var agentDevUiModel = builder.Configuration.GetValue("DevUI:AgentModel", defaultValue: "codebrewSharpClient");

        if (enableAgentDevUi)
        {
            aspireLogger.LogInformation("  ├─ Agent Framework DevUI: enabled (requires `pip install agent-framework-devui`)");

            var agentsDir = Path.Combine(AppContext.BaseDirectory, "devui-agents");

            _ = builder.AddExecutable(
                    name: "agent-devui",
                    command: "devui",
                    workingDirectory: AppContext.BaseDirectory,
                    args: [agentsDir, "--port", "8765"])
                .WithHttpEndpoint(port: 8765, targetPort: 8765, name: "http", isProxied: false)
                .WithEnvironment(ctx =>
                {
                    var apiEndpoint = api.GetEndpoint("http");
                    ctx.EnvironmentVariables["OPENAI_BASE_URL"] =
                        ReferenceExpression.Create($"{apiEndpoint}/v1");
                })
                .WithEnvironment("OPENAI_API_KEY", "sk-blaze-devui")
                .WithEnvironment("BLAZE_GATEWAY_MODEL", agentDevUiModel)
                .WaitFor(api);
        }
        else
        {
            aspireLogger.LogInformation("  ├─ Agent Framework DevUI: disabled (set DevUI:AgentFramework=true to enable)");
        }

        builder.AddScalarApiReference()
            .WithApiReference(api)
            .WaitFor(api);

        aspireLogger.LogDebug("  ├─ Dev UI playground(s) resolved (see flags above)");
        aspireLogger.LogDebug("  └─ Scalar API Reference configured for dashboard");
        aspireLogger.LogInformation("✅ Aspire orchestration ready - building distributed app");

        return builder.Build();
    }
}
