#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean → dotnet build → launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

.PARAMETER Trace
    Wrap the run in dotnet-trace to collect CPU sampling + runtime events.
    Produces a .nettrace file and a plain-text _report.txt suitable for
    pasting into Claude for analysis.
    Requires: dotnet tool install --global dotnet-trace

.EXAMPLE
    .\Run-PiCompute.ps1
    .\Run-PiCompute.ps1 --trace
#>
param(
    [switch]$Trace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir  = $PSScriptRoot
$projectFile = Join-Path $projectDir 'PI-BillionDigits.vbproj'
$outputDir   = Join-Path $projectDir 'bin\Release\net10.0-windows'
$exePath     = Join-Path $outputDir  'PI-BillionDigits.exe'

Write-Host "=== PI-BillionDigits headless run ===" -ForegroundColor Cyan
Write-Host "Project : $projectFile"
Write-Host "Output  : $exePath"
if ($Trace) { Write-Host "Mode    : CPU trace enabled" -ForegroundColor Magenta }
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

if (-not (Test-Path $exePath)) { throw "Exe not found after build: $exePath" }

# ── 3. Run (with or without trace) ───────────────────────────────────────────
Write-Host ""
Write-Host "Suppressed dialogs will appear in c:\PiOutput\pi_phase_log.txt with [DIALOG] prefix."
Write-Host ""

if ($Trace) {
    $timestamp  = Get-Date -Format 'yyyyMMdd_HHmmss'
    $traceFile  = Join-Path $projectDir "pi_trace_$timestamp.nettrace"
    $reportFile = Join-Path $projectDir "pi_trace_$timestamp`_report.txt"

    Write-Host "--- dotnet trace collect ---" -ForegroundColor Yellow
    Write-Host "Trace  : $traceFile"
    Write-Host "Report : $reportFile"
    Write-Host ""

    dotnet trace collect `
        --output $traceFile `
        --profile cpu-sampling `
        --providers "Microsoft-DotNETRuntime:0x1F000080018:5" `
        -- $exePath --digits 1000000000 --autostart --autoverify

    if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet trace exited with code $LASTEXITCODE" }

    # ── 4. Generate plain-text report ────────────────────────────────────────
    if (Test-Path $traceFile) {
        Write-Host ""
        Write-Host "--- dotnet trace report (topN) ---" -ForegroundColor Yellow
        dotnet trace report $traceFile --report topN | Tee-Object -FilePath $reportFile
        Write-Host ""
        Write-Host "Report written: $reportFile" -ForegroundColor Green
        Write-Host "Paste the contents of that file into Claude for analysis."
    } else {
        Write-Warning "Trace file not found — report skipped."
    }
} else {
    Write-Host "--- Launching (1,000,000,000 digits, autostart, autoverify) ---" -ForegroundColor Yellow
    & $exePath --digits 1000000000 --autostart --autoverify
}

Write-Host ""
Write-Host "=== Run complete ===" -ForegroundColor Cyan
