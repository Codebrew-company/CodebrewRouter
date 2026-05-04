# ADR-0011: OpenCode Go cloud provider — 14-model routing expansion

- **Status:** Proposed
- **Date:** 2026-05-03
- **Deciders:** Architecture
- **Related:** ADR-0002, [PRD](../../PRD/blaze-llmgateway-prd.md)

## Context

CodebrewRouter currently routes exclusively through local providers (OllamaRouter for classification, LmStudio for inference). The user wants to add [OpenCode Go](https://opencode.ai/zen/go) as a cloud fallback tier — a paid API key service offering 14 models across a single OpenAI-compatible endpoint (`https://opencode.ai/zen/go/v1/chat/completions`). This gives CodebrewRouter meaningful cloud fallback with model diversity (DeepSeek, Qwen, Kimi, GLM, MiniMax, MiMo families) beyond the current `OllamaRouter → LmStudio` chain.

The routing model remains **task-type-driven** with ordered provider fallback chains. OpenCode Go models act as cloud-tier fallbacks between the local first-hop and the ultimate LmStudio catch-all, with `DeepSeekV4Flash` serving as the universal last-hop cloud fallback before local.

## Decision

We will register all 14 OpenCode Go models as keyed `IChatClient` singletons with full MEAI pipeline (function invocation + context sizing), add them as `RouteDestination` enum values, and wire them into `CodebrewRouterOptions.FallbackRules` with per-task-type ordered chains. A single shared `OpenAIClient` (registered as keyed singleton) backs all 14 model registrations, avoiding duplicate HTTP connection pools.

### Details

#### 1. Static model catalog (`Core/RouteDestination.cs`)

14 enum values:

```
OpenCodeGo_DeepSeekV4Pro,
OpenCodeGo_DeepSeekV4Flash,
OpenCodeGo_Qwen3_5Plus,
OpenCodeGo_Qwen3_6Plus,
OpenCodeGo_KimiK2_5,
OpenCodeGo_KimiK2_6,
OpenCodeGo_GLM5,
OpenCodeGo_GLM5_1,
OpenCodeGo_MiniMaxM2_5,
OpenCodeGo_MiniMaxM2_7,
OpenCodeGo_MiMoV2Pro,
OpenCodeGo_MiMoV2_5,
OpenCodeGo_MiMoV2_5Pro,
OpenCodeGo_MiMoV2Omni
```

Plus a `OpenCodeGoModels` static lookup mapping enum → OpenAI model string (e.g., `OpenCodeGo_DeepSeekV4Pro` → `"deepseek-v4-pro"`):

```csharp
public static class OpenCodeGoModels
{
    public static readonly IReadOnlyDictionary<RouteDestination, string> ModelNames = new Dictionary<RouteDestination, string>
    {
        [RouteDestination.OpenCodeGo_DeepSeekV4Pro]  = "deepseek-v4-pro",
        [RouteDestination.OpenCodeGo_DeepSeekV4Flash] = "deepseek-v4-flash",
        [RouteDestination.OpenCodeGo_Qwen3_5Plus]     = "qwen3.5-plus",
        [RouteDestination.OpenCodeGo_Qwen3_6Plus]     = "qwen3.6-plus",
        [RouteDestination.OpenCodeGo_KimiK2_5]        = "kimi-k2.5",
        [RouteDestination.OpenCodeGo_KimiK2_6]        = "kimi-k2.6",
        [RouteDestination.OpenCodeGo_GLM5]            = "glm-5",
        [RouteDestination.OpenCodeGo_GLM5_1]          = "glm-5.1",
        [RouteDestination.OpenCodeGo_MiniMaxM2_5]     = "mini-max-m2.5",
        [RouteDestination.OpenCodeGo_MiniMaxM2_7]     = "mini-max-m2.7",
        [RouteDestination.OpenCodeGo_MiMoV2Pro]       = "mimo-v2-pro",
        [RouteDestination.OpenCodeGo_MiMoV2_5]        = "mimo-v2.5",
        [RouteDestination.OpenCodeGo_MiMoV2_5Pro]     = "mimo-v2.5-pro",
        [RouteDestination.OpenCodeGo_MiMoV2Omni]      = "mimo-v2-omni",
    };
}
```

#### 2. Options class (`Core/Configuration/LlmGatewayOptions.cs`)

No `Model` property — the model name is baked into each keyed registration (see §3). All 14 registrations share one endpoint and API key.

```csharp
public class OpenCodeGoOptions
{
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/go/v1";
    public string ApiKey { get; set; } = "";
    public int MaxContextTokens { get; set; } = 128000;
    public int ReservedOutputTokens { get; set; } = 16384;
}
```

Add to `ProvidersOptions`:

```csharp
public OpenCodeGoOptions OpenCodeGo { get; set; } = new();
```

Config section: `LlmGateway:Providers:OpenCodeGo`

#### 3. Registration — shared client, 14 wrappers (`InfrastructureServiceExtensions.cs`)

A single keyed `OpenAIClient` singleton is shared across all model registrations, avoiding 14 duplicate HTTP connection pools:

```csharp
// Shared OpenAIClient — one HTTP pool for all 14 models:
services.AddKeyedSingleton<OpenAIClient>("OpenCodeGo_Client", (sp, _) =>
{
    var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OpenCodeGo;
    var apiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? "notneeded" : opts.ApiKey;
    return new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(opts.BaseUrl) });
});

// Per-model IChatClient — resolves shared client, wraps with middleware:
foreach (var (dest, modelName) in OpenCodeGoModels.ModelNames)
{
    var key = dest.ToString();
    services.AddKeyedSingleton<IChatClient>(key, (sp, _) =>
    {
        var client = sp.GetRequiredKeyedService<OpenAIClient>("OpenCodeGo_Client");
        var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OpenCodeGo;
        var tokenCounter = sp.GetRequiredService<ITokenCounter>();
        var compactor = sp.GetRequiredService<IContextCompactor>();
        var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
        var sizingLogger = sp.GetRequiredService<ILogger<ContextSizingChatClient>>();

        return client.GetChatClient(modelName).AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .UseContextSizing(tokenCounter, compactor, sizingOptions,
                opts.MaxContextTokens, opts.ReservedOutputTokens, modelName, sizingLogger)
            .Build();
    });
}
```

If `ApiKey` is empty, the shared client still registers (with dummy key) but startup validation (§5) marks all 14 model keys as unavailable. This avoids null-ref on DI resolution while preventing routing to unconfigured providers.

#### 4. Provider detection — prefix-match pattern (`CodebrewRouterChatClient.cs`)

All 14 models share the same `OpenCodeGoOptions` config. Use `StartsWith("OpenCodeGo_")` in both methods to avoid 14 duplicate cases:

**`IsProviderConfigured()`:**

```csharp
return providerKey switch
{
    "LmStudio" => HasValue(providers.LmStudio.Endpoint) && HasValue(providers.LmStudio.Model)
                  && availabilityRegistry.IsProviderAvailable("LmStudio"),

    // Single catch-all for all 14 OpenCodeGo models:
    var k when k.StartsWith("OpenCodeGo_", StringComparison.OrdinalIgnoreCase)
        => HasValue(providers.OpenCodeGo.ApiKey)
           && availabilityRegistry.IsProviderAvailable(k),

    _ => true
};
```

**`TryGetProviderContextBudget()`:**

```csharp
switch (providerKey)
{
    case "LmStudio":
        budget = new ProviderContextBudget(
            providers.LmStudio.Model,
            providers.LmStudio.MaxContextTokens,
            providers.LmStudio.ReservedOutputTokens);
        return HasValue(providers.LmStudio.Model) && providers.LmStudio.MaxContextTokens > 0;

    // All OpenCodeGo models share the same context budget:
    case var k when k.StartsWith("OpenCodeGo_", StringComparison.OrdinalIgnoreCase):
        budget = new ProviderContextBudget(
            k, // model name IS the provider key
            providers.OpenCodeGo.MaxContextTokens,
            providers.OpenCodeGo.ReservedOutputTokens);
        return HasValue(providers.OpenCodeGo.ApiKey) && providers.OpenCodeGo.MaxContextTokens > 0;

    default:
        budget = default;
        return false;
}
```

**`CanFit()` in `LlmRoutingChatClient.cs`** — same prefix-match pattern for context window checks.

#### 5. Startup validation (`ModelAvailabilityHeartbeatService.cs`)

Three touchpoints:

**a) `SeedConfiguredModels()`** — seed each of the 14 model keys individually (the availability registry tracks per-model availability, not per-endpoint):

```csharp
if (HasValue(providers.OpenCodeGo.ApiKey))
{
    foreach (var dest in OpenCodeGoModels.ModelNames.Keys)
    {
        _registry.SeedProvider(dest.ToString(), enabled: false);
    }
}
```

**b) Probe method** — add `ProbeOpenCodeGoAsync()` that calls `GET /v1/models` on the OpenCodeGo endpoint. If the request succeeds, mark all 14 model keys as available:

```csharp
private async Task ProbeOpenCodeGoAsync(LlmGatewayOptions options, CancellationToken ct)
{
    var opts = options.Providers.OpenCodeGo;
    if (!HasValue(opts.ApiKey)) return;

    try
    {
        var client = _serviceProvider.GetKeyedService<OpenAIClient>("OpenCodeGo_Client");
        // OpenAIClient doesn't expose raw HTTP; use a direct HttpClient call to /v1/models
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        var response = await http.GetAsync($"{opts.BaseUrl.TrimEnd('/')}/models", ct);
        if (response.IsSuccessStatusCode)
        {
            foreach (var dest in OpenCodeGoModels.ModelNames.Keys)
                _registry.SetProviderEnabled(dest.ToString(), enabled: true);
        }
    }
    catch { /* remain disabled */ }
}
```

**c) `IsOptionalLocalProvider()`** — do NOT add OpenCodeGo here. It is a cloud provider, not optional. Configured but unreachable is a warning; unconfigured (no API key) is silently skipped.

#### 6. Fallback rules (`CodebrewRouterOptions` defaults + `appsettings.json`)

| TaskType | Ordered provider chain |
|---|---|
| Coding | OpenCodeGo_DeepSeekV4Pro → OpenCodeGo_Qwen3_6Plus → OpenCodeGo_MiniMaxM2_7 → OpenCodeGo_MiMoV2_5Pro → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| Reasoning | OpenCodeGo_KimiK2_6 → OpenCodeGo_DeepSeekV4Pro → OpenCodeGo_KimiK2_5 → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| Research | OpenCodeGo_KimiK2_6 → OpenCodeGo_GLM5_1 → OpenCodeGo_MiMoV2_5 → OpenCodeGo_MiniMaxM2_7 → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| DataAnalysis | OpenCodeGo_DeepSeekV4Pro → OpenCodeGo_Qwen3_6Plus → OpenCodeGo_MiniMaxM2_7 → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| Creative | OpenCodeGo_GLM5_1 → OpenCodeGo_MiMoV2_5 → OpenCodeGo_GLM5 → OpenCodeGo_MiniMaxM2_5 → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| General | OpenCodeGo_Qwen3_5Plus → OpenCodeGo_MiniMaxM2_5 → OpenCodeGo_Qwen3_6Plus → OpenCodeGo_MiMoV2_5Pro → OpenCodeGo_DeepSeekV4Flash → LmStudio |
| VisionObjectDetection | OpenCodeGo_MiMoV2Omni → OpenCodeGo_GLM5_1 → OpenCodeGo_MiMoV2_5 → OpenCodeGo_DeepSeekV4Flash → LmStudio |

Design principles:
- **DeepSeekV4Flash** = universal last-hop cloud fallback (fast, cheap, in every chain).
- **LmStudio** = final local fallback (always present, no API key dependency).
- First-hop model = best-in-class for each task type.
- Chains are depth-ordered (highest quality → fastest/catch-all).

#### 7. AppHost wiring (`AppHostComposition.cs`)

```csharp
// Config read:
var openCodeGoApiKey = builder.Configuration.GetValue<string>(
    "LlmGateway:Providers:OpenCodeGo:ApiKey") ?? "";

// Env var:
api.WithEnvironment("LlmGateway__Providers__OpenCodeGo__ApiKey", openCodeGoApiKey);
```

Follows the existing `LlmGateway__Providers__{Name}__{Property}` convention used for LmStudio and OllamaLocal. The base URL does not need wiring (defaults to `https://opencode.ai/zen/go/v1` in options).

#### 8. Files to modify (12 total)

1. `Blaze.LlmGateway.Core/RouteDestination.cs` — +14 enum values + `OpenCodeGoModels` static class
2. `Blaze.LlmGateway.Core/Configuration/LlmGatewayOptions.cs` — +`OpenCodeGoOptions` class + `ProvidersOptions.OpenCodeGo` property
3. `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs` — +1 shared `OpenAIClient` + 14 keyed `IChatClient` registrations
4. `Blaze.LlmGateway.Core/Configuration/CodebrewRouterOptions.cs` — new FallbackRules defaults
5. `Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs` — prefix-match `IsProviderConfigured()` + `TryGetProviderContextBudget()`
6. `Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs` — prefix-match `CanFit()` context window checks
7. `Blaze.LlmGateway.Api/ModelAvailabilityHeartbeatService.cs` — `SeedConfiguredModels()` + `ProbeOpenCodeGoAsync()` + probe call in timer loop
8. `Blaze.LlmGateway.Api/ModelProviderHealthCheck.cs` — include OpenCodeGo model count in health status
9. `Blaze.LlmGateway.Api/appsettings.json` — `OpenCodeGo` config section + updated `FallbackRules`
10. `Blaze.LlmGateway.AppHost/AppHostComposition.cs` — config read + `WithEnvironment` for API key
11. `Blaze.LlmGateway.Infrastructure/RoutingStrategies/KeywordRoutingStrategy.cs` — optional keyword matches
12. `Blaze.LlmGateway.Infrastructure/ClientFactory/OpenAICompatibleClientFactory.cs` — optional: extract shared factory to avoid duplicate code across LmStudio + OpenCodeGo registrations

#### 9. Token Counting Strategy (Phase 2)

Token counting for the 14 OpenCodeGo models uses a **graceful fallback** approach with no exceptions:

**Tokenizer Availability by Model Family:**

| Family | Tokenizer | Implementation | Accuracy | Notes |
|---|---|---|---|---|
| Qwen (3.5+, 3.6+) | Native C# | `Yuniko.Software.Qwen3Tokenizer` | ~99% | Marked for Phase 3b pending package API verification |
| DeepSeek (v4-pro, v4-flash) | HuggingFace JSON | `Microsoft.ML.Tokenizers` + cached tokenizer.json | ~90-95% | Requires manual HF tokenizer download and cache (Phase 3b) |
| GLM (5, 5.1) | HuggingFace JSON | `Microsoft.ML.Tokenizers` + cached tokenizer.json | ~90-95% | Requires manual HF tokenizer download and cache (Phase 3b) |
| Kimi, MiniMax, MiMo | None (no public tokenizers) | gpt-4o (cl100k_base) fallback | ~75-85% | Graceful fallback; logged with accuracy warnings |

**Implementation:**

- `ITokenizerRegistry` interface: Contract for model-aware tokenizer resolution, returns null for unavailable models (never throws).
- `OpenCodeGoTokenizerRegistry` implementation: Maps all 14 models explicitly to tokenizers (native or null). Logs warnings with accuracy metadata when falling back.
- `TiktokenTokenCounter` refactored: Injects registry, queries before lookup, falls back to gpt-4o with accuracy warning if unavailable.
- **Design principle:** No exceptions reach application layer; graceful degradation via logging + fallback to gpt-4o.

**Future Enhancement (Phase 3b):**

- Implement `LoadQwenTokenizer()` with proper `Yuniko.Software.Qwen3Tokenizer` API integration.
- Implement `LoadHuggingFaceTokenizer()` to load DeepSeek/GLM tokenizer.json files from cache or remote HuggingFace CDN.
- Track in GitHub issue for future tokenizer support improvements.

## Consequences

**Positive**

- 14-model cloud tier diversifies failover beyond local Ollama/LmStudio.
- Per-task-type routing gives each workload its best-fit model.
- Universal DeepSeekV4Flash hop ensures cloud fallback exists for every task before dropping to local.
- Same `OpenAIClient` pattern as existing LmStudio registration — no new SDK dependencies.
- Single shared `OpenAIClient` means one HTTP connection pool, not 14.
- Prefix-match in `IsProviderConfigured` keeps switch statements compact (2 cases, not 16).

**Negative**

- 14 registrations add ~100 lines to `InfrastructureServiceExtensions.cs` (mitigated by loop over `OpenCodeGoModels.ModelNames`).
- API key in `appsettings.json` — must use user secrets or Aspire config in production.
- Enum explosion: `RouteDestination` grows from 2 to 16 values. A config-driven `ProviderDescriptor` model (proposed in ADR-0002) would be cleaner long-term.
- All models share one API key/account — rate limits may constrain concurrency.
- Empty API key registers a dummy client rather than throwing — silent degradation if the user forgets to set it (counter: startup probe logs a warning).

**Neutral**

- Each model gets full `UseFunctionInvocation()` + `UseContextSizing()` pipeline, consistent with all other providers.
- No new `FallbackChains` entries needed for `LlmRoutingChatClient` — routing remains CodebrewRouter-mediated.
- OpenCodeGo is treated as a cloud provider (not `IsOptionalLocalProvider`), so availability checks are stricter.

## Alternatives Considered

### Alternative A — Single "OpenCodeGo" enum + per-model client selection

Register one `"OpenCodeGo"` keyed client and switch models via `ChatOptions.ModelId` at call time. Rejected because the current architecture codes provider identity into the DI key; per-model keys are required for fallback chain resolution and health-aware routing.

### Alternative B — Add only a subset of models (e.g., 4-5)

Reduces registration volume but limits routing diversity. Rejected because all 14 models are listed at the same API endpoint with the same pricing model — no marginal cost to registering all vs. a subset.

### Alternative C — Config-driven model catalog instead of enum

Uses `ProviderDescriptor` + `ModelProfile` records (per ADR-0002) with dynamic registration. Rejected for this ADR because `RouteDestination` enum is still the active routing contract; migrating to config-driven is a separate architectural initiative.

### Alternative D — 14 separate `OpenAIClient` instances

Each model registration creates its own `OpenAIClient`. Rejected — wastes HTTP connection pools, adds ~200 lines of duplicate factory code vs. the single shared-client + loop approach in §3.

## References

- [OpenCode Go API docs](https://opencode.ai/zen/go)
- [ADR-0002: Provider identity model](../adr/0002-provider-identity-model.md)
- [appsettings.json](../../src/Blaze.LlmGateway.Api/appsettings.json)
- [CodebrewRouterChatClient.cs](../../src/Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs)
- [ModelAvailabilityHeartbeatService.cs](../../src/Blaze.LlmGateway.Api/ModelAvailabilityHeartbeatService.cs)
- [InfrastructureServiceExtensions.cs](../../src/Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs)
