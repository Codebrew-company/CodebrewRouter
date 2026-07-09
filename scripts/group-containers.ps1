#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Labels all CodebrewRouter Docker containers so Docker Desktop groups
    them under the "codebrewRouter" project in the Containers view.
.DESCRIPTION
    Aspire/DCP creates containers without the compose project label that
    Docker Desktop uses for grouping. This script finds every running
    container whose image or name matches "codebrewrouter" and adds
    the label `com.docker.compose.project=codebrewRouter`, making them
    appear as a single group in Docker Desktop.

    Run AFTER `dotnet run --project Blaze.LlmGateway.AppHost` starts.
    Idempotent — safe to run multiple times.
#>

param(
    [string]$ProjectName = "codebrewRouter"
)

$ErrorActionPreference = "Stop"

Write-Host "🔍 Finding CodebrewRouter containers..." -ForegroundColor Cyan

$containers = docker ps --filter "name=codebrewrouter" --format "{{.ID}}" 2>$null
if (-not $containers) {
    $containers = docker ps --filter "name=gateway" --filter "name=openwebui" --filter "name=agent-devui" --format "{{.ID}}" 2>$null
}
if (-not $containers) {
    $containers = docker ps --filter "ancestor=ghcr.io/open-webui/open-webui" --format "{{.ID}}" 2>$null
}

if (-not $containers) {
    Write-Host "❌ No running CodebrewRouter containers found. Start Aspire first:" -ForegroundColor Red
    Write-Host "   dotnet run --project Blaze.LlmGateway.AppHost" -ForegroundColor Yellow
    exit 1
}

$count = 0
foreach ($id in $containers -split "`n" | Where-Object { $_ }) {
    $name = docker inspect --format "{{.Name}}" $id 2>$null | ForEach-Object { $_ -replace '^/', '' }
    Write-Host "  🏷️  $name → com.docker.compose.project=$ProjectName" -ForegroundColor Green
    docker container update --label "com.docker.compose.project=$ProjectName" $id 2>$null | Out-Null
    $count++
}

Write-Host "✅ Labeled $count container(s) under project '$ProjectName'." -ForegroundColor Cyan
Write-Host "   Open Docker Desktop → Containers → grouped under 'codebrewRouter'." -ForegroundColor Gray
