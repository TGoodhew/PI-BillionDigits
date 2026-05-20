# Confounders for this trace

**Status: CONTAMINATED — do not use wall-clock breakdown as steady-state baseline.**

## Concurrent activity during the trace window

| Confounder | Window | Effect |
|---|---|---|
| `robocopy C:\PiOutput → C:\PiPreserved_5B_run2_2026-05-19\` (~165 GB mirror) | Started 18:08:xx, expected to finish ~18:14-18:23 (5-15 min sustained NVMe writes) | 100% disk usage for the first ~5-15 min of the trace. Phase 1 of the 1B compute and any concurrent log writes to `C:\PiTraceOutput\` will block on disk. The build phase of `dotnet build` (earlier, ~5s) also competed briefly. |
| Periodic CPU sampler `_cpu_sample_loop.ps1` | Every 10 min while exe alive | Negligible (Get-Process snapshot, ~1ms). |

## What's still valid

- **CPU-sample data** (per-method inclusive/exclusive times): the .NET sampler hooks runtime CPU time and is largely insensitive to disk pressure. Top-N method breakdown can be trusted for relative ordering.
- **Allocation events**: same — runtime-internal, unaffected by disk.

## What's NOT valid

- **Wall-clock phase timings**: any "phase X took Y minutes" measurement during the first 5-15 min of the trace is inflated by disk contention. Anything written to the phase log or trace file got queued behind the robocopy.
- **GC pause durations during the contaminated window**: GC blocks on the trace writer; that writer is on the contended disk.
- **Lock-contention numbers** (if this were a ThreadTime trace, which it isn't — cpu-sampling mode here): would be skewed by stalls on disk I/O appearing as blocked-time on unrelated locks.

## Follow-up

A clean cpu-mode re-run is scheduled for after robocopy completes, with the box quiet. That run will be the actual Phase 0 baseline for #72 reprioritization. This trace is kept as a side-by-side reference for "with disk pressure" effects but should NOT be cited in any prioritization recommendation.
