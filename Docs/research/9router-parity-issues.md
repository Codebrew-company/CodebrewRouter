# 9Router Parity Plan — Execution Log & Manual Issues

> Execution date: 2026-07-05. Executes [`9router-parity-plan.md`](./9router-parity-plan.md) on `master`.
> Everything below was implemented in this pass unless marked ❗ (manual follow-up) or ⏭ (not started).

## What shipped

| Plan item | Status | Where |
|---|---|---|
| **P0.1** API-key enforcement on `/v1/*` | ✅ | `Api/Auth/ApiKeyAuthentication.cs` (path-prefix middleware = default-deny for every current+future `/v1` route), `Api/Auth/ApiKeyCache.cs` (in-memory cache, invalidated on key CRUD), config `LlmGateway:Auth:RequireApiKey` (null=auto-on when ≥1 key exists, false=dev bypass). 10 `WebApplicationFactory` tests in `Tests/Auth/ApiKeyAuthTests.cs`. |
| **P0.2** Rate limiting | ✅ | Per-key/per-IP request limits in the auth middleware (`RequestsPerMinutePerKey` / `RequestsPerMinutePerIp`; identity = validated key or **socket IP**, never XFF) → 429 + `Retry-After`. Provider-level: `LlmGateway:RateLimits:{providerKey}` wraps keyed clients with the existing `RateLimitingChatClient` (LmStudio, DerpYardly, all OpenCodeGo models, all Hermes profiles). |
| **P0.3** Hardening sweep | ✅ | `/admin/keys` list redacts key material (full key only at mint); admin-key compare is constant-time (`CryptographicOperations.FixedTimeEquals`); admin guard remains fail-closed outside Development; grep sweep found no hardcoded credentials/secret fallbacks. See checklist below. |
| **P1.1** Usage ledger | ✅ | `Api/Usage/UsageTrackingChatClient.cs` decorates the unkeyed router client (all `/v1` traffic): tokens (provider-reported, estimate fallback), latency, status, streamed, api-key attribution via `HttpContext.Items`, cost from catalog `CostPerToken`. Real `/admin/spend` + new `/admin/usage/summary`, `/admin/usage/history?limit&offset&keyId`, `/admin/usage/chart?days`. Tests in `Tests/Usage/UsageTrackingChatClientTests.cs`. |
| **P1.2** SQLite store (ADR-0004) | ✅ | `Api/SqliteProtocolStore.cs` — WAL, busy_timeout 5s, real columns+indexes for `usage_history`, JSON docs for protocol objects, one-time legacy JSON migration (renames to `.migrated`). Default store; `LlmGateway:ProtocolStore:Provider=json` reverts. Tests in `Tests/Persistence/SqliteProtocolStoreTests.cs`. |
| **P2** Dashboard | ✅ (v1) | `/dashboard` — self-contained HTML (no CDN): Home + CLI recipes, Providers, Usage (KPIs, 30-day chart, request log), Routes, Quota cooldown countdowns, Keys (mint/revoke). Data via `/admin/*` with operator-entered `X-Admin-Key`. New `/admin/providers` alias so the dashboard needs only the admin key. |
| **P3.1** Quota / model locks | ✅ | `Infrastructure/Quota/ModelLockRegistry.cs` — 9router pattern: text-match rate-limit signals (429/quota/overloaded/…), exponential cooldown 2s·2^level capped 30 min, cleared on success. `CodebrewRouterChatClient` skips locked providers pre-emptively in both streaming and non-streaming loops. `/admin/quota/locks` + dashboard Quota tab. Tests in `Tests/Quota/ModelLockRegistryTests.cs`. |
| **P4.1** Caveman/Ponytail savers | ✅ | `Infrastructure/OutputSavers/OutputSaverChatClient.cs` + prompts (Lite/Full/Ultra), wired into the unkeyed pipeline, live-toggled via `LlmGateway:OutputSavers` (`IOptionsMonitor`). Tests in `Tests/OutputSavers/`. |
| **P4.2** RTK tool-output compression | ✅ | `Infrastructure/PromptCleaning/ToolOutputCompressor.cs` (gitDiff, buildOutput, dedupLog, smartTruncate; auto-detect from first 1KB; fail-open) + `ToolOutputCompressingChatClient` before provider dispatch. 50KB build log compresses >40% (test-verified). Config `LlmGateway:ToolOutputCompression`. |
| **P4.3** Anthropic-native `/v1/messages` | ✅ | `Api/AnthropicMessagesEndpoint.cs` + `AnthropicModels.cs`: full Messages wire format (system blocks incl. array form, content-part arrays, base64/url images, tool_use/tool_result), Anthropic SSE event protocol (message_start → content_block_* → message_delta → message_stop), tool_choice mapping, `/v1/messages/count_tokens` via `ITokenCounter`. 5 integration tests in `Tests/Anthropic/`. |
| **P4.4** Config-driven providers | ✅ | Any `ProviderCatalog:Deployments` entry whose `Provider` key has no code-registered client gets a keyed `IChatClient` auto-built from config (endpoint, key(s), model, context window, per-deployment rate limits) — zero code for a new OpenAI-compatible provider. Tests in `Tests/Infrastructure/ConfigDrivenProviderRegistrationTests.cs`. |
| **P3.2** Multi-credential pools | ✅ | `ProviderDeploymentConfig.ApiKeys` + `CredentialStrategy` (`fill-first` / `round-robin`, rotate after 5 consecutive uses). `Infrastructure/Quota/CredentialPoolChatClient.cs`: one inner client per key, per-credential exponential cooldown on rate-limit signals, first-chunk-probe streaming failover. Tests in `Tests/Quota/CredentialPoolChatClientTests.cs`. |

## ❗ Manual issues / decisions for the owner

1. **`ServicePointManager.ServerCertificateValidationCallback += (…) => true` in `Program.cs:26`** disables TLS certificate validation process-wide. Dev convenience, but it silently applies to cloud providers (OpenCode Go) too. Recommend gating on `builder.Environment.IsDevelopment()` or per-endpoint `HttpClientHandler`. Not changed (behavior-affecting for your homelab HTTPS endpoints).
2. **Brew endpoints (`/api/chat`, `/api/memory/*`) remain unauthenticated.** Plan scoped auth to `/v1`; these paths bypass it. If the gateway is ever exposed beyond localhost, extend the middleware to `/api` or put those behind the admin guard.
3. **P1.2 deviation: raw `Microsoft.Data.Sqlite` instead of EF Core.** Same schema intent as ADR-0004, far less weight, all `IProtocolStore` behavior test-covered. If you specifically want EF Core migrations, that's a swap behind the same interface. ADR-0004 status should be flipped Proposed→Accepted (with the no-EF note) — I did not edit the ADR.
4. **Pricing granularity:** cost uses catalog `CostPerToken` (single flat rate per deployment). 9router has per-model input/output/cached rates. All current deployments have `CostPerToken: 0`, so spend shows $0 until you populate real rates in `ProviderCatalog:Deployments`.
5. **Provider attribution in the ledger** is `ChatOptions.ModelId` (requested) + `response.ModelId` (provider-reported). The winning *provider key* (e.g. `OpenCodeGo_KimiK2_6`) is known only inside `CodebrewRouterChatClient`; correlating it into the ledger needs a small plumb-through (route decisions already log it — join on time for now).
6. **Model-lock acceptance** ("simulated 429 → next request skips without upstream attempt") is verified at unit level (`ModelLockRegistryTests`) + code-wired in both router loops; a full E2E with a real 429-returning upstream wasn't run (no live provider in this session).
7. **`RateLimitExceededException` from provider-level buckets** currently surfaces via the router's normal failover (it also triggers a model-lock since the message matches "Rate limit"). It is *not* mapped to an HTTP 429 at the endpoint when all providers are exhausted — client sees the standard provider-error body.
8. **Dashboard deviations from plan:** vanilla HTML/JS instead of Blazor (zero deps, CSP-clean, ~300 lines). No drag-drop combo builder, no runtime virtual-model CRUD (P2.2), no settings write-path yet — savers/limits toggle via appsettings (hot-reloaded). The shell at `/dashboard` is public; all data calls require `X-Admin-Key`.
9. **Live-session acceptance tests (Part 4.2)** — Claude Code, Codex, OpenCode, Yardly end-to-end sessions through the gateway were not run (needs the gateway running + real keys). The Anthropic endpoint passes wire-format contract tests; try `ANTHROPIC_BASE_URL=http://<host>:<port>` with a real Claude Code session.
10. **Auth default is auto-mode:** `/v1` stays open until you mint the first key (`POST /admin/keys`). After that, every `/v1` call needs the key. If Yardly/OpenCode/Brew clients aren't updated with a key when you mint one, they will start getting 401s. Set `LlmGateway:Auth:RequireApiKey=false` to keep the old behavior explicitly.
11. **Static rate-limit buckets** in the auth middleware are process-lifetime and keyed `key-id:rpm` — a config RPM change takes effect immediately (new bucket) but old buckets linger in memory (bounded by #keys × #rpm-values; negligible).
12. **`conversation_items` growth:** SQLite store has no cap on conversation items (JSON store didn't either). Fine for single-operator; add retention if conversations get heavy.

## ⏭ Not started (next phases per plan §4.6)

- **P4.5** CLI-tool onboarding page exists in minimal form on the dashboard Home tab (Claude Code / Codex / OpenAI-compatible / curl); the full 17-tool page with per-tool status detection is open.
- **P5a/P5b** subscription upstreams — ❗ blocked on owner input: needs real pay-as-you-go API keys (Anthropic/OpenAI/OpenCode Go). The mechanism (P4.4 config-driven + P3.2 pools) is ready; each provider is now just an appsettings entry. P5b OAuth flows remain a build item.
- **P6** free-model catalog sweep — ❗ blocked on research + owner keys: free-tier limits churn, and Gemini/Groq/Cerebras/OpenRouter/GitHub Models each need an account/key. Once keys exist, each is one `ProviderCatalog:Deployments` entry with `MaxRequestsPerMinute`/`MaxTokensPerMinute` quota metadata feeding the P3.1 locks.
- **P2.2** dashboard write paths (combo builder, settings page) and §4.1 "beats 9router" surfaces (fusion inspector, task-classification view, local-inference panel).
- Configured quota windows (requests/tokens per reset cycle) — P3.1 shipped the reactive lock; proactive configured-quota tracking rides on the P1.1 ledger next.

## P0.3 hardening checklist (9router CVE lessons)

- [x] Default-deny auth on `/v1` (path prefix, not route allowlist) — CVE-2026-46339 inverse.
- [x] No default credentials anywhere (no default admin key; fail-closed outside Development).
- [x] No hardcoded secret fallbacks (grep-verified; provider keys default to empty/`notneeded` for keyless local endpoints only).
- [x] Rate-limit identity from validated key or socket IP, never `X-Forwarded-For` — CVE-2026-55501.
- [x] Secrets redacted in read APIs (`/admin/keys` list) — GHSA-vjc7; full key returned exactly once at mint.
- [x] Constant-time admin key comparison.
- [x] No web-fetch feature exists; if added, URL allow/deny validation required — GHSA-qj3v.
- [ ] ❗ Process-wide TLS validation bypass (issue #1 above).
- [ ] ❗ DB export/import endpoint intentionally **not** built (CVE-2026-55500); if ever added, require re-auth + encryption.
