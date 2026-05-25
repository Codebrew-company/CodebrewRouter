# Provider-Level Gemma Download Design

Status: Approved for implementation planning
Date: 2026-05-08

## Summary

Make `LlmGateway:LocalInference:ModelPath` a true provider-level model source. It may point to either a local GGUF file or a remote Hugging Face URL. The LocalGemma provider will materialize that source into a local GGUF path, load it once through LLamaSharp, keep the loaded model resident, and expose readiness so Aspire resources such as Scalar, OpenWebUI, and Agent DevUI do not start until the model is downloaded, loaded, and warmed.

## Goals

- Support a remote GGUF URL directly in `LlmGateway:LocalInference:ModelPath`.
- Reuse `RuntimeDownloadModelProvider` as the download/cache engine.
- Keep `LocalGemma` as the single offline provider used by `codebrewRouter`.
- Avoid async work in DI registration.
- Keep the warmed LLamaSharp model resident for the process lifetime.
- Keep startup and readiness logs outside the `[ROUTER-*]` request-routing contract.
- Make Aspire dependents wait for the warmed API through health/readiness.

## Non-Goals

- Add a separate Aspire downloader resource.
- Download multiple model variants at once.
- Add GPU offload or multimodal `mmproj` support in this change.
- Change the router request telemetry contract.

## Architecture

Introduce a provider-level LocalGemma runtime wrapper around the current LLamaSharp adapter. The wrapper will be registered as the keyed `"LocalGemma"` `IChatClient` and will also expose local model state.

The runtime wrapper will:

- Read `LocalInferenceOptions.ModelPath`.
- Use `IModelDistributionProvider.EnsureModelAvailableAsync(...)` to resolve it.
- Receive an absolute local GGUF path for both local files and downloaded remote URLs.
- Construct the existing LLamaSharp-backed `LocalGemmaChatClient` only after the local file is available.
- Guard download/load with a `SemaphoreSlim` so concurrent warmup or requests do not trigger duplicate loads.
- Delegate all chat calls to the loaded inner client.

This keeps direct provider usage, warmup, request routing, and Aspire readiness on the same path.

## Startup Flow

1. Aspire starts the API.
2. `LocalGemmaWarmupService` resolves the keyed `"LocalGemma"` client.
3. If the client exposes the LocalGemma runtime interface, warmup calls `EnsureLoadedAsync`.
4. The runtime resolves `ModelPath`:
   - Local file: validate existence and use the absolute path.
   - Remote URL: download to `CacheDirectory`, reuse cached file on future runs, then use the cached path.
5. The runtime constructs the LLamaSharp `LocalGemmaChatClient` with the resolved local path.
6. Warmup runs the configured one-token prime prompt.
7. `local-gemma-warmup` becomes healthy only after the prime succeeds.
8. Aspire resources that call `.WaitFor(api)` start after API readiness is healthy.

## Aspire Integration

`Blaze.LlmGateway.AppHost` will continue passing the existing warmup settings and will also pass provider download settings:

- `LlmGateway__LocalInference__ModelPath`
- `LlmGateway__LocalInference__CacheDirectory`
- `LlmGateway__LocalInference__DownloadTimeoutSeconds`
- `LlmGateway__LocalInference__WarmupEnabled`
- `LlmGateway__LocalInference__BlockStartupUntilWarm`
- `LlmGateway__LocalInference__WarmupTimeoutSeconds`

OpenWebUI and Agent DevUI already wait for the API. Scalar will also be assigned to a resource builder variable and chained with the generic Aspire `.WaitFor(api)` extension.

Because `/health` maps degraded and unhealthy states to HTTP 503, dependents wait while the provider is downloading, loading, or priming.

## Health And State

The warmup health check remains the Aspire readiness source. Its snapshot should report the configured or resolved model path and a message describing the current provider phase.

The implementation will add a distinct `Downloading` status so Aspire can distinguish download/cache work from LLamaSharp load work. Health is unhealthy until the file is downloaded, LLamaSharp has loaded it, and the prime has completed.

## Logging

Provider-level model materialization logs must not use `[ROUTER-*]`.

Add local model source/cache tags:

- `[LOCAL-MODEL-RESOLVE]`
- `[LOCAL-MODEL-CACHE-HIT]`
- `[LOCAL-MODEL-DOWNLOAD-START]`
- `[LOCAL-MODEL-DOWNLOAD-READY]`
- `[LOCAL-MODEL-DOWNLOAD-FAIL]`

Keep existing warmup tags:

- `[LOCAL-WARMUP-START]`
- `[LOCAL-WARMUP-LOAD]`
- `[LOCAL-WARMUP-PRIME]`
- `[LOCAL-WARMUP-READY]`
- `[LOCAL-WARMUP-SKIP]`
- `[LOCAL-WARMUP-FAIL]`

## Error Handling

- Blank `ModelPath` fails warmup. If `BlockStartupUntilWarm` is true, API startup fails.
- Missing local file fails with a clear configuration message.
- Remote download failures preserve the existing circuit-breaker behavior.
- Partial downloads continue using temporary files and cleanup before surfacing failure.
- If `BlockStartupUntilWarm` is false, startup may continue but readiness remains unhealthy and chat requests return provider unavailable until the model is loaded.
- Direct requests that arrive before warmup completes use the same runtime lock and either wait for load or surface the same provider failure.

## Configuration Example

Recommended Gemma 4 Q4_K_M source:

```json
{
  "LlmGateway": {
    "LocalInference": {
      "ModelPath": "https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
      "CacheDirectory": "E:\\models\\codebrewrouter",
      "WarmupEnabled": true,
      "BlockStartupUntilWarm": true,
      "WarmupTimeoutSeconds": 120,
      "DownloadTimeoutSeconds": 3600
    }
  }
}
```

## Testing

- Unit test remote `ModelPath` materialization with a fake `IModelDistributionProvider`.
- Unit test local file path materialization and missing local file failure.
- Unit test concurrent `EnsureLoadedAsync` calls only load once.
- Unit test warmup calls provider-level load before prime.
- Unit test warmup failure when download/materialization fails.
- Extend AppHost composition tests for `CacheDirectory`, `DownloadTimeoutSeconds`, and Scalar waiting for API when supported.
- Extend logging contract coverage so local model and warmup logs use `[LOCAL-*]` tags and never `[ROUTER-*]`.
- Run focused local inference tests, AppHost tests, full solution tests, and `dotnet build -warnaserror`.

## Acceptance Criteria

- A Hugging Face GGUF URL in `ModelPath` downloads into the configured cache directory.
- Subsequent startups reuse the cached GGUF without downloading again.
- `LocalGemma` loads from the resolved local path, not the original URL.
- API readiness is unhealthy until download, LLamaSharp load, and warmup prime complete.
- Scalar, OpenWebUI, and Agent DevUI do not start until the API is ready.
- Offline mode routes only through `LocalGemma`.
- Warmup/model-source logs use `[LOCAL-*]` tags only.
