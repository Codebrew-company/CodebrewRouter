# CodebrewRouter Collaborative Virtual Model Network Design

> **Date:** 2026-05-15  
> **Status:** Design draft  
> **Owner:** User + Codex  
> **Scope:** `Blaze.LlmGateway` / CodebrewRouter virtual models, routing, agents, skills, MCP, and RAG  
> **Reference:** Hugging Face community article, ["Best Open-Source LLM Models in 2026: Coding, Local, Agentic AI, Benchmarks, and License"](https://huggingface.co/blog/daya-shankar/open-source-llms), published 2025-11-13  

---

## Executive Summary

CodebrewRouter should evolve from a single virtual model into a collaborative virtual model network.

The user-facing model list should continue to expose `codebrewRouter` as the broad, free-form model for general chat, coding, research, troubleshooting, and routing-heavy questions. In addition, the gateway should expose specialist virtual models such as `csharpRouter`, `pythonRouter`, `espRouter`, `reactRouter`, `cppRouter`, and domain models such as `yardly`.

Each visible model is not merely a provider alias. It is an agent profile:

- A model id in `/v1/models`
- A system prompt
- A routing policy
- A set of allowed peer profiles
- Optional RAG collections
- Optional MCP servers
- Optional skills or instruction packs
- A model selection policy based on language, context size, complexity, cost, privacy, and capability
- A lead/peer behavior contract for model-to-model collaboration

The core idea:

```text
User selects a visible virtual model.
That virtual model becomes the lead profile.
Gemma4 classifies the request and, when useful, proposes peer collaborators.
The lead profile calls peers for specialist analysis.
Peers return strict internal findings, not final user-facing answers.
The lead profile composes the final answer.
```

This keeps CodebrewRouter flexible while making specialist models useful and controllable.

---

## Current State

The current CodebrewRouter architecture already has important foundation pieces:

- `codebrewRouter` is configured as a virtual model.
- `yardly` is configured as a virtual model backed by CodebrewRouter.
- `CodebrewRouterChatClient` already performs task classification, prompt cleanup, token counting, provider fallback, and first-chunk streaming probes.
- `VirtualModelOptions` already provides a place for model-specific configuration such as `ModelId`, `Provider`, `OwnedBy`, `Source`, `SystemPrompt`, `FallbackRules`, and `ContextCompaction`.
- `ModelSelectionResolver` already resolves virtual model ids to the CodebrewRouter keyed client.
- `TaskType` currently classifies requests into coarse categories: `Coding`, `Reasoning`, `Research`, `VisionObjectDetection`, `Creative`, `DataAnalysis`, and `General`.
- The API already returns OpenAI-compatible chat completion responses and model-list responses.

The current system is task-aware but not profile-aware enough. It can know "this is coding", but it does not yet know:

- This is C#.
- This is Python.
- This is ESP-IDF C firmware.
- This is React.
- This is .NET MAUI iOS.
- This is a Yardly domain question that touches ESP devices.
- This needs Yardly RAG first, then an ESP specialist.
- This is small enough for a free or local model.
- This is large enough to require a long-context model.
- This is complex enough to justify an expensive frontier model.
- This should call another specialist profile before answering.

This design adds that missing layer.

---

## Problem Statement

CodebrewRouter needs to support several related but distinct goals:

1. Preserve `codebrewRouter` as a general-purpose free-form router.
2. Expose specialist language models in the model list so users can intentionally choose a focused assistant.
3. Allow those specialist models to use language-specific prompts, tools, skills, MCP servers, and RAG.
4. Let virtual models call each other automatically when useful.
5. Keep domain models such as `yardly` domain-specific rather than turning them into generic development assistants.
6. Still allow domain models to draw on language and device specialists when their domain work touches software, firmware, data ingestion, or connected devices.
7. Use Gemma4 as a low-cost local classification and routing brain.
8. Use open-weight model guidance from the Hugging Face reference article when choosing open/open-weight routes.
9. Escalate to closed frontier models such as GPT or Claude only when context size, complexity, platform risk, or quality requirements justify the cost.
10. Make all routing explainable, bounded, logged, testable, and configurable.

The challenge is that unbounded agent-to-agent calls can become recursive, expensive, slow, and difficult to debug. The system must therefore support collaboration without letting collaboration become chaos.

---

## Goals

- Keep `codebrewRouter` available as the default general model.
- Add selectable specialist virtual models for languages and domains.
- Use Gemma4 to classify requests into structured routing metadata.
- Support peer-to-peer virtual model collaboration.
- Preserve a single lead profile for the final user-facing answer.
- Allow profiles to attach RAG collections, skills, and MCP servers.
- Allow Yardly to use Yardly-specific RAG and delegate technical subquestions to ESP/Python/C# specialists.
- Make model routing data-driven through configuration.
- Prefer open/open-weight models where appropriate.
- Escalate to premium closed models for high complexity, large context, safety-sensitive work, or specialized platform depth.
- Add guardrails for call depth, call count, cycles, timeouts, confidence, and cost.
- Maintain OpenAI-compatible external API behavior.
- Keep implementation aligned with existing CodebrewRouter patterns.
- Provide enough observability to explain why each model was selected.

---

## Non-Goals

- Do not remove `codebrewRouter` or make it coding-only.
- Do not force every user request through many peer calls.
- Do not let Gemma4 directly choose arbitrary provider ids without validation.
- Do not make Yardly behave like a software development assistant to end users.
- Do not make every specialist model globally available to every other specialist without policy.
- Do not build a full autonomous multi-agent project executor in this phase.
- Do not require every profile to have RAG, MCP, and skills from day one.
- Do not replace existing provider fallback or context sizing behavior.
- Do not break OpenAI-compatible request or response shapes.

---

## Core Design Principle

The system should separate "understanding the work" from "choosing the provider".

Gemma4 should classify the request and propose collaboration labels. Configuration should choose the actual model/provider chain.

Good:

```json
{
  "taskType": "coding",
  "language": "csharp",
  "frameworks": ["dotnet-maui", "ios"],
  "complexity": "high",
  "contextSize": "large",
  "suggestedPeers": ["mauiIosRouter", "espRouter"]
}
```

Then config maps that to:

```json
[
  "OpenAI_Gpt55",
  "Anthropic_Claude47",
  "OpenAI_Gpt53Codex",
  "OpenCodeGo_KimiK2_6",
  "OpenCodeGo_GLM5_1"
]
```

Bad:

```json
{
  "useModel": "OpenAI_Gpt55"
}
```

Gemma4 is useful as a classifier and planning assistant. It should not be the final authority over provider keys, cost policy, security policy, RAG access, or delegation permissions.

---

## Conceptual Architecture

```text
Client
  |
  | POST /v1/chat/completions
  | model = codebrewRouter | csharpRouter | pythonRouter | espRouter | yardly | ...
  v
API endpoint
  |
  v
ModelSelectionResolver
  |
  | virtual model id
  v
CodebrewRouterChatClient
  |
  | 1. Resolve lead profile
  | 2. Cleanup prompt if enabled
  | 3. Count tokens
  | 4. Ask Gemma4RoutingAgent for structured classification
  | 5. Build collaboration plan
  | 6. Execute allowed peer calls
  | 7. Merge peer findings and RAG context
  | 8. Select provider chain for lead profile
  | 9. Execute final response with failover
  v
OpenAI-compatible response or SSE stream
```

The lead profile owns the final answer. Peer profiles supply internal evidence, code notes, domain notes, and risks.

---

## Key Terms

### Virtual Model

A model id exposed through `/v1/models` that users can select in their client.

Examples:

- `codebrewRouter`
- `csharpRouter`
- `pythonRouter`
- `espRouter`
- `reactRouter`
- `yardly`

### Agent Profile

The runtime configuration behind a virtual model. It defines behavior, routing, peers, RAG, skills, MCP, and safety limits.

Every agent profile is a virtual model, but not every provider model is an agent profile.

### Lead Profile

The profile selected by the user request. It owns the final answer.

If the user selects `yardly`, Yardly remains the lead even if Yardly calls `espRouter` internally.

### Peer Profile

A specialist profile called by the lead or by another peer. Peer profiles return structured internal findings. They do not write directly to the user.

### Routing Agent

The Gemma4-powered local classifier that produces structured routing metadata. It is not a free-form answer model in this design.

### Peer Plan

A bounded list of internal specialist calls to make before the final answer.

### RAG Collection

A searchable knowledge source attached to a profile, such as Yardly wiki pages, plant-care documents, ESP device manuals, Python ingestion docs, or project-specific notes.

### Tool Surface

The combined set of MCP tools, skills, and local capabilities available to a profile.

---

## Profile Types

### General Router Profile

Primary example: `codebrewRouter`.

Purpose:

- General chat
- Coding
- Research
- High-level troubleshooting
- Free-form routing
- Asking other profiles for help

Behavior:

- Broad but concise
- Can classify many request types
- Can call specialist profiles when useful
- Does not force a narrow language/domain persona

### Language Specialist Profile

Examples:

- `csharpRouter`
- `pythonRouter`
- `cRouter`
- `cppRouter`
- `javaRouter`
- `typescriptRouter`
- `javascriptRouter`
- `nodeRouter`
- `reactRouter`
- `objectiveCRouter`
- `swiftRouter`

Purpose:

- Focus deeply on a language or ecosystem
- Use language-specific system instructions
- Use language-specific RAG
- Use language-specific MCP tools
- Prefer language-appropriate model routes
- Collaborate with framework/platform specialists

### Platform Specialist Profile

Examples:

- `mauiIosRouter`
- `espRouter`
- `iosRouter`
- `androidRouter`
- `cloudflareRouter`
- `azureRouter`

Purpose:

- Focus on platform constraints, build systems, deployment, diagnostics, and platform-specific pitfalls.

### Framework Specialist Profile

Examples:

- `reactRouter`
- `nextRouter`
- `aspnetRouter`
- `swiftuiRouter`
- `pytestRouter`

Purpose:

- Focus on ecosystem conventions, architecture, performance, tests, and framework-specific tooling.

### Domain Specialist Profile

Examples:

- `yardly`
- `plantCareRouter`
- `deviceSupportRouter`
- `telemetryRouter`

Purpose:

- Answer within a product or business domain.
- Use domain RAG.
- Hide implementation complexity unless the user asks for it.
- Call software/firmware specialists as needed.

---

## High-Level Profile Inventory

### `codebrewRouter`

Role:

- General free-form router
- Default assistant
- Broad model selector

Allowed peers:

- All approved language specialists
- All approved domain specialists
- General research/data specialists

RAG:

- Optional global docs
- Optional project knowledge

Model routing:

- Open-weight first for most normal tasks
- Premium escalation for very large context, high complexity, platform risk, or user override

### `csharpRouter`

Role:

- C#/.NET specialist

Common peers:

- `mauiIosRouter`
- `aspnetRouter`
- `reactRouter`
- `sqlRouter`
- `espRouter`
- `pythonRouter`

RAG:

- .NET docs
- Project C# architecture notes
- NuGet/package guidance
- Internal style guides

MCP/tools:

- Filesystem
- Git
- Test runner
- NuGet/package lookup
- Build diagnostics

Model routing examples:

- Small tasks: Kimi K2.6, DeepSeek V4 Flash, LocalGemma
- Medium tasks: GPT-5.3-Codex, Kimi K2.6, GLM-5.1
- Large/high-complexity tasks: GPT-5.5, Claude 4.7, GPT-5.3-Codex

### `pythonRouter`

Role:

- Python specialist

Common peers:

- `espRouter`
- `dataRouter`
- `pytestRouter`
- `azureRouter`

RAG:

- Python project docs
- Test conventions
- Ingestion pipeline docs
- UART/telemetry host tooling docs

MCP/tools:

- Filesystem
- Git
- Python test runner
- Package metadata

### `espRouter`

Role:

- ESP-IDF, embedded C, firmware, UART, Wi-Fi provisioning, telemetry device specialist

Common peers:

- `cRouter`
- `pythonRouter`
- `yardly`
- `telemetryRouter`

RAG:

- ESP-IDF docs
- Project firmware docs
- UART protocol docs
- Device manuals
- Hardware notes

MCP/tools:

- Filesystem
- Git
- ESP-IDF build/test commands
- Serial/log inspection tools if available

### `reactRouter`

Role:

- React, ReactJS, TypeScript UI specialist

Common peers:

- `typescriptRouter`
- `nodeRouter`
- `csharpRouter`
- `designRouter`

RAG:

- Frontend architecture docs
- Component library docs
- UI guidelines

MCP/tools:

- Filesystem
- Git
- Browser testing
- Playwright
- Package metadata

### `yardly`

Role:

- Yard, plant, device support, and Yardly product/domain assistant

Important constraint:

- Yardly should not become a general development assistant.
- Yardly can call technical peers internally, but final answers should remain Yardly-domain appropriate.

Common peers:

- `espRouter`
- `pythonRouter`
- `csharpRouter`
- `telemetryRouter`
- `plantCareRouter`
- `deviceWikiAgent`

RAG:

- Yardly wiki
- Plant-care docs
- Device manuals
- Device troubleshooting docs
- Installation docs
- User-facing support scripts
- Known issue database

MCP/tools:

- Yardly knowledge search
- Device diagnostics if available
- Ticket/wiki lookup if available
- Telemetry lookup if available

Example behavior:

```text
User: My Yardly boundary device keeps dropping telemetry.

yardly:
  1. Search Yardly wiki for telemetry drop symptoms.
  2. Ask espRouter for firmware/UART/Wi-Fi causes.
  3. Ask pythonRouter for ingestion/decoder causes if host tooling is involved.
  4. Compose a customer-friendly Yardly answer with likely causes and safe next steps.
```

---

## Gemma4RoutingAgent

### Purpose

`Gemma4RoutingAgent` is the local classifier and collaboration planner.

It should answer:

- What kind of task is this?
- Is this coding?
- Which language is involved?
- Which frameworks/platforms are involved?
- Is this domain-specific?
- Which profile should lead?
- Which peers are useful?
- How complex is the task?
- How large is the context?
- Does this need local/private handling?
- Does this need long-context handling?
- Does this need premium escalation?

It should not:

- Produce the final user answer.
- Call external tools directly.
- Select arbitrary provider ids.
- Override policy.
- Invent new profiles.
- Decide to bypass safety and cost limits.

### Suggested Interface

```csharp
public interface IRoutingAgent
{
    Task<RoutingDecision> DecideAsync(
        RoutingRequest request,
        CancellationToken cancellationToken = default);
}
```

### Routing Request

```json
{
  "requestedModelId": "yardly",
  "lastUserMessage": "My Yardly sensor drops telemetry after rain. Is the ESP firmware involved?",
  "conversationSummary": "Optional compacted summary",
  "availableProfiles": ["codebrewRouter", "yardly", "espRouter", "pythonRouter"],
  "tokenCount": 7420,
  "hasImages": false,
  "hasTools": false
}
```

### Routing Decision

```json
{
  "schemaVersion": 1,
  "leadProfile": "yardly",
  "taskType": "domain_support",
  "isCoding": false,
  "language": "none",
  "frameworks": [],
  "platforms": ["esp32", "yardly-device"],
  "domain": "yardly",
  "operation": "troubleshoot",
  "complexity": "medium",
  "contextSize": "small",
  "privacy": "normal",
  "preferredModelFamily": "open_weight",
  "needsRag": true,
  "ragCollections": ["yardly-wiki", "yardly-device-manuals"],
  "suggestedPeers": [
    {
      "profile": "espRouter",
      "reason": "Assess ESP firmware, Wi-Fi, and UART telemetry failure modes."
    },
    {
      "profile": "pythonRouter",
      "reason": "Assess host ingestion or decoder causes if telemetry reaches gateway."
    }
  ],
  "confidence": 0.86,
  "notes": "Yardly remains the final answer owner."
}
```

### Routing Prompt Requirements

Gemma4 should be prompted to return strict JSON only.

The prompt should include:

- Allowed task types
- Allowed language labels
- Allowed framework labels
- Allowed platform labels
- Allowed profile ids
- Allowed complexity labels
- Allowed context-size labels
- Allowed model families
- Maximum number of peer suggestions
- Rule that the requested profile normally remains the lead
- Rule that peers are advisory only
- Rule that it must return `confidence`
- Rule that unknown values should be `"unknown"` or empty arrays rather than invented labels

### Classification Labels

Task types:

- `general`
- `coding`
- `debugging`
- `code_review`
- `architecture`
- `test_generation`
- `refactor`
- `domain_support`
- `device_support`
- `research`
- `data_analysis`
- `creative`
- `vision`

Languages:

- `csharp`
- `python`
- `c`
- `cpp`
- `java`
- `typescript`
- `javascript`
- `nodejs`
- `react`
- `objective_c`
- `swift`
- `sql`
- `html`
- `css`
- `shell`
- `powershell`
- `none`
- `unknown`

Platforms/frameworks:

- `dotnet`
- `dotnet-maui`
- `aspnet`
- `ios`
- `macos`
- `android`
- `esp-idf`
- `esp32`
- `esp32c6`
- `react`
- `reactjs`
- `nodejs`
- `nextjs`
- `azure`
- `cloudflare`
- `yardly-device`
- `uart`
- `wifi`
- `ble`

Complexity:

- `trivial`
- `small`
- `medium`
- `high`
- `critical`
- `unknown`

Context size:

- `tiny`
- `small`
- `medium`
- `large`
- `huge`
- `unknown`

Preferred model family:

- `local`
- `open_weight`
- `budget_api`
- `premium_closed`
- `long_context`
- `vision`
- `unknown`

---

## Lead and Peer Collaboration Contract

### Lead Profile Responsibilities

The lead profile:

- Owns the final answer.
- Chooses whether to ask peers, subject to policy.
- Merges peer findings.
- Applies its own system prompt and domain/persona.
- Applies its own RAG and tool policy.
- Removes internal routing details unless explicitly requested.
- Keeps the user-facing response coherent.

### Peer Profile Responsibilities

A peer profile:

- Answers a narrow internal question.
- Returns structured findings.
- Does not address the user directly.
- Does not recursively call peers unless allowed by depth policy.
- Does not override the lead profile.
- Does not include unnecessary prose.

### Peer Response Shape

```json
{
  "schemaVersion": 1,
  "peerProfile": "espRouter",
  "confidence": 0.82,
  "summary": "Telemetry drops after rain are likely caused by Wi-Fi instability, power, enclosure moisture, or UART framing loss.",
  "findings": [
    {
      "type": "likely_cause",
      "text": "Moisture can degrade antenna performance or cause brownouts."
    },
    {
      "type": "firmware_check",
      "text": "Check whether firmware logs Wi-Fi reconnects or UART read errors around drop windows."
    }
  ],
  "risks": [
    "Do not advise opening powered outdoor devices without safety guidance."
  ],
  "suggestedNextSteps": [
    "Check device enclosure seal.",
    "Correlate telemetry drops with Wi-Fi reconnect logs.",
    "Inspect ESP reset reason and RSSI history."
  ],
  "citations": [
    {
      "source": "yardly-device-manuals",
      "id": "telemetry-drop-troubleshooting"
    }
  ]
}
```

### Peer Call Prompt Pattern

```text
You are being called as an internal specialist profile.
You are not writing the final user answer.
Return strict JSON matching the peer response schema.
Focus only on your specialty.
If outside your scope, say so.
Do not ask the user questions unless a question is essential.
Prefer concrete findings, risks, and next steps.
```

---

## Peer Delegation Modes

### Mode 1: No Peer Call

Use when:

- The request is simple.
- The lead profile can answer confidently.
- Peer call cost would not improve quality.

Example:

```text
User selects csharpRouter:
"What is a record in C#?"
```

No peer needed.

### Mode 2: Single Peer Call

Use when:

- A request is clearly mostly in one domain but touches one specialist area.

Example:

```text
User selects yardly:
"Could firmware cause my moisture sensor to stop reporting?"
```

Lead: `yardly`  
Peer: `espRouter`

### Mode 3: Multiple Peer Calls

Use when:

- The request spans multiple technical domains.
- The lead needs independent opinions.
- The user asked for broad diagnosis.

Example:

```text
User selects csharpRouter:
"My .NET MAUI iOS app cannot connect to the Yardly ESP device, and Python telemetry logs look weird."
```

Lead: `csharpRouter`  
Peers: `mauiIosRouter`, `espRouter`, `pythonRouter`

### Mode 4: Peer Chain

Use sparingly.

Example:

```text
yardly -> espRouter -> cRouter
```

This should be allowed only when:

- `maxDepth` permits it.
- No cycle exists.
- The second-hop peer is more specialized.
- The call budget has room.

### Mode 5: Parallel Peer Fanout

Use when peer calls are independent.

Example:

```text
yardly calls plantCareRouter and espRouter in parallel.
```

The lead merges both outputs.

---

## Required Guardrails

### Maximum Peer Calls

Each profile should define `maxPeerCalls`.

Recommended defaults:

- `codebrewRouter`: 4
- language routers: 3
- Yardly: 4
- simple domain profiles: 2

### Maximum Depth

Recommended default: `2`.

Allowed:

```text
yardly -> espRouter
yardly -> espRouter -> cRouter
```

Not allowed by default:

```text
yardly -> espRouter -> cRouter -> codebrewRouter
```

### Cycle Detection

Every collaboration plan should carry a visited profile set.

Forbidden:

```text
csharpRouter -> espRouter -> csharpRouter
yardly -> espRouter -> yardly
```

### Timeouts

Recommended defaults:

- Routing decision timeout: 3 seconds
- RAG lookup timeout per collection: 2 seconds
- Peer call timeout: 10 seconds
- Total collaboration timeout: 30 seconds

If peer calls time out, the lead should answer with available context.

### Confidence Thresholds

If Gemma4 confidence is low:

- Do not fan out aggressively.
- Use default profile fallback rules.
- Prefer asking a clarifying question only when required.

Suggested behavior:

```text
confidence >= 0.80: use suggested peers if policy allows
confidence 0.50-0.79: use at most one obvious peer
confidence < 0.50: no peer call unless the requested profile has a fixed rule
```

### Cost Limits

Each profile should define:

- Maximum premium calls per request
- Whether premium closed models are allowed
- Whether open-weight models are preferred
- Whether local-only mode is required

### Privacy Limits

Profiles should define privacy modes:

- `normal`: external providers allowed according to route policy
- `private`: local or approved private providers only
- `restricted`: no peer calls to profiles with external tools/providers

### Domain Boundary

Domain profiles should keep final answers in-domain.

Yardly can use `espRouter`, but it should not answer like:

```text
Here is a CMake patch for your ESP-IDF component...
```

unless the user explicitly asks for developer-level details.

Instead, Yardly should answer like:

```text
The device may be losing connectivity or rebooting after moisture exposure. The firmware angle to check is whether the ESP logs reset reasons, Wi-Fi reconnects, or UART framing errors around the same time the readings disappear.
```

---

## Model Selection Strategy

### Inputs

Model selection should consider:

- Lead profile
- Task type
- Language
- Frameworks
- Platform
- Complexity
- Context size
- RAG size
- Privacy mode
- Requested output size
- Tool support needs
- Vision needs
- Cost preference
- Provider availability
- Historical provider health

### Output

Model selection returns an ordered provider chain:

```json
[
  "OpenCodeGo_KimiK2_6",
  "OpenCodeGo_GLM5_1",
  "OpenCodeGo_DeepSeekV4Pro",
  "LmStudio",
  "LocalGemma"
]
```

### Recommended Tiers

#### Tiny/Trivial

Best for:

- Simple explanations
- Syntax lookup
- Short code snippets
- Formatting help

Candidates:

- `LocalGemma`
- `LmStudio`
- small Qwen/Phi variants if configured
- budget API models

#### Small

Best for:

- One-file changes
- Short debugging
- Simple tests
- Small snippets

Candidates:

- `OpenCodeGo_DeepSeekV4Flash`
- `OpenCodeGo_KimiK2_6`
- `LocalGemma`
- `LmStudio`

#### Medium

Best for:

- Multi-file reasoning
- Moderate refactors
- Framework questions
- Code review

Candidates:

- `OpenAI_Gpt53Codex`
- `OpenCodeGo_KimiK2_6`
- `OpenCodeGo_GLM5_1`
- `OpenCodeGo_DeepSeekV4Pro`

#### Large

Best for:

- Large codebase context
- Multi-system debugging
- RAG-heavy requests
- Architecture analysis

Candidates:

- `OpenAI_Gpt55`
- `Anthropic_Claude47`
- `OpenAI_Gpt53Codex`
- `OpenCodeGo_DeepSeekV4Pro`
- long-context open-weight providers if configured

#### High Complexity / Critical

Best for:

- .NET MAUI iOS
- Native interop
- ESP firmware plus host tooling
- Security-sensitive code
- Production incident analysis
- Hard cross-platform debugging

Candidates:

- `OpenAI_Gpt55`
- `Anthropic_Claude47`
- `OpenAI_Gpt53Codex`
- `OpenCodeGo_KimiK2_6`
- `OpenCodeGo_GLM5_1`

The exact provider keys should be validated against real configured providers at implementation time.

---

## Open/Open-Weight Model Reference

The Hugging Face article is useful as a routing inspiration source, not as the only source of truth. It should seed initial model-family choices, while production configuration should verify official model cards, licenses, context windows, pricing, and provider availability.

### Model Guidance Extracted for Routing

The article frames open/open-weight model selection around practical factors:

- Task performance
- Developer fit
- Hardware reality
- License freedom
- Cost
- Benchmark trust level

This aligns well with CodebrewRouter routing, because CodebrewRouter should pick models based on practical fit rather than one global leaderboard.

### Suggested Open-Weight Routing Roles

| Model family | Routing role | Notes |
|--------------|--------------|-------|
| Kimi K2.6 | Strong open-weight coding and agentic work | Good candidate for high-quality open coding routes |
| GLM-5.1 | Long-horizon agentic coding | Good candidate for multi-step software engineering |
| DeepSeek V4 Pro | Hard coding, reasoning, long-context API value | Good candidate for difficult open-weight routes |
| DeepSeek V4 Flash | Budget API, high-volume tasks | Good candidate for simple and cheap routes |
| Qwen3 | Commercial-friendly open-weight lane | Good candidate for enterprise and multilingual profiles |
| Gemma 4 | Local-first, private, efficient router/helper | Good candidate for Gemma4RoutingAgent and local private routes |
| Phi-4 | Small local reasoning/coding helper | Good candidate for small local tasks |
| Llama 4 Scout | Huge-context open-weight analysis | Candidate for very large context when available |

### Licensing Note

The article explicitly warns that model rankings move quickly and that builders should check official model cards, license, and pricing before production deployment. CodebrewRouter should encode this as a normal model catalog maintenance process.

Recommended policy:

- Store license metadata in the provider/model catalog.
- Store commercial-use notes separately from routing quality.
- Let route policies filter models by license class when needed.
- Do not infer commercial safety from popularity.

---

## Configuration Design

### New Profile Configuration Shape

```jsonc
{
  "LlmGateway": {
    "VirtualModels": {
      "csharpRouter": {
        "Enabled": true,
        "ModelId": "csharpRouter",
        "Provider": "CodebrewRouter",
        "OwnedBy": "codebrew",
        "Source": "virtual",
        "ProfileKind": "language",
        "PrimaryLanguage": "csharp",
        "SystemPrompt": "You are a focused C# and .NET engineering assistant...",
        "RoutingPolicy": "coding-csharp",
        "RagCollections": ["dotnet-docs", "codebrew-csharp-notes"],
        "Skills": ["csharp", "dotnet", "maui"],
        "McpServers": ["filesystem", "git", "nuget"],
        "AllowedPeers": ["mauiIosRouter", "reactRouter", "pythonRouter", "espRouter"],
        "MaxPeerCalls": 3,
        "MaxPeerDepth": 2,
        "FinalAnswerOwner": "self"
      }
    }
  }
}
```

The existing `VirtualModelOptions` can be extended, or a new nested `AgentProfileOptions` can be added to avoid making `VirtualModelOptions` too large.

### Recommended Option Structure

```csharp
public class VirtualModelOptions
{
    public bool Enabled { get; set; } = true;
    public string ModelId { get; set; } = "";
    public string Provider { get; set; } = "CodebrewRouter";
    public string OwnedBy { get; set; } = "codebrew";
    public string Source { get; set; } = "virtual";
    public string? SystemPrompt { get; set; }
    public Dictionary<string, string[]> FallbackRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ContextCompactionOptions ContextCompaction { get; set; } = new();
    public AgentProfileOptions AgentProfile { get; set; } = new();
}
```

```csharp
public class AgentProfileOptions
{
    public string Kind { get; set; } = "general";
    public string PrimaryLanguage { get; set; } = "none";
    public string RoutingPolicy { get; set; } = "default";
    public string[] RagCollections { get; set; } = [];
    public string[] Skills { get; set; } = [];
    public string[] McpServers { get; set; } = [];
    public string[] AllowedPeers { get; set; } = [];
    public int MaxPeerCalls { get; set; } = 2;
    public int MaxPeerDepth { get; set; } = 2;
    public int PeerCallTimeoutSeconds { get; set; } = 10;
    public int TotalCollaborationTimeoutSeconds { get; set; } = 30;
    public bool AllowPremiumModels { get; set; } = true;
    public bool PreferOpenWeight { get; set; } = true;
    public string PrivacyMode { get; set; } = "normal";
    public string FinalAnswerOwner { get; set; } = "self";
}
```

### Routing Policy Configuration

```jsonc
{
  "LlmGateway": {
    "AgentRouting": {
      "Policies": {
        "coding-csharp": {
          "Default": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_DeepSeekV4Flash", "LmStudio"],
          "Small": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_DeepSeekV4Flash", "LocalGemma"],
          "Medium": ["OpenAI_Gpt53Codex", "OpenCodeGo_KimiK2_6", "OpenCodeGo_GLM5_1"],
          "Large": ["OpenAI_Gpt55", "Anthropic_Claude47", "OpenAI_Gpt53Codex"],
          "HighComplexity": ["OpenAI_Gpt55", "Anthropic_Claude47", "OpenAI_Gpt53Codex"],
          "DotNetMauiIos": ["OpenAI_Gpt55", "Anthropic_Claude47", "OpenAI_Gpt53Codex"]
        },
        "coding-python": {
          "Default": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_DeepSeekV4Flash", "LmStudio"],
          "Small": ["OpenCodeGo_DeepSeekV4Flash", "LocalGemma"],
          "Medium": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_GLM5_1", "OpenAI_Gpt53Codex"],
          "Large": ["OpenAI_Gpt55", "OpenAI_Gpt53Codex", "OpenCodeGo_DeepSeekV4Pro"]
        },
        "yardly-domain": {
          "Default": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_GLM5_1", "LocalGemma"],
          "DeviceSupport": ["OpenCodeGo_KimiK2_6", "OpenCodeGo_DeepSeekV4Pro", "LocalGemma"],
          "HighComplexity": ["OpenAI_Gpt55", "Anthropic_Claude47", "OpenCodeGo_KimiK2_6"]
        }
      }
    }
  }
}
```

### Profile Peer Configuration

```jsonc
{
  "VirtualModels": {
    "yardly": {
      "AgentProfile": {
        "Kind": "domain",
        "RoutingPolicy": "yardly-domain",
        "RagCollections": ["yardly-wiki", "plant-care", "yardly-device-manuals"],
        "Skills": ["yardly-support", "plant-care", "device-diagnostics"],
        "McpServers": ["yardly-wiki", "yardly-telemetry"],
        "AllowedPeers": ["espRouter", "pythonRouter", "csharpRouter", "plantCareRouter"],
        "MaxPeerCalls": 4,
        "MaxPeerDepth": 2,
        "PreferOpenWeight": true,
        "AllowPremiumModels": true,
        "FinalAnswerOwner": "self"
      }
    }
  }
}
```

---

## Collaboration Planning Algorithm

### Step 1: Resolve Lead Profile

Input:

- Requested model id from the chat request.

Behavior:

- If requested model id is a configured virtual model, that profile is the lead.
- If not, use normal direct model resolution.
- If requested model is `codebrewRouter`, use the general CodebrewRouter profile.

### Step 2: Build Routing Request

Collect:

- Last user message
- Recent conversation
- Optional compacted conversation summary
- Token count
- Lead profile metadata
- Available peer profiles
- Enabled RAG collections
- Enabled MCP servers
- Known request options such as tools, response format, max tokens, and stream

### Step 3: Ask Gemma4RoutingAgent

Use Gemma4 with:

- Temperature 0
- Short max output
- Strict JSON instruction
- Known label sets
- Known allowed profiles

If Gemma4 fails:

- Use keyword fallback.
- Use lead profile default route.
- Do not fan out unless a fixed rule requires it.

### Step 4: Validate Routing Decision

Validation rules:

- `leadProfile` must match requested model unless policy allows lead transfer.
- Suggested peers must exist.
- Suggested peers must be in `AllowedPeers`.
- Number of peers must not exceed `MaxPeerCalls`.
- Depth must not exceed `MaxPeerDepth`.
- No cycles.
- Unknown labels must be normalized.
- Confidence must be clamped to 0-1.

### Step 5: Retrieve Lead RAG

If `needsRag` is true:

- Query lead profile RAG collections.
- Use collection-specific limits.
- Add citations/source metadata.
- Include only the most relevant snippets.

### Step 6: Execute Peer Calls

For each approved peer:

- Build a narrow internal prompt.
- Include only relevant user/context/RAG snippets.
- Use peer profile prompt.
- Use peer route policy.
- Enforce timeout.
- Capture structured JSON peer result.

Peer calls may run in parallel if they do not depend on each other.

### Step 7: Merge Context

Create a lead-only synthesis context:

```text
Original user request
Lead RAG findings
Peer findings
Peer risks
Known constraints
Routing notes
```

Internal routing details should not be exposed to the final answer unless debugging or audit mode is enabled.

### Step 8: Select Final Provider Chain

Use:

- Lead profile routing policy
- Routing decision labels
- Token count
- Context size
- Complexity
- Privacy
- Availability
- Provider context budget

Return ordered provider chain.

### Step 9: Execute Final Answer

Call providers with existing first-chunk probe and failover behavior.

The lead profile system prompt is applied to the final answer.

---

## Example Flows

### Flow A: General CodebrewRouter Question

Request:

```json
{
  "model": "codebrewRouter",
  "messages": [
    {
      "role": "user",
      "content": "Can you explain why async/await avoids blocking?"
    }
  ]
}
```

Decision:

```json
{
  "leadProfile": "codebrewRouter",
  "taskType": "general",
  "isCoding": false,
  "complexity": "small",
  "contextSize": "tiny",
  "suggestedPeers": []
}
```

Route:

```json
["OpenCodeGo_DeepSeekV4Flash", "LocalGemma"]
```

Final answer:

- Direct response from `codebrewRouter`.
- No peer calls.

### Flow B: User Selects C# Router

Request:

```json
{
  "model": "csharpRouter",
  "messages": [
    {
      "role": "user",
      "content": "My .NET MAUI iOS build fails after adding BLE support. How should I debug it?"
    }
  ]
}
```

Decision:

```json
{
  "leadProfile": "csharpRouter",
  "taskType": "debugging",
  "isCoding": true,
  "language": "csharp",
  "frameworks": ["dotnet-maui"],
  "platforms": ["ios", "ble"],
  "complexity": "high",
  "contextSize": "small",
  "suggestedPeers": [
    {
      "profile": "mauiIosRouter",
      "reason": "iOS build and entitlement issues are likely."
    }
  ]
}
```

Route:

```json
["OpenAI_Gpt55", "Anthropic_Claude47", "OpenAI_Gpt53Codex"]
```

Final answer:

- Written by `csharpRouter`.
- Includes peer insights from `mauiIosRouter`.
- Focuses on C#/.NET MAUI diagnostics.

### Flow C: Yardly Device Question

Request:

```json
{
  "model": "yardly",
  "messages": [
    {
      "role": "user",
      "content": "My boundary device loses telemetry after rain. Could this be firmware or should I replace the device?"
    }
  ]
}
```

Decision:

```json
{
  "leadProfile": "yardly",
  "taskType": "device_support",
  "isCoding": false,
  "domain": "yardly",
  "platforms": ["yardly-device", "esp32"],
  "complexity": "medium",
  "contextSize": "small",
  "needsRag": true,
  "ragCollections": ["yardly-wiki", "yardly-device-manuals"],
  "suggestedPeers": [
    {
      "profile": "espRouter",
      "reason": "Assess ESP firmware and connectivity failure modes."
    }
  ]
}
```

Final answer:

- Written by Yardly.
- Uses Yardly wiki/device RAG.
- Uses ESP technical findings internally.
- Does not turn into a firmware development answer unless user asks.

### Flow D: ESP plus Python Telemetry Debugging

Request:

```json
{
  "model": "espRouter",
  "messages": [
    {
      "role": "user",
      "content": "ESP32-C6 firmware sends UART telemetry but my Python decoder loses frames every few minutes."
    }
  ]
}
```

Decision:

```json
{
  "leadProfile": "espRouter",
  "taskType": "debugging",
  "isCoding": true,
  "language": "c",
  "frameworks": ["esp-idf"],
  "platforms": ["esp32c6", "uart"],
  "complexity": "medium",
  "suggestedPeers": [
    {
      "profile": "pythonRouter",
      "reason": "Decoder frame loss may be caused by host parsing, buffering, or CRC handling."
    }
  ]
}
```

Final answer:

- Written by `espRouter`.
- Includes host decoder considerations from `pythonRouter`.
- Prioritizes firmware/UART facts.

---

## RAG Design

### RAG Should Be Profile Scoped

Each profile should have its own RAG collections.

Examples:

```text
yardly:
  - yardly-wiki
  - plant-care
  - yardly-device-manuals
  - known-yardly-issues

espRouter:
  - esp-idf-docs
  - codebrew-esp-firmware-docs
  - uart-protocol-docs
  - yardly-device-technical-notes

pythonRouter:
  - codebrew-python-host-tools
  - telemetry-ingestion-docs
  - pytest-guidelines

csharpRouter:
  - dotnet-docs
  - codebrew-csharp-notes
  - maui-ios-docs
```

### RAG Query Planning

Gemma4RoutingAgent can say `needsRag`, but the profile controls which collections are accessible.

Yardly can query Yardly collections. It should not automatically search arbitrary software repo docs unless the profile allows it.

### RAG Snippet Budget

Each profile should define:

- Maximum snippets
- Maximum tokens per snippet
- Maximum total RAG tokens
- Whether citations are required

Suggested defaults:

```json
{
  "MaxSnippets": 6,
  "MaxSnippetTokens": 300,
  "MaxTotalRagTokens": 1800,
  "RequireCitations": true
}
```

### RAG Result Shape

```json
{
  "collection": "yardly-device-manuals",
  "documentId": "boundary-device-troubleshooting",
  "title": "Boundary Device Telemetry Troubleshooting",
  "score": 0.86,
  "snippet": "Telemetry loss after rain is commonly associated with enclosure moisture, low battery, antenna obstruction, or gateway connectivity loss.",
  "uri": "rag://yardly-device-manuals/boundary-device-troubleshooting"
}
```

---

## MCP and Skills Design

### Profile-Scoped Tool Access

MCP servers and skills should be attached to profiles. This prevents unrelated tools from leaking into domain models.

Example:

```json
{
  "csharpRouter": {
    "McpServers": ["filesystem", "git", "nuget"],
    "Skills": ["csharp", "dotnet", "maui"]
  },
  "yardly": {
    "McpServers": ["yardly-wiki", "yardly-telemetry"],
    "Skills": ["plant-care", "yardly-support"]
  }
}
```

### Peer Calls and Tools

Peer calls should use the peer's tools, not the lead's tools.

If Yardly calls `espRouter`, then `espRouter` may use ESP docs or firmware tools only if its profile allows those tools.

### Tool Invocation Visibility

The final response should not expose internal tool chatter by default.

Debug mode can expose:

- Lead profile
- Peer profiles called
- RAG collections searched
- Provider selected
- Token counts
- Time spent

---

## API Behavior

### `/v1/models`

The model list should include virtual profiles.

Example:

```json
{
  "object": "list",
  "data": [
    {
      "id": "codebrewRouter",
      "object": "model",
      "provider": "CodebrewRouter",
      "ownedBy": "codebrew",
      "source": "virtual",
      "enabled": true
    },
    {
      "id": "csharpRouter",
      "object": "model",
      "provider": "CodebrewRouter",
      "ownedBy": "codebrew",
      "source": "virtual",
      "enabled": true
    },
    {
      "id": "yardly",
      "object": "model",
      "provider": "CodebrewRouter",
      "ownedBy": "yardly",
      "source": "virtual",
      "enabled": true
    }
  ]
}
```

### `/v1/models/{modelId}`

Virtual model details should include profile metadata.

Example:

```json
{
  "id": "yardly",
  "object": "model",
  "provider": "CodebrewRouter",
  "ownedBy": "yardly",
  "source": "virtual",
  "enabled": true,
  "profile": {
    "kind": "domain",
    "routingPolicy": "yardly-domain",
    "allowedPeers": ["espRouter", "pythonRouter", "csharpRouter"],
    "ragCollections": ["yardly-wiki", "plant-care", "yardly-device-manuals"],
    "mcpServers": ["yardly-wiki", "yardly-telemetry"],
    "maxPeerCalls": 4,
    "maxPeerDepth": 2
  },
  "fallbackRules": [],
  "backingModels": []
}
```

### Chat Completion Response

External response format stays OpenAI-compatible.

Internal collaboration should not change the response envelope.

Non-streaming:

```json
{
  "id": "chatcmpl-...",
  "object": "chat.completion",
  "created": 1710000000,
  "model": "yardly",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 0,
    "completion_tokens": 0,
    "total_tokens": 0
  }
}
```

Streaming remains SSE with `chat.completion.chunk`.

---

## Observability

### Router Events

Add structured events:

- `RouterProfileResolvedEvent`
- `RouterDecisionEvent`
- `RouterPeerPlanEvent`
- `RouterPeerCallStartEvent`
- `RouterPeerCallSuccessEvent`
- `RouterPeerCallFailEvent`
- `RouterRagQueryEvent`
- `RouterRagResultEvent`
- `RouterPolicySelectedEvent`
- `RouterProviderChainSelectedEvent`
- `RouterCollaborationCompleteEvent`

### Example Log Flow

```text
[ROUTER-PROFILE] requested=yardly lead=yardly kind=domain
[ROUTER-DECISION] task=device_support domain=yardly complexity=medium context=small confidence=0.86
[ROUTER-RAG] profile=yardly collections=yardly-wiki,yardly-device-manuals snippets=5 tokens=1432
[ROUTER-PEER-PLAN] lead=yardly peers=espRouter maxDepth=2 maxPeerCalls=4
[ROUTER-PEER-START] lead=yardly peer=espRouter reason="Assess ESP firmware and connectivity failure modes"
[ROUTER-PEER-SUCCESS] peer=espRouter confidence=0.82 elapsedMs=1840
[ROUTER-POLICY] profile=yardly policy=yardly-domain branch=DeviceSupport
[ROUTER-CHAIN] profile=yardly providers=OpenCodeGo_KimiK2_6,OpenCodeGo_DeepSeekV4Pro,LocalGemma
[ROUTER-SUCCESS] profile=yardly provider=OpenCodeGo_KimiK2_6 model=kimi-k2.6 elapsedMs=4920
```

### Debug Response Metadata

Optional debug mode could add response headers:

```text
X-CodebrewRouter-Lead-Profile: yardly
X-CodebrewRouter-Peers: espRouter,pythonRouter
X-CodebrewRouter-Provider: OpenCodeGo_KimiK2_6
X-CodebrewRouter-Policy: yardly-domain
X-CodebrewRouter-Rag-Collections: yardly-wiki,yardly-device-manuals
```

Do not include these by default in production.

---

## Failure Modes

### Gemma4 Routing Fails

Fallback:

- Use keyword classifier.
- Use lead profile default route.
- Skip peer calls unless fixed rules exist.

User impact:

- Request still gets an answer.
- Logs show degraded routing.

### Peer Call Fails

Fallback:

- Continue with other peer results.
- Lead answers with available context.
- Do not fail the whole request unless peer is marked required.

### RAG Fails

Fallback:

- If RAG is optional, answer without it and log warning.
- If RAG is required for a domain answer, answer with uncertainty or ask for clarification.

### Provider Fails

Fallback:

- Existing provider fallback chain.
- First-chunk probe for streaming.
- Context-size-aware skip when possible.

### Recursive Cycle Detected

Fallback:

- Drop the repeated peer call.
- Log cycle.
- Continue with existing findings.

### Budget Exceeded

Fallback:

- Trim peer plan.
- Prefer fewer peers.
- Prefer cheaper/lower-latency models.
- If still too large, use context compaction or return clean context error.

---

## Security and Privacy

### Tool Isolation

Profiles should not inherit all available tools by default.

Rules:

- Tools must be explicitly allowed per profile.
- Peer calls use peer tools.
- Domain profiles should not gain broad filesystem/git tools unless explicitly enabled.
- Yardly should use Yardly RAG and device support tools, not developer tools, by default.

### RAG Isolation

Rules:

- Profiles can query only configured RAG collections.
- RAG citations should include collection and document ids.
- Private customer data should remain in restricted collections.
- Peer profiles should receive only the snippets needed for their task.

### Provider Privacy

Rules:

- `privacy=private` should restrict routing to local/private providers.
- Domain profiles with sensitive user/device data should support private mode.
- Logs should avoid raw secrets, credentials, and unnecessary user data.

### Prompt Injection

RAG and peer findings can be malicious or incorrect.

Mitigations:

- Treat RAG as evidence, not instructions.
- Keep system prompts above retrieved content.
- Mark peer results as internal analysis.
- Do not let peer output override profile policy.
- Do not let RAG content request new tools or peer calls.

---

## Testing Strategy

### Unit Tests

Add tests for:

- Routing decision JSON parsing.
- Invalid Gemma4 JSON fallback.
- Unknown labels normalization.
- Profile resolution.
- Allowed peer filtering.
- Max peer call enforcement.
- Max depth enforcement.
- Cycle detection.
- Routing policy selection.
- RAG collection filtering.
- Provider chain selection by complexity/context.
- Yardly final answer owner behavior.

### Integration Tests

Add tests for:

- `/v1/models` includes specialist virtual models.
- `/v1/models/yardly` returns profile metadata.
- `model=yardly` prepends Yardly system prompt.
- `model=csharpRouter` prepends C# system prompt.
- Yardly can call `espRouter` internally.
- Peer failure does not fail the request.
- Gemma4 routing failure falls back cleanly.
- Streaming still returns valid OpenAI SSE.

### Golden Tests

Create fixture prompts:

- Simple C# syntax question
- Complex .NET MAUI iOS build failure
- Python telemetry decoder issue
- ESP-IDF UART framing issue
- Yardly plant-care question
- Yardly device telemetry question
- React/TypeScript UI task
- Mixed C# + ESP + Python request

Assert:

- Lead profile
- Suggested peers
- Routing branch
- Provider chain
- Whether RAG was requested

### Contract Tests

Ensure:

- OpenAI response envelope remains unchanged.
- Model list remains backward-compatible.
- Existing `codebrewRouter` behavior still works.
- Existing `yardly` requests still resolve.

---

## Implementation Roadmap

### Phase 1: Profile Metadata

Add profile configuration shape.

Deliverables:

- `AgentProfileOptions`
- Extended virtual model detail response
- Initial config for `codebrewRouter`, `yardly`, `csharpRouter`, `pythonRouter`, `espRouter`
- Model list tests

### Phase 2: Gemma4 Routing Decision

Add structured routing agent.

Deliverables:

- `IRoutingAgent`
- `Gemma4RoutingAgent`
- JSON schema/validator
- keyword fallback
- logging
- tests for valid/invalid output

### Phase 3: Routing Policy Engine

Add data-driven model chain selection.

Deliverables:

- `AgentRoutingOptions`
- `IRoutingPolicyResolver`
- context/complexity selection
- open-weight preference
- premium escalation
- tests

### Phase 4: Peer Call Orchestrator

Add bounded model-to-model collaboration.

Deliverables:

- `IPeerProfileOrchestrator`
- peer request/response schemas
- max depth/calls/cycle detection
- parallel peer fanout
- timeout handling
- tests

### Phase 5: RAG Attachment

Add profile-scoped RAG interfaces.

Deliverables:

- `IRagRetriever`
- collection policy
- snippet budget
- citations
- Yardly RAG stub
- tests

### Phase 6: MCP and Skills Attachment

Add profile-scoped tool surfaces.

Deliverables:

- profile MCP registry
- skill/instruction pack references
- peer tool isolation
- tests

### Phase 7: Observability and Debugging

Add detailed router telemetry.

Deliverables:

- router event records
- debug headers in dev mode
- metrics
- failure-mode logging

### Phase 8: Expanded Model Catalog

Add model metadata for open-weight and premium routes.

Deliverables:

- model family tags
- license metadata
- context windows
- capability tags
- provider health integration
- Hugging Face reference-derived initial recommendations

---

## Initial Profile Set

Recommended initial set:

```text
codebrewRouter
yardly
csharpRouter
pythonRouter
espRouter
reactRouter
```

Why this set:

- `codebrewRouter` preserves current behavior.
- `yardly` validates domain-specific RAG plus technical delegation.
- `csharpRouter` validates language-specific coding.
- `pythonRouter` validates host tooling and ingestion workflows.
- `espRouter` validates embedded/device specialization.
- `reactRouter` validates frontend specialization.

Add later:

```text
cppRouter
cRouter
javaRouter
typescriptRouter
nodeRouter
objectiveCRouter
swiftRouter
mauiIosRouter
sqlRouter
telemetryRouter
plantCareRouter
```

---

## Acceptance Criteria

The design is successful when:

- `codebrewRouter` remains usable as a general model.
- Specialist virtual models appear in `/v1/models`.
- Each specialist can have its own system prompt.
- Gemma4 produces a validated structured routing decision.
- Model selection is data-driven by config.
- A selected specialist can call another specialist internally.
- Peer calls are bounded by max calls, max depth, timeout, and cycle detection.
- Yardly can call ESP/Python specialists without becoming a developer assistant.
- RAG collections are profile-scoped.
- MCP/tools are profile-scoped.
- OpenAI-compatible response shape is unchanged.
- Logs explain lead profile, peers, RAG, policy, and provider chain.
- Tests cover routing, peer delegation, fallback, and API contracts.

---

## Open Questions

1. Which profiles should be included in the first implementation batch?
2. Should `mauiIosRouter` be first-class immediately, or initially part of `csharpRouter`?
3. Which RAG backend should be used first?
4. Which MCP servers are safe for Yardly?
5. Should debug routing metadata be exposed through response headers, a diagnostics endpoint, or logs only?
6. What is the default cost policy for `codebrewRouter`?
7. Should users be able to override model family preferences per request?
8. Should peer calls be allowed for streaming requests before the stream starts only, or can peer calls occur midstream in a later phase?
9. Should a profile ever transfer final answer ownership to another profile?
10. How should profile-specific memory be stored and isolated?

---

## Recommended First Design Decision

Build the first version around these rules:

- `codebrewRouter` is the general lead profile.
- Specialist profiles are selectable virtual models.
- The requested profile remains the lead.
- Gemma4 suggests peers but config filters them.
- Peers return strict JSON.
- No midstream peer calls.
- Max peer depth is 2.
- Max peer calls per request is 3 by default.
- RAG and MCP are profile-scoped.
- Yardly can call technical peers but owns Yardly final answers.
- Open-weight models are preferred unless complexity/context requires premium escalation.

This gives the system real collaborative behavior while keeping the first implementation bounded and testable.

