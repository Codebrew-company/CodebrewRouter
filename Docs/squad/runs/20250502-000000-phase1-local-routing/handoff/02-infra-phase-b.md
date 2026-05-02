# Handoff: Squad Infra Phase B (Step 5 — AppHost Cleanup)
**From:** Squad Conductor
**To:** Squad Infra
**Timestamp:** 2026-05-02T11:18:48Z
**Dependencies:** Phase A (Steps 1–3) should be complete before Infra starts Step 5, but file-lock allows parallel execution

## Mission
Execute Step 5: Remove cloud provider resources from Aspire AppHost.

**Phase B is parallel-eligible** with Phase A (Coder) because AppHost changes do not depend on config/DI edits. However, both should complete before Step 4 (heartbeat). Recommend executing in parallel for speed.

## Files you may edit (exclusive lock)
- `Blaze.LlmGateway.AppHost/AppHostComposition.cs` (primary AppHost resource definitions)
- `Blaze.LlmGateway.AppHost/Program.cs` (if resource wiring is present; read-only likely)

## Files other parallel tasks own
- Config: `Blaze.LlmGateway.Api/appsettings.json` (Coder)
- DI: `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs` (Coder)
- Routing: `CodebrewRouterChatClient.cs`, `LlmRoutingChatClient.cs` (Coder)
- Tests: Reserved for Squad Tester (Step 7)

## Inherited assumptions
- Phase A (Steps 1–3) executed by Coder in parallel; results committed or staged
- Latest commit: 272975a (or Phase A commit if available)
- Aspire uses Aspire.Hosting APIs for resource definitions
- Current AppHost has cloud resources: Azure Foundry endpoint + params, GitHub Models API key, optional Foundry Local

## Pending decisions
- **Foundry Local resource:** Disable or leave skeleton for future? **Decision: Leave skeleton but not wired** (optional for Phase 2 exploration, but not used in Phase 1)

## Step 5 Acceptance Criteria
- **Removed parameters:**
  - `AddParameter("azure-foundry-endpoint", ...)` or similar (search AppHostComposition for "azure" or "foundry" params)
  - `AddParameter("github-models-key", ...)` or similar
  - Azure Foundry Responses endpoint parameter
- **Removed resource definitions:**
  - Azure Foundry resource block (e.g., `var azureFoundry = builder.AddResource(...)`)
  - GitHub Models resource block
- **Kept resources:**
  - OllamaLocal (primary @ 192.168.16.53:11434, labeled e.g. "ollama-primary" or "ollama-local")
  - OllamaLocal fallback (@ 192.168.16.12:11434, labeled e.g. "ollama-fallback" or "ollama-local-secondary")
  - LmStudio (@ 192.168.16.56:1234, labeled e.g. "lm-studio")
- **Foundry Local (optional):**
  - If present, disable (comment out or leave as skeleton not wired to builder)
  - Keep resource definition for future use, but do NOT wire to DI in this phase
- **Build result:**
  - `dotnet build --no-incremental -warnaserror` succeeds
  - `dotnet run --project Blaze.LlmGateway.AppHost` starts without secret/parameter errors
  - Aspire dashboard (manual verification later) shows exactly 3 resources as Running

## Discarded context
None; design spec available for reference (Docs/superpowers/specs/2026-05-02-local-byok-roadmap-design.md).

---
[DONE or EDIT]? Please respond with structured-action tags.
