# CodebrewRouter MVP Completion Plan — Branch `task/latest-changeset`

> **Reviewer:** derp-thinker (strategic reasoning agent)
> **Date:** 2026-06-19
> **Branch:** `task/latest-changeset` (12 commits ahead of `master`)
> **Repo:** `Blaze.LlmGateway` (.NET 10 LLM routing proxy)

---

## Executive Summary

The branch has built a **substantial provider catalog + routing strategy engine** (~6,700 lines new code, 1,600 lines of catalog-specific tests, 11,000 total test lines). The core types, all 5 routing strategies, health-aware filtering, circuit breaker, health probe service, and virtual model binding are implemented and tested. However, **the catalog is not wired end-to-end** — no `ProviderCatalog` configuration exists in `appsettings.json`, meaning the catalog is registered in DI but never populated with deployments. The system works entirely through the legacy keyed-DI path.

**MVP status: ~70% complete.** The heavy lifting (types, strategies, health, circuit breaker, tests) is done. What remains is: wiring the catalog to config, adding deployments, end-to-end testing, documentation, and a CI pipeline.

---

## 1. What's Done vs Missing

### ✅ DONE (implemented and tested)

| Component | Details |
|---|---|
| **Core catalog types** | `ProviderDeployment`, `CatalogModelRoute`, `RoutingContext`, `IProviderCatalog`, `IRoutingStrategy`, `ProviderCatalogOptions` — all in `Core/Catalog/` |
| **InMemoryProviderCatalog** | Thread-safe, config-populated, health-state tracking, 143 lines |
| **5 routing strategies** | `RoundRobinStrategy`, `ShuffleStrategy` (weighted), `LatencyStrategy` (sliding window), `CostStrategy`, `LeastBusyStrategy` |
| **Health-aware pre-filter** | Shared `HealthAwareRoutingFilter` that all strategies call — filters `Enabled`, `IsHealthy`, capabilities |
| **CircuitBreakerChatClient** | MEAI `DelegatingChatClient` wrapping per-deployment IChatClient; short-circuits unhealthy, reports success/failure |
| **CatalogMetricsChatClient** | Production wrapper combining circuit-breaker + latency reporting + in-flight tracking (unifies CB and metrics) |
| **HealthProbeService** | Background service pinging all deployments every N seconds via keyed IChatClient resolution |
| **CatalogModelRouter** | Resolves catalog model → route → strategy → deployment; with primary+fallback deployment pools |
| **RoutingStrategyResolver** | Maps 5 strategy names → cached singletons |
| **Virtual model binding** | `VirtualModelOptions.CatalogModel` field + `ModelSelectionResolver` integration; offline/online paths both handle catalog |
| **CodebrewRouterChatClient** | Existing task-classified, multi-provider fallback chain router (625 lines) |
| **Docker deployment** | Multi-stage Dockerfile + `docker-compose.yml` with Open WebUI on :8080 |
| **Python pipeline** | 5-stage YouTube AI pipeline (1,244 lines) with retry logic, executor fallback chains, complexity tiers |
| **Pipeline watchdog** | Telegram alert script for pipeline failure/staleness detection |
| **Unit tests (catalog)** | ~1,600 lines: `CatalogModelRouterTests`, `CatalogTypesTests`, `CircuitBreakerChatClientTests`, `HealthProbeServiceTests`, `InMemoryProviderCatalogTests`, `CatalogRoutingStrategyTests` |
| **Unit tests (total)** | ~11,000 lines across 39 test files |
| **Protocol MVP tests** | `ProtocolMvpTests.cs` (126 lines) |

### ❌ MISSING (blockers to MVP)

| Gap | Severity | Details |
|---|---|---|
| **No ProviderCatalog config in appsettings.json** | 🔴 CRITICAL | The `ProviderCatalog` section is **entirely absent** from `appsettings.json`. No `Deployments` array, no `ModelRouting` dictionary. The catalog is registered in DI but always empty — the system uses the legacy keyed-DI path exclusively. |
| **No CatalogModel bindings on virtual models** | 🔴 CRITICAL | None of the 5 virtual models (`codebrewSharpClient`, `codebrewPlanner`, `codebrewCouncil`, `yardly`, `codebrewOffline`) have `CatalogModel` set. They all resolve through `CodebrewRouter` (legacy path). |
| **No end-to-end catalog integration test** | 🟡 HIGH | Tests exist for individual components, but no integration test proving: config → catalog → routing → circuit breaker → health probe → actual chat completion. The plan docs reference a `HealthRoutingIntegrationTests.cs` that was never created. |
| **serviceDefaults not wired in API** | 🟡 HIGH | `Program.cs` references `ServiceDefaults` but never calls `builder.AddServiceDefaults()` — OTel, HTTP resilience, health endpoints missing. |
| **No CI/CD pipeline for .NET build/test** | 🟡 HIGH | No GitHub Actions workflow for `dotnet build` + `dotnet test`. Only AI code review workflow exists. |
| **LlmRoutingChatClient implements IChatClient directly** | 🟡 MEDIUM | Should be `DelegatingChatClient` per known technical debt. |
| **Health probe sends real pings** | 🟡 MEDIUM | The `HealthProbeService` sends `"ping"` with `MaxOutputTokens=1` to real providers — incurs token cost. Need a lighter health check method or opt-in gating. |
| **Provider keys vs deployment providers** | 🟡 MEDIUM | `HealthProbeService` resolves keyed `IChatClient` by deployment's `Provider` field (e.g., "AzureFoundry"). But many deployments map to the same provider — the probe won't distinguish deployment-specific endpoints. |
| **Streaming mid-stream failure handling** | 🟡 MEDIUM | `CircuitBreakerChatClient` handles per-chunk streaming errors but `CodebrewRouterChatClient` only runs a first-chunk probe. Full mid-stream failover is incomplete. |
| **No authentication** | 🟢 LOW | API has no key/bearer token enforcement. |
| **No rate limiting** | 🟢 LOW | No per-client or per-provider throttle. |
| **Benchmarks are placeholder** | 🟢 LOW | BenchmarkDotNet project is `Console.WriteLine("Hello, World!")`. |
| **Python pipeline hardcoded paths** | 🟢 LOW | `scan_codebase.py` hardcodes `/mnt/data/src/CodebrewRouter` — won't work outside that mount. |

### ⚠️ BRANCH DIVERGENCE CONCERN

The branch has **merged downstream commits** (AI code review workflow, 3-tier review) that exist as a separate merge from an unrelated branch. This means `task/latest-changeset` contains commits that aren't part of the catalog routing feature. When merging back to `master`, careful rebase/squash strategy is needed to avoid bringing in the review workflow prematurely or getting merge conflicts from the divergent `feature/provider-catalog-routing` subtree.

---

## 2. Priority Order for Remaining Work

Priority is ordered by **MVP unblocking value** — each step eliminates a hard blocker to a working end-to-end routing proxy.

| Priority | Task | Unlocks |
|---|---|---|
| **P0** | Add `ProviderCatalog` config section to `appsettings.json` with real deployments | Makes the catalog functional — routing engine has data to work with |
| **P1** | Wire `CatalogModel` on 2-3 virtual models (e.g., `codebrewOffline` → gemma-local) | Proves end-to-end catalog routing through virtual model binding |
| **P2** | Write catalog end-to-end integration test + fix any discovered bugs | Catches integration gaps before they reach users |
| **P3** | Wire `AddServiceDefaults()` in API Program.cs + add CI .NET build/test workflow | Production readiness, observability, prevents regressions |
| **P4** | Refactor `LlmRoutingChatClient` to `DelegatingChatClient` | Removes known technical debt, improves streaming failover |
| **P5** | Health probe: use lightweight endpoint or config-gating to avoid token waste | Cost safety for production |

---

## 3. Architecture Risks & Concerns

### Risk 1: Catalog Config Gap (CRITICAL)
The catalog is a sophisticated routing engine with zero configuration. All 6 strategies, the circuit breaker, the health probe, and the `CatalogModelRouter` are registered in DI but operate on an empty catalog. The entire system works through the legacy `CodebrewRouterChatClient` → `ModelSelectionResolver` → keyed DI path. **This means the feature branch, despite 6,700 new lines of code, does not exercise the new routing engine at runtime.** The catalog is "dark" — built, tested, but not configured.

### Risk 2: Provider Key vs Deployment Ambiguity
`HealthProbeService.ProbeDeploymentAsync()` resolves a keyed `IChatClient` by `deployment.Provider` (e.g., "OpenCodeGo"). But if two deployments (say `opencode-deepseek` and `opencode-qwen`) both have `Provider = "OpenCodeGo"`, the probe would reuse the same keyed client for both. This conflates health signals — one unhealthy deployment could incorrectly mark the provider as unhealthy for the other. The spec's example config shows per-deployment endpoints that differ even within the same provider family.

### Risk 3: Streaming Circuit Breaker Race
`CircuitBreakerChatClient.StreamingImpl()` and `CatalogMetricsChatClient.StreamingImpl()` both handle mid-stream failures by catching exceptions during `MoveNextAsync()`. However, the HTTP response stream may have already delivered partial chunks to the SSE client. The circuit opens *after* partial data is delivered — the client sees truncated output with no way to request retry. The `CodebrewRouterChatClient` mitigates this with first-chunk probing, but the catalog path (`CatalogMetricsChatClient`) doesn't.

### Risk 4: No Config Hot-Reload
The `InMemoryProviderCatalog` is built once at startup from `IOptions<ProviderCatalogOptions>`. Config changes require a restart. For an MVP this is acceptable, but it means deployment rotation (adding/removing backends) requires downtime.

### Risk 5: Health Probe Token Cost
`HealthProbeService` sends `ChatMessage(ChatRole.User, "ping")` with `MaxOutputTokens = 1` to **every deployment** every `HealthCheckIntervalSeconds` (default 30s). Even with 1 output token, each probe consumes input tokens for the `"ping"` string + overhead. For paid providers (Azure, OpenCodeGo), this is a recurring cost. The spec calls this out as a known tradeoff but offers no mitigation in the current implementation.

### Risk 6: Branch Merge Complexity
The branch has 12 commits with 6,748 lines changed across 54 files. It also contains a merge from an unrelated branch (AI code review workflow). A straightforward `git merge` could bring in unwanted commits. Recommended: squash-merge or interactive rebase to produce a clean merge commit.

---

## 4. Concrete 3-5 Step Plan to MVP

### Step 1: Configure the Provider Catalog (P0)
**File:** `Blaze.LlmGateway.Api/appsettings.json`

Add a `ProviderCatalog` section under `LlmGateway` with at minimum:
- 2-3 `Deployments` (e.g., `local-gemma`, `lmstudio-codebrew`, `opencode-deepseek`)
- 1 `ModelRouting` entry (e.g., `gemma-local` with `Strategy: "round_robin"` and 2 deployments)
- Wire `CatalogModel: "gemma-local"` on the `codebrewOffline` virtual model

This is the minimum config to prove the catalog routing engine works end-to-end. The `codebrewOffline` virtual model is the safest first target since it already uses `LocalGemma` only and has no cloud dependencies.

### Step 2: End-to-End Integration Test (P1)
**File:** `Blaze.LlmGateway.Tests/Catalog/CatalogEndToEndIntegrationTests.cs`

Write a test that:
1. Builds a `ServiceProvider` with a populated `InMemoryProviderCatalog` (2 deployments, 1 healthy, 1 unhealthy)
2. Resolves `CatalogModelRouter.SelectDeployment("gemma-local", context)`
3. Asserts the healthy deployment is selected
4. Marks it unhealthy via `ReportHealth(name, false)` 3 times
5. Asserts the fallback deployment is selected
6. Verifies `CircuitBreakerChatClient` throws when unhealthy
7. (Optional) Verifies `HealthProbeService.ProbeAsync()` updates health state

This validates the full chain without requiring real network calls. Use `MockChatClient` for provider responses.

### Step 3: Production Wiring (P2)
- **`Program.cs`:** Call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()` to enable OTel, HTTP resilience, and `/health`/`/alive` endpoints
- **`.github/workflows/dotnet-ci.yml`:** Create a simple workflow that runs `dotnet build` + `dotnet test` on PR
- **Refactor `LlmRoutingChatClient`** to inherit from `DelegatingChatClient` (removes known technical debt, enables proper middleware stacking)

### Step 4: Health Probe Hardening (P2)
- Add a `HealthCheckMethod` config option: `"ping"` (current), `"none"` (disable probes, rely on circuit breaker only), `"head"` (HTTP HEAD to endpoint)
- Default to `"none"` for self-hosted/local deployments where token cost matters
- Fix the provider-key ambiguity: make `HealthProbeService` resolve by `deployment.Name`-keyed `IChatClient` rather than `deployment.Provider`-keyed

### Step 5: Documentation & Branch Cleanup (P3)
- Add a `Docs/engineering/catalog-configuration.md` documenting the config shape, strategy descriptions, and deployment model
- Squash-merge `task/latest-changeset` into `master` with a clean commit message, excluding the AI code review merge subtree
- Tag `v0.1.0-mvp` on the merge commit

---

## Appendix: File Inventory (branch diff)

```
New types (Core/Catalog/):
  CatalogModelRoute.cs         (22 lines)
  IProviderCatalog.cs          (27 lines)
  IRoutingStrategy.cs          (17 lines)
  ProviderDeployment.cs        (47 lines)
  RoutingContext.cs            (19 lines)

New config (Core/Configuration/):
  ProviderCatalogOptions.cs    (66 lines)

Infrastructure — Catalog:
  CatalogMetricsChatClient.cs  (164 lines)
  CatalogModelRouter.cs        (111 lines)
  CircuitBreakerChatClient.cs  (136 lines)
  HealthProbeService.cs        (136 lines)
  InMemoryProviderCatalog.cs   (143 lines)

Infrastructure — RoutingStrategies/Catalog/:
  CostStrategy.cs              (57 lines)
  HealthAwareRoutingFilter.cs  (60 lines)
  IRoutingStrategyResolver.cs  (18 lines)
  LatencyStrategy.cs           (112 lines)
  LeastBusyStrategy.cs         (99 lines)
  RoundRobinStrategy.cs        (49 lines)
  RoutingStrategyResolver.cs   (43 lines)
  ShuffleStrategy.cs           (77 lines)

Tests (Catalog + RoutingStrategies):
  CatalogModelRouterTests.cs      (254 lines)
  CatalogTypesTests.cs            (265 lines)
  CircuitBreakerChatClientTests.cs(258 lines)
  HealthProbeServiceTests.cs      (264 lines)
  InMemoryProviderCatalogTests.cs (291 lines)
  CatalogRoutingStrategyTests.cs  (269 lines)
  ProtocolMvpTests.cs             (126 lines)

Deployment:
  Dockerfile                    (39 lines)
  docker-compose.yml            (47 lines)
  .dockerignore                 (30 lines)

Scripts:
  youtubeai_5stage_pipeline.py (1244 lines)
  pipeline_watchdog.py          (110 lines)
  scan_codebase.py              (148 lines)

Plans:
  .hermes/plans/2026-05-26_171500-provider-catalog-routing-complete.md (497 lines)
  .hermes/plans/2026-05-26_172200-phase3-health-routing.md             (201 lines)
  Docs/superpowers/specs/2026-05-25-provider-catalog-routing-engine.md (371 lines)
```
