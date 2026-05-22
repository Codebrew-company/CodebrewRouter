---
name: codebrewrouter-mcp-provider-security
description: Project-local CodebrewRouter MCP, provider, and agent security guidance. Use when changing or reviewing MCP configuration, McpConnectionManager, McpToolDelegatingClient, provider credential flow, AppHost secret injection, cloud egress, tool exposure, fallback chains, agent governance, supply-chain risk, or security-sensitive routing behavior.
---

# CodebrewRouter MCP Provider Security

## Start Here

Read the narrowest relevant set:

- `AGENTS.md`
- `Docs/design/adr/0008-cloud-escalation-policy.md`
- `Docs/engineering/logging-contract.md`
- `.github/copilot-instructions.md`
- `Blaze.LlmGateway.Infrastructure/McpConnectionManager.cs`
- `Blaze.LlmGateway.Infrastructure/McpToolDelegatingClient.cs`
- `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs`
- `Blaze.LlmGateway.AppHost/`
- `Blaze.LlmGateway.Api/appsettings.json`
- `Blaze.LlmGateway.Api/appsettings.Development.json`
- any `.mcp.json` file in scope

## Security Checks

- Verify secrets flow through Aspire parameters, user secrets, or environment variables, not committed config.
- Check MCP stdio commands and args for hardcoded secrets, shell injection risk, and unpinned package versions.
- Prefer pinned package or image versions over floating `latest` style references when practical.
- Treat hosted tools, MCP tool injection, model fallback, and cloud escalation as security-relevant behavior.
- Apply ADR-0008 before adding remote provider calls, external tool calls, or new cloud egress paths.
- Make provider failure behavior explicit so sensitive prompts do not silently route to an unintended provider.
- Keep agent lifecycle telemetry on `[AGENT-*]` and router request telemetry on `[ROUTER-*]`.

## Review Output

When reviewing security, lead with actionable findings:

- file and line
- risk
- concrete fix
- verification to run

If no issues are found, state that clearly and list residual risks such as missing live provider credentials or disabled MCP servers.

## Verification

Use targeted tests when touched areas have coverage:

```powershell
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~Mcp
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~AppHost
dotnet test Blaze.LlmGateway.Tests\Blaze.LlmGateway.Tests.csproj --filter FullyQualifiedName~RouterLoggingContractTests
```

For code changes affecting security-sensitive paths, also run the full build with warnings as errors.
