# CodebrewRouter Agentic MVP Completion Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task-by-task. Preserve user edits, run tests at every gate, and keep the logging contract intact.

**Goal:** Ship CodebrewRouter as a protocol-complete, OpenAI-compatible, Microsoft Agent Framework-powered LLM gateway with full Chat Completions, Responses, A2A, routing, memory, MCP, skills, and multi-tenant controls in the MVP.

**Architecture:** CodebrewRouter is not a thin LiteLLM clone. It is a C#/.NET agentic orchestration engine wearing an OpenAI-compatible mask. Every public model is resolved through an agent registry to either a single `AIAgent` or a workflow, while the northbound clients keep using normal model IDs and OpenAI-compatible APIs.

**Tech Stack:** .NET 10, Minimal APIs, Microsoft.Extensions.AI, Microsoft Agent Framework, A2A v1 hosting/client packages, EF Core SQLite, OpenTelemetry, Aspire DevUI, MCP, OpenAI-compatible provider adapters.

---

## Thesis

**LiteLLM is a passthrough proxy. CodebrewRouter is a Microsoft Agent Framework orchestration engine wearing an OpenAI-compatible mask.**

Every "model" exposed at `/v1/models` is an `AIAgent` or a `Workflow` under the hood. Single-model requests are an agent of size 1. Multi-model requests such as Council, Planner -> Executor, handoff, and specialist peer review are workflows. Clients do not need to know; they see OpenAI-compatible Chat Completions, Responses, or A2A.

This positions CodebrewRouter as an open-source LiteLLM alternative with Agent Framework superpowers: multi-tenant from day one, composable agentic primitives, durable sessions, model councils, MCP-native tools, A2A interoperability, and profile-scoped memory.

## Authoritative MVP Correction

This file supersedes older docs that deferred Responses or A2A, including older ADR notes and prior superpowers specs. For this MVP:

- Full OpenAI-compatible Chat Completions ships.
- Full OpenAI-compatible Responses ships.
- Conversations support required by Responses ships.
- Full A2A v1 hosting ships, not discovery-only.
- Microsoft Agent Framework is the primary agent runtime.
- No LiteLLM runtime is used.

## Product Positioning

- **Target:** Open-source LiteLLM alternative plus agentic orchestration platform.
- **Tenancy:** Multi-tenant from day one with virtual API keys, per-key policy, per-key spend tracking, and default-deny cloud egress.
- **Core differentiators:** Agent Framework workflows, Council mode, profile-scoped MCP/skills/memory, A2A v1, local-first routing, DevUI testing, and normal OpenAI-compatible client support.
- **MVP parity with LiteLLM:** OpenAI-compatible endpoints, virtual keys, spend tracking, fallbacks, capability-aware routing, cloud-egress policy, and provider catalog.
- **MVP beyond LiteLLM:** agent/workflow virtual models, full A2A surface, Microsoft Agent Framework integration, Yardly multimodal path, and the C#/.NET juggernaut profile.

## Architectural Layers

```text
HTTP
  /v1/chat/completions
  /v1/responses
  /v1/conversations
  /v1/models
  /admin/keys
  /admin/spend
  /.well-known/agent-card.json
  /a2a/{agentName}

Auth/Policy
  Virtual-key middleware
  Tenant identity
  Allowed models/providers
  Cloud egress allow-list
  Tool/MCP RBAC

Protocol Translation
  Chat Completions DTOs
  Responses DTOs/events
  Conversations DTOs
  A2A message/task/artifact mapping
  OpenAI-style errors

Agent Registry
  model name -> AIAgent | Workflow
  profile capabilities
  profile memory/tools/skills
  fallback and escalation policy

Microsoft Agent Framework
  ChatClientAgent
  AgentThread
  Sequential workflow
  Concurrent workflow
  Handoff workflow
  A2AAgent for remote peer agents

MEAI Pipeline
  Output cleanup
  MCP tool delegation
  routing/failover clients
  keyed provider clients
  function/tool call normalization

Persistence
  SQLite + EF Core
  Tenants, ApiKeys, ProviderCatalog, ModelProfiles
  Sessions, Messages, Conversations, Responses
  A2ATasks, Artifacts, ToolCalls, Memory
  SpendLedger, RoutingDecisions, AssetCatalog

Telemetry
  [ROUTER-*] and [AGENT-*] tags
  OpenTelemetry GenAI spans
  per-tenant/provider/model attributes
```

## Virtual Models

| Model name | Composition | Purpose | MVP requirements |
|---|---|---|---|
| `codebrewRouter` | `ChatClientAgent` backed by local Gemma 4 routing/general model | General chat, default routing, fallback decisions | Must be concise, normal chat-like, and strip hidden thinking/control tokens |
| `codebrewPlanner` | Planning workflow: planner agent -> critic/validator agent -> final plan | Coding plans, Yardly plans, ADRs, PRDs, acceptance criteria, multi-agent execution strategy | Uses configured high-reasoning model such as `gpt-5.5` when policy allows; falls back to best local/allowed planner |
| `codebrewSharpClient` | C#/.NET `ChatClientAgent` + Microsoft Learn MCP + curated assets + AgentThread | C#, .NET, MAUI, Aspire, Azure, Agent Framework, MCP, EF, Blazor | Must become the C#/.NET juggernaut profile |
| `codebrewCouncil` | Concurrent workflow: multiple frontier/specialist models -> arbiter `ChatClientAgent` | Hard questions and model-council deliberation | Requires cloud-allowed key unless all members are local/allowed |
| `yardly` | Yardly agent + vision-capable provider selection + Yardly memory/RAG | Plant ID, plant disease, care, yard/device support, mobile/local use | Must support image content parts and never behave like a developer assistant to Yardly end users |

Adding a virtual model should mean adding config plus an optional workflow graph; avoid per-model endpoint code.

## MVP Public Surfaces

### OpenAI-Compatible Models

- `GET /v1/models`
- Return OpenAI-shaped model list.
- Include non-breaking metadata for owner, source, capabilities, virtual profile, tool support, vision support, context window, routing policy, and cloud requirement.

### OpenAI-Compatible Chat Completions

- `POST /v1/chat/completions`
- Streaming and non-streaming.
- Required request support:
  - `model`
  - `messages`
  - `stream`
  - `tools`
  - `tool_choice`
  - `parallel_tool_calls`
  - `response_format`
  - `temperature`
  - `top_p`
  - `max_tokens`
  - `max_completion_tokens`
  - `stop`
  - `reasoning_effort`
  - `stream_options.include_usage`
- Required content support:
  - String content.
  - Content-part arrays.
  - Text input.
  - Image URL input.
  - Data URI image input where provider supports it.
  - Tool result messages with `tool_call_id`.
- Required response support:
  - Assistant text.
  - Assistant `tool_calls`.
  - Streaming `delta.content`.
  - Streaming `delta.tool_calls`.
  - Final usage chunk when requested.
  - `data: [DONE]`.

### OpenAI-Compatible Responses

- `POST /v1/responses`
- `GET /v1/responses/{response_id}`
- `DELETE /v1/responses/{response_id}`
- `POST /v1/responses/{response_id}/cancel`
- `GET /v1/responses/{response_id}/input_items`
- `POST /v1/responses/input_tokens`
- `POST /v1/responses/compact`

Required Responses behavior:

- Persist response objects when `store=true` or when a conversation requires state.
- Support `previous_response_id` for multi-turn state.
- Support `conversation` linkage, mutually exclusive with `previous_response_id` when the upstream spec requires it.
- Support text, image, file-shaped placeholders, function/tool calls, tool outputs, and reasoning metadata as stored response items.
- Support background responses enough for cancellation semantics.
- Emit semantic SSE events for streaming:
  - response created
  - response in progress
  - output item added
  - output text delta
  - function call arguments delta
  - function call arguments done
  - completed
  - failed
  - cancelled
  - error
- For unsupported built-in OpenAI tools such as remote web search/computer use/code interpreter, return a clear capability error unless CodebrewRouter has an explicitly configured gateway-owned tool equivalent.

### OpenAI-Compatible Conversations

- `POST /v1/conversations`
- `GET /v1/conversations/{conversation_id}`
- `POST /v1/conversations/{conversation_id}`
- `DELETE /v1/conversations/{conversation_id}`
- `GET /v1/conversations/{conversation_id}/items`
- `POST /v1/conversations/{conversation_id}/items`
- `GET /v1/conversations/{conversation_id}/items/{item_id}`
- `DELETE /v1/conversations/{conversation_id}/items/{item_id}`

Required Conversations behavior:

- Store metadata.
- Store ordered items.
- Support pagination with `after`, `limit`, and `order` where applicable.
- Support `include` parameters without failing when values are unknown; ignore unsupported includes unless a requested include is impossible to represent.
- Add response input/output items back to the conversation after successful completion.

### A2A v1

- `/.well-known/agent-card.json`
- `GET /a2a/{agentName}/.well-known/agent-card.json` if per-agent cards are needed.
- `MapA2AHttpJson` endpoints for each public virtual agent.
- `MapA2AJsonRpc` endpoints for each public virtual agent where the SDK supports it.
- A2A task lifecycle:
  - message send
  - message stream
  - task get
  - task list
  - task cancel
  - task subscribe
  - task status
  - task artifacts
  - push notification config when signed webhook settings are present

Required A2A behavior:

- Agent cards advertise `codebrewRouter`, `codebrewPlanner`, `codebrewSharpClient`, `codebrewCouncil`, and `yardly`.
- A2A messages map to internal agent events without flattening multimodal parts.
- A2A tasks persist across process restart.
- A2A artifacts persist and can be retrieved.
- Streaming uses the selected A2A SDK's SSE model.
- Remote A2A agents can be consumed as `A2AAgent` peers inside workflows when configured.

### Admin APIs

- `POST /admin/keys`
- `GET /admin/keys`
- `DELETE /admin/keys/{id}`
- `GET /admin/spend?key_id=...`
- `GET /admin/routes/recent`
- `GET /admin/assets`
- `POST /admin/assets/sync`

Admin APIs are JSON-only in MVP. No Blazor admin UI is required for MVP.

## Persistence Schema

Use SQLite + EF Core for MVP. Enable WAL mode and assume single-process writes until scale-out is explicitly designed.

Required tables:

- `Tenants`
- `ApiKeys`
- `ProviderCatalog`
- `ModelProfiles`
- `Sessions`
- `Messages`
- `Conversations`
- `ConversationItems`
- `Responses`
- `ResponseItems`
- `A2ATasks`
- `A2AArtifacts`
- `ToolCalls`
- `MemoryRecords`
- `DeveloperPreferences`
- `RoutingDecisions`
- `SpendLedger`
- `AssetCatalog`
- `AssetActivations`

Required ID conventions:

- `tenant_<ULID>`
- `key_<ULID>` for database key IDs; actual secrets use `sk-cbr-...`
- `sess_<ULID>`
- `conv_<ULID>`
- `resp_<ULID>`
- `msg_<ULID>`
- `task_<ULID>`
- `artifact_<ULID>`

## Multi-Tenant Auth and Policy

- Virtual API keys use `sk-cbr-*` format.
- Store only hashed secrets.
- Every `/v1/*`, `/a2a/*`, and admin request must resolve tenant identity unless explicitly marked public discovery.
- Key policy includes:
  - allowed models
  - allowed providers
  - cloud allowed
  - allowed MCP servers
  - allowed gateway-owned tools
  - max council fanout
  - optional spend budget metadata
- Cloud egress is default-deny.
- `codebrewCouncil` and `codebrewPlanner` must fail or degrade clearly when the selected high-reasoning/cloud provider is not allowed.

## Routing Rules

### General Prompts

- Use `codebrewRouter`.
- Use local Gemma 4 by default.
- Optimize for concise, normal chat responses.
- Never leak chain-of-thought, hidden planning, internal routing JSON, prompt-cleaning notes, or local model control tokens.

### Planning Prompts

Use `codebrewPlanner` when:

- Client explicitly requests `codebrewPlanner`.
- Prompt asks to plan, design, architect, write a PRD, define acceptance criteria, coordinate agents, build a roadmap, decompose work, or compare implementation strategies.
- Conversation state indicates the user is iterating on a plan.

`codebrewPlanner` remains lead even when the plan is about C#, Yardly, DevUI, A2A, or MCP. It may call specialist peers internally.

### Language Affinity

Use explicit prompt signals first, then saved developer + repo affinity:

- C#, .NET, ASP.NET, MAUI, Aspire, EF, Blazor -> `codebrewSharpClient`
- Yardly, plant ID, plant disease, care, yard/device domain -> `yardly`
- Unknown programming language -> ask `codebrewRouter` to choose the best available profile from catalog metadata

Persist:

- detected language
- selected model/profile
- confidence
- reason
- developer identity
- repo/workspace scope
- timestamp

### Capability Routing

Before invoking a provider, validate:

- context window
- streaming support
- tool-call support
- structured output support
- image/vision support
- local/cloud requirement
- reasoning support
- MCP/tool permission
- tenant/key allow-list

If no provider satisfies required capabilities, return an explicit `model_capability_error` instead of silently degrading.

## MCP, Skills, and Asset Catalog

### MCP

- MCP is profile-scoped and RBAC-gated.
- Client-owned tools are passed through and returned as tool calls; CodebrewRouter must not execute them.
- Gateway-owned MCP tools execute only when:
  - profile allows the MCP server
  - API key allows the MCP server
  - requested tool is enabled
  - arguments validate
- Microsoft Learn MCP is enabled for `codebrewSharpClient` when policy allows.

### Skills and Prompts

Skills/prompts/instructions are assets, not magic global context. Each asset has:

- source
- source URL/path
- license when known
- hash
- title
- description
- tags
- required tools
- supported profiles
- activation rules
- last synced timestamp

### Awesome Copilot Ingestion

Ingest selectively from:

- local `.github/agents`
- local `.github/plugins`
- local `.agents/skills`
- local `.opencode/agents`
- curated Awesome Copilot agents
- curated Awesome Copilot skills
- curated Awesome Copilot prompts
- curated Awesome Copilot instructions

For `codebrewSharpClient`, prioritize assets tagged with:

- C#
- .NET
- ASP.NET
- MAUI
- Aspire
- Azure
- Microsoft Agent Framework
- MCP
- Entity Framework
- Blazor
- testing
- debugging
- architecture
- security review

Never load every asset into a request. Activate only the minimal useful set and record `AssetActivations`.

## Virtual Model Details

### `codebrewRouter`

Required capabilities:

- General chat.
- Local-first routing.
- Fallback selection.
- Model capability explanation.
- Output cleanup.
- No hidden reasoning leakage.

Acceptance examples:

- "Explain options trading simply" returns a concise user-facing answer.
- "Could you start by asking me when I procrastinate most?" asks the question first and does not dump planning notes.
- Unknown model/provider availability returns a clear OpenAI-style error.

### `codebrewPlanner`

Required capabilities:

- Planning-intent detection.
- High-reasoning model selection by policy.
- Skills activation.
- Multi-agent plan generation.
- Acceptance criteria generation.
- Persisted decisions and open questions.

Acceptance examples:

- "Plan Yardly disease detection workflow" routes to `codebrewPlanner` and uses Yardly context.
- "Make an implementation plan for A2A" routes to `codebrewPlanner`.
- Cloud-denied planning uses local planner fallback or returns a policy-aware explanation.

### `codebrewSharpClient`

Required capabilities:

- Microsoft Learn MCP access.
- C#/.NET asset activation.
- Repo-aware language affinity.
- .NET docs grounding.
- Agent Framework and Aspire guidance.
- MAUI-safe code guidance.

Acceptance examples:

- "How do I wire Microsoft Agent Framework into a .NET MAUI app?" routes to `codebrewSharpClient`.
- "Fix this C# stack trace" routes to `codebrewSharpClient`.
- "Use Microsoft Learn MCP" triggers gateway-owned MCP only when allowed.
- Relevant Awesome Copilot assets activate with provenance logged.

### `codebrewCouncil`

Required capabilities:

- Concurrent fanout to configured member profiles/providers.
- Arbiter synthesis.
- Spend attribution for every member call and arbiter call.
- Parent/child OTel spans.
- Clear policy denial when cloud fanout is not allowed.

Acceptance examples:

- A hard architecture prompt triggers three member calls plus one arbiter call.
- The final response is one OpenAI-shaped stream.
- Spend ledger contains all calls.

### `yardly`

Required capabilities:

- Plant and yard domain behavior.
- Image input support.
- Vision-capable provider routing.
- Yardly memory/RAG.
- Technical peer delegation without changing final voice.

Acceptance examples:

- Plant image + "what disease is this?" routes to a vision-capable provider.
- Non-vision provider returns `model_not_vision_capable`.
- Yardly device telemetry question can call technical peers but final answer remains Yardly-appropriate.

## Critical Path

### Phase 0: Lock the plan and contracts

- Add this plan as the authoritative MVP doc.
- Add a short note to older docs/ADRs that Responses and A2A deferral is superseded by this plan.
- Lock protocol acceptance tests before implementation.
- Confirm the exact Microsoft Agent Framework/A2A package versions available to the solution.

### Phase 1: Protocol foundation

- Refactor OpenAI wire DTOs into shared protocol models.
- Add content parts and image support.
- Add Chat Completions streaming `delta.tool_calls`.
- Add OpenAI-style error helpers.
- Add test fixtures for OpenCode, DevUI, OpenAI SDK shape validation, and local provider simulation.

### Phase 2: Persistence and identity

- Add EF Core SQLite persistence.
- Add migrations.
- Add virtual key middleware.
- Add tenant/key policy.
- Add sessions/messages/conversations/responses/A2A task state.
- Add spend ledger write path.

### Phase 3: Responses and Conversations

- Add `/v1/responses`.
- Add `/v1/conversations`.
- Add response streaming event model.
- Add background/cancel semantics.
- Add input token estimate endpoint.
- Add compaction endpoint backed by existing context compaction or a clear local implementation.

### Phase 4: Agent Framework registry

- Add `Blaze.LlmGateway.Agents`.
- Add `IAgentRegistry`, `IAgentAdapter`, `AgentEvent`, and profile/workflow builders.
- Wrap existing router as `codebrewRouter`.
- Add `AgentThread` persistence bridge.
- Route Chat Completions and Responses through the registry.

### Phase 5: Full A2A v1

- Add A2A server registration for each public virtual agent.
- Map HTTP+JSON and JSON-RPC bindings where supported.
- Add well-known agent card.
- Add task store and artifact store.
- Add A2A streaming.
- Add remote `A2AAgent` consumption for configured peer agents.

### Phase 6: Specialized virtual models

- Add `codebrewPlanner`.
- Add `codebrewSharpClient`.
- Add `codebrewCouncil`.
- Harden existing `yardly`.
- Add language affinity persistence.
- Add capability-aware routing gates.

### Phase 7: MCP, skills, and asset catalog

- Make MCP profile-scoped.
- Add Microsoft Learn MCP for `codebrewSharpClient`.
- Add asset catalog.
- Add local agent/skill ingestion.
- Add curated Awesome Copilot sync.
- Add activation/provenance tracking.

### Phase 8: Spend, telemetry, and policy hardening

- Complete spend aggregation.
- Add `/admin/spend`.
- Add recent route diagnostics.
- Add OpenTelemetry spans.
- Ensure every new route uses `[ROUTER-*]` and `[AGENT-*]` logging tags.
- Add cloud-deny and tool-deny tests.

### Phase 9: DevUI, OpenCode, Yardly, and smoke tests

- Aspire DevUI can chat with all virtual models.
- OpenCode BYOK works through `/v1`.
- Responses client smoke test passes.
- A2A client smoke test passes.
- MAUI-safe Yardly registration sample works.
- Hidden reasoning/control-token regression tests pass.

## Acceptance Gates

1. **OpenAI Chat compatibility:** OpenCode with `base_url=.../v1`, `model=codebrewRouter`, and `api_key=sk-cbr-...` works streaming and non-streaming, including tools and `[DONE]`.
2. **Responses compatibility:** Responses create, stream, retrieve, delete, cancel, input items, input tokens, compact, and conversation linkage work.
3. **Conversations compatibility:** Conversation create/retrieve/update/delete and item list/create/retrieve/delete work with persisted state.
4. **A2A v1 compatibility:** Agent cards, HTTP+JSON, JSON-RPC where supported, streaming, task lifecycle, cancellation, artifacts, subscriptions, and restart recovery work.
5. **Multi-tenant auth + spend:** Two API keys for two tenants issue requests; spend ledger attributes tokens/cost per key; `/admin/spend?key_id=...` returns accurate totals.
6. **Cloud policy:** `cloud_allowed=false` blocks cloud models/council fanout/planner escalation; `cloud_allowed=true` allows configured providers. Decisions are logged.
7. **Council demo:** `model=codebrewCouncil` issues concurrent member calls plus arbiter call, returns one OpenAI-shaped streaming response, and records spend/spans for every call.
8. **MCP path:** `codebrewSharpClient` can call Microsoft Learn MCP when allowed; invocation is persisted in `ToolCalls`.
9. **Awesome Copilot path:** `codebrewSharpClient` activates relevant C#/.NET assets with provenance and skips unavailable-tool assets cleanly.
10. **Vision path:** `yardly` accepts text+image content and routes only to vision-capable providers.
11. **Language affinity:** C#/.NET prompts route to `codebrewSharpClient`; ambiguous later prompts reuse developer+repo preference.
12. **Planner routing:** Planning prompts route to `codebrewPlanner`; specialist models can be peer agents but planner stays lead.
13. **Persistence/recovery:** Restart preserves sessions, AgentThread continuity, responses, conversations, A2A tasks, artifacts, memory, and spend.
14. **Output quality:** DevUI responses are normal chat responses, not verbose hidden-reasoning blobs.
15. **Build + tests:** `dotnet build -warnaserror` is clean; `dotnet test` passes; new code targets at least 95% coverage where practical; logging-contract tests pass.

## LiteLLM Mapping

| LiteLLM feature | CodebrewRouter MVP | Notes |
|---|---|---|
| `/v1/chat/completions` proxy | Adopt | OpenAI-spec compatible, streaming, tools, multimodal |
| `/v1/models` | Adopt | Lists virtual models with capability metadata |
| `model_list` aliases/config | Adopt shape | Provider/model catalog plus virtual profiles |
| Virtual API keys | Adopt | Multi-tenant from day one |
| Per-key spend tracking | Adopt | `SpendLedger` |
| Fallbacks | Adopt and extend | Capability-aware and workflow-aware |
| Rate limiting | Minimal policy hooks | Full RPM/TPM can follow after spend ledger |
| Caching | Not MVP-critical | Avoid until correctness is stable |
| Admin UI | JSON only | UI can follow |
| Guardrails / PII | Policy hooks only | Full redaction can follow |
| Full Responses API | Adopt | Required MVP surface |
| Full A2A protocol | Adopt | Required MVP surface |
| Agent composition/workflows | Invent/extend | Key differentiator |
| Council mode | Invent/extend | Concurrent workflow plus arbiter |
| MCP-native tools | Invent/extend | Profile-scoped and RBAC-gated |
| AgentThread memory | Invent/extend | Backed by persistence |

## Review Notes From Current Docs

- `Docs/design/adr/0003-northbound-api-surface.md` and related architecture notes still say Responses/A2A are deferred. This MVP plan supersedes that.
- `Docs/superpowers/specs/2026-05-13-opencode-agent-framework-compatibility-design.md` correctly identifies content parts, tool streaming, and client-owned tool pass-through as blockers. These are MVP requirements now.
- `Docs/summary/summary.md` calls vision DTO support the Yardly blocker. This plan keeps multimodal content parts on the critical path.
- `Docs/research/a2aproject-a2a-dotnet-and-put-in-research-folder-a.md` confirms A2A is task/artifact/stateful, not just chat. This plan includes durable A2A tasks and artifacts.
- Current Microsoft Agent Framework docs show A2A v1 can be hosted with `AddA2AServer`, `MapA2AHttpJson`, `MapA2AJsonRpc`, and `MapWellKnownAgentCard`; use those patterns when package versions match.
- Current OpenAI docs include Responses retrieval, deletion, cancellation, input-items listing, input-token counting, compaction, and Conversations. These are included as MVP surfaces.

## Squad Execution Path

Use the existing fleet after this plan is accepted for implementation:

```text
/orchestrate --prd Docs/agents/codebrewrouter-agentic-mvp-plan.md
```

Recommended execution split:

- Protocol worker: Chat Completions, Responses, Conversations DTOs/events.
- Persistence worker: EF Core schema, migrations, stores.
- Agent worker: Agent registry, Agent Framework adapters, virtual models.
- A2A worker: A2A server, task store, artifact store, cards.
- Tooling worker: MCP, skills, Awesome Copilot asset catalog.
- Policy worker: auth, cloud egress, spend ledger.
- Verification worker: OpenCode, DevUI, A2A, Responses, Yardly, logging-contract tests.

## References

- [OpenAI Responses API](https://platform.openai.com/docs/api-reference/responses)
- [OpenAI Conversations API](https://platform.openai.com/docs/api-reference/conversations)
- [OpenAI Chat Completions API](https://platform.openai.com/docs/api-reference/chat)
- [OpenCode providers](https://opencode.ai/docs/providers)
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)
- [Microsoft Agent Framework A2A Agent](https://learn.microsoft.com/en-us/agent-framework/agents/providers/agent-to-agent)
- [Microsoft Agent Framework A2A v1 hosting announcement](https://devblogs.microsoft.com/agent-framework/a2a-v1-is-here-cross-platform-agent-communication-in-microsoft-agent-framework-for-net/)
- [A2A Protocol](https://a2a-protocol.org/)
- [MCP Specification](https://modelcontextprotocol.io/)
- [Awesome Copilot](https://github.com/github/awesome-copilot)
- [Awesome Copilot llms.txt](https://awesome-copilot.github.com/llms.txt)
- ADR-0008 cloud egress policy
- ADR-0009 squad orchestration
- ADR-0010 parallel orchestration path
