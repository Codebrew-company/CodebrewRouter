# Context-Aware Routing & Compaction — Design

**Date:** 2026-04-29
**Status:** Approved (brainstorm); pending implementation plan
**Scope:** `Blaze.LlmGateway` (CodebrewRouter)

## Problem

The current routing pipeline (`LlmRoutingChatClient` → keyed provider `IChatClient`) selects a destination based on prompt heuristics and provider availability, but is blind to the *size* of the incoming context relative to the chosen model's context window. Oversized requests fail at the provider with opaque errors and no recovery. A `ContextCompactor` exists in `Infrastructure/ContextHandling` but is not wired into the routing pipeline.

We need to:

1. **Detect** when an incoming chat request exceeds the chosen model's context window.
2. **Compact** the conversation (using the existing `IContextCompactor`) to fit, when the model has a known window.
3. **Re-route** to a larger-window model in the failover chain when compaction can't make it fit.
4. **Reject** with a clean `413 Payload Too Large` only when no provider in the chain can accommodate the prompt.

## Goals

- Per-model context-window metadata available everywhere routing decisions are made.
- A single, isolated middleware that performs the size check and compaction, leaving `LlmRoutingChatClient` focused on destination selection and failover.
- Size-aware failover: skip fallback providers whose model cannot fit the request.
- Clean error semantics: `413` only when truly unservable; otherwise the client sees a normal response.
- ≥ 95% test coverage on new code; build remains `-warnaserror` clean.

## Non-Goals

- Sizing-aware *primary* routing strategy. The new layer is a safety net; primary routing remains driven by `IRoutingStrategy`. A future strategy may consume the same metadata.
- Dynamic learning of context windows from provider error responses. We rely on discovery + curated tables.
- Per-tenant or per-request budget overrides beyond `ChatOptions.MaxOutputTokens`.

## Design

### 1. Model context metadata

Extend `Blaze.LlmGateway.Core.ModelCatalog.AvailableModel`:

```csharp
public sealed record AvailableModel(
    string Id,
    string Provider,
    string OwnedBy,
    string Source,
    string? Endpoint = null,
    bool SupportsChat = true,
    bool Enabled = true,
    string? ErrorMessage = null,
    DateTimeOffset? LastCheckedUtc = null,
    int? ContextWindowTokens = null,   // null = unknown
    int? MaxOutputTokens = null);       // null = use provider default
```

A new `IModelContextLimitsResolver` populates these fields during catalog discovery using, in order:

1. **Provider-native discovery**
   - Ollama: `POST /api/show` → `model_info.*.context_length`.
   - OpenRouter: `GET /api/v1/models` → `context_length`.
   - (Azure/OpenAI/Gemini have no public endpoint — covered by curated table.)
2. **Curated static table** (`ModelContextLimits.cs` in Infrastructure): well-known IDs with prefix/glob matching. Examples:
   - `gpt-4o`, `gpt-4o-mini` → 128 000
   - `gemini-2.0-flash` → 1 048 576
   - `llama3.1:*`, `llama3.2:*` → 128 000
   - `phi-4` → 16 384
3. **Conservative fallback**: `8192`, logged once per model id.

Catalog refresh re-runs the resolver. Limits travel with the `AvailableModel`, so any consumer of `IModelCatalog` gets them for free.

### 2. `ContextSizingChatClient` middleware

A new `DelegatingChatClient` registered between each keyed provider client and the routing layer. Wiring lives in `InfrastructureServiceExtensions`: every keyed `IChatClient` registration is wrapped with `ContextSizingChatClient` so the call sequence is:

```
LlmRoutingChatClient
  → keyed IChatClient (resolved)
    → ContextSizingChatClient   ← new
      → underlying provider client (UseFunctionInvocation, etc.)
```

**Per-request flow** (both `GetResponseAsync` and `GetStreamingResponseAsync`):

1. Determine `modelId`. Order: `ChatOptions.ModelId` if set; otherwise the provider's configured default model id (already exposed via `IModelSelectionResolver` / per-provider configuration). If neither yields a value, treat the window as unknown and apply the unknown-window branch.
2. Look up the model via `IModelCatalog.FindByIdAsync(modelId)`.
3. Compute:
   ```
   reservedOutput = options?.MaxOutputTokens
                     ?? model?.MaxOutputTokens
                     ?? ContextSizingOptions.DefaultReservedOutputTokens
   budget         = model.ContextWindowTokens - reservedOutput
   ```
4. `tokens = ITokenCounter.CountTokens(messages, modelId)`.
5. Decision tree:
   - `model.ContextWindowTokens == null` (unknown):
     - if `ContextSizingOptions.FailOnUnknownWindow` → throw `ContextOverflowException`.
     - else → forward as-is, log warning.
   - `tokens ≤ budget` → forward unchanged.
   - `tokens > budget`:
     - `result = await IContextCompactor.CompactAsync(messages, budget, modelId, ct)`.
     - if `result.WasCompacted && result.CompactedTokenCount ≤ budget` → forward `result.Messages`.
     - else → throw `ContextOverflowException(modelId, requiredTokens: tokens, budget: budget)`.

**Streaming**: the size check runs *before* the first chunk is requested, so failures surface as a thrown exception, not a partial stream. This composes cleanly with `LlmRoutingChatClient`'s existing first-chunk probe.

**No state, no I/O** beyond the catalog lookup (already cached) and the optional summarizer call inside `IContextCompactor`.

### 3. Re-route on overflow in `LlmRoutingChatClient`

`LlmRoutingChatClient` already catches provider exceptions and walks the failover chain. We extend it to recognize the new `ContextOverflowException` as a *routing*-recoverable failure with size-aware filtering:

1. **Distinguish the exception** in both `GetResponseAsync` and the streaming first-chunk probe. Log at info level — it's expected, not faulty.
2. **`CanFitAsync(RouteDestination dest, int requiredTokens, CancellationToken ct)`** helper:
   - Resolves the destination's effective model id (mapping `ChatOptions.ModelId` onto that provider when applicable).
   - Looks up `ContextWindowTokens` via `IModelCatalog`.
   - Returns `true` if `requiredTokens ≤ window - reservedOutput`. Unknown windows return `true` (optimistic; the destination's own `ContextSizingChatClient` will catch real overflow).
3. **When the trigger is `ContextOverflowException`**, iterate the failover chain but **skip destinations where `CanFitAsync` is false** based on the *original* (un-compacted) token count. The compacted form is per-provider (each one re-compacts for its own budget), so routing on raw size maximizes the chance of a low-loss summarization downstream.
4. **Exhausted chain** → throw a final `ContextOverflowException` carrying:
   - `requiredTokens`
   - the largest window attempted
   - the list of destinations tried (logged, not necessarily surfaced).
5. **Endpoint mapping**: the `POST /v1/chat/completions` handler maps `ContextOverflowException` → **HTTP 413 Payload Too Large** with JSON body:
   ```json
   {
     "error": {
       "type": "context_length_exceeded",
       "required_tokens": 245321,
       "largest_window_attempted": 200000,
       "message": "Prompt exceeds the largest available model's context window."
     }
   }
   ```

Existing fault-based failover (provider down, 5xx, throttled) is unchanged — only `ContextOverflowException` triggers size-aware filtering.

### 4. Configuration

New section, sibling to `ContextCompaction`:

```jsonc
"CodebrewRouter": {
  "ContextSizing": {
    "Enabled": true,                     // master kill switch
    "DefaultReservedOutputTokens": 1024, // floor when neither request nor model specifies
    "UnknownWindowFallbackTokens": 8192, // used for routing decisions; not enforced
    "FailOnUnknownWindow": false         // if true, throw instead of forwarding
  }
}
```

`ContextCompactionOptions` is reused unchanged.

### 5. Observability

- **Structured logs** (info): `context-sizing: model={ModelId} required={Required} budget={Budget} action={Forward|Compact|Overflow}`.
- **Compaction outcome** logged at the same site: `compacted: {Original} -> {Compacted} strategy={Strategy}`.
- **Counters** (via `ServiceDefaults` OTel):
  - `gateway.context_sizing.forward`
  - `gateway.context_sizing.compacted`
  - `gateway.context_sizing.overflow`
  - `gateway.context_sizing.reroute`
- **Histograms**: `gateway.context_sizing.tokens_required`, `gateway.context_sizing.budget`.

### 6. Components & files

**Added:**
- `Core/Configuration/ContextSizingOptions.cs`
- `Infrastructure/ContextHandling/ContextSizingChatClient.cs`
- `Infrastructure/ContextHandling/ContextOverflowException.cs`
- `Infrastructure/ModelCatalog/IModelContextLimitsResolver.cs`
- `Infrastructure/ModelCatalog/ModelContextLimitsResolver.cs`
- `Infrastructure/ModelCatalog/ModelContextLimits.cs` (curated static table)

**Modified:**
- `Core/ModelCatalog/AvailableModel.cs` — add `ContextWindowTokens`, `MaxOutputTokens`.
- `Infrastructure/LlmRoutingChatClient.cs` — distinguish `ContextOverflowException`, add `CanFitAsync` failover filter.
- `Infrastructure/InfrastructureServiceExtensions.cs` — wrap each keyed `IChatClient` with `ContextSizingChatClient`; register resolver and options.
- Existing model-discovery code paths (per-provider) — invoke resolver to populate new fields.
- `Api/Program.cs` (endpoint mapping) — translate `ContextOverflowException` → HTTP 413.

### 7. Testing

In `Blaze.LlmGateway.Tests`:

1. **`ContextSizingChatClientTests`**
   - forwards when under budget
   - compacts when over and forwards when compaction fits
   - throws `ContextOverflowException` when compaction can't fit
   - skips check (forwards) when window unknown and `FailOnUnknownWindow=false`
   - throws when window unknown and `FailOnUnknownWindow=true`
   - honors `ChatOptions.MaxOutputTokens` over model default
   - streaming path throws before first chunk on overflow
2. **`LlmRoutingChatClientContextOverflowTests`**
   - re-routes to a larger-window provider on `ContextOverflowException`
   - filters failover chain by `CanFitAsync`
   - throws final `ContextOverflowException` when no provider fits
   - non-overflow exceptions still walk full chain (regression)
3. **`ModelContextLimitsResolverTests`**
   - Ollama `/api/show` parsing → populated
   - OpenRouter `/api/v1/models` parsing → populated
   - curated-table fallback for known ids (prefix match)
   - unknown id → `null` + single-shot warning
4. **Integration**: post a >200k-token prompt; assert routing lands on a 128k+ model after compaction (or 413 if none configured).

Coverage target: ≥ 95% on the new files.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Curated table drifts from reality (model providers expand windows). | Provider-native discovery is preferred; table is fallback. Refresh on catalog refresh. Unknown windows are non-fatal by default. |
| Token counter inaccuracy across providers (tiktoken ≠ Gemini ≠ Llama). | Reserved-output buffer absorbs small drift. `ITokenCounter` already accepts `modelId` and can be extended later. |
| Summarization quality degrades reasoning. | Already mitigated by `ContextCompactor` preserving system prompts and recent N turns; falls back to pure pruning when summarizer is unavailable. |
| Streaming endpoints get stuck if overflow check is slow. | Catalog lookups are O(1) cached; token counting is in-process; summarizer call is the only network hop and respects the request `CancellationToken`. |
| `ChatOptions.ModelId` and per-provider model id mismatch (e.g., user passes `gpt-4o` but destination is OpenRouter). | `CanFitAsync` resolves destination-specific model id; if mapping fails, treat window as unknown (optimistic). |

## Acceptance Criteria

- New `AvailableModel` fields populated from Ollama and OpenRouter discovery in integration runs.
- A request whose prompt exceeds `gpt-4o-mini` (128k) is compacted and served by the same model when compaction fits.
- A request that can't be compacted to fit `gpt-4o-mini` is re-routed to `gemini-2.0-flash` (1M) when present in the failover chain.
- A request larger than every configured model's window returns HTTP 413 with the documented body.
- All new tests pass; coverage ≥ 95% on new files; build is `-warnaserror` clean.
