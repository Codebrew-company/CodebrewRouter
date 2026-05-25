# CodebrewRouter Logging Contract

This contract keeps gateway and agent telemetry searchable in Aspire logs and CLI output.

## Project Scope

The logging guardian skill and agent definitions are project-level only. Keep them in this repository:

- Codex skill: `.agents/skills/codebrewrouter-logging-contract/`
- GitHub Copilot CLI agent: `.github/agents/codebrewrouter-logging.agent.md`
- OpenCode agent: `.opencode/agents/codebrewrouter-logging.md`
- OpenCode command: `.opencode/commands/codebrewrouter-logging.md`
- Copilot-compatible project command: `.claude/commands/codebrewrouter-logging.md`
- Copilot CLI plugin command: `.github/plugins/codebrewrouter-logging/`
- Codex plugin command package: `plugins/codebrewrouter-logging/`

Do not copy these definitions into user-global agent or skill directories unless the user explicitly asks for a global install.

## Command Entry Points

Use `/codebrewrouter-logging [files, diff, or change summary]` when a tool supports project slash commands.

- OpenCode loads the project command from `.opencode/commands/codebrewrouter-logging.md` and runs it with the `codebrewrouter-logging` subagent.
- GitHub Copilot CLI can use `.claude/commands/codebrewrouter-logging.md` as the repo command. The same workflow is also packaged as `.github/plugins/codebrewrouter-logging/` for `copilot --plugin-dir .github/plugins/codebrewrouter-logging` or `gh copilot -- --plugin-dir .github/plugins/codebrewrouter-logging`.
- Codex already has the project skill in `.agents/skills/codebrewrouter-logging-contract/`. The optional repo-local command package lives in `plugins/codebrewrouter-logging/` and is listed in `.agents/plugins/marketplace.json` for Codex plugin flows.

## Router Tags

All request routing telemetry must start with one of these exact tags:

| Tag | Level | Meaning |
|---|---|---|
| `[ROUTER-START]` | Information | Request entry and message count. |
| `[ROUTER-CLEAN]` | Information | Prompt characters before/after cleanup and elapsed milliseconds. |
| `[ROUTER-RESOLVE]` | Information | Task type, token count, provider chain, and elapsed milliseconds. |
| `[ROUTER-CONTEXT]` | Debug | Per-provider context budget check. |
| `[ROUTER-COMPACT]` | Information | Context compaction before/after token counts. |
| `[ROUTER-SKIP]` | Warning | Provider skipped with reason, token count, and budget when relevant. |
| `[ROUTER-TRY]` | Information | Provider attempt number and model name. |
| `[ROUTER-PROBE]` | Information | First chunk latency and success/failure. |
| `[ROUTER-SUCCESS]` | Information | Finish reason, usage stats, and elapsed milliseconds. |
| `[ROUTER-FAIL]` | Warning | Provider failure message. |
| `[ROUTER-EXHAUSTED]` | Warning | All providers failed and fallback behavior. |
| `[ROUTER-MIDSTREAM-FAIL]` | Warning | Streaming disconnect after response started. |
| `[ROUTER-STREAM-COMPLETE]` | Information | Total chunks, model, and elapsed milliseconds. |

Use `RouterLog.Write(...)` instead of hand-written router log messages. Add or update tests in `Blaze.LlmGateway.Tests/RouterLoggingContractTests.cs` whenever a router event changes.

## Local Warmup Tags

Local model startup and Aspire readiness telemetry must use `[LOCAL-WARMUP-*]` tags, not `[ROUTER-*]`, because warmup is process lifecycle work rather than request routing.

| Tag | Level | Meaning |
|---|---|---|
| `[LOCAL-WARMUP-START]` | Information | Local Gemma warmup started and startup-blocking mode was recorded. |
| `[LOCAL-WARMUP-LOAD]` | Information | Local Gemma model load state was resolved. |
| `[LOCAL-WARMUP-PRIME]` | Information | One-token warmup inference began. |
| `[LOCAL-WARMUP-READY]` | Information | Local Gemma model loaded, primed, and ready for first chat. |
| `[LOCAL-WARMUP-SKIP]` | Information | Warmup was intentionally skipped because local inference or warmup was disabled, or startup was allowed without a model path. |
| `[LOCAL-WARMUP-FAIL]` | Warning | Warmup failed, including missing model path when startup must block. |

## Local Model Materialization Tags

Provider-level local model source and cache events must use `[LOCAL-MODEL-*]` tags, not `[ROUTER-*]`, because model materialization is process lifecycle work rather than request routing. Do not emit these events through `RouterLog.Write(...)`.

| Tag | Level | Meaning |
|---|---|---|
| `[LOCAL-MODEL-RESOLVE]` | Information | Local path or model source resolved to an absolute GGUF path. |
| `[LOCAL-MODEL-CACHE-HIT]` | Information | Remote model URL was already cached locally. |
| `[LOCAL-MODEL-DOWNLOAD-START]` | Information | Remote model download started. |
| `[LOCAL-MODEL-DOWNLOAD-READY]` | Information | Remote model download completed and cached path is ready. |
| `[LOCAL-MODEL-DOWNLOAD-FAIL]` | Error | Remote model download failed. |

## Agent Tags

Agent lifecycle telemetry must use `[AGENT-*]` tags so it does not collide with gateway request routing.

| Tag | Level | Meaning |
|---|---|---|
| `[AGENT-START]` | Information | Agent/session start, agent name, and objective. |
| `[AGENT-PLAN]` | Information | Chosen plan, scope, and constraints. |
| `[AGENT-ACTION]` | Information | Tool action or implementation step started. |
| `[AGENT-HANDOFF]` | Information | Delegation to another agent or external CLI. |
| `[AGENT-RESULT]` | Information | Result from a step, tool, or delegated agent. |
| `[AGENT-FAIL]` | Warning | Recoverable agent failure, blocked step, or failed verification. |
| `[AGENT-COMPLETE]` | Information | Final outcome and verification evidence. |

New agent code, prompts, and custom-agent files must mention this contract and use `[AGENT-*]` for their own lifecycle logs. Do not use `[ROUTER-*]` for agent orchestration unless the agent is changing runtime gateway routing logs.
