# CodebrewRouter MVP Completion Backlog

> **PM:** derp-pm
> **Date:** 2026-06-19
> **Branch:** `task/latest-changeset` (commit 5085afb)
> **Status when reviewed:** ~70% complete — catalog engine built, 0 catalog config wired. Routing engine is "dark."

---

## Synthesis of Existing Analysis

The architecture review (derp-thinker, June 19) correctly identifies that:

1. **The catalog routing engine is fully built and tested** (~6,700 new lines, 1,600 test lines with 39 test files total). Core types (`ProviderDeployment`, `CatalogModelRoute`, `IProviderCatalog`), all 5 strategies (`RoundRobin`, `Shuffle`, `Latency`, `Cost`, `LeastBusy`), `HealthAwareRoutingFilter`, `CircuitBreakerChatClient`, `CatalogMetricsChatClient`, `HealthProbeService`, `CatalogModelRouter`, `RoutingStrategyResolver` — all implemented and registered in DI.

2. **The catalog has zero configuration.** No `ProviderCatalog` section exists in `appsettings.json`. No `Deployments` array, no `ModelRouting` dictionary. The `InMemoryProviderCatalog` is instantiated from `ProviderCatalogOptions` which binds from `LlmGateway:ProviderCatalog` — but that section doesn't exist.

3. **The ModelSelectionResolver DOES handle catalog routing** (lines 36-57 for offline, 76-111 for online) — the code path is there but the `VirtualModelOptions.CatalogModel` is `null` on all 5 virtual models. So the resolver always falls through to `CodebrewRouter` (legacy keyed-DI path).

4. **The entire catalog subsystem is "dark"** — built, tested in isolation, registered in DI, but never exercised at runtime. The system operates entirely through the legacy keyed-DI path.

### Additional findings (derp-pm review)

- **`ServiceDefaults` IS wired** (line 45 of Program.cs: `builder.AddServiceDefaults()`), contradicting the review which said it was missing. However, `AddServiceDefaults()` is called **after** `builder.Logging.ClearProviders()` — this may clear providers set up by ServiceDefaults. Need to verify the calling order.
- **`MapDefaultEndpoints()` IS wired** (line 319), also listed as missing.
- **No CI workflow for .NET build/test** — confirmed.
- **No GitHub Actions workflows** exist for CI.
- **The ProviderCatalog section path is nested**: `LlmGateway:ProviderCatalog`, not `ProviderCatalog` at root. This is important for the config shape.

---

## Prioritized Work Items

Each item is **actionable**, has clear scope boundaries, and is assigned to the appropriate profile.

### 🔴 P0 — CRITICAL: Wiring the Catalog to Config

---

#### Item P0.1: Add ProviderCatalog config section to appsettings.json
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 30 min
**Prerequisites:** None

**What to do:**
In `Blaze.LlmGateway.Api/appsettings.json`, add a `ProviderCatalog` section under `LlmGateway`. The config must include at minimum:

1. **2-3 Deployments** — at least one for `LocalGemma` (offline/local) and optionally one for `OpenCodeGo` or `LmStudio`
2. **1 ModelRouting entry** — a `gemma-local` model with `Strategy: "round_robin"` and at least 1 deployment

**Why this order:** This is the single blocker. Until there's config, the catalog is always empty and all routing goes through the legacy path.

**Config shape to add** (place inside the `"LlmGateway": { ... }` object, at the same level as `"Providers"`):

```json
"ProviderCatalog": {
  "DefaultRoutingStrategy": "round_robin",
  "DefaultFallbackStrategy": "failover",
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
```

**Verification:**
- Start the API, look for log line: "Routing catalog model gemma-local with strategy round_robin"
- If `CatalogModel` is set on `codebrewOffline` virtual model, the offline path should resolve through the catalog

---

#### Item P0.2: Wire CatalogModel on codebrewOffline virtual model
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 15 min
**Prerequisites:** P0.1 (config must exist)

**What to do:**
In `appsettings.json`, in the `"VirtualModels"` → `"codebrewOffline"` object, add:
```json
"CatalogModel": "gemma-local"
```

**Why this model first:** `codebrewOffline` is the safest first target — it only uses `LocalGemma` (no cloud dependencies), is already set up, and targets `CodebrewRouter` Provider. Adding `CatalogModel` will make the `ModelSelectionResolver` route through the catalog path (lines 36-57 of `ModelSelectionResolver.cs`) instead of the legacy keyed-DI path.

**Verification:**
- After startup, any request with `model: "codebrewOffline"` should log: "Offline-only mode active; resolved virtual model codebrewOffline to catalog deployment local-gemma (LocalGemma)"

---

### 🟡 P1 — HIGH: End-to-End Validation

---

#### Item P1.1: Write catalog end-to-end integration test
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 2 hours
**Prerequisites:** P0.1 (config shape must be understood)

**What to do:**
Create `Blaze.LlmGateway.Tests/Catalog/CatalogEndToEndIntegrationTests.cs` with tests that validate the full chain:

1. **Test: Healthy deployment selected over unhealthy**
   - Build `ServiceProvider` with `InMemoryProviderCatalog` (2 deployments for same model, both healthy)
   - Resolve `CatalogModelRouter.SelectDeployment("test-model", context)`
   - Assert the healthy deployment is selected

2. **Test: Unhealthy deployment skipped, fallback used**
   - Mark primary unhealthy via `catalog.ReportHealth("primary-dep", false)` × 3
   - Assert fallback deployment is selected

3. **Test: CircuitBreakerChatClient throws when unhealthy**
   - Create `CircuitBreakerChatClient` wrapping a `MockChatClient` for an unhealthy deployment
   - Assert `InvalidOperationException` on `CompleteAsync`

4. **Test: CatalogMetricsChatClient wraps and reports health**
   - Use `MockChatClient` as inner, verify `IsHealthy` flip after success/failure

5. **Test: HealthProbeService probes and reports**
   - Use `MockChatClient`, call `ProbeDeploymentAsync`, verify `ReportHealth` was called

**Reference implementation patterns:**
- `CatalogModelRouterTests.cs` shows how to build a populated `InMemoryProviderCatalog`
- `CircuitBreakerChatClientTests.cs` shows MockChatClient usage
- `HealthProbeServiceTests.cs` shows health probe testing

---

#### Item P1.2: Verify serviceDefaults correct call ordering
**Status:** READY
**Assigned to:** derp-coder
**Estimated effort:** 15 min
**Prerequisites:** None

**What to do:**
In `Program.cs`, line 36-45:
```csharp
builder.Logging.ClearProviders();        // Line 36
builder.Logging.SetMinimumLevel(LogLevel.Debug);  // Line 37
builder.Logging.AddSimpleConsole(...);   // Line 38-43
builder.AddServiceDefaults();            // Line 45
```

`AddServiceDefaults()` may add OpenTelemetry logging providers. If it calls `AddConsole()` or `AddOpenTelemetry()`, this ordering should be fine since we're adding SimpleConsole before ServiceDefaults. However, verify that `ClearProviders()` at line 36 doesn't clear providers added by `AddServiceDefaults()` if/when the call order changes.

**Action:** Move `builder.AddServiceDefaults()` to be the FIRST service registration call (after `WebApplication.CreateBuilder`), then explicitly configure logging overrides after. This is the standard Aspire pattern.

**Recommended order:**
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();            // FIRST
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddSimpleConsole(...);
```

---

### 🟡 P2 — HIGH: Production Readiness

---

#### Item P2.1: Add GitHub Actions CI workflow for .NET build/test
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 45 min
**Prerequisites:** None

**What to do:**
Create `.github/workflows/dotnet-ci.yml`:

```yaml
name: .NET CI

on:
  push:
    branches: [master, task/latest-changeset]
  pull_request:
    branches: [master]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet test --no-build --verbosity normal

  lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet format --verify-no-changes --no-restore
```

---

#### Item P2.2: Refactor LlmRoutingChatClient to DelegatingChatClient
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 1-2 hours
**Prerequisites:** P1.1 (integration tests as safety net)

**What to do:**
`LlmRoutingChatClient` currently implements `IChatClient` directly. Per known technical debt, it should inherit from `DelegatingChatClient` to enable proper middleware stacking. The spec at `Docs/superpowers/specs/2026-05-25-provider-catalog-routing-engine.md` section "Streaming fallback" mentions incomplete mid-stream failover — this refactor would address it.

**Key changes:**
1. Change base class from `IChatClient` to `DelegatingChatClient`
2. Inner client becomes the `LmStudio` keyed client (the base chat client it wraps)
3. Override `CompleteAsync` and `CompleteStreamingAsync` with routing + failover logic
4. This allows `CircuitBreakerChatClient` and `CatalogMetricsChatClient` to wrap it properly

**File:** `Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs` (rename or keep name, but change base)

---

#### Item P2.3: Health probe hardening
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 1 hour
**Prerequisites:** None (independent)

**What to do:**
Two separate changes:

**A) Add HealthCheckMethod config option:**
In `ProviderCatalogOptions`, add:
```csharp
public string HealthCheckMethod { get; set; } = "ping";
// "ping" = current behavior (chat completion with "ping")
// "none" = disable probes entirely, rely on circuit breaker only
// "head" = HTTP HEAD to deployment Endpoint (future)
```

In `HealthProbeService`, check this option and skip probing when `"none"`.

**B) Fix provider-key ambiguity:**
`HealthProbeService.ProbeDeploymentAsync()` currently resolves keyed `IChatClient` by `deployment.Provider` (e.g., "AzureFoundry"). But multiple deployments can share the same provider key — the probe hits the same client for different deployments.

The fix: register per-deployment keyed clients in `AddLlmInfrastructure` using `deployment.Name` as the key, or resolve via the standard `ModelSelectionResolver` path. For MVP, adding a todo comment and documenting the limitation is acceptable.

---

### 🟢 P3 — LOW: Documentation & Polish

---

#### Item P3.1: Write catalog configuration documentation
**Status:** NOT STARTED
**Assigned to:** derp-researcher
**Estimated effort:** 45 min
**Prerequisites:** P0.1 (actual config shape exists)

**What to do:**
Create `Docs/engineering/catalog-configuration.md` covering:
1. Config shape (ProviderCatalog section, Deployments, ModelRouting)
2. Strategy descriptions (RoundRobin, Shuffle, Latency, Cost, LeastBusy)
3. Deployment model (Name, Provider mapping, Weight/Priority, Capabilities)
4. Health integration (HealthCheckIntervalSeconds, UnhealthyThreshold, circuit breaker)
5. Virtual model binding (CatalogModel field)
6. Example configs (offline-only, hybrid local+cloud, multi-region)

**Use the spec at `Docs/superpowers/specs/2026-05-25-provider-catalog-routing-engine.md` as the source of truth for descriptions.**

---

#### Item P3.2: Branch cleanup and merge strategy
**Status:** NOT STARTED
**Assigned to:** derp-pm (me) to coordinate; derp-coder to execute
**Estimated effort:** 30 min
**Prerequisites:** All P0-P2 items complete

**What to do:**
1. Ensure all work items are committed on `task/latest-changeset`
2. Squash-merge into `master` to avoid bringing in the unrelated AI code review merge subtree
3. Tag `v0.1.0-mvp` on the merge commit
4. Write release notes summarizing the catalog routing feature

---

#### Item P3.3: Fix Python pipeline hardcoded paths
**Status:** NOT STARTED
**Assigned to:** derp-coder (simple fix)
**Estimated effort:** 15 min
**Prerequisites:** None

**What to do:**
`scripts/scan_codebase.py` hardcodes `/mnt/data/src/CodebrewRouter`. Make it accept a command-line argument or environment variable instead.

---

#### Item P3.4: Benchmark project setup (placeholder → real)
**Status:** NOT STARTED
**Assigned to:** derp-coder
**Estimated effort:** 30 min
**Prerequisites:** P0.1

**What to do:**
`Blaze.LlmGateway.Benchmarks/Program.cs` is currently `Console.WriteLine("Hello, World!")`. Replace with a basic BenchmarkDotNet runner measuring:
- Routing strategy selection speed
- InMemoryProviderCatalog lookup speed
- Pipeline overhead (catalog routing vs direct keyed-DI)

---

## Coordination Plan

### Phase 1: derp-coder (P0 + P1)
- **Sprint:** Complete P0.1, P0.2, P1.1, P1.2 in sequence
- **Verification checkpoint:** After P0.1 + P0.2, run the API and confirm catalog log lines appear
- **Communication:** Report blockers to derp-pm

### Phase 2: derp-coder (P2)
- P2.1 (CI), P2.2 (DelegatingChatClient refactor), P2.3 (health probe)
- Can run P2.1 and P2.3 in parallel with P1.1

### Phase 3: derp-researcher + derp-coder (P3)
- derp-researcher handles P3.1 (docs)
- derp-coder handles P3.3 (python paths), P3.4 (benchmarks)
- derp-pm handles P3.2 (merge coordination)

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Config shape mismatch between spec and actual `ProviderCatalogOptions` binding | Medium | High | P1.1 integration test catches this; `ProviderCatalogOptions` uses `SectionName = "ProviderCatalog"` but binding path is `LlmGateway:ProviderCatalog` (nested under LlmGateway) — verify with P0.1 |
| `HealthProbeService` sends real pings on startup | High | Medium | P2.3 adds `HealthCheckMethod = "none"` option; default to "ping" is intentional for MVP but costs tokens |
| Catalog path resolves but returns wrong `IChatClient` because of provider key ambiguity | Medium | Medium | Documented in P2.3-B; for MVP with single-provider-per-deployment this is fine |
| `AddServiceDefaults()` clearing logging providers | Low | Low | P1.2 addresses call ordering |
| Branch merge complexity from downstream commits | Low | Medium | P3.2 uses squash-merge to avoid bringing in unwanted merge subtree |

---

## Summary of Assignments

| Profile | Items | Total Est. |
|---------|-------|-----------|
| **derp-coder** | P0.1, P0.2, P1.1, P1.2, P2.1, P2.2, P2.3, P3.3, P3.4 | ~7 hours |
| **derp-researcher** | P3.1 | ~45 min |
| **derp-pm** | P3.2 (merge coordination), overall tracking | ~30 min |
| **derp-thinker** | Available for architecture questions if any design gaps are found | On-call |

---

## Definition of MVP Done

- [ ] `ProviderCatalog` section exists in `appsettings.json` with at least 1 deployment and 1 model route
- [ ] At least `codebrewOffline` virtual model resolves through the catalog
- [ ] End-to-end integration test passes (catalog → routing → health → deployment selection)
- [ ] CI workflow runs `dotnet build` + `dotnet test` on push/PR
- [ ] Catalog configuration is documented
- [ ] Branch cleanly merged to master with tag
