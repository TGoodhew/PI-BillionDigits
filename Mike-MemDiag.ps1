<#
.SYNOPSIS
    During-run memory diagnostic for the PI-BillionDigits computation. Run it WHILE a Pi run is
    in progress to capture whether the machine is memory-starved (the root cause of slow cross-
    machine runs).
.DESCRIPTION
    Captures, in one shot, the exact starvation signature the MemoryBudget governor reacts to:
      * Physical RAM    : total / available  (availPhys in the app log)
      * Commit          : limit / in-use / available  (availCommit in the app log) -- this is
                          RAM + pagefile, and the ceiling that forces DOP 9->1 + pressure trims
      * Pagefile        : configured size(s) + current usage  (raise this if commit-limited)
      * Top consumers   : the processes holding the most COMMIT and the most WORKING SET, so an
                          external ~40GB hog (browser / VM / game / AV reindex) is named outright
      * Pi process      : the PI-BillionDigits worker's own RSS + commit, if running

    Healthy box (idle, enough commit headroom): availCommit stays tens of GB above what the run
    needs, no single external process holds a large slice, and the app log shows "DOP floored
    20->N" rather than "peak ... > budget (RAM cap)" / "pressure trim".

    Starved box: availCommit collapses toward a few GB, some external process dominates commit,
    and the app log fills with RAM-cap routing + pressure trims (hot-path muls forced serial).

    Every sample is echoed to the console AND appended to a log on the Desktop so several samples
    over the course of a run accumulate in one file you can send back.
.PARAMETER Samples
    How many samples to take. Default 1 (one shot). Use e.g. 6 to watch a trend.
.PARAMETER IntervalSec
    Seconds between samples when Samples > 1. Default 600 (10 minutes).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Mike-MemDiag.ps1
        One snapshot now.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Mike-MemDiag.ps1 -Samples 12 -IntervalSec 600
        A snapshot every 10 minutes for 2 hours (good for an overnight watch).
.NOTES
    Read-only. Touches nothing the run depends on. Safe to run alongside a live computation.
#>
[CmdletBinding()]
param(
    [int]$Samples = 1,
    [int]$IntervalSec = 600
)

$ErrorActionPreference = 'SilentlyContinue'
$logPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ("pi_memdiag_{0}.txt" -f $env:COMPUTERNAME)

function Write-Both([string]$line) {
    Write-Output $line
    Add-Content -Path $logPath -Value $line
}

function Take-Sample {
    $now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Both ""
    Write-Both ("================ pi mem-diag  {0}  [{1}] ================" -f $now, $env:COMPUTERNAME)

    # --- Physical RAM + commit (Win32_OperatingSystem reports KB; Virtual* == commit) ---
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os) {
        $physTotGB  = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)   # KB -> GB
        $physFreeGB = [math]::Round($os.FreePhysicalMemory     / 1MB, 1)
        $commLimGB  = [math]::Round($os.TotalVirtualMemorySize / 1MB, 1)   # commit limit (RAM+pagefile)
        $commFreeGB = [math]::Round($os.FreeVirtualMemory      / 1MB, 1)   # available commit
        $commUseGB  = [math]::Round($commLimGB - $commFreeGB, 1)
        Write-Both ("  RAM      : total {0,6} GB   available {1,6} GB" -f $physTotGB, $physFreeGB)
        Write-Both ("  COMMIT   : limit {0,6} GB   in-use   {1,6} GB   available {2,6} GB" -f $commLimGB, $commUseGB, $commFreeGB)
        if ($commFreeGB -lt 12) {
            Write-Both ("  *** WARNING: available commit {0} GB < 12 GB -- the governor will throttle DOP and force serial muls (this IS the slowdown). ***" -f $commFreeGB)
        }
    } else {
        Write-Both "  (could not read Win32_OperatingSystem)"
    }

    # --- Pagefile: configured + current usage ---
    $pfUse = Get-CimInstance Win32_PageFileUsage
    if ($pfUse) {
        foreach ($pf in $pfUse) {
            $allocGB = [math]::Round($pf.AllocatedBaseSize / 1KB, 1)   # MB -> GB
            $curGB   = [math]::Round($pf.CurrentUsage     / 1KB, 1)
            $peakGB  = [math]::Round($pf.PeakUsage        / 1KB, 1)
            Write-Both ("  PAGEFILE : {0}  allocated {1} GB  current {2} GB  peak {3} GB" -f $pf.Name, $allocGB, $curGB, $peakGB)
        }
    } else {
        Write-Both "  PAGEFILE : none reported (system-managed=0 or disabled -> commit limit ~= RAM only; raise it if commit-limited)"
    }
    $pfSet = Get-CimInstance Win32_PageFileSetting
    if ($pfSet) {
        foreach ($s in $pfSet) {
            Write-Both ("  PF-SETTING: {0}  initial {1} MB  max {2} MB" -f $s.Name, $s.InitialSize, $s.MaximumSize)
        }
    }

    # --- Top consumers by COMMIT (PrivateMemorySize64) and by WORKING SET ---
    $procs = Get-Process
    Write-Both "  -- top 10 by COMMIT (private bytes) --"
    $procs | Sort-Object PrivateMemorySize64 -Descending | Select-Object -First 10 | ForEach-Object {
        Write-Both ("     {0,8:N1} GB  commit  | {1,8:N1} GB ws  | {2}" -f ($_.PrivateMemorySize64/1GB), ($_.WorkingSet64/1GB), $_.ProcessName)
    }
    Write-Both "  -- top 10 by WORKING SET --"
    $procs | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 | ForEach-Object {
        Write-Both ("     {0,8:N1} GB  ws      | {1,8:N1} GB commit | {2}" -f ($_.WorkingSet64/1GB), ($_.PrivateMemorySize64/1GB), $_.ProcessName)
    }

    # --- The Pi worker itself ---
    $pi = Get-Process PI-BillionDigits -ErrorAction SilentlyContinue
    if ($pi) {
        Write-Both ("  PI-WORKER: RSS {0:N1} GB   commit {1:N1} GB   (running)" -f ($pi.WorkingSet64/1GB), ($pi.PrivateMemorySize64/1GB))
    } else {
        Write-Both "  PI-WORKER: PI-BillionDigits not currently running"
    }
}

Write-Both ("# pi memory diagnostic -- logging to {0}" -f $logPath)
for ($i = 1; $i -le $Samples; $i++) {
    Take-Sample
    if ($i -lt $Samples) { Start-Sleep -Seconds $IntervalSec }
}
Write-Both ""
Write-Both ("# done ({0} sample(s)). Send {1} back." -f $Samples, $logPath)
