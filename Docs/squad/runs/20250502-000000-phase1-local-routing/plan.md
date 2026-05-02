# Phase 1 Implementation Plan: 8 Ordered Steps

## Step 1: Update FallbackRules in appsettings.json
**File:** Blaze.LlmGateway.Api/appsettings.json
**Task:** Replace all 7 task types' FallbackRules with ["LmStudio"] only
**Before:**
\\\json
"Reasoning": { "FallbackRules": ["AzureFoundry", "Ollama", "OllamaBackup"] },
"Coding": { "FallbackRules": ["GithubModels", "Gemini", "OpenRouter"] },
...
\\\

**After:**
\\\json
"Reasoning": { "FallbackRules": ["LmStudio"] },
"Coding": { "FallbackRules": ["LmStudio"] },
...
\\\
**Owner:** Squad Coder | **Parallel:** Yes (Phase A, Steps 1–3)

---

## Step 2: Remove Cloud Provider DI Registrations
**File:** Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs
**Task:** Remove AddKeyedSingleton calls for AzureFoundry, GithubModels, FoundryLocal
**Delete:**
- AddKeyedSingleton("AzureFoundry", ...)
- AddKeyedSingleton("GithubModels", ...)
- AddKeyedSingleton("FoundryLocal", ...)

**Keep:** OllamaLocal, LmStudio keyed clients

**Owner:** Squad Coder | **Parallel:** Yes (Phase A, Steps 1–3)

---

## Step 3: Simplify Routing Logic (Cloud References)
**Files:**
- Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs (replace AzureFoundry → LmStudio logic)
- Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs (remove cloud context-sizing logic)

**Task:** Remove cloud-specific decision trees and fallback chains

**Owner:** Squad Coder | **Parallel:** Yes (Phase A, Steps 1–3)

---

## Step 4: Update AppHost Provisioning
**File:** Blaze.LlmGateway.AppHost/Program.cs
**Task:** Remove AddAzureAIFoundry, AddGitHubModel, AddContainer("ollama-backup", ...). Keep OllamaLocal + LmStudio

**Owner:** Squad Infra | **Parallel:** No (blocked on Step 3; Phase B)

---

## Step 5: Remove Cloud Secrets Setup
**File:** Blaze.LlmGateway.AppHost/Program.cs
**Task:** Remove SetParameter calls for azure-foundry-endpoint, azure-foundry-api-key, github-models-api-key, etc.

**Owner:** Squad Infra | **Parallel:** No (blocked on Step 4; Phase B)

---

## Step 6: Update Test Expectations
**File:** Blaze.LlmGateway.Tests/**
**Task:** Update mock providers to only recognize OllamaLocal, LmStudio. Remove test cases for AzureFoundry fallback chains.

**Owner:** Squad Tester | **Parallel:** No (blocked on Steps 1–3; Phase B)

---

## Step 7: Verify Build & Coverage
**Command:** dotnet build --no-incremental -warnaserror
**Command:** dotnet test --no-build --collect:"XPlat Code Coverage"
**Target:** 95% coverage maintained; all 248+ tests passing

**Owner:** Squad Tester | **Parallel:** No (Phase C, post-coder)

---

## Step 8: Quality Gate & Commit
**Task:** Verify no regressions, clean up unused imports, commit to main
**Owner:** Squad Reviewer | **Parallel:** No (Phase C, final gate)

---

## Parallelization Windows

### Phase A: Steps 1–3 (Parallel — Coder)
- File sets disjoint: appsettings.json, InfrastructureServiceExtensions.cs, CodebrewRouterChatClient.cs + LlmRoutingChatClient.cs
- No dependencies between steps
- Time: ~1 hour (parallel) vs ~3 hours (sequential)

### Phase B: Steps 4–6 (Sequential — Infra + Tester)
- Step 4 blocks on Step 3 (know which providers remain)
- Step 5 blocks on Step 4
- Step 6 blocks on Steps 1–3 (test mocks reference old providers)

### Phase C: Steps 7–8 (Sequential — Tester + Reviewer)
- Final verification and commit

**Total Time:** ~2–3 hours (vs 5+ hours sequential)
