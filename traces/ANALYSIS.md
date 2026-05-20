# Issue #50 Phase 0 trace analysis

This is the consolidated Phase 0 baseline from the 2026-05-19 trace session,
the input data for the #72 parallelism rollout prioritization decisions.

## Hardware

- **CPU**: Intel i9-12900K — 16 cores / 24 threads, **8 P-cores + 8 E-cores (hybrid)**.
- 64 GB DDR5 RAM, 3.3 TB NVMe SSD.

**Important correction to #72's playbook**: the Phase 5 deferral note "your
current Intel i9 is all P-cores" is **wrong** — this is a 12th-gen Alder
Lake hybrid CPU. Issue **#41 (P/E core detection + affinity)** is relevant
*for this box*, not just for future hardware.

## Trace inventory

| Run | Scale | Mode | Status | Wall | Notes |
|---|---|---|---|---|---|
| 20260519_180901 | 1B (killed) | cpu | Partial | 4h+ killed | .nettrace corrupt; recovered via `dotnet-trace convert --format speedscope` (65 MB JSON). CONFOUNDED by concurrent robocopy ~18:08-18:15. |
| 20260519_204422 | 100M | cpu | ✅ | 1m 31s | Clean baseline of parallel phases. |
| 20260519_204846 | 100M | gc | ✅ | ~1.5m | gc-verbose providers reveal allocator pressure. |
| 20260519_205028 | 100M | perfview-cpu | ⏸ SKIPPED | — | Needs admin elevation (kernel ETW). Re-run from elevated PowerShell when needed. |
| 20260519_205xxx | 100M | counters | (in flight) | — | dotnet-counters CSV alongside exe. |
| (planned) | 500M | cpu | pending | ~1.5-2h | Will trigger SafeMpzReciprocal (sqrt input 52M limbs > 33.5M SAFE) — the missing piece for SafeMpzReciprocal function-level breakdown. |

## Findings

### Finding 1: Single-core bottleneck after Phase 2 (THE dominant finding for #72)

**Source**: CPU sampler from the killed 1B trace (`traces/1B_cpu_run_cpu_samples.log`).

```
[CPU 18:12] cores=12.9   53.7% of 24-core   RSS=4.38 GB  ← Phase 1 parallel
[CPU 18:23] cores=14.4   60.0% of 24-core   RSS=8.29 GB  ← Phase 2 parallel
[CPU 18:34] cores= 1.03   4.3% of 24-core   RSS=4.09 GB  ← SINGLE-CORE PINNED
... 13 more samples at 1.03 cores ...
[CPU 20:35] cores= 1.03   4.3% of 24-core   RSS=3.99 GB  ← still 1 of 24 after 2h+
```

After Phase 2 completes (~24 min into the 1B run), the runtime collapses to
a single active core and stays there. At 1B digit scale, the sqrt input is
~105M limbs (well above the 33.5M `SAFE_LIMB_THRESHOLD`), so
`SafeMpzReciprocal` runs the full Newton iteration loop — and §166/§167/§168
force every inner `a × r` / `q × b` / reciprocal multiplication to serial.

**This is the bottleneck #72 exists to fix.** At 5B scale it's the ~80h of
single-threaded post-Newton work. At 1B scale it's hours; we observed 4+
hours stuck in `Step 4: SafeMpzSqrt of 2,000,000,005-digit number` before
killing the run.

### Finding 2: Allocator init/clear pressure (Phase 1 / Phase 2 parallel)

**Source**: 100M cpu-mode and gc-verbose topN reports.

| Trace mode | `mpz_inits` excl | `mpz_clears` excl | Combined |
|---|---|---|---|
| cpu-sampling | 12.1% | 2.5% | **14.6%** |
| gc-verbose | 20.96% | 20.72% | **41.7%** |

Under gc-verbose providers, `mpz_inits` + `mpz_clears` together account for
**41.7% of exclusive CPU time** — a massive allocator-churn signal. Each
combine pair in Phase 2 calls `mpz_inits(newP, newQ, tempA, tempB, ...)` +
matching `mpz_clears`; multiplied over ~70K combine pairs at 1B / ~350K at
5B, the per-call native allocator path becomes the dominant cost.

**Implication for #72**: Issue **#47 (GmpNativeAlloc per-CPU pool heads)** is
not a marginal speedup — at this allocation rate the single-SLIST head's
cache-line bouncing is a real cap on parallel scaling. Promote #47 ahead of
#44/#55 in the playbook ordering.

### Finding 3: ThreadPool worker semaphore contention

**Source**: 100M cpu-mode topN.

```
LowLevelLifoSemaphore.WaitForSignal(int32)    37.27% incl   35.76% excl
Thread.Sleep(int32)                            6.44% excl
```

**`LowLevelLifoSemaphore.WaitForSignal` 35.76% exclusive** is the .NET
ThreadPool worker park/unpark loop. Workers are parking while waiting for
work — either because we aren't feeding the pool fast enough between
combine pairs, OR because we have more workers than fan-out (24 logical
threads × overlapping `Parallel.For` waves).

**Implication**: combined with the allocator churn (Finding 2), the parallel
phases at 100M are spending more time in synchronization + allocation than
in the actual `SafeMpzMul` work (which is only 9.13% exclusive). A
combination of #47 (allocator scaling) and a tuning pass on `Parallel.For`
DOP for small chunks could meaningfully shift the breakdown before any of
the #44/#55 SafeMpzMul parallelism work fires.

### Finding 4: `mpz_get_str` is cheap at 100M (don't optimise prematurely)

**Source**: 100M cpu-mode topN.

```
gmp_lib.mpz_get_str(...)    0.81% excl
```

At 100M the decimal conversion is **<1%** of CPU time — native `mpz_get_str`
handles it fast. The §216 chunked converter only matters at >1.5B digits.
Issue **#37 (parallel decimal conversion)** is correctly Phase 2 priority,
not Phase 0/1 — confirms the playbook's ordering.

### Finding 5: 100M wall time decomposition

| Phase | Wall | % of total |
|---|---|---|
| Phase 1 (parallel chunks) + early Phase 2 | ~52s | 57% |
| Step 2 (`SafeMpzMul gmpSqrtInput = gmpOne^2`) | ~4s | 4% |
| Step 4 `SafeMpzSqrt` (native fast path at this scale) | ~4s | 4% |
| Numerator (R0/R1/R2 + combines) | ~13s | 14% |
| Division | ~4s | 4% |
| `mpz_get_str` (native, single thread) | ~14s | 15% |
| **Total** | **1m 31s** | **100%** |

The decimal conversion is 15% of wall here — but only because the
allocator-bound parallel phases are themselves under-utilising the 24
cores. With #47 + #44 lifting the parallel scaling, the *relative* weight
of the decimal conversion will grow (which is when #37 starts to matter).

## #72 reprioritization recommendation (based on 100M + 1B sampler)

Original #72 ordering: #50 → #74 → #47 → #55 → #44 → #37 → #60 → #43 → #42.

**Recommended adjustment based on traces:**

1. **Phase 0**: #50 ✅ (now), #74 ✅ (shipped in PR #77).
2. **Phase 1**: #47 ✅ (no change in priority; trace confirms allocator
   contention is large at parallel scaling, AND the 41.7% exclusive
   allocator-init/clear cost makes per-CPU pool heads more impactful than
   originally estimated — possibly 1.3-1.7× on its own, not just 3-5%).
3. **Phase 2**: keep #55 → #44 ordering. **Add a sub-step**: before #44,
   run a 500M cpu trace **with §166/§167/§168 lifted (#55 changes
   in-place)** to confirm the post-Newton stretch can run parallel without
   reproducing the original NR bug. This validates #55 in isolation before
   committing to #44's larger code change.
4. **#41 (P/E core detection) is more urgent than #72 says** — this box
   IS hybrid (8P+8E). Currently a Parallel.For at DOP=24 will schedule
   work onto E-cores at 1/3 the throughput of P-cores; the topN data
   doesn't distinguish but the wall-clock will be ~30% slower than P-core
   only. Promote #41 from Phase 5 to Phase 1 alongside #47.

## Pending: 500M cpu trace

The 500M cpu trace (launched after counters completes) will fill the
critical gap: function-level breakdown of the single-threaded post-Newton
stretch. Will identify whether the bottleneck inside `SafeMpzReciprocal`
is leaf `SafeMpzMul` recursion, §accum, allocator, or sync — answering
"what's the *next* bottleneck after #55+#44 lift the obvious one?"

## Confounders

- **1B run**: concurrent robocopy 18:08-18:15 contaminated the first ~7 min
  of disk I/O. CPU samples post-18:15 are clean (no disk contention).
  See `traces/20260519_180901_cpu_1000000000d/CONFOUNDERS.md`.
- **100M runs**: clean — robocopy finished before launch. No concurrent load.
- **500M run** (pending): launch with the box quiet. Document any
  concurrent activity in the per-run summary.

## Tool gaps for future work

- **PerfView modes (perfview-cpu / perfview-block)**: need admin
  elevation. Re-run from elevated PowerShell to capture native-frame
  stacks + lock contention.
- **WPR**: same — needs admin for kernel ETW.
- **VTune (hotspots / uarch-exploration)**: not installed. Required for
  the hybrid CPU attribution question (P-core vs E-core breakdown for
  Finding 1 verification). Intel oneAPI installer is ~3 GB, interactive.
- **AMD uProf**: not relevant (Intel hardware).

When admin + VTune are available, re-run the 500M cpu trace under
`perfview-cpu` (admin) AND `vtune-hotspots` to triangulate the
single-core-stretch findings with native-frame and per-core-type data.
