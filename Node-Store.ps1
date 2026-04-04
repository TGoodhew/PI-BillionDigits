#Requires -Version 5.1
<#
.SYNOPSIS
    Save and restore PI-BillionDigits node cache snapshots to a protected store.

.DESCRIPTION
    Snapshots live in C:\pioutput\NodeCache\snap_L{N}\ and are managed (and
    sometimes deleted) automatically by the app and Run-PiCompute.ps1.

    This script saves copies to C:\pioutput\SnapshotStore\ — a directory that
    neither the app nor Run-PiCompute.ps1 knows about or touches.  Only you
    can delete files from there.

    Actions:
      Save     Copy a snapshot from NodeCache into SnapshotStore.
               If a save with the same name already exists you will be asked
               to confirm before it is overwritten.
      Restore  Copy a snapshot back from SnapshotStore into NodeCache,
               replacing whatever is there (after confirmation).
      List     Show all saves currently in SnapshotStore.

.PARAMETER Action
    Save | Restore | List

.PARAMETER Level
    The snapshot level number to save or restore (e.g. 19 for snap_L19).
    Not required for List.

.PARAMETER NodeCacheDir
    Path to the NodeCache directory.  Defaults to C:\pioutput\NodeCache.

.PARAMETER StoreDir
    Path to the protected store directory.  Defaults to C:\pioutput\SnapshotStore.
    This directory is never touched by the app or Run-PiCompute.ps1.

.EXAMPLE
    .\Node-Store.ps1 -Action List
    .\Node-Store.ps1 -Action Save    -Level 19
    .\Node-Store.ps1 -Action Restore -Level 19
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('Save','Restore','List')]
    [string]$Action,

    [int]   $Level        = -1,
    [string]$NodeCacheDir = 'C:\pioutput\NodeCache',
    [string]$StoreDir     = 'C:\pioutput\SnapshotStore'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Helpers ───────────────────────────────────────────────────────────────────

function Format-DirSize {
    param([string]$Path)
    $bytes = (Get-ChildItem $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    if     ($bytes -ge 1GB) { "{0:N1} GB" -f ($bytes / 1GB) }
    elseif ($bytes -ge 1MB) { "{0:N1} MB" -f ($bytes / 1MB) }
    else                    { "{0:N0} KB" -f ($bytes / 1KB) }
}

function Get-SnapMeta {
    param([string]$SnapDir)
    $metaFile = Join-Path $SnapDir 'meta.txt'
    if (-not (Test-Path $metaFile)) { return $null }
    $meta = @{}
    Get-Content $metaFile | ForEach-Object {
        if ($_ -match '^(\w+)=(.*)$') { $meta[$Matches[1]] = $Matches[2] }
    }
    return $meta
}

# ── List ──────────────────────────────────────────────────────────────────────

if ($Action -eq 'List') {
    if (-not (Test-Path $StoreDir)) {
        Write-Host "Store is empty (directory does not exist): $StoreDir" -ForegroundColor Yellow
        exit 0
    }
    $saves = @(Get-ChildItem $StoreDir -Directory | Sort-Object Name)
    if ($saves.Count -eq 0) {
        Write-Host "Store is empty: $StoreDir" -ForegroundColor Yellow
        exit 0
    }
    Write-Host "=== SnapshotStore: $StoreDir ===" -ForegroundColor Cyan
    Write-Host ("{0,-20} {1,10}  {2,12}  {3}" -f "Name","Files","Size","Meta")
    Write-Host ("-" * 70)
    foreach ($dir in $saves) {
        $fileCount = (Get-ChildItem $dir.FullName -File).Count
        $size      = Format-DirSize $dir.FullName
        $meta      = Get-SnapMeta $dir.FullName
        $metaStr   = if ($meta) { "digits=$($meta['digits'])  chunks=$($meta['numChunks'])  level=$($meta['level'])" } else { "(no meta)" }
        Write-Host ("{0,-20} {1,10}  {2,12}  {3}" -f $dir.Name, $fileCount, $size, $metaStr)
    }
    exit 0
}

# ── Save / Restore require -Level ─────────────────────────────────────────────

if ($Level -lt 0) {
    Write-Error "-Level is required for -Action $Action"
    exit 1
}

$snapName   = "snap_L$Level"
$sourcePath = Join-Path $NodeCacheDir $snapName   # NodeCache\snap_L19
$destPath   = Join-Path $StoreDir     $snapName   # SnapshotStore\snap_L19

# ── Save ──────────────────────────────────────────────────────────────────────

if ($Action -eq 'Save') {
    if (-not (Test-Path $sourcePath)) {
        Write-Error "Snapshot not found: $sourcePath"
        exit 1
    }

    $fileCount = (Get-ChildItem $sourcePath -File).Count
    $size      = Format-DirSize $sourcePath
    $meta      = Get-SnapMeta $sourcePath
    $metaStr   = if ($meta) { "digits=$($meta['digits'])  chunks=$($meta['numChunks'])" } else { "(no meta)" }

    Write-Host "=== Save: $snapName ===" -ForegroundColor Cyan
    Write-Host "  Source : $sourcePath"
    Write-Host "  Dest   : $destPath"
    Write-Host "  Files  : $fileCount   Size: $size   $metaStr"

    if (Test-Path $destPath) {
        Write-Host ""
        Write-Host "A save already exists at: $destPath" -ForegroundColor Yellow
        $confirm = Read-Host "Overwrite it? [y/N]"
        if ($confirm -notmatch '^[Yy]') {
            Write-Host "Cancelled." -ForegroundColor Red
            exit 0
        }
        Remove-Item $destPath -Recurse -Force
    }

    if (-not (Test-Path $StoreDir)) {
        New-Item -ItemType Directory -Path $StoreDir | Out-Null
    }

    Write-Host ""
    Write-Host "Copying..." -ForegroundColor Yellow
    Copy-Item -Path $sourcePath -Destination $destPath -Recurse
    $savedCount = (Get-ChildItem $destPath -File).Count
    Write-Host "Saved: $savedCount files → $destPath" -ForegroundColor Green
    exit 0
}

# ── Restore ───────────────────────────────────────────────────────────────────

if ($Action -eq 'Restore') {
    if (-not (Test-Path $destPath)) {
        Write-Error "No saved snapshot found: $destPath`nRun with -Action List to see what is stored."
        exit 1
    }

    $fileCount = (Get-ChildItem $destPath -File).Count
    $size      = Format-DirSize $destPath
    $meta      = Get-SnapMeta $destPath
    $metaStr   = if ($meta) { "digits=$($meta['digits'])  chunks=$($meta['numChunks'])" } else { "(no meta)" }

    Write-Host "=== Restore: $snapName ===" -ForegroundColor Cyan
    Write-Host "  Source : $destPath"
    Write-Host "  Dest   : $sourcePath"
    Write-Host "  Files  : $fileCount   Size: $size   $metaStr"

    if (Test-Path $sourcePath) {
        Write-Host ""
        Write-Host "An existing snapshot is at: $sourcePath" -ForegroundColor Yellow
        $confirm = Read-Host "Replace it? [y/N]"
        if ($confirm -notmatch '^[Yy]') {
            Write-Host "Cancelled." -ForegroundColor Red
            exit 0
        }
        Remove-Item $sourcePath -Recurse -Force
    }

    if (-not (Test-Path $NodeCacheDir)) {
        New-Item -ItemType Directory -Path $NodeCacheDir | Out-Null
    }

    Write-Host ""
    Write-Host "Copying..." -ForegroundColor Yellow
    Copy-Item -Path $destPath -Destination $sourcePath -Recurse
    $restoredCount = (Get-ChildItem $sourcePath -File).Count
    Write-Host "Restored: $restoredCount files → $sourcePath" -ForegroundColor Green
    Write-Host ""
    Write-Host "The store copy is untouched at: $destPath" -ForegroundColor DarkGray
    exit 0
}
