# Handoff: Coder Step 4 (Heartbeat Dual-Ollama Probe)
**From:** Squad Conductor
**To:** Squad Coder
**Timestamp:** 2026-05-02

## Mission
Execute **Step 4: Update heartbeat to probe both Ollama instances** (primary @ 192.168.16.53:11434, fallback @ 192.168.16.12:11434).

**Dependency:** Phase A + B complete (DI removal, AppHost cleanup)

## Files you may edit (exclusive lock)
- `Blaze.LlmGateway.Api/ModelAvailabilityHeartbeatService.cs` (only file)

## Files other parallel tasks own
- Config/DI/Routing: ✅ Complete (Phase A)
- AppHost: ✅ Complete (Phase B)
- Tests: Reserved for Squad Tester (Step 7)

## Inherited assumptions
- Phase A (Steps 1–3) complete; appsettings.json updated with OllamaLocal config
- OllamaLocal registration still present in DI (only cloud providers removed)
- AppHost cleanup complete; no cloud resource wirings
- Default Ollama probe timeout: 5 seconds (or existing timeout in codebase)

## Current State
- `ModelAvailabilityHeartbeatService.cs` currently probes **single Ollama endpoint** (OllamaLocal)
- Probe logic in `RefreshSnapshotAsync()` calls `ProbeChatProviderAsync()` once
- No failover or dual-instance awareness

## Target State
- Probe **two Ollama endpoints:**
  - Primary: 192.168.16.53:11434
  - Fallback: 192.168.16.12:11434
- Heartbeat logic:
  - Try primary first (5 sec timeout)
  - If primary succeeds → mark router available, use primary endpoint for routing
  - If primary fails → immediately try fallback (5 sec timeout)
  - If fallback succeeds → mark router available, use fallback endpoint for routing
  - If both fail → mark router unavailable
- Log messages clearly indicate which Ollama instance is healthy/unhealthy

## Implementation Strategy

### Refactor Approach
1. **Identify current probe method:** Find `ProbeChatProviderAsync()` calls for OllamaLocal
2. **Add dual-probe logic:**
   - Create method `ProbeOllamaWithFailover()` or similar
   - Accepts primary URL (192.168.16.53:11434) and fallback URL (192.168.16.12:11434)
   - Probes primary first; if timeout/exception, probes fallback
   - Returns tuple: (isHealthy: bool, activeEndpoint: string)
3. **Update RefreshSnapshotAsync:**
   - Call new dual-probe method instead of single probe
   - Update `ProvidersSnapshot` to track which Ollama endpoint is active
4. **Logging:**
   - Log "Probing primary Ollama @ 192.168.16.53:11434"
   - If success: "Primary Ollama healthy"
   - If fail: "Primary Ollama unavailable, trying fallback @ 192.168.16.12:11434"
   - If fallback success: "Fallback Ollama healthy"
   - If both fail: "Both Ollama instances unavailable"

### Configuration Hints
- Primary endpoint: likely from `appsettings.json` → `OllamaLocal` config → BaseUrl (should be updated to .53 by Phase A)
- Fallback endpoint: likely hardcoded or from config as secondary URL
- Timeout: check existing code for default probe timeout (search `timeoutMs`, `timeout`, `TimeSpan`)

### No Breaking Changes
- Existing `ProbeChatProviderAsync()` signature should not change (may be called by other code)
- `ProvidersSnapshot` structure may need to track fallback logic, but contracts with external callers (DI, routing) remain the same

## Acceptance Criteria (Step 4)
- ✅ **Dual probe logic:** Heartbeat probes both Ollama endpoints (.53 primary, .12 fallback)
- ✅ **Failover logic:** If primary times out, immediately tries fallback
- ✅ **Logging:** Clear log messages distinguish primary vs. fallback probe status
- ✅ **Startup:** On `RunLoopAsync()` startup, logs show both endpoints being checked
- ✅ **Build:** `dotnet build --no-incremental -warnaserror` succeeds
- ✅ **No breaking changes:** Existing signatures preserved; only internal logic updated

## Emission Requirements
- **[EDIT]**: ModelAvailabilityHeartbeatService.cs
- **[DONE]**: When Step 4 completes and build succeeds

---
[DONE or BLOCKED]? Please respond with structured-action tags.
