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

## Conclusion / recommendation

- **No channel-scheduling software lever exists on a single-socket dual-channel desktop** — Lanes 1–2
  are out, Lane 3 is marginal. The bandwidth ceiling caps §gen parallel speedup at ~6.5× (≈72% eff),
  and the bigger limit is the 9-way structural cap leaving cores idle.
- **The one promising software direction is the higher-order split (4×4)** to use the idle cores —
  but it must be prototyped and measured (it may simply move the bottleneck from "idle cores" to
  "worse contention"). This is the recommended next step for #88, and the natural fit for a
  `--test-dopscan`-style A/B at 5B operand sizes.
- **Breaking the bandwidth portion is fundamentally a hardware change** (more channels). The Lane 4
  model says DDR5-7200 buys ~1.2×, quad-channel ~1.5–1.7×, 12-channel ~2.5–3× — useful for deciding
  whether to chase software past this point.

`--test-dopscan` is committed as the reusable measurement harness (size via `PI_DOPSCAN_LIMBS`).
