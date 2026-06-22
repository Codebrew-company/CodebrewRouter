# Research: psmon/AgentZeroLite

**Source:** https://github.com/psmon/AgentZeroLite
**Date researched:** 2026-04-26
**Researcher:** Claude Code

---

## Summary

**AgentZeroLite** is a .NET 10 Windows desktop application built by psmon that acts as an **AI-era terminal multiplexer** with built-in AI-to-AI conversation capabilities. It enables running multiple CLIs (Claude Code, Codex, PowerShell, etc.) in parallel ConPTY tabs and orchestrates them through a chat bot that can forward commands between terminals, enabling direct AI ↔ AI dialogue without cloud relays.

### Core Innovation

The project's headline feature is **cross-terminal AI dialogue**: two different AI CLIs (e.g., Claude in tab 0, Codex in tab 1) can communicate with each other through AgentZero's IPC mechanism (`WM_COPYDATA` + memory-mapped files). One AI can send messages to another terminal by running `AgentZeroLite.ps1 terminal-send <group> <tab> "text"`, and read replies with `terminal-read`. This creates autonomous AI-to-AI conversations within separate ConPTY sessions, all orchestrated through a single GUI.

---

## Architecture Overview

### Project Structure

```
AgentZeroLite.slnx
├── AgentZeroWpf (WinExe, net10.0-windows)    — WPF GUI + ConPTY hosting
├── ZeroCommon (ClassLib, net10.0)            — UI-free shared logic, actors, services
├── AgentTest (xUnit, net10.0-windows)        — WPF-dependent tests
└── ZeroCommon.Tests (xUnit, net10.0)         — Headless tests for shared logic
```

**Dependency rule:** `AgentZeroWpf → ZeroCommon` (never reversed). Anything without WPF/Win32 dependencies belongs in `ZeroCommon`.

### Actor Topology (Akka.NET)

```
/user/stage                  — StageActor: supervisor, lifecycle broker
    /bot                     — AgentBotActor: chat/key mode, UI callback routing
    /ws-<workspace>          — WorkspaceActor: owns terminals for a folder
        /term-<id>           — TerminalActor: wraps ITerminalSession (ConPTY)
```

All messages defined in `ZeroCommon/Actors/Messages.cs`. The actor model enables supervised terminal lifecycle and clean shutdown without deadlocking the UI thread.

### Single Executable, Dual Modes

`AgentZeroLite.exe` is built as a `WinExe` but operates in two modes decided in `App.OnStartup`:

1. **CLI mode** (`-cli` flag): `CliHandler.Run` executes the command, sends IPC to the running GUI via `WM_COPYDATA`, waits for response from memory-mapped files, and exits.
2. **GUI mode** (default): Single-instance check via named mutex, actor system initialization, then `MainWindow` shows.

---

## Key Technologies & Dependencies

### Core Framework

| Component | Version/Package | Purpose |
|---|---|---|
| **.NET 10** | `net10.0` / `net10.0-windows` | Target framework (preview as of 2026-04) |
| **WPF** | Built-in | UI framework for desktop GUI |
| **Akka.NET** | `1.5.40` | Actor model for terminal lifecycle, supervision |
| **EF Core + SQLite** | `10.0.0-preview.3.25171.6` | Persistence (`%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db`) |

### Terminal Infrastructure

| Package | Version | Purpose |
|---|---|---|
| **EasyWindowsTerminalControl** | `1.0.36` | ConPTY wrapper for WPF |
| **CI.Microsoft.Terminal.Wpf** | `1.22.250204002` | Official MS Terminal WPF component |
| **conpty.dll** | `1.22.250314001` | Native ConPTY DLL (copied to output) |
| **Microsoft.Terminal.Control.dll** | `1.22.250204002` | Native terminal control DLL |

**Critical build note:** The `.csproj` hard-codes `$(NuGetPackageRoot)` paths to copy native DLLs. If versions are bumped, the `<Content Include=...>` paths must be updated or ConPTY tabs silently fail to start.

### UI Components

| Package | Version | Purpose |
|---|---|---|
| **Dirkster.AvalonDock** | `4.72.1` | Dockable/floating panes (AgentBot, Notes, Output) |
| **AvalonEdit** | `6.3.1.120` | Code/text editor control |
| **Microsoft.Web.WebView2** | `1.0.3124.44` | Markdown + Mermaid rendering |
| **SharpVectors.Wpf** | `1.8.5` | SVG rendering support |
| **Docnet.Core** | `2.6.0` | PDF preview in notes pane |

### On-Device AI (AIMODE)

| Package | Version | Purpose |
|---|---|---|
| **LLamaSharp** | `0.26.0` | .NET bindings for llama.cpp |
| **Custom llama.cpp builds** | Commit `3f7c29d` | CPU + Vulkan variants in `runtimes/win-x64-{cpu,vulkan}/native/` |

**AIMODE** is an experimental feature that runs a local LLM (Gemma 4 or Nemotron) as an in-shell coordinator. The LocalLLM acts as a "secretary" that routes user requests to the appropriate terminal AI (Claude, Codex), waits for responses, and summarizes results—all without leaving the machine.

---

## Dependency Documentation Links

### .NET & Microsoft Core

- **.NET 10 Preview:** https://dotnet.microsoft.com/download/dotnet/10.0
- **WPF Documentation:** https://learn.microsoft.com/windows/apps/winui/winui3/
- **Entity Framework Core 10:** https://learn.microsoft.com/ef/core/what-is-new/ef-core-10.0/whatsnew
- **Microsoft.Terminal.Wpf:** https://github.com/microsoft/terminal (ConPTY/Terminal component source)

### Actor Model & Messaging

- **Akka.NET v1.5:** https://getakka.net/
- **Akka.NET Documentation:** https://getakka.net/articles/intro/what-is-akka.html
- **Akka.NET DI Integration:** https://getakka.net/articles/actors/dependency-injection.html

### Terminal & ConPTY

- **ConPTY Overview:** https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
- **EasyWindowsTerminalControl:** https://github.com/fernandreu/office-ribbonx-editor/tree/master/src/EasyWindowsTerminalControl
- **CI.Microsoft.Terminal.Wpf NuGet:** https://www.nuget.org/packages/CI.Microsoft.Terminal.Wpf

### UI Libraries

- **AvalonDock:** https://github.com/Dirkster99/AvalonDock
- **AvalonEdit:** https://github.com/icsharpcode/AvalonEdit
- **WebView2:** https://learn.microsoft.com/microsoft-edge/webview2/
- **SharpVectors:** https://github.com/ElinamLLC/SharpVectors

### On-Device LLM

- **LLamaSharp:** https://github.com/SciSharp/LLamaSharp
- **llama.cpp:** https://github.com/ggerganov/llama.cpp
- **GBNF Grammar (tool calling):** https://github.com/ggerganov/llama.cpp/blob/master/grammars/README.md
- **Gemma 4 Model:** https://huggingface.co/google/gemma-4-9b-it

### IPC & Native Interop

- **WM_COPYDATA:** https://learn.microsoft.com/windows/win32/dataxchg/wm-copydata
- **Memory-Mapped Files (.NET):** https://learn.microsoft.com/dotnet/standard/io/memory-mapped-files

---

## Features Relevant to CodebrewRouter

### 1. **Multi-Terminal AI Orchestration**

AgentZeroLite's core pattern—running multiple AI CLIs in parallel and enabling them to talk to each other—is directly analogous to what CodebrewRouter needs for **multi-agent routing**.

**Relevance to CodebrewRouter:**
- CodebrewRouter currently routes one request to one provider. AgentZeroLite demonstrates orchestrating **multiple concurrent AI sessions** with cross-talk capability.
- The IPC mechanism (`WM_COPYDATA` + MMF) could inspire a similar **agent coordination protocol** within CodebrewRouter's multi-agent flows.

### 2. **Akka.NET Actor Model for Agent Supervision**

AgentZeroLite uses Akka.NET actors to supervise terminal lifecycle, handle crashes gracefully, and avoid UI thread deadlocks. Each terminal is an isolated actor; failures in one don't cascade.

**Relevance to CodebrewRouter:**
- CodebrewRouter has no multi-agent orchestration yet. If we implement **Microsoft Agent Framework** (per `research/agent-framework-local-to-production.md`), Akka.NET could provide the supervision layer for `AgentThread` instances.
- Actor-based routing would enable **circuit breaker** patterns per provider without blocking the main request pipeline.

**Example ADR crosswalk:**
- ADR-0009 (Squad Orchestration) mentions multi-agent coordination. Akka.NET is a proven .NET actor model that could replace ad-hoc orchestration.
- ADR-0010 (Parallel Orchestrator) mentions parallel task dispatch. Akka actors naturally support parallel message processing.

### 3. **On-Device LLM (AIMODE) as Task Classifier**

AgentZeroLite runs a local Gemma 4 model with GBNF-constrained tool calling to route user requests to the right terminal AI. The LocalLLM doesn't do heavy reasoning—it's a **secretary/dispatcher**.

**Relevance to CodebrewRouter:**
- CodebrewRouter uses `OllamaMetaRoutingStrategy` with a similar pattern: send prompt to Ollama, get `RouteDestination` enum name back.
- AgentZeroLite's `AgentToolLoop` (generate → tool → feed-back → repeat) is the **function-call agentic loop** that turns text completion into action. This is the missing piece for MCP tool execution in CodebrewRouter.
- The GBNF grammar that forces tool-call JSON output could replace CodebrewRouter's "hope the model returns an enum name" approach with a **guaranteed structured output**.

**Files to study for GBNF tool calling:**
- `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.Gbnf`
- `Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs`
- `Project/ZeroCommon/Actors/AgentReactorActor.cs`

### 4. **IPC for External Scripts to Drive the Application**

AgentZeroLite's CLI can send commands to a running GUI and read responses synchronously. This enables external scripts (PowerShell, Python, bash) to orchestrate the app.

**Relevance to CodebrewRouter:**
- CodebrewRouter exposes an HTTP API but has no **local IPC** for scripting. If we wanted a `CodebrewRouter.exe -cli` mode similar to AgentZeroLite, we could add WM_COPYDATA or named pipes.
- More realistically: AgentZeroLite's pattern shows how to **expose a dual-mode executable** (GUI + CLI in the same binary). This could inspire a future `Blaze.LlmGateway.Cli` project that wraps the API as a CLI tool.

### 5. **Security-First Documentation**

AgentZeroLite's README includes a prominent **Security Notice** explaining the prompt injection → OS command execution risk surface. This is critical for any tool that bridges LLMs and system shells.

**Relevance to CodebrewRouter:**
- ADR-0008 (cloud-egress policy) touches security but doesn't explicitly warn about **tool execution risks** or **prompt injection in MCP tools**.
- We should add a similar security notice to `CLAUDE.md` and `README.md` warning that MCP tool execution = code execution surface.

---

## Architecture Patterns to Borrow

### Pattern 1: Actor-Based Multi-Agent Routing

**Current CodebrewRouter gap:** No supervision model for multiple concurrent provider calls. If we implement streaming failover or multi-agent orchestration, we need lifecycle management.

**AgentZeroLite solution:**
```
StageActor
  ├── WorkspaceActor (per project)
  │   └── TerminalActor (per AI session)
  └── AgentBotActor (dispatcher)
```

**How to adapt for CodebrewRouter:**
```
RouterStageActor
  ├── ProviderPoolActor (per RouteDestination)
  │   └── ProviderSessionActor (per request, circuit-breaker-wrapped)
  └── McpToolCoordinatorActor (tool discovery + execution)
```

Each provider call becomes an actor with a timeout supervisor. Circuit breaker state lives in the actor. Failed actors restart with exponential backoff.

**Required packages:**
- `Akka` (already in ZeroCommon)
- `Akka.DependencyInjection` (already in ZeroCommon)

### Pattern 2: GBNF-Constrained Tool Calling

**Current CodebrewRouter gap:** `OllamaMetaRoutingStrategy` sends a prompt and hopes the model returns a valid enum name. No guaranteed structure.

**AgentZeroLite solution:** GBNF grammar forces the sampler to only emit valid tool-call JSON:
```json
{"tool": "send_to_terminal", "args": {"group": 0, "tab": 1, "text": "hi"}}
```

**How to adapt for CodebrewRouter:**
- Create a `RouteDestination.Gbnf` grammar that only allows valid enum names.
- Pass the grammar to Ollama via `ChatOptions` with the `grammar` parameter (LLamaSharp supports this).
- The classifier can't hallucinate invalid destinations anymore.

**Example grammar snippet:**
```gbnf
root ::= "{" ws "\"destination\":" ws destination "}"
destination ::= "\"AzureFoundry\"" | "\"FoundryLocal\"" | "\"GithubModels\""
ws ::= [ \t\n]*
```

### Pattern 3: Dual-Mode Executable (GUI + CLI)

**Current CodebrewRouter architecture:** `Blaze.LlmGateway.Api` is an HTTP service. No CLI wrapper.

**AgentZeroLite solution:** Single executable with mode switching in `App.OnStartup`:
```csharp
if (args.Contains("-cli")) {
    CliHandler.Run(args);
    Environment.Exit(0);
} else {
    // Show GUI
}
```

**How to adapt for CodebrewRouter:**
- Add a `Blaze.LlmGateway.Cli` project (WinExe or Exe) that:
  - Checks for `-cli` flag
  - If present: sends HTTP to `localhost:5000` (or IPC to a running API)
  - If absent: launches the API in-process with `WebApplication.CreateBuilder()`
- Enables `CodebrewRouter.exe chat "hello"` for local scripting.

---

## Security Considerations

### 1. Prompt Injection → Command Execution Surface

AgentZeroLite explicitly warns:
> "This app directly drives and brokers CLIs, and the AgentChatBot forwards typed text / raw keystrokes into the active terminal. That means there is a real surface where prompt injection can turn into OS command execution."

**CodebrewRouter parallel:**
- `McpToolDelegatingClient` appends MCP tools to `ChatOptions.Tools`.
- MEAI's `FunctionInvokingChatClient` auto-executes tools.
- A malicious prompt could trick the LLM into calling a dangerous MCP tool.

**Mitigation strategies (from AgentZeroLite's approach):**
1. **Audit all MCP tools before registration.** Never auto-discover external MCP servers.
2. **Sandbox tool execution.** Run MCP servers in containers or AppArmor/SELinux profiles.
3. **User confirmation for destructive tools.** File writes, network calls, shell commands require explicit approval.
4. **Rate limiting on tool calls.** Prevent runaway loops.

### 2. Native DLL Verification

AgentZeroLite ships custom-compiled `llama.cpp` DLLs. These are **potential supply-chain risks**.

**Recommendation:**
- If CodebrewRouter adopts LLamaSharp for local routing, pin the official NuGet package and avoid custom DLLs.
- If custom builds are needed (e.g., for Vulkan support), provide a reproducible build script and hash verification.

---

## Lessons for CodebrewRouter Development

### DO Adopt

| Pattern | Why | Effort |
|---|---|---|
| **Akka.NET for multi-agent supervision** | Proven .NET actor model; clean lifecycle | Medium |
| **GBNF grammars for structured LLM output** | Eliminates hallucinated enum names in routing | Small |
| **Security-first documentation** | Warn users about tool execution risks upfront | Small |
| **Actor-based circuit breaker** | Per-provider failure isolation without blocking | Medium |

### DON'T Adopt (Yet)

| Pattern | Why Not | Alternative |
|---|---|---|
| **WPF GUI for routing** | CodebrewRouter is a headless API | Keep HTTP API; add CLI wrapper if needed |
| **Custom llama.cpp builds** | Supply-chain risk; hard to maintain | Use official LLamaSharp NuGet |
| **WM_COPYDATA IPC** | Windows-only; fragile | HTTP or gRPC for cross-platform IPC |

---

## Proposed Next Steps for CodebrewRouter

### Phase 1: GBNF-Based Routing (Small, High Value)

1. Add `RouteDestination.Gbnf` grammar to `Blaze.LlmGateway.Infrastructure/Routing/`.
2. Update `OllamaMetaRoutingStrategy` to pass the grammar via `ChatOptions`.
3. Write a test that verifies invalid destinations cannot be sampled.

**Files to change:**
- `Blaze.LlmGateway.Infrastructure/OllamaMetaRoutingStrategy.cs`
- `Blaze.LlmGateway.Tests/OllamaMetaRoutingStrategyTests.cs`

### Phase 2: Akka.NET Multi-Agent Supervisor (Medium, High Value)

1. Add `Akka` and `Akka.DependencyInjection` to `Blaze.LlmGateway.Infrastructure`.
2. Create `RouterStageActor`, `ProviderPoolActor`, `ProviderSessionActor`.
3. Wire into `AddLlmInfrastructure` as a singleton actor system.
4. Migrate `LlmRoutingChatClient` to dispatch via `Tell` instead of direct keyed DI resolution.

**ADR to write:** `ADR-0011-actor-based-provider-supervision.md`

### Phase 3: MCP Tool Execution via AgentToolLoop Pattern (Medium, High Value)

1. Study `AgentZeroLite/Project/ZeroCommon/Llm/Tools/AgentToolLoop.cs`.
2. Implement `McpToolExecutor` that follows the generate → tool → feed-back loop.
3. Replace `FunctionInvokingChatClient` with our own loop to gain control over tool approval.

**Related to:** MCP fix (priority #1 in `research/agent-framework-local-to-production.md`)

---

## Files Mirrored Locally

Per `research/README.md` conventions, key source files are mirrored under `research/sources/psmon-agentzerolite/`:

```
research/sources/psmon-agentzerolite/
├── README.md                              — Project overview
├── CLAUDE.md                              — Build & architecture guide
├── AgentZeroLite.slnx                     — Solution structure
├── Project/
│   ├── ZeroCommon/ZeroCommon.csproj       — Shared library dependencies
│   ├── AgentZeroWpf/AgentZeroWpf.csproj   — WPF app dependencies
│   └── ZeroCommon/Llm/Tools/
│       ├── AgentToolGrammar.Gbnf          — GBNF tool-call grammar
│       └── AgentToolLoop.cs               — Function-call agentic loop
└── Docs/
    ├── gemma4-gpu-load-failures.md        — GPU troubleshooting
    ├── gemma4-performance-benchmarks.md   — Performance baselines
    └── llm/                                — LLM integration notes
```

---

## References

- **GitHub Repository:** https://github.com/psmon/AgentZeroLite
- **Sister Repos:**
  - harness-kakashi: https://github.com/psmon/harness-kakashi (harness training sandbox)
  - pencil-creator: https://github.com/psmon/pencil-creator (design system generator)
  - memorizer-v1: https://github.com/psmon/memorizer-v1 (vector memory MCP server)
- **Akka.NET:** https://getakka.net/
- **LLamaSharp:** https://github.com/SciSharp/LLamaSharp
- **llama.cpp:** https://github.com/ggerganov/llama.cpp
- **ConPTY:** https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
- **GBNF Grammars:** https://github.com/ggerganov/llama.cpp/blob/master/grammars/README.md

---

## Appendix: Key Dependency Versions

| Package | Version | License | Security Notes |
|---|---|---|---|
| Akka | 1.5.40 | Apache-2.0 | Stable; mature .NET actor framework |
| Akka.DependencyInjection | 1.5.40 | Apache-2.0 | DI integration for Akka |
| LLamaSharp | 0.26.0 | MIT | Bindings for llama.cpp; GPU support |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.0-preview.3 | MIT | .NET 10 preview; stable for EF migrations |
| EasyWindowsTerminalControl | 1.0.36 | MIT | ConPTY WPF wrapper |
| CI.Microsoft.Terminal.Wpf | 1.22.250204002 | MIT | Official MS Terminal component |
| Dirkster.AvalonDock | 4.72.1 | Ms-PL | Docking library for WPF |
| Microsoft.Web.WebView2 | 1.0.3124.44 | Proprietary (MS) | Chromium-based WebView for .NET |

**Security patches applied:**
- `Microsoft.Build.Tasks.Core 17.14.28` (overrides EF Design's 17.7.2 for NU1903 / GHSA-h4j7-5rxr-p4wc)
- `System.Security.Cryptography.Xml 10.0.7` (overrides 9.0.0 for GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf)

---

**End of Research Report**
