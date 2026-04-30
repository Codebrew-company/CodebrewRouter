# Blaze.LlmGateway agent-platform planning draft

## Plan artifact locations

- **Session source of truth:** `C:\Users\peterab\.copilot\session-state\470dcfde-dcda-409f-8bd6-f82d5a7009f5\plan.md`
- **Repo copy:** `E:\src\CodebrewRouter\plan\llm-agent-platform-plan.md`

## Problem statement

Evolve Blaze.LlmGateway from a routed `Microsoft.Extensions.AI` chat gateway into an internal-LAN LLM engine that can power chatbots and personal-agent projects. The target platform should combine:

- Local inference endpoints on the LAN: Ollama, Foundry Local, and LM Studio, including Gemma 4 family models.
- Online/cloud providers when policy allows: Azure Foundry, Claude, Codex/OpenAI, Gemini, GitHub Copilot, GitHub Models, OpenRouter.
- Agent/runtime integrations above the inference layer: Microsoft Agent Framework, Azure Foundry hosted/local agents, and Copilot ecosystem consumers.
- Northbound API compatibility for clients like OpenCode, Copilot CLI BYOM, Claude Code, Codex, and internal apps.

## Current state summary

The repository already has a strong inference-gateway foundation:

- `Blaze.LlmGateway.Infrastructure\InfrastructureServiceExtensions.cs`
  - Registers nine keyed `IChatClient` providers.
  - Builds the current MEAI pipeline: `McpToolDelegatingClient -> LlmRoutingChatClient -> keyed provider .UseFunctionInvocation()`.
- `Blaze.LlmGateway.Infrastructure\LlmRoutingChatClient.cs`
  - Resolves a provider via `IRoutingStrategy`, but still implements `IChatClient` directly.
- `Blaze.LlmGateway.Infrastructure\RoutingStrategies\OllamaMetaRoutingStrategy.cs`
  - Uses an Ollama router model plus keyword fallback.
- `Blaze.LlmGateway.Infrastructure\McpConnectionManager.cs`
  - MCP connection and tool caching scaffold exists, but production behavior is incomplete.
- `Blaze.LlmGateway.Api\Program.cs`
  - Exposes a minimal OpenAI-compatible chat completions SSE endpoint with manual JSON parsing.
- `Blaze.LlmGateway.AppHost\Program.cs`
  - Already provisions Foundry Local, GitHub Models, and a local Ollama container.
- `Blaze.LlmGateway.Tests\LlmRoutingChatClientTests.cs`
  - Covers basic routing/fallback behavior.
- `Blaze.LlmGateway.Tests\OllamaMetaRoutingStrategyTests.cs`
  - Covers router parsing and fallback behavior.

Main gaps versus the target:

- No real agent/session runtime yet.
- Enum-based destination model will not scale well to a larger local/cloud model catalog.
- API contract is too thin for serious BYOM/client compatibility.
- MCP tool plane is scaffold-only.
- No circuit breaker, provider health model, or streaming failover policy.
- No first-class LM Studio support.
- No Copilot SDK or Microsoft Agent Framework integration layer yet.

## Confirmed planning choices

- **Product boundary:** full gateway and agent runtime in one primary API host.
- **Phase 1 client surfaces:** OpenAI-compatible Chat Completions API, Copilot CLI BYOM usage, Copilot SDK integration sample, Microsoft Agent Framework integration, Azure Foundry hosted/local agent integration.
- **Session model:** durable persisted sessions first.
- **Local runtimes to prioritize first:** Ollama, Foundry Local, and LM Studio.
- **Cloud escalation policy:** local-first with explicit allow rules for cloud.

## Planning principles

1. Preserve Blaze.LlmGateway as the inference and tool-routing core even though the primary host will also own the agent runtime.
2. Keep clear internal layering between inference plane, tool plane, agent plane, and integration plane inside the same primary API host.
3. Prefer config- and capability-driven routing over growing the `RouteDestination` enum indefinitely.
4. Make local-first and LAN-aware execution a first-class policy, with explicit cloud allow rules.
5. Keep `Microsoft.Extensions.AI` as the core abstraction for model execution and streaming.
6. Design the host so Copilot SDK, Copilot CLI BYOM, Agent Framework, and Azure Foundry agents can all consume the same core engine without duplicating provider logic.

## Proposed north-star architecture

Split the platform into four internal planes within the same primary API host:

1. **Inference plane**
   - Provider registry
   - Model capability catalog
   - Routing/failover/circuit breaking
   - Streaming request execution

2. **Tool plane**
   - MCP server lifecycle
   - Tool registry and policy
   - Tool filtering by provider/model/client policy

3. **Agent plane**
   - Session and memory model
   - Workflow orchestration
   - Personal-agent abstractions
   - Adapters for Microsoft Agent Framework and Azure Foundry agents

4. **Integration plane**
   - OpenAI-compatible API
   - Copilot SDK and Copilot CLI BYOM integration guidance
   - Azure Foundry hosted/local agent adapters
   - Internal sample hosts and reference clients
   - Optional Responses/A2A-style surfaces later

Durable session persistence is part of the initial architecture, not a later enhancement. The host should be designed so routed chat requests, orchestrated agent runs, tool invocations, and long-lived sessions all share a consistent persistence and observability model.

## Context map

### Files to modify

| File | Purpose | Changes needed |
| --- | --- | --- |
| `Blaze.LlmGateway.Infrastructure\InfrastructureServiceExtensions.cs` | DI registration for providers and pipeline | Replace enum-centric bootstrap with provider/catalog-aware registration and future middleware wiring |
| `Blaze.LlmGateway.Infrastructure\LlmRoutingChatClient.cs` | Core routing middleware | Refactor to `DelegatingChatClient`; add richer provider resolution and retry/failover hooks |
| `Blaze.LlmGateway.Infrastructure\RoutingStrategies\OllamaMetaRoutingStrategy.cs` | Current semantic router | Evolve from destination-name routing toward capability/profile-based routing |
| `Blaze.LlmGateway.Infrastructure\McpToolDelegatingClient.cs` | Tool injection middleware | Finish MCP tool mapping and tool policy/filtering |
| `Blaze.LlmGateway.Infrastructure\McpConnectionManager.cs` | MCP connection lifecycle | Add robust connection management, config binding, health, and reconnection |
| `Blaze.LlmGateway.Core\Configuration\LlmGatewayOptions.cs` | Configuration model | Expand into provider descriptors, model profiles, routing policy, and local/cloud topology |
| `Blaze.LlmGateway.Core\RouteDestination.cs` | Current provider identity model | Likely replace or reduce in favor of config-driven provider IDs and capabilities |
| `Blaze.LlmGateway.Api\Program.cs` | Northbound API host | Move from manual JSON parsing to explicit contracts and richer compatibility behavior |
| `Blaze.LlmGateway.AppHost\Program.cs` | Dev orchestration | Add LAN-local runtime topology for new local servers and agent-host projects |
| `Blaze.LlmGateway.Web\Program.cs` and future web components | UI host | Optional later consumer for demo/admin/chat tooling |
| `Blaze.LlmGateway.Tests\*` | Existing unit tests | Expand toward streaming, failover, MCP, provider catalog, and API contract coverage |

### Dependencies (may need updates)

| File | Relationship |
| --- | --- |
| `Blaze.LlmGateway.Api\Program.cs` | Depends on infrastructure DI and eventual API DTO surface |
| `Blaze.LlmGateway.AppHost\Program.cs` | Depends on provider/configuration model and any new integration projects |
| `Blaze.LlmGateway.Tests\LlmRoutingChatClientTests.cs` | Covers current router behavior and will need updates after middleware refactor |
| `Blaze.LlmGateway.Tests\OllamaMetaRoutingStrategyTests.cs` | Covers current destination parsing and will need updates after routing model changes |
| `README.md` | Documents the product boundary and supported providers/surfaces |

### Test files

| Test | Coverage |
| --- | --- |
| `Blaze.LlmGateway.Tests\LlmRoutingChatClientTests.cs` | Provider routing and fallback selection |
| `Blaze.LlmGateway.Tests\OllamaMetaRoutingStrategyTests.cs` | Router response parsing and fallback behavior |

### Reference patterns

| File | Pattern |
| --- | --- |
| `research\github-copilot-sdk.md` | Copilot SDK is a CLI-backed orchestration/runtime surface, best treated as a consumer/integration layer |
| `research\https-github-com-microsoft-agent-framework.md` | Agent Framework can sit above `IChatClient` as an agent/workflow layer rather than replacing the gateway |
| `research\https-github-com-microsoft-foundry-local.md` | Foundry Local supports both embedded and OpenAI-compatible web-service modes; web-service mode aligns well with the current gateway |
| `research\https-github-com-microsoft-agent-framework-samples.md` | Production-shaped examples for workflows, Foundry Local, AG-UI/DevUI, and hosted agent scenarios |

### Risk assessment

- [x] Breaking changes to public API
- [ ] Database migrations needed
- [x] Configuration changes required

## Proposed epics and feature groups

### Phase 0 - Architecture decisions and product boundary

Goal: decide what Blaze.LlmGateway is and is not before implementation begins.

- Record the confirmed decision that the primary API host owns both gateway and agent runtime.
- Define the internal layer and project boundaries inside the solution.
- Record the phase 1 client surfaces and durable session requirement.
- Decide whether provider identity remains enum-based.
- Decide the persistence substrate for durable sessions, agent state, and auditability.

### Phase 1 - Gateway hardening

Goal: make the existing gateway safe as the platform core.

- Refactor router middleware to `DelegatingChatClient`.
- Add provider health, circuit breaking, and fallback chains.
- Define streaming failover behavior.
- Harden `/v1/chat/completions` contract.
- Complete MCP tool plane basics.
- Add durable request/session correlation hooks needed by later agent-runtime work.

### Phase 2 - Provider/model catalog

Goal: support more providers and local runtimes without architectural sprawl.

- Add config-driven provider descriptors and model capability metadata.
- Support LM Studio as a first-class local endpoint.
- Model Gemma 4 variants as catalog entries, not enum values.
- Add LAN-locality policy and cloud escalation policy.
- Define cloud providers as explicit allow-listed capability routes, not the default path.

### Phase 3 - Agent integration layer

Goal: add real agent scenarios inside the primary host without collapsing architectural boundaries.

- Introduce an agent integration project/layer, likely `Blaze.LlmGateway.Agents` or `Blaze.LlmGateway.Integrations`, that is hosted by the same primary API host.
- Add Microsoft Agent Framework host adapters.
- Add Azure Foundry hosted/local agent adapters.
- Define session, memory, and tool boundary contracts between gateway core and agent runtime.
- Implement durable persisted sessions first.

#### Phase 3 sub-epic — Squad orchestration surface (development-time)

Parallel sub-epic scoped to the *development-time* agent surface (as opposed to the *runtime* agent plane above). Delivers a dual-target (Copilot CLI + Claude Code) Claude-powered development squad authored under `prompts/squad/` and materialized by `scripts/sync-squad.ps1`. Zero .NET code; markdown + JSON + one PowerShell script.

- See [squad-orchestration-plan.md](./squad-orchestration-plan.md) for the tactical task list, file inventory, sync contract, and verification checklist.
- Governing decision: [ADR-0009](../design/adr/0009-squad-orchestration.md).
- Unblocks the gaps called out in `CLAUDE.md` §"Known Incomplete Areas" (circuit breaker, streaming failover, `HostedMcpServerTool` mapping, Blazor wiring) by giving contributors an orchestrated multi-agent workflow.

### Phase 4 - Client ecosystem integration

Goal: make the platform easy to consume.

- Document and sample OpenAI-compatible BYOM usage for Copilot CLI, Claude Code, Codex, and internal apps.
- Add optional Copilot SDK sample integration.
- Add phase 1 guidance and validation for Copilot CLI BYOM usage.
- Decide whether to add Responses/A2A-style APIs after chat-completions hardening.
- Add internal reference apps for chatbot/personal-agent patterns.

### Phase 5 - Security, policy, and operational readiness

Goal: make the platform governable on an internal LAN.

- Add client authentication and authorization model.
- Add provider allow/deny and cloud-escalation policy.
- Add rate limiting, usage/cost telemetry, and audit trails.
- Add integration tests, benchmarks, and health checks.

## Critical ADRs required before implementation

1. **Primary host boundary ADR**
   - Record the confirmed choice: gateway core plus agent runtime in one primary API host.
   - Define the internal layer boundaries so the host does not become one undifferentiated monolith.

2. **Provider identity ADR**
   - Replace or augment `RouteDestination` with provider IDs plus capability metadata.

3. **Northbound API ADR**
   - Chat Completions only first, or Chat Completions plus Responses/A2A later?

4. **Session state ADR**
   - Durable persisted sessions first.
   - Decide storage model, retention, recovery semantics, and correlation identifiers.

5. **Local runtime compatibility ADR**
   - Treat LM Studio as an OpenAI-compatible endpoint or add a specialized adapter?

6. **Azure Foundry agents ADR**
   - Route hosted agents through the same execution path, or expose them as a distinct agent abstraction?

7. **Copilot ecosystem ADR**
   - Support Copilot as OpenAI-compatible BYOM consumer plus Copilot SDK integration sample in phase 1.
   - Decide whether plugin/MCP packaging is phase 1 or later.

8. **Cloud escalation ADR**
   - Define when local requests may use Claude/Codex/Gemini/Azure Foundry or other external models.
   - Confirm explicit allow-list policy and which clients may request escalation.

## Suggested planning deliverables before code

1. North-star architecture document for the four-plane model.
2. ADR set for the eight decisions above.
3. Feature requirements/spec package for the first three epics:
   - gateway hardening
   - provider/model catalog
   - agent integration layer
4. Internal deployment topology document:
   - two local LLM servers
   - primary API host with embedded agent runtime
   - trust/auth boundaries on the LAN
5. Client compatibility matrix:
   - Copilot CLI BYOM
   - Copilot SDK
   - Claude Code
   - Codex/OpenCode
   - internal apps

## Remaining open questions

These do not block the planning direction, but they will shape the first specs and ADRs:

- Which persistence technology should back durable sessions in phase 1?
- Should Claude/Codex connect directly through dedicated providers, or initially through OpenAI-compatible/OpenRouter-style compatibility layers where needed?
- Is plugin/MCP packaging for Copilot a phase 1 deliverable or a follow-on deliverable after API hardening?
- Do we need a separate northbound API for durable agent sessions in phase 1, or should durable sessions start behind the chat-completions surface plus internal APIs?

## Superseded provisional assumptions

These assumptions were validated and replaced by confirmed choices:

- Internal LAN only does **not** mean no auth; basic client identity and policy still matter.
- The first release should stay streaming-first and avoid adding every OpenAI/Responses feature immediately.
- The primary host owns both gateway and agent runtime, but the implementation should still preserve internal layering.
- Local-first routing is the default, with cloud escalation gated by policy.

## Execution-oriented todos

- Build a formal requirements/spec package for the north-star product boundary.
- Draft ADRs for product boundary, provider identity, API surface, and session model first.
- Turn local runtime support into a provider/model catalog design.
- Define the first implementation slice for gateway hardening before any agent-runtime work.
