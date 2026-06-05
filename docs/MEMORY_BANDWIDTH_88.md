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

`--test-dopscan` (DOP sweep), `--test-gridscan` (split-factor) and `--test-cellsweep` (cell-size at
5B sizes) are committed as the reusable measurement harnesses (size via `PI_DOPSCAN_LIMBS`).

## §267 — adaptive cell size (prototype) — 1B VALIDATED bit-identical

`PI_CG_ADAPTIVE=1` (opt-in; default OFF = unchanged 1.5M) sizes the chunked cell ≈ `max(szA,szB)/3`
capped at the FFT-safe 16M (`PI_CG_CELL_MAX`), floored at 1.5M.

- **`--test-chunkedgrid` under the flag:** all PASS — full bit-exact AND HIGH mode correct
  (`highOver`+`highRegionEq`, the §107 contract the reciprocal/a×r rely on). Speedups ~doubled vs
  1.5M (26M² cgHigh 2.79×→**6.84×**, 68M×52M 7.51×→**10.96×**).
- **1B resume-from-snap_Phase3 with `PI_CG_ADAPTIVE=1`:** π SHA-256 = `b153e8d5…56d9b`
  **BIT-IDENTICAL to the 1B oracle**; 282 adaptive-cell engagements (7.29M/14.58M/16M cells), 0
  errors. Phase 3 ran in **1h07m vs the §262 baseline's ~1h50m — ~38% faster at 1B**. Confirms §160's
  FFT-accuracy concern was a misdiagnosis: 16M cells are bit-exact in the real pipeline.
## §268 — adaptive cell ENABLED BY DEFAULT (2026-06-05) — 5B VALIDATED bit-identical

Full-5B validation: resumed from the run-2 `snap_Phase3`/`gmpNumer` with `PI_CG_ADAPTIVE=1`, ran the
final 5B divide (reciprocal + a×r with adaptive **16M** cells — 17×17 = 289-cell grids), §216
conversion, autoverify. Result: **π SHA-256 = `2218ee06…e08983a` — BIT-IDENTICAL to the 5B oracle**;
75 adaptive-cell engagements, 0 errors, **adj-up 0 iters (exact quotient)**. RAM peaked ~40 GB (the
52 GB watchdog never fired — 16M cells are RAM-safe at 5B). Timing: divide 5h02m vs the ~7–8h §gen
baseline (~30–40% faster); the **un-accelerated §gen q×b (~1h34m+) is now the divide bottleneck** —
routing it through the chunked grid (as §262 did for a×r) is the obvious follow-up.

⇒ **`PI_CG_ADAPTIVE` now defaults ON** (§268; opt out `=0`). The chunked grid — the production path
for the dominant 5B reciprocal (#70) + a×r (§262) — uses the FFT-safe-max cell. Validated 1B
(~38% faster Phase 3) + 5B (bit-identical, ~30–40% faster divide), both SHA-matched.

## §269 — q×b routed through the chunked grid (full mode) — 1B + 5B VALIDATED

q×b was the divide's last §gen-recursive stage (~1h34m at 5B; §267/§268 only accelerated the
reciprocal + a×r). §269 routes it through `SafeMpzMul_ChunkedGrid(qb, q, b, 0L)` — **full** mode
(need all of q×b for `rem = a−q×b`), bit-exact, and with the §268 16M cell far faster than §gen.
Gated like §262 (flag `PI_DIV_QB_CHUNKED` default ON + size + DOP, off under `_5b_verify`); qb's
buffer lifecycle is unchanged (§gen finalize at 3562 == chunked at 3226, both swap a GmpNativeAlloc
accumulator into qb).

- **1B:** resume-from-snap_Phase3 (div_q/gmpPi deleted to force the divide) — `[§269] q×b chunked-full
  szQ=51.9M szB=140.1M`, Division complete in seconds, **π SHA-256 = `b153e8d5…` bit-identical**.
- **5B:** resume-from-snap_Phase3/gmpNumer — `[§269] q×b chunked-full szQ=259.5M szB=739M`, adj-up 0
  iters (exact quotient), **`gmpPi.bin` SHA = `34f40cde…` bit-identical to the run-3 oracle binary**
  (value-deterministic serialization ⇒ binary compare is sound, skips the irrelevant ~2–3h §216).
  **Divide ran ~3h14m vs §268's 5h02m** — q×b's §gen ~1h34m became chunked minutes.

⇒ **The whole 5B divide (reciprocal + a×r + q×b) is now on the chunked grid at the FFT-safe-max cell**,
down from the ~7–8h §gen baseline to **~3h14m (~2.3×)**. Remaining divide cost is the reciprocal
Newton; next slow stage is §216 conversion (#90).

## §270 — 5B-safe parallel decimal conversion (#90 closed) — default ON

The §226 parallel converter (mpz→decimal by recursive halving: split `d` digits at `10^(d/2)`,
`Parallel.Invoke` on hi/lo, leaf `mpz_get_str`; byte-identical to §216) was validated at 1B but
**unsafe at 5B** — a naive halve there needs a `10^2.5B` divisor (130M limbs). §270 adds a peel rule:

```vbnet
Private Const CONV_SAFE_PEEL As Long = 500_000_000L
Private Shared Function ConvSplitLowDigits(digits As Long) As Long
    If digits > 2L * CONV_SAFE_PEEL Then Return CONV_SAFE_PEEL  ' peel a fixed 500M-digit low chunk
    Return digits \ 2                                           ' else halve as before
End Function
```

This caps every split divisor at `10^500M` (26M limbs); the 5B power table becomes
31.25M / 62.5M / 125M / 250M / 500M digits — no `10^2.5B`. Parallelism and byte-identity are
preserved (the same divide-and-conquer tree, just with a bounded top split).

- **5B:** resume from gmpNumer+gmpPi, converter only — **5B digits in 933 s (~15.6 min)**: 8 s
  power-table build + 924.8 s parallel halving, vs §216 serial ~47 min (**~3×**). Verify OK;
  **π SHA-256 = `2218ee06…e08983a` bit-identical to the oracle**; RAM ~19 GB.

⇒ **`PI_CONV_PARALLEL` now defaults ON** (opt out `=0` → §216 serial); routes for all digits ≥ 100M.
#90 closed.

## §271 — movable digit-window display (#98)

The display option streamed every digit into a RichTextBox via `AppendText` — ~O(n²) (each append is
O(current length)) and duplicated GB of text on top of the native buffer, so it was unusable at
1B/5B. §271 instead shows a bounded **250k-digit window** read on demand from the native result
buffer, with a `TrackBar` docked under the digit box that scrubs the window across the *whole* digit
range (O(window) per move, constant memory); the RichTextBox's own scrollbar scrolls within the
window, and a label shows "Digits A–B of N". For a large native result `StreamPiToScreen` writes
`pi_digits.txt` and runs Verify immediately, then calls `SetupNavWindow` — no streaming pass.

**Display-only:** the output file and the Verify path both read the native buffer directly and are
untouched, so there is no correctness impact. Build clean, offset bounds checked. It's an
interactive-UI change (the headless `--autostart` path forces display off), so it needs an on-screen
confirmation of the slider before #98 is closed. Landed in commit `8b97a5b` (alongside §270).
