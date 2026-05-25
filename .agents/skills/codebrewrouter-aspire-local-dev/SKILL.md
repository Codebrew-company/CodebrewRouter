---
name: codebrewrouter-aspire-local-dev
description: Project-local CodebrewRouter Aspire and local development guidance. Use when changing, running, debugging, or explaining Blaze.LlmGateway.AppHost, ServiceDefaults, local inference, local model warmup, Foundry Local, AppHost parameters/secrets, provider environment wiring, health checks, Open WebUI, Agent Framework DevUI, or local run/test troubleshooting.
---

# CodebrewRouter Aspire Local Dev

## Start Here

Read the smallest useful set:

- `README.md`
- `AGENTS.md`
- `.github/copilot-instructions.md`
- `Blaze.LlmGateway.AppHost/Program.cs`
- `Blaze.LlmGateway.AppHost/AppHostComposition.cs`
- `Blaze.LlmGateway.AppHost/appsettings.json`
- `Blaze.LlmGateway.ServiceDefaults/Extensions.cs`
- `Blaze.LlmGateway.LocalInference/`
- `AZURE_FOUNDRY_SETUP.md`

## Rules

- Keep provider secrets on the AppHost project through Aspire parameters, user secrets, or environment variables.
- Do not move provider secrets into API appsettings files.
- Prefer `dotnet run --project Blaze.LlmGateway.AppHost` for full local orchestration.
- Use direct API runs only when isolating API behavior from Aspire orchestration.
- Preserve `[LOCAL-WARMUP-*]` and `[LOCAL-MODEL-*]` separation from `[ROUTER-*]` telemetry.
- Keep AppHost composition testable without Docker or live provider credentials where possible.
- When local runtime behavior touches provider routing, also use `codebrewrouter-architecture-routing`.

## Troubleshooting Flow

1. Check `git status --short` and avoid unrelated workspace changes.
2. Read AppHost settings and parameter names before changing environment wiring.
3. Reproduce with the narrowest host: AppHost for orchestration issues, API for endpoint issues, tests for composition issues.
4. Inspect logs for local warmup/model tags before treating a startup problem as router failure.
5. Record any required user-secret commands in docs, never with real secret values.

## Verification

Use targeted checks first:

```powershell
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~AppHost
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~LocalInference
dotnet build Blaze.LlmGateway.slnx --no-incremental -warnaserror
```

For runtime verification, start AppHost only when the task requires live orchestration or user-visible local behavior.
