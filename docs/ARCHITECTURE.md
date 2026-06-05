# Architecture

A runtime map of how PI-BillionDigits computes π, naming the methods that own each stage. For the
*why* behind individual changes see the [Change Log](../Change_LOG.md); for the `§NNN` markers see
[SECTION_MARKERS.md](SECTION_MARKERS.md); for setup see [BUILD.md](BUILD.md).

The algorithm is **Chudnovsky + binary splitting**, with all big-integer arithmetic delegated to GMP
via P/Invoke. The bulk of the engineering is about not exceeding GMP's 32-bit internal limits, and not
exceeding RAM, at billions of digits.

## Pipeline overview

```
 UI / CLI ─► ComputePiGMP (compute thread, 256 MB stack)
                │
   Phase 1  ────┤  BinarySplitGMP → BinarySplitChunk (parallel leaves)
                │     per-chunk (P,Q,T)  ──► NodeCache\ (disk) or RAM   [RAM Threshold gates this]
                │
   Phase 2  ────┤  bottom-up pairwise combine  ──► root (finalP, finalQ, finalT)
                │     WriteLevelSnapshot per level  (resume points)
                │
   Phase 3  ────┤  final arithmetic on the root values  ──► gmpPi
                │     three-pass multiply · SafeMpzReciprocal/SafeMpzDiv · SafeMpzSqrt
                │     snap_Phase3 checkpoint at entry
                │
   Convert  ────┤  ParallelMpzGetStr (default ≥1.5B) / ChunkedMpzGetStr (fallback)  ──► native digit buffer
                │
   Output   ────┘  Write to file · navigable 250k-digit window display · RunVerification
```

## Stages

**Phase 1 — binary-split leaves.** `BinarySplitGMP` divides the term range into adaptive-size chunks
(`clamp(numTerms\10000, 512, 8192)` terms each) and computes each chunk's three partial products
`(P, Q, T)` in parallel via `BinarySplitChunk` (with `BinarySplitChunkParallelTop` for the last few
chunks, §234). Each leaf is either kept in RAM or streamed to `NodeCache\` — decided by the **RAM
Threshold** (`--threshold` / spinner): node counts above it spill to disk (§3), with E-core
serializer threads (§248) handling the writes.

**Phase 2 — combine.** The leaves are merged bottom-up, one pair at a time, into the root
`(finalP, finalQ, finalT)` — integers hundreds of millions of digits long. Each level is snapshotted
(`WriteLevelSnapshot`, §94) and mirrored to `SnapshotStore\` (§104/§232) so an interrupted run resumes
from the highest complete level (`TryFindBestSnapshot`).

**Phase 3 — root arithmetic** (`ComputePiGMP`, with a `snap_Phase3` checkpoint at entry, §103).
Turns the root rational into the scaled integer π: a memory-bounded three-pass multiply (§7), a
Barrett-reciprocal division (`SafeMpzDiv` ← `SafeMpzReciprocal`), and a square root (`SafeMpzSqrt`).
All of these route their large multiplies through the safe-multiply layer below; none call GMP's FFT
multiply or its divide/sqrt directly (which overflow GMP's 32-bit `mp_size_t` at 5 B).

**Decimal conversion.** GMP's `mpz_get_str` crashes above ~2 GB of output, so π is rendered by
`ParallelMpzGetStr` (the parallel recursive-halving converter, default at ≥ 1.5 B digits, §226/§270)
or `ChunkedMpzGetStr` (the serial slab fallback, §216) into a native character buffer.

**Output & verify.** The digits are optionally written to `pi_digits.txt`, shown in a navigable
250,000-digit window (§271), and checked by `RunVerification` against known digit positions.

## The safe-multiply layer (the heart of it)

Every large multiplication goes through one of:

- **`SafeMpzMul`** — splits into a 3×3 schoolbook grid whenever the combined operand size exceeds
  `SAFE_LIMB_THRESHOLD = 5,000,000` limbs (§160), recursing so GMP never sees an over-large FFT
  multiply. Uses raw `GmpRaw_*` P/Invoke on the hot path to bypass marshalling.
- **`SafeMpzMul_ChunkedGrid`** — the production path for the dominant 5 B multiplies (§251). A grid of
  FFT-safe cells accumulated by `mpn_add` at each cell's limb offset, with an **adaptive cell size**
  (§267/§268) and parallel cells. Supports a **HIGH mode** (`keepLimbs > 0`, §250) that computes only
  the top of the product — used by the reciprocal and by the divide's `a×r` (§262) and `q×b` (§269).

**Correctness contract.** `SafeMpzReciprocal` returns a strict *underestimate* of `floor(2^kBits/b)`
(the §107 invariant); the HIGH-product short-muls round *up* so the reciprocal never overshoots.
`SafeMpzDiv` then corrects the Barrett quotient to the exact floor with its §171/§218 adjust loop.
This is why over-estimating the high-products is safe and π stays bit-identical. (See the XML-doc on
`SafeMpzReciprocal` and `SafeMpzDiv`.)

## Cross-cutting concerns

- **Native memory.** A custom VirtualAlloc/VirtualFree GMP allocator (`GmpNativeAlloc.dll`, §6/§30)
  returns large freed pages to the OS immediately, avoiding commit-limit exhaustion; a pre-allocation
  pattern avoids GMP realloc-abort crashes. A memory-budget planner (§243) scales parallelism (DOP) to
  available RAM and can fall back to the chunked grid when a multiply would OOM (§70).
- **CPU affinity.** On hybrid CPUs the process mask stays on all cores; a watchdog (§106/§247) hard-
  pins compute threads to P-cores while E-cores carry overlapping I/O (§248/§249).
- **Checkpoint/resume.** Phase 2 level snapshots and the `snap_Phase3` checkpoint, mirrored to
  `SnapshotStore\`, let multi-hour runs resume after interruption. See the checkpoint-method XML-docs.
- **Observability.** A 0–5 runtime logging ladder (`--log-level`, §252/§257), per-run telemetry +
  ETA + a performance advisor (`Form1.Telemetry.vb` / `Form1.Eta.vb` / `Form1.Advisor.vb`, §258–§260),
  and the `--test-*` diagnostic harnesses (`Form1.Bandwidth.vb` / `Form1.SelfTest.vb`).

## Source layout

| File | Contents |
|------|----------|
| `Form1.vb` | The core (~10k lines): UI, GMP P/Invoke, binary split, safe multiply/divide/reciprocal/sqrt, decimal conversion, checkpointing, logging. |
| `Form1.Designer.vb` | WinForms designer-generated UI. |
| `Form1.Telemetry.vb` / `Form1.Eta.vb` / `Form1.Advisor.vb` | Run-history telemetry, ETA projection, performance advisor (§258–§260). |
| `Form1.Bandwidth.vb` / `Form1.SelfTest.vb` | `--test-*` benchmark and self-test harnesses. |
| `ApplicationEvents.vb` | Unhandled-exception logging. |
| `GmpNativeAlloc/` | The native C++ allocator DLL (issue #30). |
| `Run-PiCompute.ps1` / `Node-Store.ps1` | Build+launch and node-cache management scripts. |
