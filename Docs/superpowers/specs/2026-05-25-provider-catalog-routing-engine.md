# Provider Catalog & Routing Strategy Engine

**Status:** Draft  
**Date:** 2026-05-25  
**Feature:** `feature/provider-catalog-routing`  
**Supersedes:** N/A — new capability  

---

## 1. Motivation

CodebrewRouter currently has a flat `Providers` config section with one-off options per provider (OllamaRouter, LmStudio, OpenCodeGo) and a basic `ModelSelectionResolver` that does sequential keyed-DI resolution. This doesn't match LiteLLM's routing proxy model where:

- A config-driven **provider catalog** registers N deployments per model_name
- **Routing strategies** (shuffle, least-busy, latency, cost) select the best deployment at request time
- **Health signals** feed into routing decisions, not just observability
- **Specialized route models** (`coder`, `yardly`, `codebrewSharpClient`) resolve to different deployment chains and strategies

### Goal

Build a provider catalog + routing strategy engine that makes CodebrewRouter a true LiteLLM-class routing proxy with specialized route models, all while keeping the existing OpenAI-compatible surface untouched.

---

## 2. Architecture

```
Request → ResolveVirtualModel(modelId)
           ↓
         VirtualModelConfig
           ├── routing_strategy: "shuffle" | "least_busy" | "latency" | "cost" | "round_robin"
           ├── deployments: [ProviderDeployment, ...]  ← ordered/weighted list
           └── fallback_strategy: "failover" | "circuit_breaker"
           ↓
         RoutingStrategy.SelectDeployment(deployments, context)
           ├── filter unhealthy
           ├── apply strategy (shuffle, least-busy, etc.)
           └── return winning deployment
           ↓
         ProviderResolver.ResolveChatClient(deployment)
           ├── resolve keyed IChatClient
           ├── wrap with context sizing
           └── return ready client
```

### Layers

| Layer | Responsibility | New/Existing |
|-------|---------------|-------------|
| **Config** | YAML/JSON `model_list` with deployments per model_name | **New** |
| **VirtualModelResolver** | Maps modelId → VirtualModelConfig (existing, enhanced) | Enhanced |
| **ProviderCatalog** | Registers all deployments on startup, health-aware | **New** |
| **RoutingStrategy** | Selects a deployment per request based on strategy | **New** |
| **ProviderResolver** | Resolves IChatClient for a deployment | Enhanced |
| **HealthMonitor** | Background health probing, feeds into routing | Existing `ProviderHealthMonitor` |

---

## 3. Config Shape

### LiteLLM-inspired `model_list`

```json
{
  "LlmGateway": {
    "ProviderCatalog": {
      "RoutingStrategy": "latency_first",
      "DefaultFallbackStrategy": "failover",
      "HealthCheckIntervalSeconds": 30,
      "Deployments": [
        {
          "Name": "azure-gpt4-mini",
          "ModelName": "gpt-4o-mini",
          "Provider": "AzureFoundry",
          "Endpoint": "https://eastus.api.cognitive.microsoft.com",
          "ApiKey": "",
          "Model": "gpt-4o-mini",
          "Weight": 3,
          "Priority": 1,
          "MaxContextTokens": 128000,
          "Capabilities": ["chat", "tools", "vision"],
          "CostPerToken": 0.00000015,
          "Tags": ["primary", "low-latency"]
        },
        {
          "Name": "opencode-deepseek",
          "ModelName": "deepseek-v4-pro",
          "Provider": "OpenCodeGo",
          "Endpoint": "https://opencode.ai/zen/go/v1",
          "ApiKey": "",
          "Model": "deepseek-v4-pro",
          "Weight": 1,
          "Priority": 2,
          "MaxContextTokens": 128000,
          "Capabilities": ["chat", "tools", "reasoning"],
          "CostPerToken": 0.000002,
          "Tags": ["coding", "reasoning"]
        },
        {
          "Name": "local-gemma",
          "ModelName": "gemma-local",
          "Provider": "LocalGemma",
          "Endpoint": "",
          "ApiKey": "",
          "Model": "",
          "Weight": 1,
          "Priority": 3,
          "MaxContextTokens": 4096,
          "Capabilities": ["chat"],
          "CostPerToken": 0,
          "Tags": ["local", "offline"]
        },
        {
          "Name": "ollama-gemma4",
          "ModelName": "gemma4-local",
          "Provider": "Ollama",
          "Endpoint": "http://192.168.16.12:11434",
          "ApiKey": "",
          "Model": "gemma4:e4b",
          "Weight": 1,
          "Priority": 1,
          "MaxContextTokens": 32768,
          "Capabilities": ["chat"],
          "CostPerToken": 0,
          "Tags": ["local", "quick"]
        }
      ],
      "ModelRouting": {
        "gpt-4o-mini": {
          "Strategy": "latency",
          "Deployments": ["azure-gpt4-mini"],
          "Fallbacks": ["opencode-deepseek"]
        },
        "deepseek-v4-pro": {
          "Strategy": "round_robin",
          "Deployments": ["opencode-deepseek"],
          "Fallbacks": ["local-gemma"]
        },
        "gemma-local": {
          "Strategy": "round_robin",
          "Deployments": ["ollama-gemma4", "local-gemma"],
          "Fallbacks": []
        }
      }
    }
  }
}
```

### Virtual model → catalog binding

Existing `VirtualModelOptions` gains a `CatalogRouting` field that ties a virtual model to a catalog model_name:

```json
"VirtualModels": {
  "codebrewRouter": {
    "ModelId": "codebrewRouter",
    "CatalogModel": "gpt-4o-mini",
    "SystemPrompt": "..."
  },
  "codebrewSharpClient": {
    "ModelId": "codebrewSharpClient",
    "CatalogModel": "deepseek-v4-pro",
    "SystemPrompt": "You are a C# assistant.",
    "McpServers": ["microsoft-learn"]
  },
  "yardly": {
    "ModelId": "yardly",
    "CatalogModel": "gpt-4o-mini",
    "SystemPrompt": "...",
    "ResponseContract": "yardly-json"
  }
}
```

When `CatalogModel` is set, the virtual model's routing resolves through the catalog instead of the old keyed-DI path. If unset, it falls back to current behavior.

---

## 4. New Types

### `ProviderDeployment`

```csharp
public sealed record ProviderDeployment
{
    public required string Name { get; init; }
    public required string ModelName { get; init; }
    public required string Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public int Weight { get; init; } = 1;
    public int Priority { get; init; } = 10;
    public int MaxContextTokens { get; init; } = 4096;
    public string[] Capabilities { get; init; } = [];
    public double CostPerToken { get; init; } = 0;
    public string[] Tags { get; init; } = [];
    public bool Enabled { get; init; } = true;
}
```

### `CatalogModelRoute`

```csharp
public sealed record CatalogModelRoute
{
    public required string ModelName { get; init; }
    public required string Strategy { get; init; }  // "round_robin" | "shuffle" | "latency" | "cost" | "least_busy"
    public required string[] Deployments { get; init; }
    public string[] Fallbacks { get; init; } = [];
}
```

### `IRoutingStrategy`

```csharp
public interface IRoutingStrategy
{
    string Name { get; }
    ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context);
}
```

### `RoutingContext`

```csharp
public sealed record RoutingContext(
    string ModelId,
    int EstimatedInputTokens,
    bool StreamingRequested,
    bool ToolsRequested,
    bool VisionRequested,
    CancellationToken CancellationToken);
```

### `IProviderCatalog`

```csharp
public interface IProviderCatalog
{
    IReadOnlyList<ProviderDeployment> GetAllDeployments();
    IReadOnlyList<ProviderDeployment> GetDeploymentsForModel(string modelName);
    CatalogModelRoute? GetRoute(string modelName);
    ProviderDeployment? GetDeployment(string name);
    void ReportHealth(string deploymentName, bool healthy);
    bool IsHealthy(string deploymentName);
}
```

---

## 5. Routing Strategies

### 5.1 RoundRobin

Cycles through eligible deployments in order. Tracks a per-model-name counter.

- Use when: all deployments are equivalent, just want load spreading

### 5.2 Shuffle

Random weighted selection. Weight field biases probability.

- Use when: A/B testing, traffic splitting between providers

### 5.3 Latency-Based

Selects the deployment with the lowest recent average latency. Maintains a sliding window of response times per deployment. Falls back to shuffle on no data.

- Use when: latency-sensitive apps, multi-region deployments

### 5.4 Cost-Based

Selects the cheapest eligible deployment. Ties broken by shuffle.

- Use when: cost optimization is a priority

### 5.5 Least-Busy

Selects the deployment with the fewest in-flight requests.

- Use when: avoiding hotspotting, concurrency management

### 5.6 Health-Aware Filtering

All strategies share a pre-filter that removes:
- Deployments with `Enabled = false`
- Deployments where `IsHealthy(name)` returns `false`
- Deployments missing required capabilities (e.g. vision requested but deployment says no vision)

---

## 6. Health Integration

The existing `ProviderHealthMonitor` (or a new `DeploymentHealthMonitor`) probes deployments on a configurable interval. The catalog's `IsHealthy()` queries the latest health state.

### Probe per deployment

For each deployment, the health checker:
1. Sends a lightweight probe (e.g. `GET /health` or a tiny chat completion)
2. Records success/failure + latency
3. Updates `DeploymentHealth` state with: `lastSeen`, `consecutiveFailures`, `averageLatencyMs`

### Circuit breaker

If `consecutiveFailures >= threshold` (default: 3), the deployment is marked unhealthy. A background recovery probe runs every `recoveryIntervalSeconds` (default: 30). On success, it's restored.

---

## 7. Fallback & Failover

When `Strategy.Select()` returns null (no healthy deployment):

1. **Failover chain** — try each deployment in `Fallbacks` in order
2. **Circuit breaker** — if all deployments and fallbacks fail, return a clear `503 Service Unavailable` with `X-CodebrewRouter-Error: no_healthy_deployment`

### Streaming fallback

If streaming primary fails mid-stream (`first-chunk probe` succeeds but stream errors mid-way):
- Log the failure
- Return `data: [DONE]` with error metadata in response
- Do NOT restart mid-stream on a different deployment (impractical for SSE)

---

## 8. Migration Path

### Phase 1 (this feature)

1. Define `ProviderDeployment`, `CatalogModelRoute`, `IProviderCatalog` types in Core
2. Implement `InMemoryProviderCatalog` — populated from config at startup
3. Implement all 5 routing strategies
4. Wire health checks into catalog
5. Add `CatalogModel` field to `VirtualModelOptions`
6. Update `ModelSelectionResolver` to route through catalog when `CatalogModel` is set
7. Full test coverage

### Phase 2 (future)

1. Config hot-reload (watch YAML/JSON for changes, update catalog live)
2. Redis-backed catalog for multi-instance deployments
3. Dynamic deployment registration via admin API
4. Cost tracking + spend ledger integration
5. Per-deployment RPM/TPM rate limiting
6. Admin UI for catalog management

---

## 9. Testing Strategy

| Test | What it covers |
|------|---------------|
| `ProviderCatalog_RegistersDeploymentsFromConfig` | Config → catalog |
| `RoundRobinStrategy_RotatesThroughDeployments` | Strategy correctness |
| `ShuffleStrategy_RespectsWeights` | Weighted distribution |
| `LatencyStrategy_SelectsFastest` | Latency-aware selection |
| `CostStrategy_SelectsCheapest` | Cost-aware selection |
| `HealthAwareRouting_SkipsUnhealthyDeployments` | Health → routing |
| `FallbackChain_TriesAllBeforeFailing` | Fallback ordering |
| `CatalogModel_BindsVirtualModelToDeployments` | Virtual model integration |
| `AllStrategies_HandleEmptyPool_Gracefully` | Edge case: no candidates |

---

## 10. Open Questions

1. Should the catalog support per-deployment API keys via env-var references (e.g. `$AZURE_OPENAI_KEY`) for security?
2. Should `LatencyStrategy` use EMA (exponential moving average) or a simple sliding window?
3. Should the catalog be hot-reloadable in Phase 1 via `IOptionsMonitor<T>`, or defer to Phase 2?
4. How should streaming failures during mid-stream be surfaced to clients — pass-through the upstream error, or mask it with a generic message?
