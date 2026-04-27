#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Materializes prompts/jarvis/ to .github/plugins/jarvis/ (Copilot CLI) and .claude/agents/ (Claude Code).

.DESCRIPTION
    prompts/jarvis/ is the single source of truth for the JARVIS personal-developer-agent fleet.
    This script parses role prompt frontmatter, translates tool-name vocabularies between
    Claude Code and Copilot CLI, and emits per-target variants. It also copies command
    shims to both targets.

    Idempotent. Runs on demand. Not CI-enforced — drift caught at PR review.

    Naming map:
        Source                       Claude target                       Copilot target
        conductor.prompt.md       -> jarvis-conductor.md              -> jarvis.conductor.agent.md
        gateway-bugfix.prompt.md  -> gateway-bugfix.md                -> jarvis.gateway-bugfix.agent.md
        memory-architect.prompt.md-> jarvis-memory-architect.md       -> jarvis.memory-architect.agent.md
        tools-architect.prompt.md -> jarvis-tools-architect.md        -> jarvis.tools-architect.agent.md
        agent-architect.prompt.md -> jarvis-agent-architect.md        -> jarvis.agent-architect.agent.md
        vision-architect.prompt.md-> jarvis-vision-architect.md       -> jarvis.vision-architect.agent.md

.EXAMPLE
    pwsh ./scripts/sync-jarvis.ps1
#>

[CmdletBinding()]
param(
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot '..')
$srcRoot     = Join-Path $repoRoot 'prompts/jarvis'
$copilotRoot = Join-Path $repoRoot '.github/plugins/jarvis'
$claudeRoot  = Join-Path $repoRoot '.claude'

if (-not (Test-Path $srcRoot)) {
    throw "Source jarvis directory not found: $srcRoot"
}

# Source name -> Claude/Copilot file basename mapping.
# gateway-bugfix is intentionally un-prefixed in .claude/agents/ to match analysis.md references.
$roles = @(
    @{ Source = 'conductor';        Claude = 'jarvis-conductor';        Copilot = 'jarvis.conductor' },
    @{ Source = 'gateway-bugfix';   Claude = 'gateway-bugfix';          Copilot = 'jarvis.gateway-bugfix' },
    @{ Source = 'memory-architect'; Claude = 'jarvis-memory-architect'; Copilot = 'jarvis.memory-architect' },
    @{ Source = 'tools-architect';  Claude = 'jarvis-tools-architect';  Copilot = 'jarvis.tools-architect' },
    @{ Source = 'agent-architect';  Claude = 'jarvis-agent-architect';  Copilot = 'jarvis.agent-architect' },
    @{ Source = 'vision-architect'; Claude = 'jarvis-vision-architect'; Copilot = 'jarvis.vision-architect' }
)

# Claude Code -> Copilot CLI tool-name mapping (matches sync-squad.ps1).
$toolMapCopilot = @{
    'Read'     = 'read'
    'Edit'     = 'edit'
    'Grep'     = 'search'
    'Glob'     = 'search'
    'Bash'     = 'shell'
    'WebFetch' = 'web'
    'Agent'    = 'agent'
    'Write'    = 'edit'
}

function Ensure-Dir {
    param([string]$Path)
    if ($WhatIf) { Write-Host "  (what-if) mkdir $Path" -ForegroundColor Yellow; return }
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Write-File {
    param([string]$Path, [string]$Content)
    if ($WhatIf) { Write-Host "  (what-if) write $Path ($($Content.Length) chars)" -ForegroundColor Yellow; return }
    Ensure-Dir -Path (Split-Path -Parent $Path)
    # Normalize line endings to LF.
    $normalized = $Content -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($Path, $normalized, [System.Text.UTF8Encoding]::new($false))
}

function Parse-Frontmatter {
    param([string]$Content)
    $match = [regex]::Match($Content, '^---\r?\n(?<fm>.*?)\r?\n---\r?\n(?<body>.*)$', 'Singleline')
    if (-not $match.Success) {
        throw "No YAML frontmatter found in prompt file."
    }
    return @{ Frontmatter = $match.Groups['fm'].Value; Body = $match.Groups['body'].Value }
}

function Extract-FrontmatterValue {
    param([string]$Frontmatter, [string]$Key)
    $line = $Frontmatter -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($Key))\s*:\s*" } | Select-Object -First 1
    if (-not $line) { return $null }
    return ($line -replace "^\s*$([regex]::Escape($Key))\s*:\s*", '').Trim()
}

function Translate-Tools {
    param([string]$ToolsLine, [hashtable]$Map)
    if (-not $ToolsLine) { return "[]" }
    $inner = $ToolsLine.Trim('[', ']', ' ')
    if (-not $inner) { return "[]" }
    $translated = $inner.Split(',') `
        | ForEach-Object { $_.Trim() } `
        | ForEach-Object { if ($Map.ContainsKey($_)) { $Map[$_] } else { $_.ToLowerInvariant() } } `
        | Select-Object -Unique
    return "[" + ($translated -join ", ") + "]"
}

function Emit-ClaudeAgent {
    param([hashtable]$Role, [hashtable]$Parsed)
    $target = Join-Path $claudeRoot "agents/$($Role.Claude).md"
    $frontmatter = $Parsed.Frontmatter.TrimEnd()
    $content = "---`n$frontmatter`n---`n$($Parsed.Body)"
    Write-File -Path $target -Content $content
    Write-Host "  [claude]  agents/$($Role.Claude).md" -ForegroundColor Cyan
}

function Emit-CopilotAgent {
    param([hashtable]$Role, [hashtable]$Parsed)
    $target = Join-Path $copilotRoot "agents/$($Role.Copilot).agent.md"
    $toolsSrc = Extract-FrontmatterValue -Frontmatter $Parsed.Frontmatter -Key 'tools'
    $toolsTranslated = Translate-Tools -ToolsLine $toolsSrc -Map $toolMapCopilot

    # Rewrite only the tools: line; leave everything else identical.
    $newFrontmatter = ($Parsed.Frontmatter -split "`n") | ForEach-Object {
        if ($_ -match '^\s*tools\s*:') { "tools: $toolsTranslated" } else { $_ }
    }
    $newFrontmatter = ($newFrontmatter -join "`n").TrimEnd()
    $content = "---`n$newFrontmatter`n---`n$($Parsed.Body)"
    Write-File -Path $target -Content $content
    Write-Host "  [copilot] agents/$($Role.Copilot).agent.md" -ForegroundColor Magenta
}

function Copy-Verbatim {
    param([string]$SourceDir, [string]$DestDir, [string]$Label)
    if (-not (Test-Path $SourceDir)) { return }
    Ensure-Dir -Path $DestDir
    Get-ChildItem -Path $SourceDir -File | ForEach-Object {
        $dest = Join-Path $DestDir $_.Name
        $content = Get-Content -Raw -Path $_.FullName
        Write-File -Path $dest -Content $content
        Write-Host "  [$Label] $($_.Name)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "JARVIS sync: $srcRoot" -ForegroundColor Green
Write-Host "  -> $copilotRoot" -ForegroundColor Green
Write-Host "  -> $claudeRoot" -ForegroundColor Green
Write-Host ""

# 1. Agents — per-target variants with translated tool names.
Ensure-Dir -Path (Join-Path $copilotRoot 'agents')
Ensure-Dir -Path (Join-Path $claudeRoot 'agents')

foreach ($role in $roles) {
    $src = Join-Path $srcRoot "$($role.Source).prompt.md"
    if (-not (Test-Path $src)) {
        Write-Warning "Missing source prompt: $src — skipped."
        continue
    }
    $raw    = Get-Content -Raw -Path $src
    $parsed = Parse-Frontmatter -Content $raw
    Emit-ClaudeAgent  -Role $role -Parsed $parsed
    Emit-CopilotAgent -Role $role -Parsed $parsed
}

# 2. Commands — Copilot CLI exposes the JARVIS plugin as /agent jarvis; commands are informational.
Copy-Verbatim `
    -SourceDir (Join-Path $srcRoot 'commands') `
    -DestDir   (Join-Path $copilotRoot 'commands') `
    -Label     'copilot'

# Claude Code slash commands live under .claude/commands/.
Copy-Verbatim `
    -SourceDir (Join-Path $srcRoot 'commands') `
    -DestDir   (Join-Path $claudeRoot 'commands') `
    -Label     'claude'

Write-Host ""
Write-Host "JARVIS sync complete." -ForegroundColor Green
Write-Host "Review the diff before committing: git diff .github/plugins/jarvis .claude"
Write-Host ""
