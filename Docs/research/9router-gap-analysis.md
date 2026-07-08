# 9Router vs CodebrewRouter — Feature & UIX Gap Analysis

> Companion document: [`9router-validation.md`](./9router-validation.md) (market validation).
> Reference: [decolua/9router](https://github.com/decolua/9router) (~19.9k ⭐, MIT, Node.js/Next.js).
> Baseline audited: this repository as of 2026-07-05 (branch `claude/9router-validation-research-za563q`).
> Note: the codebase is significantly ahead of `analysis.md` (dated 2026-04-26) — this document reflects the *code*, not the stale audit.

## 1. What 9Router actually is

An OpenAI-compatible proxy that routes AI **coding tools** (Claude Code, Codex, Cursor, Cline, Copilot, …) across 40+ providers with a 3-tier automatic fallback (subscription → cheap → free), aggressive token optimization, and a polished Next.js 16 / React 19 / Tailwind 4 web dashboard. Feature inventory:

| Area | 9Router capability |
|---|---|
| Endpoint | `POST /v1/chat/completions`, `GET /v1/models`, `POST /v1/compress` (optional external compressor) |
| Routing | "Combos" — user-defined ordered model chains (`cc/claude-opus-4-7` → `glm/glm-5.1` → `kr/claude-sonnet-4.5`) with auto-fallback on quota/error |
| Quota | Per-provider/model quota tracking with reset countdowns (5-hour / daily / weekly cycles) |
| Accounts | Multi-account per provider with round-robin load balancing; auto-refreshing OAuth tokens |
| Format translation | OpenAI ↔ Claude ↔ Gemini ↔ Cursor ↔ Kiro ↔ Vertex wire formats |
| Token savers | RTK (compresses tool outputs, −20-40% input), Caveman Mode (terse-output prompts, −65% output), Ponytail (YAGNI-first codegen prompts), Headroom (external compression) |
| Dashboard | Provider OAuth connection mgmt, combo builder, real-time usage stats, API-key generation/mgmt, settings toggles, request-log viewer |
| Persistence | SQLite (better-sqlite3) with automatic backups; cloud sync across devices |
| Security | API keys minted **and enforced** on `/v1` (post-CVE: auth bypass CVE-2026-5842 fixed 0.3.75; critical RCE CVE-2026-46339 fixed 0.4.37) |
| Deploy | localhost, VPS/PM2, Docker (multi-platform), Cloudflare Workers |

## 2. Gap matrix

Legend: ✅ Have (equal or better) · 🟡 Partial (exists but unwired/stubbed/different shape) · ❌ Missing.

| Capability | 9Router | CodebrewRouter | Evidence |
|---|---|---|---|
| OpenAI chat completions + SSE | ✅ | ✅ | `Blaze.LlmGateway.Api/ProgramPartial.cs` (`RegisterLiteLlmEndpoints`) |
| `/v1/models` | ✅ | ✅ (+ `/v1/models/diagnostics`) | same |
| OpenAI **Responses** API | ❌ | ✅ | `ProgramPartial.cs` — full CRUD + cancel/compact/input_tokens |
| OpenAI **Conversations** API | ❌ | ✅ | same |
| **A2A agent protocol** | ❌ | ✅ | agent-card + JSON-RPC + tasks endpoints |
| Ordered fallback chains ("combos") | ✅ UI-built | ✅ config-built | `VirtualModels` + `CodebrewRouter:FallbackRules` in `Blaze.LlmGateway.Api/appsettings.json`; `CodebrewRouterChatClient` first-chunk-probe failover |
| Task-aware routing (classify → route) | ❌ | ✅ | `TaskClassification` (keyword/Ollama), per-task-type fallback rules |
| Routing strategies (round-robin/latency/cost/least-busy) | 🟡 round-robin only | ✅ | `ProviderCatalog:ModelRouting` + `CatalogModelRouter` |
| Health probes / circuit breaker | 🟡 | ✅ | `HealthProbeService`, `CircuitBreakerChatClient` |
| Input token saver | ✅ RTK −20-40% | 🟡 `PromptCleanup` (Gemma rewrite of last user msg) + `ContextCompaction` — narrower than RTK's tool-output compression | `GemmaPromptCleaner`, `PromptCleanupOptions` |
| Output token saver (Caveman/Ponytail) | ✅ | ❌ (system prompts per virtual model exist, but no terse-mode toggle) | `VirtualModels` config |
| On-device local inference | ❌ | ✅ | `Blaze.LlmGateway.LocalInference` — LM-Kit Gemma, warmup, offline mode |
| Agent-fleet routing | ❌ | ✅ | 11 `Hermes_*` profile clients (ports 8642–8652) |
| Provider breadth | ✅ 40+ incl. OAuth subscription providers | 🟡 ~5 families (LmStudio, DerpYardly, 14× OpenCodeGo, Hermes, Ollama, LocalGemma) | `InfrastructureServiceExtensions.cs`, `LocalInference/ServiceCollectionExtensions.cs` |
| OAuth provider mgmt (Claude Code / Copilot / Cursor accounts) | ✅ | ❌ (see §5 risk note — deliberate skip is defensible) | — |
| Quota tracking + reset countdowns | ✅ | ❌ | — |
| Multi-account round-robin per provider | ✅ | ❌ | — |
| Format translation (Claude/Gemini native wire) | ✅ | ❌ OpenAI-compat only | — |
| **API-key enforcement on `/v1`** | ✅ (post-CVE) | ❌ keys are minted but never validated; landing page says "any non-empty API key" | `AdminEndpoint.cs` `/admin/keys`; no `UseAuthentication` anywhere in Api |
| Rate limiting | 🟡 | 🟡 **implemented but unwired** — only instantiated in tests | `Infrastructure/RateLimiting/RateLimitingChatClient.cs`; no runtime registration |
| Usage / spend tracking | ✅ real-time stats + cost estimates | 🟡 **stub** — `Spend()` returns hard-coded zeros | `AdminEndpoint.cs:32-33` |
| Request/route logging | ✅ debug log viewer | ✅ backend only — `[ROUTER-*]` structured logs, persisted route decisions, `/admin/routes/recent`, Prometheus `/metrics` | `RouterLog.cs`, `JsonProtocolStore.cs` |
| Database | ✅ SQLite + backups + cloud sync | 🟡 JSON file store (`App_Data/protocol-store.json`); ADR-0004 (SQLite+EF Core) not implemented | `JsonProtocolStore.cs` |
| **Web dashboard** | ✅ the product's centerpiece | ❌ none for the router (Swagger/Scalar/DevUI are dev tools; `Brew/Brew.App` is a separate assistant chat UI, not a gateway console) | `Brew/Brew.App/*`, `/devui` |
| Docker / edge deploy | ✅ | 🟡 Aspire AppHost for local orchestration; no published container story | `Blaze.LlmGateway.AppHost` |
| Token counting | 🟡 | ✅ Tiktoken + per-model registry (14 OpenCodeGo models, graceful gpt-4o fallback) | `TokenCounting/*` |

**Bottom line:** the routing engine is at or beyond 9Router (task classification, strategy-based catalog routing, health/circuit-breaking, local inference, A2A/Responses APIs are all things 9Router doesn't have). What's missing is the **product wrapper** that made 9Router explode: enforced keys, quotas, real usage numbers, and above all the dashboard UIX.

## 3. UIX gap — what a CodebrewRouter dashboard needs

9Router's dashboard pages → Blazor equivalent (reuse `Brew/Brew.App` stack: Blazor WASM + Syncfusion, or server-side Blazor hosted in the Api):

| 9Router page | Purpose | CodebrewRouter backing surface (already exists) | Net-new work |
|---|---|---|---|
| Providers | connect/disconnect, OAuth flows, account health | `ProviderCatalog` config, `HealthProbeService`, `/v1/models/diagnostics` | UI + provider CRUD API (config is file-only today; catalog has hot-reload via `IOptionsMonitor`) |
| Combo builder | drag-order model chains, save as routable model | `VirtualModels` + `FallbackRules` (config) | UI + write-back endpoint to mutate virtual models at runtime |
| Usage stats | tokens/cost per provider/model, quota countdowns | `/metrics` (Prometheus), token counters, route decisions | Real spend aggregation (replace `Spend()` stub), quota model |
| API keys | mint/revoke, per-key stats | `/admin/keys` CRUD (exists) | **Enforcement middleware first**, then per-key usage attribution |
| Request logs | live log viewer (debug) | `/admin/routes/recent`, `[ROUTER-*]` logs | UI + optional request/response capture toggle |
| Settings | token-saver toggles, ports, sync | `PromptCleanup`, `ContextCompaction`, `OfflineOnly` config | UI + runtime settings endpoint |

## 4. PRD-style roadmap — "what this code set needs"

Priorities assume the goal stated by the owner: *9Router's UIX and features are the target*. Router-product platform first; the JARVIS/agent direction (analysis.md Phases 2–8) layers on top rather than being replaced — its A2A/Responses/local-inference work is precisely the differentiation (§ validation report).

**P0 — Security & correctness (prerequisite for everything public-facing)**
1. Enforce API-key auth on `/v1/*` and `/admin/*`: validate minted `cbr_...` keys from `IProtocolStore` via middleware/endpoint filter. 9Router's CVE cluster is the cautionary tale — CVE-2026-46339 (CVSS 10.0 unauthenticated RCE via 40+ unguarded API routes, fixed 0.4.37) and CVE-2026-5842 (admin-API auth bypass, fixed 0.3.75) — ship enforcement before any dashboard.
2. Wire the existing `RateLimitingChatClient` into the pipeline (per-key and per-provider buckets).

**P1 — Real usage/spend + durable store**
3. Implement spend/usage aggregation: token counts already flow through `TiktokenTokenCounter`; persist per-request usage (key, provider, model, tokens, est. cost) and replace the `AdminEndpoint.Spend` stub.
4. Execute ADR-0004: SQLite + EF Core replacing `JsonProtocolStore` (keys, route decisions, usage, conversations). Enables per-key stats and the dashboard's data needs.

**P2 — Router admin dashboard (the UIX ask)**
5. Blazor dashboard per §3, starting with read-only pages (usage, routes, models, health) then write paths (keys, combo builder, settings). Reuse Brew.App component patterns; host under `/dashboard` in the Api.

**P3 — Quota & fleet ergonomics**
6. Quota tracking with reset countdowns per provider/model; surface in dashboard and in fallback decisions (skip exhausted providers pre-emptively rather than on error).
7. Multi-account/multi-endpoint rotation within a provider family (generalize what Hermes profiles already do).

**P4 — Reach features (validate demand first — see validation report)**
8. Output-side token saver (terse-mode system-prompt injection à la Caveman/Ponytail — trivial given `VirtualModels` system prompts; add a toggle).
9. Anthropic/Gemini native wire-format translation (lets Claude Code point at the gateway natively).
10. Provider breadth expansion via `ProviderCatalog` (config-driven, no code per provider). **Deliberately skip** OAuth subscription-reuse providers (Claude Code/Copilot/Cursor account piggybacking) — ToS risk, see validation report §risks.

**Explicit non-goals kept from `analysis.md` Part 6:** multi-tenancy, billing-grade metering, Kubernetes-scale ops. The dashboard is single-operator.

## 5. Decision the owner should confirm

`analysis.md` (Part 0) deliberately pivoted *away* from "LiteLLM-class gateway" toward a personal JARVIS agent. "9Router is exactly what I wanted — UIX and features and all" points back toward a router **product**. This roadmap assumes **both, router-platform first** (P0–P2 are also prerequisites for a safe JARVIS deployment). If JARVIS-first is still the intent, P0/P1 stand unchanged and P2's dashboard shrinks to a usage/health page.

## 6. Start here next session

`P0.1` — API-key enforcement middleware: single endpoint filter over the `/v1` + `/admin` groups in `ProgramPartial.cs`, validating against `IProtocolStore` keys, with a config kill-switch (`LlmGateway:Auth:Enabled`) defaulting to on when any key exists. Small, testable (`WebApplicationFactory` pattern already in use), and unblocks everything above it.
