---
name: codebrewrouter-codebase-onboarding
description: Project-local CodebrewRouter repository onboarding and codebase mapping guidance. Use when asked to map this repo, onboard a contributor, explain architecture, summarize docs or ADRs, identify likely files for a task, inspect current status, or create concise CodebrewRouter codebase documentation.
---

# CodebrewRouter Codebase Onboarding

## Start Here

Read in this order unless the user gives a narrower target:

- `AGENTS.md`
- `README.md`
- `Docs/INDEX.md`
- `Docs/design/adr/`
- `Docs/superpowers/specs/`
- `Docs/superpowers/plans/`
- `Docs/engineering/logging-contract.md`
- `Blaze.LlmGateway.Tests/TEST_COVERAGE_MATRIX.md`
- Recent git history with `git log --oneline -8`
- Current workspace status with `git status --short`

## Ignore By Default

Do not inventory these unless directly relevant:

- `bin/`
- `obj/`
- `node_modules/`
- `.worktrees/`
- `.claude/worktrees/`
- `Docs/research/sources/`
- generated plugin copies when source prompts are available

## Output Style

- Prefer a compact map of projects, docs, and likely files over a broad file dump.
- Call out known gaps only when they matter to the user's request.
- Link to exact repo files when explaining where behavior lives.
- Separate "source of truth" docs from generated or mirrored copies.
- If asked for implementation guidance, hand off to the relevant project skill after mapping the area.

## Common Handoffs

- Use `codebrewrouter-architecture-routing` for routing, provider, API, or model catalog changes.
- Use `codebrewrouter-mcp-provider-security` for MCP, provider secrets, cloud egress, or tool exposure.
- Use `codebrewrouter-aspire-local-dev` for AppHost, ServiceDefaults, local inference, and local troubleshooting.
- Use `codebrewrouter-logging-contract` for router or agent telemetry changes.

## Verification

For onboarding-only answers, no build is required. For doc changes, review the rendered Markdown mentally and run:

```powershell
git diff --check
```
