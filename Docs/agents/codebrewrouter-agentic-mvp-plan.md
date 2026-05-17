# CodebrewRouter Agentic MVP Completion Plan

## Summary

This is the authoritative MVP plan for CodebrewRouter as an agentic LLM gateway. It supersedes older planning notes that defer Responses or A2A. The MVP is protocol-first and agentic: Chat Completions, Responses, A2A, Microsoft Agent Framework, MCP, skills, memory, virtual models, and routing intelligence ship together.

No LiteLLM runtime is used. CodebrewRouter implements the normalization, routing, agent, and protocol layers in C#/.NET.

## MVP Architecture

### General Chat: `codebrewRouter`

- Uses the built-in local Gemma 4 routing/general model.
- Handles ordinary chat, simple Q&A, lightweight routing, and fallback decisions.
- Must never leak hidden thinking, planning traces, or control tokens.

### Planning: `codebrewPlanner`

- Dedicated planning router for coding plans, Yardly plans, architecture, PRDs, acceptance criteria, and multi-agent execution strategy.
- Uses a high-reasoning model such as `gpt-5.5` when cloud policy allows.
- Uses planning skills such as Superpowers-style brainstorming, writing-plans, TDD, verification, and agent orchestration.
- Triggered by explicit model/profile request or automatic planning-intent detection.

### C#/.NET Juggernaut: `codebrewSharpClient`

- Extends `codebrewRouter`.
- Specializes in C#, .NET, MAUI, Aspire, Azure, Microsoft Agent Framework, MCP, EF, Blazor, and backend engineering.
- Uses Microsoft Learn MCP when allowed.
- Syncs and indexes local repo agents/skills plus curated Awesome Copilot agents, skills, prompts, instructions, and plugins.
- Activates relevant assets selectively; it must not dump all prompts/skills into context.

### Yardly: `yardly`

- Extends the same routing platform for plant identification, disease detection, care guidance, local/mobile operation, and long-running user memory.
- Supports image-capable requests through OpenAI-compatible content parts and provider capability matching.
- Uses Yardly-specific memory/RAG and can delegate technical subquestions to specialist profiles while keeping Yardly as final answer owner.

## Required Public Surfaces

### OpenAI-Compatible APIs

- `GET /v1/models`
- `POST /v1/completions`
- `POST /v1/chat/completions`
- `POST /v1/responses`
- `GET /v1/responses/{response_id}`
- `DELETE /v1/responses/{response_id}`
- `POST /v1/responses/{response_id}/cancel`
- `GET /v1/responses/{response_id}/input_items`
- Conversation endpoints needed for Responses state:
  - Create conversation
  - Retrieve conversation
  - Update conversation
  - Delete conversation
  - List conversation items
  - Create conversation item
  - Retrieve conversation item
  - Delete conversation item

### A2A APIs

- `/.well-known/agent-card.json`
- JSON-RPC message send and streaming send.
- HTTP/REST bindings where supported by the selected .NET A2A SDK.
- Task get, list, cancel, subscribe, artifacts, and status lifecycle.
- Push notification config supported only with signed webhook settings; disabled by default.

### Dev and Test Surfaces

- Aspire DevUI chat against `codebrewRouter`, `codebrewSharpClient`, `codebrewPlanner`, and `yardly`.
- OpenCode-compatible BYOK base URL.
- Microsoft Agent Framework `ChatClientAgent` and `AgentThread` integration.
- MAUI-safe registration sample for Yardly.

## Implementation Workstreams

### Protocol Compatibility

- Replace text-only DTO assumptions with shared content-part models for text, images, tool calls, tool results, function call arguments, artifacts, and streaming deltas.
- Preserve OpenAI Chat Completions tool semantics, including `tool_calls`, `tool_call_id`, tool-role messages, `response_format`, structured output, and streaming `delta.tool_calls`.
- Implement Responses state, input/output items, streaming events, cancellation, retrieval, deletion, and conversation linkage.
- Normalize errors to OpenAI-style error payloads.

### A2A and Agent Runtime

- Add `Blaze.LlmGateway.Agents`.
- Add `IAgentRegistry`, `IAgentAdapter`, `AgentEvent`, `CodebrewRouterA2AAgentHandler`, and durable task store.
- Use Microsoft Agent Framework as the primary local agent runtime.
- Bridge A2A messages/tasks to internal agent events and durable sessions.

### Persistence, Memory, and Preferences

- Add SQLite-backed persistence for responses, conversations, sessions, messages, tool calls, A2A tasks, artifacts, agent runs, memory, and developer preferences.
- Save language/model affinity by developer + repo scope.
- Save successful routing decisions, useful skills, selected MCP servers, and planning decisions.

### Routing and Model Profiles

- Add config-driven provider/model catalog with capability metadata.
- Keep existing keyed clients as a compatibility bridge.
- Support Microsoft Foundry, Google AI API, OpenCode Go, OpenRouter, Ollama, LM Studio, vLLM, Foundry Local, and other OpenAI-compatible local runtimes.
- Use Gemma 4 as the default lightweight routing brain.
- Use `codebrewPlanner` for advanced planning, not Gemma 4.
- Route C#/.NET prompts to `codebrewSharpClient`; if no language-specific model exists, ask `codebrewRouter` to choose the best available model and persist the choice.

### MCP, Skills, and Awesome Copilot Ingestion

- Make MCP profile-scoped and RBAC-gated.
- Separate client-owned tools from gateway-owned MCP tools.
- Add Microsoft Learn MCP to `codebrewSharpClient`.
- Add an asset catalog for agents, skills, prompts, instructions, plugins, source URL, license, hash, tags, required tools, and activation rules.
- Sync local `.github/agents`, `.github/plugins`, `.agents/skills`, `.opencode/agents`, and selected Awesome Copilot assets.
- Skip assets with unavailable tools and log why.

### Observability and Safety

- Use existing `[ROUTER-*]` and `[AGENT-*]` tags through `RouterLog.Write(...)`.
- Add OpenTelemetry spans across protocol endpoints, routing, provider calls, MCP calls, agent runs, A2A tasks, and persistence.
- Enforce default-deny cloud routing unless client identity and model profile policy allow it.
- Add output cleanup for LocalGemma/control-token leaks.

## Success Criteria

- OpenCode can use CodebrewRouter as a normal OpenAI-compatible model with chat, streaming, tools, and structured output.
- DevUI chat produces normal concise responses, not hidden reasoning blobs.
- Responses API works for create, stream, retrieve, cancel, delete, input items, and conversation state.
- A2A works for discovery, message send, streaming send, task lifecycle, artifacts, cancellation, subscription, and restart recovery.
- `codebrewRouter` handles general chat through Gemma 4.
- `codebrewPlanner` handles planning prompts and can use planning skills.
- `codebrewSharpClient` acts as the C#/.NET juggernaut with Microsoft Learn MCP and curated Awesome Copilot assets.
- Language affinity learns developer + repo preferences and reuses them.
- Yardly can accept plant-care text and image-capable requests through the same protocol stack.
- Cloud escalation is denied unless explicitly allowed.
- All new behavior is covered by tests and logging-contract checks.

## Verification

- Run build with warnings as errors.
- Run all unit and integration tests.
- Add protocol tests for Chat Completions, Responses, A2A, streaming, tools, content parts, and persistence.
- Add DevUI/OpenCode smoke tests.
- Add regression tests proving hidden thinking/control tokens are stripped.
- Add C# routing tests for `codebrewSharpClient`.
- Add planning routing tests for `codebrewPlanner`.
- Add Awesome Copilot ingestion tests for provenance, activation, and skipped tool requirements.

## References

- [OpenAI Responses API](https://platform.openai.com/docs/api-reference/responses)
- [OpenAI Conversations API](https://platform.openai.com/docs/api-reference/conversations)
- [OpenCode providers](https://opencode.ai/docs/providers)
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)
- [Microsoft Agent Framework memory](https://learn.microsoft.com/en-us/agent-framework/get-started/memory)
- [Awesome Copilot repo](https://github.com/github/awesome-copilot)
- [Awesome Copilot llms.txt](https://awesome-copilot.github.com/llms.txt)
