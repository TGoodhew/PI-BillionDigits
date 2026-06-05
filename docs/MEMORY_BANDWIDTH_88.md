# #88 — Memory-bandwidth-bound 5B compute: investigation findings

**Branch:** `MemoryPerf` · **Date:** 2026-06-04 · **Tool:** `--test-dopscan` (§263, `Form1.Bandwidth.vb`)

## Question

The #88 premise: at 5B the §gen multiply is memory-bandwidth bound (live trace: ~37% cores active),
and channel-aware scheduling could break the DDR5 ceiling for a 5–7× unlock. Is that achievable in
software on this machine, and what is the real ceiling?

## Hardware reality (decides feasibility)

`i9-12900K` · **1 socket, 1 NUMA node** · **dual-channel DDR5-5600** (Controller0-DIMM1 +
Controller1-DIMM1) · **30 MB L3** · ~89.6 GB/s peak DRAM bandwidth.

⇒ **Lanes 1 & 2 of the issue (NUMA / per-channel pinning) are physically impossible here:** one NUMA
node means `VirtualAllocExNuma` has nothing to target, and the integrated memory controller
interleaves the two channels at cache-line granularity with **no software API to pin an allocation to
a channel**. Channel-aware placement only pays off on multi-socket / Threadripper / EPYC where
channels map to distinct NUMA nodes.

## Measurement — `--test-dopscan`

Times one large, L3-overflowing §gen multiply across DOP values (env `PI_DOPSCAN_LIMBS`). §gen does a
3×3 split = **9 sub-products**, so wall-time ≈ `ceil(9/DOP)` waves.

**24M × 24M limbs (sub-products 64 MB, ~2× L3):**

| DOP | ms | speedup | waves |
|----:|------:|------:|:--|
| 1 | 189,453 | 1.00× | 9 |
| 2 | 106,395 | 1.78× | 5 |
| 3 | 67,118 | 2.82× | 3 |
| 4 | 65,553 | 2.89× | 3 |
| 6 | 46,408 | 4.08× | 2 |
| 8 | 46,392 | 4.08× | 2 |
| **9** | **27,868** | **6.80×** | **1** |

**12M × 12M limbs (sub-products 32 MB, ~1× L3):** DOP=9 → 4,995 ms, **6.38×**.

## What the data says

1. **The curve shape is wave quantization, not smooth saturation.** With 9 sub-products, DOP that
   doesn't divide 9 wastes a partial final wave (3≈4 → 3 waves; 6≈8 → 2 waves; **9 → 1 wave**). So
   **DOP=9 is optimal** — it matches the 9-way structure exactly.
2. **The gap from ideal 9× is pure bandwidth contention.** A lone sub-product (1-wide) takes 21.0 s
   (24M); nine in parallel take 27.9 s wall ⇒ each is **1.32× slower** under 9-way DDR5 contention.
   `9 / 1.32 = 6.80×` — matches the measurement exactly. 12M: `9 / 1.41 = 6.38×`. So §gen runs at
   **~72% parallel efficiency (~28% lost to bandwidth)**, roughly flat across 12–24M.
3. **The dominant limit is structural, not bandwidth.** §gen uses **only 9 cores** (the 3×3 split).
   On a 24-logical / 16-P box, **15 cores are idle by design** — the issue's "37% cores active"
   (≈8.9/24) is largely just §gen's 9-way fan-out occupying 9 cores, **not** the 9 cores being
   37% utilized.

## Lane assessment for this hardware

| Lane | Verdict |
|---|---|
| **1 — NUMA-pinned placement** | **Impossible** (single NUMA node). |
| **2 — channel-tiled / narrowed DOP** | **Rejected.** No channel API; and narrowing DOP loses more to lost parallelism + wave quantization than it saves in contention. DOP=9 is already optimal. |
| **3 — non-temporal stores in §accum** | **Marginal + risky.** §accum is now mpn-offset (§256, no whole-buffer shift), so the streaming-write win shrank; inserting `_mm_stream` would require a custom mpn routine on the core hot path. Not worth it without a measured ≥10% gain (which needs PerfView/VTune memory counters, unavailable). Deferred. |
| **4 — hardware ROI model** | **The real lever** (below). |
| **NEW — higher-order split (4×4=16)** | **Untested, most promising software direction.** Splitting each operand into 4 → 16 sub-products engages the 15 idle cores. Same total work (16·(N/4)² = N²), but trades against (a) more concurrent memory streams ⇒ worse contention, (b) more per-mul FFT overhead, (c) FFT-overflow headroom (smaller sub-products are safer, not worse). Net effect unknown — could win on the structural cap or lose to contention. **Needs a prototype** (a 4×4 §gen variant behind a flag, measured with `--test-dopscan`-style timing at 5B sizes). |

## Lane 4 — hardware upgrade ROI (model)

Anchored on the measured ~72% efficiency / ~28% bandwidth loss at DOP=9. The memory-bound fraction of
a 5B run is high (~85% per the §235 trace); a bandwidth uplift `B` shrinks that fraction toward
`bound/B`. Rough wall-time projections vs the current ~9 h (post-§262) 5B run:

| Config | Peak BW | vs DDR5-5600 | Projected 5B wall | Note |
|---|---|---|---|---|
| **DDR5-5600 dual (current)** | ~90 GB/s | 1.0× | ~9 h (measured-ish) | the baseline ceiling |
| **DDR5-7200 dual** | ~115 GB/s | +28% | **~7.5 h (~1.2×)** | cheap RAM swap; same board class |
| **Threadripper 7000, quad DDR5-5200** | ~166 GB/s | +85% | **~5.5–6 h (~1.5–1.7×)** | + more cores ⇒ the 9-way cap also lifts (finer split usable) |
| **EPYC 9004, 12-channel DDR5-4800** | ~460 GB/s | +410% | **~3–4 h (~2.5–3×)** | bandwidth ceiling largely removed ⇒ compute re-binds; the playbook's pre-saturation 5–7× becomes reachable with enough cores |

(Model only — assumptions: ~85% memory-bound fraction, linear bandwidth scaling, no re-binding until
EPYC. Real numbers need a run on the target.)

## Split-factor experiment — `--test-gridscan` (§265)

Tests the idle-core hypothesis directly: drive the chunked-grid full product at coarse k×k grids
(cell ≈ N/k via `_cgCellOverride`) and compare split factors, each bit-checked against §gen. 24M × 24M:

| method | cells | ms | vs §gen | bit-exact |
|---|---:|---:|---:|:--|
| §gen 3×3 (recursive, production) | 9 | 30,974 | 1.00× | ref |
| **chunked 3×3** (flat cells) | 9 | **4,632** | **6.69×** | yes |
| chunked 4×4 | 16 | 4,856 | 6.38× | yes |
| chunked 5×5 | 25 | 6,583 | 4.70× | yes |
| chunked 6×6 | 36 | 7,961 | 3.89× | yes |

- **The higher-order split is REJECTED.** chunked 4×4 (16 cells / 16 cores) is *marginally slower*
  than 3×3 (9 cells), and 5×5/6×6 are progressively worse. More cores don't help — **fewer, bigger
  cells win** (better per-mul FFT efficiency, fewer accumulate passes, less bandwidth contention).
  This is the bandwidth-bound prediction confirmed, and kills the "use the idle cores" idea.
- **Cell SIZE is the dominant knob — and the gap is REAL (Release-confirmed).** The chunked flat-cell
  path is ~6.7× faster than §gen's recursive 3×3 (which re-splits each 8M sub-product down to ~0.9M,
  doing a fresh GMP FFT + a native shift/accumulate at every level). The production chunked grid's
  1.5M cell is far finer than the ~8M FFT-safe optimum at this size.
- **Re-measured in Release (one-off, then reverted to Debug):** §gen 31,631 ms, chunked 3×3 4,728 ms
  → **6.69×, identical to Debug.** The earlier "Debug penalises §gen's managed recursion" hypothesis
  was **wrong** — the heavy work (GMP `mpz_mul`, native `mpn` shift/add) is all native, so build
  config barely moves it. So the gap is genuine, not a Debug artifact. (#70's earlier "chunked 1.4×
  *slower*" was measured at the fine 1.5M cell — cell size, not the grid itself, was the difference.)
- **Why this isn't yet a production win:** at 5B the coarse 8M cell overflows GMP's 33M-limb FFT, so
  the grid must use ≤~16M cells (260M operand ⇒ ~16×16 = 256 cells). The gridscan only proves
  *coarser-beats-finer* at sizes where coarse cells are FFT-safe (N ≲ 48M). The open question is
  whether **≤16M cells still beat §gen at 5B** — needs a dedicated cell-size sweep at large N (the
  current k=3..6 grids FFT-overflow above ~48M).

## Cell-size sweep at 5B operand sizes — `--test-cellsweep` (§266) — THE headline result

Sweeps the chunked grid over cell sizes at **260M × 260M** (the 5B q×b shape, where the 8M cell
overflows GMP's FFT so cells must be ≤16M — the real 5B regime). Reference = 1.5M (production cell);
each bigger cell bit-checked against it. (§gen is opt-in here: at 260M its ~36 GB peak pages on a
64 GB box — observed thrashing >30 min — so it does not gate the run.)

| cell | cells | time | vs 1.5M | bit-exact |
|---|---:|---:|---:|:--|
| **1.5M (production)** | 30,276 | **32.4 min** | 1.00× | ref |
| 4M | 4,225 | 13.3 min | 2.43× | yes |
| 8M | 1,089 | 6.8 min | 4.77× | yes |
| **16M** | 289 | **3.8 min** | **8.62×** | yes |

**The production 1.5M cell takes 32 minutes for one 5B q×b multiply; a 16M cell does it in 3.8 —
8.62×, bit-for-bit identical.** The cost is dominated by per-cell overhead (parallel-wave sync +
serial accumulate), which scales with cell *count*: 1.5M → 30,276 cells, 16M → 289. And the chunked
grid is **already the production path for the dominant 5B muls** — the reciprocal Newton (#70) and
a×r (§262) — so this is on the critical path, not a niche.

**Is the 1.5M cap safe to raise? §160 says no — but §160 is almost certainly a misdiagnosis.** §160
([Form1.vb:3393](../Form1.vb)) capped cells at 1.5M (≤5M product) believing GMP's FFT produces
"silently wrong products" above ~5M total because "FFT coefficients exceed double precision (53-bit
mantissa)." But GMP's large multiply is **Schönhage–Strassen — integer modular arithmetic, not
floating-point** — so there is no double-precision mantissa to overflow. The wrong products §160 was
chasing were later root-caused to the **§200/§201 Newton-convergence bug** (which is why §220 lifted
the sibling force-serial measures §166/167/168). §160's cell cap was simply never revisited. The
*real* hard limit is the FFT size cap — `pl·64 < 2^31 ⇒ ≤ 33,554,431 limbs` — and a 16M cell = 32M
product sits just under it. The sweep being bit-exact at 16M is consistent with this.

**Caveat:** the sweep uses random operands + full products (keepLimbs=0); the production reciprocal/
a×r use HIGH mode (keepLimbs>0). So this is strong evidence, not proof, for the real path. A 1B (then
5B) run at a raised cell size, π bit-identical to the oracle, is required before shipping — the
downside of a silently-wrong 5B digit is catastrophic.

## Conclusion / recommendation

- **No channel-scheduling software lever exists on a single-socket dual-channel desktop** — Lanes 1–2
  are out, Lane 3 is marginal. The bandwidth ceiling caps §gen parallel speedup at ~6.5× (≈72% eff).
- **The higher-order split (4×4) is tested and rejected** (§265) — more cells/cores lose to bigger,
  fewer cells under the bandwidth ceiling.
- **THE lead: raise the chunked-grid cell size from 1.5M toward the ~16M FFT-safe maximum.** Measured
  **8.62×** on a 5B-q×b-sized full product, bit-exact, and the chunked grid is already the production
  path for the dominant 5B muls (reciprocal #70, a×r §262). §160's 1.5M cap rests on an FFT-accuracy
  concern that does not apply to GMP's integer FFT (the real bug was §200/§201). **Next step:** make
  the cell size adaptive (≈`min(operand/√cores-ish, 16M-FFT-safe-max)` behind a flag), test HIGH mode
  (keepLimbs>0) at coarse cells, then validate 1B → 5B π bit-identical. Potentially the single biggest
  5B speedup on the table.
- **Breaking the bandwidth portion is fundamentally a hardware change** (more channels). The Lane 4
  model says DDR5-7200 buys ~1.2×, quad-channel ~1.5–1.7×, 12-channel ~2.5–3×.

`--test-dopscan` (DOP sweep) and `--test-gridscan` (split-factor / cell-size) are committed as the
reusable measurement harnesses (size via `PI_DOPSCAN_LIMBS`).
