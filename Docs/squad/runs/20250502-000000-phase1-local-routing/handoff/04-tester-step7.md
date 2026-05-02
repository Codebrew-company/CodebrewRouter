# Handoff: Squad Tester — Step 7 (Dual-Ollama Failover Test)
**From:** Squad Conductor
**To:** Squad Tester
**Timestamp:** [now]

## Mission
Execute **Step 7: Add dual-Ollama failover test** verifying that when primary Ollama (.53) is unavailable, the system falls back to secondary (.12).

**Dependency:** Steps 1–6 complete (config, DI, routing, AppHost, heartbeat, verification)

## Files you may create/edit (exclusive lock)
- `Blaze.LlmGateway.Tests/OllamaFailoverTests.cs` (create new)
- Optionally extend `ModelAvailabilityHeartbeatServiceTests.cs` if it exists

## Current Test Status
- 248/251 tests passing
- 3 known isolation issues (pass individually)
- Latest commit: Phase 1 Steps 1–6 complete, build clean

## Test Scope (3 test cases minimum)

### Test 1: Primary Ollama Healthy → Routing Uses Primary
**Setup:**
- Mock OllamaApiClient for primary endpoint (.53:11434) → returns healthy probe
- Heartbeat runs startup probe

**Verification:**
- `ProvidersSnapshot.Ollama.IsHealthy` == true
- Routing layer knows primary endpoint is active
- No fallback attempts logged

### Test 2: Primary Ollama Down → Fallback Healthy → Routing Uses Fallback
**Setup:**
- Mock primary endpoint (.53:11434) → throws timeout/exception on probe
- Mock fallback endpoint (.12:11434) → returns healthy probe
- Heartbeat runs startup probe

**Verification:**
- Fallback probe is attempted (primary failed)
- `ProvidersSnapshot.Ollama.IsHealthy` == true (fallback is healthy)
- Log shows: "[WARNING] Primary Ollama unavailable... Trying fallback"
- Log shows: "[INFO] Fallback Ollama healthy"
- Routing layer knows fallback endpoint is active

### Test 3: Both Ollama Instances Down → Router Unavailable
**Setup:**
- Mock both endpoints (.53 and .12) → both throw timeout/exception
- Heartbeat runs startup probe

**Verification:**
- Both probe attempts fail
- `ProvidersSnapshot.Ollama.IsHealthy` == false
- Log shows: "[WARNING] Both Ollama instances unavailable"
- Requests should fail gracefully (no crash)

## Implementation Guidance

### Use Existing Patterns
1. **Mocking:** Check `Blaze.LlmGateway.Tests/` for existing mock patterns (likely using Moq)
2. **LocalHttpServer fixture:** Look for `LocalHttpServer` or similar for mocking HTTP endpoints
3. **Heartbeat testing:** Find `ModelAvailabilityHeartbeatServiceTests.cs` as a template for probe testing

### Test Structure (xUnit)
```csharp
[Fact]
public async Task ProbeOllamaWithFailover_PrimaryHealthy_UsePrimary()
{
    // Arrange: Mock primary healthy, heartbeat service
    // Act: heartbeat.RunLoopAsync() or RefreshSnapshotAsync()
    // Assert: Primary endpoint marked healthy
}
```

### No Integration Test (Local vs. CI)
- These tests should use **mocked** Ollama endpoints (via LocalHttpServer or Moq)
- **Do NOT** require actual Ollama instances running
- This allows tests to run in CI/CD without local Ollama setup

## Acceptance Criteria (Step 7)
- ✅ **Test 1:** Primary healthy → verified ✓
- ✅ **Test 2:** Primary down, fallback healthy → verified ✓
- ✅ **Test 3:** Both down → verified ✓
- ✅ **Logging:** All three tests verify appropriate log messages
- ✅ **Coverage:** >90% code coverage for new failover logic
- ✅ **Build:** `dotnet test --no-build` passes (all tests including new ones)
- ✅ **Total tests:** 251+ (new tests added, no existing tests removed)

## Emission Requirements
- **[CREATE]**: OllamaFailoverTests.cs file created
- **[EDIT]**: Any existing test files modified (if extending existing test class)
- **[DONE]**: When all 3 test cases pass and build is clean

---
[DONE or BLOCKED]? Please respond with structured-action tags.
