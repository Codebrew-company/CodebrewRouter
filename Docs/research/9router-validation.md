# Idea Validation — 9Router-Class Routing Gateway (CodebrewRouter)

> Companion document: [`9router-gap-analysis.md`](./9router-gap-analysis.md) (feature/UIX gap matrix + roadmap).
> Research date: 2026-07-05. Method: deep-research harness — 5 parallel search angles, top-source fetch, and 3-vote adversarial verification per claim (104 agents; claims killed on ≥2/3 refutation). Votes shown per finding.
>
> **Skill note:** the requested `last30days` and `agent-reach` skills were unavailable in this session. The last-30-days sweep replicates their intent via recency-scoped web research (GitHub API/repo metadata, community discussions, security advisories, dated news). Reddit/X sentiment is therefore measured indirectly through secondary aggregation and is likely **under**-counted.

## Verdict

**The idea is validated — but only in a specific lane.** Demand for cost-aware LLM routing for AI coding tools is real, current, and quantifiable. The generic "route coding CLIs to free tiers" niche, however, is saturated by 9router/OmniRoute and is legally fraught (provider-ToS violations) and security-scarred (a CVSS 10.0 RCE in the market leader). The defensible position for CodebrewRouter is the one its codebase is already closest to: a **ToS-clean, security-first, first-party-Microsoft-stack gateway** (MEAI + Agent Framework + a2a-dotnet) with on-device local-inference classification — sold on the exact failover gap GitHub created in June 2026. That lane is essentially unoccupied.

## A. Demand signals — last 30 days (Jun 5 – Jul 5, 2026)

1. **Copilot billing shock (strongest signal).** GitHub's June 1 switch from flat-rate Premium Request Units to metered "GitHub AI Credits" drew ~900 downvotes and 400+ comments on the announcement; agentic users project 10x–50x cost increases ($29→$750, $50→$3,000); one user burned 8% of a monthly Pro+ allotment in two hours; a single Opus session consumed 16% of a month's credits. *(verified 3-0; cost figures are user projections aggregated by [TechTimes](https://www.techtimes.com/articles/319340/20260629/github-copilot-billing-shock-confirmed-agentic-users-face-10x-cost-surge.htm), corroborated against GitHub's blog and Community Discussions #192948/#198015)*
2. **A vendor-created failover gap.** In the same change GitHub *removed* automatic fallback-to-a-cheaper-model: when credits run out (and no pay-as-you-go budget is set), requests stop rather than downgrade — precisely what an intelligent routing gateway fixes. A March 2026 rate-limit snap-back ([The Register](https://www.theregister.com/2026/04/15/github_copilot_rate_limiting_bug/)) shows the pain is recurring. *(3-0)*
3. **The benchmark incumbent is thriving.** 9router hit 19,954 stars / 3,240 forks (live GitHub API, 2026-07-05) and shipped at least three releases inside the window (v0.5.8 Jun 21, v0.5.12 Jun 26, v0.5.18 Jul 3). Demand for exactly this category is current, not historical. *(3-0)*

## B. Competitive landscape

- **9router (~19.9k ⭐) and OmniRoute dominate the ToS-gray lane**: routing paid coding agents onto free tiers and *reused OAuth subscription accounts* (Claude Code, Codex, Copilot, Cursor), multi-account round-robin, and — in OmniRoute's case — TLS-fingerprint anti-detection and ChatGPT raw-token import. Anthropic's terms restrict OAuth to Claude Code/Claude.ai and account bans are documented. *(features/ToS risk verified 3-0; OmniRoute's exact star count/rank was refuted 1-2 — treat its scale as unverified)*
- **A2A support is NOT a differentiator by itself.** OmniRoute ships A2A (JSON-RPC 2.0 + SSE + agent card) with a built-in MCP server, and LiteLLM's proxy ships a production A2A Agent Gateway routing Vertex AI, LangGraph, Azure AI Foundry, Bedrock AgentCore, and Pydantic AI agents with auth/cost-tracking. CodebrewRouter's A2A story must be narrower: A2A-based routing *of an agent fleet* inside a first-party .NET stack (which is what the Hermes fleet already does). *(3-0; LiteLLM's Foundry/AgentCore providers are recent/preview-labeled — some fleet-routing depth remains open)*
- **The unserved niche is .NET/on-device.** Nobody is building an MEAI-native, on-device-classifying gateway: the official LM-Kit MEAI bridge has ~2 stars and ~323 NuGet downloads. That is simultaneously the opportunity and a dependency risk (see §D). *(3-0)*

## C. Differentiation for CodebrewRouter — technically validated

The stack this repo already uses was verified end-to-end as real and first-party *(all 3-0)*:

- **LM-Kit.NET MEAI integration** exposes local Gemma through `IChatClient`/`IEmbeddingGenerator` with streaming + tool calling, fully on-device — and `Blaze.LlmGateway.LocalInference` already wires LM-Kit 2026.5.1 in-process (`LocalGemmaChatClient`). The local classifier/prompt-cleaner brain needs no HTTP plumbing — something neither 9router nor LiteLLM offers.
- **Microsoft Agent Framework** (11.9k ⭐, dotnet-1.13.0 released Jul 3 2026) ships first-party A2A hosting (`Microsoft.Agents.AI.Hosting.A2A`).
- **a2aproject/a2a-dotnet** (Microsoft-driven, Linux Foundation) provides the A2A server/client, SSE streaming, and task-based long-running communication primitives for fleet routing. *Caveat: 1.0.0-preview2 — API instability.*

**Positioning that survives verification:** "The security-hardened, ToS-clean LLM gateway for the Microsoft stack — API-key providers only, on-device routing brain, agent-fleet A2A routing — that keeps your coding agents running when Copilot credits run dry."

## D. Risks

1. **ToS exposure (avoid the incumbents' lane).** OAuth-subscription reuse is the incumbents' core feature and their core legal liability; Anthropic bans are documented. CodebrewRouter should stay API-key/metered-endpoint only — this is also why the gap-analysis roadmap deliberately skips OAuth-provider piggybacking (P4 note). *(3-0)*
2. **Security is a market-entry requirement, not a nice-to-have.** 9router's advisory cluster — **CVE-2026-46339** (CVSS 10.0 unauthenticated RCE: 40+ `/api/cli-tools/*` and `/api/mcp/*` routes bypassed a dashboard guard protecting only 8 listed routes; fixed 0.4.37) and **CVE-2026-5842** (moderate admin-API auth bypass with public exploit; fixed 0.3.75), plus a hardcoded JWT secret — is the cautionary architecture lesson: unauthenticated tool/MCP execution bolted onto a router. CodebrewRouter currently enforces **no** API-key auth at all (see gap analysis P0). *(3-0; note the research prompt originally mislabeled 5842 as the critical one — corrected here)*
3. **Dependency risk on the LM-Kit MEAI bridge.** The bridge is stale (2026.2.1 vs core 2026.7.1) with ~323 downloads; LM-Kit is commercial/non-OSS — a licensing tension for an open-source gateway. Vendor overall is active (core ~55K downloads), so the risk is bridge-specific. Fallbacks to evaluate: ONNX Runtime GenAI or llama.cpp-based `IChatClient`. *(3-0)*
4. **Commoditization.** Format translation, provider breadth, and even A2A hosting are table stakes held by LiteLLM/OmniRoute. Differentiation must come from the .NET-first + on-device + security posture, not feature count.

## Open questions (from verification)

1. How aggressively are providers enforcing OAuth-reuse bans — would enforcement push incumbent users toward ToS-clean gateways, or collapse the category?
2. What is the real size/willingness-to-pay of the .NET/enterprise segment, given the loudest demand comes from individual devs who gravitate to free-tier-stitching tools?
3. Is the LM-Kit MEAI bridge staleness a deprioritization signal, and is ONNX Runtime GenAI / llama.cpp a viable on-device fallback?
4. How deep is LiteLLM/OmniRoute agent-fleet routing really (capability dispatch, cross-agent failover) vs surface A2A hosting — i.e. how much white space remains for fleet-routing differentiation?

## Caveats

Reddit/X sentiment is measured indirectly (secondary aggregation) and likely under-counted. Copilot cost figures are user projections, not settled invoices (first billing cycle closed days before window end). Star counts, release cadence, and preview-labeled SDKs are all time-sensitive. Two claims were refuted and excluded: OmniRoute's exact market rank (1-2) and a "1,000x agentic token multiplier" claim (0-3). Incumbent capabilities are verified as *marketed positioning*, not as efficacy or ToS compliance.

## Recommendation

Proceed — with the P0-first sequencing in [`9router-gap-analysis.md`](./9router-gap-analysis.md): enforce API-key auth and wire the existing rate limiter *before* any dashboard work (the 9router CVE history makes "secure by default" the credible marketing claim), then real usage/spend + SQLite, then the dashboard UIX that made 9router a product rather than a proxy. Skip OAuth-subscription piggybacking permanently.
