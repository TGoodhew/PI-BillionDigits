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

## 500M cpu trace findings (the critical data point)

Run: `traces/20260519_205513_cpu_500000000d/`. Wall: 4h 26m (20:55 → 01:21).

**Wall-time breakdown** (from `pi_phase_log.txt`):

| Phase | Wall | % of total | Cores |
|---|---|---|---|
| Phase 1 + Phase 2 + Step 1-3 setup | ~1 min | 0.4% | 15 |
| **SafeMpzSqrt (Newton on 1B-digit input)** | **~3 hours** | **68%** | **1** |
| Q split + numerator R0/R1/R2 combines | ~8 min | 3% | parallel |
| **SafeMpzDiv (numer / T, 1.3B/1B division)** | **~1 hour** | **22.5%** | **1** |
| String conversion (native, sub-§216 threshold) | ~1 min | 0.4% | 1 |
| Misc + verify + exit | ~13 min | 5% | mixed |

**The serial sqrt-Newton + serial post-Newton division = 91% of wall time.** Everything else is rounding error.

**CPU sampler timeline** (`traces/500M_cpu_run_cpu_samples.log`):

```
20:56: 15.19 cores  RSS=1.01 GB   ← Phase 1+2 parallel
21:07: 1.02 cores   RSS=3.58 GB   ← single-thread pinned (SafeMpzSqrt starts)
... 24 samples all at 1.02-1.04 cores ...
01:09: 1.03 cores   RSS=5.39 GB   ← still single-threaded ~4h later
01:20: 14.73 cores  RSS=9.57 GB   ← briefly parallel at end (verify/cleanup)
```

24 consecutive 1-core samples across 4 hours — unambiguous.

**Top exclusive findings** (500M cpu topN, `traces/20260519_205513_cpu_500000000d/summary.txt`):

| Rank | Function | Exclusive % | Interpretation |
|---|---|---|---|
| 1 | `LowLevelLifoSemaphore.WaitForSignal` | **70.48%** | The other 23 worker threads parked waiting for work that never comes (single-threaded compute) |
| 2 | `SafeMpzMul` | **23.76%** | The actual work happening on the 1 active core |
| 3 | `Thread.Sleep` | 17.22% | More parked-worker time |
| 4 | `Missing Symbol` (native GMP) | 8.84% | libgmp-10 internals (mostly mpn_mul / mpn_sqr / mpn_tdiv inside SafeMpzMul) |
| 5 | `PInvoke.WaitMessage` | 8.81% | UI message pump |
| 6 | `WaitHandle.WaitOneNoCheck` | 8.78% | More sync wait |
| 9 | `Form1.SafeMpzDiv` (incl) | 8.00% | The post-Newton division |
| 10 | `Form1.SafeMpzReciprocal` (incl) | 6.94% | Inside sqrt-Newton (Barrett reciprocal step) |
| 11 | `Form1.SafeMpzSqrt` (incl) | 6.11% | Top-level sqrt |
| 12 | `BinarySplitChunk` (excl) | 1.09% | Phase 1 — almost negligible at this scale |
| 13 | `gmp_lib.mpz_inits` (excl) | **0.90%** | **Allocator pressure has VANISHED at this scale** |

(Exclusive percentages are over total CPU samples across all 24 threads — they don't sum to 100% across functions because "exclusive" excludes child function time.)

### What changed vs. the 100M-only reading

**Allocator pressure is scale-dependent**: at 100M it was 41.7% combined (`mpz_inits` + `mpz_clears`), at 500M it's 0.90% + 0%. Why: the per-call allocator cost is fixed-ish per `mpz_inits` invocation, but the *work between* `mpz_inits` calls grows as N^(1.5-2) with operand size. At 5B scale, allocator pressure will be even smaller — well under 0.1% of wall time.

**Implication for #47**: the playbook describes #47 (per-CPU pool heads) as "a multiplier on every later issue". The 500M data says: at 5B-scale post-Newton compute, **#47 is essentially a no-op** — there's so little allocation per second of CPU that pool contention can't be measurable. #47 still helps Phase 1+2 + smaller-scale runs (where allocator is 12-41% of CPU), but it is NOT the prerequisite that #44/#55 hinge on. **Downgrade #47 from Phase 1 critical to Phase 1 nice-to-have.**

**Implication for #55 + #44**: they are now CONFIRMED as the dominant Phase 2 wins. With 91% of 500M wall in single-threaded SafeMpzSqrt + SafeMpzDiv (both ultimately bottlenecked on serial SafeMpzMul recursion inside SafeMpzReciprocal), lifting §166/§167/§168 (#55) and enabling 9-way sub-product parallelism (#44) directly target the dominant 91%. At 24 cores, even 6× practical parallelism (per #72's DDR5 bandwidth estimate) shrinks the 4h serial work to ~40 min.

**Implication for #41 (P/E cores)**: the parallel phases at 500M are <5% of wall. Hybrid scheduling matters most when there's parallel work to schedule. At 5B scale where 80h of post-Newton is serial, P/E core attribution matters for the 23 PARKED worker threads (which can spin on E-cores wasting power but not on perf), not for the active compute. **Demote #41 back toward Phase 3-4**, contradicting my earlier 100M-only read.

### Revised #72 reprioritization (3 data points: 100M + 500M + 1B sampler)

Original: `#50 → #74 → #47 → #55 → #44 → #37 → #60 → #43 → #42`

**Now recommended:**

1. **Phase 0** (done): #50 ✅, #74 ✅
2. **Phase 1**: **#55 first** (lift the gate) + **#44 second** (enable the parallelism #55 unlocks). These two together address 91% of 5B wall. #47 deferred to Phase 2.
3. **Phase 2**: **#60** (parallel §accum), **#43** (NR-loop iter pipelining), **#42** (a×r / q×b overlap) — second-derivative wins on top of the now-parallel SafeMpzMul. **#47** (allocator) now lands here as a Phase 1+2-scale optimisation, not a 5B-scale prereq.
4. **Phase 3**: **#37** (parallel decimal converter) — still correctly delayed.
5. **Phase 4-5**: as playbook.

The single biggest insight: **the playbook's `#47 → #55 → #44` ordering is inverted relative to the 5B bottleneck.** #55 + #44 should land first; #47 follows once parallel scaling is real.

### Why 1B still adds value (overnight run plan)

500M gave us function-level breakdown. 1B will give:
1. **Cross-check at 2× operand size** — extrapolation confidence for 5B from 3 data points (100M / 500M / 1B) is much stronger than 2.
2. **Newton iteration count empirical** — 500M shows 3 sqrt-Newton steps (kBitsX 350M→700M→1.4B→target). At 1B we'll see 4 steps. Confirms scaling.
3. **Full post-Newton SafeMpzDiv at 1B-scale operands** — 500M's SafeMpzDiv was 1h on 1.3B/1B digit operands. At 1B, SafeMpzDiv operates on ~2.7B/2B digit operands — much closer to 5B's 5.4B/5B operand sizes. This is the most extrapolation-relevant data we can capture in <16h.
4. **Validates §217** — 1B run with `-AutoCheckpoint` will produce `gmpPi.bin` (and possibly `div_q.bin`, `nr_r.bin`) at completion. Pre-§217, those would have been deleted. Post-§217, they survive — giving us a future cheap-resume base for testing parallelism changes.

## 1B cpu trace findings (and an unexpected discovery)

Run: `traces/20260520_012506_cpu_1000000000d/`. Wall: **14h 21m** (01:25 → 15:46).
**Did NOT complete — crashed in `SafeMpzDiv §171` Barrett correction.** But §76 + §217
together saved everything that mattered: clean process exit, `.nettrace` flushed,
all 9 checkpoints (`gmpSqrt`, `finalT`, `gmpNumer`, `mpR0/1/2`, `nr_r`, `sqrt_newton`,
`nr_raise`) preserved on disk for future resume.

### Wall-time breakdown (1B)

| Phase | Wall | Cores | Notes |
|---|---|---|---|
| Phase 1 + Phase 2 + Step 1-3 setup | ~2 min | parallel | trivial |
| **SafeMpzSqrt Newton (4 steps)** | **~9h 44m** | **1** | kBitsX 700M → 1.4B → 2.8B → 3.32B (target) |
| Q split + R0/R1/R2 numerator | ~18 min | parallel-ish | Phase 3 |
| gmpNumer checkpoint saved | 2 sec | — | snap_Phase3 |
| **SafeMpzDiv (numer / T) — partial** | **~4h 19m** | **1** | crashed in §171 quotient correction at 15:46 |
| **TOTAL (incomplete)** | **14h 21m** | mostly 1 | |

If §171 had not failed and SafeMpzDiv completed normally, **full 1B wall ≈ 15-16 hours**.

### Top exclusive findings (1B cpu topN)

| Rank | Function | Exclusive % | Δ vs 500M |
|---|---|---|---|
| 5 | `LowLevelLifoSemaphore.WaitForSignal` | **59.27%** | 70.48% → 59.27% (dropped — slack taken by SafeMpzMul + finalizers) |
| 6 | **`SafeMpzMul`** | **32.41%** | 23.76% → 32.41% (more time in actual compute as operands grew) |
| 13 | `Thread.Sleep` | 17.68% | 17.22% → 17.68% (worker park-wait, ~unchanged) |
| 14 | `Missing Symbol` (libgmp-10 internals) | **17.57%** | 8.84% → 17.57% (2× jump — native GMP work growing with operand size) |
| 30 | **`GC.RunFinalizers`** | **17.12%** | **0.61% → 17.12% (28× jump — NEW BOTTLENECK)** |
| 28 | `PInvoke.WaitMessage` | 17.52% | 8.81% → 17.52% (UI message pump idle while compute runs) |
| 29 | `WaitHandle.WaitOneNoCheck` | 17.51% | 8.78% → 17.51% (paired wait pattern) |
| 32 | `SafeMpzDiv` (incl) | 16.55% | 8.00% → 16.55% (twice as much wall in div) |
| 42 | `BinarySplitChunk` (excl) | 2.26% | 1.09% → 2.26% (Phase 1 ~unchanged absolute, halved relative) |
| 43 | `gmp_lib.mpz_inits` (excl) | 1.73% | 0.90% → 1.73% (allocator still negligible) |

### The new finding: `GC.RunFinalizers` 17.12% exclusive

At 500M it was 0.61%. At 1B it's 17.12% — a **28× jump for 2× run duration**.

Every `mpz_init` call allocates a managed `mpz_t` wrapper struct AND a native limb
buffer. The wrapper has a finalizer that runs on the GC finalizer thread when the
managed object is collected. Over 14 hours, finalizer backlog grows; the finalizer
thread starts competing for CPU with the single compute thread.

This is a **scale-and-duration-dependent bottleneck** that the playbook does not
mention. Not unique to single-threaded code — but visible there because there's no
parallel work hiding the finalizer thread's CPU steal.

**New issue worth filing** (not in #41-#65 catalog): **Replace `mpz_t` wrapper
finalizer with explicit dispose semantics**, or no-op the finalizer when the wrapper
is known-deterministically-freed. Estimated impact at 1B: 17% wall = ~2.5 hours.
Larger at 5B (since duration is ~5×).

### The bug we found: `SafeMpzDiv §171` Barrett correction at 1B+ precision

Crash:

```
EXCEPTION: SafeMpzDiv §171 pass 1 did not reduce rem SIZE:
   before=192030933, after=192030933, szB=140125808, szProd=192030933,
   ptrMatch=True, bTopBits=34.
ROOT CAUSE: Barrett estimate was off by ~2^3,321,928,000 (far more than the usual
±1-2). Single-limb top-limb correction cannot converge when bTopBits<48 and rem/b
value-ratio is ~2^(3,321,928,000). Investigate upstream: SafeMpzMul(ar,a,r),
BigShiftRight(ar,kBits), SafeMpzReciprocal precision at 5B scale.
```

`bTopBits=34` is the giveaway. The §171 Barrett quotient correction loop assumes the
divisor's top limb has at least ~48 significant bits — that's what makes the
single-limb top-bits correction `q_corr = (rem_top << 16) / b_top` accurate to ±1-2.
When `bTopBits=34`, the correction value can be off by 2^14 = 16,000× per pass, but
the loop only does one pass before giving up. At 1B (3.32B-bit precision) the
SafeMpzReciprocal output is just precise enough to make this fire; at 500M (1.66B-bit
precision) it didn't.

This will fire 100% at 5B unless fixed.

**Filed as [GitHub issue #78](https://github.com/TGoodhew/PI-BillionDigits/issues/78)** —
P1, blocks all #72 5B-scale parallelism testing. Cheap to iterate on the fix using
the preserved 1B checkpoints at `C:\PiPreserved_1B_trace_crash_2026-05-20\`
(~10 min per test cycle, resume from `gmpNumer.bin`, vs ~10h from scratch).

### Triangulated scaling law (100M / 500M / 1B)

The three data points let us fit the empirical scaling exponent for the post-Newton
wall time:

| Scale | Sqrt-Newton wall | Ratio vs prior |
|---|---|---|
| 100M | n/a (under SAFE threshold, used native `mpz_sqrt`) | — |
| 500M | 3h 00m | — |
| 1B | 9h 44m | **3.24×** for 2× operand |

3.24× per 2× = log₂(3.24) = **1.70 → empirical exponent N^1.70**.

This is closer to N^log₂(3) ≈ N^1.58 (Karatsuba) than to N^log₂(9) ≈ N^3.17 (naive
3×3) — consistent with SafeMpzMul's 3-way split + sub-product cache reuse. The
playbook's `N·log(N)` estimate for 5B scaling assumes FFT multiplication, which we
don't use; the actual scaling is significantly worse than playbook estimates.

**Revised 5B sqrt-Newton extrapolation**: from 1B's 9h 44m × 5^1.70 = **~140 hours**.

That's much higher than the playbook's "~80h post-§NR at 5B" estimate. The 80h
estimate matched the actual May 2026 5B run because that run **resumed from §NR-ckpt
iter 36 mid-Newton** rather than starting fresh — so it skipped roughly half the
Newton iterations. A fresh 5B run today would take ~140h sqrt-Newton + ~40h
SafeMpzDiv + checkpoints + decimal conversion = **~200 hours wall for a clean 5B
compute**, with no parallelism speedup.

**Implication for parallelism payoff projection**: the parallelism rollout's
expected 7-12× speedup is on a **larger absolute baseline than the playbook
estimates**. If achieved, total 5B wall: **~20-30 hours** vs the current
single-threaded 200h. That's the real prize.

### Final #72 reprioritization (3 data points + 1 new bottleneck + 1 new bug)

**ORDER**: `#50 ✓ → #74 ✓ → [new: file §171 bug fix] → [new: finalizer issue]
→ #55 → #44 → #47 → #60 → #43 → #42 → #37 → ...`

The two **new prerequisites** before Phase 1 parallelism work:
1. **§171 bug fix** — without this, any 5B-scale parallelism test will crash before
   reaching the parallel SafeMpzDiv. Block.
2. **Finalizer issue** — 17% of 1B wall, scales with duration. Lower-cost change than
   the parallelism work, addresses real bottleneck. Worth landing in Phase 1.

The other ordering recommendations from the 500M analysis stand:
- `#47 (per-CPU pool heads)` is a 100M/Phase-1-2-scale optimisation, not a 5B prereq
- `#55` + `#44` are the dominant Phase 2 wins (target 91% of wall at 500M+)
- `#41 (P/E core attribution)` matters for parallel phases (<5% of 5B wall) — defer

### Confounders

- **1B run**: clean, no concurrent activity. Process exited gracefully via §76
  (`[FormClosing] Reason=ApplicationExitCall`), `dotnet-trace` flushed the buffer
  cleanly, `Run-PiCompute.ps1`'s `Invoke-CheckpointBackup` ran to completion
  (`snap_L13 → SnapshotStore (3 files), snap_Phase3 → SnapshotStore (19 files)`).
- **500M run**: clean.
- **100M runs**: clean.

### §75 / §76 / §217 in-the-wild validation

The 1B crash inadvertently validated the entire prerequisite bundle from PR #77:

- **§75** would have run via autoverify — didn't reach it (crash was upstream of
  `RunVerification`). No regression, just not exercised.
- **§76** **WORKED**: process called `Application.Exit()` cleanly with
  `Environment.ExitCode = 1`, message loop terminated, `Run-PiCompute.ps1` exited
  the `Start-Process -Wait` with non-zero status, `BackupCheckpoint` ran on the
  post-crash state. **No hung process, no manual kill needed.**
- **§217** **WORKED**: all 9 mid-run checkpoints preserved on disk. Pre-§217, the
  §171-ckpt deletion at SafeMpzDiv§202-exit would have eaten `div_q.bin` after each
  successful SafeMpzDiv inside the sqrt-Newton iteration (10+ such calls during sqrt).
  Post-§217, they all survived.

**This means**: when we fix the §171 bug, the next test run can resume from
`gmpNumer.bin` and reach the failure point in ~10 minutes (vs 14 hours from scratch).
That's the iteration-cycle multiplier #72 needed.

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
