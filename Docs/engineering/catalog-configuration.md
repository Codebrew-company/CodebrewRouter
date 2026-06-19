# Provider Catalog Configuration

> **Status:** MVP  
> **Date:** 2026-06-19  
> **Feature:** `feature/provider-catalog-routing`  
> **Source spec:** `Docs/superpowers/specs/2026-05-25-provider-catalog-routing-engine.md`

---

## 1. Overview

The CodebrewRouter provider catalog is a config-driven registry of LLM provider deployments. It maps model names to deployment chains with configurable routing strategies, health-aware filtering, and fallback chains — giving CodebrewRouter LiteLLM-class routing proxy capabilities.

When a virtual model's `CatalogModel` field is set, the `ModelSelectionResolver` resolves its deployments through the catalog instead of the legacy keyed-DI path.

---

## 2. Config Location

Catalog configuration lives under `LlmGateway:ProviderCatalog` in `appsettings.json`:

```json
{
  "LlmGateway": {
    "ProviderCatalog": {
      "DefaultRoutingStrategy": "round_robin",
      "DefaultFallbackStrategy": "failover",
      "HealthCheckIntervalSeconds": 30,
      "UnhealthyThreshold": 3,
      "RecoveryIntervalSeconds": 30,
      "Deployments": [ /* ... */ ],
      "ModelRouting": { /* ... */ }
    }
  }
}
```

---

## 3. Config Shape

### 3.1 Top-Level Settings

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `DefaultRoutingStrategy` | string | `"round_robin"` | Fallback strategy when `ModelRouting[model].Strategy` is absent |
| `DefaultFallbackStrategy` | string | `"failover"` | Default fallback behavior |
| `HealthCheckIntervalSeconds` | int | 30 | Seconds between health probes |
| `UnhealthyThreshold` | int | 3 | Consecutive failures before marking deployment unhealthy |
| `RecoveryIntervalSeconds` | int | 30 | Seconds between recovery probes for unhealthy deployments |

### 3.2 Deployment

Each entry in the `Deployments` array registers one provider instance:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Name` | string | **Yes** | Unique deployment identifier (e.g. `"local-gemma"`) |
| `ModelName` | string | **Yes** | Model name this deployment serves (e.g. `"gemma-local"`) |
| `Provider` | string | **Yes** | Provider key matching a registered `IChatClient` (e.g. `"LocalGemma"`, `"OpenCodeGo"`, `"LmStudio"`) |
| `Endpoint` | string | No | Provider endpoint URL |
| `ApiKey` | string | No | API key (prefer env-var references in production) |
| `Model` | string | No | Model parameter passed to provider |
| `Weight` | int | 1 | Routing weight (higher = more traffic in shuffle/weighted strategies) |
| `Priority` | int | 10 | Priority (lower = preferred in priority-based selection) |
| `MaxContextTokens` | int | 4096 | Maximum context window tokens |
| `Capabilities` | string[] | `[]` | Supported capabilities: `"chat"`, `"tools"`, `"vision"`, `"reasoning"`, `"streaming"` |
| `CostPerToken` | double | 0 | Estimated cost per token (used by `cost` strategy) |
| `Tags` | string[] | `[]` | Free-form tags: `"local"`, `"offline"`, `"primary"`, `"low-latency"`, `"coding"` |
| `Enabled` | bool | `true` | Whether this deployment is active |

### 3.3 ModelRouting

Each key in `ModelRouting` maps a model name to its routing configuration:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Strategy` | string | **Yes** | Routing strategy (see §4) |
| `Deployments` | string[] | **Yes** | List of deployment `Name` values to route between |
| `Fallbacks` | string[] | `[]` | Fallback deployment names in priority order |

---

## 4. Routing Strategies

### 4.1 `round_robin`

Cycles through eligible deployments sequentially. Tracks a per-model-name counter.

**Use when:** All deployments are equivalent, just want load spreading.

### 4.2 `shuffle`

Random weighted selection. The `Weight` field biases probability.

**Use when:** A/B testing, traffic splitting between providers.

### 4.3 `latency`

Selects the deployment with the lowest recent average latency. Maintains a sliding window of response times per deployment. Falls back to shuffle when no latency data is available.

**Use when:** Latency-sensitive apps, multi-region deployments.

### 4.4 `cost`

Selects the cheapest eligible deployment by `CostPerToken`. Ties broken by shuffle.

**Use when:** Cost optimization is a priority.

### 4.5 `least_busy`

Selects the deployment with the fewest in-flight requests.

**Use when:** Avoiding hotspotting, concurrency management.

### Health-Aware Filtering (All Strategies)

Every strategy automatically filters out:
- Deployments with `Enabled = false`
- Deployments marked unhealthy (`IsHealthy()` returns `false`)
- Deployments missing required capabilities (e.g. vision requested but deployment doesn't support it)

---

## 5. Virtual Model Binding

Virtual models bind to the catalog via the `CatalogModel` field in `VirtualModelOptions`:

```json
{
  "LlmGateway": {
    "VirtualModels": {
      "codebrewOffline": {
        "ModelId": "codebrewOffline",
        "CatalogModel": "gemma-local",
        "SystemPrompt": "You are an offline-only assistant backed by local Gemma."
      },
      "codebrewRouter": {
        "ModelId": "codebrewRouter",
        "CatalogModel": "gpt-4o-mini",
        "SystemPrompt": "You are CodebrewRouter, an intelligent LLM gateway assistant."
      },
      "codebrewSharpClient": {
        "ModelId": "codebrewSharpClient",
        "CatalogModel": "deepseek-v4-pro",
        "SystemPrompt": "You are a C# and .NET specialist.",
        "McpServers": ["microsoft-learn"]
      }
    }
  }
}
```

When `CatalogModel` is set, the `ModelSelectionResolver` routes through the catalog:
1. Looks up `CatalogModel` in `ModelRouting`
2. Resolves the strategy
3. Selects the best healthy deployment
4. Returns the resolved `IChatClient`

When `CatalogModel` is null/absent, the virtual model falls back to legacy keyed-DI resolution.

---

## 6. Health Integration

The `HealthProbeService` background service probes each deployment on the configured interval:

1. Sends a lightweight probe (chat completion with `"ping"`)
2. Records success/failure with latency
3. Reports health to the catalog via `IProviderCatalog.ReportHealth()`

**Circuit breaker:** After `UnhealthyThreshold` consecutive failures, the deployment is marked unhealthy. A recovery probe runs every `RecoveryIntervalSeconds` until it succeeds, at which point the deployment is restored.

---

## 7. Fallback & Failover

When no healthy deployment is available from the primary strategy:

1. **Failover chain** — tries each deployment in `Fallbacks` in order
2. **All exhausted** — returns `503 Service Unavailable` with `X-CodebrewRouter-Error: no_healthy_deployment`

The `LlmRoutingChatClient` handles the failover chain with context-overflow awareness: if the primary deployment fails due to context window overflow, fallback deployments with windows too small are skipped.

---

## 8. Example Configs

### 8.1 Offline-Only (MVP Current)

```json
{
  "ProviderCatalog": {
    "DefaultRoutingStrategy": "round_robin",
    "HealthCheckIntervalSeconds": 30,
    "UnhealthyThreshold": 3,
    "RecoveryIntervalSeconds": 30,
    "Deployments": [
      {
        "Name": "local-gemma",
        "ModelName": "gemma-local",
        "Provider": "LocalGemma",
        "Endpoint": "",
        "ApiKey": "",
        "Model": "",
        "Weight": 1,
        "Priority": 10,
        "MaxContextTokens": 4096,
        "Capabilities": ["chat"],
        "CostPerToken": 0,
        "Tags": ["local", "offline"],
        "Enabled": true
      }
    ],
    "ModelRouting": {
      "gemma-local": {
        "Strategy": "round_robin",
        "Deployments": ["local-gemma"],
        "Fallbacks": []
      }
    }
  }
}
```

### 8.2 Hybrid Local + Cloud

```json
{
  "ProviderCatalog": {
    "DefaultRoutingStrategy": "latency",
    "Deployments": [
      {
        "Name": "azure-gpt4-mini",
        "ModelName": "gpt-4o-mini",
        "Provider": "AzureFoundry",
        "Endpoint": "https://eastus.api.cognitive.microsoft.com",
        "Model": "gpt-4o-mini",
        "Weight": 3,
        "Priority": 1,
        "MaxContextTokens": 128000,
        "Capabilities": ["chat", "tools", "vision"],
        "CostPerToken": 0.00000015,
        "Tags": ["primary", "low-latency"],
        "Enabled": true
      },
      {
        "Name": "opencode-deepseek",
        "ModelName": "deepseek-v4-pro",
        "Provider": "OpenCodeGo",
        "Endpoint": "https://opencode.ai/zen/go/v1",
        "Model": "deepseek-v4-pro",
        "Weight": 1,
        "Priority": 2,
        "MaxContextTokens": 128000,
        "Capabilities": ["chat", "tools", "reasoning"],
        "CostPerToken": 0.000002,
        "Tags": ["coding", "reasoning"],
        "Enabled": true
      },
      {
        "Name": "local-gemma",
        "ModelName": "gemma-local",
        "Provider": "LocalGemma",
        "Weight": 1,
        "Priority": 10,
        "MaxContextTokens": 4096,
        "Capabilities": ["chat"],
        "CostPerToken": 0,
        "Tags": ["local", "offline"],
        "Enabled": true
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
        "Deployments": ["local-gemma"],
        "Fallbacks": []
      }
    }
  }
}
```

### 8.3 Multi-Region (Latency-Optimized)

```json
{
  "ProviderCatalog": {
    "DefaultRoutingStrategy": "latency",
    "Deployments": [
      {
        "Name": "azure-eastus",
        "ModelName": "gpt-4o-mini",
        "Provider": "AzureFoundry",
        "Endpoint": "https://eastus.api.cognitive.microsoft.com",
        "Model": "gpt-4o-mini",
        "Weight": 1,
        "Priority": 1,
        "MaxContextTokens": 128000,
        "Capabilities": ["chat", "tools", "vision"],
        "CostPerToken": 0.00000015,
        "Tags": ["us-east", "low-latency"],
        "Enabled": true
      },
      {
        "Name": "azure-westeurope",
        "ModelName": "gpt-4o-mini",
        "Provider": "AzureFoundry",
        "Endpoint": "https://westeurope.api.cognitive.microsoft.com",
        "Model": "gpt-4o-mini",
        "Weight": 1,
        "Priority": 2,
        "MaxContextTokens": 128000,
        "Capabilities": ["chat", "tools", "vision"],
        "CostPerToken": 0.00000015,
        "Tags": ["eu-west", "failover"],
        "Enabled": true
      }
    ],
    "ModelRouting": {
      "gpt-4o-mini": {
        "Strategy": "latency",
        "Deployments": ["azure-eastus", "azure-westeurope"],
        "Fallbacks": []
      }
    }
  }
}
```

---

## 9. Logging

When the catalog resolves a deployment, the following log lines appear:

```
🔀 ResolveTargetClient: Getting routing decision...
  ├─ Routing strategy decided: local-gemma
  └─ Found registered client for local-gemma
```

For catalog-bound virtual models:
```
Offline-only mode active; resolved virtual model codebrewOffline to catalog deployment local-gemma (LocalGemma)
```

All routing decisions use the `[ROUTER-*]` logging contract tags.

---

## 10. Migration from Legacy Path

| Aspect | Legacy (keyed-DI) | Catalog |
|--------|------------------|---------|
| Resolution | `IServiceProvider.GetKeyedService<IChatClient>(provider)` | `IProviderCatalog` → `RoutingStrategy.Select()` → resolved `IChatClient` |
| Config | Per-provider options in `Providers` section | `Deployments` array in `ProviderCatalog` |
| Health | `ModelAvailabilityRegistry` + `IModelAvailabilityRegistry` | `IProviderCatalog.IsHealthy()` + `HealthProbeService` |
| Failover | `IFailoverStrategy` with config-driven chain | `Fallbacks` array per model route + circuit breaker |
| Virtual models | `VirtualModelOptions` without `CatalogModel` | `VirtualModelOptions` with `CatalogModel` set |

Existing virtual models without `CatalogModel` continue to work through the legacy path unchanged.

---

## 11. References

- [Provider Catalog & Routing Strategy Engine Spec](../superpowers/specs/2026-05-25-provider-catalog-routing-engine.md)
- [CodebrewRouter Agentic MVP Plan](../agents/codebrewrouter-agentic-mvp-plan.md)
- [Logging Contract](logging-contract.md)
