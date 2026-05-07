# Offline Gemma Warmup And Aspire Readiness

## Summary
Add an always-warm offline startup path for `codebrewRouter -> LocalGemma`. The API will load the configured Gemma GGUF at startup, run a tiny one-token warmup prompt, keep the singleton model resident for the process lifetime, and expose readiness through Aspire logs and health checks.

## Key Changes
- Extend `LocalInferenceOptions` with:
  - `WarmupEnabled = true`
  - `WarmupPrompt = "ready"`
  - `WarmupMaxOutputTokens = 1`
  - `WarmupTimeoutSeconds = 120`
  - `BlockStartupUntilWarm = true` when a real local model path is configured
- Update `LocalGemmaWarmupService` to:
  - resolve keyed `"LocalGemma"` at startup
  - confirm the model loaded from `ModelPath`
  - run and consume a tiny streaming response
  - block startup on failure when `BlockStartupUntilWarm` is true
- Add local warmup telemetry tags:
  - `[LOCAL-WARMUP-START]`
  - `[LOCAL-WARMUP-LOAD]`
  - `[LOCAL-WARMUP-PRIME]`
  - `[LOCAL-WARMUP-READY]`
  - `[LOCAL-WARMUP-SKIP]`
  - `[LOCAL-WARMUP-FAIL]`
- Do not use `[ROUTER-*]` for warmup/startup logs. Keep `RouterLog.Write(...)` and router tags only for request routing.

## Aspire Integration
- Pass local warmup settings from `Blaze.LlmGateway.AppHost` into the API with environment variables:
  - `LlmGateway__LocalInference__ModelPath`
  - `LlmGateway__LocalInference__WarmupEnabled`
  - `LlmGateway__LocalInference__BlockStartupUntilWarm`
  - `LlmGateway__LocalInference__WarmupTimeoutSeconds`
- Add a `local-gemma-warmup` readiness health check so Aspire can show whether the model is `Loading`, `Priming`, `Ready`, `Skipped`, or `Failed`.
- Because startup blocks until warm when offline, Aspire dependents using `.WaitFor(api)` will wait for the warmed API.

## Test Plan
- Add config binding tests for the new warmup options.
- Add warmup service tests for disabled, skipped, successful prime, timeout, and startup-failure behavior.
- Add logging contract coverage confirming `[LOCAL-WARMUP-*]` tags exist and no warmup log uses `[ROUTER-*]`.
- Add AppHost composition coverage for the local warmup environment variables.
- Verify with focused local inference tests, logging contract tests, full build with `-warnaserror`, and full solution tests.

## Assumptions
- Offline mode uses only local Gemma through `LocalGemma`.
- `ModelPath` must point to a real local Gemma 4 GGUF file for warmup to succeed.
- `BlockStartupUntilWarm = true` is the intended offline GGUF profile because fast first chat matters more than fast process startup.
- Base checked-in API/AppHost config leaves `BlockStartupUntilWarm = false` while `ModelPath` is blank, so local development starts and reports degraded warmup readiness instead of crashing.
