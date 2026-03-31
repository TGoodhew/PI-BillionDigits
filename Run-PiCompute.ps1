#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean → dotnet build → launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

.NOTES
    Run from the project directory:
        cd C:\Users\Tony\source\PI-BillionDigits\PI-BillionDigits
        .\Run-PiCompute.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir  = $PSScriptRoot
$projectFile = Join-Path $projectDir 'PI-BillionDigits.vbproj'
$outputDir   = Join-Path $projectDir 'bin\Release\net10.0-windows'
$exePath     = Join-Path $outputDir  'PI-BillionDigits.exe'

Write-Host "=== PI-BillionDigits headless run ===" -ForegroundColor Cyan
Write-Host "Project : $projectFile"
Write-Host "Output  : $exePath"
Write-Host ""

# ── 1. Clean ─────────────────────────────────────────────────────────────────
Write-Host "--- dotnet clean ---" -ForegroundColor Yellow
dotnet clean $projectFile --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed (exit $LASTEXITCODE)" }

# ── 2. Build ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "--- dotnet build ---" -ForegroundColor Yellow
dotnet build $projectFile --configuration Release --no-incremental
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# ── 3. Run ───────────────────────────────────────────────────────────────────
if (-not (Test-Path $exePath)) {
    throw "Exe not found after build: $exePath"
}

Write-Host ""
Write-Host "--- Launching (1,000,000,000 digits, autostart, autoverify) ---" -ForegroundColor Yellow
Write-Host "Suppressed dialogs will appear in c:\PiOutput\pi_phase_log.txt with [DIALOG] prefix."
Write-Host ""

& $exePath --digits 1000000000 --autostart --autoverify

Write-Host ""
Write-Host "=== Run complete ===" -ForegroundColor Cyan
