#Requires -Version 5.1
<#
.SYNOPSIS
    Clean, build, and run PI-BillionDigits in headless mode.

.DESCRIPTION
    Runs: dotnet clean -> dotnet build -> launch exe with
    --digits 1000000000 --autostart --autoverify
    No dialogs will appear during the run.  All suppressed dialog text
    is written to the phase log with a [DIALOG] prefix for review.

    Builds in Debug by default.  Pass -UseRelease to build and run the
    Release configuration instead.

    The build output directory is auto-detected by globbing for
    PI-BillionDigits.exe under bin\Debug (or bin\Release with -UseRelease)
    after the build  -  no hardcoded
    TFM folder name.  The output directory defaults to .\PiOutput next to
    the script, and can be overridden with -OutputDir.

    Output files (written to OutputDir):
        pi_digits.txt       -  computed Pi digits
        pi_phase_log.txt    -  phase timings + suppressed dialog text

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

    Equivalent to -TraceMode cpu.  Kept for backwards compatibility.

.PARAMETER TraceMode
    Issue #50 multi-tool trace harness.  Wraps the run in the named profiler
    and writes the raw trace + a summary.txt into a per-run subdir under
    -TraceDir.  Modes:

      none            no tracing (default)
      cpu             dotnet-trace cpu-sampling + topN report
      gc              dotnet-trace gc-verbose (GC events + alloc-tick)
      alloc           dotnet-trace gc-collect (low overhead, full-run safe)
      counters        dotnet-counters CSV (continuous, runs alongside exe)
      perfview-cpu    PerfView CPU + .NET providers (.etl.zip)
      perfview-block  PerfView ThreadTime + lock contention (.etl.zip)
      wpr             Windows Performance Recorder CPU + Disk (.etl)
      vtune-hotspots  Intel VTune hotspots collect (requires VTune install)
      vtune-uarch     Intel VTune microarchitecture exploration (heavy)
      uprof           AMD uProf time-based profiling (AMD CPUs only)

    Tool locations (override via env var):
      PerfView : %PERFVIEW_EXE%  or  C:\Tools\PerfView\PerfView.exe
      VTune    : %VTUNE_EXE%     or  C:\Program Files (x86)\Intel\oneAPI\vtune\latest\bin64\vtune.exe
      uProf    : %UPROF_EXE%     or  C:\Program Files\AMD\AMDuProf\bin\AMDuProfCLI.exe

.PARAMETER TraceDir
    Root directory for #50 trace artifacts.  Each run creates a subdir
    {timestamp}_{mode}_{digits}d/ containing trace.* + summary.txt.
    Defaults to .\traces next to this script.

.PARAMETER Test
    Run the app at every power of 10 from 10 up to -Digits (default 1,000,000,000).
    Each run is isolated in its own subdirectory under OutputDir (test_10, test_100, …).
    After all runs a combined pass/fail and timing table is printed and saved to
    OutputDir\test_suite_report.txt.

    Verification pass/fail rules (known digit positions in Pi):
      999999        expected at position 762   -  checked when digits >= 768
      777777777     expected at position 24,658,601  -  checked when digits >= 24,658,610
      e-digits      27182818284  -  position unknown; reported as Found/Not found (informational)

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
    is detected automatically and the run resumes from that level  -  no -ResumeFromLevel
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
    .\Run-PiCompute.ps1 -TraceMode cpu -Digits 1000000000
    .\Run-PiCompute.ps1 -TraceMode gc -Digits 1000000000
    .\Run-PiCompute.ps1 -TraceMode perfview-cpu -Digits 1000000000
    .\Run-PiCompute.ps1 -TraceMode wpr -Digits 100000000
    .\Run-PiCompute.ps1 -TraceMode vtune-hotspots -Digits 1000000000
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
    [ValidateSet('none','cpu','gc','alloc','counters','perfview-cpu','perfview-block','wpr','vtune-hotspots','vtune-uarch','uprof')]
    [string]$TraceMode           = 'none',
    [string]$TraceDir            = (Join-Path $PSScriptRoot 'traces'),
    [switch]$Test,
    [string]$ReportOnly          = ""
)

# Back-compat: -Trace is an alias for -TraceMode cpu.
if ($Trace -and $TraceMode -eq 'none') { $TraceMode = 'cpu' }

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
if ($TraceMode -ne 'none')      { Write-Host "TraceMode: $TraceMode" -ForegroundColor Magenta }
Write-Host ""

# ── Ensure output directory exists ───────────────────────────────────────────
if (-not (Test-Path $OutputDir)) {
    Write-Host "Creating output directory: $OutputDir"
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# ── 1. Build native C DLL (GmpNativeAlloc) via VS MSBuild ────────────────────
# The dotnet CLI cannot load VC++ targets, so the native project is built
# separately using the full Visual Studio MSBuild before dotnet build runs.
$msbuildCandidates = @(
    'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
)
$msbuildExe = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$nativeVcxproj = Join-Path $projectDir 'GmpNativeAlloc\GmpNativeAlloc.vcxproj'
$slnFile       = Join-Path $projectDir 'PI-BillionDigits.sln'

if ($msbuildExe -and (Test-Path $nativeVcxproj)) {
    Write-Host "--- MSBuild GmpNativeAlloc ($config|x64) ---" -ForegroundColor Yellow
    & $msbuildExe $slnFile /p:Configuration=$config '/p:Platform=Any CPU' /v:minimal /t:GmpNativeAlloc
    if ($LASTEXITCODE -ne 0) { throw "MSBuild GmpNativeAlloc failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "WARNING: VS MSBuild not found or native project missing  -  skipping GmpNativeAlloc build." -ForegroundColor Yellow
    Write-Host "         GmpNativeAlloc.dll must already be present in the output directory." -ForegroundColor Yellow
}

# ── 2. Clean ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "--- dotnet clean ---" -ForegroundColor Yellow
dotnet clean $projectFile --configuration $config
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed (exit $LASTEXITCODE)" }

# ── 3. Build ─────────────────────────────────────────────────────────────────
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

        # e-digits: informational  -  position not guaranteed within 1B
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

# ── CheckpointBackup / Restore helpers ──────────────────────────────────────
# Backup: copy all snap_L* and snap_Phase3 dirs from NodeCache -> SnapshotStore.
# Restore: copy any missing/incomplete snaps from SnapshotStore -> NodeCache.
# Together these ensure the backup always reflects the latest run, and the next
# run always starts with the best available checkpoint  -  even if the app deleted
# NodeCache entries during normal operation.

# ── Issue #50 trace dispatcher ───────────────────────────────────────────────
# Each mode wraps the launch in a different profiler.  The exe is a WinForms
# GUI app that exits cleanly on its own when --autostart --autoverify finish
# (§76 makes sure it exits with code 1 on exception).  For modes that need a
# PID (counters), launch with Start-Process -PassThru and pass the Id.

function Resolve-TraceTool {
    param([string]$EnvVar, [string]$Default)
    $candidate = [System.Environment]::GetEnvironmentVariable($EnvVar)
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $Default }
    if (Test-Path $candidate) { return $candidate }
    return $null
}

function Invoke-TraceRun {
    param(
        [string]$Mode,
        [string]$ExePath,
        [string[]]$ExeArgs,
        [string]$RunDir,
        [string]$Label
    )

    if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }
    $summaryFile = Join-Path $RunDir 'summary.txt'
    "=== Trace run: $Mode @ $(Get-Date) ===" | Out-File -FilePath $summaryFile -Encoding utf8
    "Exe   : $ExePath" | Out-File -FilePath $summaryFile -Append
    "Args  : $($ExeArgs -join ' ')" | Out-File -FilePath $summaryFile -Append
    "" | Out-File -FilePath $summaryFile -Append

    $perfViewExe = Resolve-TraceTool 'PERFVIEW_EXE' 'C:\Tools\PerfView\PerfView.exe'
    $vtuneExe    = Resolve-TraceTool 'VTUNE_EXE'    'C:\Program Files (x86)\Intel\oneAPI\vtune\latest\bin64\vtune.exe'
    $uprofExe    = Resolve-TraceTool 'UPROF_EXE'    'C:\Program Files\AMD\AMDuProf\bin\AMDuProfCLI.exe'

    # PerfView and WPR both require admin elevation for kernel-mode CPU/disk tracing.
    # Detect early and skip cleanly rather than trigger silent self-elevation that
    # spawns an orphaned elevated PerfView and leaves us with no trace.
    $isElevated = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    switch ($Mode) {
        'cpu' {
            $traceFile = Join-Path $RunDir 'trace.nettrace'
            Write-Host "--- dotnet-trace cpu-sampling ---" -ForegroundColor Magenta
            dotnet-trace collect `
                --output $traceFile `
                --providers "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5,Microsoft-DotNETRuntime:0x1F000080018:5" `
                -- $ExePath @ExeArgs
            if (Test-Path $traceFile) {
                "--- dotnet-trace topN (inclusive, top 50) ---" | Out-File -FilePath $summaryFile -Append
                dotnet-trace report $traceFile topN -n 50 --inclusive | Tee-Object -FilePath $summaryFile -Append
            }
        }

        'gc' {
            $traceFile = Join-Path $RunDir 'trace.nettrace'
            Write-Host "--- dotnet-trace gc-verbose ---" -ForegroundColor Magenta
            dotnet-trace collect --output $traceFile --profile gc-verbose -- $ExePath @ExeArgs
            if (Test-Path $traceFile) {
                "Trace: $traceFile" | Out-File -FilePath $summaryFile -Append
                "Open in PerfView (File → Open → trace.nettrace) for GC pause histogram + alloc-by-type." | Out-File -FilePath $summaryFile -Append
                "" | Out-File -FilePath $summaryFile -Append
                "--- dotnet-trace topN (inclusive, top 30) ---" | Out-File -FilePath $summaryFile -Append
                dotnet-trace report $traceFile topN -n 30 --inclusive | Out-File -FilePath $summaryFile -Append
            }
        }

        'alloc' {
            $traceFile = Join-Path $RunDir 'trace.nettrace'
            Write-Host "--- dotnet-trace gc-collect (low overhead) ---" -ForegroundColor Magenta
            dotnet-trace collect --output $traceFile --profile gc-collect -- $ExePath @ExeArgs
            if (Test-Path $traceFile) {
                "Trace: $traceFile" | Out-File -FilePath $summaryFile -Append
                "--- dotnet-trace topN (inclusive, top 30) ---" | Out-File -FilePath $summaryFile -Append
                dotnet-trace report $traceFile topN -n 30 --inclusive | Out-File -FilePath $summaryFile -Append
            }
        }

        'counters' {
            $countersFile = Join-Path $RunDir 'counters.csv'
            Write-Host "--- dotnet-counters collect (continuous CSV alongside exe) ---" -ForegroundColor Magenta
            # Launch exe in background; use PassThru to get the Id.
            $proc = Start-Process -FilePath $ExePath -ArgumentList $ExeArgs -NoNewWindow -PassThru
            Start-Sleep -Seconds 3
            if (-not $proc.HasExited) {
                # dotnet-counters runs in foreground; it exits when the target exits.
                dotnet-counters collect --process-id $proc.Id --output $countersFile --format csv --refresh-interval 5
            }
            if (-not $proc.HasExited) { $proc.WaitForExit() }
            "Counters CSV: $countersFile" | Out-File -FilePath $summaryFile -Append
            "Open in Excel; rows are per-counter, columns are 5-second samples." | Out-File -FilePath $summaryFile -Append
        }

        'perfview-cpu' {
            if (-not $perfViewExe) {
                Write-Warning "PerfView not found. Download single .exe from https://github.com/microsoft/perfview/releases and either put at C:\Tools\PerfView\PerfView.exe or set `$env:PERFVIEW_EXE."
                "SKIPPED: PerfView not installed." | Out-File -FilePath $summaryFile -Append
                return
            }
            if (-not $isElevated) {
                Write-Warning "PerfView CPU stack-sampling requires admin elevation (kernel ETW). Re-run this session 'as Administrator' to capture perfview-cpu/perfview-block/wpr."
                "SKIPPED: perfview-cpu needs an elevated PowerShell session (kernel ETW)." | Out-File -FilePath $summaryFile -Append
                "To fix: Right-click PowerShell -> Run as administrator, then re-run." | Out-File -FilePath $summaryFile -Append
                return
            }
            $traceFile = Join-Path $RunDir 'trace.etl.zip'
            $perfLog   = Join-Path $RunDir 'perfview.log'
            Write-Host "--- PerfView CPU + .NET providers ---" -ForegroundColor Magenta
            # PerfView syntax: Run takes ONE quoted "exe args" string (NOT dotnet-trace's -- convention).
            $argString = ($ExeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
            $cmd = '"' + $ExePath + '" ' + $argString
            & $perfViewExe /AcceptEULA /NoGui /BufferSizeMB=512 /CircularMB=8192 /DataFile=$traceFile /LogFile=$perfLog Run $cmd
            "PerfView trace: $traceFile" | Out-File -FilePath $summaryFile -Append
            "Open in PerfView (CPU Stacks → expand by process / thread). Use GroupPats `[group module entries]` for native frames (libgmp, GmpNativeAlloc)." | Out-File -FilePath $summaryFile -Append
        }

        'perfview-block' {
            if (-not $perfViewExe) {
                Write-Warning "PerfView not found (see perfview-cpu mode for install)."
                "SKIPPED: PerfView not installed." | Out-File -FilePath $summaryFile -Append
                return
            }
            if (-not $isElevated) {
                Write-Warning "PerfView ThreadTime tracing requires admin elevation."
                "SKIPPED: perfview-block needs an elevated PowerShell session (kernel ETW)." | Out-File -FilePath $summaryFile -Append
                return
            }
            $traceFile = Join-Path $RunDir 'trace.etl.zip'
            $perfLog   = Join-Path $RunDir 'perfview.log'
            Write-Host "--- PerfView ThreadTime + lock contention ---" -ForegroundColor Magenta
            $argString = ($ExeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
            $cmd = '"' + $ExePath + '" ' + $argString
            & $perfViewExe /AcceptEULA /NoGui /ThreadTime /BufferSizeMB=512 /CircularMB=8192 /DataFile=$traceFile /LogFile=$perfLog Run $cmd
            "PerfView ThreadTime trace: $traceFile" | Out-File -FilePath $summaryFile -Append
            "Open in PerfView → 'Thread Time Stacks' → 'CPU_TIME' vs 'BLOCKED_TIME'. Lock contention shows in 'BLOCKED_TIME on Lock'." | Out-File -FilePath $summaryFile -Append
        }

        'wpr' {
            if (-not $isElevated) {
                Write-Warning "WPR kernel tracing (-start CPU) requires admin elevation."
                "SKIPPED: wpr needs an elevated PowerShell session." | Out-File -FilePath $summaryFile -Append
                return
            }
            $traceFile = Join-Path $RunDir 'trace.etl'
            Write-Host "--- WPR (Windows Performance Recorder) CPU + Disk + FileIO ---" -ForegroundColor Magenta
            # System-wide recording. -filemode writes incrementally rather than circular buffer.
            $startOut = & wpr -start CPU -start DiskIO -start FileIO -filemode 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "wpr -start failed: $startOut. Try: wpr -cancel; check Admin rights."
                "SKIPPED: wpr -start failed ($LASTEXITCODE). $startOut" | Out-File -FilePath $summaryFile -Append
                return
            }
            try {
                Start-Process -FilePath $ExePath -ArgumentList $ExeArgs -NoNewWindow -Wait
            } finally {
                & wpr -stop $traceFile "PI BillionDigits CPU+Disk (issue #50)" 2>&1 | Out-Null
            }
            "WPR ETL: $traceFile" | Out-File -FilePath $summaryFile -Append
            "Open in WPA (Windows Performance Analyzer, from Windows ADK)." | Out-File -FilePath $summaryFile -Append
        }

        { $_ -in 'vtune-hotspots','vtune-uarch' } {
            if (-not $vtuneExe) {
                Write-Warning "VTune not installed. Get it from https://www.intel.com/content/www/us/en/developer/tools/oneapi/vtune-profiler-download.html (free, ~3 GB)."
                "SKIPPED: VTune not installed." | Out-File -FilePath $summaryFile -Append
                "Install: https://www.intel.com/content/www/us/en/developer/tools/oneapi/vtune-profiler-download.html" | Out-File -FilePath $summaryFile -Append
                return
            }
            $collect = if ($Mode -eq 'vtune-hotspots') { 'hotspots' } else { 'uarch-exploration' }
            $resultDir = Join-Path $RunDir "vtune-$collect"
            Write-Host "--- VTune -collect $collect ---" -ForegroundColor Magenta
            & $vtuneExe -collect $collect -result-dir $resultDir -- $ExePath @ExeArgs
            "VTune result: $resultDir" | Out-File -FilePath $summaryFile -Append
            "" | Out-File -FilePath $summaryFile -Append
            "--- VTune -report summary ---" | Out-File -FilePath $summaryFile -Append
            & $vtuneExe -report summary -result-dir $resultDir 2>&1 | Out-File -FilePath $summaryFile -Append
        }

        'uprof' {
            if (-not $uprofExe) {
                Write-Warning "AMD uProf not installed (and this box has an Intel CPU — use vtune-* modes instead)."
                "SKIPPED: AMD uProf not installed (CPU vendor: $((Get-CimInstance Win32_Processor).Manufacturer))." | Out-File -FilePath $summaryFile -Append
                return
            }
            $traceFile = Join-Path $RunDir 'uprof.caperf'
            Write-Host "--- AMD uProf time-based profiling ---" -ForegroundColor Magenta
            & $uprofExe collect -e tbp -o $traceFile -- $ExePath @ExeArgs
            "uProf trace: $traceFile" | Out-File -FilePath $summaryFile -Append
        }

        default {
            throw "Unknown TraceMode: $Mode"
        }
    }

    # Append to traces/README.md index.
    $indexFile = Join-Path $TraceDir 'README.md'
    $rel = Split-Path $RunDir -Leaf
    Add-Content -Path $indexFile -Value "- [$Label]($rel/summary.txt) — $Mode @ $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

    Write-Host "Trace summary: $summaryFile" -ForegroundColor Green
}

function Invoke-CheckpointBackup {
    param([string]$NodeCacheDir, [string]$StoreDir)
    $snaps = @(Get-ChildItem $NodeCacheDir -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like 'snap_L*' -or $_.Name -eq 'snap_Phase3' })
    if ($snaps.Count -eq 0) {
        Write-Host "BackupCheckpoint: no snap_L* / snap_Phase3 dirs found in $NodeCacheDir" -ForegroundColor Yellow
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
        Write-Host "BackupCheckpoint: $($snap.Name) -> SnapshotStore ($count files)" -ForegroundColor Green
    }
}

function Invoke-CheckpointRestore {
    param([string]$NodeCacheDir, [string]$StoreDir)
    if (-not (Test-Path $StoreDir)) { return }
    $saves = @(Get-ChildItem $StoreDir -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like 'snap_L*' -or $_.Name -eq 'snap_Phase3' })
    if ($saves.Count -eq 0) { return }
    if (-not (Test-Path $NodeCacheDir)) {
        New-Item -ItemType Directory -Path $NodeCacheDir | Out-Null
    }
    foreach ($save in $saves) {
        $dest = Join-Path $NodeCacheDir $save.Name
        $storeCount = @(Get-ChildItem $save.FullName -File).Count
        $cacheCount  = if (Test-Path $dest) { @(Get-ChildItem $dest -File).Count } else { 0 }
        if ($storeCount -gt 0 -and $cacheCount -ge $storeCount) {
            Write-Host "RestoreCheckpoint: $($save.Name)  -  NodeCache current ($cacheCount files), skipping" -ForegroundColor DarkGray
            continue
        }
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Copy-Item -Path $save.FullName -Destination $dest -Recurse
        $restored = @(Get-ChildItem $dest -File).Count
        Write-Host "RestoreCheckpoint: $($save.Name) <- SnapshotStore ($restored files restored)" -ForegroundColor Cyan
    }
}

function Invoke-PhaseLogArchive {
    # Issue #84: the exe opens pi_phase_log.txt in truncate mode on startup, so a
    # crash-resume would otherwise overwrite (and permanently lose) the log of the
    # session that just died.  Before launching, move any existing log into a
    # logs\ subdir, stamped with its OWN last-write time (when that session ended,
    # not now), and keep only the newest $Keep archives.
    param([string]$LogPath, [int]$Keep = 20)
    if (-not (Test-Path $LogPath)) { return }
    $info = Get-Item $LogPath
    if ($info.Length -eq 0) { return }   # nothing worth keeping
    $logsDir = Join-Path (Split-Path $LogPath -Parent) 'logs'
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }
    $stamp = $info.LastWriteTime.ToString('yyyyMMdd_HHmmss')
    $dest  = Join-Path $logsDir "pi_phase_log_$stamp.txt"
    # Guard against two sessions sharing a second-resolution timestamp.
    if (Test-Path $dest) {
        $dest = Join-Path $logsDir "pi_phase_log_${stamp}_$([System.IO.Path]::GetRandomFileName().Substring(0,4)).txt"
    }
    Move-Item -LiteralPath $LogPath -Destination $dest -Force
    $sizeMB = [math]::Round($info.Length / 1MB, 1)
    Write-Host "PhaseLogArchive: preserved prior log ($sizeMB MB) -> $dest" -ForegroundColor Green
    # Retention: keep the newest $Keep archives, delete the rest.
    $old = @(Get-ChildItem $logsDir -Filter 'pi_phase_log_*.txt' -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -Skip $Keep)
    foreach ($f in $old) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
    if ($old.Count -gt 0) {
        Write-Host "PhaseLogArchive: trimmed $($old.Count) old archive(s), keeping newest $Keep" -ForegroundColor DarkGray
    }
}

# ── 3. Restore checkpoints from SnapshotStore before running ─────────────────
# Ensures that snap_Phase3 / snap_L* saved from a previous run (or backed up
# after a crash) are present in NodeCache before the app starts.  Safe to call
# even on a fresh run: it silently no-ops when SnapshotStore is empty.
if ($BackupCheckpoint) {
    Write-Host ""
    Write-Host "--- Restoring checkpoints from SnapshotStore ---" -ForegroundColor Yellow
    Invoke-CheckpointRestore -NodeCacheDir (Join-Path $OutputDir 'NodeCache') `
                             -StoreDir     (Join-Path $OutputDir 'SnapshotStore')
}

# ── 3b. Preserve the prior phase log before the exe truncates it (issue #84) ──
# Runs unconditionally (every launch truncates pi_phase_log.txt, not just backup
# runs).  No-ops on a fresh run where the log does not yet exist.
Invoke-PhaseLogArchive -LogPath $logFile

# ── 4. Run (with or without trace) ───────────────────────────────────────────
Write-Host ""
Write-Host "Suppressed dialogs will appear in $logFile with [DIALOG] prefix."
Write-Host ""

# Common exe argument list (used by every dispatch path below).
$mainArgs = @("--digits", $Digits, "--autostart", "--autoverify", "--log-level", $LogLevel, "--output-dir", $OutputDir)
if ($Threshold           -gt 0) { $mainArgs += @("--threshold",             $Threshold) }
if ($CheckpointFromLevel -gt 0) { $mainArgs += @("--checkpoint-from-level", $CheckpointFromLevel) }
if ($ResumeFromLevel     -gt 0) { $mainArgs += @("--resume-from-level",     $ResumeFromLevel) }
if ($AutoCheckpoint)            { $mainArgs += "--auto-checkpoint" }

if ($TraceMode -ne 'none') {
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $runDir    = Join-Path $TraceDir "${timestamp}_${TraceMode}_${Digits}d"
    if (-not (Test-Path $TraceDir)) { New-Item -ItemType Directory -Path $TraceDir -Force | Out-Null }

    # Seed traces/README.md if it doesn't exist.
    $indexFile = Join-Path $TraceDir 'README.md'
    if (-not (Test-Path $indexFile)) {
        @(
            '# Trace bundle index (issue #50)',
            '',
            'One entry per trace run.  Open the linked summary.txt for the report; the raw',
            'trace artifacts (.nettrace / .etl / vtune dirs) live next to each summary.txt',
            'and are gitignored.',
            ''
        ) | Out-File -FilePath $indexFile -Encoding utf8
    }

    $label = "$($Digits.ToString('N0')) digits @ $timestamp"
    Invoke-TraceRun -Mode $TraceMode -ExePath $exePath -ExeArgs $mainArgs -RunDir $runDir -Label $label

    if ($BackupCheckpoint) {
        Invoke-CheckpointBackup -NodeCacheDir (Join-Path $OutputDir 'NodeCache') `
                                -StoreDir     (Join-Path $OutputDir 'SnapshotStore')
    }
} else {
    Write-Host "--- Launching ($Digits digits, autostart, autoverify, log-level $LogLevel) ---" -ForegroundColor Yellow
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
