---
name: codebrewrouter-architecture-routing
description: Project-local CodebrewRouter routing and architecture guidance. Use when changing, reviewing, or explaining the MEAI pipeline, IChatClient composition, keyed provider DI, RouteDestination/provider identity, routing strategies, streaming contracts, fallback/failover, context sizing, model catalog behavior, or OpenAI-compatible API flow in Blaze.LlmGateway.
---

# CodebrewRouter Architecture Routing

## Start Here

Read the smallest useful set before editing:

- `AGENTS.md`
- `README.md`
- `.github/copilot-instructions.md`
- `Docs/design/adr/`
- `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs`
- `Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs`
- `Blaze.LlmGateway.Infrastructure/RoutingStrategies/`
- `Blaze.LlmGateway.Api/ChatCompletionsEndpoint.cs`
- `Blaze.LlmGateway.Api/OpenAiProtocolMapper.cs`
- `Blaze.LlmGateway.Core/RouteDestination.cs`
- `Blaze.LlmGateway.Core/Configuration/`

## Rules

- Keep all LLM calls on `Microsoft.Extensions.AI` abstractions unless the user explicitly asks for a protocol adapter.
- Keep provider registration and pipeline assembly in infrastructure extension methods; keep `Program.cs` minimal.
- Resolve providers by keyed DI using names aligned with `RouteDestination`, router output text, and configuration keys.
- Preserve OpenAI-compatible streaming shape and the final `data: [DONE]` terminator.
- Treat routing, fallback, context sizing, and provider selection as behavioral contracts that need targeted tests.
- When changing router telemetry, also use `codebrewrouter-logging-contract`.
- When changing cloud escalation, MCP, tools, or provider secrets, also use `codebrewrouter-mcp-provider-security`.

## Review Checklist

- Confirm the selected provider path is testable without real provider credentials.
- Confirm streaming and non-streaming behavior still map through the OpenAI protocol layer.
- Confirm cancellation tokens flow through new async paths.
- Confirm new destinations update enum values, DI registrations, model catalog behavior, configuration, and tests together.
- Confirm fallback behavior is explicit when a provider is unavailable, skipped, or over context budget.

## Verification

Prefer targeted tests first, then broader checks when production code changes:

```powershell
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~Routing
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~OpenAi
dotnet build Blaze.LlmGateway.slnx --no-incremental -warnaserror
```

For API contract changes, add the relevant endpoint or protocol mapper tests before relying on a full build alone.
