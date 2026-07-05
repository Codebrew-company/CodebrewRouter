# CodebrewRouter → 9Router Parity Plan

> Research date: 2026-07-05. Sources: full clone + crawl of [decolua/9router](https://github.com/decolua/9router) v0.5.18 (19,993 ⭐ / 3,246 forks, MIT, live GitHub API), all 9 GitHub security advisories, release history v0.2.89→v0.5.18, top issues, gitbook docs; plus a read-only audit of this repo at master (2026-07-05).
> Companions: `Docs/research/9router-validation.md` (market validation), `Docs/research/9router-gap-analysis.md` (first-pass gap matrix) — both on branch `claude/9router-validation-research-za563q`.
> **This document is the execution-ready work plan: what's left so CodebrewRouter works like 9Router.**

---

## Part 1 — What 9Router provides (complete inventory)

### 1.1 Product shape

One npm package (`npm install -g 9router`, Node 20+). Single process on port **20128** serving three things: the OpenAI/Anthropic-compatible API (`/v1`), a Next.js 16 + React 19 + Tailwind 4 dashboard, and 50+ internal `/api/*` routes backing the dashboard. Data lives in `~/.9router` (SQLite, WAL mode, auto-backups). Default login password `123456` (their repeated security mistake — see §1.7). Deploy modes: localhost (auto-opens browser, tray icon), Docker (multi-platform, Docker Hub + GHCR), VPS/PM2, reverse proxy, Cloudflare Tunnel/Tailscale.

### 1.2 Feature inventory

| Feature | How it actually works (from source) |
|---|---|
| **Model addressing** | `provider/model` strings (`cc/claude-opus-4-5`, `glm/glm-4.7`) or a combo name used directly as the `model` field |
| **Combos** | User-defined ordered model chains stored in DB. Four strategies per combo: `fallback` (sequential), `round-robin` (sticky: N consecutive requests per model then rotate), `fusion` (fan out to all models in parallel, judge model synthesizes; anonymized sources, quorum vote), `capacity` (reorders per request — image/PDF requests routed to capable models first) |
| **Fallback triggers** | Rule table in `accountFallback.js`: 429/quota/capacity/overloaded → exponential backoff (2s·2^level, 5-min cap per step, 30-min max cooldown); 401/403 → OAuth refresh + retry once, then 2-min cooldown; 402 → cooldown; text-match rules ("rate limit", "quota exceeded") checked before status codes |
| **Cooldown state** | Per-model locks as flat fields on the connection record (`modelLock_${model}` = expiry timestamp; `modelLock___all` = whole account). Checked at account-selection time — exhausted providers skipped *pre-emptively*, not on error |
| **Quota tracking** | Per-provider usage handlers (registry pattern) fetch real quota from each provider's usage API; reset-cycle metadata per provider (Claude 5-h rolling + weekly, Gemini daily+monthly, GLM daily at fixed Beijing hour, Copilot monthly). Dashboard shows progress bars + reset countdowns. Feeds routing: quota-exhausted account marked unavailable until reset |
| **Multi-account** | Account pool per provider. Selection strategy: `fill-first` (priority order, default) or sticky `round-robin` (least-recently-used with per-account consecutive-use counter). Per-provider strategy override |
| **OAuth token refresh** | Per-provider refresh handlers; proactive refresh 5 min before expiry + reactive on 401/403 with 3 retries; tokens persisted on connection record |
| **Format translation** | Registry of `from:to` translator functions. Direct route when registered (lossless), otherwise **pivot through OpenAI** (`source→openai→target`). Formats: OpenAI, OpenAI-Responses, Claude, Gemini, Gemini-CLI, Antigravity, Kiro, Cursor. Concerns handled: tool-call ID repair, thinking/reasoning normalization per provider, streaming delta translation |
| **RTK token saver** (input, −20-40%) | Compresses `tool_result` content only. 11 heuristic filters: gitDiff, gitStatus, grep, find, ls, tree, dedupLog, smartTruncate, readNumbered, buildOutput, search-list. Auto-detects filter from first 1KB. **Fail-open**: if filter errors or output grows, original kept. Runs before format translation so it works for every provider. Logs per-request savings |
| **Caveman** (output, −65%) | Terse-output system prompt appended per request, levels Lite/Full/Ultra. Format-aware injection (OpenAI `system` vs Claude system block vs Gemini instruction) |
| **Ponytail** (output) | YAGNI/lazy-senior-dev codegen system prompt, levels Lite/Full/Ultra. Stacks with Caveman + RTK |
| **Headroom** (input, external) | Optional sidecar (`pip install headroom-ai[proxy]`); 9router POSTs messages to `{url}/v1/compress`, 3-s timeout, fail-open. Dashboard: install detection, status probe, one-click start/stop |
| **API keys** | `9r_` prefix; `requireApiKey` setting toggles enforcement on `/v1`; accepted via `Authorization: Bearer`, `x-api-key`, `x-goog-api-key`, or `?key=` |
| **Usage/spend** | Every request persisted to `usageHistory` (timestamp, provider, model, connectionId, apiKey, promptTokens, completionTokens, cachedTokens, cost, status, meta) + `usageDaily` aggregates. Pricing tables per provider/model in the provider registry. Dashboard shows spend vs "what ChatGPT API would have cost" |
| **Provider registry** | 97 config-driven provider definition files: `{id, name, baseUrl, models[] (static or async fetch), transport{format, auth}, quirks, pricing, capabilities{vision, pdf, audio, reasoning}}`. New provider = new config file, no engine code |
| **Persistence** | SQLite (better-sqlite3 → bun:sqlite → node:sqlite → sql.js WASM fallback chain). 11 tables: `_meta, settings, providerConnections, providerNodes, proxyPools, apiKeys, combos, kv, usageHistory, usageDaily, requestDetails`. WAL, 64MB cache, busy_timeout 5s. DB export/import from dashboard |
| **Cloud sync** | Optional; POSTs providers/combos/keys to cloud URL keyed by machine ID; periodic scheduler; fail-open |
| **MITM proxy** | Intercepts IDE tools that can't change base URL (Copilot, Antigravity, Kiro, Cursor) via local root CA + per-domain certs, remaps model names. **ToS-evasion adjacent — we deliberately skip this** (also "tool cloaking": renaming tools to evade provider detection on OAuth tokens — skip) |
| **Extras** | Proxy pools (+ one-click deploy of relay workers to Vercel/Cloudflare/Deno), web search/fetch endpoints, embeddings, TTS/STT/image-gen routing, translator debug tool, i18n (30+ languages), skills page |

### 1.3 The `/v1` surface

`POST /v1/chat/completions`, `POST /v1/messages` (**Anthropic-native — this is how Claude Code connects without translation hacks**), `/v1/embeddings`, `/v1/images/generations`, `/v1/audio/{speech,transcriptions,voices}`, `/v1/web/{fetch,search}`, `/v1/models`, `/v1/messages/count_tokens`, `/v1/responses/*`.

### 1.4 CLI-tool onboarding (the growth engine)

Every tool gets a copy-paste recipe (gitbook + dashboard CLI-Tools page writes configs for you): Claude Code (`ANTHROPIC_BASE_URL=http://localhost:20128/v1` + per-tier model env vars), Codex (`OPENAI_BASE_URL`), Cursor (cloud URL required), Cline/Continue/Roo/Kilo (OpenAI-compatible provider), plus 17 tools total with per-tool status detection in the dashboard.

### 1.5 Dashboard (17 pages — the product's centerpiece)

| Page | Purpose | Key elements |
|---|---|---|
| Home/Endpoint | Connection info | endpoint URL, machine ID, API key, health, token-saver toggles (RTK/Headroom/Caveman/Ponytail with level selectors), cloud sync |
| Providers | Connection mgmt | responsive card grid grouped Custom/OAuth/Free/API-key; status badges (Connected: N, Error: N); enable toggles; Test All batch probe with latency + diagnosis modal |
| Provider detail | Add/edit connection | OAuth flow buttons, API key input, per-connection proxy, test |
| Combos | Chain builder | combo cards; **drag-drop model reordering** (dnd-kit); strategy selector (fallback/round-robin/fusion/capacity); capacity badges on models |
| Usage | Analytics | tabs Overview/Logs/Details; recharts time-series (requests, tokens, cost) with period selector (today/24h/7D/30D/60D); request log table; full request inspector |
| Quota | Limits | per-provider usage bars, remaining quota, reset countdowns |
| Keys (in settings) | API keys | mint/revoke, copy, per-key stats |
| CLI Tools (+detail) | Integrations | 17 tool cards with connected/error/not-configured status; per-tool config writer |
| Proxy Pools | Relay mgmt | health checks, batch import, deploy-to-edge buttons |
| Token Saver | Compression lab | before/after comparison, token counts |
| Translator | Debug tool | 7-step accordion (client req → source → OpenAI pivot → target req → provider resp → OpenAI resp → client resp), Monaco read-only editors |
| Basic Chat | Endpoint tester | model dropdown, SSE chat |
| Console Log | Live server log | SSE stream, filter, auto-scroll |
| Profile | Settings | theme, language, password change, OIDC config, DB export/import, outbound proxy, shutdown |
| MITM / Media Providers / Skills | niche | (skip / later) |

Landing page: animated hero, flow animation, features, CTA → dashboard.

### 1.6 Release cadence signal

~60 releases Feb→Jul 2026. Feature order they shipped: usage dashboard (0.2.89) → real-time usage + custom models (0.3.17) → i18n + Copilot MITM (0.3.34) → per-provider round-robin (0.3.42) → RTK (0.3.98) → Caveman (0.4.11) → sticky round-robin (0.4.12) → SQLite migration (0.4.25) → OIDC login (0.4.31) → security wave post-CVE (0.4.37–0.4.80) → Combo Fusion + capacity auto-switch (0.5.2) → Ponytail + Headroom (0.5.6) → token-saver dashboard (0.5.12) → cached-token tracking (0.5.18). Token savers and the dashboard drove the star growth; security was retrofitted under fire.

### 1.7 Security history — 9 advisories (the anti-pattern catalog)

| Advisory | CVSS | Root cause | Lesson for CodebrewRouter |
|---|---|---|---|
| CVE-2026-46339 | 10.0 | Auth middleware guarded an **allowlist of 8 routes**; 40+ `/api/cli-tools/*`, `/api/mcp/*` routes unauthenticated → RCE via spawn | **Default-deny.** Auth is opt-OUT per route, never opt-in |
| GHSA-g6g7 (RCE) | crit | `/api/tunnel/tailscale-install` missing from middleware matcher; piped `sudoPassword` into `sudo -S sh` | Never let route registration and auth registration drift; no shell-out with request data |
| CVE-2026-49352 | 9.8 | Hardcoded fallback JWT secret `"9router-default-secret-change-me"` when env unset | No secret fallbacks. Fail startup or generate + persist random |
| GHSA-4922 (RCE chain) | 9.9 | Default password `123456` + Host-header "local-only" bypass + unvalidated `spawn()` args | No default credentials; never trust Host/Origin for access decisions |
| CVE-2026-49353 | 7.5 | **Incomplete fix** of 46339: "local-only" gate read Host/Origin headers instead of TCP source address | Locality = socket address, not headers (their eventual fix: custom-server.js strips client X-Forwarded-For, injects socket-derived real IP) |
| CVE-2026-55500 | 9.9 | DB export/import endpoint returned all credentials/tokens with only session auth | Credential-bearing exports need re-auth + encryption |
| CVE-2026-55501 | 7.3 | Login rate limiter keyed on spoofable `X-Forwarded-For` | Rate-limit keys from socket IP |
| GHSA-vjc7 | 10.0 | Unauthenticated CRUD on `/api/providers`; `/api/usage/stats` leaked plaintext API keys | Redact secrets in every read API |
| GHSA-qj3v (SSRF) | 7.7 | `/v1/web/fetch` fetched arbitrary URLs incl. metadata endpoints | URL allow/deny validation on any fetch feature |

---

## Part 2 — CodebrewRouter current state (audited 2026-07-05, master)

Routing engine is **ahead** of 9router; product wrapper is behind.

| Capability | 9Router | CodebrewRouter | Evidence |
|---|---|---|---|
| OpenAI chat completions + SSE | ✅ | ✅ | `ProgramPartial.cs:25` |
| `/v1/models` + diagnostics | ✅ | ✅ | `ProgramPartial.cs:64-86` |
| Anthropic-native `/v1/messages` | ✅ | ❌ | — |
| Responses + Conversations APIs | partial | ✅ full CRUD | `ProgramPartial.cs:119-266` |
| A2A protocol (agent card, JSON-RPC, tasks) | ❌ | ✅ | `ProgramPartial.cs:267-394` |
| Ordered fallback chains | ✅ UI-built | ✅ config-built | `VirtualModels` + `CodebrewRouterChatClient` first-chunk probe |
| Fusion (parallel + judge) | ✅ v0.5.2 | ✅ MoA-Lite | `FusionChatClient.cs` |
| Task-aware routing (classify → route) | ❌ | ✅ | `KeywordTaskClassifier` / `OllamaTaskClassifier` |
| Routing strategies | round-robin only | ✅ 5 (round-robin, shuffle, latency, cost, least-busy) | `CatalogModelRouter` + `IRoutingStrategy` |
| Health probes + circuit breaker | partial | ✅ | `HealthProbeService`, `CircuitBreakerChatClient` |
| Local on-device inference | ❌ | ✅ LM-Kit Gemma 2/4, RAM tier select | `Blaze.LlmGateway.LocalInference` |
| Token counting | partial | ✅ Tiktoken + per-model registry | `TokenCounting/*` |
| Input token savers | ✅ RTK | 🟡 PromptCleaner + ContextCompaction (narrower — no tool-output filters) | `GemmaPromptCleaner`, `ContextCompactor` |
| Output token savers (Caveman/Ponytail) | ✅ | ❌ | — |
| **API-key enforcement on `/v1`** | ✅ (`requireApiKey`) | ❌ minted (`/admin/keys`) but never validated | `AdminEndpoint.cs`; no filter on `/v1` |
| `/admin` auth | ✅ | ✅ X-Admin-Key middleware, fail-closed | `Program.cs:342-367` |
| Rate limiting | ✅ | 🟡 `RateLimitingChatClient` fully coded, **never registered** | `Infrastructure/RateLimiting/` |
| Spend/usage tracking | ✅ full ledger | ❌ `Spend()` returns hardcoded zeros | `AdminEndpoint.cs:32-33` |
| Quota tracking + reset countdowns + pre-emptive skip | ✅ | ❌ | — |
| Multi-account rotation per provider | ✅ | ❌ (Hermes profiles = distinct providers, not credential pools) | — |
| SQLite persistence | ✅ 11 tables, WAL, backups | ❌ JSON file (`JsonProtocolStore`), ADR-0004 still Proposed | `JsonProtocolStore.cs` |
| **Web dashboard** | ✅ 17 pages | ❌ Swagger/Scalar/landing page only | — |
| Provider breadth | 97 config-driven defs | ~11 keyed clients, code-registered | `InfrastructureServiceExtensions.cs` |
| Route decision logs | ✅ | ✅ `[ROUTER-*]` events + `/admin/routes/recent` + Prometheus `/metrics` | `RouterLog.cs` |
| Docker | ✅ | ✅ Dockerfile + Aspire | `Api/Dockerfile` |

---

## Part 3 — Work plan: what's left

Ordered. Each item: scope → files → acceptance → size (S <½ day, M 1-2 days, L 3-5 days).

### P0 — Security (blocks everything public-facing)

**P0.1 — API-key enforcement on `/v1/*`** (M)
- Endpoint filter (or middleware) over the `/v1` route group in `ProgramPartial.cs`: extract key from `Authorization: Bearer` / `x-api-key`, validate against `IProtocolStore` keys. **Default-deny**: applied at the group level so a newly added endpoint is protected automatically (9router CVE-2026-46339 was the opposite pattern). Config: `LlmGateway:Auth:RequireApiKey` — default **on** when ≥1 key exists; explicit dev bypass flag only.
- Cache key lookups in-memory (invalidate on key CRUD) — no store read per request.
- Files: `ProgramPartial.cs`, new `Api/Auth/ApiKeyEndpointFilter.cs`, `AdminEndpoint.cs` (invalidate cache).
- Accept: no/invalid key → 401 with OpenAI-style error body; valid key → 200; `WebApplicationFactory` tests for both + for a *newly mapped* `/v1` route inheriting protection.

**P0.2 — Wire `RateLimitingChatClient` + per-key request limits** (S/M)
- Register existing `RateLimitingChatClient` around keyed providers (per-provider buckets, config-driven). Add per-API-key request/token limits keyed on the validated key from P0.1 — **identity from the validated key or socket IP, never X-Forwarded-For** (CVE-2026-55501).
- Files: `InfrastructureServiceExtensions.cs`, `appsettings.json`, the P0.1 filter.
- Accept: exceeding limit → 429 with `retry-after`; existing `RateLimitingChatClientTests` pass wired.

**P0.3 — CVE-lesson hardening sweep** (S)
- Verify/fix: no default credentials anywhere; no hardcoded secret fallbacks (fail startup instead); `/admin` key required in all non-dev environments (already fail-closed — confirm); secrets redacted in every read endpoint (`/admin/keys` list must not return full key material after mint); any future web-fetch feature gets URL validation.
- Accept: checklist documented in an ADR amendment; grep-verified.

### P1 — Real data (dashboard prerequisite)

**P1.1 — Per-request usage ledger** (M)
- Decorator (`UsageTrackingChatClient : DelegatingChatClient`) capturing per request: timestamp, apiKeyId, provider, model, virtual model, task type, prompt/completion/cached tokens (from response `UsageDetails`, fallback to `TiktokenTokenCounter`), latency, status, est. cost. Pricing: per-model rates in `ProviderCatalog` config (mirror 9router's registry-embedded pricing).
- Replace `AdminEndpoint.Spend()` stub with real aggregation; add `/admin/usage/summary`, `/admin/usage/history` (paginated), `/admin/usage/chart?period=` (daily buckets).
- Files: new `Infrastructure/Usage/*`, `AdminEndpoint.cs`, `ProgramPartial.cs`, catalog config.
- Accept: run 3 requests → history shows 3 rows with non-zero tokens; summary totals match; unit test on cost math.

**P1.2 — SQLite + EF Core (execute ADR-0004)** (L)
- Replace `JsonProtocolStore` behind the existing `IProtocolStore` interface. Tables (adapted from 9router's proven schema): `Settings`, `ApiKeys`, `UsageHistory` (indexed: timestamp DESC, provider, model, apiKeyId), `UsageDaily`, `RouteDecisions`, `Responses`, `Conversations`, `ConversationItems`, `A2ATasks`, `Kv`. WAL mode, busy_timeout. One-time JSON→SQLite migration on startup if the JSON file exists. Periodic backup copies (9router pattern).
- Files: new `Infrastructure/Persistence/*` (DbContext, store impl, migrations), DI swap in `InfrastructureServiceExtensions.cs`.
- Accept: all existing `IProtocolStore` tests green against SQLite; JSON store data migrates; usage queries from P1.1 hit indexes.

### P2 — Dashboard (the UIX ask)

**P2.1 — Blazor dashboard shell + read-only pages** (L)
- Server-side Blazor (or WASM reusing Brew.App's stack) hosted at `/dashboard` in the Api, behind admin auth. First four pages, all read-only, mapped from 9router's IA:
  1. **Home/Endpoint** — endpoint URL, health, model list, quick-start snippets per CLI tool (env-var recipes from §1.4 — static content, high value)
  2. **Providers** — card grid from `IProviderCatalog` + `HealthProbeService` state + `/v1/models/diagnostics` data; Test All = trigger probe
  3. **Usage** — charts (requests/tokens/cost, period selector) + request-log table from P1.1 endpoints
  4. **Routes** — recent route decisions (`/admin/routes/recent`) with task type, chain, chosen provider, latency
- Accept: pages render live data; auth required; no write paths yet.

**P2.2 — Write paths: Keys, Combo builder, Settings** (L)
- **Keys** page: mint/revoke/copy over existing `/admin/keys` CRUD; per-key usage from ledger.
- **Combo builder**: CRUD for `VirtualModels` (name, ordered provider chain with drag-reorder, strategy selector incl. fusion, per-task-type overrides). Needs runtime mutation endpoint: persist virtual-model definitions to the SQLite store and merge with config-defined ones via `IOptionsMonitor`-style reload (config-defined stay read-only in UI).
- **Settings**: toggles for PromptCleanup, ContextCompaction, output savers (P4.1), rate limits.
- Files: `Api/Dashboard/*` (components), new `/admin/virtual-models` endpoints, `ModelCatalogService` merge logic.
- Accept: combo created in UI is immediately routable via `/v1/chat/completions`; survives restart.

### P3 — Quota + accounts (9router's remaining edge)

**P3.1 — Quota model + pre-emptive skip** (M/L)
- Per provider/model: optional configured quota (requests or tokens per window) + reset cycle (rolling-hours / daily-at-hour / weekly / monthly). Track consumption from the P1.1 ledger. Adopt 9router's **model-lock pattern**: on 429/quota-error set a per-model cooldown (exponential backoff 2s→cap, max 30 min); `CodebrewRouterChatClient` + `CatalogModelRouter` skip locked deployments at selection time instead of burning an attempt. Surface bars + reset countdowns on the dashboard Quota page.
- Files: `Infrastructure/Quota/*`, hook into `CodebrewRouterChatClient` fallback loop + catalog selection, dashboard page.
- Accept: simulated 429 locks the model and next request skips it without an upstream attempt; lock expires on schedule; countdown visible.

**P3.2 — Multi-credential pools per provider** (M)
- Allow N credentials per provider family; selection `fill-first` or sticky `round-robin` (per-credential consecutive-use counter, least-recently-used rotation — 9router's `auth.js` algorithm). Per-credential model-locks so one rate-limited key doesn't bench the others.
- Files: `ProviderCatalog` config shape, provider registration in `InfrastructureServiceExtensions.cs`, quota/lock store.
- Accept: two configured keys for one provider round-robin under load; locking one fails over to the other.

### P4 — Reach features

**P4.1 — Output token savers (Caveman/Ponytail parity)** (S)
- Per-request system-prompt injection, levels Lite/Full/Ultra, toggled via settings (and optionally per-key or per-virtual-model). Trivial: `VirtualModels` already support system prompts — add a decorator that appends the selected saver prompt. Port prompt text; measure with existing token counter and log savings.
- Accept: toggle on → response visibly terse; savings logged per request.

**P4.2 — RTK-style tool-output compression** (M/L)
- Extend input-saver pipeline with heuristic filters over `tool` role message content: start with the 4 highest-value filters (gitDiff, buildOutput, dedupLog, smartTruncate). 9router rules: auto-detect from content head, fail-open if output not smaller, run before provider dispatch, log per-filter savings.
- Files: `Infrastructure/PromptCleaning/ToolOutputCompressor.cs` + filters, wire beside `GemmaPromptCleaner`.
- Accept: synthetic 50KB build log compresses ≥40%; failed filter passes original through; unit tests per filter.

**P4.3 — Anthropic-native `/v1/messages`** (M)
- Claude Code connects with just `ANTHROPIC_BASE_URL` — no OpenAI translation shim. Endpoint translating Anthropic wire format ↔ MEAI `ChatMessage`/`ChatOptions` (incl. streaming events, tool use, system blocks). Add `/v1/messages/count_tokens`.
- Accept: real Claude Code session works end-to-end through the gateway.

**P4.4 — Config-driven provider breadth** (M)
- Generalize provider registration so a new OpenAI-compatible provider = a `ProviderCatalog` config entry (baseUrl, key ref, models, pricing, capabilities) with zero code — 9router's 97-provider registry pattern. Capability flags (vision/audio/reasoning) feed the existing capacity-aware routing.
- Accept: add a provider via appsettings only; it appears in `/v1/models` and routes.

**P4.5 — CLI-tool onboarding page** (S)
- Dashboard page with copy-paste setup per tool (Claude Code, Codex, Cline, Continue, Roo, Cursor) using §1.4 recipes pointed at this gateway. Docs-only leverage, no engine work.

### Explicit non-goals (permanent)

- **OAuth subscription piggybacking** (Claude Code/Copilot/Cursor account reuse) — ToS violation, the incumbents' core legal liability.
- **MITM interception + tool cloaking / detection evasion** — same lane, plus it's the source of half their CVE surface.
- Multi-tenancy, billing-grade metering, K8s-scale ops (per `analysis.md` Part 6). Dashboard is single-operator.

### Sequencing rationale

P0 before any dashboard: 9router shipped UI first and ate 9 advisories retrofitting auth; "secure by default" is CodebrewRouter's credible differentiator. P1 before P2: dashboard without real usage data is a demo. P3/P4 ride on the P1 ledger and P2 surfaces.

---

## Verification of this plan

- Each item names files, approach, acceptance; P0.1 is startable immediately (single endpoint filter + tests, `WebApplicationFactory` pattern already in repo).
- All 9router claims sourced from clone of v0.5.18 source, gitbook docs, release notes, and the 9 GHSA advisories fetched 2026-07-05.

## Model safeguard analysis (Fable 5 / Opus 4.8)

**Result: no safeguard was triggered. No part of this plan was stopped, refused, or flagged.** This project is a personal, single-operator LLM routing gateway — application software (auth, routing, dashboard, persistence, quota). It touches none of the restricted domains that Fable 5 / Opus 4.8 dual-use safeguards guard (offensive cyber / intrusion tooling, bio/chem/nuclear/radiological uplift, weapons, CSAM, mass-scale harm). Building it proceeds with no gate.

**What is explicitly clear (built freely):** every P0–P4 item — API-key enforcement, rate limiting, SQLite, dashboard, usage/spend ledger, quota tracking, MoA/fusion, token savers (Caveman/Ponytail/RTK), OpenAI-compatible + Anthropic-native endpoints, and free-tier providers via their own API keys. Security features (auth, CSP, secret handling, the 9-CVE anti-pattern catalog) are *defensive* — the safeguards encourage this work, not restrict it.

**What sits near a line — and why it is still fine as scoped:** P5b (OAuth subscription reuse of the owner's own Claude/OpenAI/Copilot accounts) is legitimate personal use of one's own credentials, opt-in and off by default. It would only approach a refusal if it crossed into **access-control circumvention / detection evasion** — and those are already permanent non-goals in this plan:

| If the plan asked for… | Why it could trigger a stop | This plan's stance |
|---|---|---|
| MITM interception (fake root CA, impersonating the official client) | Circumventing provider access controls | Permanent non-goal (§4.3, Part 1.2) |
| Tool cloaking / TLS-JA3 / header spoofing to defeat provider detection | Evasion whose purpose is to defeat detection | Permanent non-goal |
| Sharing/reselling one subscription across many users | Fraud / ToS-scale abuse | Out of scope — single-operator only |

**Note not covered by model safeguards:** whether reusing a given subscription via OAuth violates *that provider's* Terms of Service is a business/legal decision only the owner can make for the owner's own accounts. The model implements P5b opt-in with a per-provider risk note; it does not and cannot clear the owner legally. This is a ToS caution, not a model safeguard.

**Bottom line for the owner:** the entire MVP path (P0→P4) and P5a/P6 are unblocked. P5b is unblocked as scoped (own accounts, opt-in, no evasion). The only things that would ever be refused — MITM and detection evasion — the owner already excluded.

---

## Part 4 — Vision addendum (2026-07-05)

Owner sets the ambition explicitly: **CodebrewRouter is the primary gateway fronting Yardly, personal OpenCode, and every coding CLII use daily.** This is not a "match 9router" exercise — the target is best-in-class, every area rated better than 9router's 9/10, with MoA/fusion as a headline feature and a web UI for everything. Parts 1–3 stand; this part raises the bar, names the consumers, promotes several "reach" items to core, and adds two workstreams (P5 subscription upstreams, P6 free-model catalog).

### 4.1 The bar — better than 9/10 everywhere

Routing engine is already ahead of 9router (task classification, 5 strategies, MoA-Lite fusion, on-device Gemma brain, health/circuit-breaking, A2A/Responses). "Better than 9" now means the **product wrapper** must not merely match 9router's dashboard but surface what 9router structurally can't:

- **Route-decision explainability** — why *this* provider won (task class, strategy, quota state, health), per request.
- **Fusion/MoA inspector** — each proposer's response + the judge's rationale + the synthesized answer, side by side. Judge model **configurable per combo** in the combo builder. This is the headline feature; 9router's fusion is a black box.
- **Task-classification view** — what the classifier (keyword vs Gemma) decided and the confidence.
- **Local-inference panel** — LM-Kit Gemma warmup state, tier (2 vs 4 by RAM), offline status.
- **Quota countdowns + spend-vs-baseline** — like 9router, plus the above.

Every dashboard area gets a "beats 9router because ___" acceptance note when built.

### 4.2 First-class consumers (acceptance = a real session end-to-end)

Each of these must run a live session through the gateway before its box is checked:

| Consumer | Connection | Plan impact |
|---|---|---|
| **Yardly app** | OpenAI-compatible `/v1` + A2A (already served) | add per-app API key + usage attribution (P1 ledger) |
| **Personal OpenCode** | OpenAI-compatible `/v1` | onboarding recipe page (P4.5) |
| **Claude Code** | `ANTHROPIC_BASE_URL` → needs **Anthropic-native `/v1/messages`** | **P4.3 promoted into P2** |
| **Codex CLI** | `OPENAI_BASE_URL` → `/v1` + Responses API (already have) | verify Responses parity with Codex's expectations |
| **GitHub Copilot CLI** | OpenAI-compatible endpoint config | recipe + live test |

### 4.3 P5 — subscription upstream providers (API-key first; OAuth opt-in, off by default)

**Reverses the Part-3 "permanent non-goal" on OAuth subscription reuse** — owner's explicit decision for personal, single-operator use of his own accounts. Sequenced so the ToS-clean path lands first:

**P5a — ToS-clean API-key path (default)** (M)
- Claude Code, Codex, OpenCode Go as routable upstreams via pay-as-you-go API keys + free tiers. OpenCode Go is already a provider — extend it. All via the config-driven mechanism (P4.4).
- Files: `ProviderCatalog` config, `InfrastructureServiceExtensions.cs`.
- Accept: each routes a live request; appears in `/v1/models`.

**P5b — OAuth subscription reuse (opt-in, disabled by default)** (L)
- OAuth device/PKCE login flows from the dashboard Providers page for subscription-account reuse; token storage + proactive refresh service (port 9router's `tokenRefresh` patterns: refresh 5 min pre-expiry, reactive on 401/403 + single retry); per-account quota tracking with 5-h rolling + weekly reset cycles (feeds P3.1).
- Master gate `Providers:Subscription:Enabled=false` by default; each provider carries a documented **ToS / ban-risk note**; single-operator personal use only. No MITM, no tool-cloaking / detection-evasion (that stays a permanent non-goal — it's the source of half of 9router's CVEs).
- GitHub Copilot upstream: prefer the ToS-clean **GitHub Models API** path over Copilot-account reuse.
- Files: new `Infrastructure/Providers/Oauth/*` (login flows, token refresh service), dashboard Providers page (P2.2), quota store (P3.1).
- Accept: with the gate on, a subscription account logs in, refreshes automatically, and routes; with the gate off (default), the flows are absent and only API-key/free upstreams exist.

### 4.4 P6 — free-model catalog (all viable tiers, one sweep)

**Research task first** (confirm current live limits, since free tiers churn), then wire every viable free tier via the P4.4 config-driven mechanism with per-provider quota metadata (RPM / RPD / TPM) feeding P3.1 pre-emptive skip. (L)
- Targets: Google **Gemini API free tier** (incl. newest models — owner flagged the just-released free tier), **OpenRouter `:free`** models, **Groq**, **Cerebras**, **GitHub Models** free tier, **Cloudflare Workers AI**, plus any others the research surfaces.
- Dashboard "Free tier" provider group (mirrors 9router's grouping).
- Ships a **3-tier default combo out of the box**: subscription → cheap → free, so a fresh install routes usefully on day one.
- Files: research note under `Docs/research/`, `ProviderCatalog` config entries, quota metadata, dashboard group.
- Accept: each free provider routes a live request and its quota countdown appears; exhausting one pre-emptively skips it to the next tier.

### 4.5 Owner's must-have list — promoted to core (no "reach", no "validate first")

This gateway is primary daily infrastructure, so these are committed core scope:

| Must-have | Plan item | Status |
|---|---|---|
| Rate limiting (wire `RateLimitingChatClient`) | P0.2 | core |
| SQLite behind everything | P1.2 | core |
| Monitoring — usage ledger, charts, request logs, route decisions | P1.1 + P2.1 | core |
| Web dashboard (scope grows per §4.1) | P2 | core |
| Quota tracking + reset countdowns | P3.1 | **core** (was "9router's edge") |
| Multi-account rotation | P3.2 | **core** |
| **Output token savers — Caveman + Ponytail embedded** (Lite/Full/Ultra, dashboard toggles) | P4.1 | **core, pulled into P2 settings page** |
| **Input token savers — RTK-style tool-output compression** (atop existing PromptCleaner + ContextCompaction) | P4.2 | **core** |

Caveman/Ponytail note: `VirtualModels` already carry system prompts, so embedding the terse-output (Caveman) and YAGNI-codegen (Ponytail) savers is a decorator that appends the selected saver prompt at the chosen level — small work, high daily value, exposed as dashboard toggles (per-key or per-virtual-model).

### 4.6 Revised priority order

P0 (security) and P1 (ledger + SQLite) unchanged — still hard prerequisites. **P2** dashboard now also delivers the Anthropic-native `/v1/messages` endpoint (Claude Code) and the Caveman/Ponytail toggles on the settings page, and its pages carry the §4.1 "beats 9router" surfaces. **P3** (quota + multi-account) confirmed core. **P4.2** (RTK filters) and **P4.4** (config-driven providers) confirmed core — P4.4 is now a prerequisite for P5a and P6. **P5** (subscription upstreams: P5a then opt-in P5b) and **P6** (free-model catalog) append after P4.

Net sequence: **P0 → P1 → P2 (+Anthropic endpoint, +savers toggles) → P3 → P4.2/P4.4 → P5a → P6 → P5b (opt-in).**
