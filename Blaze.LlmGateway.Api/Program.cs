using System.Text.Json;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

FoundryConfigurationAliases.AddFoundryEnvironmentAliases(builder.Configuration);

builder.AddServiceDefaults();

// Detect if running under Aspire and configure logging appropriately
var isRunningUnderAspire = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_ORCHESTRATION_ENABLED")) ||
                           !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")) ||
                           builder.Configuration["ASPIRE_RUNNING"] == "true";

// Configure logging: when under Aspire, logs go to Aspire console; otherwise standard console
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (!isRunningUnderAspire)
{
    // Add simple formatter for standalone runs
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = false;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    });
}

builder.Services.Configure<LlmGatewayOptions>(
    builder.Configuration.GetSection(LlmGatewayOptions.SectionName));

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AzureFoundryModelDiscovery>();
builder.Services.AddSingleton<ModelAvailabilityRegistry>();
builder.Services.AddSingleton<IModelAvailabilityRegistry>(sp => sp.GetRequiredService<ModelAvailabilityRegistry>());
builder.Services.AddHostedService<ModelAvailabilityHeartbeatService>();
builder.Services.AddSingleton<IModelCatalog, ModelCatalogService>();
builder.Services.AddHealthChecks()
    .AddCheck<ModelProviderHealthCheck>("model_providers", failureStatus: HealthStatus.Degraded);

// MCP integration disabled (microsoft-learn server connection issues)
// To re-enable: uncomment below and ensure @microsoft/mcp-server-microsoft-learn is available
// builder.Services.AddSingleton<IEnumerable<McpConnectionConfig>>([
//     new McpConnectionConfig
//     {
//         Id = "microsoft-learn",
//         TransportType = "Stdio",
//         Command = "npx",
//         Arguments = ["-y", "@microsoft/mcp-server-microsoft-learn"]
//     }
// ]);
// builder.Services.AddHostedService<McpConnectionManager>();
// builder.Services.AddSingleton(sp =>
//     sp.GetServices<IHostedService>().OfType<McpConnectionManager>().First());

// Keyed provider clients + routing pipeline (MCP disabled for now)
builder.Services.AddLlmProviders();
builder.Services.AddLlmInfrastructure();

// Configure OpenAPI
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

// Dev-only permissive CORS so playground UIs (Open WebUI, Agent Framework DevUI)
// running as sibling Aspire resources can call /v1/chat/completions from the browser.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("devui", policy => policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    });
}

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var aspireNote = isRunningUnderAspire ? " (via Aspire)" : " (standalone)";
startupLogger.LogInformation("🟢 Blaze.LlmGateway.Api starting up{AspireNote}...", aspireNote);
startupLogger.LogDebug("  ├─ Environment: {Environment}", app.Environment.EnvironmentName);
if (isRunningUnderAspire)
{
    startupLogger.LogDebug("  ├─ Running under Aspire orchestration - logs routed to Aspire console");
}
else
{
    startupLogger.LogDebug("  ├─ Standalone mode - logging to console");
}

app.MapOpenApi();
app.MapScalarApiReference("/scalar", options =>
{
    options.WithTitle("Blaze.LlmGateway API Reference")
        .WithTheme(ScalarTheme.BluePlanet)
        .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
        .AddDocument("v1", "Blaze.LlmGateway API", "/openapi/v1.json", true);
});

// Landing page at `/` linking to Swagger, Scalar, raw OpenAPI, and key endpoints.
const string landingHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Blaze.LlmGateway</title>
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <style>
    :root { color-scheme: light dark; }
    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
           max-width: 720px; margin: 4rem auto; padding: 0 1.25rem; line-height: 1.55; }
    h1 { margin: 0 0 .25rem 0; font-size: 1.75rem; }
    p.lede { margin: 0 0 2rem 0; color: #666; }
    section { margin: 1.5rem 0; }
    section h2 { font-size: 1rem; text-transform: uppercase; letter-spacing: .05em;
                 color: #888; margin: 0 0 .5rem 0; font-weight: 600; }
    ul { list-style: none; padding: 0; margin: 0; }
    li { margin: .25rem 0; }
    a.card { display: block; padding: .75rem 1rem; border: 1px solid #ccc6; border-radius: 8px;
             text-decoration: none; color: inherit; transition: background .15s; }
    a.card:hover { background: color-mix(in srgb, currentColor 8%, transparent); }
    a.card .title { font-weight: 600; }
    a.card .path { font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
                   font-size: .85em; opacity: .7; }
    code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
           background: #8881; padding: .1em .35em; border-radius: 4px; }
    footer { margin-top: 3rem; color: #888; font-size: .85em; }
  </style>
</head>
<body>
  <h1>Blaze.LlmGateway</h1>
  <p class="lede">OpenAI-compatible routing proxy over Azure Foundry and Foundry Local with intelligent model discovery.</p>

  <section>
    <h2>API docs</h2>
    <ul>
      <li><a class="card" href="/scalar"><span class="title">Scalar API Reference</span>
          <span class="path">/scalar</span></a></li>
      <li><a class="card" href="/openapi/v1.json"><span class="title">OpenAPI document (JSON)</span>
          <span class="path">/openapi/v1.json</span></a></li>
    </ul>
  </section>

  <section>
    <h2>Core endpoints</h2>
    <ul>
      <li><a class="card" href="/v1/models"><span class="title">GET /v1/models</span>
          <span class="path">List available models</span></a></li>
      <li><a class="card" href="/v1/models/diagnostics"><span class="title">GET /v1/models/diagnostics</span>
          <span class="path">Show provider connectivity and errors</span></a></li>
      <li><a class="card" href="/scalar#tag/chat"><span class="title">POST /v1/chat/completions</span>
          <span class="path">Streaming chat (SSE). Try it in Scalar.</span></a></li>
      <li><a class="card" href="/health"><span class="title">GET /health</span>
          <span class="path">Health probe</span></a></li>
    </ul>
  </section>

  <footer>
    Tip: point an OpenAI-compatible client at <code>/v1</code> with any non-empty API key.
  </footer>
</body>
</html>
""";

app.MapGet("/", () => Results.Content(landingHtml, "text/html; charset=utf-8"))
   .ExcludeFromDescription();

if (app.Environment.IsDevelopment())
{
    app.UseCors("devui");
    startupLogger.LogInformation("  ├─ Permissive 'devui' CORS policy active (Development only)");
}

startupLogger.LogInformation("  ├─ OpenAPI JSON available at /openapi/v1.json");
startupLogger.LogInformation("  ├─ Scalar available at /scalar");

// Register LiteLLM-compatible endpoints  
app.RegisterLiteLlmEndpoints();

app.MapDefaultEndpoints();

startupLogger.LogInformation("✅ Blaze.LlmGateway.Api startup complete");

app.Run();

// For testing via WebApplicationFactory
public partial class Program { }

