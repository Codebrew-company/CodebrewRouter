using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
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
        var azureFoundryEndpoint = builder.AddParameter("azure-foundry-endpoint");
        var azureFoundryApiKey = builder.AddParameter("azure-foundry-api-key", secret: true);
        var githubModelsApiKey = builder.AddParameter("github-models-api-key", secret: true);
        var azureFoundryEndpointAlias = builder.Configuration["COPILOT_FOUNDRY_AZURE_BASE_URL"];
        var azureFoundryResponsesEndpoint = builder.Configuration.GetValue(
            "LlmGateway:Providers:AzureFoundry:ResponsesEndpoint",
            "https://codebrew-resource.services.ai.azure.com/api/projects/codebrew/openai/v1/responses");
        var azureFoundryResponsesEndpointAlias = builder.Configuration["COPILOT_FOUNDRY_RESPONSES_ENDPOINT"];
        var azureFoundryApiKeyAlias = builder.Configuration["COPILOT_AZURE_API_KEY"];
        var azureFoundryModelAlias = builder.Configuration["COPILOT_FOUNDRY_DEFAULT_MODEL"]
            ?? builder.Configuration["COPILOT_FOUNDRY_GENERAL_MODEL"];
        var foundryLocalEnabled = builder.Configuration.GetValue(
            "LlmGateway:Providers:FoundryLocal:Enabled",
            false);
        var foundryLocalModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:FoundryLocal:Model",
            "phi-4-mini");
        var foundryLocalModelVersion = builder.Configuration.GetValue(
            "LlmGateway:Providers:FoundryLocal:ModelVersion",
            "1");
        var foundryLocalModelFormat = builder.Configuration.GetValue(
            "LlmGateway:Providers:FoundryLocal:ModelFormat",
            "Microsoft");
        var ollamaLocalBaseUrl = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:BaseUrl",
            "http://192.168.16.12:11434");
        var ollamaLocalModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:OllamaLocal:Model",
            "gemma4:e4b");
        var lmStudioEndpoint = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Endpoint",
            "http://192.168.16.56:1234/v1");
        var lmStudioModel = builder.Configuration.GetValue(
            "LlmGateway:Providers:LmStudio:Model",
            "local-model");

        // Gateway API listen URLs — controls which interfaces/ports Kestrel binds to.
        // Leave empty to use Kestrel defaults (localhost only from launchSettings.json).
        // Set to "http://0.0.0.0:5022" to expose the gateway on all LAN interfaces.
        var gatewayListenUrls = builder.Configuration.GetValue<string?>("Gateway:ListenUrls");

        IResourceBuilder<FoundryDeploymentResource>? foundryLocalConnectionString = null;
        if (foundryLocalEnabled)
        {
            // ── Foundry Local — Aspire-managed dev-only resource ──
            // Disabled by default because some machines auto-select a cached CUDA variant
            // that fails to load with CUBLAS_STATUS_ALLOC_FAILED. Re-enable via
            // LlmGateway:Providers:FoundryLocal:Enabled=true once the local runtime is stable.
            var foundry = builder.AddFoundry("foundryLocal")
                .RunAsFoundryLocal();

            var foundryLocalChat = foundry.AddDeployment(
                "foundryLocalChat",
                foundryLocalModel,
                foundryLocalModelVersion,
                foundryLocalModelFormat);

            // Aspire.Hosting.Foundry 13.3.0-preview.1 has a race in RunAsFoundryLocal()'s
            // WithInitializer: it samples FoundryLocalManager.IsServiceRunning immediately after
            // StartServiceAsync(), which can return false while the service is still warming up.
            // The parent foundryLocal resource then sticks in FailedToStart even though the
            // service comes up moments later and the deployment downloads + loads the model
            // successfully. Promote the parent to Running once the deployment reports Running.
            builder.Eventing.Subscribe<ResourceReadyEvent>(
                foundryLocalChat.Resource,
                async (evt, ct) =>
                {
                    var rns = evt.Services.GetRequiredService<ResourceNotificationService>();
                    await rns.PublishUpdateAsync(
                        foundry.Resource,
                        state => state.State?.Text == KnownResourceStates.FailedToStart
                            ? state with { State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success) }
                            : state).ConfigureAwait(false);
                });

            foundryLocalConnectionString = foundryLocalChat;
        }
        else
        {
            aspireLogger.LogInformation("  ├─ Foundry Local: disabled (set LlmGateway:Providers:FoundryLocal:Enabled=true to enable)");
        }

        // ── GitHub Models ──
        var ghGpt4oMini = builder.AddGitHubModel("gh-gpt4o-mini", "openai/gpt-4o-mini")
            .WithApiKey(githubModelsApiKey);
        var ghPhi4Mini = builder.AddGitHubModel("gh-phi4-mini", "microsoft/phi-4-mini-instruct")
            .WithApiKey(githubModelsApiKey);

        // ── API project — wire all resources ──
        var api = builder.AddProject<Projects.Blaze_LlmGateway_Api>("api")
            .WithReference(ghGpt4oMini)
            //.WithReference(ghPhi4Mini)
            .WithEnvironment("LlmGateway__Providers__FoundryLocal__Enabled", foundryLocalEnabled.ToString())
            .WithEnvironment("LlmGateway__Providers__GithubModels__ApiKey", githubModelsApiKey)
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__BaseUrl", ollamaLocalBaseUrl)
            .WithEnvironment("LlmGateway__Providers__OllamaLocal__Model", ollamaLocalModel)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Endpoint", lmStudioEndpoint)
            .WithEnvironment("LlmGateway__Providers__LmStudio__Model", lmStudioModel);

        if (foundryLocalConnectionString is not null)
        {
            api.WaitFor(foundryLocalConnectionString)
               .WithReference(foundryLocalConnectionString);
        }

        if (string.IsNullOrWhiteSpace(azureFoundryEndpointAlias))
        {
            api.WithEnvironment("LlmGateway__Providers__AzureFoundry__Endpoint", azureFoundryEndpoint);
        }
        else
        {
            api.WithEnvironment("LlmGateway__Providers__AzureFoundry__Endpoint", azureFoundryEndpointAlias);
        }

        api.WithEnvironment(
            "LlmGateway__Providers__AzureFoundry__ResponsesEndpoint",
            string.IsNullOrWhiteSpace(azureFoundryResponsesEndpointAlias)
                ? azureFoundryResponsesEndpoint
                : azureFoundryResponsesEndpointAlias);

        if (string.IsNullOrWhiteSpace(azureFoundryApiKeyAlias))
        {
            api.WithEnvironment("LlmGateway__Providers__AzureFoundry__ApiKey", azureFoundryApiKey);
        }
        else
        {
            api.WithEnvironment("LlmGateway__Providers__AzureFoundry__ApiKey", azureFoundryApiKeyAlias);
        }

        if (!string.IsNullOrWhiteSpace(azureFoundryModelAlias))
        {
            api.WithEnvironment("LlmGateway__Providers__AzureFoundry__Model", azureFoundryModelAlias);
        }

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

        if (enableOpenWebUi)
        {
            aspireLogger.LogInformation("  ├─ Open WebUI: enabled (requires Docker Desktop)");

            _ = builder.AddContainer("openwebui", "ghcr.io/open-webui/open-webui", "v0.9.2")
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
                .WithEnvironment("BLAZE_GATEWAY_MODEL", "codebrew-router")
                .WaitFor(api);
        }
        else
        {
            aspireLogger.LogInformation("  ├─ Agent Framework DevUI: disabled (set DevUI:AgentFramework=true to enable)");
        }

        builder.AddScalarApiReference()
            .WithApiReference(api, "/openapi/v1.json");

        aspireLogger.LogDebug("  ├─ API project configured with GitHub Models references");
        aspireLogger.LogDebug("  ├─ Dev UI playground(s) resolved (see flags above)");
        aspireLogger.LogDebug("  └─ Scalar API Reference configured for dashboard");
        aspireLogger.LogInformation("✅ Aspire orchestration ready - building distributed app");

        return builder.Build();
    }
}
