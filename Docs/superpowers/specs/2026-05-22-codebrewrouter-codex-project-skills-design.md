# CodebrewRouter Codex Project Skills Design

## Context

CodebrewRouter already has a broad agent surface: repo-local Codex skills under `.agents/skills/`, GitHub Copilot agents and plugins, OpenCode commands, squad prompts, ADRs, and Superpowers specs/plans. The user wants Codex skills only for this project, not global Codex installs and not additional Copilot or OpenCode packaging.

The new work should curate a small project-local skill pack that helps Codex work inside this repository with better routing, onboarding, security, Aspire, and logging awareness. The design draws inspiration from Awesome GitHub Copilot skills such as codebase knowledge, Aspire, .NET MCP, MCP security audit, agent governance, and .NET design review, but adapts them into CodebrewRouter-specific Codex skills.

## Goals

- Add project-only Codex skills under `.agents/skills/`.
- Keep skills concise, discoverable, and aligned with existing repo conventions.
- Cover all requested workflow areas: architecture/routing, logging, MCP/provider security, Aspire/local development, and codebase onboarding.
- Reuse existing source-of-truth docs instead of duplicating long instructions.
- Avoid touching user-global skill directories, Copilot agents/plugins, OpenCode files, or generated prompt packages.

## Non-Goals

- Do not install skills into `$CODEX_HOME/skills` or any user-global path.
- Do not import Awesome Copilot skills verbatim.
- Do not create or change GitHub Copilot or OpenCode packaging.
- Do not change runtime application code as part of the skill-pack implementation.
- Do not replace the existing squad workflow; these skills should complement it.

## Proposed Skill Pack

### `codebrewrouter-architecture-routing`

Use for changes or reviews involving the MEAI pipeline, `IChatClient` composition, keyed provider DI, routing strategies, streaming contracts, provider identity, failover, context sizing, and model catalog behavior.

Core source material:

- `README.md`
- `.github/copilot-instructions.md`
- `Docs/design/adr/`
- `Blaze.LlmGateway.Infrastructure/`
- `Blaze.LlmGateway.Api/`
- `Blaze.LlmGateway.Core/`

Expected guidance:

- Prefer `Microsoft.Extensions.AI` primitives and existing abstractions.
- Keep provider registration and pipeline assembly in infrastructure extension methods.
- Preserve OpenAI-compatible streaming and final `data: [DONE]` behavior.
- Keep enum values, keyed DI names, router outputs, and configuration aligned.
- Require targeted tests for routing or protocol behavior changes.

### `codebrewrouter-codebase-onboarding`

Use when Codex needs to map the repository, explain architecture, identify likely files for a task, summarize docs, or onboard a contributor into the current state of CodebrewRouter.

Core source material:

- `README.md`
- `Docs/INDEX.md`
- `Docs/design/`
- `Docs/superpowers/specs/`
- `Docs/superpowers/plans/`
- `Blaze.LlmGateway.Tests/TEST_COVERAGE_MATRIX.md`
- Recent git history

Expected guidance:

- Start with repo docs and ADRs before reading implementation files.
- Ignore generated worktrees, `bin/`, `obj/`, `node_modules/`, and mirrored research sources unless the task explicitly requires them.
- Surface known implementation gaps from the current docs.
- Produce concise maps, file inventories, or task-entry recommendations rather than generic explanations.

### `codebrewrouter-mcp-provider-security`

Use for MCP configuration, provider credential flow, cloud egress, tool exposure, agent governance, secrets, supply-chain, and security-sensitive routing reviews.

Core source material:

- `Docs/design/adr/0008-cloud-escalation-policy.md`
- `.github/copilot-instructions.md`
- `Blaze.LlmGateway.Infrastructure/McpConnectionManager.cs`
- `Blaze.LlmGateway.Infrastructure/McpToolDelegatingClient.cs`
- `Blaze.LlmGateway.AppHost/`
- `Blaze.LlmGateway.Api/appsettings*.json`
- `.mcp.json` files if present

Expected guidance:

- Verify secrets are injected through Aspire/user secrets or environment variables, not committed config.
- Check MCP commands for hardcoded secrets, shell injection risk, unpinned packages, and overbroad tool exposure.
- Apply the repo cloud-egress policy before adding remote provider or agent behavior.
- Treat provider fallback, model routing, and tool invocation as security-relevant surfaces.

### `codebrewrouter-aspire-local-dev`

Use for Aspire AppHost, ServiceDefaults, local model startup, local inference, provider parameters, Open WebUI, Agent Framework DevUI, health checks, and local run/test troubleshooting.

Core source material:

- `README.md`
- `.github/copilot-instructions.md`
- `Blaze.LlmGateway.AppHost/`
- `Blaze.LlmGateway.ServiceDefaults/`
- `Blaze.LlmGateway.LocalInference/`
- `AZURE_FOUNDRY_SETUP.md`

Expected guidance:

- Keep secrets on the AppHost project, not the API project.
- Prefer AppHost orchestration for full local development.
- Preserve local warmup and local model telemetry tag separation.
- Use targeted verification commands before full solution checks when diagnosing local issues.

### Existing `codebrewrouter-logging-contract`

Keep the existing skill and refine only if needed for consistency with the pack. It already correctly points to `Docs/engineering/logging-contract.md` and keeps the skill project-level only.

Expected refinement, if implementation reveals drift:

- Keep frontmatter trigger language focused on router and agent logging.
- Ensure verification commands match the current test project and test names.
- Do not expand it into general observability or Aspire guidance; those belong in the other skills.

## Skill Structure

Each new skill should contain:

```text
.agents/skills/codebrewrouter-architecture-routing/
  SKILL.md
  agents/openai.yaml
```

Use the same structure for each new skill, replacing the folder name with the skill name.

References should be links or file paths to existing repo docs, not copied documents. Add a `references/` directory only if implementation discovers a genuinely reusable summary that does not already exist elsewhere.

The `SKILL.md` files should stay short:

- Frontmatter with only `name` and `description`.
- A short "Start Here" section listing source files to read.
- A compact checklist for decisions and verification.
- No broad README, changelog, or installation document inside each skill folder.

## Discovery And Triggering

The descriptions should include concrete trigger phrases and repository-specific nouns so Codex can select the right skill:

- architecture, routing, MEAI, provider DI, streaming, fallback
- map this repo, onboard, where is, explain CodebrewRouter
- MCP security, provider secrets, cloud egress, tool exposure
- Aspire, AppHost, local model, Foundry Local, Open WebUI
- router logs, `[ROUTER-*]`, `[AGENT-*]`, logging contract

The skills should overlap only where necessary. If a task touches both routing and logging, both `codebrewrouter-architecture-routing` and `codebrewrouter-logging-contract` may apply.

## Data Flow

1. User asks Codex for a CodebrewRouter task.
2. Codex skill metadata triggers one or more project-local skills.
3. The skill instructs Codex which repo docs and files to read first.
4. Codex performs the requested review, plan, or implementation with project-specific constraints loaded.
5. Verification follows the skill-specific checklist and the general repo build/test conventions.

## Error Handling

- If a source-of-truth file referenced by a skill is missing, Codex should search nearby docs before proceeding and report the drift.
- If skill instructions conflict with AGENTS.md or direct user instructions, AGENTS.md and the user request take precedence.
- If a workflow spans multiple skills, Codex should load the smallest relevant set rather than treating the pack as one large skill.
- If an Awesome Copilot skill appears useful later, adapt the idea manually into project-local Codex form instead of installing it globally.

## Testing And Validation

Implementation should validate each new or changed skill with the system skill validator:

```powershell
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-architecture-routing
```

Repeat the validator for each new or changed skill folder.

Validation should also include:

- Confirm every skill folder name matches its frontmatter `name`.
- Confirm every skill has project-local scope language.
- Confirm no new files are created outside `.agents/skills/` except the approved design/plan docs.
- Confirm existing unrelated workspace changes remain untouched.
- Optionally run a lightweight grep check for stale global install language:

```powershell
rg "\$CODEX_HOME|~/.codex|global install|Copilot|OpenCode" E:\src\CodebrewRouter\.agents\skills\codebrewrouter-*
```

The grep may return intentional mentions in "do not install globally" language; review matches rather than treating any result as a failure.

## Implementation Order

1. Create `codebrewrouter-architecture-routing`.
2. Create `codebrewrouter-codebase-onboarding`.
3. Create `codebrewrouter-mcp-provider-security`.
4. Create `codebrewrouter-aspire-local-dev`.
5. Review and lightly refine `codebrewrouter-logging-contract` only if needed.
6. Validate all skill folders.
7. Report the final skill list and validation commands.

## Open Questions

None. The user approved a Codex-only, project-local pack that covers all five workflow areas.
