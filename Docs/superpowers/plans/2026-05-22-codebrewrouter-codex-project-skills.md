# CodebrewRouter Codex Project Skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the approved CodebrewRouter Codex-only, project-local skill pack and update project documentation to reference the actual skills.

**Architecture:** Each workflow gets its own `.agents/skills/<skill-name>/SKILL.md` and `agents/openai.yaml`. The skill bodies stay short and point to existing repo source-of-truth docs instead of duplicating long guidance.

**Tech Stack:** Codex skills, Markdown, YAML, PowerShell, system skill validator.

---

### Task 1: Initialize Skill Folders

**Files:**
- Create: `.agents/skills/codebrewrouter-architecture-routing/SKILL.md`
- Create: `.agents/skills/codebrewrouter-architecture-routing/agents/openai.yaml`
- Create: `.agents/skills/codebrewrouter-codebase-onboarding/SKILL.md`
- Create: `.agents/skills/codebrewrouter-codebase-onboarding/agents/openai.yaml`
- Create: `.agents/skills/codebrewrouter-mcp-provider-security/SKILL.md`
- Create: `.agents/skills/codebrewrouter-mcp-provider-security/agents/openai.yaml`
- Create: `.agents/skills/codebrewrouter-aspire-local-dev/SKILL.md`
- Create: `.agents/skills/codebrewrouter-aspire-local-dev/agents/openai.yaml`

- [x] **Step 1: Run the skill initializer for each new skill**

Run:

```powershell
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\init_skill.py codebrewrouter-architecture-routing --path E:\src\CodebrewRouter\.agents\skills --interface display_name='CodebrewRouter Architecture Routing' --interface short_description='Route and MEAI pipeline guidance' --interface default_prompt='Use $codebrewrouter-architecture-routing to review a CodebrewRouter routing or MEAI pipeline change.'
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\init_skill.py codebrewrouter-codebase-onboarding --path E:\src\CodebrewRouter\.agents\skills --interface display_name='CodebrewRouter Codebase Onboarding' --interface short_description='Map and explain this repository' --interface default_prompt='Use $codebrewrouter-codebase-onboarding to map the CodebrewRouter repository for a new task.'
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\init_skill.py codebrewrouter-mcp-provider-security --path E:\src\CodebrewRouter\.agents\skills --interface display_name='CodebrewRouter MCP Provider Security' --interface short_description='Review MCP and provider safety' --interface default_prompt='Use $codebrewrouter-mcp-provider-security to review MCP, provider secrets, or cloud egress changes.'
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\init_skill.py codebrewrouter-aspire-local-dev --path E:\src\CodebrewRouter\.agents\skills --interface display_name='CodebrewRouter Aspire Local Dev' --interface short_description='Guide Aspire and local runtime work' --interface default_prompt='Use $codebrewrouter-aspire-local-dev to troubleshoot AppHost, local inference, or local dev setup.'
```

Expected: each command creates one skill folder with `SKILL.md` and `agents/openai.yaml`.

### Task 2: Write Skill Bodies

**Files:**
- Modify: `.agents/skills/codebrewrouter-architecture-routing/SKILL.md`
- Modify: `.agents/skills/codebrewrouter-codebase-onboarding/SKILL.md`
- Modify: `.agents/skills/codebrewrouter-mcp-provider-security/SKILL.md`
- Modify: `.agents/skills/codebrewrouter-aspire-local-dev/SKILL.md`

- [x] **Step 1: Replace initializer placeholders with final instructions**

Use frontmatter with only `name` and `description`. Include "Start Here", "Rules", and "Verification" sections. Keep each file concise and project-local.

### Task 3: Refresh Documentation

**Files:**
- Modify: `AGENTS.md`
- Modify: `README.md`
- Modify: `Docs/INDEX.md`
- Modify: `SKILL.md`

- [x] **Step 1: Change planned-language to existing-skill language**

Update docs so they say the project skill pack exists under `.agents/skills/`, still preserving the local-only rule.

### Task 4: Validate And Commit

**Files:**
- Validate: `.agents/skills/codebrewrouter-architecture-routing`
- Validate: `.agents/skills/codebrewrouter-codebase-onboarding`
- Validate: `.agents/skills/codebrewrouter-mcp-provider-security`
- Validate: `.agents/skills/codebrewrouter-aspire-local-dev`
- Validate: `.agents/skills/codebrewrouter-logging-contract`

- [x] **Step 1: Run skill validation**

Run:

```powershell
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-architecture-routing
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-codebase-onboarding
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-mcp-provider-security
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-aspire-local-dev
python C:\Users\peterab\.codex\skills\.system\skill-creator\scripts\quick_validate.py E:\src\CodebrewRouter\.agents\skills\codebrewrouter-logging-contract
```

Expected: all validators pass.

- [x] **Step 2: Review created files**

Run:

```powershell
git status --short
git diff -- .agents/skills AGENTS.md README.md Docs/INDEX.md SKILL.md
```

Expected: only the new skill pack and docs are changed, aside from pre-existing unrelated workspace changes.

- [x] **Step 3: Commit**

Run:

```powershell
git add .agents/skills/codebrewrouter-architecture-routing .agents/skills/codebrewrouter-codebase-onboarding .agents/skills/codebrewrouter-mcp-provider-security .agents/skills/codebrewrouter-aspire-local-dev AGENTS.md README.md Docs/INDEX.md SKILL.md Docs/superpowers/plans/2026-05-22-codebrewrouter-codex-project-skills.md
git commit -m "feat: add codex project skills"
```
