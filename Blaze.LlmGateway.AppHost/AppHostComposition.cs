using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.Aspire;

namespace Blaze.LlmGateway.AppHost;

public static class AppHostComposition
{
    private const string NetworkName = "codebrewRouter";

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
        var lmStudioEndpoint = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Endpoint",
            "http://192.168.16.53:11434/v1");
        var lmStudioModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Model",
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
        var api = builder.AddProject<Projects.Blaze_LlmGateway_Api>("gateway")
            .WithHttpEndpoint(port: 5022, name: "http")
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__BaseUrl", ollamaBaseUrl)
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__Model", ollamaModel)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Endpoint", lmStudioEndpoint)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Model", lmStudioModel)
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
        aspireLogger.LogDebug("  │  ├─ LmStudio: {Endpoint} ({Model})", lmStudioEndpoint, lmStudioModel);
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

        // ═══════════════════════════════════════════════════════════════════
        // Scalar API Reference
        // ═══════════════════════════════════════════════════════════════════
        builder.AddScalarApiReference()
            .WithApiReference(api)
            .WaitFor(api);
        aspireLogger.LogDebug("  ├─ Scalar API Reference");

        // ── After all resources start, label containers so Docker Desktop groups them ──
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>(async (@event, ct) =>
        {
            try
            {
                // Resolve scripts/group-containers.ps1 relative to the solution root.
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
                var script = Path.Combine(root, "scripts", "group-containers.ps1");
                if (!File.Exists(script))
                {
                    root = Directory.GetCurrentDirectory();
                    script = Path.Combine(root, "scripts", "group-containers.ps1");
                }
                if (!File.Exists(script)) return;

                var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -File \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync(ct);
                    var error = await proc.StandardError.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    if (!string.IsNullOrWhiteSpace(output)) aspireLogger.LogDebug("  │  {Output}", output.Trim());
                    if (!string.IsNullOrWhiteSpace(error)) aspireLogger.LogDebug("  │  {Error}", error.Trim());
                }
            }
            catch (Exception ex)
            {
                aspireLogger.LogDebug(ex, "  ├─ Skipping Docker group labels ({Message})", ex.Message);
            }
        });

        aspireLogger.LogInformation("✅ CodebrewRouter orchestration ready — building distributed app");
        return builder.Build();
    }
}
