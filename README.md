# PI-BillionDigits — Change Log

Differences between the original implementation and the current code, with explanations of why each change was made.

---

## Section 1 — Debug Code Removed

`ComputePiGMP` originally contained five `MessageBox.Show` calls used during initial development:

```vb
MessageBox.Show($"Step B: numTerms={numTerms:N0} - about to LogPhase...")
MessageBox.Show("Step C: LogPhase OK - about to call BinarySplitGMP")
MessageBox.Show($"Step D: BinarySplitGMP complete - {nodes.Count} nodes")
MessageBox.Show("Step E: Final combine complete - about to sqrt")
MessageBox.Show("Step F: About to convert to string")
```

These were removed. For runs of hundreds of millions of digits the compute thread runs for 20–40 minutes unattended; a blocking dialog would halt the run indefinitely and serve no diagnostic purpose. All useful information from these checkpoints is now captured by the logging system (Section 2).

---

## Section 2 — Crash Visibility and Logging

**Problem:** when a crash occurred the process terminated with no record of where or why. The original code had only `LogPhase` (coarse phase transitions), and the single catch block in `ComputePiGMP` showed a `MessageBox` that could be missed on a background thread. `ApplicationEvents.vb` was an empty stub.

### 2.1 ApplicationEvents.vb — UnhandledException handler

**Before:** `MyApplication` class body was empty — the `UnhandledException` event had no handler, so unhandled managed exceptions terminated the process silently.

**After:** Implements `MyApplication_UnhandledException`. Walks the full `InnerException` chain and writes the complete exception type, message, and stack trace to the log file before showing a dialog. The log write happens first so a record survives even if the dialog interaction is interrupted.

### 2.2 AppDomain.CurrentDomain.UnhandledException subscription

Added in `Form1_Load`. Covers exceptions thrown on background threads (including the 256 MB compute thread) that do not pass through the VB application framework. Calls `WriteExceptionToLog` with the full chain, and logs the `IsTerminating` flag.

### 2.3 SetUnhandledExceptionFilter (native crash handler)

GMP is a native C library. When GMP encounters an internal error it calls `abort()`, which terminates the process entirely within native code — the .NET CLR never sees it, so no managed handler fires. A Win32-level `SetUnhandledExceptionFilter` callback is registered in `Form1_Load` as a last-resort handler. It appends a `NATIVE CRASH — process terminating` marker to the log file before returning `EXCEPTION_CONTINUE_SEARCH` so Windows can still write a crash dump.

The callback delegate is stored in a field (`_nativeCrashCallback`) for the lifetime of the process. If it were a local variable the GC would collect it, leaving a dangling function pointer that would crash the process when Windows tried to invoke it.

### 2.4 WriteToLog helper

Added a `WriteToLog(message)` method that writes only to the log file — no UI interaction. Each entry includes:

- Timestamp to millisecond precision
- Managed thread ID
- Elapsed computation time
- Current working set in MB
- The message text

`File.AppendAllText` opens, writes, and closes synchronously on every call. This guarantees the entry is on disk before execution continues, so the last entry in the log before a crash identifies the exact operation that failed.

### 2.5 WriteExceptionToLog helper

Added `WriteExceptionToLog(context, ex)`. Walks the full `InnerException` chain and writes the type, message, and stack trace for each level to the log via `WriteToLog`. All catch blocks in `ComputePiGMP` and `BtnCompute_Click` call this before any `MessageBox`.

### 2.6 Per-operation trace logging (LOGGING_DETAIL compile-time constant)

A three-level compile-time constant `#Const LOGGING_DETAIL` controls the volume of diagnostic logging:

| Value | Behaviour |
|-------|-----------|
| `0` | Major `[PHASE]` markers and exceptions only. Zero per-operation overhead. Use for normal stable runs. |
| `1` | Detail on the final combine level and all `ComputePiGMP` steps. **(Default / recommended.)** Adds at most ~50 file writes per run. |
| `2` | Full trace: every `BinarySplitChunk` call and every combine operation at every level and node pair. Use only when debugging an early-level crash; generates very large log files. |

The old code had a single boolean `DETAILED_LOGGING` that was either full detail (>1M file writes for a billion-digit run) or nothing. The three-tier system makes level 1 always safe to leave on: it captures the high-value final operations without the I/O cost of logging every intermediate level.

At `LOGGING_DETAIL = 1`, `BinarySplitGMP`'s combine loop passes an `isLastLevel` flag to `SerializeNodeToDisk` and `LoadNodeFromDisk` so only the final combine level emits detailed entries. `ComputePiGMP` uses `#If LOGGING_DETAIL >= 1` for all its per-operation entries.

### 2.7 BtnCompute_Click exception handling

**Before:** single catch block, silently updated the status label with no log write.

**After:** three separate catch blocks (`OutOfMemoryException`, `OverflowException`, generic `Exception`), each calling `WriteExceptionToLog` before showing a `MessageBox`. The log file record survives even if the dialog is dismissed or the UI is in an inconsistent state.

---

## Section 3 — BinarySplitGMP: Complete Rewrite (In-Memory → Disk-Based)

### Original design

The original `BinarySplitGMP` allocated three arrays upfront:

```vb
Dim chunkP(CInt(numChunks) - 1) As mpz_t
Dim chunkQ(CInt(numChunks) - 1) As mpz_t
Dim chunkT(CInt(numChunks) - 1) As mpz_t
```

For a billion-digit computation `numChunks ≈ 137,000` (with `CHUNK_SIZE = 1024`). Each array element is an `mpz_t` wrapper pointing to a GMP-managed limb buffer. The three arrays themselves are 85+ KB managed objects that land on the Large Object Heap (which is not compacted by default in .NET). All 137,000 chunks were held in RAM simultaneously while the combine loop ran. `STOP_AT = 4`, so the combine ran until 4 nodes remained and then returned them to `ComputePiGMP` for a final in-memory pass.

At 500 million digits the total size of all chunk data is on the order of tens of GB — far more than available RAM. The original design could only work for small digit counts.

### Current design — streaming to disk

**Phase 1 (streaming):** each chunk is computed by `BinarySplitChunk`, immediately serialized to a binary file (`DISK_CACHE_DIR\L0_N{i}.bin`), and the GMP integers are freed. At any moment only one chunk's worth of GMP data is in RAM. `CHUNK_SIZE` was reduced to 512 (from 1024) to keep individual chunk files at a manageable size.

**Phase 2 (combine levels):** rather than combining in arrays, a `List(Of DiskNode)` tracks which file each node lives in. At each level, pairs of nodes are loaded from disk, combined, and the result written back to disk. The input files are deleted immediately after loading. `STOP_AT = 1`, so the combine continues until a single node remains. Between levels a `GC.Collect` flushes .NET heap pressure.

**Phase 3 (final load):** the single remaining node is loaded from disk into memory and returned as a `List(Of Result)` to `ComputePiGMP`.

### DiskNode: Class → Structure

`DiskNode` is now a `Structure` (value type). `List(Of DiskNode)` stores all ~137,000 nodes in a single contiguous array with no per-element heap allocation. When the list is replaced between levels, only one object (the List wrapper) becomes garbage — not 137,000 individual class instances causing a large Gen1/Gen2 collection.

The `Tuple(Of mpz_t,mpz_t,mpz_t)` field was replaced by three separate fields `MemP`, `MemQ`, `MemT`. This also eliminates one `Tuple.Create` heap allocation per in-memory node.

### Return type: Tuple → Result struct

| | Signature |
|---|---|
| **Before** | `ByRef nodes As List(Of Tuple(Of mpz_t, mpz_t, mpz_t))` |
| **After** | `ByRef nodes As List(Of Result)` |

The existing `Result` Structure (P, Q, T fields) was already present for `BinarySplitChunk`'s internal use. Using it as the return type eliminates all `Tuple.Create` allocations in the combine path.

### Serialization helpers: SerializeNodeToDisk / LoadNodeFromDisk

Two new methods handle disk I/O for nodes. Both avoid LOH allocations:

**Serialization — before:**
```vb
Dim pBytes(pLen - 1) As Byte   ' → LOH for large numbers
Marshal.Copy(...)
bw.Write(pBytes)
```
**After:** a single 64 KB staging buffer (below the 85 KB LOH threshold) is reused for all three fields. The GMP-allocated native buffer is walked in 64 KB chunks through the staging buffer directly into the `BinaryWriter`.

**Deserialization — before:**
```vb
Dim pHandle As GCHandle = GCHandle.Alloc(pBytes, GCHandleType.Pinned)
gmp_lib.mpz_import(...)
pHandle.Free()
```
Pinning prevents the GC from compacting the heap segment the array lives in. If `mpz_import` threw, `pHandle.Free()` was never called — a guaranteed leak on any error path.

**After:** `Marshal.AllocHGlobal` allocates from the native (unmanaged) heap, invisible to the GC. The `Free` is in a `Finally` block so it cannot leak.

Both helpers were also updated after the VirtualAlloc custom allocator was installed (Section 6): `SerializeOneMpz` pre-allocates a buffer with `Marshal.AllocHGlobal` and passes it to `mpz_export` directly, so GMP makes no internal allocation. Without this, GMP would allocate the export buffer via VirtualAlloc but `gmp_lib.free` would call CRT `free` on that pointer — a heap mismatch causing an immediate crash.

### BinarySplitChunk: pre-sized collections and error logging

**Before:**
```vb
Dim workStack As New Stack(Of WorkItem)()
Dim results As New Dictionary(Of Integer, Result)
```
Both collections started at default capacity and resized by doubling as they grew. Each resize creates a new internal array discarded as Gen0 garbage. `BinarySplitChunk` is called ~137,000 times per billion digits.

**After:** both collections are pre-sized at the start of the function. A `maxDepth` value (ceiling of log₂(b−a) × 2) bounds the stack depth; the dictionary is pre-sized to (b − a) terms. Neither collection resizes during normal operation.

A `Try/Catch` was added around the main work loop that logs the exact `WorkItem` (a, b, isComplete) that was being processed when an exception occurred, then re-throws.

Progress logging frequency increased from every 1,000 chunks to every 100.

---

## Section 4 — Combine Loop Memory Optimisations

These changes apply to both the `BinarySplitGMP` disk-mode combine loop and the final in-memory combine loop inside `ComputePiGMP`.

### 4.1 Early-free optimisation

**Before** (both loops): all six input operands were kept alive until after all four multiplications and the addition completed, then freed together:

```vb
gmp_lib.mpz_mul(newP,  leftP,  rightP)
gmp_lib.mpz_mul(newQ,  leftQ,  rightQ)
gmp_lib.mpz_mul(tempA, leftT,  rightQ)
gmp_lib.mpz_mul(tempB, leftP,  rightT)
gmp_lib.mpz_add(newT,  tempA,  tempB)
gmp_lib.mpz_clears(leftP, leftQ, leftT, rightP, rightQ, rightT, Nothing)
```

At the point `tempB` was being allocated, ten large integers were simultaneously live: all six inputs plus `newP`, `newQ`, `tempA`, and `tempB`. For the final Level 17 combine (handling ~1.27 GB + ~68 MB nodes), this inflated peak RAM by approximately 2 GB above what the computation actually required.

**After:** each input is freed immediately after its last use:

```vb
gmp_lib.mpz_mul(newP,  leftP,  rightP)
gmp_lib.mpz_clears(rightP, Nothing)           ' rightP done

gmp_lib.mpz_mul(newQ,  leftQ,  rightQ)
gmp_lib.mpz_clears(leftQ, Nothing)            ' leftQ done

gmp_lib.mpz_mul(tempA, leftT,  rightQ)
gmp_lib.mpz_clears(leftT, rightQ, Nothing)    ' leftT, rightQ done

gmp_lib.mpz_mul(tempB, leftP,  rightT)
gmp_lib.mpz_clears(leftP, rightT, Nothing)    ' leftP, rightT done

gmp_lib.mpz_add(tempA, tempA, tempB)          ' in-place (see 4.2)
gmp_lib.mpz_clears(tempB, Nothing)
```

### 4.2 In-place mpz_add optimisation

**Before:** `mpz_add(newT, tempA, tempB)` wrote the T result into a freshly allocated `newT`. At the moment of allocation, `newP` (~443 MB), `newQ` (~443 MB), `tempA` (~443 MB), and `tempB` (~443 MB) were all live. Allocating a fresh `newT` (~443 MB) pushed peak RAM from ~1,772 MB to ~2,215 MB — enough to exhaust committed memory and trigger a GMP `abort()`.

**After:** the add writes in-place into `tempA`'s already-allocated limb buffer:

```vb
gmp_lib.mpz_add(tempA, tempA, tempB)
```

GMP §5.5 explicitly permits the destination to alias a source operand. `tempA` already has a ~443 MB buffer; the in-place add only needs to extend it by at most one limb (8 bytes) for a potential carry, rather than allocating a fresh ~443 MB block. The `newT` variable is removed entirely; `tempA` is used directly as the T component of the result node.

---

## Section 5 — ComputePiGMP Memory Optimisations (Final-Phase Arithmetic)

After `BinarySplitGMP` returns, the original code had these objects live when it reached the large `gmpNumer *= finalQ` multiplication:

| Variable | Size | Status |
|----------|------|--------|
| `finalP` | ~340 MB | P from binary split — not used in final formula |
| `finalQ` | ~548 MB | Q operand for the multiply |
| `finalT` | ~548 MB | T operand for the division after the multiply |
| `gmpSqrtInput` | ~396 MB | Already used; sqrt was done |
| `gmpSqrt` | ~198 MB | Value encoded in gmpNumer; no longer needed |
| `gmpOne` | ~198 MB | Used only for squaring; no longer needed |
| `gmpNumer` | ~198 MB | Being multiplied; result ~754 MB |

**Baseline before multiply: ~2,030 MB. Peak during multiply (result + FFT scratch): ~3,022 MB.**

Four changes were made:

### 5.1 gmpOne freed after gmpSqrtInput = gmpOne²

`gmpOne` is used for one squaring operation. After `mpz_mul(gmpSqrtInput, gmpOne, gmpOne)`, `gmpOne`'s value is no longer referenced. It is freed immediately with `mpz_clear`, then re-initialised to 0 with `mpz_init` so the `Finally` block can safely call `mpz_clear` on it. **Saving: ~198 MB** before all subsequent operations.

### 5.2 gmpSqrt and finalP freed before the large multiply

After `mpz_mul_ui(gmpNumer, gmpSqrt, 426880)`, `gmpSqrt`'s value is encoded in `gmpNumer` and is freed immediately. `finalP` is not used in the final formula (`pi = 426880·sqrt(10005)·Q/T`) and is also freed here. **Combined saving: ~538 MB** before the multiply.

### 5.3 finalT spilled to disk before the large multiply

`finalT` (~548 MB) is only needed for the division that follows the multiply. It is serialized to `DISK_CACHE_DIR\finalT_spill.bin` using `SerializeOneMpz`, then `mpz_clear`'d before the multiply. After the multiply completes and `finalQ` is freed, `finalT` is reloaded with `mpz_init` + `DeserializeOneMpz`. **Saving: ~548 MB** during the multiply's peak.

**Combined effect:** baseline before multiply drops from ~2,030 MB to ~750 MB.

### 5.4 gmpSqrtInput freed after sqrt

`gmpSqrtInput` is freed with `mpz_clear` immediately after `mpz_sqrt`. This was already present in the original code; no change needed.

---

## Section 6 — VirtualAlloc/VirtualFree Custom GMP Allocator

### Problem

Even after all the memory optimisations above, the large multiply crashed intermittently. The log showed the working-set baseline was well within the range that had previously succeeded. The paradox: Pass B (of a 2-way split attempt) failed at a *lower* working-set reading than Pass A had succeeded at.

### Root cause: committed memory vs working set

The Task Manager working-set figure shows pages currently backed by physical RAM. *Committed memory* is the larger figure: pages reserved such that the OS guarantees they can be accessed, backed by RAM or the page file. Windows enforces a system-wide commit limit (physical RAM + page file size).

GMP is linked against the static CRT inside `libgmp-10.dll`. The CRT's `malloc/free` manages an internal pool: it calls `VirtualAlloc` to grow the pool but returns freed blocks to an internal free-list *without* calling `VirtualFree`. Freed pages remain committed. Each large multiply+free cycle (Level 17 combine, sqrt, 3-pass multiply) allocated and freed hundreds of MB of GMP limb buffers. After each cycle the committed-but-free pages accumulated. The working-set looked low but the commit charge was at or near the system limit. When the next large allocation attempted to extend the CRT heap, `VirtualAlloc` failed, `malloc` returned `NULL`, and GMP called `abort()` — bypassing all .NET handlers.

### Fix: replace GMP's allocator with VirtualAlloc/VirtualFree for large blocks

GMP's `mp_set_memory_functions` API allows replacing the three allocator functions used for all internal allocations:

| Allocation size | Allocator used |
|-----------------|----------------|
| `>= 512 KB` | `VirtualAlloc(MEM_COMMIT\|MEM_RESERVE)` / `VirtualFree(MEM_RELEASE)` — immediately decommits pages; no free-list accumulation |
| `< 512 KB` | `_savedGmpAlloc` / `_savedGmpFree` (GMP's original CRT allocators, saved before installation) |

Small allocations must stay on GMP's own CRT heap. Routing them through `Marshal.AllocHGlobal` (the process default heap) would mix two different heap managers for the same `mpz_t` struct bodies, corrupting GMP's internal state and causing crashes in `BinarySplitChunk`.

The realloc handles all four size-crossing combinations (small→small, large→large, small→large, large→small).

### Direct P/Invoke to \_\_gmp\_set\_memory\_functions

Math.Gmp.Native's `mp_set_memory_functions` wrapper calls `__gmp_set_memory_functions` and then immediately re-reads the table via `_get_memory_functions`, updating an internal lambda that captures the function pointers. Under .NET 10, `Marshal.GetDelegateForFunctionPointer` on a managed thunk pointer returns the original delegate rather than creating a new wrapper, so the cast inside that lambda fails with `InvalidCastException`. A direct P/Invoke to `__gmp_set_memory_functions` in `libgmp-10.dll` is used instead, bypassing the wrapper's re-read entirely.

### Initialisation order

`InitGmpVirtualAllocFunctions()` is called in `Form1_Load` as the very first operation, before any `mpz_t` is created. Step 1 calls `gmp_lib.mp_get_memory_functions` to force the Math.Gmp.Native static initializer to run while the native table still points to the default CRT allocators — this populates `_savedGmpAlloc/Realloc/Free`. Step 2 installs the custom thunks via the direct P/Invoke.

### Delegate lifetime

All six delegate objects (`_gmpAlloc`, `_gmpRealloc`, `_gmpFree`, `_savedGmpAlloc`, `_savedGmpRealloc`, `_savedGmpFree`) are stored as `Shared` fields. If they were local variables the GC would collect them after the method returned, leaving GMP holding dangling function pointers.

### SerializeOneMpz fix required by the custom allocator

The original `SerializeOneMpz` called `mpz_export` with a `NULL` destination pointer, causing GMP to allocate the export buffer internally. After the custom allocator was installed, that allocation went through `VirtualAlloc` for large numbers. But the subsequent `gmp_lib.free` call invoked CRT `free` on a VirtualAlloc-backed pointer — a heap mismatch causing an immediate crash.

**Fix:** `SerializeOneMpz` now pre-allocates a buffer with `Marshal.AllocHGlobal` (sized from `mpz_sizeinbase`) and passes it to `mpz_export` directly. GMP writes into the caller's buffer and makes no allocation of its own. The `gmp_lib.free` call is eliminated entirely.

---

## Section 7 — Three-Way Split Multiply (gmpNumer \*= finalQ)

After the custom allocator eliminated the commit-charge problem, the single direct multiply `gmpNumer *= finalQ` still crashed because its peak working-set (result buffer + GMP's FFT scratch) exceeded available RAM in a single call.

The multiply is split into three passes by dividing `finalQ` into three equal thirds by bit position:

```
finalQ = Q2·2^(2k) + Q1·2^k + Q0     where k = bitlen(finalQ) / 3

Pass 0:  r0 = gmpNumer × Q0  → spill r0 to disk
Pass 1:  r1 = gmpNumer × Q1  → spill r1 to disk
Pass 2:  r2 = gmpNumer × Q2  (separate output variable — see below)

Combine: gmpNumer = ((r2 << k) + r1) << k + r0
```

`Q1` and `Q2` are spilled to disk after extraction and loaded back one at a time for their respective passes. This keeps each pass's peak well within available RAM.

### Pass 2 aliasing fix

The original Pass 2 implementation wrote in-place:

```vb
gmp_lib.mpz_mul(gmpNumer, gmpNumer, mpQ2)
```

This crashed. **Root cause:** Math.Gmp.Native declares `mpz_t` as a `Structure` (value type). Passing `gmpNumer` as both the destination and the first source argument passes *two separate struct copies* on the call stack — different stack addresses, same internal native pointer. GMP's aliasing check compares struct addresses: it sees `dst ≠ src1`, concludes there is no aliasing, and skips the internal temp-copy guard. The result is written into the same limb buffer that is still being read as source data, corrupting the multiplication. GMP detects the corruption via an internal assertion and calls `abort()`.

Pass 0 and Pass 1 never exhibited this because they used separate output variables (`mpR0`, `mpR1`) — genuinely different `mpz_t` values with different native pointers.

**Fix:** Pass 2 uses a separate output variable `mpR2`, then `mpz_swap`:

```vb
Dim mpR2 As New mpz_t()
gmp_lib.mpz_init(mpR2)
gmp_lib.mpz_mul(mpR2, gmpNumer, mpQ2)    ' dst ≠ src1 — no aliasing
gmp_lib.mpz_clear(mpQ2)
gmp_lib.mpz_swap(gmpNumer, mpR2)         ' O(1) pointer swap
gmp_lib.mpz_clear(mpR2)                  ' frees old ~208 MB gmpNumer buffer
```

`mpz_swap` exchanges the native pointers inside the two structs without moving any limb data. `mpz_clear(mpR2)` then frees the old `gmpNumer` buffer (~208 MB), reducing peak RAM during the combine shifts that follow.

---

## Section 8 — displayStr Memory Release

**Before:** `displayStr` was set in `StreamPiToScreen` and never cleared.

For a billion-digit computation the pi string is approximately 2 GB as a .NET UTF-16 `String`, allocated on the Large Object Heap. With `displayStr` holding a reference to it, the string could not be collected. If the user ran a second computation, both strings would be live simultaneously, requiring 4 GB of LOH address space.

**After:** once `DisplayTimer_Tick` completes streaming and the optional file write, `displayStr` is set to `Nothing`:

```vb
displayStr = Nothing
WriteToLog("[DisplayTimer] displayStr released (LOH block freed)")
```

This removes the GC root, allowing the string to be collected at the next Gen2 sweep.

---

## Section 9 — Summary of All Changed Locations

### ApplicationEvents.vb

| | |
|---|---|
| **Before** | `MyApplication` class body empty (comments only) |
| **After** | `MyApplication_UnhandledException` implemented; writes full exception chain to log file before showing dialog |

### Form1.vb — file header

| | |
|---|---|
| **Before** | No compile-time constants; no `System.Diagnostics` import |
| **After** | `#Const LOGGING_DETAIL = 1` with three-level description; `Imports System.Diagnostics` added |

### Form1.vb — class-level declarations (all new)

- `DiskNode` Structure (`FilePath`, `IsInMemory`, `MemP`, `MemQ`, `MemT`, `Level`, `Index`)
- `LOG_FILE` constant (`c:\PiOutput\pi_phase_log.txt`)
- `GMP_LARGE_THRESHOLD` constant (512 KB)
- `VirtualAlloc`, `VirtualFree`, `CopyMemory` P/Invoke declarations
- `GmpSetMemoryFunctionsNative` P/Invoke (direct to `__gmp_set_memory_functions`)
- `MEM_COMMIT_RESERVE`, `MEM_RELEASE`, `VA_PAGE_READWRITE` constants
- `_gmpAlloc`, `_gmpRealloc`, `_gmpFree` Shared fields (custom allocator delegates)
- `_savedGmpAlloc`, `_savedGmpRealloc`, `_savedGmpFree` Shared fields (GMP originals)
- `GmpAllocFunc`, `GmpReallocFunc`, `GmpFreeFunc` Shared methods
- `InitGmpVirtualAllocFunctions()` method
- `SetUnhandledExceptionFilter` P/Invoke declaration
- `NativeCrashFilterCallback` delegate type
- `_nativeCrashCallback` field (GC anchor for native crash filter)
- `HandleNativeCrash` method
- `WriteToLog` method
- `WriteExceptionToLog` method
- `OnAppDomainUnhandledException` method

### Form1.vb — Form1_Load

| | |
|---|---|
| **Before** | Dummy `mpz_t` init, `gmpC3Const` init, `MessageBox` |
| **After** | `InitGmpVirtualAllocFunctions()` called first; `AppDomain.UnhandledException` subscription; `SetUnhandledExceptionFilter` registration; output directory and `DISK_CACHE_DIR` creation; GMP DLL path detection |

### Form1.vb — LogPhase

| | |
|---|---|
| **Before** | Wrote directly to log file and `BeginInvoke`'d the UI update |
| **After** | Calls `WriteToLog` for the file write (gains timestamp/thread/RAM header), then `BeginInvoke`'s the UI update as before |

### Form1.vb — BtnCompute_Click

| | |
|---|---|
| **Before** | Single catch, no log write, log header had no mode info |
| **After** | Three catch blocks (OOM, Overflow, generic), each calls `WriteExceptionToLog`; log header includes `LOGGING_DETAIL` mode string |

### Form1.vb — BinarySplitChunk

| | |
|---|---|
| **Before** | Unsized `Stack` and `Dictionary`; no error logging; no `Try/Catch` |
| **After** | `Stack` pre-sized to `(maxDepth + 4)`; `Dictionary` pre-sized to `(b - a)`; `Try/Catch` logs failing `WorkItem` on exception; entry/exit `WriteToLog` calls (LOGGING_DETAIL = 2 only) |

### Form1.vb — BinarySplitGMP (complete rewrite)

| | |
|---|---|
| **Before** | `CHUNK_SIZE=1024`, `STOP_AT=4`; all chunks in three `mpz_t` arrays; no disk I/O; returns `List(Of Tuple(Of mpz_t, mpz_t, mpz_t))`; combine held all operands live; no `GC.Collect` between levels |
| **After** | `CHUNK_SIZE=512`, `STOP_AT=1`; streaming to disk via `DiskNode` list; `SerializeNodeToDisk` / `LoadNodeFromDisk` helpers (no LOH, no pinning); returns `ByRef nodes As List(Of Result)`; early-free + in-place `mpz_add` in combine loop; `GC.Collect` between levels (~17 total per billion-digit run); `isLastLevel` flag gates LOGGING_DETAIL=1 per-op logging; chunk progress every 100 (was 1,000) |

### Form1.vb — new serialization helpers

- **`SerializeNodeToDisk`** — 3 `mpz_t` parameters (not Tuple); 64 KB SOH staging buffer
- **`SerializeOneMpz`** — walks GMP native buffer in 64 KB chunks; uses `Marshal.AllocHGlobal` for export buffer (no GMP-internal allocation); staging buffer ≥ 512 KB uses `VirtualAlloc`/`VirtualFree` instead of `Marshal.AllocHGlobal` (§10.1)
- **`LoadNodeFromDisk`** — `ByRef` p/q/t output (not Tuple return); `Marshal.AllocHGlobal` for import buffer in `Finally` block (no pinned managed arrays)
- **`DeserializeOneMpz`** — reads in 64 KB chunks via staging buffer; staging buffer ≥ 512 KB uses `VirtualAlloc`/`VirtualFree` (§10.1)

### Form1.vb — ComputePiGMP

| | |
|---|---|
| **Before** | Debug `MessageBox` calls throughout; `nodes` as `List(Of Tuple)`; combine loop: `newT` allocated fresh, all 6 inputs kept alive; `gmpOne` not freed early; `gmpSqrt` and `finalP` not freed before multiply; `finalT` not spilled; single `gmpNumer *= finalQ` multiply; catch block: `MessageBox` only, no log write |
| **After** | Debug `MessageBox`es removed; `nodes` as `List(Of Result)`; combine loop: early-free + in-place `mpz_add` (§4); `gmpOne` freed after `gmpSqrtInput = gmpOne²` (§5.1); `gmpSqrt` + `finalP` freed before multiply (§5.2); `finalT` spilled to disk before multiply (§5.3); 3-way split multiply with Pass 2 aliasing fix (§7); combine section uses separate output vars + `mpz_swap` for all 4 operations (§10.2); catch block calls `WriteExceptionToLog`; all `[ComputePi]` `WriteToLog` calls gated on `LOGGING_DETAIL >= 1`; `mpz_get_str` wrapped with a per-second status ticker (§11) |

### Form1.vb — DisplayTimer_Tick

| | |
|---|---|
| **Before** | `displayStr` never cleared after streaming completed |
| **After** | `displayStr = Nothing` after streaming and optional file write complete |

---

## Section 10 — Run-Time Crash Fixes (1-Billion-Digit Testing)

These crashes were found while running large-scale computations. The 500-million-digit run completed successfully; crashes described here were encountered during 1-billion-digit testing (250 million was the earlier smaller successful run).

### 10.1 Marshal.AllocHGlobal heap retention in spill I/O

**Crash symptom:** Pass 2 multiply (gmpNumer × Q2) failed with a VirtualAlloc failure inside GmpReallocFunc. The committed-memory reading at the start of Pass 2 was only ~420 MB — well within what had worked earlier — yet the allocation still failed.

**Root cause:** `SerializeOneMpz` and `DeserializeOneMpz` staged their I/O through `Marshal.AllocHGlobal` buffers sized to the mpz export size (~391 MB each for R0 and R1 at 1 billion digits). `Marshal.FreeHGlobal` calls the Windows heap `HeapFree`. For large allocations the Windows heap manager returns the block to its internal free-list rather than calling `VirtualFree` — the pages remain committed. After the R0 spill serialize (391 MB) and R1 spill serialize (391 MB) + their corresponding deserializes, ~782 MB of formerly-freed pages were still committed even though the managed code had released the handles. When Pass 2's GMP FFT scratch tried to grow, the system commit limit was already effectively exhausted.

**Fix:** Both `SerializeOneMpz` and `DeserializeOneMpz` now use `VirtualAlloc`/`VirtualFree` for any staging buffer ≥ `GMP_LARGE_THRESHOLD` (512 KB), matching the same strategy used by the custom GMP allocator for limb buffers. `VirtualFree(MEM_RELEASE)` immediately decommits and releases the pages back to the OS; no free-list accumulation.

```vb
Dim useVA As Boolean = (capacity >= GMP_LARGE_THRESHOLD)
If useVA Then
    buf = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(capacity)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
    If buf = IntPtr.Zero Then Throw New OutOfMemoryException(...)
Else
    buf = Marshal.AllocHGlobal(New IntPtr(capacity))
End If
Try
    ' ... export/import ...
Finally
    If useVA Then VirtualFree(buf, UIntPtr.Zero, MEM_RELEASE)
    Else Marshal.FreeHGlobal(buf)
End If
```

### 10.2 Combine section struct-aliasing (same root cause as §7 Pass 2 fix)

**Crash symptom:** the process crashed after "Pass 2 multiply done; entering Combine" was logged. All four combine operations were using `gmpNumer` as both the destination and a source operand:

```vb
gmp_lib.mpz_mul_2exp(gmpNumer, gmpNumer, k1)   ' aliased — crash
gmp_lib.mpz_add(gmpNumer, gmpNumer, mpR1)       ' aliased — crash
gmp_lib.mpz_mul_2exp(gmpNumer, gmpNumer, k1)   ' aliased — crash
gmp_lib.mpz_add(gmpNumer, gmpNumer, mpR0)       ' aliased — crash
```

**Root cause:** identical to the Pass 2 aliasing issue (§7): `mpz_t` is a value type in GMP.NET, so passing the same variable as both `rop` and `op` gives GMP two struct copies at different stack addresses sharing the same internal `_mp_d` pointer. GMP's aliasing guard sees `dst ≠ src` (different addresses), skips the temp-copy path, calls `MPZ_REALLOC(rop, …)` which frees and replaces `rop._mp_d`, then reads from `op._mp_d` — which now points to freed memory — crashing with a heap corruption or access violation.

**Fix:** each combine step uses a separate output variable and `mpz_swap`, identical in structure to the Pass 2 fix:

```vb
' Step A: gmpNumer = r2 << k
Dim mpShiftA As New mpz_t()
gmp_lib.mpz_init2(mpShiftA, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))  ' see §10.4
gmp_lib.mpz_mul_2exp(mpShiftA, gmpNumer, k1)   ' dst ≠ src — no aliasing
gmp_lib.mpz_swap(gmpNumer, mpShiftA)
gmp_lib.mpz_clear(mpShiftA)

' Step B: gmpNumer += r1
Dim mpAddB As New mpz_t()
gmp_lib.mpz_init2(mpAddB, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))    ' see §10.4
gmp_lib.mpz_add(mpAddB, gmpNumer, mpR1)        ' dst ≠ src1,src2
gmp_lib.mpz_swap(gmpNumer, mpAddB)
gmp_lib.mpz_clear(mpAddB)
gmp_lib.mpz_clear(mpR1)

' Steps C and D follow the same pattern for the second shift and r0 add.
```

### 10.3 VirtualAlloc failure logging in GmpAllocFunc / GmpReallocFunc

**Motivation:** previous crashes produced no log entry explaining the failure — GMP called `abort()` immediately after receiving a `NULL` pointer back from the allocator. The log ended at the last `WriteToLog` call before the failing GMP operation, leaving the root cause ambiguous.

**Fix:** all three paths in `GmpReallocFunc` (large→large, small→large, large→small) and `GmpAllocFunc` now call `System.IO.File.AppendAllText(LOG_FILE, …)` directly (bypassing the instance-only `WriteToLog`) when `VirtualAlloc` returns `IntPtr.Zero`. The resulting log line appears immediately before GMP's NULL-dereference crash, pinpointing both the allocation size that failed and which realloc path was taken.

### 10.4 Crash in Combine A `mpz_mul_2exp` — force VirtualAlloc initial limb buffer

**Crash symptom:** the process crashed inside `mpz_mul_2exp(mpShiftA, gmpNumer, k1)` (Combine Step A) after logging `"Combine A: mpz_mul_2exp  k=1,459,414,540 bits"`. No `[GmpRealloc] FAILED` line appeared, ruling out a VirtualAlloc failure.

**Root cause hypothesis:** `mpz_init` allocates exactly 1 limb (8 bytes) for the new variable's limb buffer, using the saved GMP CRT allocator (`_savedGmpAlloc`). When `mpz_mul_2exp` later needs to grow `mpShiftA` from 8 bytes to ~563 MB, `GmpReallocFunc` takes the **small→large** realloc path: it calls `VirtualAlloc` for the new buffer, copies the data, then calls `_savedGmpFree(old_ptr, 8)` to free the original 8-byte CRT-heap block. If the CRT heap has been corrupted by a prior operation (or the CRT free triggers a heap-check assertion in debug mode), this call crashes inside `_savedGmpFree`.

**Fix:** replaced `mpz_init` with `mpz_init2(x, GMP_LARGE_THRESHOLD × 8 bits)` for all four combine output variables (`mpShiftA`, `mpAddB`, `mpShiftC`, `mpAddD`):

```vb
gmp_lib.mpz_init2(mpShiftA, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
```

`GMP_LARGE_THRESHOLD` is 512 KB. Requesting `512 KB × 8 = 4,194,304 bits` forces the initial limb buffer allocation (512 KB) through `GmpAllocFunc` → `VirtualAlloc` rather than the CRT heap. When `mpz_mul_2exp` subsequently grows the buffer to 563 MB, `GmpReallocFunc` takes the **large→large** path: `VirtualAlloc` new buffer + `VirtualFree` old buffer — no `_savedGmpFree` call, no CRT heap involvement at all.

**Additional diagnostics added in the same change:**

- `GmpReallocFunc` now logs a success line (`[GmpRealloc] large→large VirtualAlloc(N bytes) OK` / `small→large … OK`) for any allocation ≥ 500 MB, confirming the realloc completed before GMP begins the shift operation.
- Combine A pre-call log now includes `gmpNumer=N bits` so the log confirms `gmpNumer` is valid immediately before `mpz_mul_2exp`.

### 10.5 Combine A crash persists — add per-step GmpReallocFunc logging

**Crash symptom (second run):** Still crashes in `mpz_mul_2exp(mpShiftA, gmpNumer, k1)` after the §10.4 changes. The `[GmpRealloc] large→large ... OK` success log does **not** appear, ruling out a crash in the actual shift operation. The `[GmpRealloc] ... FAILED` line also does not appear, ruling out a VirtualAlloc failure. The crash therefore occurs between VirtualAlloc returning a valid pointer and the success log write — i.e., inside `CopyMemory` or `VirtualFree`.

**Diagnostic change:** the single post-VirtualFree success log was replaced by four per-step log lines (gated on new size ≥ 400 MB, covering the combine-phase reallocs):

1. `[GmpRealloc] L→L enter: new=N old=M` — function entered for large→large
2. `[GmpRealloc] L→L VA ok: newP=… copy=…` — VirtualAlloc succeeded
3. `[GmpRealloc] L→L copy done; about to VirtualFree oldP=…` — CopyMemory done
4. `[GmpRealloc] L→L VirtualFree done → OK` — VirtualFree done (= full success)

The last line that appears in the log tells us which call crashes. The same four-step pattern was added for the small→large path (`S→L`). A `"Combine A: mpz_mul_2exp returned OK"` log line was also added immediately after the `mpz_mul_2exp` call, so if GmpReallocFunc completes but the actual shift crashes, this log line will be absent while the `VirtualFree done` line will be present.

### 10.6 Second crash run — GmpReallocFunc never entered; add native struct dump

**Observation from second crash run (§10.5 diagnostics):** The `[GmpRealloc] L→L enter` line (the very first log in the large→large branch, written before `VirtualAlloc`) **never appeared** after the `mpz_mul_2exp` call. This rules out a crash inside `GmpReallocFunc`. GmpReallocFunc was never called at all.

**Possible causes:**

1. **MPZ_REALLOC macro short-circuits** — GMP's `MPZ_REALLOC(w, wsize)` macro expands to: `((wsize) <= (w)->_mp_alloc ? (w)->_mp_d : _mpz_realloc(w, wsize))`. If `_mp_alloc` in mpShiftA's native struct has been corrupted to a value ≥ 71,559,269 (the required limb count), the macro returns the existing 512 KB pointer without calling our realloc function. GMP then writes 546 MB into a 512 KB buffer → buffer overflow crash. Our `SetUnhandledExceptionFilter` may not fire if the overflow corrupts adjacent mapped memory rather than unmapped pages.

2. **GMP crashes before MPZ_REALLOC** — Some GMP internal state is corrupted such that `mpz_mul_2exp` faults or calls `abort()` before reaching `MPZ_REALLOC`. `abort()` bypasses `SetUnhandledExceptionFilter`.

**New diagnostics added:**

- `GmpAllocFunc` now logs every VirtualAlloc call in the 400 KB–2 MB range (`[GmpAlloc] VA: size=N → ptr=P`). These are the `mpz_init2` seed allocations. The log confirms: (a) GmpAllocFunc was reached, and (b) the exact size passed by GMP — which determines `_mp_alloc` in the native struct.

- Immediately after `mpz_init2(mpShiftA, ...)` in Combine A, the code reads the native `__mpz_struct` directly via `Marshal.ReadInt32/64` and logs all three fields: `_mp_alloc` (expected: 65537), `_mp_size` (expected: 0), `_mp_d` (expected: the pointer from GmpAllocFunc). If `_mp_alloc` reads as a large unexpected value, the MPZ_REALLOC short-circuit hypothesis is confirmed.

### 10.7 Combine A crash persists — bypass GmpReallocFunc via native struct pre-allocation

**Observation from fourth crash run (§10.6 diagnostics):** The native struct dump confirms `mpShiftA` was correctly initialized:

```
[GmpAlloc] VA: size=524,288 → ptr=1FE00000000
mpShiftA: ptr=21E7214EED0  _mp_alloc=65536  _mp_size=0  _mp_d=1FE00000000
mpz_mul_2exp  k=1,459,414,540 bits  gmpNumer=3,120,378,614 bits
```

`_mp_alloc = 65536` limbs = 512 KB. The required result size is `wsize = 48,755,916 + 22,803,352 + 1 = 71,559,269` limbs ≈ 546 MB. Since `71,559,269 > 65,536`, `MPZ_REALLOC` **must** call `_mpz_realloc`, which calls our `GmpReallocFunc`. Yet `[GmpRealloc] L→L enter` never appeared.

**Root cause (confirmed):** `GmpReallocFunc` is a managed delegate called directly from native GMP code. In .NET 10, any managed exception that escapes from a native callback terminates the process **immediately** — the CLR does not let the exception propagate to any managed handler, does not invoke `AppDomain.UnhandledException`, and does not invoke `SetUnhandledExceptionFilter`. The very first operation in `GmpReallocFunc`'s large→large branch is a string-interpolated `File.AppendAllText` call (the `[GmpRealloc] L→L enter` log). If this call throws (e.g. a string-formatting exception on an unexpected argument, a race on the file handle, or any internal CLR issue), the process dies before writing the log line.

**Fix:** pre-allocate the full result buffer before each GMP operation so `MPZ_REALLOC` short-circuits and `GmpReallocFunc` is never called. Immediately after each `mpz_init2` call in the combine section, the code:

1. Reads `_mp_size` from the source operand(s) via `Marshal.ReadInt32(ptr, 4)` to compute the exact limb count needed.
2. Calls `VirtualAlloc` for `(needed_limbs + 2) × 8` bytes.
3. Calls `VirtualFree` on the old `_mp_d` pointer (the 512 KB seed buffer from `mpz_init2`).
4. Writes the new pointer into `_mp_d` (`Marshal.WriteInt64(ptr, 8, …)`) and the new limb count into `_mp_alloc` (`Marshal.WriteInt32(ptr, 0, …)`).

After this, `MPZ_REALLOC(rop, wsize)` sees `wsize ≤ _mp_alloc`, returns the existing `_mp_d`, and writes the result into the pre-allocated buffer. `GmpReallocFunc` is never invoked.

**Applied to all four combine steps:**

| Step | Operation | Limb formula |
|------|-----------|-------------|
| A | `mpz_mul_2exp(mpShiftA, gmpNumer, k1)` | `abs(_mp_size(gmpNumer)) + (k1 / 64) + 2` |
| B | `mpz_add(mpAddB, gmpNumer, mpR1)` | `max(abs(_mp_size(gmpNumer)), abs(_mp_size(mpR1))) + 2` |
| C | `mpz_mul_2exp(mpShiftC, gmpNumer, k1)` | `abs(_mp_size(gmpNumer)) + (k1 / 64) + 2` (updated gmpNumer from step B) |
| D | `mpz_add(mpAddD, gmpNumer, mpR0)` | `max(abs(_mp_size(gmpNumer)), abs(_mp_size(mpR0))) + 2` |

The `+2` margin covers GMP's internal `wsize = usize + cnt_limbs + 1` formula for shifts, and carry propagation for adds.

### 10.8 Crash in `mpz_tdiv_q` — pre-allocate `gmpPi` quotient buffer

**Crash symptom:** the process crashed immediately after logging `"mpz_tdiv_q: pi = numer / T  (numer~1,817,982,666 digits  T~25,068,680 digits)"`. The combine steps A–D all completed successfully (§10.7 pre-allocation fix worked). Crash is now in the final division operation.

**Root cause:** `gmpPi` was initialised via `mpz_inits` (1-limb CRT heap buffer, 8 bytes). The division quotient is ~1.793 billion digits ≈ 93 million limbs ≈ 744 MB. `mpz_tdiv_q` calls `MPZ_REALLOC(gmpPi, 93M)`, which sees `_mp_alloc=1 < 93M` and calls `_mpz_realloc` → `GmpReallocFunc`. As established in §10.7, `GmpReallocFunc` crashes silently in .NET 10 when a managed exception escapes the native callback boundary.

**Fix:** same pre-allocation pattern as §10.7 applied to `gmpPi` before `mpz_tdiv_q`:

1. Read `_mp_size` from `gmpNumer` and `finalT` to compute `quotient_limbs = max(numer_limbs − denom_limbs + 1, 1) + 2`.
2. `VirtualAlloc` the full quotient buffer.
3. Read `_mp_alloc` from the existing `gmpPi` struct, then free the old CRT buffer via `_savedGmpFree(old_ptr, old_alloc × 8)` — **not** `VirtualFree`, because `mpz_inits` allocates via `_savedGmpAlloc` (CRT heap), not VirtualAlloc.
4. Write new pointer and limb count into `gmpPi`'s native struct.

The CRT-vs-VirtualAlloc distinction matters: the combine output variables were initialised via `mpz_init2` with a 512 KB seed, which routes through `GmpAllocFunc` → VirtualAlloc and must be freed with `VirtualFree`. The `gmpPi` variable was initialised via `mpz_inits` with a 1-limb seed, which routes through `_savedGmpAlloc` (CRT heap) and must be freed with `_savedGmpFree`.

---

## Section 11 — String Conversion Progress Ticker

**Problem:** `mpz_get_str(char_ptr.Zero, 10, gmpPi)` is a single opaque GMP call that blocks the compute thread (T5) for a very long time at 500 million digits or more. During this time the status label stays frozen at `"Division complete"`, giving no indication whether the thread is still working or has hung.

**Fix:** a `System.Threading.Timer` is started immediately before `mpz_get_str` and disposed in a `Finally` block immediately after it returns. The timer fires every second on a threadpool thread and calls `Me.BeginInvoke` to update `LblStatus` with the elapsed conversion time:

```
String conversion... 02:34 elapsed
```

The timer is independent of the compute thread — it fires as long as the UI message loop is running (i.e., the process is alive and not deadlocked on the UI thread). The existing running-time counter already confirms the process is alive; the ticker adds confirmation that the string conversion step specifically is in progress and shows how long it has been running.

The `Finally` block guarantees the timer is always disposed even if `mpz_get_str` throws, and `LogPhase("String conversion complete")` immediately overwrites the ticker message in `LblStatus` when the call returns.

---

## Section 12 — OutOfMemoryException in `piCharPtr.ToString()`

**Exception:** `System.OutOfMemoryException` at `char_ptr.ToString()` → `Marshal.PtrToStringAnsi(IntPtr)` → `String.CreateStringForSByteConstructor`. RAM was 2,518 MB at the point of failure.

**Root cause:** `mpz_get_str(char_ptr.Zero, 10, gmpPi)` allocates a native char buffer containing the complete decimal representation of the quotient. At 500 million target digits, the Chudnovsky binary-splitting intermediate result has ~1.793 billion decimal digits, so the native buffer is ~1.8 GB. `char_ptr.ToString()` calls `Marshal.PtrToStringAnsi(IntPtr)` **without a length**, which copies every character into a managed .NET `String`. A .NET `String` uses UTF-16 (2 bytes per char), so a 1.8-billion-char string requires ~3.6 GB of managed heap — causing OOM on top of the ~744 MB still held by `gmpPi` and the ~1.8 GB native char buffer.

**Fix (superseded by §13):** `Marshal.PtrToStringAnsi(piCharPtr.Pointer, digits + 1)` reduced the managed string to ~1 GB, but still crashed. See §13 for the final fix that eliminates the managed string entirely.

---

## Section 13 — Native Buffer Streaming (eliminate managed pi string)

**Problem:** even after limiting the managed string to `digits + 1` characters (§12), allocating ~1 GB on the managed heap on top of ~1.8 GB of live native memory still caused OOM.

**Root cause:** any approach that materialises the pi digits as a single managed `String` requires a contiguous ~1 GB+ LOH allocation alongside the native char buffer and residual GMP data. For large digit counts this is not feasible.

**Fix:** keep the native char buffer alive after `mpz_get_str` and stream bytes from it directly, with no managed string created at any point.

**`_displayNativePtr` / `_displayNativeLen` fields** are added to `Form1`. After `mpz_get_str` returns:
- `gmpPi` is freed (`mpz_clear` + `mpz_init` stub) to reclaim ~744 MB.
- `piCharPtr.Pointer` is stored in `_displayNativePtr`; `digits + 1` in `_displayNativeLen`.
- The native buffer is **not freed** — `DisplayTimer_Tick` now owns its lifetime.
- `ComputePiGMP` returns `""`.

**`DisplayTimer_Tick`** checks `_displayNativePtr <> IntPtr.Zero` to enter native mode:
- **Per-tick read:** `Marshal.ReadByte(_displayNativePtr, displayIdx)` one byte at a time, converted to `Char` via `Chr()`. On the first tick `displayIdx = 0`, the leading "3" is emitted followed by a literal "." to form the "3.14159…" representation — the native buffer contains no decimal point.
- **End of stream:** when `displayIdx >= _displayNativeLen`, the buffer is freed via `VirtualFree(_displayNativePtr, ...)` (it was VirtualAlloc'd by `GmpAllocFunc` since it is >512 KB) and `_displayNativePtr` is reset to `IntPtr.Zero`.
- **File write:** instead of `File.WriteAllText(outputFile, displayStr)`, a `FileStream` writes the native buffer in 1 MB chunks via `Marshal.Copy`, prepending "3." before the remaining digits.

Peak memory during streaming: ~1.8 GB (native char buffer only). No managed string is ever created. The `displayStr` field remains `Nothing` for the lifetime of the native stream.

---

## Section 14 — Exception Handling Consolidation

**Problem:** when running at 1 billion digits, two exception dialogs appeared but only one log entry was written. Execution also did not stop cleanly after the first dialog.

**Root cause — two dialog sources:**
1. `ComputePiGMP`'s own `Catch ex As Exception` block called `MessageBox.Show` directly from the background compute thread, then called `Me.BeginInvoke` to update the UI, then `Return ""`. This suppressed the exception — it never reached the outer handler in `BtnCompute_Click`. The `BeginInvoke` UI update and `Return ""` left the app in an indeterminate state.
2. `GmpReallocFunc` and `GmpFreeFunc` had no exception handling. An `OverflowException` from `CLng(size)` (for a corrupted or extremely large allocation size) propagated back through the P/Invoke boundary from the GMP callback, surfacing as an unhandled exception caught by `ApplicationEvents.vb`'s `MyApplication_UnhandledException` handler — a second dialog at the framework level, with its own log format.

**Fixes:**

### 14.1 GmpReallocFunc — Try/Catch wrapper
Wrapped the entire body in `Try … Catch ex As Exception`. On exception: logs `[GmpReallocFunc] EXCEPTION …` to the log file using `File.AppendAllText` (safe from a GMP callback context) and returns `New void_ptr(IntPtr.Zero)`. GMP will then abort, which is the correct behaviour — the native crash handler (§2.3) will record a log entry.

### 14.2 GmpFreeFunc — safe size conversion + Try/Catch wrapper
The original `CLng(size)` could throw `OverflowException` for a `size_t` value that exceeds `Long.MaxValue` (e.g. a corrupted pointer/size pair). Fixed with `CLng(CULng(size))` — `CULng` first converts the opaque `size_t` to `ULong` without sign-extension overflow, then `CLng` converts the `ULong` to `Long` (safe for all realistic allocation sizes). The entire body is also wrapped in `Try … Catch ex As Exception` so that any remaining exception is logged and the function returns without crashing, logging `[GmpFreeFunc] EXCEPTION …`.

### 14.3 ComputePiGMP catch — log + re-throw
The `Catch ex As Exception` in `ComputePiGMP` previously called `MessageBox.Show` and `Me.BeginInvoke` and then silently returned `""`. This was replaced with:
```vb
Catch ex As Exception
    WriteExceptionToLog("ComputePiGMP", ex)
    Throw
```
The `Throw` re-raises the exception to the outer `BtnCompute_Click` compute-thread handler, which is the **single** location that shows a dialog (`Me.Invoke` with `MessageBox.Show`) and restores the UI. The `Finally` block in `ComputePiGMP` still runs (GMP variable cleanup), so resources are released before the exception propagates.

**Result:** exactly one log entry and one dialog per exception, execution stops cleanly, and the UI is correctly reset regardless of which exception type was thrown.

---

## Section 15 — Corrupted `size_t` in GMP Allocator Callbacks

**Problem:** During the 1-billion-digit binary split at Level 16 (final top-level merge pass), `GmpAllocFunc` received an allocation request of `18446744073709036064` bytes (~18.4 EB). This is clearly a corrupted value — GMP's internal arithmetic produced an invalid size (likely from a corrupted `_mp_size` field in an `mpz_t` struct). The crash log showed:

```
[GmpAllocFunc] EXCEPTION (OverflowException): '18446744073709036064' is out of range of the Int64 data type. — returning null
```

**Root cause:** All three GMP memory callback functions (`GmpAllocFunc`, `GmpReallocFunc`, `GmpFreeFunc`) used `CLng(size_t)` to convert the native `size_t` value to a managed `Long`. `Math.Gmp.Native`'s `size_t.op_Explicit(size_t) As Long` throws `OverflowException` for any value exceeding `Long.MaxValue` (9.2 EB). The corrupted size exceeded this limit.

The fix applied in §14 (try/catch in `GmpFreeFunc`: `CLng(CULng(size))`) was also incorrect — `CULng(size)` correctly extracts the raw `ULong` value, but the subsequent `CLng(ULong)` throws again if the `ULong` is > `Long.MaxValue`.

**Fix:** In all three functions, replace `CLng(size_t)` with an explicit two-step conversion with a guard:

```vb
Dim rawSz As ULong = CULng(size)     ' safe: uses op_Explicit(size_t) As ULong, never throws
If rawSz > CULng(Long.MaxValue) Then
    ' Corrupted size — log and handle gracefully (return null / leak)
    System.IO.File.AppendAllText(LOG_FILE, $"[...] CORRUPT SIZE ({rawSz}) ...")
    Return ...
End If
Dim sz As Long = CLng(rawSz)         ' safe: rawSz <= Long.MaxValue guaranteed
```

**`GmpAllocFunc`:** logs `CORRUPT SIZE` and returns `void_ptr.Zero`. GMP receives null, calls `abort()`, and the native crash handler (§2.3) records the event. This is the correct behaviour — there is no way to satisfy a corrupt allocation request.

**`GmpReallocFunc`:** guards both `old_size` and `new_size`. Returns `void_ptr.Zero` if either is corrupt, with the old buffer leaked (the process is about to abort anyway).

**`GmpFreeFunc`:** if size is corrupt, the allocator (VirtualAlloc vs CRT) cannot be determined safely. The pointer is leaked and the corruption is logged. VirtualFree on a CRT pointer (or vice versa) could cause secondary heap corruption, so leaking is the safer choice when the size is untrustworthy.

**Why the size became corrupted** is not yet determined. One hypothesis: the struct-patching code that writes directly to `__mpz_struct._mp_alloc` (offset 0) via `Marshal.WriteInt32` may interact with GMP's internal reallocation decisions in a way that corrupts `_mp_size` (offset 4) under edge cases at 1-billion-digit scale. Further investigation requires a native debugger attached during the binary split phase.

**Root cause confirmed and fixed (§17):** GMP's 32-bit `mp_size_t` overflows when `pl × GMP_NUMB_BITS > 2^31`. The `SafeMpzMul` wrapper splits both operands into 3 pieces when `szA + szB ≥ 33,554,432`, keeping all 9 sub-products below the overflow threshold.

---

## Section 16 — Level-16 Crash Diagnostics: Operand Size Logging

**Problem:** `[GmpAllocFunc] CORRUPT SIZE (18446744073709036064)` occurs reproducibly at the same point — Level 16 (5→3 nodes), always after the same sequence of GmpAlloc VA lines — but no log entry identifies WHICH of the four `mpz_mul` calls in the combine loop triggers it, or what `_mp_size` the operands had at that point.

**Change:** Added an `isTopLevel` flag (`currentSize <= 16`, covering the top ~4 levels). Under `#If LOGGING_DETAIL >= 1`, before each `mpz_mul` and `mpz_add` in the combine loop, the `_mp_size` field of both operands is read directly from `__mpz_struct` offset 4 via `Marshal.ReadInt32` and written to the log:

```
[Combine] L16 N0: mul newP  leftP=27,523,104 rightP=27,523,104 limbs
[Combine] L16 N0: mul newQ  leftQ=... rightQ=... limbs
[Combine] L16 N0: mul tempA  leftT=... rightQ=... limbs
[Combine] L16 N0: mul tempB  leftP=... rightT=... limbs
[Combine] L16 N0: add newT  tempA=... tempB=... limbs
```

If any operand has a wildly unexpected `_mp_size` (e.g. negative, or orders of magnitude larger than expected), that indicates the corruption originates in the data loaded from disk rather than in GMP's allocation arithmetic. Expected limb counts at Level 16 for 1 billion digits: ~15–30 million limbs per P/Q/T value.

**Finding:** the inputs were not corrupted. `leftQ=33,873,440` and `rightQ=34,258,968` limbs — both reasonable. The crash occurred *inside* `mpz_mul(newQ, leftQ, rightQ)`. Root cause confirmed as the GMP 32-bit overflow described in §17.

---

## Section 17 — GMP 32-bit `mp_size_t` Overflow in FFT Multiplication

**Root cause:** GMP's MSVC build uses signed 32-bit `mp_size_t`. Inside `mpn_mul_fft`, the code computes:
```c
Kl = pl * GMP_NUMB_BITS / K;   /* where GMP_NUMB_BITS = 64 */
```
`pl = nl + ml` is the result limb count and the multiplication is done as 32-bit int arithmetic. When `pl ≥ 33,554,432` (= 2³¹/64), `pl * 64` **overflows int32**, producing a corrupted `Kl` value. This drives a corrupt FFT scratch-space size that is passed back to our allocator as a huge unsigned value (e.g. `18446744073709036064 = -64444 × 8` in two's complement).

At Level 16 of the 1-billion-digit binary split, `leftQ + rightQ = 68,132,408` limbs — more than double the safe threshold. The P multiplication (`leftP + rightP = 43,432,983` limbs) appears to succeed because GMP selects a different algorithm (likely Toom-Cook rather than FFT) at that operand size.

**Fix — `SafeMpzMul` wrapper:**

```vb
Private Shared Sub SafeMpzMul(result As mpz_t, opA As mpz_t, opB As mpz_t)
    Const SAFE_LIMB_THRESHOLD As Integer = 33_554_431
    Dim szA = Abs(ReadInt32(opA.Pointer, 4))
    Dim szB = Abs(ReadInt32(opB.Pointer, 4))
    If szA + szB <= SAFE_LIMB_THRESHOLD Then
        gmp_lib.mpz_mul(result, opA, opB) : Return
    End If
    ' 3-way split: A = A0 + A1*2^bitsA + A2*2^(2*bitsA), same for B.
    ' 9 sub-products; each has at most ceil(szA/3)+ceil(szB/3) limbs ≈ 22.7M < 33.5M → safe.
    ' Assemble via mpz_mul_2exp + mpz_add (both O(n), no FFT).
End Sub
```

Each of the four `mpz_mul` calls in the binary-split combine loop (`newP`, `newQ`, `tempA`, `tempB`) is replaced with `SafeMpzMul`. The wrapper is a no-op for operand pairs below the threshold — no overhead for smaller levels.

**Why the 3-way split:** a 2-way split of both operands gives sub-products of `≈ szA/2 + szB/2 = (szA+szB)/2 ≈ 34M` limbs, which still exceeds the threshold. A 3-way split gives `≈ (szA+szB)/3 × 2 ≈ 22.7M` limbs per sub-product, safely below 33.5M.

---

## Section 18 — SafeMpzMul: Struct-Aliasing Crash in `mpz_add` Accumulation

**Crash symptom:** the process crashed silently at Level 17 Node 0 during `SafeMpzMul(newQ, leftQ, rightQ)` (leftQ = 64,986,678 limbs, rightQ = 3,423,380 limbs). The log showed the GmpReallocFunc `S→L` entry for `absA` completing successfully, the two `L→L` reallocs for `shifted` completing successfully, and a repeating series of small FFT-scratch allocs — then abrupt termination with no error log and no native-crash-handler entry.

**Root cause:** same class as §7 and §10.2. Inside `SafeMpzMul`, the accumulation loop calls:

```vb
gmp_lib.mpz_add(result, result, prod)     ' also: mpz_add(result, result, shifted)
```

`result` is passed as both `rop` and `op1`. GMP.NET passes `mpz_t` by value, so GMP receives **two separate stack copies** at different addresses but with the same internal `_mp_d` pointer. GMP's aliasing guard compares struct addresses (`&rop ≠ &op1`), sees no alias, and skips the temp-copy path.

When `result`'s buffer needs to grow, `GmpReallocFunc` allocates a new block and calls `VirtualFree` on the old one. The new pointer is stored into the `rop` stack copy's `_mp_d`. The `op1` stack copy still holds the **old freed pointer**. `mpn_add` then reads from the freed VirtualAlloc region → `STATUS_ACCESS_VIOLATION`. The CLR's internal SEH handler calls `TerminateProcess` directly, bypassing both `AppDomain.UnhandledException` and `SetUnhandledExceptionFilter` — no log entry, no dialog.

The crash appeared late (after several successful sub-products and two logged `L→L` reallocs) because the freed pages happened to still be physically backed immediately after release. The process silently accumulated corrupt state until the freed pages were reclaimed, at which point the fault became hard.

**Fix:** pre-allocate `result`'s limb buffer to `szA + szB + 2` limbs via `VirtualAlloc` before the accumulation loop, patching the native `__mpz_struct` directly (same technique as §10.7 and §10.8). With `result._mp_alloc ≥ wsize` for all nine accumulation steps, `MPZ_REALLOC` always short-circuits and `GmpReallocFunc` is never called during the loop. The aliasing issue cannot fire.

**Additional memory optimisations in the same change:**

- **Eliminated `absA`/`absB` copies:** P and Q values in Chudnovsky splitting are always non-negative. `opA` and `opB` are used directly as the split sources instead of making 495 MB + 26 MB abs-copies. Saves ≈ 521 MB peak during the split phase.

- **Free `A_i` after its row:** After all `j` iterations for a given `i`, `A_i` is freed immediately. At `i = 1` saves ≈ 165 MB; at `i = 2` saves ≈ 330 MB.
