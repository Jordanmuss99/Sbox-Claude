# sync-addon.ps1 — copy the canonical bridge addon into a live s&box project.
#
# Phase 0.2 deliverable. The canonical source lives at:
#   <repo>/sbox-bridge-addon/Editor/MyEditorMenu.cs
#
# The live copy lives at:
#   <sbox-project>/Libraries/claudebridge/Editor/MyEditorMenu.cs
#
# This script copies canonical -> target, prints SHA256, and refuses to run
# unless an explicit -Target path or $env:SBOX_PROJECT_LIB is set (no guessing
# in-place destructive overwrites).
#
# Usage:
#   pwsh scripts/sync-addon.ps1 -Target "C:\path\to\sbox-project\Libraries\claudebridge"
#   $env:SBOX_PROJECT_LIB = "C:\path\to\...\claudebridge"; pwsh scripts/sync-addon.ps1
#
# Exit codes:
#   0  copy succeeded (or no-op when SHA256 already matches)
#   1  argument missing / target invalid
#   2  canonical file missing
#   3  copy failed (IO error)

[CmdletBinding()]
param(
    [string]$Target = $env:SBOX_PROJECT_LIB,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Resolve repo root from this script's location: <repo>/sbox-mcp-server/scripts/
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Resolve-Path (Join-Path $scriptDir "..\..") | Select-Object -ExpandProperty Path
$canonical = Join-Path $repoRoot "sbox-bridge-addon\Editor\MyEditorMenu.cs"

if ( -not (Test-Path -LiteralPath $canonical) ) {
    Write-Error "Canonical addon source not found: $canonical"
    exit 2
}

if ( [string]::IsNullOrWhiteSpace($Target) ) {
    Write-Host ""
    Write-Host "Usage: sync-addon.ps1 -Target <path-to-claudebridge-library>"
    Write-Host ""
    Write-Host "  Or set `$env:SBOX_PROJECT_LIB to the target directory."
    Write-Host ""
    Write-Host "Example targets:"
    Write-Host "  C:\Users\you\Documents\s&box-projects\mygame\Libraries\claudebridge"
    Write-Host "  D:\sbox-projects\mygame\Libraries\claudebridge"
    Write-Host ""
    exit 1
}

$targetEditor = Join-Path $Target "Editor"
$targetFile   = Join-Path $targetEditor "MyEditorMenu.cs"

if ( -not (Test-Path -LiteralPath $targetEditor) ) {
    if ( -not $Force ) {
        Write-Error "Target Editor/ directory does not exist: $targetEditor. Re-run with -Force to create it (only if you're sure this is the right library)."
        exit 1
    }
    New-Item -ItemType Directory -Path $targetEditor -Force | Out-Null
}

$canonicalHash = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash
$targetHash    = if (Test-Path -LiteralPath $targetFile) {
    (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
} else { "(absent)" }

Write-Host ""
Write-Host "Canonical: $canonical"
Write-Host "  SHA256:  $canonicalHash"
Write-Host "Target:    $targetFile"
Write-Host "  SHA256:  $targetHash"
Write-Host ""

if ( $canonicalHash -eq $targetHash ) {
    Write-Host "Already in sync. No copy needed."
    exit 0
}

try {
    Copy-Item -LiteralPath $canonical -Destination $targetFile -Force
} catch {
    Write-Error "Copy failed: $_"
    exit 3
}

$newHash = (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
if ( $newHash -ne $canonicalHash ) {
    Write-Error "Post-copy hash mismatch (corruption?): expected $canonicalHash got $newHash"
    exit 3
}

Write-Host "Synced. Restart s&box for the addon hotload to pick up changes."
exit 0
