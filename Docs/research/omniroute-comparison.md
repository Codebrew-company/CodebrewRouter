> Companion to [`9router-validation.md`](./9router-validation.md) and [`9router-gap-analysis.md`](./9router-gap-analysis.md).
> Reference: [diegosouzapw/OmniRoute](https://github.com/diegosouzapw/OmniRoute) (~12.4k ⭐, MIT, Node/TS) · [decolua/9router](https://github.com/decolua/9router) (~19.9k ⭐).
> Momentum signals: deep-research run, 2026-07-06 (11 claims verified 3-0; run hit a session limit mid-verification — some 9router-side signals remain unverified, flagged inline).


# OmniRoute vs 9router vs CodebrewRouter

Three-way feature comparison of open-source self-hosted LLM routing gateways for AI coding tools. See also [9router-validation.md](./9router-validation.md), [9router-gap-analysis.md](./9router-gap-analysis.md), [9router-parity-plan.md](./9router-parity-plan.md).

## One-line verdict

**OmniRoute is not a niche fork of 9router — it is a maximalist superset** that out-features both 9router and CodebrewRouter on nearly every breadth axis (237 providers, 17 routing strategies, a 10-engine compression pipeline, a built-in 95-tool MCP server, 21k tests). That makes the strategic conclusion sharper, not weaker: **competing on breadth is unwinnable.** CodebrewRouter's defensible lane is the one neither of them occupies — **.NET/MEAI-native, on-device inference, ToS-clean, security-first, Microsoft-stack enterprise** — and OmniRoute's openly adversarial anti-detection stack (TLS-fingerprint spoofing, MITM, proxy marketplace) *widens* the ToS-clean gap rather than closing it.

## Feature matrix

Legend: ✅ strong / present · 🟡 partial or narrower · ❌ absent · ⭐ distinctive strength

| Capability | OmniRoute | 9router | CodebrewRouter |
|---|---|---|---|
| GitHub stars | ~12.4k | ~19.9k ⭐ | (private) |
| Stack | Node/TS 6, Next.js 16 / React 19 | Node/JS, Next.js 16 | .NET 10 / MEAI ⭐ |
| Providers | 237 (90+ free, 11 perma-free) ⭐ | ~40 | ~5 families + config-driven OpenAI-compat (any endpoint) |
| On-device local inference | ❌ | ❌ | ✅ Gemma via LM-Kit ⭐ |
| Fallback tiers | 4-tier ⭐ | 3-tier | task-classified chains (VirtualModels + FallbackRules) |
| Routing strategies | 17 (⭐ incl. fusion, power-of-two, context-relay) | 1–3 | round_robin / latency / cost / least_busy + task classification |
| Input token saver | RTK + 10-engine pipeline (~89% avg) ⭐ | RTK (−20–40%) | Gemma PromptCleanup + context compaction |
| Output token saver | Caveman + Output Styles ⭐ | Caveman (−65%), Ponytail | TerseMode |
| MCP server | ✅ 95 tools, 3 transports, 30 scopes ⭐ | 🟡 | ❌ (implemented client, disabled) |
| A2A protocol | ✅ JSON-RPC 2.0 + SSE, 6 skills | ❌ | ✅ agent-card + JSON-RPC + tasks ⭐ |
| OpenAI Responses / Conversations APIs | ❌ | ❌ | ✅ full CRUD ⭐ |
| Anthropic Messages API (`/v1/messages`) | 🟡 (translator) | 🟡 (format xlate) | ✅ native incl. SSE event sequence ⭐ |
| Format translation | OpenAI/Claude/Gemini/Cursor/Kiro/Vertex ⭐ | OpenAI/Claude/Gemini/Cursor/Kiro/Vertex | OpenAI + Anthropic |
| API-key auth (enforced) | ✅ per-key read/write/admin scopes, IP filter ⭐ | ✅ (post-CVE) | ✅ enforced, auto-lockdown after first key |
| Rate limiting | ✅ per-key | 🟡 | ✅ wired (per-gateway bucket) |
| Quota tracking + reset countdowns | ✅ multi-window (5h/7d/model) ⭐ | ✅ | ✅ fixed-window per provider |
| Multi-account | ✅ Quota-Share Deficit-Round-Robin ⭐ | ✅ round-robin | ✅ RotatingChatClient round-robin |
| Usage/spend tracking | ✅ live budgets, cost headers ⭐ | ✅ | ✅ SQLite, per-key attribution, pricing |
| Persistence | SQLite + LowDB, AES-256-GCM sealed creds ⭐ | SQLite | SQLite protocol store |
| Admin dashboard | ✅ 9 pages (providers/combos/analytics/health/translator/logs…) ⭐ | ✅ 6 pages | ✅ 6 tabs (overview/usage/routes/models/quotas/keys) |
| Anti-detection | ⚠️ TLS-fingerprint spoof, 3-level proxy, MITM TPROXY ⭐(risk) | ❌ | ❌ (deliberate) |
| OAuth-subscription reuse | ⚠️ 8 providers PKCE auto-refresh (risk) | ⚠️ (risk) | ❌ (deliberate — API-key only) |
| Tests | 21,000+ / 2,586 files ⭐ | (unquantified) | 626 (−warnaserror gate) |
| Deploy targets | npm/Docker/Electron/Termux/PWA/Podman/ARM ⭐ | localhost/VPS/Docker/CF Workers | Aspire + Docker compose |
| i18n | 42 locales ⭐ | multi (video docs) | ❌ |
| License | MIT | MIT | (private) |

## What's genuinely differentiating vs commodity table-stakes

**Now commodity (everyone has it — do not compete here):** OpenAI-compatible endpoint, tiered fallback, combos/chains, per-key auth, SQLite usage tracking, a provider dashboard, basic token compression, format translation. CodebrewRouter already matches these after P0–P4.

**OmniRoute's real moats:** provider *catalog* breadth (237, curated + deduplicated), the 10-engine compression pipeline (LLMLingua-2 ML semantic compression is genuinely ahead), 17 routing strategies, the built-in MCP server, and the deployment surface (Termux/PWA/Electron). These are real and hard to match — but they are breadth/polish, not architecture.

**CodebrewRouter's moats (unmatched by either):**
- **On-device inference** (LM-Kit Gemma) as the routing brain — neither competitor runs a model locally; both are pure proxies.
- **First-party Microsoft stack** — MEAI + Agent Framework + a2a-dotnet. The only .NET-native entrant in a JS-monoculture category.
- **Responses + Conversations APIs** — neither competitor implements OpenAI's stateful protocols.
- **ToS-clean + security-first posture** — no OAuth-subscription piggybacking, no anti-detection. This is a *marketable* stance precisely because the incumbents can't claim it.

## Risk column — the ToS/security divergence

OmniRoute leans *harder* into the legally fraught lane than 9router:
- **TLS-fingerprint spoofing (wreq-js JA3/JA4), transparent MITM decrypt (TPROXY per-SNI CA), and a free-proxy marketplace** are explicit provider-evasion tooling. This is not incidental OAuth reuse — it is engineered anti-detection, which materially raises ToS-breach and CFAA-style exposure for operators and contributors.
- **OAuth-subscription reuse** across 8 providers with auto-refresh — same core ToS violation flagged for 9router (Anthropic restricts OAuth to Claude Code/Claude.ai; documented bans).
- 9router's own history — **CVE-2026-46339 (CVSS 10.0 unauthenticated RCE, fixed 0.4.37)** and **CVE-2026-5842 (auth bypass, fixed 0.3.75)** — is the cautionary tale for bolting broad unauthenticated surface onto a router. OmniRoute's much larger surface (237 providers, MCP server, MITM proxy) is a correspondingly larger attack surface to audit; its AES-256-GCM sealed credentials and per-key scoping are the right mitigations, but the surface is vast.

**Implication for CodebrewRouter:** the security-first, ToS-clean position is *strengthened* by OmniRoute's direction. As providers enforce against fingerprint-evasion (a harder line than passive OAuth reuse), an operator wanting a gateway that won't get their accounts banned or expose them legally has essentially one architecture to choose — and it isn't these two.

## Momentum & mindshare (last 30 days)

> Method: deep-research run (recency-scoped, replicating the unavailable `last30days`/`agent-reach` skills). The run hit a session token limit mid-verification — synthesis and ~41 verifier votes failed — so 9router-side momentum signals (fresh star count, OAuth-reuse framing, v0.5.18 cadence) errored **unverified** rather than being refuted. The OmniRoute-side signals below all passed **3-0 adversarial verification**.

**Verified (3-0):**
- **OmniRoute is on a torrid release cadence — a new release roughly every 1–2 days** (v3.8.45 on Jul 6 2026, v3.8.40 on Jun 29 → six releases in ~8 days). This is the strongest in-window momentum signal in the category.
- **OmniRoute is at v3.8.45 — a mature, heavily-iterated codebase, decisively NOT an early-stage niche fork of 9router.** It is a genuine rival.
- **OmniRoute ~12.4k stars** confirmed (vs 9router's ~19.9k from the earlier verified run — 9router still leads on adoption, OmniRoute leads on breadth + commit velocity).
- **The RTK + Caveman compression tech is shared lineage across BOTH 9router and OmniRoute** — i.e. token compression is now *commodity table-stakes*, not an OmniRoute moat. (9router: RTK −20–40% input, Caveman −65% output; OmniRoute stacks the same base into a larger pipeline claiming 78–95%.)
- OmniRoute's 4-tier fallback (Subscription→API→Cheap→Free) is one tier deeper than 9router's 3-tier, but the **same architectural pattern** — also commodity.
- OmniRoute's "auto" routing strategy uses **9-factor live scoring** (verified) — the one routing feature that is genuinely more sophisticated than a static strategy list.

**Unverified (errored on session limit — treat as directional, not confirmed):** a possible OmniRoute star trajectory of ~8.1k (Feb 2026) → ~12.4k (Jul 2026), i.e. ~50% growth in ~5 months (would confirm strong momentum); 9router's fresh star count/forks; and an emerging sibling tool ("CC Gateway") that strips Anthropic billing headers — worth a follow-up scan when the token budget resets.

**Read:** OmniRoute has the *momentum* (release velocity, feature accretion) even though 9router has the *lead* (stars). Neither signal changes CodebrewRouter's strategy — both confirm a fast-moving, breadth-maximizing, ToS-fraught category where the winning move is a differentiated architecture, not a breadth catch-up.

## Bottom line for CodebrewRouter

1. **Do not chase provider count or compression-engine count.** That race is lost to OmniRoute and irrelevant to the target user.
2. **Double down on the four moats** (on-device, .NET/MEAI, stateful APIs, ToS-clean/secure). Every one is unavailable from both competitors.
3. **Market the security posture explicitly** — "the gateway that won't get you banned or breached" is now a *three-way* differentiator, not a two-way one.
4. **Selectively adopt** the few OmniRoute ideas that fit the lane without the risk: multi-window quota buckets, an MCP *server* surface (JARVIS Phase 3 already wants this), and per-key scopes (read/write/admin) on top of the auth already shipped.
