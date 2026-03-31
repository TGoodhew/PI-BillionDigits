#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean → dotnet build → launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

    Output files (always written to c:\PiOutput):
        pi_digits.txt      — computed Pi digits
        pi_phase_log.txt   — phase timings + suppressed dialog text

.PARAMETER Trace
    Wrap the run in dotnet-trace to collect CPU sampling + runtime events.
    Produces a .nettrace file and a plain-text _report.txt in the project
    directory, suitable for pasting into Claude for analysis.
    Requires: dotnet tool install --global dotnet-trace

.PARAMETER ReportOnly
    Path to an existing .nettrace file.  Skips clean/build/run and goes
    straight to generating the topN report.  Use this to process a trace
    file from a previous run.

.EXAMPLE
    .\Run-PiCompute.ps1
    .\Run-PiCompute.ps1 -Trace
    .\Run-PiCompute.ps1 -ReportOnly "C:\...\pi_trace_20260331_121017.nettrace"
#>
param(
    [switch]$Trace,
    [string]$ReportOnly = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── ReportOnly: skip everything except report generation ─────────────────────
if ($ReportOnly -ne "") {
    if (-not (Test-Path $ReportOnly)) { throw "Trace file not found: $ReportOnly" }
    $reportFile = [System.IO.Path]::ChangeExtension($ReportOnly, $null).TrimEnd('.') + "_report.txt"
    Write-Host "--- dotnet trace report (topN) ---" -ForegroundColor Yellow
    Write-Host "Trace  : $ReportOnly"
    Write-Host "Report : $reportFile"
    Write-Host ""
    dotnet trace report $ReportOnly topN -n 50 --inclusive | Tee-Object -FilePath $reportFile
    Write-Host ""
    Write-Host "Report written: $reportFile" -ForegroundColor Green
    Write-Host "Paste the contents of that file into Claude for analysis."
    exit 0
}

$projectDir  = $PSScriptRoot
$projectFile = Join-Path $projectDir 'PI-BillionDigits.vbproj'
$buildDir    = Join-Path $projectDir 'bin\Release\net10.0-windows10.0.26100.0'
$exePath     = Join-Path $buildDir   'PI-BillionDigits.exe'
$piOutputDir = 'c:\PiOutput'
$digitsFile  = Join-Path $piOutputDir 'pi_digits.txt'
$logFile     = Join-Path $piOutputDir 'pi_phase_log.txt'

Write-Host "=== PI-BillionDigits headless run ===" -ForegroundColor Cyan
Write-Host "Project : $projectFile"
Write-Host "Digits  : $digitsFile"
Write-Host "Log     : $logFile"
if ($Trace) { Write-Host "Mode    : CPU trace enabled" -ForegroundColor Magenta }
Write-Host ""

# ── Ensure output directory exists ───────────────────────────────────────────
if (-not (Test-Path $piOutputDir)) {
    Write-Host "Creating output directory: $piOutputDir"
    New-Item -ItemType Directory -Path $piOutputDir | Out-Null
}

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
Write-Host "Suppressed dialogs will appear in $logFile with [DIALOG] prefix."
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
        --providers "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:2,Microsoft-DotNETRuntime:0x1F000080018:5" `
        -- $exePath --digits 1000000000 --autostart --autoverify

    if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet trace exited with code $LASTEXITCODE" }

    # ── 4. Generate plain-text report ────────────────────────────────────────
    if (Test-Path $traceFile) {
        Write-Host ""
        Write-Host "--- dotnet trace report (topN) ---" -ForegroundColor Yellow
        dotnet trace report $traceFile topN -n 50 --inclusive | Tee-Object -FilePath $reportFile
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
Write-Host "Digits : $digitsFile"
Write-Host "Log    : $logFile"
