using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.AppHost;

public static class AppHostComposition
{
    private const string NetworkName = "codebrewRouter";
    private const string DockerDesktopProjectName = "CodebrewRouter";

    // Gateway host-published HTTP port. Used for the project endpoint and the
    // host.docker.internal URL the Open WebUI container uses to reach it.
    private const int GatewayPort = 5022;

    public static DistributedApplication Build(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
        var aspireLogger = loggerFactory.CreateLogger("Blaze.LlmGateway.AppHost");
        aspireLogger.LogInformation("🔵 CodebrewRouter Aspire Orchestration starting...");
        aspireLogger.LogDebug("  ├─ Environment: {Environment}", builder.Environment.EnvironmentName);
        aspireLogger.LogDebug("  ├─ Docker network: {Network}", NetworkName);
        aspireLogger.LogDebug("  ├─ Wiring resources and dependencies");

        // ── Provider configuration (defaults overridden by appsettings / user secrets) ──
        var ollamaBaseUrl = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:BaseUrl",
            "http://192.168.16.53:11434");
        var ollamaModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:Model",
            "gemma4:e4b");
        var openCodeGoApiKey = builder.Configuration.GetValue<string>(
            "LlmGateway:Providers:OpenCodeGo:ApiKey") ?? "";
        var localInferenceModelPath = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:ModelPath") ?? "";
        var localInferenceCacheDirectory = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:CacheDirectory") ?? ".llm-cache";
        var localInferenceDownloadTimeoutSeconds = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:DownloadTimeoutSeconds", 3600);
        var localInferenceSystemPrompt = builder.Configuration.GetValue<string>(
            "LlmGateway:LocalInference:SystemPrompt") ?? "";
        var localInferenceWarmupEnabled = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:WarmupEnabled", true);
        var localInferenceBlockStartupUntilWarm = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:BlockStartupUntilWarm", true);
        var localInferenceWarmupTimeoutSeconds = builder.Configuration.GetValue(
            "LlmGateway:LocalInference:WarmupTimeoutSeconds", 120);
        var gatewayListenUrls = builder.Configuration.GetValue<string?>("Gateway:ListenUrls");

        // ═══════════════════════════════════════════════════════════════════
        // API Gateway — the core CodebrewRouter service
        // ═══════════════════════════════════════════════════════════════════
        aspireLogger.LogInformation("  ├─ Wiring API gateway...");
        // Gateway runs as a host process (not a container): LM-Kit's native embedded-Gemma
        // (LocalGemma) loads fine on the host but hangs on model load inside a Linux container.
        // Open WebUI (containerized) reaches this host process via host.docker.internal, which
        // ONLY works if the gateway binds 0.0.0.0 — the default launch profile binds
        // localhost-only (https://localhost:7200;http://localhost:5022), invisible to containers.
        // So: skip the launch profile, own port 5022 unproxied, and bind all interfaces.
        var api = builder.AddProject<Projects.Blaze_LlmGateway_Api>("gateway", launchProfileName: null)
            .WithHttpEndpoint(port: GatewayPort, name: "http", isProxied: false)
            .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:" + GatewayPort)
            // launchProfileName: null drops launchSettings env vars — set them explicitly.
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__BaseUrl", ollamaBaseUrl)
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__Model", ollamaModel)
            .WithEnvironment("LlmGateway__Providers__OpenCodeGo__ApiKey", openCodeGoApiKey)
            .WithEnvironment("LlmGateway__LocalInference__ModelPath", localInferenceModelPath)
            .WithEnvironment("LlmGateway__LocalInference__CacheDirectory", localInferenceCacheDirectory)
            .WithEnvironment("LlmGateway__LocalInference__DownloadTimeoutSeconds", localInferenceDownloadTimeoutSeconds.ToString())
            .WithEnvironment("LlmGateway__LocalInference__SystemPrompt", localInferenceSystemPrompt)
            .WithEnvironment("LlmGateway__LocalInference__WarmupEnabled", localInferenceWarmupEnabled.ToString())
            .WithEnvironment("LlmGateway__LocalInference__BlockStartupUntilWarm", localInferenceBlockStartupUntilWarm.ToString())
            .WithEnvironment("LlmGateway__LocalInference__WarmupTimeoutSeconds", localInferenceWarmupTimeoutSeconds.ToString())
            .WithEnvironment("LlmGateway__Auth__SeedDevKeys", "true");

        if (!string.IsNullOrWhiteSpace(gatewayListenUrls))
        {
            api.WithEnvironment("ASPNETCORE_URLS", gatewayListenUrls);
            aspireLogger.LogInformation("  │  └─ Gateway listen URLs overridden: {Urls}", gatewayListenUrls);
        }

        aspireLogger.LogDebug("  │  ├─ Ollama: {Url} ({Model})", ollamaBaseUrl, ollamaModel);
        aspireLogger.LogDebug("  │  ├─ LocalInference: runtime=LMKit, cache={CacheDirectory}", localInferenceCacheDirectory);

        // Clean up duplicate URLs on the dashboard tile.
        api.WithUrl("/", "Gateway Home")
           .WithUrls(ctx =>
           {
               var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
               ctx.Urls.RemoveAll(u => !seen.Add($"{u.DisplayText ?? string.Empty}|{u.Url}"));
           });

        // ═══════════════════════════════════════════════════════════════════
        // Open WebUI — browser-based chat playground
        // ═══════════════════════════════════════════════════════════════════
        var enableOpenWebUi = builder.Configuration.GetValue("DevUI:OpenWebUI", defaultValue: true);
        var openWebUiImageTag = builder.Configuration.GetValue("DevUI:OpenWebUIImageTag", "v0.10.2");

        if (enableOpenWebUi)
        {
            aspireLogger.LogInformation("  ├─ Open WebUI: {ImageTag}", openWebUiImageTag);

            _ = builder.AddContainer("openwebui", "ghcr.io/open-webui/open-webui", openWebUiImageTag)
                .WithContainerRuntimeArgs(
                    "--label", $"com.docker.compose.project={DockerDesktopProjectName}",
                    "--label", "com.docker.compose.service=openwebui",
                    "--label", "com.docker.compose.container-number=1")
                .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
                .WithVolume("blaze-openwebui-data", "/app/backend/data")
                .WithEnvironment("WEBUI_AUTH", "False")
                .WithEnvironment("ENABLE_OLLAMA_API", "False")
                // Force env config to win on every boot. Default (true) caches the OpenAI
                // connection in the volume on first run, so later env changes are ignored and
                // the container keeps a stale gateway URL — the "no models" symptom.
                .WithEnvironment("ENABLE_PERSISTENT_CONFIG", "False")
                .WithEnvironment(ctx =>
                {
                    // openwebui is a container; the gateway is a host process. Reach the host's
                    // published port via host.docker.internal (Docker Desktop maps it to the host
                    // loopback on Win/Mac). Fixed GatewayPort so the URL is stable across runs.
                    ctx.EnvironmentVariables["OPENAI_API_BASE_URL"] =
                        $"http://host.docker.internal:{GatewayPort}/v1";
                    ctx.EnvironmentVariables["OPENAI_API_KEY"] = "sk-blaze-openwebui";
                });
            // No .WaitFor(api): api is a host project, and a container depending on a host
            // resource makes Aspire build a container tunnel to it — which fails here
            // ("gateway-http should have valid address"). openwebui reaches the gateway via
            // host.docker.internal (no Aspire bridge), and refetches models when the UI loads,
            // so start ordering doesn't matter.
        }
        else
        {
            aspireLogger.LogInformation("  ├─ Open WebUI: disabled (DevUI:OpenWebUI=true to enable)");
        }

        // ═══════════════════════════════════════════════════════════════════
        // Agent Framework DevUI — agent debugging playground
        // ═══════════════════════════════════════════════════════════════════
        var enableAgentDevUi = builder.Configuration.GetValue("DevUI:AgentFramework", defaultValue: false);
        var agentDevUiModel = builder.Configuration.GetValue("DevUI:AgentModel", defaultValue: "codebrewSharpClient");

        if (enableAgentDevUi)
        {
            aspireLogger.LogInformation("  ├─ Agent DevUI: {Model}", agentDevUiModel);

            _ = builder.AddExecutable(
                    name: "agent-devui",
                    command: "devui",
                    workingDirectory: AppContext.BaseDirectory,
                    args: [Path.Combine(AppContext.BaseDirectory, "devui-agents"), "--port", "8765"])
                .WithHttpEndpoint(port: 8765, targetPort: 8765, name: "http", isProxied: false)
                .WithEnvironment(ctx =>
                {
                    // agent-devui runs as a host executable (not a container), so localhost
                    // reaches the gateway directly.
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
            aspireLogger.LogInformation("  ├─ Agent DevUI: disabled (DevUI:AgentFramework=true to enable)");
        }

        // Swagger + Scalar are served by the gateway itself (host process). The Scalar.Aspire
        // aggregator is a container, and a container referencing the host gateway trips the same
        // container-tunnel failure as openwebui — so link the gateway's built-in UIs on its
        // dashboard tile instead.
        api.WithUrl("/swagger", "Swagger UI")
           .WithUrl("/scalar/v1", "Scalar API Reference");
        aspireLogger.LogDebug("  ├─ Swagger + Scalar served by gateway (/swagger, /scalar/v1)");

        aspireLogger.LogInformation("✅ CodebrewRouter orchestration ready — building distributed app");
        return builder.Build();
    }
}
