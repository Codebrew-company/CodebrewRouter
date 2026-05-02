# Phase 1 Specification: Local Routing Setup & Redundancy

## Overview
Phase 1 removes all cloud provider registrations and configuration, leaving only local-only providers (OllamaLocal, LmStudio). This establishes a clean local-development baseline where all LLM calls are routed through on-device models.

## Problem Statement
- Current infrastructure registers 9 providers (AzureFoundry, FoundryLocal, GithubModels, Gemini, OpenRouter, Ollama, OllamaBackup, OllamaLocal, LmStudio)
- Scope creep: routing logic is bloated with cloud-specific rules and context-sizing
- Local-only roadmap requires surgical removal of cloud bindings without breaking the router logic

## Scope & Goals

### In Scope (Phase 1)
1. Remove cloud provider DI registrations (AzureFoundry, GithubModels, FoundryLocal)
2. Update FallbackRules to only point to LmStudio (no cloud fallbacks)
3. Simplify CodebrewRouterChatClient and LlmRoutingChatClient routing logic
4. Simplify AppHost to only provision OllamaLocal + LmStudio
5. Update test expectations to match new provider set

### Out of Scope (Phase 2+)
- Ollama/LmStudio model swapping logic
- Prompt optimization pipeline
- Vision passthrough (still on roadmap)
- Circuit breaker / resilience patterns

## Acceptance Criteria

✅ All 7 task types in appsettings.json have FallbackRules: ["LmStudio"] only
✅ DI container removes AzureFoundry, GithubModels, FoundryLocal keyed clients
✅ CodebrewRouterChatClient and LlmRoutingChatClient have cloud references removed
✅ AppHost only provisions OllamaLocal + LmStudio containers
✅ Build succeeds: dotnet build --no-incremental -warnaserror
✅ All 248 tests pass
✅ No cloud provider keys in secrets (cleanup)

## Technical Notes
- File-disjoint edits allow parallel execution in Phase A (Steps 1–3)
- Phase B (Steps 4–6) blocked on Phase A; handled by Infra + Tester
- Phase C (Step 8) cleanup and gate happens last
