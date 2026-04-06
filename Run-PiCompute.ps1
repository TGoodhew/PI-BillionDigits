#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean → dotnet build → launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

    Builds in Debug by default.  Pass -UseRelease to build and run the
    Release configuration instead.

    The build output directory is auto-detected by globbing for
    PI-BillionDigits.exe under bin\Debug (or bin\Release with -UseRelease)
    after the build — no hardcoded
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

.PARAMETER Test
    Run the app at every power of 10 from 10 up to -Digits (default 1,000,000,000).
    Each run is isolated in its own subdirectory under OutputDir (test_10, test_100, …).
    After all runs a combined pass/fail and timing table is printed and saved to
    OutputDir\test_suite_report.txt.

    Verification pass/fail rules (known digit positions in Pi):
      999999        expected at position 762  — checked when digits >= 768
      777777777     expected at position 24,658,601 — checked when digits >= 24,658,610
      e-digits      27182818284 — position unknown; reported as Found/Not found (informational)

    Combine with -Trace to run dotnet-trace on every power-of-10 run; per-run
    trace reports are appended to the combined report.

.PARAMETER CheckpointFromLevel
    Serialize combine nodes at this level and above to disk regardless of -Threshold.
    Use this on a run that might crash so checkpoint files are available for -ResumeFromLevel.
    Example: -CheckpointFromLevel 15 writes nodes for levels 15, 16, 17, 18, 19 to disk.
    Files are written as L{level-1}_N{index}.bin in the NodeCache directory.

.PARAMETER ResumeFromLevel
    Skip Phase 1 and levels 1..N-1.  Load the L{N-1}_N*.bin checkpoint files written
    by a previous run with -CheckpointFromLevel N and continue Phase 2 from level N.
    The -Digits value must match the original run so numChunks is computed correctly.
    Example: -ResumeFromLevel 15 reads L14_N*.bin files and resumes Phase 2 at level 15.

.PARAMETER AutoCheckpoint
    Write a RAM snapshot to NodeCache\snap_L{N}\ at the end of each Phase 2 level.
    All combine work still runs in RAM; the snapshot is written as a batch after each
    level completes.  On the next run with -AutoCheckpoint the highest valid snapshot
    is detected automatically and the run resumes from that level — no -ResumeFromLevel
    needed.  Only the most recent level's snapshot is kept (previous level deleted after
    next level confirms).
    Example: -AutoCheckpoint  (use on every run; interrupted runs resume automatically)

.PARAMETER BackupCheckpoint
    After the run completes or crashes, copy all snap_L* snapshot directories from
    NodeCache into OutputDir\SnapshotStore\.  The store directory is never touched by
    the app, so backups there survive the next run's cache clear.
    Existing store entries with the same name are overwritten.
    Combine with -AutoCheckpoint to both save checkpoints during the run and back them
    up afterwards.
    Example: -AutoCheckpoint -BackupCheckpoint

.PARAMETER LogLevel
    Logging detail level passed to the exe as --log-level N.  Defaults to 1.
      0  None        Errors and crashes only. Silent on success.
      1  Performance [PHASE] markers with wall-clock timing (default).
      2  Stages      Per-phase step detail: file I/O, initial calc steps, node sizes.
      3  Last stage  Full per-operation trace for the final combine and ComputePiGMP.
      4  Full trace  Everything in 3, plus SafeMpzMul diagnostics and BinarySplitChunk.
      5  Allocator   Everything in 4, plus pool/affinity diagnostics.

.PARAMETER UseRelease
    Build and run the Release configuration instead of Debug.
    Default is Debug.

.PARAMETER ReportOnly
    Path to an existing .nettrace file.  Skips clean/build/run and goes
    straight to generating the topN report.  Use this to process a trace
    file from a previous run.

.EXAMPLE
    .\Run-PiCompute.ps1
    .\Run-PiCompute.ps1 -OutputDir "D:\PiResults"
    .\Run-PiCompute.ps1 -Digits 100000000
    .\Run-PiCompute.ps1 -LogLevel 0
    .\Run-PiCompute.ps1 -LogLevel 3
    .\Run-PiCompute.ps1 -Trace -LogLevel 2
    .\Run-PiCompute.ps1 -Test
    .\Run-PiCompute.ps1 -Test -Digits 1000000
    .\Run-PiCompute.ps1 -Test -Trace
    .\Run-PiCompute.ps1 -ReportOnly ".\pi_trace_20260331_121017.nettrace"
    .\Run-PiCompute.ps1 -Digits 5000000000 -Threshold 1000000 -CheckpointFromLevel 15 -LogLevel 2
    .\Run-PiCompute.ps1 -Digits 5000000000 -ResumeFromLevel 15 -LogLevel 2
    .\Run-PiCompute.ps1 -Digits 5000000000 -AutoCheckpoint -LogLevel 2
    .\Run-PiCompute.ps1 -Digits 5000000000 -AutoCheckpoint -BackupCheckpoint -LogLevel 2
#>
param(
    [string]$OutputDir           = 'C:\PiOutput',
    [long]  $Digits              = 1000000000,
    [int]   $LogLevel            = 1,
    [long]  $Threshold           = 0,
    [int]   $CheckpointFromLevel = 0,
    [int]   $ResumeFromLevel     = 0,
    [switch]$AutoCheckpoint,
    [switch]$BackupCheckpoint,
    [switch]$UseRelease,
    [switch]$Trace,
    [switch]$Test,
    [string]$ReportOnly          = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$config = if ($UseRelease) { 'Release' } else { 'Debug' }

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
Write-Host "Project  : $projectFile"
Write-Host "Config   : $config"
Write-Host "Output   : $OutputDir"
Write-Host "Digits   : $Digits"
Write-Host "LogLevel : $LogLevel"
if ($Threshold           -gt 0) { Write-Host "Threshold: $($Threshold.ToString('N0')) nodes (RAM only)" -ForegroundColor Yellow }
if ($CheckpointFromLevel -gt 0) { Write-Host "Checkpoint: from level $CheckpointFromLevel" -ForegroundColor Yellow }
if ($ResumeFromLevel     -gt 0) { Write-Host "Resume   : from level $ResumeFromLevel" -ForegroundColor Cyan }
if ($AutoCheckpoint)            { Write-Host "Mode     : Auto-checkpoint enabled" -ForegroundColor Green }
if ($BackupCheckpoint)          { Write-Host "Backup   : Checkpoints backed up to SnapshotStore after run" -ForegroundColor Green }
if ($Trace)                     { Write-Host "Mode     : CPU trace enabled" -ForegroundColor Magenta }
Write-Host ""

# ── Ensure output directory exists ───────────────────────────────────────────
if (-not (Test-Path $OutputDir)) {
    Write-Host "Creating output directory: $OutputDir"
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# ── 1. Clean ─────────────────────────────────────────────────────────────────
Write-Host "--- dotnet clean ---" -ForegroundColor Yellow
dotnet clean $projectFile --configuration $config
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed (exit $LASTEXITCODE)" }

# ── 2. Build ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "--- dotnet build ---" -ForegroundColor Yellow
dotnet build $projectFile --configuration $config --no-incremental
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# ── Auto-detect exe path ─────────────────────────────────────────────────────
$exeCandidates = @(Get-ChildItem -Path (Join-Path $projectDir "bin\$config") `
                               -Filter 'PI-BillionDigits.exe' `
                               -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending)
if ($exeCandidates.Count -eq 0) {
    throw "PI-BillionDigits.exe not found under bin\$config after build."
}
$exePath = $exeCandidates[0].FullName
Write-Host "Exe     : $exePath"

# ── 3a. Test suite (powers of 10) ────────────────────────────────────────────
if ($Test) {
    # Build the power-of-10 sequence up to $Digits
    $testRuns = [System.Collections.Generic.List[long]]::new()
    $n = 10L
    while ($n -le $Digits) { $testRuns.Add($n); $n *= 10L }

    $reportFile  = Join-Path $OutputDir "test_suite_report_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
    $reportLines = [System.Collections.Generic.List[string]]::new()

    $header = "{0,-18} {1,10}  {2,-14} {3,-22} {4,-16} {5}" -f `
              "Digits","Time(s)","999999@762","777777777@24658601","e-digits","Result"
    $separator = "-" * 90
    Write-Host ""
    Write-Host "=== Test Suite ($($testRuns.Count) runs, up to $($Digits.ToString('N0')) digits) ===" -ForegroundColor Cyan
    Write-Host $separator
    Write-Host $header
    Write-Host $separator
    $reportLines.Add("=== PI-BillionDigits Test Suite ===")
    $reportLines.Add("Started : $(Get-Date)")
    $reportLines.Add("Exe     : $exePath")
    $reportLines.Add("")
    $reportLines.Add($separator)
    $reportLines.Add($header)
    $reportLines.Add($separator)

    $allPassed = $true

    foreach ($d in $testRuns) {
        $runDir = Join-Path $OutputDir ("test_" + $d.ToString())
        if (-not (Test-Path $runDir)) { New-Item -ItemType Directory -Path $runDir | Out-Null }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if ($Trace) {
            $runTraceFile  = Join-Path $runDir "trace.nettrace"
            $runReportFile = Join-Path $runDir "trace_report.txt"
            Write-Host "  [trace] $runTraceFile" -ForegroundColor DarkMagenta
            $traceArgs = @("--digits", $d, "--autostart", "--autoverify", "--log-level", $LogLevel, "--output-dir", $runDir)
            if ($Threshold           -gt 0) { $traceArgs += @("--threshold",             $Threshold) }
            if ($CheckpointFromLevel -gt 0) { $traceArgs += @("--checkpoint-from-level", $CheckpointFromLevel) }
            if ($ResumeFromLevel     -gt 0) { $traceArgs += @("--resume-from-level",     $ResumeFromLevel) }
            dotnet-trace collect `
                --output $runTraceFile `
                --providers "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5,Microsoft-DotNETRuntime:0x1F000080018:5" `
                -- $exePath @traceArgs
            if (Test-Path $runTraceFile) {
                dotnet-trace report $runTraceFile topN -n 20 --inclusive |
                    Out-File -FilePath $runReportFile -Encoding utf8
            }
        } else {
            # Use Start-Process -Wait: the exe is a WinForms GUI app and & returns immediately.
            $runArgs = @("--digits", $d, "--autostart", "--autoverify", "--log-level", $LogLevel, "--output-dir", $runDir)
            if ($Threshold           -gt 0) { $runArgs += @("--threshold",             $Threshold) }
            if ($CheckpointFromLevel -gt 0) { $runArgs += @("--checkpoint-from-level", $CheckpointFromLevel) }
            if ($ResumeFromLevel     -gt 0) { $runArgs += @("--resume-from-level",     $ResumeFromLevel) }
            Start-Process -FilePath $exePath -ArgumentList $runArgs -NoNewWindow -Wait
        }
        $sw.Stop()
        $elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)

        # ── Parse verify result from the run's phase log ──────────────────
        $runLog = Join-Path $runDir "pi_phase_log.txt"
        $verifyLine = ""
        if (Test-Path $runLog) {
            $match = Select-String -Path $runLog -Pattern '\[Verify\]' | Select-Object -Last 1
            if ($match) { $verifyLine = $match.Line }
        }

        # ── Determine pass/fail for each known sequence ───────────────────
        # 999999 expected at position 762 (checkable when digits >= 768)
        if ($d -lt 768) {
            $check1 = "N/A"
        } elseif ($verifyLine -match '999999@762 OK') {
            $check1 = "PASS"
        } elseif ($verifyLine -match '999999') {
            $check1 = "FAIL"
            $allPassed = $false
        } else {
            $check1 = "FAIL(no log)"
            $allPassed = $false
        }

        # 777777777 expected at position 24,658,601 (checkable when digits >= 24,658,610)
        if ($d -lt 24658610) {
            $check2 = "N/A"
        } elseif ($verifyLine -match '777777777@24,658,601 OK') {
            $check2 = "PASS"
        } elseif ($verifyLine -match '777777777') {
            $check2 = "FAIL"
            $allPassed = $false
        } else {
            $check2 = "FAIL(no log)"
            $allPassed = $false
        }

        # e-digits: informational — position not guaranteed within 1B
        if ($verifyLine -match 'e-digits@(\d+) OK') {
            $check3 = "Found@" + $Matches[1]
        } else {
            $check3 = "Not found"
        }

        $overallRun = if ($check1 -eq "FAIL" -or $check1 -eq "FAIL(no log)" -or
                          $check2 -eq "FAIL" -or $check2 -eq "FAIL(no log)") { "FAIL" } else { "PASS" }

        $colour = if ($overallRun -eq "PASS") { "Green" } else { "Red" }
        $row = "{0,-18} {1,10}  {2,-14} {3,-22} {4,-16} {5}" -f `
               $d.ToString('N0'), $elapsed, $check1, $check2, $check3, $overallRun
        Write-Host $row -ForegroundColor $colour
        $reportLines.Add($row)
    }

    Write-Host $separator
    $reportLines.Add($separator)
    $overall = if ($allPassed) { "ALL PASSED" } else { "FAILURES DETECTED" }
    $overallColour = if ($allPassed) { "Green" } else { "Red" }
    $summaryLine = "Overall: $overall   Completed: $(Get-Date)"
    Write-Host $summaryLine -ForegroundColor $overallColour
    $reportLines.Add($summaryLine)

    # ── Append per-run trace reports to combined report ───────────────────────
    if ($Trace) {
        $reportLines.Add("")
        $reportLines.Add("=" * 90)
        $reportLines.Add("PER-RUN TRACE SUMMARIES (dotnet-trace topN, top 20 inclusive)")
        $reportLines.Add("=" * 90)
        foreach ($d in $testRuns) {
            $runReportFile = Join-Path $OutputDir ("test_" + $d.ToString()) "trace_report.txt"
            $reportLines.Add("")
            $reportLines.Add("--- $($d.ToString('N0')) digits ---")
            if (Test-Path $runReportFile) {
                Get-Content $runReportFile | ForEach-Object { $reportLines.Add($_) }
            } else {
                $reportLines.Add("(trace report not found)")
            }
        }
    }

    $reportLines | Out-File -FilePath $reportFile -Encoding utf8
    Write-Host ""
    Write-Host "Report saved: $reportFile" -ForegroundColor Cyan
    exit 0
}

# ── BackupCheckpoint helper ──────────────────────────────────────────────────
function Invoke-CheckpointBackup {
    param([string]$NodeCacheDir, [string]$StoreDir)
    $snaps = @(Get-ChildItem $NodeCacheDir -Directory -Filter 'snap_L*' -ErrorAction SilentlyContinue)
    if ($snaps.Count -eq 0) {
        Write-Host "BackupCheckpoint: no snap_L* directories found in $NodeCacheDir" -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path $StoreDir)) {
        New-Item -ItemType Directory -Path $StoreDir | Out-Null
    }
    foreach ($snap in $snaps) {
        $dest = Join-Path $StoreDir $snap.Name
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Copy-Item -Path $snap.FullName -Destination $dest -Recurse
        $count = @(Get-ChildItem $dest -File).Count
        Write-Host "BackupCheckpoint: $($snap.Name) → $dest ($count files)" -ForegroundColor Green
    }
}

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

    $mainArgs = @("--digits", $Digits, "--autostart", "--autoverify", "--log-level", $LogLevel, "--output-dir", $OutputDir)
    if ($Threshold           -gt 0) { $mainArgs += @("--threshold",             $Threshold) }
    if ($CheckpointFromLevel -gt 0) { $mainArgs += @("--checkpoint-from-level", $CheckpointFromLevel) }
    if ($ResumeFromLevel     -gt 0) { $mainArgs += @("--resume-from-level",     $ResumeFromLevel) }
    if ($AutoCheckpoint)            { $mainArgs += "--auto-checkpoint" }
    dotnet-trace collect `
        --output $traceFile `
        --providers "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5,Microsoft-DotNETRuntime:0x1F000080018:5" `
        -- $exePath @mainArgs

    if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet trace exited with code $LASTEXITCODE" }

    if ($BackupCheckpoint) {
        Invoke-CheckpointBackup -NodeCacheDir (Join-Path $OutputDir 'NodeCache') `
                                -StoreDir     (Join-Path $OutputDir 'SnapshotStore')
    }

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
    Write-Host "--- Launching ($Digits digits, autostart, autoverify, log-level $LogLevel) ---" -ForegroundColor Yellow
    $mainArgs = @("--digits", $Digits, "--autostart", "--autoverify", "--log-level", $LogLevel, "--output-dir", $OutputDir)
    if ($Threshold           -gt 0) { $mainArgs += @("--threshold",             $Threshold) }
    if ($CheckpointFromLevel -gt 0) { $mainArgs += @("--checkpoint-from-level", $CheckpointFromLevel) }
    if ($ResumeFromLevel     -gt 0) { $mainArgs += @("--resume-from-level",     $ResumeFromLevel) }
    if ($AutoCheckpoint)            { $mainArgs += "--auto-checkpoint" }
    Start-Process -FilePath $exePath -ArgumentList $mainArgs -NoNewWindow -Wait

    if ($BackupCheckpoint) {
        Invoke-CheckpointBackup -NodeCacheDir (Join-Path $OutputDir 'NodeCache') `
                                -StoreDir     (Join-Path $OutputDir 'SnapshotStore')
    }
}

Write-Host ""
Write-Host "=== Run complete ===" -ForegroundColor Cyan
Write-Host "Digits : $digitsFile"
Write-Host "Log    : $logFile"
