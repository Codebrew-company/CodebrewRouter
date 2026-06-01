#!/usr/bin/env python3
"""Scan CodebrewRouter source tree and produce a compact context string for the pipeline planner."""
import json, os, re

CODEBREW_DIR = "/mnt/data/src/CodebrewRouter"

def read_trimmed(path, max_lines=60):
    p = os.path.join(CODEBREW_DIR, path)
    if not os.path.exists(p):
        return None
    with open(p) as f:
        lines = f.readlines()
    if len(lines) <= max_lines:
        return "".join(lines)
    return "".join(lines[:max_lines]) + "\n... (truncated)"

def extract_interface_names(path):
    p = os.path.join(CODEBREW_DIR, path)
    if not os.path.exists(p):
        return []
    names = []
    with open(p) as f:
        for line in f:
            m = re.search(r'(interface|class|record)\s+(\w+)', line)
            if m:
                names.append(m.group(2))
    return names

files = {
    "Infrastructure.cs": "Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs",
    "Router.cs": "Blaze.LlmGateway.Infrastructure/CodebrewRouterChatClient.cs",
    "ModelResolver.cs": "Blaze.LlmGateway.Infrastructure/ModelSelectionResolver.cs",
    "ProviderBuilder.cs": "Blaze.LlmGateway.Infrastructure/Provider/CodebrewRouterProviderBuilder.cs",
}

sections = {}

# Build architecture overview
sections["solution_structure"] = [
    "Core/ — domain types: RouteDestination enum, LlmGatewayOptions config",
    "Infrastructure/ — routing middleware, MCP integration, strategies, ALL pipeline components",
    "Api/ — Minimal API host, Program.cs wires DI, exposes /v1/chat/completions SSE",
    "AppHost/ — .NET Aspire orchestration, GitHub Models provisioning, DevUI playgrounds",
    "ServiceDefaults/ — OpenTelemetry, HTTP resilience, health checks, service discovery",
    "Tests/ — xUnit + Moq (95% coverage target)",
    "Benchmarks/ — BenchmarkDotNet for provider latency/routing overhead"
]

sections["pipeline_layers"] = [
    "McpToolDelegatingClient — injects MCP tools into ChatOptions (unkeyed IChatClient)",
    "  → LlmRoutingChatClient — resolves target provider via IRoutingStrategy",
    "    → [Keyed IChatClient].UseFunctionInvocation() — per-provider model call"
]

sections["providers"] = [
    "3 selectable destinations (keyed DI): AzureFoundry, FoundryLocal, GithubModels",
    '1 internal classifier: OllamaRouter (not exposed via /v1/models)',
    "1 virtual facade: CodebrewRouter — task-routing over the 3 real providers",
    "14 OpenCodeGo models registered for future use (Phase 3)"
]

sections["routing"] = [
    "Primary: OllamaMetaRoutingStrategy — Ollama router model classifies RouteDestination",
    "Fallback: KeywordRoutingStrategy — parses keywords ('foundry local', 'github', 'azure')",
    "Default destination: AzureFoundry",
    "CURRENTLY: OllamaRouter DISABLED (known connectivity issues), keyword-only active"
]

sections["mcp_status"] = [
    "McpConnectionManager.StartAsync() — PLACEHOLDER, not fully wired",
    "McpToolDelegatingClient.AppendMcpTools — needs mapping to HostedMcpServerTool instances",
    "MCP tool injection currently DISABLED in InfrastructureServiceExtensions (commented out)",
    "Tool invocation handlers — placeholder in TranslateTools"
]

sections["known_gaps"] = [
    "Circuit breaker — NOT IMPLEMENTED (high-priority Phase 2; builder has validation stubs only)",
    "Streaming failover — basic first-chunk probe exists, but mid-stream failure handling incomplete",
    "Authentication — no API key or bearer token enforcement on gateway",
    "Rate limiting / cost tracking — NOT IMPLEMENTED",
    "OpenCodeGo model tokenizers — Phase 3b, graceful fallback to gpt-4o encoding",
    "Integration test with real GitHub Models endpoint — scaffolded, awaits credentials"
]

sections["recent_fixes"] = [
    "✅ GithubModels registration fixed",
    "✅ OpenAI wire format (proper chunk sequencing + role/finish_reason)",
    "✅ Function calling forward (tools → AIFunctions)",
    "✅ Vision support (multimodal via ChatMessageContentConverter)",
    "✅ Streaming failover (first-chunk probe + fallback chain)"
]

sections["key_interfaces"] = {
    "Core routing": ["IRoutingStrategy", "OllamaMetaRoutingStrategy", "KeywordRoutingStrategy", "IFailoverStrategy", "ConfiguredFailoverStrategy"],
    "Infrastructure": extract_interface_names("Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs"),
    "Provider": extract_interface_names("Blaze.LlmGateway.Infrastructure/Provider/CodebrewRouterProviderBuilder.cs"),
    "MCP": ["McpToolDelegatingClient", "McpConnectionManager", "HostedMcpServerTool"],
    "Task classification": ["ITaskClassifier", "OllamaTaskClassifier", "KeywordTaskClassifier"],
    "Context handling": ["IContextCompactor", "IPromptCleaner", "GemmaPromptCleaner", "NoopPromptCleaner"],
}

# Build the output
parts = []
parts.append("## CodebrewRouter Project Structure")
parts.append("Located at /mnt/data/src/CodebrewRouter (Blaze.LlmGateway .NET 10 solution)")
parts.append("")
parts.append("### Solution Layout")
for item in sections["solution_structure"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### MEAI Middleware Pipeline (outer → inner)")
for item in sections["pipeline_layers"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### Provider Registrations (Keyed DI)")
for item in sections["providers"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### Routing Strategy")
for item in sections["routing"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### MCP Integration Status")
for item in sections["mcp_status"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### Known Implementation Gaps (High Priority)")
for item in sections["known_gaps"]:
    parts.append(f"- {item}")

parts.append("")
parts.append("### Completed Phase 1 Fixes")
for item in sections["recent_fixes"]:
    parts.append(f"- {item}")

context = "\n".join(parts)

# Write to a temp file that the pipeline can source
out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".codebase_context.txt")
with open(out_path, "w") as f:
    f.write(context)

print(f"✅ Codebase context written ({len(context):,} chars) to {out_path}")
