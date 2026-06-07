#Requires -Version 5.1
<#
.SYNOPSIS
    One-command "baseline" launcher for cross-machine comparison.  Runs the
    standard PI-BillionDigits baseline computation and bundles the diagnostics
    so the result can be compared between machines.

.DESCRIPTION
    Thin wrapper over Run-PiCompute.ps1 that fixes the agreed baseline parameters
    so every machine runs an identical, comparable computation:

        -LogLevel 2   -AutoCheckpoint   -Threshold 200000   (in-memory)

    It auto-names a fresh, unique output directory per machine and run:

        <OutputRoot>\PiOutput_baseline_<digits>_<COMPUTERNAME>_<yyyyMMdd_HHmmss>

    The timestamp makes every launch a clean from-scratch run (no stale-checkpoint
    resume) and guarantees two machines never collide.  All console output is
    tee'd to console.txt, and when the run finishes the phase log + console +
    run_history.json are zipped to a same-named bundle on the Desktop, ready to
    send back for comparison.

    Locates Run-PiCompute.ps1 next to itself, so you can run it from any
    PowerShell session without cd-ing into the repo first.

.PARAMETER Digits
    Number of Pi digits.  Default 5,000,000,000 (the 5B baseline).
    Use -Digits 1000000000 for the 1B baseline.

.PARAMETER OutputRoot
    Drive/folder under which the auto-named output directory is created.
    Default C:\.  Pick a drive with enough free space (the 5B run needs roughly
    30-50 GB for the digits file + checkpoints).

.EXAMPLE
    .\Run-Baseline.ps1
        Runs the 5B baseline; bundles to
        Desktop\PiOutput_baseline_5000000000_<PC>_<stamp>.zip

.EXAMPLE
    .\Run-Baseline.ps1 -Digits 1000000000
        Runs the 1B baseline.

.EXAMPLE
    .\Run-Baseline.ps1 -OutputRoot 'D:\'
        Puts the output directory on D: instead of C:.

.NOTES
    The 5B baseline is an IN-MEMORY run: use a 64 GB-class, otherwise-idle
    machine and close other large memory consumers (browsers, games, VMs) first.
    Under physical-RAM pressure the MemoryBudget governor serializes the hot path
    (DOP -> 1) and the run slows dramatically.  The 1B baseline peaks ~5 GB and is
    fine on smaller boxes.
#>
[CmdletBinding()]
param(
    [long]  $Digits     = 5000000000,
    [string]$OutputRoot = 'C:\'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Locate the standard runner next to this script.
$runScript = Join-Path $PSScriptRoot 'Run-PiCompute.ps1'
if (-not (Test-Path $runScript)) {
    throw "Run-PiCompute.ps1 not found next to this script ($PSScriptRoot). Run Run-Baseline.ps1 from the repository directory."
}

# Auto-named, unique, from-scratch output directory.
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$leaf  = "PiOutput_baseline_${Digits}_$($env:COMPUTERNAME)_$stamp"
$out   = Join-Path $OutputRoot $leaf
New-Item -ItemType Directory -Force -Path $out | Out-Null

$console = Join-Path $out 'console.txt'
$bundle  = Join-Path "$HOME\Desktop" "$leaf.zip"

Write-Host "=== PI-BillionDigits baseline ===" -ForegroundColor Cyan
Write-Host "Digits : $Digits"
Write-Host "Params : -LogLevel 2 -AutoCheckpoint -Threshold 200000 (in-memory)"
Write-Host "Output : $out"
Write-Host "Bundle : $bundle"
Write-Host ""

# Run the standard computation with the fixed baseline parameters; capture all
# streams (including the suppressed-dialog text) to console.txt as well as the host.
& $runScript -Digits $Digits -LogLevel 2 -AutoCheckpoint -Threshold 200000 -OutputDir $out *>&1 |
    Tee-Object -FilePath $console

# Bundle the diagnostics for comparison: phase log + console + run history.
$runHistory  = Join-Path $env:APPDATA 'PI-BillionDigits\run_history.json'
$bundleItems = @(
    (Join-Path $out 'pi_phase_log.txt'),
    $console
)
if (Test-Path $runHistory) {
    $bundleItems += $runHistory
} else {
    Write-Host "WARNING: run_history.json not found ($runHistory) - bundle will omit it." -ForegroundColor Yellow
}

Compress-Archive -Path $bundleItems -DestinationPath $bundle -Force

Write-Host ""
Write-Host "DONE: $bundle" -ForegroundColor Green
Write-Host "Send that .zip back for comparison."
