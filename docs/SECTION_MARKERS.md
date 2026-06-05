# Section markers (`§NNN`) — index

The source (chiefly `Form1.vb`) is threaded with **fix-markers** of the form `§NNN` (and a few named
ones like `§NR-ckpt`, `§piCkpt`). Each marks the code introduced or changed by one numbered entry in
the [Change Log](../Change_LOG.md), and the same number is used in commit messages and the project
memory — so a marker lets you correlate a line of code with *why* it exists across code ↔ commit ↔
Change Log.

- **Where the detail lives:** the full rationale for every marker is its section in
  [Change_LOG.md](../Change_LOG.md). This file is just a one-line lookup.
- **Numbering:** the earliest entries are titled "Section N" in the Change Log; later ones use the
  `§N` form. They share one number line — "Section 17" and "§17" are the same change.
- **Sub-markers:** a few markers carry a suffix (e.g. `§171-iter`, `§201-raise`, `§251-fix`) for a
  follow-up to the base change; find them under the nearest base number in the Change Log.
- This index is generated from the Change Log section headings; regenerate it if the headings change.

## Load-bearing markers (most referenced in code)

These recur throughout the math path and are worth knowing:

| Marker | What it is |
|--------|-----------|
| §3 | Disk-based binary split (streams P/Q/T chunks to NodeCache). |
| §6 / §30 | Custom VirtualAlloc/VirtualFree GMP allocator (`GmpNativeAlloc.dll`). |
| §39 | `SafeMpzMul` §39 column-group accumulate via mpn limb offset. |
| §107 | Reciprocal underestimate invariant (the correctness contract `SafeMpzDiv` relies on). |
| §160 | `SAFE_LIMB_THRESHOLD = 5,000,000` — the 3×3 split / FFT-safe cap. |
| §171 / §218 | `SafeMpzDiv` quotient adjust loop that restores the exact floor. |
| §200 / §201 | Reciprocal-Newton precision schedule / `§201-raise` resume seeding. |
| §216 | Serial chunked decimal converter (fallback). |
| §226 / §270 | Parallel recursive-halving decimal converter (default ≥ 1.5 B). |
| §247 / §248 / §249 | P/E affinity watchdog + E-core I/O overlap. |
| §250 / §251 | `SafeMpzMulHigh` / `SafeMpzMul_ChunkedGrid` (chunked-grid multiply). |
| §262 / §269 | Divide a×r (HIGH) / q×b (full) routed through the chunked grid. |
| §267 / §268 | Adaptive chunked-grid cell size (default on). |
| §272 | Reciprocal-Newton 126-bit seed + sound convergence detector. |

## Full index

| Marker | Description (date, issue) |
|--------|---------------------------|
| Section 1 | Debug Code Removed |
| Section 2 | Crash Visibility and Logging |
| Section 3 | BinarySplitGMP: Complete Rewrite (In-Memory → Disk-Based) |
| Section 4 | Combine Loop Memory Optimisations |
| Section 5 | ComputePiGMP Memory Optimisations (Final-Phase Arithmetic) |
| Section 6 | VirtualAlloc/VirtualFree Custom GMP Allocator |
| Section 7 | Three-Way Split Multiply (gmpNumer \*= finalQ) |
| Section 8 | displayStr Memory Release |
| Section 9 | Summary of All Changed Locations |
| Section 10 | Run-Time Crash Fixes (1-Billion-Digit Testing) |
| Section 11 | String Conversion Progress Ticker |
| Section 12 | OutOfMemoryException in `piCharPtr.ToString()` |
| Section 13 | Native Buffer Streaming (eliminate managed pi string) |
| Section 14 | Exception Handling Consolidation |
| Section 15 | Corrupted `size_t` in GMP Allocator Callbacks |
| Section 16 | Level-16 Crash Diagnostics: Operand Size Logging |
| Section 17 | GMP 32-bit `mp_size_t` Overflow in FFT Multiplication |
| Section 18 | SafeMpzMul: Struct-Aliasing Crash in `mpz_add` Accumulation |
| Section 19 | SafeMpzMul: `shifted` Buffer Realloc Crash in `mpz_mul_2exp` |
| Section 20 | SafeMpzMul Recursion and Post-Combine Large Multiplications |
| Section 21 | Combine-Step Pre-Alloc Guard: Small VirtualAlloc Buffers Freed via CRT |
| Section 27 | Compute Thread Priority and Power Throttling |
| Section 22 | SafeMpzMul: `mp_bitcnt_t` Overflow for Large Equal-Size Operands |
| Section 23 | Division Pre-Alloc Guard: Small VirtualAlloc Buffer for `gmpPi` |
| Section 24 | `Chr()` Encoding 1252 Not Available on .NET Core |
| Section 25 | Verify Button Searches Full Native Buffer Without Interrupting Display |
| Section 26 | Defensive Fixes for Wrong-Result Pi Buffer |
| Section 28 | Diagnostic Logging: SafeMpzMul Verbosity Reduction |
| Section 29 | SafeMpzMul `_shiftedLimbs` Buffer Too Small for Large Asymmetric Operands |
| Section 30 | GMP `mpz_export` 32-bit Overflow for Large mpz_t (Issue #12) |
| Section 31 | `SafeMpzMul` Lazy A-Piece Creation to Reduce Peak Memory (OOM at Level 18) |
| Section 32 | Diagnostic Logging to Bisect Post-`done` Crash in `SafeMpzMul` |
| Section 33 | Fine-Grained Logging to Pinpoint Crash After `loop i=0 j=2: inner returned` |
| Section 34 | `SafeMpzMul`: Defer `shifted` Pre-Allocation to Inside i-Loop to Reduce Peak Memory |
| Section 35 | `SafeMpzMul` A-Piece Direct Limb Extraction to Eliminate Atmp Allocations |
| Section 36 | `SafeMpzMul` Conditional `shifted` Pre-Alloc (Only When Two-Step Shifts Exist) |
| Section 37 | `SafeMpzMul` Unconditional Per-Iteration `shifted` Pre-Alloc to Eliminate Organic L→L Reallocs |
| Section 38 | `SafeMpzMul` Per-j `shifted` Allocation: Allocate After Inner Returns, Free After Add |
| Section 39 | `SafeMpzMul` `result.Pointer` Corruption: Save and Restore Native Struct Address |
| Section 40 | `SafeMpzMul` Struct-Contents Corruption: Separate Accumulator Object |
| Section 41 | `SafeMpzMul` Pointer Corruption Extends to All Outer `mpz_t` Objects |
| Section 42 | `SafeMpzMul` `mpz_t.Pointer` Assignment Does Not Persist; Bypass Wrapper Entirely |
| Section 43 | `SafeMpzMul` Managed Stack Frame Corruption by Native GMP |
| Section 44 | `SafeMpzMul` Stash accumPtr in result's Native Struct, Not a Managed Stack |
| Section 45 | `SafeMpzMul` Case 1 Heap Overflow: `mpz_inits` → `mpz_init2` for A_part |
| Section 46 | Three-Pass Multiply: `mp_bitcnt_t` Overflow for `k2 = 2 × thirdBits` |
| Section 47 | Three-Pass Multiply Q-Split: Silent Crash in `mpz_tdiv_q_2exp` (Pre-alloc Missing) |
| Section 48 | Thread-Safe GMP Allocator Callbacks (`AppendLog` helper) |
| Section 49 | Parallel Phase 1: `Parallel.For` over 137,700 Independent Chunks |
| Section 50 | Progress Updates During Old Cache Deletion |
| Section 51 | Fix Phase 1 Status Label Never Updating for Small Chunk Counts |
| Section 51 | Fix Phase 1 Status Label Not Updating on Full 1B Run (Timer-Based Polling) |
| Section 54 | Parallel Multiplications Within Phase 2 Serial Combines |
| Section 62 | Degree-of-Parallelism Caps to Prevent Oversubscription |
| Section 61 | Parallel Three-Pass Q Multiply + Non-Blocking GC Between Levels |
| Section 60 | Parallel.Invoke Inside Parallel Phase 2 Pairs |
| Section 59 | Parallel 9 Sub-Products Inside SafeMpzMul |
| Section 58 | Full RAM Mode (DISK_THRESHOLD raised to 200,000) |
| Section 57 | Phase 2 Level Progress in Status Label |
| Section 56 | Larger Staging Buffer in SerializeOneMpz / DeserializeOneMpz |
| Section 55 | Single-File L0.bin Format for Level-0 Chunks |
| Section 53 | Parallel Phase 2 Combines (Levels 1–N-3) |
| Section 68 | GMP Pool Cap Raised to 256 |
| Section 69 | DOP Rebalance: Phase 2 Outer=ProcessorCount, SafeMpzMul Inner=1 |
| Section 63 | Headless / Automation Mode + PowerShell Script |
| Section 70 | Make Run-PiCompute.ps1 Machine-Independent |
| Section 71 | Portable Output Paths + Remove Vestigial Chunk Size UI |
| Section 64 | Skip Display Loop When Display Is Off |
| Section 65 | Bucketed VirtualAlloc Pool (GMP Allocator v3) |
| Section 66 | P-Core Affinity Detection + Thread Pool Pre-Warm |
| Section 88 | Raw DllImport in SafeMpzMul Slow Path; GmpRaw_add in Phase 2 (issues #25, #26) |
| Section 87 | Phase 2 Parallel Path: Remove Inner Parallel.Invoke (issue #24) |
| Section 86 | SafeMpzMul: Shared Shifted Buffer (issue #23) |
| Section 85 | GMP Pool Allocator Hot-Path Optimisation (issues #20, #21, #22) |
| Section 84 | Power-of-10 Test Suite (issue #18) |
| Section 83 | Runtime Logging Level (issue #15) |
| Section 82 | Auto-Verify Checkbox; Verification Results in Status Bar (issue #12) |
| Section 81 | Display Streaming Performance Improvements (issue #16) |
| Section 79 | Fix Pool Corruption: Pre-Alloc Blocks Must Use PoolGet Not VirtualAlloc |
| Section 78 | Fix SafeMpzMul Fast Path: Use Raw P/Invoke to Avoid Managed Wrapper Corruption |
| Section 77 | Granular Per-Call Logging in §61 Multiply Block |
| Section 76 | Fix Three-Pass Multiply Pre-Alloc Crash on Small Digit Counts |
| Section 75 | Button Text Centering, Uniform Size, and Equal Row Spacing |
| Section 74 | Revert Output Directory to C:\PiOutput |
| Section 67 | `--verify-at` and `--verify-contains` CLI Options |
| Section 94 | Level-Boundary Auto-Checkpoint / Resume |
| §100 | SafeMpzSqrt, SafeMpzDiv, SafeMpzReciprocal, BigShiftRight, BigShiftLeft |
| §101 | PreAllocMpzToLimbs: bypass GMP S→L realloc crash in BigShiftRight |
| §102 | BigShiftLeft first-chunk pre-alloc (partial fix) |
| §103 | snap_Phase3 checkpoint: skip Phase 1/2 on Phase 3 crash |
| §104 | Immediate SnapshotStore backup after every snapshot write |
| §105 | BigShiftLeft full pre-alloc: fix all chunks, not just the first |
| §106 | Affinity watchdog (#33), Phase 3 parallelism gaps (#34), Newton + Phase 3 checkpointing |
| §107 | SafeMpzReciprocal: Newton iteration guard and floor truncation |
| §108 | SafeMpzDiv: dense diagnostic logging + adj-loop safety abort |
| §109 | SafeMpzMul general-path per-sub-product diagnostics + q bottom-limb logging |
| §110 | SafeMpzMul/SafeMpzDiv: sub-product top-2 limb diagnostics + `a` top-2 |
| §111 | SafeMpzMul/SafeMpzDiv: targeted error-limb diagnostic |
| §112 | SafeMpzDiv: sparse ar limb sweep to localise middle-zone error |
| §113 | SafeMpzDiv: verify q middle limbs after BigShiftRight |
| §114 | SafeMpzMul §39: per-column diagnostics for q×b |
| §115 | SafeMpzMul: distinguish r×r vs q×b calls via buffer-identity check |
| §NR-raw | SafeMpzReciprocal: replace managed wrapper calls with raw P/Invoke in Newton loop |
| §123-§126 | Targeted limb diagnostics in SafeMpzReciprocal Newton final iteration |
| §128 | SafeMpzMul: disable §39 column fast path when any split piece is zero |
| §175/§181 | SafeMpzMul: remove result.Pointer re-reads after inner calls |
| §176–§183 | §176–§183 — SafeMpzMul diagnostic probes |
| §144-serial | SafeMpzDiv b×r diagnostic: force serial |
| §184 | SafeMpzDiv: bypass managed wrapper for qb and remainder (fix STATUS_ASSERTION_FAILURE crash) |
| §SqNewton | SafeMpzSqrt Newton loop: bypass managed wrapper for nTrunc/xTrunc/q (fix STATUS_ASSERTION_FAILURE crash) |
| §NumeratorDiv | ComputePi final division: restore Pointer fields after §78 corruption (fix STATUS_HEAP_CORRUPTION) |
| §Phase3OOM | Step 2 squaring OOM crash at 5B digits |
| §171-iter | SafeMpzDiv: iterate top-limb correction + capture raw prodHdr (5B SafeMpzSqrt crash fix) |
| §171-barrett | 5B SafeMpzSqrt Newton step 1: Barrett precision bug (NOT a §171 bug) |
| §5B-investigate | Boundary-limb logging to localise the upstream Barrett bug |
| §201-raise | Newton-raising for SafeMpzReciprocal (NativeOptimization branch, 2026-04-27) |
| §171-ckpt | Save Barrett quotient `q` before `q×b` (NativeOptimization branch, 2026-04-30) |
| §piCkpt | Save gmpPi after final divide, before mpz_get_str (NativeOptimization branch, 2026-05-01) |
| §202-trace: SafeMpzDiv exit + SafeMpzSqrt post-divide tracing | §202-trace: SafeMpzDiv exit + SafeMpzSqrt post-divide tracing |
| §211 | Defer §NR-ckpt cleanup until SafeMpzDiv succeeds (2026-05-15) |
| §212 | Depth-0 §gen RAM diagnostics (2026-05-15) |
| §213 | Eager `r`-clear in SafeMpzDiv when `_5b_verify=False` (issue #66, 2026-05-15) |
| §214 | Skip P+Q load when `gmpNumer.bin` resume will fire (issue #67, 2026-05-15) |
| §215 | Int32 overflow in §gen / SafeMpzDiv log-offset arithmetic (2026-05-17) |
| §216 | Chunked decimal conversion to avoid mpz_get_str crash at 5B (2026-05-19) |
| §74 | Chunk-N-of-M progress indicator during chunked decimal conversion (2026-05-19, issue #74) |
| §75 | RunVerification crashes at 5B via Marshal.PtrToStringAnsi (2026-05-19, issue #75) |
| §76 | Headless mode hangs on exception (missing Application.Exit) (2026-05-19, issue #76) |
| §217 | Checkpoint-preservation invariant: no checkpoint deleted mid-run (2026-05-19) |
| §218 | SafeMpzDiv §171 normalization at 1B+ precision (2026-05-21, issue #78) |
| §219 | Drain finalizer queue at idle break points (2026-05-21, issue #79) |
| §225 | §201-raise scope-compatibility gate (2026-05-22, issue #80) |
| §226 | Parallel recursive-halving decimal converter (2026-05-22, issue #37) |
| §227 | Parallel Q-split (2026-05-22, issue #61) |
| §228 | Parallel xSq / x1Sq squarings in SafeMpzSqrt final-adj (2026-05-23, issue #54) |
| §229 | Parallel out-of-place BigShiftLeft (2026-05-23, issue #56) |
| §230 | §201-raise exact-scale reuse (2026-05-23, issue #81) |
| §231 | Scale-aware DOP for serial-path Phase 2 (2026-05-23, issue #58) |
| §232 | Async BackupSnapshotToStore via tail-chained Task (2026-05-23, issue #46) |
| §233 | Lift §210 force-serial for R0/R1/R2 multiplies (2026-05-23, issue #53) |
| §234 | Tail-mode parallel top-split for BinarySplitChunk (2026-05-23, issue #59) |
| §235 | Performance trace pass (2026-05-26, issue #50) |
| §236 | Preserve pi_phase_log.txt across relaunches (2026-05-27, issue #84) |
| §237 | 64-bit safe pointer arithmetic for residual NR diagnostic reads (2026-05-27, issue #86) |
| §238 | Thread-local nesting cap for SafeMpzMul recursive Parallel.For (2026-05-28, issue #87) |
| §239 | 64-bit-safe ar/b boundary reads in SafeMpzDiv (2026-05-31, issue #71 residual) |
| §241 | GmpNativeAlloc pool census + phase-boundary trim (2026-05-31, issue #69) |
| §242 | Cache bTrunc across capped-precision reciprocal iterations (2026-05-31, issue #93 cand 1) |
| §243 | MemoryBudget: live RAM feedback + adaptive DOP floor (2026-05-31, issue #68) |
| §244 | Parallelize Phase-3 Step 1/2 + pow10 checkpoint (2026-05-31, issues #85, #83) |
| §245 | Fix MemoryBudget floor double-counting (2026-06-01, issue #85 / #68) |
| §246 | Parallel per-column add-chains in the §39 column-group path (2026-06-01, issue #45) |
| §248 | Phase-1 producer-consumer: E-core serializers (2026-06-01, issue #48) |
| §249 | Phase-2 serial-path prefetch: E-core read-ahead (2026-06-01, issue #49) |
| §250–§254 | §250–§254 — Chunked-grid high-product reciprocal (2026-06-01..04, issues #94, #70) |
| §256 | §39 column-group accumulate via mpn offset (2026-06-04, issue #45) |
| §252 / §257 | Logging-level ladder, single 0–5 integer scale (2026-06-04, issue #95) |
| §258–§260 | §258–§260 — Run telemetry, ETA estimator, performance advisor (2026-06-04, issues #62 / #63) |
| §261 | Code-quality review + dead-code removal (2026-06-04, issue #40) |
| §262 | Chunked-HIGH a×r in SafeMpzDiv (2026-06-04, issue #42) |
| §263 / §264 | Bandwidth investigation tooling + test-harness UI fix (2026-06-04, `MemoryPerf` branch) |
| §265 | Split-factor experiment: 4×4 grid rejected, cell SIZE is the lever (2026-06-04, issue #88) |
| §266 | Cell-size sweep at 5B sizes: 16M cell = 8.62× bit-exact (2026-06-04, issue #88) |
| §267 | Adaptive chunked-grid cell size (2026-06-04, issue #88) |
| §268 | Adaptive chunked cell ENABLED BY DEFAULT — 5B bit-identical (2026-06-05, issue #88) |
| §269 | Route q×b through the chunked grid (full mode) (2026-06-05, issue #88) |
| §270 | Parallel decimal converter enabled by default, 5B-safe (2026-06-05, issue #90) |
| §271 | Movable 250k-digit window display (2026-06-05, issue #98) |
| §272 | Reciprocal-Newton seed + sound convergence detector (2026-06-05, issue #88) |
