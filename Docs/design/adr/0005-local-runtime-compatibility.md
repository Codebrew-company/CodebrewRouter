# ADR-0005: Local runtime compatibility — LM Studio and llama.cpp as OpenAI-compatible catalog entries

- **Status:** Proposed — *LM Studio provider removed 2026-07-08 (redundant; Ollama/LocalGemma cover local inference). The llama.cpp / OpenAI-compatible-catalog reasoning below still stands; treat LM Studio here as one historical example, not a current provider.*
- **Date:** 2026-04-17
- **Deciders:** Architecture
- **Related:** ADR-0002, [PRD §7.2](../../PRD/blaze-llmgateway-prd.md), [plan/llm-agent-platform-plan.md](../../plan/llm-agent-platform-plan.md) §"Phase 2 - Provider/model catalog"

## Context

The planning draft names **LM Studio** and **llama.cpp server** as first-class local runtimes (alongside Ollama and Azure Foundry Local). Both expose an OpenAI-compatible HTTP surface:

- **LM Studio** — `http://localhost:1234/v1/chat/completions` (configurable port); OpenAI Chat Completions spec, models enumerable via `GET /v1/models`.
- **llama.cpp `llama-server`** — `http://localhost:8080/v1/chat/completions`; OpenAI Chat Completions compatibility layer.

Implementation options:

1. Specialized SDK per runtime (like we have for Ollama via `OllamaSharp` and Gemini via `Google.GenAI`).
2. Generic `OpenAIClient` with a `BaseAddress` override.

Option 2 is already how the codebase talks to GitHub Copilot, GitHub Models, OpenRouter, and Foundry Local:

```csharp
var client = new OpenAIClient(new ApiKeyCredential(opts.ApiKey),
    new OpenAIClientOptions { Endpoint = new Uri(opts.Endpoint) });
return client.GetChatClient(opts.Model).AsIChatClient()...;
```

Option 1 would mean writing and maintaining two more provider adapter classes and fighting minor protocol divergences (LM Studio's `keep_alive` semantics, llama.cpp's extra `slot_id` headers). The wins are small — both servers follow the OpenAI contract closely enough that pretending they are OpenAI endpoints is sufficient for Phase 1–2.

## Decision

We will **treat LM Studio and llama.cpp as `ProviderKind.OpenAICompatible` catalog entries** (per ADR-0002), with no specialized adapter classes. Any runtime-specific knob is exposed as a `ProviderDescriptor` option.

### Details

**Catalog entries.** Config snippets consumers drop into `appsettings.json`:

```json
{
  "Id": "lmstudio-workstation",
  "Kind": "OpenAICompatible",
  "Endpoint": "http://localhost:1234/v1",
  "ApiKey": "notneeded",
  "Locality": "Local",
  "Models": [
    {
      "Id": "lmstudio-workstation/gemma-4-9b",
      "ModelName": "gemma-4-9b-instruct",
      "Capabilities": { "ContextWindowTokens": 8192, "SupportsToolCalls": true, "FamilyTag": "gemma-4" }
    }
  ]
}

{
  "Id": "llamacpp-rig",
  "Kind": "OpenAICompatible",
  "Endpoint": "http://localhost:8080/v1",
  "ApiKey": "notneeded",
  "Locality": "Local",
  "Models": [
    { "Id": "llamacpp-rig/llama3.3-70b", "ModelName": "llama-3.3-70b-instruct", "Capabilities": { "ContextWindowTokens": 131072 } }
  ]
}
```

**Factory wiring.** `ProviderClientFactory` (see ADR-0002) uses the same branch as GitHub Copilot / OpenRouter / GithubModels:

```csharp
ProviderKind.OpenAICompatible => BuildOpenAICompatClient(descriptor, model),
```

where `BuildOpenAICompatClient` is already the pattern in [InfrastructureServiceExtensions.cs:49-57](../../Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs) etc.

**Discovery (Phase 2 add-on, not Phase 1).** An optional `OpenAICompatibleProbe` hosted service can call `GET {endpoint}/models` at startup to detect loaded models and either (a) warn on config drift ("you configured `gemma-4-9b` but LM Studio is serving `llama-3.2-3b`") or (b) auto-discover and register `ModelProfile`s if `Providers[].AutoDiscoverModels: true`. Out of Phase 1 scope.

**Capability metadata.** Set per-model by hand in config based on the model card — we do not attempt to derive capabilities from the runtime. A `SupportsToolCalls` value that turns out wrong at runtime surfaces as a tool-call error; Phase 5 adds a capability health check.

**Headers and quirks.** None required for Phase 1. If a runtime needs a custom header, we extend `ProviderDescriptor` with an optional `Headers: Dictionary<string,string>` rather than adding a dedicated adapter.

## Consequences

**Positive**

- Zero new adapter code. Both runtimes go live as soon as ADR-0002's descriptor model lands.
- Same is true for any future OpenAI-compatible server (vLLM, Text Generation Inference, Ray Serve, self-hosted Mistral endpoints) — all become config entries.
- Test surface is narrow: assert the factory builds the right `OpenAIClient` for `OpenAICompatible`, assert tool calls round-trip against a fake OpenAI-shaped server.

**Negative**

- Subtle spec divergences are swept under the rug until a user hits them. Mitigation: compatibility-matrix doc in `design/tech-design/` §4 with known caveats per runtime (e.g. "llama.cpp does not emit `usage` on the final chunk under some builds").
- If a runtime adds features that only work with a native SDK (e.g. LM Studio's JIT loading API), we cannot reach them via this abstraction. Fine — that's a plugin scenario, not a gateway feature.

**Neutral**

- PRD §7.2 provider table grows two rows (LM Studio, llama.cpp) with `Transport = HTTP`, `Auth = None`.
- Tests: add one end-to-end integration test per runtime pointed at a localhost fixture server to catch regressions in the compat path.

## Alternatives Considered

### Alternative A — Dedicated adapter project per runtime

`Blaze.LlmGateway.Infrastructure.LmStudio`, `.LlamaCpp`, etc. **Rejected** — cost to maintain (mocks, tests, release cadence), no feature gain in Phase 1, and encourages per-runtime drift.

### Alternative B — Defer local runtimes entirely to Phase 2+

Ship Phase 1 with only existing providers; add LM Studio/llama.cpp after the catalog refactor is stable. **Rejected** — the descriptor model lands in Phase 2 anyway; adding two rows of config once it exists is essentially free. Early support also validates ADR-0002's catalog model with non-Ollama non-Azure runtimes.

## References

- [LM Studio OpenAI API reference](https://lmstudio.ai/docs/local-server)
- [llama.cpp `llama-server` docs](https://github.com/ggerganov/llama.cpp/tree/master/examples/server)
- [../tech-design/blaze-llmgateway-architecture.md](../tech-design/blaze-llmgateway-architecture.md) §4 Inference plane
- [../../plan/llm-agent-platform-plan.md](../../plan/llm-agent-platform-plan.md) §"Local runtimes to prioritize first"
