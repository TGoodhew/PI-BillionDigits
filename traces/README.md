# Trace bundle index (issue #50)

One entry per trace run.  Open the linked `summary.txt` for the report; the raw
trace artifacts (`.nettrace` / `.etl` / `.etl.zip` / VTune dirs) live next to
each `summary.txt` and are gitignored.

## How to add a new trace

```powershell
.\Run-PiCompute.ps1 -TraceMode <mode> -Digits 1000000000
```

The dispatcher writes `traces/<timestamp>_<mode>_<digits>d/` containing the
raw trace plus `summary.txt`, and appends a line to this file's run-log
section below.

## Modes

| Mode | Tool | Output | Overhead | Recommended duration | Need install? |
|---|---|---|---|---|---|
| `cpu` | dotnet-trace cpu-sampling | `.nettrace` | ~5% | 5-30 min slice | dotnet-trace |
| `gc` | dotnet-trace gc-verbose | `.nettrace` | ~10% | 5-15 min slice | dotnet-trace |
| `alloc` | dotnet-trace gc-collect | `.nettrace` | ~1% | full run | dotnet-trace |
| `counters` | dotnet-counters monitor | `.csv` | ~1% | full run | dotnet-counters |
| `perfview-cpu` | PerfView CPU + .NET providers | `.etl.zip` | ~3-5% | 5-30 min slice | PerfView |
| `perfview-block` | PerfView ThreadTime + lock contention | `.etl.zip` | ~10-15% | 5-15 min slice | PerfView |
| `wpr` | WPR CPU + Disk + FileIO | `.etl` | ~3% | 5 min slice | Windows built-in |
| `vtune-hotspots` | VTune `-collect hotspots` | VTune result dir | ~10% | 5-15 min slice | VTune |
| `vtune-uarch` | VTune `-collect uarch-exploration` | VTune result dir | ~20-30% | 2-5 min slice | VTune |
| `uprof` | AMD uProf time-based | `.caperf` | ~5-10% | 5-15 min slice | uProf (AMD only) |

## Tool inventory on this box

CPU: Intel i9-12900K (16 cores / 24 threads, 8 P-cores + 8 E-cores — **hybrid**).
This contradicts the #72 Phase 5 deferral note ("your current Intel i9 is all
P-cores") — issue #41 (P/E core detection + affinity) is in fact relevant for
this box.

- ✅ `dotnet-trace` 9.0 (installed via `dotnet tool install -g dotnet-trace`)
- ✅ `dotnet-counters` 9.0 (installed via `dotnet tool install -g dotnet-counters`)
- ✅ `PerfView` 3.1.18 at `C:\Tools\PerfView\PerfView.exe` (~23 MB single-exe download)
- ✅ `wpr` (Windows 11 built-in at `C:\Windows\System32\wpr.exe`)
- ⏸ `VTune` — not installed (deferred; needs interactive ~3 GB Intel oneAPI download)
- ⏸ `AMD uProf` — not installed (irrelevant on Intel hardware)
- ⏸ `Concurrency Visualizer` — Visual Studio extension, not CLI-driven; install via VS Marketplace if needed

## Run-log

<!-- Entries below appended automatically by Invoke-TraceRun in Run-PiCompute.ps1 -->
- [1,000,000,000 digits @ 20260519_180901](20260519_180901_cpu_1000000000d/summary.txt) — cpu @ 2026-05-19 20:43
- [100,000,000 digits @ 20260519_204422](20260519_204422_cpu_100000000d/summary.txt) — cpu @ 2026-05-19 20:45
- [100,000,000 digits @ 20260519_204829](20260519_204829_gc_100000000d/summary.txt) — gc @ 2026-05-19 20:50
- [100,000,000 digits @ 20260519_205028](20260519_205028_perfview-cpu_100000000d/summary.txt) — perfview-cpu @ 2026-05-19 20:50
- [100,000,000 digits @ 20260519_205312](20260519_205312_counters_100000000d/summary.txt) — counters @ 2026-05-19 20:54
