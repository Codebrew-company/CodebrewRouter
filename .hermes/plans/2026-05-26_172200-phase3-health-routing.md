# Provider Catalog Routing — Phase 3: Health Probes & Circuit Breaker

> **Plan mode — no implementation in this turn.**

**Goal:** Add background health probing and a circuit-breaker middleware that feeds failure/success signals back into `IProviderCatalog`, making routing decisions health-aware in real time.

**Current context:** Phase 2 is merged. `IProviderCatalog` already has `ReportHealth(name, bool)` and `IsHealthy(name)`. `InMemoryProviderCatalog` tracks consecutive failures (3 → unhealthy). `HealthAwareRoutingFilter` already skips unhealthy deployments. `CatalogModelRouter` already falls back when all primaries are unhealthy. What's missing is:

1. A **background service** that actually probes deployments and calls `ReportHealth`
2. A **circuit-breaker middleware** on each `IChatClient` that catches runtime failures and reports them back
3. **Tests** for both

**Branch:** `feature/provider-catalog-routing`
**Base:** `master` (current head includes Phase 2)

---

## Task 3.1: CircuitBreakerChatClient middleware

**Objective:** MEAI `DelegatingChatClient` that checks health before forwarding, catches exceptions to report failures, and reports success on 200.

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/Catalog/CircuitBreakerChatClient.cs`
- Test: `Blaze.LlmGateway.Tests/Catalog/CircuitBreakerChatClientTests.cs`

### Design

```csharp
public sealed class CircuitBreakerChatClient(
    IProviderCatalog catalog,
    string deploymentName,
    ILogger<CircuitBreakerChatClient> logger) : DelegatingChatClient
{
    public override Task<ChatResponse> CompleteAsync(...)
    {
        if (!catalog.IsHealthy(deploymentName))
            throw new InvalidOperationException($"Deployment '{deploymentName}' is unhealthy.");

        try {
            var response = await base.CompleteAsync(...);
            catalog.ReportHealth(deploymentName, true);
            return response;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            catalog.ReportHealth(deploymentName, false);
            throw;
        }
    }

    // Same pattern for CompleteStreamingAsync — but streaming needs
    // per-chunk error handling since a stream can fail mid-response.
}
```

**Key design decisions:**
- Short-circuit: throw immediately when unhealthy — the catalog router will try another deployment or fallback
- Report success on every 200 (resets the failure counter)
- Report failure on any non-cancellation exception
- Streaming variant wraps the `IAsyncEnumerable` in a try/catch so mid-stream failures still report health

**Tests (8):**

| Test | Assertion |
|------|-----------|
| `CompleteAsync_WhenHealthy_ForwardsCall` | Calls inner client, reports healthy |
| `CompleteAsync_WhenUnhealthy_Throws` | Throws without calling inner |
| `CompleteAsync_OnSuccess_ReportsHealthy` | `ReportHealth(name, true)` called |
| `CompleteAsync_OnException_ReportsUnhealthy` | `ReportHealth(name, false)` called |
| `CompleteStreamingAsync_WhenHealthy_StreamsChunks` | Yields chunks from inner |
| `CompleteStreamingAsync_WhenUnhealthy_Throws` | Throws without streaming |
| `CompleteStreamingAsync_MidStreamError_ReportsUnhealthy` | Exception during enumeration reports unhealthy |
| `Constructor_ThrowsOnNullCatalog` | Null guard |

---

## Task 3.2: HealthProbeService

**Objective:** `BackgroundService` that periodically probes every catalog deployment with a lightweight ping and reports health.

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/Catalog/HealthProbeService.cs`
- Test: `Blaze.LlmGateway.Tests/Catalog/HealthProbeServiceTests.cs`

### Design

```csharp
public sealed class HealthProbeService(
    IProviderCatalog catalog,
    IServiceScopeFactory scopeFactory,
    IOptions<ProviderCatalogOptions> options,
    ILogger<HealthProbeService> logger) : BackgroundService
```

**Probe strategy:**
- On startup, wait `HealthCheckIntervalSeconds` (configurable, default 30s)
- Iterate all deployments from `catalog.GetAllDeployments()`
- For each, resolve an `IChatClient` via `IServiceScopeFactory` and send `"ping"` with `MaxOutputTokens = 1`
- 10-second per-deployment timeout
- Success → `catalog.ReportHealth(name, true)`
- Timeout/exception → `catalog.ReportHealth(name, false)`
- After all probes, wait `HealthCheckIntervalSeconds` and repeat

**Health resolution:**
- The service creates a scope and resolves `IProviderCatalog` from it (to get a fresh catalog if it was replaced)
- For each deployment, resolves a keyed `IChatClient` for the deployment's `Provider`
- Sends a minimal probe: `new ChatMessage(ChatRole.User, "ping")` with `MaxOutputTokens = 1`

**Edge cases:**
- Deployment removed from catalog between ticks → skip gracefully
- No keyed client registered for provider → `ReportHealth(name, false)`
- Cancellation token during probe → skip that deployment
- All deployments fail → log warning, keep trying next tick

**Tests (6):**

| Test | Assertion |
|------|-----------|
| `ProbeAsync_HealthyDeployment_ReportsHealthy` | `ReportHealth(name, true)` called |
| `ProbeAsync_UnhealthyDeployment_ReportsUnhealthy` | Timeout/exception → `ReportHealth(name, false)` |
| `ProbeAsync_SkipsMissingDeployments` | No crash when deployment removed mid-cycle |
| `ProbeAsync_MissingKeyedClient_ReportsUnhealthy` | No IChatClient registered → unhealthy |
| `ProbeAsync_RespectsCancellationToken` | Stops probing when cancelled |
| `Constructor_ThrowsOnNullDependencies` | Null guards |

---

## Task 3.3: Wire into DI

**Objective:** Register `HealthProbeService` as a hosted service and add a helper to wrap keyed `IChatClient` registrations with `CircuitBreakerChatClient`.

**Files:**
- Modify: `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs`
- Read: existing keyed client registrations to know where to wrap

### Approach

Option A — Wrap each keyed registration individually:
```csharp
services.AddKeyedSingleton<IChatClient>("AzureFoundry", (sp, _) =>
{
    var inner = CreateAzureFoundryClient(sp);
    var catalog = sp.GetRequiredService<IProviderCatalog>();
    var logger = sp.GetRequiredService<ILogger<CircuitBreakerChatClient>>();
    return inner.AsBuilder()
        .Use((next, _) => new CircuitBreakerChatClient(catalog, "AzureFoundry", logger) { InnerClient = next })
        .Build();
});
```

Option B — Add a post-configuration step that wraps all keyed clients. This is cleaner but more complex. **Recommend Option A** for clarity — wrap the catalog-routed providers (AzureFoundry, OpenCodeGo, Ollama, etc.).

**Wrapping target:** Any keyed client that could be selected by the catalog. Currently: `LmStudio`, `CodebrewRouter`, `OllamaLocal` (commented out), and any future catalog-registered providers.

**Registration:**
```csharp
services.AddHostedService<HealthProbeService>();
```

---

## Task 3.4: Integration test

**Objective:** Verify the full chain — probe marks unhealthy → catalog router selects fallback → circuit breaker short-circuits unhealthy.

**Files:**
- Create: `Blaze.LlmGateway.Tests/Catalog/HealthRoutingIntegrationTests.cs`

**Test (1):**
```csharp
[Fact]
public async Task FullChain_UnhealthyDeployment_RoutesToFallback()
{
    // Arrange: create catalog with 2 deployments for same model, both healthy
    // Act: report dep-A unhealthy 3x, then resolve a request
    // Assert: dep-B (fallback) is selected
}
```

---

## Verification

```bash
cd /mnt/data/src/CodebrewRouter
dotnet build --no-restore 2>&1 | tail -5
dotnet test --no-build 2>&1 | tail -5
```

Expected: build clean, 525+ passing (511 existing Phase 1+2 + 14 new Phase 3).

```bash
git add -A
git commit -m "feat: health probes, circuit breaker, fallback chain integration"
```

---

## Risks & tradeoffs

1. **Probe cost**: Sending `"ping"` to every deployment every 30s incurs token cost. Tradeoff: acceptable for <20 deployments; for larger catalogs, consider active/passive probes.
2. **Streaming circuit breaker**: Mid-stream failures are hard to handle because the HTTP response has already started. The streaming variant catches exceptions during enumeration and reports unhealthy, but the partial response is already delivered.
3. **Startup race**: HealthProbeService waits 30s before first probe, so all deployments start "healthy" (optimistic). The circuit breaker catches real failures faster than the probe.
