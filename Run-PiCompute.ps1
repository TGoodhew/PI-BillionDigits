#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean → dotnet build → launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

    The build output directory is auto-detected by globbing for
    PI-BillionDigits.exe under bin\Release after the build — no hardcoded
    TFM folder name.  The output directory defaults to .\PiOutput next to
    the script, and can be overridden with -OutputDir.

    Output files (written to OutputDir):
        pi_digits.txt      — computed Pi digits
        pi_phase_log.txt   — phase timings + suppressed dialog text

.PARAMETER OutputDir
    Directory for pi_digits.txt and pi_phase_log.txt.
    Defaults to "PiOutput" next to the script.
    Created automatically if it does not exist.

.PARAMETER Digits
    Number of Pi digits to compute.  Defaults to 1,000,000,000.

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
    .\Run-PiCompute.ps1 -OutputDir "D:\PiResults"
    .\Run-PiCompute.ps1 -Digits 100000000
    .\Run-PiCompute.ps1 -Trace
    .\Run-PiCompute.ps1 -ReportOnly ".\pi_trace_20260331_121017.nettrace"
#>
param(
    [string]$OutputDir  = 'C:\PiOutput',
    [long]  $Digits     = 1000000000,
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
    dotnet-trace report $ReportOnly topN -n 50 --inclusive | Tee-Object -FilePath $reportFile
    Write-Host ""
    Write-Host "Report written: $reportFile" -ForegroundColor Green
    Write-Host "Paste the contents of that file into Claude for analysis."
    exit 0
}

$projectDir  = $PSScriptRoot
$projectFile = Join-Path $projectDir 'PI-BillionDigits.vbproj'
$digitsFile  = Join-Path $OutputDir  'pi_digits.txt'
$logFile     = Join-Path $OutputDir  'pi_phase_log.txt'

Write-Host "=== PI-BillionDigits headless run ===" -ForegroundColor Cyan
Write-Host "Project : $projectFile"
Write-Host "Output  : $OutputDir"
Write-Host "Digits  : $Digits"
if ($Trace) { Write-Host "Mode    : CPU trace enabled" -ForegroundColor Magenta }
Write-Host ""

# ── Ensure output directory exists ───────────────────────────────────────────
if (-not (Test-Path $OutputDir)) {
    Write-Host "Creating output directory: $OutputDir"
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
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

# ── Auto-detect exe path ─────────────────────────────────────────────────────
$exeCandidates = @(Get-ChildItem -Path (Join-Path $projectDir 'bin\Release') `
                               -Filter 'PI-BillionDigits.exe' `
                               -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending)
if ($exeCandidates.Count -eq 0) {
    throw "PI-BillionDigits.exe not found under bin\Release after build."
}
$exePath = $exeCandidates[0].FullName
Write-Host "Exe     : $exePath"

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

    dotnet-trace collect `
        --output $traceFile `
        --providers "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5,Microsoft-DotNETRuntime:0x1F000080018:5" `
        -- $exePath --digits $Digits --autostart --autoverify

    if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet trace exited with code $LASTEXITCODE" }

    # ── 4. Generate plain-text report ────────────────────────────────────────
    if (Test-Path $traceFile) {
        Write-Host ""
        Write-Host "--- dotnet trace report (topN) ---" -ForegroundColor Yellow
        dotnet-trace report $traceFile topN -n 50 --inclusive | Tee-Object -FilePath $reportFile
        Write-Host ""
        Write-Host "Report written: $reportFile" -ForegroundColor Green
        Write-Host "Paste the contents of that file into Claude for analysis."
    } else {
        Write-Warning "Trace file not found — report skipped."
    }
} else {
    Write-Host "--- Launching ($Digits digits, autostart, autoverify) ---" -ForegroundColor Yellow
    & $exePath --digits $Digits --autostart --autoverify
}

Write-Host ""
Write-Host "=== Run complete ===" -ForegroundColor Cyan
Write-Host "Digits : $digitsFile"
Write-Host "Log    : $logFile"
