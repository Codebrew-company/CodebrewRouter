# Handoff: Coder Phase A (Steps 1–3 — Config + DI + Routing)
**From:** Squad Conductor
**To:** Squad Coder
**Timestamp:** 05/02/2026 11:18:00

## Mission
Execute Steps 1–3 in parallel:
1. Update FallbackRules to ["LmStudio"]
2. Remove cloud provider DI registrations
3. Simplify cloud-specific routing logic

All three tasks have **disjoint file sets** (non-overlapping edits). Parallel execution is safe.

## Files you may edit (exclusive lock)
- \Blaze.LlmGateway.Api/appsettings.json\ (Step 1)
- \Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs\ (Step 2)
- \Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs\ (Step 3 part 1)
- \Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs\ (Step 3 part 2)

## Files other parallel tasks own
- AppHost changes: reserved for Squad Infra (Step 5)
- Tests: reserved for Squad Tester (Step 7)

## Inherited assumptions
- Current 248/251 tests passing; 3 have known isolation issues (pass individually)
- Latest commit: 272975a (LM Studio config fixes)
- Design spec approved; no architectural questions
- Removed providers: AzureFoundry, GithubModels, FoundryLocal

## Pending decisions
None; spec is complete.

## Acceptance Criteria
**Per Step 1 (FallbackRules):**
- All 7 task types: Reasoning, Coding, Research, VisionObjectDetection, Creative, DataAnalysis, General
- Each has FallbackRules: \["LmStudio"]\ only
- No references to AzureFoundry, GithubModels, FoundryLocal in FallbackRules section

**Per Step 2 (DI):**
- Removed: AddKeyedSingleton("AzureFoundry", ...), AddKeyedSingleton("GithubModels", ...), AddKeyedSingleton("FoundryLocal", ...)
- Kept: AddKeyedSingleton("OllamaLocal", ...), AddKeyedSingleton("LmStudio", ...)
- Build succeeds: \dotnet build --no-incremental -warnaserror\

**Per Step 3 (Routing):**
- CodebrewRouterChatClient: AzureFoundry references replaced with LmStudio
- CodebrewRouterChatClient.PrepareMessagesForProviderAsync(): Cloud provider checks removed
- LlmRoutingChatClient context sizing: AzureFoundry + GithubModels cases removed
- No compile errors

## Discarded context
None; previous chat context is available for reference.

---
[DONE or EDIT]? Please respond with structured-action tags.
