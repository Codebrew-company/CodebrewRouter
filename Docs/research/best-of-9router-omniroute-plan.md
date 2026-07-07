# Best-of 9router + OmniRoute → CodebrewRouter — Feature Plan

> Planning document (not yet implemented). Companion to [`omniroute-comparison.md`](./omniroute-comparison.md), [`9router-parity-plan.md`](./9router-parity-plan.md), [`9router-validation.md`](./9router-validation.md), [`9router-gap-analysis.md`](./9router-gap-analysis.md).
> Status: P0–P5 parity already shipped. This plan covers the six genuinely-absent "best of both" features surfaced by a 2026-07-07 code audit.

## Context

The strongest features of 9router and OmniRoute, folded into CodebrewRouter (Blaze.LlmGateway), reusing existing infrastructure — no rewrites, no new heavy dependencies. Deep research is already committed; this plan does **not** re-research (token-sensitive). LLMLingua-style ML compression is deliberately **skipped** (marginal over existing heuristics, ML-heavy to build and run).

A code audit found these already implemented (do not rebuild): Fusion/MoA (`FusionChatClient`), model-lock quota + credential pools, output savers (Caveman/Ponytail), RTK tool-output compression, Anthropic `/v1/messages` + count_tokens, config-driven + subscription providers, usage ledger + SQLite store, dashboard, API-key auth. Baseline gate: 678 tests green.

## Features

Legend: reuse-heavy, each a self-contained tested/committed increment behind a default-off/opt-in flag where it changes runtime behavior.

### F1 — Adaptive `auto` routing strategy *(OmniRoute's headline router)*
A 6th catalog strategy scoring each candidate `ProviderDeployment` across live signals instead of one dimension.
- New `AutoRoutingStrategy : IRoutingStrategy` (`Name="auto"`). Reuse `HealthAwareRoutingFilter.Filter` for eligibility, then score = weighted sum of **health** (`IProviderCatalog`), **latency** (shared measurement store — extract the one `LatencyStrategy` uses), **cost** (`ProviderDeployment.CostPerToken`), **quota/lock** (`Quota/ModelLockRegistry` — locked ⇒ skip), **load** (`least_busy` in-flight counter — extract shared). Weights from `ProviderCatalog:AutoWeights` (sane defaults).
- Register `"auto"` in `RoutingStrategyResolver.CreateStrategy` (currently throws on unknown). Expose per-candidate score breakdown for F2.
- Files: new `RoutingStrategies/Catalog/AutoRoutingStrategy.cs`; share latency/load stores; `RoutingStrategyResolver.cs`; `ProviderCatalogOptions.cs`.
- Accept: locked/unhealthy excluded; highest composite score wins for fixed signals; deterministic fallback with no signal data.

### F2 — Structured route-decision explainability *(the "beats 9router" surface)*
Turn free-text `RouteDecision.Reason` into structured "why this provider won".
- Extend `RouteDecision` (`ProtocolModels.cs`) with nullable `TaskType`, `Strategy`, `ChosenProvider`, `CandidateScores` (name→score+breakdown), `Health`, `LatencyMs`, `QuotaState` (back-compat).
- Capture F1's breakdown at selection via a scoped `IRouteExplanationSink`, read where `RouteDecision` is written (`ResponsesEndpoint.cs`, `A2AEndpoint.cs`, chat/router path). Add `GET /admin/routes/{id}`.
- Dashboard **Routes** tab shows task class, strategy, chosen provider, candidate-score mini-table.
- Files: `ProtocolModels.cs`, `SqliteProtocolStore.cs`/`JsonProtocolStore.cs`, route write sites, `AdminEndpoint.cs`, `Dashboard/DashboardEndpoint.cs`.
- Accept: routed request → stored explanation names chosen provider + candidate scores; dashboard renders it.

### F3 — API-key scope enforcement *(OmniRoute per-key scopes — currently stored but unenforced)*
Enforce the `AdminApiKey.Scopes` already persisted (`chat`/`responses`/`a2a`/`admin`).
- In `Api/Auth/ApiKeyAuthentication.cs`, after validation, map path → required scope (`/v1/chat*`→`chat`, `/v1/responses*`→`responses`, `/v1/messages*`→`chat`, `/a2a*`→`a2a`, `/admin*`→`admin`); 403 when missing. `admin` implies all. Apply the guard to `/admin` too, not just `/v1`.
- Files: `ApiKeyAuthentication.cs`, `AdminEndpoint.cs` (mint accepts scopes; never returns full key after mint — verify).
- Accept: `chat`-only key → 200 on `/v1/chat/completions`, 403 on `/v1/responses` + `/admin/*`; `admin` key → all 200. `WebApplicationFactory` tests.

### F4 — Multi-window quota accounting *(9router + OmniRoute edge)*
Rolling 5h / daily / weekly / per-model windows atop the existing exponential model-lock, fed by the P1 usage ledger.
- New `Quota/QuotaWindowTracker` reading `IProtocolStore` usage per provider/model over configured windows (`ProviderCatalog` deployment gains `Quota: { RequestsPer5h, RequestsPerDay, TokensPerDay, … }`). Pre-emptive skip in `CodebrewRouterChatClient` fallback + catalog selection when exhausted (composes with `ModelLockRegistry`).
- Reset countdowns on dashboard **Quota** tab + `/admin/quotas`.
- Files: new `Quota/QuotaWindowTracker.cs`, `ProviderCatalogOptions.cs`, fallback/selection hooks, `AdminEndpoint.cs`, `Dashboard/DashboardEndpoint.cs`.
- Accept: 5h cap consumed → provider pre-emptively skipped; countdown visible; window rolls off on schedule (TimeProvider test).

### F5 — MCP **server** surface *(OmniRoute's 95-tool headline; doubles as JARVIS Phase 3)*
Expose CodebrewRouter's own operations as MCP tools (today it is MCP *client* only via `McpConnectionManager`).
- Use the already-referenced `ModelContextProtocol` v1.3.0 server side. Map an MCP server (stdio + HTTP/SSE) at `/mcp`, behind admin scope (F3). Focused tool set (not 95): `list_models`, `get_usage_summary`, `get_recent_routes`, `explain_route`, `list_providers_health`, `mint_api_key` (admin), `set_quota`. Tools call existing services — no new business logic.
- Files: new `Api/Mcp/GatewayMcpServer.cs` + tool defs, `Program.cs` (map + gate), config `LlmGateway:McpServer:Enabled` (default off).
- Accept: MCP client (or unit test over handlers) lists tools and invokes `list_models`/`get_usage_summary` with live data; 401/403 without admin scope.

### F6 — Gemini-native endpoint *(format-translation parity; mirrors Anthropic `/v1/messages`)*
Gemini CLI/SDK clients connect with no shim.
- `POST /v1beta/models/{model}:generateContent` and `:streamGenerateContent` translating Gemini wire (`contents[].parts[]`, `systemInstruction`, `generationConfig`, SSE) ↔ MEAI, mirroring `AnthropicMessagesEndpoint.cs`. Records usage (`endpoint = "gemini.generateContent"`).
- Files: new `Api/GeminiMessagesEndpoint.cs` + `GeminiModels.cs`, wire in `ProgramPartial.cs`, reuse `ChatCompletionsEndpoint.ResolveClientAsync` + `UsageTracking`.
- Accept: `generateContent` routes through the pipeline, returns Gemini shape; streaming emits Gemini SSE; usage recorded. Unit + `WebApplicationFactory` tests.

## Token-sensitivity measures
- No further deep-research; rely on committed comparison docs.
- **Skip** LLMLingua/semantic prompt compression (ML-heavy, marginal over `ToolOutputCompressor` + `GemmaPromptCleaner` + `ContextCompaction`).
- Reuse routing filter, catalog signals, model-lock, usage ledger, `ResolveClientAsync`, `UsageTracking`, existing dashboard shell. No new heavy NuGet deps (MCP server uses the already-referenced package).
- Each feature is default-off/opt-in where it changes runtime behavior, so the shipped P0–P5 stays stable.

## Sequencing
F3 (scopes — small, security) → F1 (auto router) → F2 (explainability, builds on F1) → F4 (multi-window quota) → F6 (Gemini, isolated) → F5 (MCP server, largest, isolated). Build + test green after each.

## Verification
- Per feature: `dotnet build --no-restore` then `dotnet test --no-build` — stay green (baseline 678); new unit + `WebApplicationFactory` tests per Accept.
- E2E smoke (providers optional): mint `admin` + `chat`-only keys → scope 403s (F3); `GET /admin/routes/{id}` structured explanation (F2); `/admin/quotas` window countdowns (F4); `POST /v1beta/models/auto:generateContent` Gemini shape (F6); MCP client lists tools at `/mcp` (F5).
- CI-style gate (no `-warnaserror`; sandbox NU1603 is environmental).

## Out of scope / non-goals (reaffirmed)
Provider-count race, 17-strategy race, LLMLingua ML compression, OAuth-subscription piggybacking beyond the gated P5b, MITM / TLS-fingerprint / detection evasion (permanent non-goal), multi-tenancy / billing-grade metering, 42-locale i18n, Electron/Termux/PWA packaging.
