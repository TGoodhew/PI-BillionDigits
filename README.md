# PI-BillionDigits

## What it is

PI-BillionDigits is a Windows Forms application that computes Pi to an arbitrary number of decimal digits — up to and including one billion — and displays the result. It is written in VB.NET targeting .NET 10 and uses the [Math.Gmp.Native](https://www.nuget.org/packages/Math.Gmp.Native.NET/) wrapper around the GNU Multiple Precision Arithmetic Library (GMP) for all big-integer arithmetic.

## How it works

The computation uses the **Chudnovsky algorithm** with **binary splitting**, which is the standard approach for computing billions of digits of Pi efficiently.

**Chudnovsky series:** The algorithm sums a rapidly-converging series discovered by the Chudnovsky brothers. Each term adds roughly 14.18 decimal digits of Pi precision, so about 70.5 million terms are needed for one billion digits.

**Binary splitting:** Rather than computing each term individually, binary splitting recursively divides the range of terms in half, computes three integer partial products (P, Q, T) for each half, then combines pairs of halves by multiplying across. This transforms the computation into a binary tree of big-integer multiplications. The leaves (individual terms) have small integers; the root holds exact rational numerator and denominator integers that are hundreds of millions of digits long. Final division and square root at the root give Pi.

**Disk-based tree:** For large digit counts the intermediate P, Q, T values at each tree level become gigabytes in size and cannot all fit in RAM simultaneously. The app serializes nodes to disk (`C:\PiOutput\NodeCache\`) and loads them one pair at a time during the combine (bottom-up merge) phase. A single final in-memory combine produces the root values.

**GMP for arithmetic:** All big-integer arithmetic (multiply, divide, square root, base-10 conversion) is delegated to GMP's native C library via P/Invoke, which uses asymptotically fast algorithms (Toom-Cook, Schönhage-Strassen FFT) for the enormous integers involved.

**Output:** After the root rational is computed, GMP converts the result to a decimal string which is streamed character-by-character into the display via a timer tick, keeping the UI responsive during the transfer.

## UI controls

| Control | What it does |
|---------|-------------|
| **Digits of Pi** text box | Number of decimal digits to compute. Accepts values like `1,000,000` or `1000000000`. Auto-formatted with commas as you type. Default: 1,000,000. |
| **Start** button | Begins the computation on a high-priority background thread (256 MB stack). Disabled while a run is in progress. |
| **Pause** button | Cancels the current run via a cancellation token. If "Write to File" is checked, the digits computed so far are saved before stopping. |
| **Display** checkbox | When checked, the computed digits are streamed into the output panel after computation completes. Unchecking this is useful when only the file output matters, since displaying a billion digits takes significant time. |
| **Write to File** checkbox | When checked, the full digit string is saved to `C:\PiOutput\pi_digits.txt` after computation. |
| **Chunk Size** text box | Number of characters pushed into the display per timer tick during streaming. Higher values stream faster but may make the UI less responsive. Default: 500. Range: 1–1,000,000. |
| **Test** button | Searches the computed digits for three known substrings and reports whether they appear at the correct positions: `999999` (expected at position 762, the Feynman point), `777777777` (expected at position 24,658,601), and `27182818284` (first digits of e). Searches the full native buffer when available, otherwise searches the display text box. |
| **Status** bar | Shows the current phase (e.g., "Streaming 1,000,000,000 digits...") or any error message. |
| **Running Time** label | Elapsed wall-clock time since Start was clicked, updated every second. |
| **Digits Displayed** label | Running count of digits streamed to the output panel so far. |
| **Phase log** list box | Timestamped log of major computation phases (chunk processing, combine levels, string conversion, streaming). Each entry shows elapsed time since Start. |
| **Output panel** | Black-background, green-text RichTextBox showing the Pi digits as they are streamed in. Displays `3.` followed by the decimal digits. |

---

## Cumulative Summary of Changes

A high-level overview of everything that was changed from the original implementation to reach a working 1-billion-digit computation. The detailed Change Log below documents each individual change and its root cause.

### Architecture

**Disk-based binary split (§3):** The original code held all ~137,000 chunk P/Q/T values in RAM simultaneously — feasible for small digit counts but tens of GB at 1 billion digits. The rewrite streams each chunk to disk immediately after computation and loads one pair at a time during the combine phase. Only the final combine pair is held in memory at once.

**Three-pass multiply (§7, §46, §47):** The final `gmpNumer *= finalQ` multiplication (~1.1 GB × ~1.1 GB) peaks at ~2.3 GB, exceeding available headroom after the other live buffers. `finalQ` is split into three equal bit-thirds (Q0, Q1, Q2) and multiplied separately; the three partial products are shifted and summed to reconstruct the full result. Peak per-pass is ~1.2 GB.

**`SafeMpzMul` (§17–§45):** GMP's internal FFT uses a 32-bit `mp_size_t` (signed `int` on Windows MSVC). For operands above ~67 million limbs (≈ 536 MB each) the FFT's internal size arithmetic overflows, producing garbage or crashing. `SafeMpzMul` is a schoolbook 3×3 split: each operand is divided into three equal thirds by bit position and the nine sub-products are computed separately with GMP's fast routines, which never see an operand large enough to trigger the overflow. Recursive: sub-products that still exceed the threshold recurse.

---

### Memory management

**Custom VirtualAlloc/VirtualFree allocator (§6):** GMP's internal CRT `malloc/free` retains freed pages in a free-list instead of releasing them to the OS. After repeated large allocate/free cycles (Level 17 combine, sqrt, three-pass multiply) the committed-but-idle pages accumulated, hitting the system commit limit (`RAM + page file`). The next large allocation then failed with `NULL` from `malloc` and GMP called `abort()`. Fix: GMP's `mp_set_memory_functions` API replaces the three allocator callbacks. Allocations ≥ 512 KB use `VirtualAlloc(MEM_COMMIT|MEM_RESERVE)` / `VirtualFree(MEM_RELEASE)`, which immediately returns pages to the OS on free. Smaller allocations stay on GMP's own CRT heap.

**Pre-allocation pattern (§19, §21, §23, §37–§38, §47):** When GMP allocates a fresh result buffer via `GmpReallocFunc` (S→L transition), it calls `VirtualAlloc` and then immediately writes into the new pages. The page faults for that write cannot always be serviced in time, causing a silent access violation inside native GMP — a CLR FailFast with no managed handler reachable. The fix used throughout the code is to manually pre-allocate the result buffer with `VirtualAlloc` before the GMP call, then write the pointer and alloc count directly into the native `__mpz_struct`. `MPZ_REALLOC` then short-circuits (result already large enough) and `GmpReallocFunc` is never called.

**`SerializeOneMpz` / `DeserializeOneMpz` (§6, §30):** The original serializer called `mpz_export` with a NULL destination, asking GMP to allocate the export buffer. After the custom allocator was installed that buffer came from `VirtualAlloc`, but the subsequent `gmp_lib.free` used CRT `free` — a heap mismatch crash. Fixed by pre-allocating with `Marshal.AllocHGlobal` and passing it directly. `mpz_export` was also replaced entirely for numbers above 67 million limbs because `mp_size_t` overflow in GMP's export code returns wrong sizes; the serializer now reads raw limb data directly from the native struct via `Marshal.ReadInt64`.

**Spill-and-reload pattern (§5):** Large intermediate values (`finalP`, `finalT`, `gmpSqrt`) are freed or serialised to disk before the peak-memory operations and reloaded afterwards. This keeps peak live memory around ~1.6 GB for the three-pass multiply rather than ~3+ GB.

---

### `SafeMpzMul` internals

`SafeMpzMul` went through approximately 25 iterative fixes (§17–§45) to handle increasingly subtle memory and interop issues. The key ones:

- **Accumulator isolation (§40):** GMP's FFT stack writes beyond its stack frame, corrupting the outer managed call frame's local variables (including `result.Pointer`). A separate `accum` accumulator object, never passed to inner GMP calls, is immune to this corruption.
- **Raw P/Invoke bypass (§42):** Math.Gmp.Native's managed wrapper reassigns `mpz_t.Pointer` on every P/Invoke call in a way that does not persist for local value-type objects. All inner GMP calls inside `SafeMpzMul` are made via raw `DllImport` helpers operating on saved `IntPtr` values directly, bypassing the wrapper.
- **§44 stash:** `accumPtr` (the 16-byte header for the accumulator) is stored at `result.Pointer + 8` (the `_mp_d` slot of the blanked result struct). This slot survives native GMP stack-frame corruption because inner calls only write to `prod`'s struct. After all 9 sub-products, `accumPtr` is recovered from the stash and its fields are copied back to `result`.
- **A-piece direct extraction (§35):** A-pieces 1 and 2 are extracted by copying limbs directly from `opA`'s limb array into the pre-allocated `A_part` buffer, avoiding a 710 MB temporary per A-piece.
- **`mpz_init2` for A_part (§45):** When `mpz_tdiv_r_2exp` produces zero (all low limbs of Q are zero due to accumulated factors of 2), GMP skips `MPZ_REALLOC` and leaves `_mp_alloc = 1`. The subsequent `CopyMemory` of 57+ MB into a 1-limb buffer was a silent heap overflow. Fixed by using `mpz_init2(A_part, bitsA)` to pre-allocate the correct number of limbs before the tdiv call.

---

### Platform / interop fixes

**`mp_bitcnt_t` 32-bit limit (§22, §46):** On Windows, GMP is compiled with MSVC where `unsigned long` is 32 bits. `mp_bitcnt_t` (used for bit-shift counts) therefore caps at 4,294,967,295. At 1-billion-digit scale several shift counts exceed this. Fixed by splitting operations that would require a shift > 4.29 billion bits into two sequential shifts each within the 32-bit range.

**`mp_size_t` 32-bit overflow (§17):** GMP's FFT code computes intermediate sizes as `mp_size_t` (32-bit signed int on Windows). Numbers above ~67 million limbs cause overflow in that arithmetic, producing wrong results or crashes. `SafeMpzMul` ensures GMP never sees an operand that large.

**`Chr()` encoding (§24):** VB.NET's `Chr()` uses Windows-1252 code page encoding, unavailable in .NET Core. Replaced with `ChrW()` (Unicode) throughout.

**Delegate lifetime (§2, §6):** All delegate objects passed to native APIs (`SetUnhandledExceptionFilter`, `mp_set_memory_functions`) are stored as `Shared` fields. Local-variable delegates are collected by the GC, leaving dangling function pointers.

---

### Observability

- Structured log file with timestamp, thread ID, elapsed time, and RAM per entry; synchronous flush on every write guarantees the last entry before a crash is on disk (§2).
- `SetUnhandledExceptionFilter` native crash handler catches GMP `abort()` and writes a marker before the process exits (§2).
- `LOGGING_DETAIL` compile-time constant: 0 = phases only, 1 = detail on final combine + all ComputePiGMP steps (default), 2 = full trace (§2).
- Native buffer streaming: the billion-digit result is kept as a native `char*` rather than a managed string, avoiding a 1 GB managed allocation and GC pressure during display (§13).
- Thread priority `AboveNormal` + `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` to prevent Windows from throttling the compute thread (§27).
- Headless / automation mode (§63): `--digits N --autostart --autoverify` runs end-to-end without any UI dialogs; suppressed dialogs written to the phase log with a `[DIALOG]` prefix.
- Custom digit verification (§67): `--verify-at "DIGITS:POSITION"` and `--verify-contains "DIGITS"` CLI options for automated correctness checks.

### P-Core Affinity on Hybrid CPUs

Intel 12th-gen+ (Alder Lake, Raptor Lake) and AMD Zen 4c CPUs expose two classes of cores: **P-cores** (full-power, high IPC, preferred for GMP math) and **E-cores** (lower power, lower single-thread performance, shared L2). Without affinity pinning, the Windows thread pool schedules tasks onto whichever logical processors are available — including E-cores — which can unpredictably slow down bandwidth-bound GMP operations.

**How it works (§66):**

The Win32 API `GetLogicalProcessorInformationEx(RelationProcessorCore, ...)` returns one `SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX` record per physical core. The `EfficiencyClass` byte in each record tells you whether the core is a P-core (`EfficiencyClass > 0`) or an E-core (`EfficiencyClass = 0`). Accumulate a bitmask for each class, then call `SetProcessAffinityMask` if both classes are present.

**P/Invoke declarations:**
```vb
<DllImport("kernel32.dll", SetLastError:=True)>
Private Shared Function SetProcessAffinityMask(
    hProcess As IntPtr,
    dwProcessAffinityMask As IntPtr) As Boolean
End Function

<DllImport("kernel32.dll", SetLastError:=True)>
Private Shared Function GetLogicalProcessorInformationEx(
    relationshipType As Integer,
    buffer As IntPtr,
    ByRef returnedLength As UInteger) As Boolean
End Function

Private Const RelationProcessorCore As Integer = 0
```

**Detection and pinning:**
```vb
Private Shared Sub SetPCoreAffinity()
    Try
        ' Step 1: two-call pattern — first call returns required buffer size
        Dim bufferSize As UInteger = 0
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, bufferSize)
        If bufferSize = 0 Then Return

        Dim buffer As IntPtr = Marshal.AllocHGlobal(CInt(bufferSize))
        Try
            If Not GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, bufferSize) Then Return

            ' Step 2: parse variable-length records.
            ' SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX layout (RelationProcessorCore):
            '   +0   Relationship   : DWORD  (4 bytes)
            '   +4   Size           : DWORD  (4 bytes) — record length, varies
            '   +8   Flags          : BYTE   (1 byte)
            '   +9   EfficiencyClass: BYTE   (1 byte)  — 0=E-core, >0=P-core
            '  +10   Reserved[20]   : BYTE  (20 bytes)
            '  +30   GroupCount     : WORD   (2 bytes)
            '  +32   GroupMask[0].Mask : ULONG_PTR (8 bytes on x64) — logical processor bitmask
            Dim pCoreMask As Long = 0L
            Dim eCoreMask As Long = 0L
            Dim offset As Integer = 0

            Do While offset < CInt(bufferSize)
                Dim recordSize As Integer = Marshal.ReadInt32(buffer, offset + 4)
                If recordSize <= 0 Then Exit Do

                Dim efficiencyClass As Byte = Marshal.ReadByte(buffer, offset + 9)
                Dim mask As Long = Marshal.ReadInt64(buffer, offset + 32)

                If efficiencyClass > 0 Then
                    pCoreMask = pCoreMask Or mask   ' P-core logical processors
                Else
                    eCoreMask = eCoreMask Or mask   ' E-core logical processors
                End If

                offset += recordSize  ' advance to next record
            Loop

            ' Step 3: only pin if this is actually a hybrid CPU
            If pCoreMask <> 0L AndAlso eCoreMask <> 0L Then
                SetProcessAffinityMask(GetCurrentProcess(), New IntPtr(pCoreMask))
                ' Log: $"Hybrid CPU. P-core mask=0x{pCoreMask:X}  E-core mask=0x{eCoreMask:X}"
            Else
                ' Uniform CPU — all cores same class, leave affinity unchanged
            End If
        Finally
            Marshal.FreeHGlobal(buffer)
        End Try
    Catch ex As Exception
        ' Log and continue — affinity is an optimisation, not a correctness requirement
    End Try
End Sub
```

**Call site — invoke once before the first `Parallel.For`:**
```vb
SetPCoreAffinity()
ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount)
```

**Key points:**
- The two-call pattern (size query with `IntPtr.Zero`, then data query) is required — the buffer size varies with the number of cores.
- `Size` at offset +4 is the actual record length and must be used to advance the offset; do not assume a fixed struct size.
- On a non-hybrid machine all records have the same `EfficiencyClass`, so `eCoreMask` stays 0 and the affinity mask is left unchanged — the function is safe to call unconditionally.
- `GetCurrentProcess()` returns a pseudo-handle that is always valid; no `CloseHandle` required.
- The affinity mask is inherited by all threads including thread pool workers, so one call from the UI/compute thread is sufficient.

### Automation

**Headless mode (§63):** All three `MessageBox.Show` dialogs are gated behind `If Not _headless Then`. In headless mode the text is written to the phase log with a `[DIALOG]` prefix so automated runs leave a full audit trail without blocking.

**`Run-PiCompute.ps1` (§63, §70):** PowerShell script that clean-builds and launches the exe. Machine-independent: the exe path is auto-detected by globbing `bin\Release\**\PI-BillionDigits.exe` after the build (no hardcoded TFM folder), and the output directory defaults to `.\PiOutput` next to the script (overridable via `-OutputDir`). Parameters: `-Digits N` (default 1B), `-OutputDir <path>`, `-Trace`, `-ReportOnly <path>`.

**Quick start:**
```powershell
# Standard run
.\Run-PiCompute.ps1

# Custom digit count and output location
.\Run-PiCompute.ps1 -Digits 100000000 -OutputDir "D:\PiResults"

# With CPU trace
.\Run-PiCompute.ps1 -Trace

# Re-generate report from existing trace
.\Run-PiCompute.ps1 -ReportOnly ".\pi_trace_20260331_121017.nettrace"
```

**P-core affinity + thread pool pre-warm (§66):** On hybrid CPUs (Intel P+E core), `GetLogicalProcessorInformationEx` is used to detect P-cores by `EfficiencyClass` and restrict the process affinity mask to those cores only. `ThreadPool.SetMinThreads(ProcessorCount, ProcessorCount)` pre-warms the thread pool before Phase 1 to eliminate first-task latency.

---

## Change Log

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

- **Pre-allocation logs and OOM guard:** `WriteToLog` records the pre-allocation result (`[SafeMpzMul] result pre-alloc OK/FAILED`). If `VirtualAlloc` fails, an `OutOfMemoryException` is thrown immediately rather than silently falling through to the aliasing-unsafe loop.

- **`A_i` early-freeing removed (was §18 rev 1):** An earlier revision freed each `A_parts(i)` piece after its row via `mpz_clears(A_parts(i)) + mpz_inits(A_parts(i))`. This was unsafe: `A_parts(i)` is a struct copy — `mpz_clears` frees `_mp_d` through the copy's `Pointer` field, but `_mp_d` is left as a dangling (non-NULL) pointer in the native struct. If `mpz_inits` on the copy then allocates a *new* native struct and updates the copy's `Pointer`, the original named variable (`A0`/`A1`/`A2`) still holds the old `Pointer` with the dangling `_mp_d`. The final `mpz_clears(A0, A1, A2)` would call `GmpFreeFunc` on that dangling pointer → heap corruption. The optimization was removed; `A0`/`A1`/`A2` are freed together at the end of the function.

---

## Section 19 — SafeMpzMul: `shifted` Buffer Realloc Crash in `mpz_mul_2exp`

**Crash symptom:** after the §18 fix, the process crashed silently at Level 16 Node 1 during `SafeMpzMul(newQ, leftQ, rightQ)` (leftQ = 33,873,440 limbs, rightQ = 34,258,968 limbs). Per-step diagnostic logging added to the accumulation loop identified the failure point exactly: `i=2 j=2: before mpz_mul_2exp shift=2906982784` — the log entry was written but no "after mpz_mul_2exp" entry followed. All eight earlier sub-products (i=0..2, j=0..1 and i=0..1, j=2) completed successfully.

**Root cause:** `mpz_mul_2exp(shifted, prod, 2,906,982,784)` at the final sub-product (i=2, j=2) needed to grow `shifted`'s buffer from ~455 MB (56,841,263 limbs, last grown at i=1,j=2) to ~545 MB (68,132,414 limbs). This triggered `GmpReallocFunc` (L→L path). The new 545 MB buffer was allocated and the old buffer freed — but the old buffer's address was still live as the source pointer inside the GMP `mpz_mul_2exp` call. Reading from the freed `VirtualAlloc` region → `STATUS_ACCESS_VIOLATION`. The CLR terminates immediately, explaining why no GmpRealloc log entry appears.

**Fix:** pre-allocate `shifted`'s limb buffer to `szA + szB + 2` limbs via `VirtualAlloc` immediately after `mpz_inits(prod, shifted, Nothing)`, patching the native `__mpz_struct` directly (same technique as §18 for `result`). With `shifted._mp_alloc` large enough to hold any sub-product at any shift, `MPZ_REALLOC` always short-circuits and `GmpReallocFunc` is never called for `shifted` during the loop.

The bound `szA + szB + 2` is tight: the worst case is i=2,j=2 with shift = 2·bitsA + 2·bitsB bits, which gives a result of at most ⌈(szA/3 + szB/3)⌉ + ⌈(2·mA + 2·mB)⌉ + 1 ≈ szA + szB + 2 limbs — the same upper bound already used for `result`.

---

## Section 20 — SafeMpzMul Recursion and Post-Combine Large Multiplications

**Crash symptom:** after the §18/§19 combine-phase fixes, the process crashed in `ComputePiGMP` at the first post-combine multiplication: `gmpSqrtInput = gmpOne^2`. The log showed `[ComputePi] mpz_mul: gmpSqrtInput = gmpOne^2` followed immediately by repeating FFT scratch allocs and then `[GmpAllocFunc] CORRUPT SIZE (18446744073709315104)` — the same corrupt-size symptom as §17, but now inside the post-combine finalisation code.

**Root cause — two issues:**

1. **Direct `gmp_lib.mpz_mul` calls in post-combine code.** `gmpOne = 10^1,000,000,000` has ≈52 M limbs. `gmpOne × gmpOne` has szA + szB ≈ 104 M >> 33,554,431 threshold and was called via `gmp_lib.mpz_mul` directly (not through `SafeMpzMul`). Similarly, the three-pass numerator multiplications (`gmpNumer × Q0/Q1/Q2` where gmpNumer ≈ 26 M limbs and each Q third ≈ 21 M limbs, sum ≈ 47 M) used direct `gmp_lib.mpz_mul`.

2. **SafeMpzMul was not recursive — inner products also exceeded the threshold.** For `gmpOne × gmpOne` (52 M + 52 M), SafeMpzMul splits each operand into three pieces of ≈17.3 M limbs. The inner products (17.3 M + 17.3 M = 34.6 M) still exceeded the 33,554,431 threshold. The inner loop called `gmp_lib.mpz_mul` directly on these pieces, which would crash the same way.

3. **Pre-allocation free logic did not handle large existing buffers.** The result pre-alloc code always called `_savedGmpFree` to free the old buffer. On a recursive call (inner-loop iteration 2+), `prod` already holds a large `VirtualAlloc` buffer from the previous iteration; `_savedGmpFree` (CRT free) on a `VirtualAlloc` buffer corrupts the heap.

**Fixes:**

- `SafeMpzMul` inner loop changed from `gmp_lib.mpz_mul(prod, A_parts(i), B_parts(j))` to `SafeMpzMul(prod, A_parts(i), B_parts(j))`. This makes SafeMpzMul recursive: for the `gmpOne^2` case the pieces (17.3 M each) also exceed the threshold and are handled by a second level of SafeMpzMul; each second-level piece (17.3 M/3 ≈ 5.77 M, product 11.5 M) is safely below the threshold.

- Result and shifted pre-alloc free logic updated to dispatch on size: if `_oldAlloc × 8 ≥ GMP_LARGE_THRESHOLD` use `VirtualFree`; otherwise use `_savedGmpFree`. This ensures correct deallocation whether the buffer was created by the CRT (first call, from `mpz_inits`) or by a prior `SafeMpzMul` pre-allocation.

- `gmp_lib.mpz_mul(gmpSqrtInput, gmpOne, gmpOne)` changed to `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)`.

- Three-pass numerator multiplications `gmp_lib.mpz_mul(mpR0/mpR1/mpR2, gmpNumer, Q0/Q1/Q2)` changed to `SafeMpzMul`. The 26 M + 21 M = 47 M pieces split to 8.7 M + 7 M = 15.7 M < threshold, so one level of SafeMpzMul suffices with no recursion.

---

## Section 21 — Combine-Step Pre-Alloc Guard: Small VirtualAlloc Buffers Freed via CRT

**Crash symptom:** after all SafeMpzMul fixes, the process crashed in `mpz_clear(mpAddB)` (Combine B) with no log entry after `[ComputePi] Combine B: mpz_clear(mpAddB) + mpz_clear(mpR1)`. The crash was silent (no managed exception, no GmpFreeFunc log).

**Root cause:** the Combine A/B/C/D pre-alloc blocks call `VirtualAlloc` to create a result buffer sized to `ceil(sourceSize + k/64 + 2) × 8` bytes, then patch it directly into the `__mpz_struct`. When the source numbers are small (e.g. a test with few digits where `thirdBits = 0` and `gmpNumer` has 1 bit), the computed buffer size is only a few bytes — far below `GMP_LARGE_THRESHOLD` (512 KB). After `mpz_swap`, that tiny VirtualAlloc buffer ends up inside the mpz_t that is later passed to `mpz_clear`. `GmpFreeFunc` sees `size < GMP_LARGE_THRESHOLD` → routes to `_savedGmpFree` (CRT free) → CRT freeing a `VirtualAlloc` pointer → native heap corruption → `STATUS_ACCESS_VIOLATION` with no log.

**Fix:** each Combine A/B/C/D pre-alloc block now guards with `If _shiftBytes >= GMP_LARGE_THRESHOLD Then`. For small numbers the `mpz_init2` buffer (512 KB, already a `VirtualAlloc` allocation) is sufficient and correct — GMP's normal `GmpReallocFunc` handles any growth, and since the source and destination mpz_t variables in the Combine steps are always different (no aliasing), the realloc use-after-free issue cannot occur.

---

## Section 27 — Compute Thread Priority and Power Throttling

**Problem:** when the application window is in the background on a modern Windows 11 system with a hybrid CPU (Intel 12th gen+ with P-cores and E-cores), two independent mechanisms slow the compute thread:

1. **Windows Efficiency Mode / EcoQoS** — Windows automatically opts background processes into power throttling, routing their threads to E-cores and halving their scheduler time quota. This is independent of thread priority and cannot be overridden by `.Priority` alone.
2. **Scheduler priority** — the UI message-pump thread competes with the compute thread for time slices on whichever cores are assigned.

**Fix — two changes:**

1. `DisablePowerThrottling()` is called from `Form1_Load`. It P/Invokes `SetProcessInformation` with `PROCESS_POWER_THROTTLING_STATE { ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED, StateMask = 0 }` — setting `StateMask = 0` explicitly opts the process *out* of execution-speed throttling, overriding Windows' automatic backgrounding policy. This keeps threads on P-cores at full boost frequency for the lifetime of the process.

2. `computeThread.Priority = ThreadPriority.AboveNormal` is set before `computeThread.Start()`. This tells the scheduler to prefer the compute thread over normal-priority threads (including the UI pump), reducing preemption during the ~70-minute computation.

---

## Section 22 — SafeMpzMul: `mp_bitcnt_t` Overflow for Large Equal-Size Operands

**Symptom:** after all previous fixes, the computation ran to completion but produced `gmpNumer ≈ 1 decimal digit` (near-zero), causing `gmpPi = gmpNumer / T = 0`. The last crash was then in `mpz_clear(gmpPi)` after `mpz_get_str` (Section 23 covers that separately).

**Root cause:** `mp_bitcnt_t` on Windows is 32-bit (maximum shift = 4,294,967,295 bits). In `SafeMpzMul`'s 3×3 schoolbook accumulation loop, the shift amount for piece (i,j) is `shiftBits = i×bitsA + j×bitsB`. For the top-level call `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)` with `gmpOne = 10^(1B)` (≈ 52 M limbs), `mA = mB ≈ 17.3 M` and `bitsA = bitsB ≈ 1.109 B bits`. The (i=2, j=2) piece requires `shiftBits = 4 × 1.109 B ≈ 4.436 B bits > 4.295 B = UInt32.MaxValue`. The `CUInt(shiftBits)` cast silently wrapped to ≈ 142 M bits, placing the A₂×B₂ product ≈ 4.3 B bits too low. The schoolbook sum was thus completely wrong, and `gmpSqrtInput` (and hence every subsequent value) came out nearly zero.

**Fix:** in the accumulation loop, compare `shiftBits` to `UInt32.MaxValue` before constructing `mp_bitcnt_t`. When `shiftBits > UInt32.MaxValue`, split into two shifts each ≤ UInt32.MaxValue:

```vb
If shiftBits <= CULng(UInt32.MaxValue) Then
    gmp_lib.mpz_mul_2exp(shifted, prod, New mp_bitcnt_t(CUInt(shiftBits)))
Else
    Dim _shift1 As ULong = shiftBits \ 2UL
    Dim _shift2 As ULong = shiftBits - _shift1
    gmp_lib.mpz_mul_2exp(shifted, prod, New mp_bitcnt_t(CUInt(_shift1)))
    gmp_lib.mpz_mul_2exp(shifted, shifted, New mp_bitcnt_t(CUInt(_shift2)))
End If
```

The second call passes `shifted` as both rop and op1. `MPZ_REALLOC` short-circuits (shifted is pre-allocated to `szA+szB+2` limbs, large enough for the fully-shifted result), so no buffer is freed or reallocated — the in-place left shift is safe.

**Affected calls:** only `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)` hits the overflow for a 1B-digit computation; the three-pass multiply and recursive inner calls have smaller piece sizes whose maximum shift stays below 4.295 B bits.

---

## Section 23 — Division Pre-Alloc Guard: Small VirtualAlloc Buffer for `gmpPi`

**Symptom:** after the Section 22 fix was needed (but before it was applied), the computation produced `gmpNumer ≈ 1 digit` and `T ≈ 930 M digits`, giving `gmpPi = 0` (integer division). The pre-alloc code computed `_quotLimbs = 3`, `_quotBytes = 24 bytes`, called `VirtualAlloc(24)`, then later `mpz_clear(gmpPi)` called `GmpFreeFunc` with `size = 24 < GMP_LARGE_THRESHOLD` → `_savedGmpFree` on a VirtualAlloc pointer → crash. The log showed `[ComputePi] mpz_get_str: converting result to string` as the final entry (the crash happened immediately after in `mpz_clear`).

**Root cause:** same class of bug as Section 21 (Combine A-D). The `gmpPi` pre-alloc unconditionally called `VirtualAlloc` regardless of buffer size, so tiny quotients (from wrong/test inputs) produced dangerously small VirtualAlloc blocks.

**Fix:** wrap the `VirtualAlloc` call in `If _quotBytes >= GMP_LARGE_THRESHOLD Then`. For small quotients, `gmpPi` retains its 1-limb CRT buffer from `mpz_inits`; GMP's normal realloc handles any growth without the allocator mismatch.

---

## Section 24 — `Chr()` Encoding 1252 Not Available on .NET Core

**Symptom:** computation completed successfully (1 billion digits in ~70 minutes), but crashed immediately on entering the display stage:

```
System.NotSupportedException: No data is available for encoding 1252.
   at Microsoft.VisualBasic.Strings.Chr(Int32 CharCode)
   at PI_BillionDigits.Form1.DisplayTimer_Tick
```

**Root cause:** `Microsoft.VisualBasic.Strings.Chr()` is documented as converting an ANSI code point (Windows-1252) to a `Char`. On .NET Core / .NET 5+, Windows-1252 is not loaded by default — `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` is required before `Chr()` can be called, even for values 0–127. The display timer was reading raw bytes from the native Pi string (all ASCII digits 48–57) and converting them with `Chr()`, which threw on every byte.

**Fix:** replace `Chr(...)` with `ChrW(...)` in both byte-reading calls in `DisplayTimer_Tick`. `ChrW` maps directly to a Unicode code point with no encoding dependency; for ASCII (0–127) the result is identical.

---

## Section 25 — Verify Button Searches Full Native Buffer Without Interrupting Display

**Problem:** the Test button searched `RtbPiDigits.Text`, which only contains digits streamed so far by the display timer. The nine-7s sequence (`777777777`) appears at position 24,658,601 and the digits-of-e sequence (`27182818284`) appears even later — both would return "not found" if the user clicked Test before streaming reached those positions. Additionally, the original implementation freed `_displayNativePtr` after searching, which stopped the display timer mid-stream.

**Change:** the native Pi buffer (`_displayNativePtr`, the null-terminated ASCII string produced by `mpz_get_str`) is retained for the lifetime of the computation result. When the user clicks "Verify Now", the button marshals the full native buffer to a managed string via `Marshal.PtrToStringAnsi` and runs all searches against the complete digit sequence — without freeing the buffer. The display timer continues streaming uninterrupted.

The buffer is freed only at the top of `BtnCompute_Click` when a new computation starts.

If `_displayNativePtr` is zero (no native buffer, or a run that used the managed-string path), verification falls back to searching `RtbPiDigits.Text`.

---

## Section 26 — Defensive Fixes for Wrong-Result Pi Buffer

**Symptom:** when `gmpNumer` comes out near-zero (e.g. due to the Section 22 shift bug not being compiled in), `gmpPi = 0` and `mpz_get_str` allocates a 3-byte CRT-malloc buffer (`"0\0"`). Three separate crashes followed:

1. **Streaming buffer overrun** — `_displayNativeLen` was set to `digits + 1` (1,000,000,001) but the actual buffer was 2 bytes. The streaming loop read a billion bytes past the end → access violation.
2. **Wrong free in `BtnTest_Click`** — `VirtualFree` was called unconditionally on the CRT-malloc pointer → heap corruption.
3. **Wrong free in `BtnCompute_Click`** — same unconditional `VirtualFree` on the retained pointer from the previous run.

**Fixes:**
- Added `_displayNativeBufSize As Long` field. Before clearing `gmpPi`, `mpz_sizeinbase(gmpPi, 10) + 2` is captured (mirrors the size GmpAllocFunc receives); `_displayNativeLen` is now derived from this actual digit count rather than assuming `digits + 1`.
- Streaming loop now checks for the null terminator byte-by-byte and stops early if hit, preventing any overrun regardless of buffer size.
- `BtnTest_Click` and `BtnCompute_Click` now dispatch on `_displayNativeBufSize >= GMP_LARGE_THRESHOLD` to choose between `VirtualFree` (large, VirtualAlloc'd buffer) and `_savedGmpFree` (small, CRT-malloc'd buffer).

---

## Section 28 — Diagnostic Logging: SafeMpzMul Verbosity Reduction

**Problem:** The `[SafeMpzMul] loop i=X j=Y: before/after ...` log entries were emitted unconditionally for every iteration of the 9-sub-product loop. With two levels of SafeMpzMul recursion (outer: 52M-limb × 52M-limb; inner: 17M × 17M), each top-level call generates ~91 "done" entries plus hundreds of "before/after" entries. This flooded the log file so completely that the key diagnostic line `[ComputePi] mpz_sqrt: sqrt(X-digit number)` — which immediately shows whether `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)` produced the correct result — was unreachable in the output.

**Fixes:**

1. **Gated verbose loop entries** — all per-iteration `before mul / after mul / before shift / after shift` entries inside `SafeMpzMul` are now compiled only when `LOGGING_DETAIL >= 2`. At the default `LOGGING_DETAIL = 1` they are silent.

2. **TWO-STEP confirmation** — when `shiftBits > UInt32.MaxValue` and the two-step shift path is taken (the §22 fix), a single concise line is written at `LOGGING_DETAIL >= 1`:
   ```
   [SafeMpzMul] TWO-STEP i=2 j=2: shiftBits=4429237504 shift1=... shift2=...
   ```
   This confirms the overflow path is actually being entered.

3. **Result-size summary** — after the 9-sub-product accumulation loop, a single line is written at `LOGGING_DETAIL >= 1`:
   ```
   [SafeMpzMul] done: szA=51,905,127 szB=51,905,127 → 2,000,000,009 digits
   ```
   This shows the output size of every SafeMpzMul call that took the split path, making it possible to verify correctness without examining individual sub-products.

4. **Direct post-call diagnostic** — an unconditional line is written immediately after `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)`:
   ```
   [DIAG] gmpSqrtInput after SafeMpzMul(gmpOne^2): 2,000,000,009 digits
   ```
   If this shows `1 digits`, `SafeMpzMul` is producing near-zero for this call and the §22 two-step shift fix needs further investigation. If it shows ~2B digits, the bug is downstream (three-pass multiply or division).

**Why:** With LOGGING_DETAIL = 1, the log now progresses clearly through the post-binary-split pipeline and shows exactly at which step the computed value first goes wrong, instead of being dominated by hundreds of sub-product trace lines that obscure the diagnostic signal.

---

## Section 29 — SafeMpzMul `_shiftedLimbs` Buffer Too Small for Large Asymmetric Operands

**Problem:** `SafeMpzMul` pre-allocates the `shifted` accumulator with `_shiftedLimbs = szA+szB+2` — the same upper bound used for the `result` buffer. This is correct for the result (a product fits in szA+szB limbs), but not for the shifted accumulator.

The largest shifted value arises at i=2, j=2: `A2*B2 << (2*bitsA + 2*bitsB)`. The product `A2*B2` fits in `(mA+mB)` limbs; the shift then adds `2*mA+2*mB` more limbs, giving `3*(mA+mB)` total. Because `mA = ceil(szA/3)` and `mB = ceil(szB/3)` use ceiling division, `3*mA` can be up to `szA+2` and `3*mB` up to `szB+2`, so `3*(mA+mB)` can reach `szA+szB+4` — up to 4 limbs larger than `_resultLimbs`.

For the final BinarySplitGMP combine (szA≈68 M limbs, szB≈72 M limbs), `3*(mA+mB)+1 = 140,083,427` while `szA+szB+2 = 140,083,426`. That single missing limb means the second `mpz_mul_2exp` in the two-step shift path (§22) triggers `GmpReallocFunc`, which frees the old buffer mid-operation and causes the shifted value — and therefore `newQ` — to come out as zero.

**Symptom observed:**
```
[SafeMpzMul] done: szA=51,905,127 szB=0 → 1 digits   (×3, for Q passes 0/1/2)
[ComputePi] Three-pass multiply: splitting finalQ (Q~1 digits)
```
`finalQ` arrived at the three-pass multiply as zero because BinarySplitGMP's combine produced `newQ=0`.

**Fix (line ~1226 in `SafeMpzMul`):**

```vb
' Before:
Dim _shiftedLimbs As Long = _resultLimbs  ' same upper bound: szA+szB+2

' After:
Dim _shiftedLimbs As Long = 3L * (CLng(mA) + CLng(mB)) + 2L
```

This sizes the shifted buffer to the true worst-case maximum, so `MPZ_REALLOC` always short-circuits and `GmpReallocFunc` is never called during the shift accumulation.

**Why:** The root cause was confirmed by diagnostic log showing `[SafeMpzMul] done: szA=51,905,127 szB=0 → 1 digits`, which proved `finalQ=0` before the three-pass multiply. Tracing back, the only path that could produce zero for a non-zero input was `GmpReallocFunc` firing mid-shift on the insufficiently-sized `_shiftedLimbs` buffer.

---

## Section 30 — GMP `mpz_export` 32-bit Overflow for Large mpz_t (Issue #12)

**Problem:** `SerializeOneMpz` called `mpz_export` with `size=1` (1 byte per word). Internally, Math.Gmp.Native 2.0.6's MSVC build computes the output byte count as `_mp_size * bits_per_limb` in a **32-bit integer**. When `_mp_size > 67,108,864` limbs (i.e., the number exceeds 2^32 bits), this multiplication overflows:

```
_mp_size = 68,132,407
68,132,407 × 64 = 4,360,474,048  →  overflows 32-bit
4,360,474,048 − 4,294,967,296 = 65,506,752 bits → 8,188,344 bytes  (wraps!)
```

So `mpz_export` returned `count = 8,188,344` instead of the correct `~545,059,255`, writing only ~8 MB of the ~545 MB number to disk. On reload, the Q value was reconstructed from truncated/wrong data — producing an incorrect (effectively zero) value for Q at level 16 node 1.

**Symptom observed (diagnostic logs):**
```
[Combine] L16 N1: pre-serialize newQ._mp_size=68,132,407   ← correct before serialize
[SerializeOneMpz] large: _mp_size=68,132,407 bitCount=4,360,474,036 capacity=545,059,256
[SerializeOneMpz] large post-export: byteLen=8,188,343      ← should be ~545,059,255
```
L16 N0's Q (`_mp_size=64,986,678 < 67,108,864`) was below the overflow threshold and serialized correctly, explaining why L17 N0 had `leftQ` correct but `rightQ=0`.

**First attempted fix (size=8) — did not work:**

Changing `size=1` to `size=8` in `mpz_export`/`mpz_import` was tried, but the overflow is *upstream* of the word-size division. GMP computes `_mp_size * GMP_NUMB_BITS` (= `68M * 64 = 4.36B`) as a 32-bit `unsigned long` (MSVC on Windows) before dividing by anything. With `size=8` the divided result is `4.36B / 64 = 68M` word count — but the 32-bit overflow has already happened, so the overflowed bit count `65,506,752` is divided instead: `65,506,752 / 64 = 1,023,543` words → `1,023,543 * 8 = 8,188,344 bytes`. Same wrong answer.

**Final fix — bypass `mpz_export`/`mpz_import` entirely:**

`SerializeOneMpz` now reads `_mp_size` and `_mp_d` directly from the native `__mpz_struct` (via `Marshal.ReadInt32`/`Marshal.ReadIntPtr`) and streams the raw limb bytes in 64 KB chunks. No intermediate buffer or `mpz_export` call.

`DeserializeOneMpz` has two paths:
- **limbCount < 67,108,864** (bit count fits in 32-bit): use `mpz_realloc2` to let GMP manage the allocation, read raw limbs into `_mp_d`, then set `_mp_size`.
- **limbCount ≥ 67,108,864**: call `mpz_clear` to free GMP's existing allocation, `VirtualAlloc` a new limb buffer, write `_mp_alloc`/`_mp_size`/`_mp_d` directly to the struct, then read raw limbs. GmpFreeFunc will call `VirtualFree` when the mpz is later cleared (size ≥ GMP_LARGE_THRESHOLD).

**Disk format change:** header is now `_mp_size` (Int32, signed — encodes sign of the number) rather than `byteLen`. Body is `|_mp_size| * 8` bytes of raw GMP limb data in native (little-endian) byte order.

**Why:** The overflow is in GMP's C code in `mpz/export.c`: `(mp_size_t)(bits_per_limb * abs_size)` where `bits_per_limb = 64` and `abs_size` is `_mp_size`. On MSVC Windows, `unsigned long` is 32-bit, so this overflows for `_mp_size ≥ 67,108,864`. Bypassing the API entirely is the only fix that works regardless of GMP's internal word-size assumptions.

---

## Section 31 — `SafeMpzMul` Lazy A-Piece Creation to Reduce Peak Memory (OOM at Level 18)

**Problem:** `SafeMpzMul` was called for the Level 18 combine with `leftQ` having 133,119,085 limbs (~1.07 GB) and `rightQ` having 7,006,723 limbs (~54 MB). The 3-way schoolbook split path decomposed `opA` (leftQ, the large operand) into three pieces A0, A1, A2 — each ~44,373,362 limbs (~355 MB) — all allocated before the inner loop started. Together with the temporary `Atmp` (710 MB) needed to compute A1 and A2, peak memory for A-pieces alone was:

- A0: 355 MB
- Atmp: 710 MB (used to compute A1 and A2 via successive right-shifts)
- A1: 355 MB
- A2: 355 MB
- Total: **1,775 MB** — on top of the result (~1.07 GB) and shifted (~1.07 GB) pre-allocations, plus baseline compute RAM of ~3.5 GB. Total peak ≈ 7.5 GB, triggering `VirtualAlloc` failure inside `GmpReallocFunc`, which caused a silent process termination (FailFast).

**Fix:** Create A-pieces lazily — one per outer loop iteration. A_part is a single `mpz_t` reused across iterations; each Case computes the appropriate slice and frees any intermediate Atmp immediately:
- `i=0`: `A_part = opA mod 2^bitsA` (no Atmp needed)
- `i=1`: `Atmp1 = opA >> bitsA`, then `A_part = Atmp1 mod 2^bitsA`, then `mpz_clears(Atmp1)`
- `i=2`: `Atmp2 = opA >> bitsA`, then `A_part = Atmp2 >> bitsA`, then `mpz_clears(Atmp2)`

Peak A-piece memory is now Atmp (710 MB) + A_part (355 MB) = **1,065 MB** — a saving of 710 MB — bringing total peak below 7 GB and allowing `VirtualAlloc` to succeed.

**Why the B-pieces are kept upfront:** `opB` is the small operand (7,006,723 limbs, ~54 MB); its three pieces (B0, B1, B2 ≈ 18 MB each) are cheap to coexist and there is no benefit to lazifying them.

---

## Section 32 — Diagnostic Logging to Bisect Post-`done` Crash in `SafeMpzMul`

**Problem:** After the lazy A-piece fix (Section 31), the app still crashes at Level 18. The last log line is `[SafeMpzMul] done: szA=44,373,029 szB=2,335,573 → 899884175 digits` — an inner recursive call completing — but no output follows. The crash is silent (no managed exception, no native crash handler output), indicating it occurs either inside `mpz_clears` (where a corrupted-state exception would bypass `Try/Catch`) or immediately after the inner SafeMpzMul returns in the outer loop's shift/add path.

**Change:** Added two unconditional diagnostic log lines to bisect the crash point:
1. `[SafeMpzMul] cleared: szA=... szB=...` — written after `mpz_clears(prod, shifted, A_part, B0, B1, B2, Nothing)` completes. If this is absent after a `done` line, the crash is inside `mpz_clears`.
2. `[SafeMpzMul] loop i=X j=Y: inner returned` — written (at LOGGING_DETAIL >= 1) after each inner `SafeMpzMul(prod, A_part, B_parts(j))` returns and before the shift/add. If this is absent after the `cleared` line, the crash is in the outer loop's `mpz_mul_2exp` or `mpz_add`.

**Why:** The crash is silent — no managed handler fires — suggesting a corrupted-state exception (e.g., `VirtualFree` called on a bad pointer, or double-free). These two log points will identify which phase (cleanup vs. shift/add) triggers it, enabling a targeted fix.

## Section 33 — Fine-Grained Logging to Pinpoint Crash After `loop i=0 j=2: inner returned`

**Problem:** With Section 32 logs in place, the last lines before the crash are:
```
[SafeMpzMul] cleared: szA=44,373,029 szB=2,335,573
[SafeMpzMul] loop i=0 j=2: inner returned
```
This tells us the inner Level-1 `SafeMpzMul(44M×2.3M)` completed and cleaned up, and the outer Level-0 loop returned from the Level-1 call for `(i=0, j=2)`. The crash is in one of three subsequent operations in the outer Level-0 call (`SafeMpzMul(133M×7M)`):
1. The single-step `mpz_mul_2exp(shifted, prod, shiftBits)` for `(i=0, j=2)`
2. The `mpz_add(result, result, shifted)` for `(i=0, j=2)`
3. The `Case 1` block starting `i=1`: creating `Atmp1` and calling `mpz_tdiv_q_2exp(Atmp1, opA, bitsA)`

**Change:** Added four new diagnostic log lines all at `LOGGING_DETAIL >= 1`:
1. `[SafeMpzMul] loop i=X j=Y: single-step shift=N` — written before `mpz_mul_2exp` in the single-step branch. If absent after `inner returned`, crash is inside `mpz_mul_2exp`.
2. `[SafeMpzMul] loop i=X j=Y: after shift, before mpz_add` — elevated from `LOGGING_DETAIL >= 2` to `LOGGING_DETAIL >= 1`. If absent after `single-step shift`, crash is inside `mpz_mul_2exp`. If present but no `done`, crash is inside `mpz_add`.
3. `[SafeMpzMul] Case 1: Atmp1 alloc start` — written unconditionally at Case 1 entry, before `mpz_inits(Atmp1)`. If absent after all `(i=0,j=0..2)` entries, crash is inside `mpz_add` for `j=2`.
4. `[SafeMpzMul] Case 2: Atmp2 alloc start` — analogous for Case 2.

**Why:** Memory analysis shows ~7 GB peak during the outer `SafeMpzMul(newQ, 133M, 7M)` call. The crash likely reflects OOM inside `mpz_mul_2exp` (which internally calls `mpz_realloc2` → GmpReallocFunc → VirtualAlloc). These logs will identify which exact GMP call is the last one reached, narrowing the fix to either memory reduction or VirtualAlloc failure handling.

## Section 34 — `SafeMpzMul`: Defer `shifted` Pre-Allocation to Inside i-Loop to Reduce Peak Memory

**Problem:** Section 33 logs confirmed the crash is in `mpz_tdiv_q_2exp(Atmp1, opA, bitsA)` at the start of the `i=1` iteration. This call needs to grow `Atmp1` from a tiny CRT buffer to ~710 MB (88.6M limbs × 8 bytes). At that point, `shifted` (pre-allocated to 1.12 GB before the i-loop) and `prod` (374 MB, holding `A0×B2` from the last inner call) were both live, pushing peak to ~7.6 GB and exhausting available physical RAM + page file. The crash is silent because VirtualAlloc can succeed (committing virtual address space) but the subsequent page fault cannot be satisfied when RAM+pagefile are full — the resulting access violation inside native GMP bypasses managed exception handling entirely.

**Change:** Moved `shifted`'s pre-allocation from before the i-loop to inside the i-loop, after the A-piece (`Atmp1`/`Atmp2`) computation and just before the j-loop. After the j-loop completes, `shifted` and `prod` are freed (`mpz_clear` → GmpFreeFunc → `VirtualFree`) and re-initialized to tiny 1-limb CRT buffers (`mpz_init`). On the next i-iteration, the pre-alloc only competes with the tiny re-initialized buffers.

**Memory comparison during `Atmp1` allocation at `i=1` entry:**
| Component | Before (§31–33) | After (§34) |
|---|---|---|
| Caller variables (newP, leftP, leftT, rightT, leftQ, rightQ) | 3,900 MB | 3,900 MB |
| `result` pre-alloc | 1,120 MB | 1,120 MB |
| `shifted` pre-alloc | 1,120 MB | 0 MB (freed after i=0) |
| `prod` (A0×B2 from last inner call) | 374 MB | 0 MB (freed after i=0) |
| `A_part` (A0, about to be replaced) | 355 MB | 355 MB |
| `B`-pieces | 56 MB | 56 MB |
| `Atmp1` being allocated | +710 MB | +710 MB |
| **Peak total** | **~7,635 MB** | **~6,141 MB** |

**Why:** The 1,494 MB reduction is sufficient to keep peak within available RAM+page file. The pre-allocated `shifted` buffer is safe to defer because no j-loop operations run while the A-piece Atmp is live — the allocations are strictly sequential.

## Section 35 — `SafeMpzMul` A-Piece Direct Limb Extraction to Eliminate Atmp Allocations

**Problem:** Section 34 reduced the peak from ~7.6 GB to ~6.1 GB by freeing `shifted` and `prod` between i-iterations. However, the crash continued at the same point (`Case 1: Atmp1 alloc start`), indicating even 6.1 GB exceeds available physical RAM + page file. The `Atmp1` and `Atmp2` allocations (each ~710 MB) are still the peak driver: at the moment they are allocated, `result` (1.12 GB) + `A_part` (355 MB) + caller variables (3.9 GB) + `Atmp1` (710 MB) = ~6.1 GB committed.

**Key observation:** `bitsA = mA * 64` is always a multiple of 64 bits (= 1 GMP limb = 8 bytes), so the three A-pieces fall on exact limb boundaries:
- A0 = `opA` limbs `[0, mA)` — already computed correctly by `mpz_tdiv_r_2exp` in Case 0
- A1 = `opA` limbs `[mA, 2*mA)` — previously needed an 88.6M-limb (710 MB) temporary `Atmp1`
- A2 = `opA` limbs `[2*mA, szA)` — previously needed an 88.6M-limb (710 MB) temporary `Atmp2`

Since the boundaries are limb-aligned, extracting A1/A2 is just a `CopyMemory` from `opA`'s native limb array at the right byte offset, directly into `A_part`'s existing buffer (which was allocated to hold `mA` limbs in Case 0). No temporary is needed.

**Change:** Replaced `mpz_tdiv_q_2exp`/`mpz_tdiv_r_2exp` + `Atmp` in Cases 1 and 2 with direct limb extraction:
1. Read `opA._mp_d` (the native limb array pointer) via `Marshal.ReadInt64(opA.Pointer, 8)`
2. `CopyMemory` `mA` limbs from offset `mA` (Case 1) or `2*mA` (Case 2) into `A_part._mp_d`
3. Scan backwards to find the highest non-zero limb and write the result to `A_part._mp_size`

No `Atmp` mpz_t is created. No GMP memory allocation occurs during Cases 1 or 2 — only a memcpy of 355 MB within already-committed memory.

**Memory comparison at `i=1` Case entry:**
| Component | §31–33 | §34 | §35 |
|---|---|---|---|
| Caller variables | 3,900 MB | 3,900 MB | 3,900 MB |
| `result` | 1,120 MB | 1,120 MB | 1,120 MB |
| `shifted` | 1,120 MB | 0 MB | 0 MB |
| `prod` | 374 MB | 0 MB | 0 MB |
| `A_part` | 355 MB | 355 MB | 355 MB |
| `B`-pieces | 56 MB | 56 MB | 56 MB |
| `Atmp1` | +710 MB | +710 MB | **0 MB** |
| **Peak** | **~7,635 MB** | **~6,141 MB** | **~5,431 MB** |

After the A-piece extraction, `shifted` is pre-allocated (1.12 GB) just before the j-loop, bringing the working set to ~6.55 GB for the actual multiplication phase. This is the irreducible minimum for the i=1 iteration at Level 18.

## Section 36 — `SafeMpzMul` Conditional `shifted` Pre-Alloc (Only When Two-Step Shifts Exist)

**Problem:** After Sections 34 and 35 fixed the Level-18 `SafeMpzMul(133M×7M)` crash, the app crashed at Level 17 (`SafeMpzMul(64.9M×68.1M)`, nearly symmetric operands). The log RAM counter showed 3,084 MB before the call; total peak inside SafeMpzMul was ~8 GB. The bottleneck was `shifted` being pre-allocated to 1.06 GB **for every i-iteration** (0, 1, 2), even though only i=2 can ever trigger two-step aliased shifts at Level 17.

**Root cause of unconditional pre-alloc:** The pre-alloc prevents MPZ_REALLOC from firing during the two-step aliased shift `mpz_mul_2exp(shifted, shifted, shift2)` (where shifted is both rop and op1). If MPZ_REALLOC fires here, GmpReallocFunc moves `_mp_d` to a new block and frees the old one, but the stale op1 copy inside GMP still holds the freed pointer → corruption. However, for **single-step** shifts (`mpz_mul_2exp(shifted, prod, shiftBits)`), `shifted` is rop and `prod` is op1 — they are different variables, so MPZ_REALLOC on `shifted` is completely safe.

**Key insight:** Two-step shifts occur only when `shiftBits = i*bitsA + j*bitsB > UInt32.MaxValue`. The worst case per i-iteration is j=2, so a two-step shift exists for this i if and only if `i*bitsA + 2*bitsB > UInt32.MaxValue`. For Level 17 (`bitsA=1.386B, bitsB=1.453B`): only i=2 satisfies this (2×1.386+2×1.453 = 5.68B > 4.29B). For Level 1 inner calls (`bitsA=0.462B, bitsB=0.484B`): max shift = 1.89B < 4.29B — **no pre-alloc ever needed**.

**Change:** Wrapped the shifted pre-alloc block in `If CULng(i)*bitsA + 2UL*bitsB > CULng(UInt32.MaxValue) Then`. When this condition is False, `shifted` starts as a tiny mpz_init buffer and grows organically via GmpReallocFunc as each single-step shift requires it. At end of j-loop, `shifted` is freed (mpz_clear+mpz_init) regardless.

**Memory savings at Level 17 during inner Level-1 calls:**
| Phase | Before §36 | After §36 |
|---|---|---|
| Level-0 (L17) i=0 j-loop: shifted_L0 | 1,066 MB pre-alloc | tiny → organic growth (max ~718 MB at j=2 shift, transient) |
| Level-1 (inner): shifted_L1 | 355 MB pre-alloc | tiny → organic growth (max ~239 MB) |
| Peak during L1 call at i=0 j=2 | ~8,000 MB | ~6,355 MB |
| Transient peak during L0 i=0 j=2 shift | ~8,000 MB | ~6,477 MB |
| Peak during L0 i=2 j-loop (pre-alloc needed) | same | ~6,884 MB |

---

## Section 37 — `SafeMpzMul` Unconditional Per-Iteration `shifted` Pre-Alloc to Eliminate Organic L→L Reallocs

**Problem:** After Section 36 fixed the Level-17 crash, the app crashed at Level 16 (`SafeMpzMul(33.9M×34.3M)`). The log showed all 9 sub-products completing; the last log line was `loop i=2 j=2: single-step shift=2906982784`. Crash happened inside `mpz_mul_2exp(shifted, prod, 2906982784)` where `shifted` (~453 MB, grown organically from i=2 j=1) needed to grow to ~545 MB via GmpReallocFunc's L→L path. Despite the expected "L→L enter" log being unconditional, it never appeared. The app died silently.

**Root cause — silent organic L→L crash:** GmpReallocFunc's L→L path calls `VirtualAlloc` for the new 545 MB buffer. VirtualAlloc succeeds (commits virtual address space backed by page file). GmpReallocFunc returns the new pointer to GMP. GMP's shift kernel immediately starts writing to the new pages (reading from `prod`, writing to `shifted`). When Windows tries to back those pages with physical RAM, the page file is exhausted — the page fault cannot be satisfied → access violation **inside native GMP** → CLR FailFast before any managed exception handler or logging code runs. This is why "L→L enter" never appeared: the crash happened after `VirtualAlloc` returned but before the next managed log write could execute.

**Why Section 36's conditional approach was insufficient:** Section 36 correctly identified that single-step shifts don't cause aliased-pointer corruption, so pre-alloc was conditional on two-step shifts. However, `mpz_mul_2exp(shifted, prod, shiftBits)` with a single-step shift still calls `MPZ_REALLOC(shifted, needed_limbs)` when `shifted` is undersized. This triggers `GmpReallocFunc` → `VirtualAlloc` for a large new buffer → immediate write → page fault → silent crash. The aliasing safety was correct, but the organic-growth-crashes-on-write problem remained.

**Fix — unconditional per-iteration pre-alloc:** Pre-allocate `shifted` unconditionally before each j-loop, sized to the **per-iteration maximum** across all j in [0,2]:

```
max shifted size for iteration i = prod_limbs + max_shift_limbs
                                 = (mA + mB) + (i·mA + 2·mB)
                                 = (i+1)·mA + 3·mB   [+ 2 for safety]
```

This guarantees no realloc during any `mpz_mul_2exp` call in the j-loop — neither for two-step aliased shifts nor for large single-step shifts. Per-iteration sizing is strictly smaller than the global maximum for i=0 and i=1, reducing peak committed memory vs. pre-allocating to the global max for all iterations.

**Memory at Level 16 N1 (`szA=33.9M, szB=34.3M, mA=11.3M, mB=11.4M`):**

| Iteration | Pre-alloc size (§36) | Pre-alloc size (§37) |
|---|---|---|
| i=0 | none (single-step only) | `1×11.3M + 3×11.4M + 2 = 45.5M limbs = 364 MB` |
| i=1 | none (single-step only) | `2×11.3M + 3×11.4M + 2 = 56.8M limbs = 454 MB` |
| i=2 | 101.5M limbs = 812 MB (global max) | `3×11.3M + 3×11.4M + 2 = 68.1M limbs = 545 MB` |

The organic 453 MB → 545 MB L→L realloc at i=2 j=2 is eliminated. The i=2 pre-alloc (545 MB) is also smaller than the §36 global pre-alloc (812 MB), reducing peak for two-step iterations as well.

**Removed variables:** `_shiftedLimbs` and `_shiftedBytes` (global pre-alloc sizing vars declared before the i-loop) are no longer needed; replaced by `_sLimbs` and `_sBytes` declared inline before each pre-alloc.

---

## Section 38 — `SafeMpzMul` Per-j `shifted` Allocation: Allocate After Inner Returns, Free After Add

**Problem:** After Section 37 fixed the Level-16 crash by pre-allocating `shifted` unconditionally for all three i-iterations, the app crashed at Level 17 N0 (`SafeMpzMul(64.9M×68.1M)`) with the last log line `loop i=0 j=2: after shift, before mpz_add`. Section 37 added a 718 MB `shifted` pre-alloc for outer i=0 that did not exist in Section 36. This pre-alloc was live throughout the outer j-loop, including during all three inner Level-1 SafeMpzMul calls (~21.7M×22.7M). The inner calls added ~1,066 MB of their own allocations (result_L1=355, B_pieces_L1=180, A_part_L1=58, shifted_L1_i2=355, prod_L1=118), pushing the total peak from ~5,884 MB (Section 36) to ~6,602 MB (Section 37). The crash at `mpz_add` with ~5,891 MB live is a deferred consequence of the system being stressed to 6,602 MB during the inner calls immediately before.

**Key insight:** `shifted` does not need to be live during inner calls. It is only used for:
1. `mpz_mul_2exp(shifted, prod, ...)` — rop is `shifted`, op1 is `prod` (different variables)
2. `mpz_mul_2exp(shifted, shifted, ...)` — two-step only, where shifted is both rop and op1 (requires pre-alloc to prevent MPZ_REALLOC aliasing corruption)
3. `mpz_add(result, result, shifted)` — source operand

All three operations occur AFTER the inner SafeMpzMul returns. So `shifted` can be allocated after the inner call and freed immediately after `mpz_add`. This ensures it is never live during any inner call.

**Change:** Remove the per-i shifted pre-alloc block. Inside the j-loop, after the inner SafeMpzMul returns:
1. Compute `_neededLimbs = szProd + shiftBits/64 + 3` (exact size for this j's shift)
2. VirtualAlloc a `_neededLimbs`-limb buffer and install it as `shifted`'s backing store
3. Perform the single-step or two-step shift into `shifted`
4. Call `mpz_add(result, result, shifted)`
5. VirtualFree `shifted`'s buffer and call `mpz_init(shifted)` to reset it to a tiny CRT buffer

This eliminates shifted as a source of memory pressure during inner calls for all i-iterations. The sizing uses the actual `prod._mp_size` (from the just-returned inner call) for exact fit rather than a conservative per-iteration maximum.

**Memory analysis at Level 17 (`szA=64.9M, szB=68.1M`):**

| Scenario | Peak during inner calls | Peak during shift+add | Overall peak |
|---|---|---|---|
| §36 (conditional pre-alloc, i=2 only) | 6,948 MB (at i=2) | 6,237 MB | 6,948 MB |
| §37 (unconditional per-i pre-alloc) | 6,948 MB (at i=2) + new peaks 6,602/6,776 at i=0/i=1 | — | 6,948 MB |
| §38 (per-j, allocated after inner) | 5,884 MB (no shifted live during inner) | 6,237 MB (i=2 j=2 two-step) | **6,237 MB** |

Section 38 saves ~711 MB vs Sections 36/37 (6,237 vs 6,948 MB).

**Why Section 37 crashed while Section 36 did not (at L17):** Section 36 succeeded at L17 with a peak of 6,948 MB at i=2. Section 37 also has a 6,948 MB peak at i=2, but additionally introduces NEW peaks of 6,602 MB at i=0 and 6,776 MB at i=1 that did not exist in Section 36. Repeated high-pressure episodes (all three i-iterations, not just i=2) appear to push the system past its sustainable limit, leading to a crash in the subsequent mpz_add even after the inner call returns.

## Section 39 — `SafeMpzMul` `result.Pointer` Corruption: Save and Restore Native Struct Address

**Problem:** Section 38 produced correct memory behaviour but the accumulated result was always zero. Diagnostic logs at Section 38 showed:
- `res_alloc=44,373,031` (inner's pre-alloc size) instead of `133,119,087` (outer's pre-alloc)
- `res_sz=0` (no accumulation) for all j-iterations
- `shi_sz=0` (shifted was zero because `prod._mp_size=0` was read from the wrong struct)
- `mpz_add` succeeded (adding zeros) and "shifted freed" was logged, then crash in cleanup

Adding pointer-address logging to the "inner returned" message and an INIT log revealed:

```
INIT: res_ptr=1C4CDCFC610  prod_ptr=1C4CDCFC380  res_alloc=133,119,087
j=0 inner returned: res_ptr=1C4CDCFC380  prod_ptr=1C4CDCFC870  res_alloc=44,373,031
j=1 inner returned: res_ptr=1C4CDCFC380  prod_ptr=1C4CDCFC870  res_alloc=44,373,031
```

**Root cause:** `result.Pointer` (the public `IntPtr` field in `Math.Gmp.Native.mpz_t` that holds the address of the native GMP struct) changed from `0x1C4CDCFC610` to `0x1C4CDCFC380` (the old `prod.Pointer`) during the j=0 inner SafeMpzMul call. `prod.Pointer` simultaneously changed to a new address `0x1C4CDCFC870`.

Confirmed by PowerShell reflection: `mpz_clear(x)` sets `x.Pointer = IntPtr.Zero` and `mpz_init(x)` sets `x.Pointer = new_native_struct_address`. The inner SafeMpzMul's calls to `mpz_clear`/`mpz_init` on its own local variables (inner `prod`, `shifted`, etc.) appear to cause a side effect — via an unknown Math.Gmp.Native bookkeeping mechanism — that reuses or swaps native struct addresses in a way that overwrites the outer caller's `result.Pointer` field.

Because `result.Pointer` now pointed to the old `prod` struct (pre-alloc'd to 44M limbs with `_mp_size=0`), all subsequent `Marshal.ReadInt32(result.Pointer, ...)` and `gmp_lib.mpz_add(result, ...)` operated on the wrong struct. The 133M-limb result struct (at the original `savedResultPtr`) was never written to, so the caller received a zeroed result and crashed shortly after.

**Fix:** Save `result.Pointer` as a plain `IntPtr` local variable (`savedResultPtr`) immediately after the pre-alloc — before any inner call can corrupt it. A plain `IntPtr` cannot be modified externally. After each inner `SafeMpzMul(prod, ...)` call, restore `result.Pointer = savedResultPtr`. Also restore after the per-i cleanup (`mpz_clear(prod)`/`mpz_init(prod)`) and before the final `mpz_sizeinbase`/`mpz_neg` operations. This ensures all GMP operations and Marshal reads/writes use the correct native struct throughout.

**Restore points:**
1. After pre-alloc: `Dim savedResultPtr As IntPtr = result.Pointer`
2. After each `SafeMpzMul(prod, ...)` call (inside the j-loop): `result.Pointer = savedResultPtr`
3. After the per-i cleanup `mpz_init(prod)`: `result.Pointer = savedResultPtr`
4. Before `mpz_sizeinbase(result, ...)` and `mpz_neg(result, result)`: `result.Pointer = savedResultPtr`

---

## Section 40 — `SafeMpzMul` Struct-Contents Corruption: Separate Accumulator Object

**Problem:** Section 39's restore of `result.Pointer = savedResultPtr` ran correctly (confirmed by log showing `accum_ptr` staying stable), but `after direct add` still showed `res_alloc=44,373,031 res_sz=0`. The accumulation produced zero for all nine sub-products.

Diagnostic evidence:

```
INIT: res_ptr=2F7B8220140  prod_ptr=2F7B8220440  res_alloc=133,119,087
j=0 inner returned: res_ptr=2F7B8220440  prod_ptr=2F7B3E08EC0  res_alloc=44,373,031
j=0 after direct add: res_alloc=44,373,031  res_sz=0
```

The "after direct add" log reads from `result.Pointer` AFTER restoring `result.Pointer = savedResultPtr = 0x2F7B8220140`. Yet it still shows `_mp_alloc=44,373,031`. This proves the native `__mpz_struct` at address `0x2F7B8220140` was itself overwritten during the inner call — not just the `Pointer` field, but the 16-byte struct contents at that address. The inner pre-alloc value of 44,373,031 was written to `0x2F7B8220140 + 0`, replacing the outer's 133,119,087.

**Root cause (deepened):** The inner SafeMpzMul call corrupts both:
1. `result.Pointer` (the managed field) — changed from `0x2F7B8220140` to `0x2F7B8220440`, via the same Math.Gmp.Native side effect as §39.
2. The contents of the native struct AT `0x2F7B8220140` — `_mp_alloc` at that address is overwritten with 44,373,031 (the inner's result pre-alloc size). The mechanism is not fully understood but likely involves the inner call's `mpz_init`/`mpz_clear` operations freeing and reusing the struct at `savedResultPtr` as part of the inner's local variable lifecycle.

Restoring `result.Pointer` (§39) fixes symptom 1 but not symptom 2. After the restore, GMP reads from the correct address but the struct there has been clobbered.

**Fix (§40):** Accumulate into a completely separate `accum` mpz_t object that is **never** passed to any inner SafeMpzMul call.

- `accum` is allocated with `mpz_init` (giving it a 1-limb CRT buffer) immediately after the pre-alloc.
- Its 1-limb CRT buffer is freed with `_savedGmpFree` and replaced with a fresh VirtualAlloc buffer of `_resultLimbs` capacity.
- All nine sub-product accumulations use `accum` instead of `result` (`mpz_set_ui(accum, 0)`, `mpz_add(accum, accum, ...)`).
- `result`'s struct at `savedResultPtr` is **blanked** (all three fields zeroed) after freeing its old limb buffer. This ensures the inner calls find nothing useful at that address and cannot inadvertently corrupt a live buffer pointer.
- After all nine sub-products are accumulated, `accum`'s three struct fields (`_mp_alloc`, `_mp_size`, `_mp_d`) are copied directly to `savedResultPtr` using Marshal writes, and `result.Pointer = savedResultPtr` is restored.
- `accum`'s struct is then zeroed so that `mpz_clear(accum)` (in the final `mpz_clears`) calls native GMP `mpz_clear` with `_mp_alloc=0` (no limb buffer freed — GMP skips the free when `_mp_alloc == 0`) and frees only the 16-byte `__mpz_struct` allocated by the initial `mpz_init(accum)`. The VirtualAlloc buffer is now owned by `result` and will be freed by the caller's eventual `mpz_clear(result)`.

Since `accum` is never passed to any inner SafeMpzMul call, inner calls have no reference to it and cannot modify its `Pointer` field or its native struct contents, regardless of the corruption mechanism.

---

## Section 41 — `SafeMpzMul` Pointer Corruption Extends to All Outer `mpz_t` Objects

**Problem:** Section 40's `accum` isolation still failed. Log showed:

```
INIT: accum_ptr=166C79478A0  prod_ptr=166C7947400  accum_alloc=133,119,087
j=0 inner returned: accum_alloc=44,373,031 accum_sz=0 accum_ptr=166C79473A0  prod_ptr=166C79475E0
j=0 after direct add: accum_alloc=44,373,031 accum_sz=0
```

`accum.Pointer` changed from `166C79478A0` to `166C79473A0` even though `accum` was never passed to the inner call. `prod.Pointer` also changed from `166C7947400` to `166C79475E0`. The Math.Gmp.Native side effect corrupts the `Pointer` fields of **all** outer `mpz_t` locals during inner SafeMpzMul calls, not just the one passed as `result`.

**Key insight:** The struct at the original `_sv_accum` address (`166C79478A0`) is **intact** — the inner call never writes there. The corruption is in the `Pointer` field of the managed object (pointing to a different address), not in the struct contents at the saved address. Similarly, the inner call's §40 logic writes the multiplication result to the struct at the original `_sv_prod` address (`166C7947400`). Those structs are correct; only the Pointer fields are wrong.

**Fix (§41):** Save `accum.Pointer`, `prod.Pointer`, and `shifted.Pointer` as plain `IntPtr` locals before each inner `SafeMpzMul` call, and restore all three after. Plain `IntPtr` locals cannot be externally modified. After restoration, all subsequent Marshal reads and GMP calls use the correct struct addresses — `accum`'s at the correct 133M-limb buffer and `prod`'s at the struct the inner call deposited its result into.

## Section 42 — `SafeMpzMul` `mpz_t.Pointer` Assignment Does Not Persist; Bypass Wrapper Entirely

**Problem:** Section 41's restore (`accum.Pointer = _sv_accum`) did not persist. Immediately after the assignment, reading `accum.Pointer` returned the corrupted value instead of `_sv_accum`. Log:

```
INIT: accum_ptr=22280FAC110  prod_ptr=22280BBE880  accum_alloc=133,119,087
j=0 inner returned: accum_alloc=44,373,031 accum_sz=0  accum_ptr=22280BBE860  prod_ptr=222C9D4E260
```

`accum.Pointer = _sv_accum` ran (confirmed by placement), yet `accum.Pointer` still showed `22280BBE860` (the inner call's allocated address). ILSpy analysis confirmed `mpz_t.Pointer` is a plain `public IntPtr` field on `mp_base` — no backing store or property interceptor. The root cause remains unclear (possible JIT interaction with Math.Gmp.Native's allocator callbacks during inner calls), but the practical consequence is: **`mpz_t.Pointer` cannot be reliably read after an inner SafeMpzMul call returns**.

**Root cause analysis:** Reflection on `Math.Gmp.Native.dll` revealed that `mpz_t.Initializing()` allocates the native `__mpz_struct` header via `gmp_lib.allocate(16)` and stores the pointer in `this.Pointer`; `mpz_t.Clear()` frees the header and zeros `this.Pointer`. No other Math.Gmp.Native code writes to `x.Pointer` for an arbitrary `x`. Yet empirically, `Pointer` changes for locally-scoped `mpz_t` objects during inner recursive calls.

**Fix (§42):**

1. **Replace managed `accum` with raw `accumPtr`**: Allocate the 16-byte `__mpz_struct` header with `Marshal.AllocHGlobal(16)`, initialize it directly with `Marshal.WriteInt32/WriteInt64`, and use the resulting `IntPtr` everywhere. `Marshal.AllocHGlobal` is entirely outside Math.Gmp.Native's allocator; the pointer cannot be corrupted by any GMP operation.

2. **Add raw P/Invoke declarations** for `libgmp-10.dll`: `GmpRaw_add`, `GmpRaw_mul_2exp`, `GmpRaw_neg`, `GmpRaw_sizeinbase` — bypassing the `mpz_t` wrapper entirely for all accumulation operations.

3. **Use `_sv_prod` and `_sv_shifted`** (plain `IntPtr` saved before each inner call) as raw pointers in the P/Invoke calls. The inner call writes its result to wherever `result.Pointer` pointed at the **start** of the inner call (= `_sv_prod`), so `_sv_prod` always refers to the current sub-product regardless of post-call Pointer corruption.

4. **Free `accumPtr` header** with `Marshal.FreeHGlobal(accumPtr)` at the end (the limb buffer ownership transfers to `result` via `savedResultPtr`).

5. **Remove `accum`** from the final `mpz_clears` call.

## Section 43 — `SafeMpzMul` Managed Stack Frame Corruption by Native GMP

**Problem (§42 crash):** The §42 fix replaced managed `mpz_t accum` with a raw `Marshal.AllocHGlobal(16)` header stored in `Dim accumPtr As IntPtr`. This was expected to be immune to Math.Gmp.Native corruption — but the crash recurred. Log:

```
j=0 inner returned: accum_alloc=44,373,031 accum_sz=0  accumPtr=182C9B4E5B0  _sv_prod=182CA9ED5B0
```

`accumPtr` had been `182C9B4E550` before the inner call (changed by +0x60 = 96 bytes). `_sv_prod` (also a plain `Dim … As IntPtr` local) changed similarly. Both are on the managed stack frame. The heap memory at the original `accumPtr` address was unmodified — only the stack-local **variable** (holding the address) was overwritten.

**Root cause:** On Windows x64 the managed JIT stack and the native P/Invoke stack share the same physical stack. During a deep sub-multiplication inside the inner `SafeMpzMul` call chain (ultimately calling `libgmp-10.dll` FFT at ~33 M × 33 M limbs), native GMP writes beyond the bounds of one of its stack-allocated temporaries, overwriting the outer call's managed stack frame. The outer frame's local `IntPtr` variables happen to sit at the overwritten addresses.

**Fix (§43):** Move `accumPtr` off the managed stack for the duration of inner calls.

1. **Add `Private Shared _accumPtrStack As New Stack(Of Long)()`** — a managed-heap-resident stack that survives native stack writes.

2. **Push before loops:** Immediately after initialising the accumPtr struct, push `accumPtr.ToInt64()` onto `_accumPtrStack`. The push writes to the Stack's internal heap array, not to any stack slot.

3. **Restore after each inner call:** Immediately after `SafeMpzMul(prod, A_part, B_parts(j))` returns, execute:
   ```vb
   accumPtr    = New IntPtr(_accumPtrStack.Peek())  ' from heap, never stack-corrupted
   _sv_prod    = prod.Pointer                        ' inner call restored this to pre-call value
   _sv_shifted = shifted.Pointer                     ' same — inner call doesn't modify shifted
   ```
   The inner `SafeMpzMul` always ends with `result.Pointer = savedResultPtr`, so `prod.Pointer` is correctly restored to the pre-call struct address regardless of any corruption that happened to the outer frame's `_sv_prod` local.

4. **Pop after `Next i`:** `_accumPtrStack.Pop()` releases the entry for this invocation.

This ensures `accumPtr`, `_sv_prod`, and `_sv_shifted` always hold correct values when used in `GmpRaw_add`/`GmpRaw_mul_2exp`, even if native GMP overwrote their managed stack slots during the inner call.

## Section 44 — `SafeMpzMul` Stash accumPtr in result's Native Struct, Not a Managed Stack

**Problem (§43 crash):** The `_accumPtrStack` (a `Shared Stack(Of Long)` on the managed GC heap) also produced wrong values after the inner call. `_accumPtrStack.Peek()` returned `1DD6C26EC00` (the inner call's `accumPtr` address, `_mp_alloc=44,373,031`) instead of the outer call's `1DD6C26E7C0`. Log:

```
INIT: accumPtr=1DD6C26E7C0  accum_alloc=133,119,087
j=0 inner returned: accum_alloc=44,373,031 accum_sz=0  accumPtr=1DD6C26EC00  _sv_prod=1DD6C0FB980
j=1 inner returned: accum_alloc=44,373,031 accum_sz=0  accumPtr=1DD6C26EC00  _sv_prod=1DD6C0FB980
j=2 inner returned: accum_alloc=44,373,031 accum_sz=0  accumPtr=1DD6C26EC00  _sv_prod=1DD6C0FB980
```

The `Peek()` value is wrong for all three j-iterations, meaning either the Push stored the wrong value, the Stack's internal array was corrupted by native GMP, or the inner call's Pop did not execute. All three lead to the same symptom: the outer call uses the inner call's (already-freed) accumPtr struct, producing a zero result.

**Root cause:** Both the managed stack and managed GC heap are susceptible to overwrite by native GMP's stack overflow. No managed-heap object is safe.

**Fix (§44):** Stash `accumPtr.ToInt64()` in the **native CRT-heap struct at `result.Pointer + 8`** (`_mp_d` offset). Key properties that make this safe:

1. `result.Pointer` holds the native CRT address of result's `__mpz_struct` (set at function entry, untouched thereafter by our own code).
2. `result` is the outer call's result parameter. Inner calls receive `prod` as their `result` — they write to `prod.Pointer`'s struct, never to outer `result.Pointer`'s struct.
3. `result.Pointer` is a field of a managed-heap `mpz_t` object — reading it via `result.Pointer` always gives the correct CRT address, because managed-heap fields are not corrupted by native GMP stack overflows (managed heap ≠ native stack).
4. We blank `_mp_alloc` and `_mp_size` (offsets 0 and 4) as before, but write `accumPtr.ToInt64()` to `_mp_d` (offset 8) instead of 0 — this slot is not used by any GMP function during the accumulation phase.

**Recovery:** After each inner `SafeMpzMul` call: `accumPtr = New IntPtr(Marshal.ReadInt64(result.Pointer, 8))`. After `Next i`: re-read both `savedResultPtr = result.Pointer` and `accumPtr = New IntPtr(Marshal.ReadInt64(savedResultPtr, 8))` to ensure both locals are correct before the final struct copy.

The stash is overwritten at the very end when the final `Marshal.WriteInt64(savedResultPtr, 8, ...)` copies the real `_mp_d` (accumBuf pointer) into the result struct — which is the correct final value.

---

## Section 45 — `SafeMpzMul` Case 1 Heap Overflow: `mpz_inits` → `mpz_init2` for A_part

**Crash symptom:** silent process termination during Level 17 Node 0 `SafeMpzMul(newQ, leftQ, rightQ)` (outer call: szA=64,986,678 limbs, szB=3,423,380 limbs; inner call: szA=21,662,226 limbs, szB=22,713,467 limbs). The crash was in the inner call's Case 1 at `CopyMemory(_A1_dst, _A1_src, 57.8 MB)`. A native heap corruption occurred, with no managed exception and no native-crash-handler output.

**Root cause:** `A_part` was initialised with `gmp_lib.mpz_inits(A_part, Nothing)`, which allocates exactly one limb (8 bytes) for the native `__mpz_struct._mp_d` buffer (`_mp_alloc = 1`). For the inner call, `bitsA = mA × 64` where `mA = 7,220,742` limbs. Before `CopyMemory` in Case 1, Case 0 first called `mpz_tdiv_r_2exp(A_part, opA, bitsA)` to extract the low `mA` limbs. The outer `opA` at this recursion level is the outer call's `A_part`, whose low `7,220,742` limbs are all zero (Q accumulates enormous powers of 2 in the Chudnovsky formula; at Level 17 the outer A_part has ~462 million zero low-order bits = ~7.2 million zero low limbs).

When `mpz_tdiv_r_2exp` produces a mathematically-zero result, GMP does **not** call `MPZ_REALLOC` to grow the destination buffer — it simply sets `_mp_size = 0` and returns, leaving `_mp_alloc = 1` (the initial 1-limb allocation). Case 1 then called `CopyMemory(A_part._mp_d, src, mA × 8)` = `CopyMemory(8-byte-buffer, src, 57.8 MB)` → silent heap overflow → crash.

**Why A_part = 0 is expected:** In the Chudnovsky binary split, Q accumulates factors of 2 from every term ((6k+2), (6k+4), (6k+6) each contribute at least one factor of 2). For N ≈ 350M terms (1B digits), Q has approximately 1.05 billion factors of 2 in its prime factorisation. The outer A_part (= outer `opA mod 2^(outer_bitsA)`) with its low 462M bits all zero means outer `opA` is divisible by `2^462M` — the zero result from `tdiv_r_2exp` is mathematically correct.

**Diagnostic path:** Multiple rounds of logging identified the bug:
1. FAST-PRE/POST threshold lowered from `10M` to `5M` limbs to capture inner calls where `szA ≈ 0` but `szB ≈ 7.57M`.
2. Case 0 post-tdiv log added: exposed `A_part_alloc = 1` (expected: `mA = 7,220,742`).
3. Case 1 pre-copy log added: confirmed `A_part_alloc = 1` and `mA = 7,220,742` — the 57.8 MB copy target was a 1-limb buffer.

**Fix:** Replace `gmp_lib.mpz_inits(A_part, Nothing)` with `gmp_lib.mpz_init2(A_part, New mp_bitcnt_t(CUInt(bitsA)))`. `mpz_init2` pre-allocates `ceil(bitsA / 64) = mA` limbs before `mpz_tdiv_r_2exp` is called. Even if `tdiv_r_2exp` produces zero and skips `MPZ_REALLOC`, `_mp_alloc ≥ mA` is already guaranteed. Both Case 1 (`CopyMemory mA limbs`) and Case 2 (`CopyMemory mA limbs`) are safe.

```vb
' Before:
Dim A_part As New mpz_t()
gmp_lib.mpz_inits(A_part, Nothing)

' After:
Dim A_part As New mpz_t()
gmp_lib.mpz_init2(A_part, New mp_bitcnt_t(CUInt(bitsA)))
```

---

## Section 46 — Three-Pass Multiply: `mp_bitcnt_t` Overflow for `k2 = 2 × thirdBits`

**Crash symptom:** `System.OverflowException` immediately after logging `"[ComputePi] Three-pass multiply: splitting finalQ (Q~2,699,652,552 digits)"`. The stack trace pointed to the `k2` construction in the three-pass multiply setup code.

**Root cause:** `mp_bitcnt_t` is defined in GMP's `gmp.h` as `unsigned long`. On Windows 64-bit with MSVC, `unsigned long` is 32 bits, so `mp_bitcnt_t` can only hold values up to `UInt32.MaxValue = 4,294,967,295` bits.

`finalQ` at 1-billion-digit scale has ~8.97 billion bits, so `thirdBits = totalBits / 3 ≈ 2,990,693,808` bits — this fits in `UInt32` (max 4.29 billion). But the original code then computed:

```vb
Dim k2 As New mp_bitcnt_t(CUInt(thirdBits * 2L))  ' 2 × 2.99B ≈ 5.98B → UInt32 overflow
```

`thirdBits * 2L ≈ 5,981,387,616 > 4,294,967,295 = UInt32.MaxValue`. VB.NET's `CUInt` in checked context throws `OverflowException` → crash.

**Fix:** Remove `k2` entirely. Replace the two `k2`-based extraction calls with sequential `k1`-only operations using a temporary `tmpHigh`:

```vb
' Before — crashes:
Dim k2 As New mp_bitcnt_t(CUInt(thirdBits * 2L))
gmp_lib.mpz_tdiv_q_2exp(mpQ2, finalQ, k2)    ' finalQ >> 2k  → Q2
gmp_lib.mpz_tdiv_r_2exp(finalQ, finalQ, k2)  ' finalQ mod 2^2k → Q1*2^k + Q0
gmp_lib.mpz_tdiv_q_2exp(mpQ1, finalQ, k1)    ' >> k → Q1
gmp_lib.mpz_tdiv_r_2exp(finalQ, finalQ, k1)  ' mod 2^k → Q0

' After — no UInt32 overflow:
Dim tmpHigh As New mpz_t()
gmp_lib.mpz_init(tmpHigh)
gmp_lib.mpz_tdiv_q_2exp(tmpHigh, finalQ, k1)  ' tmpHigh = Q2*2^k + Q1
gmp_lib.mpz_tdiv_r_2exp(finalQ, finalQ, k1)   ' finalQ  = Q0

Dim mpQ1 As New mpz_t()
gmp_lib.mpz_init(mpQ1)
Dim mpQ2 As New mpz_t()
gmp_lib.mpz_init(mpQ2)
gmp_lib.mpz_tdiv_r_2exp(mpQ1, tmpHigh, k1)   ' mpQ1 = Q1
gmp_lib.mpz_tdiv_q_2exp(mpQ2, tmpHigh, k1)   ' mpQ2 = Q2
gmp_lib.mpz_clear(tmpHigh)
```

All shift operations use `k1` (≈ 2.99 billion bits), which fits in `UInt32`. Two sequential k1-sized shifts replace the single k2-sized shift, with a temporary holding the upper two-thirds of `finalQ` between them.

---

## Section 47 — Three-Pass Multiply Q-Split: Silent Crash in `mpz_tdiv_q_2exp` (Pre-alloc Missing)

**Crash symptom:** After Section 46 fixed the `OverflowException`, the process still died silently at the exact same log line — `"[ComputePi] Three-pass multiply: splitting finalQ"`. New fine-grained `[3PM-DBG]` logging confirmed the last entry was `"about to tdiv_q_2exp(tmpHigh, finalQ, k1)"`. Neither `"tdiv_q_2exp done"` nor any `[GmpRealloc] S→L enter` log appeared. The crash occurred inside `gmp_lib.mpz_tdiv_q_2exp(tmpHigh, finalQ, k1)` before `GmpReallocFunc` was ever reached.

**Root cause:** `tmpHigh` was initialised with `mpz_init` (1-limb CRT buffer, `_mp_alloc = 1`). When `mpz_tdiv_q_2exp` ran, GMP needed to realloc `tmpHigh` from 1 limb to ~93.4 million limbs (~747 MB) via `GmpReallocFunc`. Inside `GmpReallocFunc`, `VirtualAlloc(MEM_COMMIT | MEM_RESERVE)` was called for 747 MB. Even though `MEM_COMMIT` succeeded, the physical pages were not yet faulted in. GMP then immediately started writing the right-shifted limbs into those pages. The page faults could not be serviced fast enough (or the OS raised a structured exception for the committed-but-unfaulted region) → silent access violation inside native GMP → CLR FailFast with no managed handler possible.

This is the same root cause already documented in Section 43/44 and the Combine section: "VirtualAllocs new pages that GMP writes to immediately; if those page faults can't be satisfied → silent AV inside native GMP → CLR FailFast".

`mpQ1` (~373 MB, result of `mpz_tdiv_r_2exp(mpQ1, tmpHigh, k1)`) and `mpQ2` (~373 MB, result of `mpz_tdiv_q_2exp(mpQ2, tmpHigh, k1)`) had the same problem and were pre-allocated at the same time.

**Fix:** Pre-allocate `tmpHigh`, `mpQ1`, and `mpQ2` via `VirtualAlloc` before the GMP calls, exactly as the Combine section does. Changed from `mpz_init` to `mpz_init2(GMP_LARGE_THRESHOLD * 8 bits)` (so the seed buffer is VirtualAlloc'd and can be freed with `VirtualFree`), then immediately replaced the seed buffer with the correctly-sized VirtualAlloc'd buffer. With `_mp_alloc` set to the needed limb count, `MPZ_REALLOC` short-circuits and `GmpReallocFunc` is never called.

```vb
' Before (crashes):
Dim tmpHigh As New mpz_t()
gmp_lib.mpz_init(tmpHigh)                       ' _mp_alloc = 1
gmp_lib.mpz_tdiv_q_2exp(tmpHigh, finalQ, k1)   ' GMP tries S→L realloc → crash

' After (pre-alloc bypasses GmpReallocFunc):
Dim tmpHigh As New mpz_t()
gmp_lib.mpz_init2(tmpHigh, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))  ' VirtualAlloc'd seed
' ... VirtualAlloc full buffer, VirtualFree seed, write _mp_alloc + _mp_d ...
gmp_lib.mpz_tdiv_q_2exp(tmpHigh, finalQ, k1)   ' MPZ_REALLOC short-circuits → no crash
```

Sizes: `tmpHigh` = `finalQ._mp_size - k1/64 + 2` limbs ≈ 747 MB; `mpQ1` = `mpQ2` = `k1/64 + 2` limbs ≈ 373 MB each.

---

## Section 48 — Thread-Safe GMP Allocator Callbacks (`AppendLog` helper)

**Branch:** PerfWork

**Change:** Added a `_logLock As New Object()` shared field and an `AppendLog(message)` static helper that wraps `File.AppendAllText` in `SyncLock _logLock`. All `System.IO.File.AppendAllText(LOG_FILE, ...)` calls inside `GmpAllocFunc`, `GmpReallocFunc`, and `GmpFreeFunc` were replaced with `AppendLog(...)`.

**Why:** The underlying memory operations in the three allocator callbacks — `VirtualAlloc`, `VirtualFree`, MSVC CRT `malloc`/`realloc`/`free` — are all intrinsically thread-safe Win32/CRT APIs. The only non-thread-safe element was the `File.AppendAllText` log writes: concurrent calls from multiple worker threads can race on the log file and either lose entries or throw `IOException` (silently swallowed by the `Try/Catch`). The lock ensures log entries are never interleaved or dropped under parallel load.

---

## Section 49 — Parallel Phase 1: `Parallel.For` over 137,700 Independent Chunks

**Branch:** PerfWork

**Change:** Replaced the serial `For i As Long = 0 To numChunks - 1` loop in `BinarySplitGMP` Phase 1 with `Parallel.For(0L, numChunks, Sub(i) ...)`. Results are written into a pre-sized `DiskNode()` array by index (no locking needed — each index is written exactly once by exactly one thread). After the parallel section, `diskNodes.AddRange(chunkResults)` populates the list in order. Progress is tracked with `Interlocked.Increment` and the status label is updated every ~1% of chunks (`statusUpdateInterval = Math.Max(1, numChunks \ 100)`). Per-chunk `SerializeNodeToDisk` log entries are suppressed (`detailLog:=False`) to avoid 137K concurrent log writes.

---

## Section 50 — Progress Updates During Old Cache Deletion

**Branch:** PerfWork

**Change:** Before deleting old `.bin` cache files, the file list is now fetched with `Directory.GetFiles` so the total count is known. An initial `BeginInvoke` sets `LblStatus` to `"Clearing N cached files from previous run..."`. During the deletion loop a further update fires every 1,000 files showing `"Clearing cache: X / N files deleted..."`.

**Why:** Deleting ~137,739 small files from a previous run is a pure NVMe metadata workload taking ~2 minutes. The thread pool is idle at this point (the `Parallel.For` hasn't started) so `BeginInvoke` reaches the UI thread promptly. Without this change the status label was frozen at the `LogPhase` message for the entire deletion period with no indication of progress.

---

## Section 51 — Fix Phase 1 Status Label Never Updating for Small Chunk Counts

**Branch:** PerfWork

**Change:** Replaced the hard-coded `done Mod 1000L = 0L` update condition with `done Mod statusUpdateInterval = 0L`, where `statusUpdateInterval = Math.Max(1L, numChunks \ 100L)`. The interval variable is computed once before the `Parallel.For`.

**Why:** With 138 chunks (small digit counts), `done` only reaches 138, so `done Mod 1000 = 0` is never satisfied and `LblStatus` stays frozen on the initial `LogPhase` message for the entire duration of Phase 1. The dynamic interval fires at every ~1% of completion regardless of total chunk count — for 138 chunks it updates every 1–2 chunks; for 137,700 chunks it updates every ~1,377.

---

## Section 51 — Fix Phase 1 Status Label Not Updating on Full 1B Run (Timer-Based Polling)

**Branch:** PerfWork

**Change:** Replaced the `BeginInvoke`-inside-parallel-loop approach with a dedicated background `Thread` (not a thread-pool thread) that polls `completedChunks` via `Interlocked.Read` every 500 ms and posts a `BeginInvoke` to update `LblStatus`. The thread loops `While completedChunks < numChunks` and exits naturally when the parallel loop finishes; the calling thread calls `phase1PollThread.Join()` after `Parallel.For` returns. The `Parallel.For` body retains only `Interlocked.Increment` and a file log every 5,000 completions.

**Why:** `System.Threading.Timer` callbacks execute on thread-pool threads. `Parallel.For` exhausts the thread pool, so timer callbacks are queued but cannot run — causing the status label to stay frozen at the initial `LogPhase` message for ~2 minutes. A dedicated `Thread` gets its own OS time-slice from the Windows scheduler, independent of thread-pool saturation, so the first status update appears within 500 ms of `Parallel.For` starting.

---

## Section 54 — Parallel Multiplications Within Phase 2 Serial Combines

**Branch:** PerfWork

**Change:** In the serial Phase 2 combine path (top levels, `pairCount < 4`), replaced the four sequential `SafeMpzMul` calls with two `Parallel.Invoke` pairs:
- **Pair 1:** `newP = leftP × rightP` and `newQ = leftQ × rightQ` run simultaneously (disjoint operands).
- **Pair 2:** `tempA = leftT × rightQ` and `tempB = leftP × rightT` run simultaneously (disjoint operands).

The `mpz_clears` early-free calls and the final `mpz_add` are unchanged and still happen after both tasks in each pair complete. `LOGGING_DETAIL` pre-call size logs for each pair are emitted together before the `Parallel.Invoke` (read-only access; safe).

**Why:** At the top 2–3 combine levels (1–3 pairs), each `SafeMpzMul` operates on operands hundreds of MB to over a GB in size and takes minutes. Since `newP`/`newQ` use completely disjoint operands, and `tempA`/`tempB` likewise, `Parallel.Invoke` gives ~2× wall-clock speedup on each pair. `SafeMpzMul` calls with distinct result and operand objects are thread-safe: GMP arithmetic on non-aliased `mpz_t` objects is safe concurrently, and all shared state (allocator logging) is already protected by `SyncLock _logLock` (§48).

**Note:** The full 9 sub-product parallelism inside `SafeMpzMul` itself was considered but deferred — see memory file `project_safempzmul_parallel_future.md`.

---

## Section 62 — Degree-of-Parallelism Caps to Prevent Oversubscription

**Branch:** AdvPerfWork

**Change:** Added `ParallelOptions.MaxDegreeOfParallelism` to two sites:

1. **SafeMpzMul `Parallel.For(0, 9)`** — capped to `Environment.ProcessorCount`. When `SafeMpzMul` is called from inside Phase 2's outer `Parallel.For`, uncapped inner parallelism creates `pairCount × 2 × 9` concurrent tasks on 24 cores (e.g. 8 pairs × 2 × 9 = 144 tasks). This saturates memory bandwidth without proportional throughput gain. Capping to `ProcessorCount` ensures the inner loop never exceeds the physical core count regardless of nesting depth.

2. **Phase 2 outer `Parallel.For(pairCount)`** — capped to `Math.Max(1, Environment.ProcessorCount \ 2)`. Each outer task spawns 2 inner tasks via `Parallel.Invoke` (§60), so uncapped outer DOP × 2 = `2 × ProcessorCount` concurrent multiplications. Capping outer DOP to `ProcessorCount \ 2` keeps total concurrent multiplications at ~`ProcessorCount`, leaving the thread pool headroom to schedule the inner `Parallel.Invoke` tasks without queuing.

**Why:** .NET's thread pool uses work-stealing so oversubscription doesn't deadlock, but it does cause excessive context switching and memory bus contention for GMP's FFT multiplications which are already memory-bandwidth-bound at the larger operand sizes. These caps keep effective concurrency at the physical core count without reducing throughput at levels where pairCount < ProcessorCount/2 (where the outer cap is non-binding anyway).

---

## Section 61 — Parallel Three-Pass Q Multiply + Non-Blocking GC Between Levels

**Branch:** AdvPerfWork

### Part A — Parallel three-pass Q multiply

**Change:** The three serial passes `r0 = gmpNumer × Q0`, `r1 = gmpNumer × Q1`, `r2 = gmpNumer × Q2` now run simultaneously via `System.Threading.Tasks.Parallel.Invoke`. `gmpNumer` is read-only in all three calls; each result is a distinct `mpz_t`, so there is no shared mutable state between threads.

Q1 and Q2 are no longer spilled to disk between extraction and use — they stay in RAM. r0 and r1 are no longer spilled and reloaded between passes — Combine B and D use the in-memory variables directly. This removes 6 disk I/O operations (4 serialize + 2 deserialize) and eliminates the disk round-trip latency entirely.

**Why:** The three passes were purely serial because of the disk spill/reload pattern. Each pass took roughly the same time as one `SafeMpzMul` on ~400 MB operands. Running them in parallel gives up to 3× speedup on this section. The disk spilling was necessary when memory was constrained (before §58 raised `DISK_THRESHOLD`); with the full-RAM mode on a 64 GB machine the ~1.2 GB peak for all three simultaneous results is trivial.

**Memory impact:** Peak during parallel multiply: gmpNumer (~208 MB) + Q0+Q1+Q2 (~549 MB) + r0+r1+r2 (~1.15 GB) + SafeMpzMul intermediates (~2.9 GB) ≈ ~4.8 GB. Well within the 64 GB budget.

### Part B — Non-blocking GC between Phase 2 levels

**Change:** The `GC.Collect(MaxGeneration, Aggressive, blocking:=True, compacting:=True)` call between Phase 2 levels now uses `GCCollectionMode.Optimized` with `blocking:=False` at all levels except the final level (where the aggressive blocking collect is retained to reclaim the large Phase 2 intermediates before the final arithmetic).

**Why:** The aggressive blocking GC was pausing all threads at each of ~17 levels. At lower levels (many small nodes, fast combines) these pauses were a significant fraction of level time. A non-blocking optimized collect is sufficient to reclaim the `mpz_t` wrapper objects from freed pairs without stalling parallel work.

---

## Section 60 — Parallel.Invoke Inside Parallel Phase 2 Pairs

**Branch:** AdvPerfWork

**Change:** Added `Parallel.Invoke` for the two independent multiply pairs inside the `Parallel.For` lambda of the parallel Phase 2 path — the same §54 pattern already used in the serial top-level path:
- **Invoke pair 1:** `newP = leftP × rightP` and `newQ = leftQ × rightQ` run simultaneously (disjoint operands).
- **Invoke pair 2:** `tempA = leftT × rightQ` and `tempB = leftP × rightT` run simultaneously (disjoint operands).

**Why:** As Phase 2 levels rise, the number of pairs halves each level while each pair's operands double in size (and multiply time grows quadratically). Without this change, each outer `Parallel.For` task uses only 1 core — the 4 multiplications per pair are serial. By Level 2 (34,435 pairs, each taking ~4× longer than Level 1), each task was using 1/24th of available cores. Adding `Parallel.Invoke` within each task doubles intra-pair parallelism, effectively maintaining the same number of concurrent multiplications as Level 1 (34,435 × 2 = 68,870 simultaneous multiplies vs Level 1's ~68,870 pairs × 1).

This mirrors the §54 change applied to the serial top-level path. `SafeMpzMul` with non-aliased operands is thread-safe; all shared log writes are already serialised via `SyncLock _logLock`.

---

## Section 59 — Parallel 9 Sub-Products Inside SafeMpzMul

**Branch:** AdvPerfWork

**Change:** The 9 independent sub-products `A_i × B_j` (i,j ∈ {0,1,2}) inside `SafeMpzMul`'s slow path now run concurrently via `Parallel.For(0, 9, ...)` instead of serially in nested `For i / For j` loops.

Key design points:
- **A-piece extraction moved upfront:** A0, A1, A2 are all extracted before the parallel section (previously A_part was lazily re-extracted per-i iteration). Each is `mpz_init2`'d to ensure `_mp_alloc >= mA` before direct limb copies for A1 and A2.
- **9 distinct `prods(k)` result buffers:** Each thread writes into its own `prods(k)` object; no shared mutable state between threads.
- **Thread safety:** A_parts and B_parts are read-only during the parallel section. GMP arithmetic on non-aliased `mpz_t` objects is intrinsically thread-safe. The §44 accumPtr stash lives in `result`'s native struct; inner calls stash into `prods(k)` structs and never touch `result`'s struct, preserving the outer stash.
- **Serial accumulation after:** After `Parallel.For`, a serial `For k = 0 To 8` loop shifts each `prod_k` into its positional slot (`shiftBits = i*bitsA + j*bitsB`) and accumulates into `accum`. No inner `SafeMpzMul` calls in this loop — no §42/§44 corruption risk. Each `prods(k)` is freed immediately after use to limit peak RAM.
- **Existing shift/add/free logic preserved:** The VirtualAlloc pre-sizing for `shifted`, two-step shift for large `shiftBits`, and `GmpRaw_add` accumulation are unchanged from the serial path.

**Why:** At Levels 14–17 of Phase 2, `SafeMpzMul` hits the slow path because `szA + szB > 33,554,431` limbs. The 9 sub-products were computed serially, leaving 23 cores idle during each multiply. Running them in parallel gives up to 9× speedup on the sub-product step — the dominant cost at the top combine levels.

**Memory impact:** At Level 17, 9 simultaneous sub-products × ~374 MB each = ~3.4 GB extra peak RAM. Plus A0+A1+A2 simultaneously (~1.1 GB vs ~374 MB lazy). Total ~3.1 GB extra vs serial — well within the 64 GB machine's headroom.

---

## Section 58 — Full RAM Mode (DISK_THRESHOLD raised to 200,000)

**Branch:** AdvPerfWork

**Change:** `DISK_THRESHOLD` raised from `1` to `200_000` in `BinarySplitGMP`. Since `numChunks = 137,739 < 200,000`, Phase 1 keeps all chunk results in the `chunkResults()` array in RAM (no `L0.bin` written). Since every Phase 2 level produces `nextSize < 200,000` nodes, all levels also stay in RAM. The NVMe is no longer touched during computation at all — only for writing the final digit string.

**Why:** `DISK_THRESHOLD = 1` was set defensively during the §40–§44 allocator crash fixes, not because RAM was insufficient. The machine has 64 GB RAM; the total P+Q+T data across any single Phase 2 level is ~1.7 GB (constant across all levels), and peak RAM during a combine (current level + next level + multiply intermediates) is ~6–8 GB. With the allocator now stable, there is no reason to use disk as a crutch. Eliminating disk I/O from Phase 2 removes the NVMe read/write overhead that dominated wall-clock time at Levels 1–13.

---

## Section 57 — Phase 2 Level Progress in Status Label

**Branch:** PerfWork

**Change:** The status label now shows pair progress for every Phase 2 combine level:

- **Parallel path** (`pairCount >= 4`): A dedicated background `Thread` (not thread-pool) polls an `Interlocked`-incremented `completedPairs` counter every 500 ms and posts `BeginInvoke` updates showing `"Phase 2 Level N: X / Y pairs"`. `Interlocked.Increment(completedPairs)` is called at the end of each `Parallel.For` lambda body. The calling thread calls `phase2PollThread.Join()` after `Parallel.For` returns (same pattern as Phase 1 §51).
- **Serial path** (`pairCount < 4`, top levels): A direct `BeginInvoke` is posted after each pair completes, showing the same `"Phase 2 Level N: X / Y pairs"` format. The thread pool is not exhausted at these levels (only 1–3 pairs, each taking minutes), so `BeginInvoke` reaches the UI thread promptly.

**Why:** During Phase 2 the status label was frozen with no indication of progress within a level. For Level 1 (~68,869 pairs) running in parallel, users had no visibility into how far along the combine was. The dedicated-thread pattern is required for the parallel path for the same reason as Phase 1: `Parallel.For` exhausts the thread pool, starving any timer-based polling.

---

## Section 56 — Larger Staging Buffer in SerializeOneMpz / DeserializeOneMpz

**Branch:** PerfWork

**Change:** Increased the `staging` array from 64 KB (`staging(65535)`) to 4 MB (`staging(4194303)`) in three places:
- `SerializeNodeToDisk` — the buffer passed to `SerializeOneMpz` for all three fields
- `LoadNodeFromDisk` — the buffer passed to `DeserializeOneMpz` for all three fields
- The `Parallel.For` lambda in Phase 1 (`stagingBuf`) — used for the per-chunk `MemoryStream` serialization path

**Why:** `SerializeOneMpz` and `DeserializeOneMpz` read/write limb data in a `While remaining > 0` loop, each iteration copying `staging.Length` bytes via `Marshal.Copy` and writing/reading via `BinaryWriter`/`BinaryReader`. With a 64 KB buffer, a Level-17 value (~560 MB) requires ~8,738 loop iterations. A 4 MB buffer reduces this to ~137 iterations — a 64x reduction in loop overhead. The gains are largest at Levels 14–17 where each `mpz_t` is tens to hundreds of MB. The 4 MB buffer exceeds the .NET 85 KB LOH threshold but is short-lived (local to each call, collected promptly) so LOH fragmentation is not a concern.

---

## Section 55 — Single-File L0.bin Format for Level-0 Chunks

**Branch:** PerfWork

**Change:** Replaced 137,739 individual `L0_N{i}.bin` files with a single `L0.bin` file for all Level-0 chunks. Key design points:

- A `FileStream` for `L0.bin` (4 MB buffer, `FileShare.None`) is opened once before the `Parallel.For`.
- Each worker thread serializes its three `mpz_t` values to a `MemoryStream` outside any lock (no contention for large serialization work), then enters a `SyncLock` only to record the current stream position into `node.FileOffset` and write the pre-built bytes — minimising lock hold-time to a single seek + write.
- `DiskNode` gains a `FileOffset As Long` field (0 for non-L0 nodes that use individual files).
- `LoadNodeFromDisk` gains a `fileOffset As Long` parameter; if non-zero, `fs.Seek(fileOffset, SeekOrigin.Begin)` is called before reading.
- All five `LoadNodeFromDisk` call sites pass `diskNodes(idx).FileOffset`.
- Phase 2 combine loops skip `File.Delete` for Level-0 nodes (`diskNodes(idx).Level = 0`) — `L0.bin` is cleaned up by the single-file `Directory.GetFiles` + delete at the start of the next run (§50), which now removes 1 file instead of 137,739.

**Why:** The ~2-minute NVMe metadata overhead at the start of every run was caused by deleting 137,739 small individual files (§50 visible). A single `L0.bin` file reduces the next run's cache clear from 137,739 `File.Delete` calls to 1, eliminating that entire overhead. The lock-outside-memorystream pattern keeps thread contention minimal even on 24 cores writing simultaneously.

---

## Section 53 — Parallel Phase 2 Combines (Levels 1–N-3)

**Branch:** PerfWork

**Change:** Replaced the serial `While nodeIdx < diskNodes.Count - 1` inner loop in the Phase 2 combine pass with a `Parallel.For` over pair indices when `pairCount >= 4`. Each pair is fully independent: it loads from two unique disk files, uses only thread-local `mpz_t` objects, and writes to a unique output file. Results are written into a pre-sized `nextResults(pairCount-1)` array by pair index (no locking needed), then `nextDiskNodes.AddRange(nextResults)` populates the list in order. The serial path is retained for `pairCount < 4` (the top levels where operands are very large and there are too few pairs for parallelism to help without excessive RAM pressure). The `LOGGING_DETAIL` diagnostic blocks and the per-100-pair `LogPhase` call remain in the serial path only.

**Why:** At the lower combine levels there are thousands of independent node pairs per level — e.g. ~68,869 pairs at Level 1, ~34 at Level 12. Each pair's four `SafeMpzMul` calls are entirely independent of all other pairs. On a 24-logical-processor machine this is the same situation as Phase 1: a serial loop leaves 23 cores idle. The `pairCount >= 4` threshold keeps the top 1–2 levels serial where loading multiple pairs simultaneously would multiply already-large (~hundreds of MB to GB) operands across threads and risk OOM.

---

## Section 68 — GMP Pool Cap Raised to 256

**Branch:** AdvPerfWork

**Change:** `POOL_CAP` increased from 32 to 256.

**Why:** With outer Phase 2 DOP=24 (§69) and each pair running `Parallel.Invoke(2 SafeMpzMul)`, up to 48 concurrent GMP operations can return pool blocks simultaneously. A cap of 32 meant the pool was constantly evicting via `VirtualFree` and missing on the next alloc via `VirtualAlloc`, negating the pool's purpose. Raising to 256 keeps all concurrently live blocks in the pool for immediate reuse.

---

## Section 69 — DOP Rebalance: Phase 2 Outer=ProcessorCount, SafeMpzMul Inner=1

**Branch:** AdvPerfWork

**Change:** Two coordinated DOP changes to eliminate nested thread-pool oversubscription:

1. **Phase 2 parallel outer DOP:** raised from `ProcessorCount \ 2` (12) to `ProcessorCount` (24).
2. **SafeMpzMul inner DOP:** controlled by new `Shared _safeMulDop As Integer` field. Set to `1` (serial) before the Phase 2 outer `Parallel.For`; restored to `ProcessorCount` after the parallel path completes (before the serial top-level path and `ComputePiGMP`). In `SafeMpzMul`, when `_safeMulDop <= 1`, the 9 sub-products run in a serial `For k = 0 To 8` loop instead of `Parallel.For`.

**Effect:** `_safeMulDop = 1` eliminates the inner `Parallel.For` inside `SafeMpzMul` when called from the Phase 2 parallel path — sub-products run serially, no nested task submission. Serial Phase 2 top levels and `ComputePiGMP` restore `_safeMulDop = Environment.ProcessorCount` to keep full inner parallelism for those single-pair operations.

**Why (trace 4 → trace 5 correction):** Trace 4 showed `LowLevelLifoSemaphore.WaitForSignal` at 18.77% — thread pool threads parking between sub-product tasks. Eliminating inner `Parallel.For` from the parallel path fixed that. However trace 5 revealed that raising outer DOP to `ProcessorCount` (24) was counterproductive: each outer task calls `Parallel.Invoke(newP, newQ)` which needs a free thread for the second task. With all 24 threads occupied by outer tasks, `Parallel.Invoke` could not immediately place its second task — `Task.WaitAll` jumped to 19.33% and the allocator worsened from 35% to 44% (48 concurrent GMP operations vs 24). Outer DOP is therefore restored to `ProcessorCount \ 2`, giving 12 outer tasks × 2 free threads per Invoke = both newP and newQ always run truly in parallel with no blocking.

---

## Section 63 — Headless / Automation Mode + PowerShell Script

**Branch:** PerfWork / AdvPerfWork

**Change:** Added three CLI arguments parsed in `Form1_Load`:

- `--digits N` — sets the digit count programmatically (bypasses the UI spinner).
- `--autostart` — triggers `BtnCompute_Click` from `Form1_Shown` so the computation starts without any user interaction.
- `--autoverify` — triggers `BtnTest_Click` automatically after computation completes.

All three `MessageBox.Show` dialogs (compute complete, verify result, error) are gated: in headless mode the dialog text is written to the phase log with a `[DIALOG]` prefix instead of blocking the process.

Headless defaults applied at startup: `ChkboxDisplay.Checked = False`, `TxtChunkSize.Text = "500000"`, `ChkboxWriteToFile.Checked = True`.

Added `Run-PiCompute.ps1`:
- `dotnet clean` → `dotnet build --configuration Release` → launch exe with `--digits $Digits --autostart --autoverify`.
- `-Trace` switch: wraps the run with `dotnet-trace collect` (providers: `Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5` + `Microsoft-DotNETRuntime:0x1F000080018:5`), then calls `dotnet-trace report topN -n 50 --inclusive` and saves a `_report.txt` alongside the `.nettrace`.
- `-ReportOnly <path>`: skips build/run; re-generates the topN report from an existing `.nettrace`.
- Output directory hardcoded to `c:\PiOutput`; created if missing. (Replaced in §70 — see below.)
- `.gitignore` updated to exclude `*.nettrace`, `*.nettrace.etlx`, `*.etlx`, `*.etl`, `*.etl.zip`, `*.speedscope.json`, `*.diagsession`, `*.vspx`, `*.psess`, `*_report.txt`, `pi_trace_*`.

---

## Section 70 — Make Run-PiCompute.ps1 Machine-Independent

**Branch:** AdvPerfWork

**Change:** Three portability improvements to `Run-PiCompute.ps1`:

1. **Auto-detect exe path** — after the build, `Get-ChildItem -Recurse` globs `bin\Release\**\PI-BillionDigits.exe` and takes the most recently written match. No hardcoded TFM folder (`net10.0-windows10.0.26100.0`) — works correctly when the target framework version changes or on machines with a different Windows SDK.

2. **Relative output directory** — `$piOutputDir` replaced by `-OutputDir` parameter defaulting to `.\PiOutput` (i.e. a `PiOutput` folder next to the script). No longer requires write access to `c:\`. Override with `-OutputDir "D:\SomeOtherPath"` without editing the script body.

3. **Configurable digit count** — `-Digits` parameter (default `1000000000`) passed through to the exe, replacing the hardcoded `--digits 1000000000`. Useful for quick test runs at lower digit counts.

**Why:** The original hardcoded `c:\PiOutput` and `bin\Release\net10.0-windows10.0.26100.0` paths break silently on any machine where the system drive letter differs, the user lacks root-write permission, or the .NET SDK target framework version is updated. All machine-specific values are now computed defaults that work on a clean clone without any manual configuration.

---

## Section 71 — Portable Output Paths + Remove Vestigial Chunk Size UI

**Branch:** AdvPerfWork

**Change:** Three related clean-up items addressing issue #5:

**1. Machine-independent output paths in Form1.vb**

All hardcoded `c:\PiOutput` paths replaced with a single `Shared _outputDir` field defaulting to `%LOCALAPPDATA%\PI-BillionDigits` (always writable, no admin rights, works on any drive letter). `outputFile`, `LOG_FILE`, and `DISK_CACHE_DIR` are now computed `ReadOnly Property` values derived from `_outputDir`. The two `File.AppendAllText("c:\PiOutput\pi_phase_log.txt", ...)` calls in `StreamPiToScreen` and `DisplayTimer_Tick` that bypassed the logging subsystem are replaced with `WriteToLog(...)` calls.

**2. Remove Chunk Size UI control**

`TxtChunkSize` (TextBox) and its `Label5` ("Chunk Size:") label removed from the form. The control originally configured Phase 1 chunk size, which has been a compile-time constant (`Const CHUNK_SIZE As Long = 512`) since §49. Its only remaining effect was controlling `DisplayTimer_Tick` streaming speed (chars per 100 ms tick), which is now a code constant (`Const chunkSize As Integer = 500`). The headless default-setting line (`TxtChunkSize.Text = "500000"`) is also removed.

**Why:** `TxtChunkSize` was presenting a misleading "Chunk Size" label to users that no longer did what it implied (Phase 1 computation granularity). Its actual effect (display streaming rate) is invisible to users running headless or with display off, and the default of 500 chars/tick is appropriate for interactive viewing of smaller runs. Removing it declutters the UI and eliminates a potential source of confusion.

## Section 64 — Skip Display Loop When Display Is Off

**Branch:** PerfWork / AdvPerfWork

**Change:** `StreamPiToScreen` now fast-paths at entry: if `ChkboxDisplay.Checked = False`, it calls `WriteResultToFile()` directly and returns without entering the streaming loop. `WriteResultToFile(digitCount As Long)` is extracted as a shared helper used by both the fast path and the display loop's final step.

**Why:** In headless mode (or any interactive run with Display unchecked) the streaming loop was still iterating over the native buffer one chunk at a time for no visible effect, wasting several seconds.

---

## Section 65 — Bucketed VirtualAlloc Pool (GMP Allocator v3)

**Branch:** AdvPerfWork

**Change:** Replaced the per-call `VirtualAlloc`/`VirtualFree` allocator with a power-of-2 bucketed pool:

- 64 fixed `ConcurrentStack(Of IntPtr)` slots, one per bit-length bucket (bucket `b` holds blocks of exactly `2^b` bytes).
- `PoolBucket(sz)` computes `ceil(log2(sz))` via a shift loop — no `Math.Log`, no dictionary lookup.
- `PoolGet`: if `sz ≤ POOL_MAX_BLOCK (16 MB)`, pops from the matching bucket (or calls `VirtualAlloc(2^b)` on miss). Blocks above 16 MB bypass the pool entirely (their sizes are unique per combine level and would never hit).
- `PoolReturn`: if `sz ≤ POOL_MAX_BLOCK` and `stack.Count < POOL_CAP (32)`, pushes back; otherwise `VirtualFree`.
- `FlushGmpPool()` drains all 64 stacks via `VirtualFree`, called between Phase 2 levels and in the `ComputePiGMP` Finally block to prevent committed-memory accumulation.
- Pool stacks initialised in `InitGmpVirtualAllocFunctions`.

**Why (v1 → v2 → v3 evolution):**
- v1 (§6): per-call `VirtualAlloc`/`VirtualFree` — correct but ~41% of wall-clock time in allocator overhead per dotnet-trace.
- v2: `ConcurrentDictionary(sz → stack)` pool — `Monitor.Wait` lock contention at 5.55% of runtime (visible in trace 3 as `LowLevelLock` and `ConcurrentDictionary.GetOrAdd`).
- v3: fixed 64-bucket array — no dictionary, no Monitor; only lock-free `ConcurrentStack` push/pop.

Crash fix during pool development: top-level combine blocks (e.g. Level 16–17, 107 MB–268 MB) exceeded `POOL_MAX_BLOCK`, so `VirtualAlloc` returned NULL when committed memory hit ~38 GB from un-flushed pool blocks. Fixed by the `POOL_MAX_BLOCK` bypass and per-level `FlushGmpPool()`.

---

## Section 66 — P-Core Affinity Detection + Thread Pool Pre-Warm

**Branch:** AdvPerfWork

**Change:** Added `SetPCoreAffinity()` called from `BtnCompute_Click` before Phase 1:

- Calls `GetLogicalProcessorInformationEx(RelationProcessorCore, ...)` to enumerate all logical processor groups.
- Each `SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX` entry for a processor core exposes `EfficiencyClass` (0 = E-core, 1+ = P-core on Intel hybrid CPUs; on non-hybrid CPUs all cores report the same class).
- Accumulates a bitmask of all P-core logical processors; if the set is non-empty and not equal to all processors, calls `SetProcessAffinityMask` to restrict the process.
- Logs the detected counts and final affinity mask with an `[Affinity]` prefix.
- No-op on non-hybrid machines (all cores report the same `EfficiencyClass`).

Added `ThreadPool.SetMinThreads(ProcessorCount, ProcessorCount)` immediately before Phase 1 to pre-warm the .NET thread pool, eliminating the ramp-up latency for the first `Parallel.For` tasks.

**Why:** On Intel 12th-gen+ hybrid CPUs, Windows may schedule .NET thread pool threads onto E-cores. E-cores have lower single-thread IPC and share L3 differently, degrading GMP FFT performance which is memory-bandwidth-bound. Pinning to P-cores ensures consistent throughput. Thread pool pre-warming eliminates the ~100 ms first-task latency seen at Phase 1 start in trace runs.

---

## Section 88 — Raw DllImport in SafeMpzMul Slow Path; GmpRaw_add in Phase 2 (issues #25, #26)

**Branch:** PerfWork

### #25 — Math.Gmp.Native wrapper dispatch eliminated from SafeMpzMul slow path

Added five new raw `DllImport` declarations (`GmpRaw_init`, `GmpRaw_init2`, `GmpRaw_clear`, `GmpRaw_tdiv_r_2exp`, `GmpRaw_tdiv_q_2exp`) and replaced all `gmp_lib.*` calls in the `SafeMpzMul` slow path with direct P/Invoke.

The slow path calls the wrapper for B-piece splitting (4 init + 4 tdiv + 1 clear), A-piece init (3 × `mpz_init2` + 1 tdiv), 9 product inits, 9 product clears (inside the accumulation loop), and 7 final clears. Each wrapper call went through `Marshal.GetDelegateForFunctionPointerInternal` before every native call.

**Pattern:** Struct headers for locally-created objects allocated via `Marshal.AllocHGlobal(16)`; limb buffers filled by `GmpRaw_init`/`GmpRaw_init2` via `GmpAllocFunc` (same path as `gmp_lib.mpz_init`). Cleanup: `GmpRaw_clear` + `Marshal.FreeHGlobal`. Matches the existing `accumPtr` pattern already in the slow path (§42).

### #26 — GmpRaw_add for Phase 2 T-accumulation

Replaced `gmp_lib.mpz_add(tempA, tempA, tempB)` with `GmpRaw_add(tempA.Pointer, tempA.Pointer, tempB.Pointer)` in both the parallel and serial Phase 2 combine paths. `GmpRaw_add` was already declared (§42). One wrapper dispatch eliminated per pair at every Phase 2 level — 68,869 calls at level 1 alone.

The `newP`/`newQ`/`tempA`/`tempB` init and clear calls in Phase 2 are unchanged: their struct headers are allocated by `gmp_lib.mpz_init` and their lifetimes span levels (stored as `MemP`/`MemQ`/`MemT` in `DiskNode`), so switching ownership models there would require also changing `BinarySplitChunk` and `LoadNodeFromDisk`.

**1B-digit trace results (§88 vs §87):**

| Metric | §87 | §88 | Δ |
|--------|-----|-----|---|
| `SafeMpzMul` (excl) | 17.98% | 20.48% | +2.5 pp ¹ |
| `GmpAllocFunc` (excl) | 23.4% | 23.5% | flat |
| `GmpFreeFunc` (excl) | 22.42% | 22.42% | flat |
| `LowLevelLifoSemaphore` (excl) | 13.9% | 14.09% | flat |
| `gmp_lib.mpz_inits` (excl) | not in top 50 | 0.86% | Phase 2 remainder ² |
| `gmp_lib.mpz_clears` (excl) | 0.58% | 0.75% | Phase 2 remainder ² |
| Wall-clock | 27:47 | **27:28** | −19s |

¹ The +2.5 pp SafeMpzMul exclusive is a sampling redistribution: `Thread.Sleep` dropped −2.6 pp (poll threads ran fewer cycles in a slightly faster run). `GmpAllocFunc`/`GmpFreeFunc` are flat — the irreducible thunk crossing dominates; eliminating the delegate dispatch layered on top had no measurable effect at 1B digits because the slow path is only triggered at the top few levels where each multiply takes hundreds of milliseconds.

² `gmp_lib.mpz_inits`/`mpz_clears` now visible at 0.86%/0.75% exclusive are the Phase 2 `newP`/`newQ`/`tempA`/`tempB` wrapper calls intentionally left unchanged. Converting them requires a consistent ownership model from `BinarySplitChunk` through all Phase 2 levels — not worth the refactor risk. Issues #25 and #26 closed.

---

## Section 87 — Phase 2 Parallel Path: Remove Inner Parallel.Invoke (issue #24)

**Branch:** PerfWork

`Parallel.Invoke` appeared at 20.45% inclusive in the 1B-digit trace. It was called twice per pair inside the Phase 2 `Parallel.For` body — once for (newP, newQ) and once for (tempA, tempB) — creating and scheduling 2 thread-pool tasks per invocation × 2 invocations × pairCount = `4 × pairCount` task round-trips per level.

**Changes:**

- **Outer DOP raised** from `ProcessorCount \ 2` to `ProcessorCount`. The halved DOP existed only to leave free threads for the inner `Parallel.Invoke` tasks; removing `Parallel.Invoke` eliminates this constraint.
- **Inner `Parallel.Invoke` removed.** The 4 `SafeMpzMul` calls per pair now run sequentially within each outer task. Parallelism across pairs comes from the outer `Parallel.For`; parallelism within each `SafeMpzMul` comes from `_safeMulDop`.
- **`_safeMulDop` now scales with pairCount** (`= ProcessorCount \ pairCount`, minimum 1) so sub-product parallelism fills idle cores when the level has few large pairs:

| pairCount | `_safeMulDop` | Active threads |
|---|---|---|
| ≥ 24 | 1 | 24 (outer For saturated) |
| 8 | 3 | 8 × 3 = 24 |
| 4 | 6 | 4 × 6 = 24 |

No deadlock risk: outer tasks no longer block on `Parallel.Invoke`; the inner `Parallel.For` sub-tasks are short fast-path `GmpRaw_mul` calls with no further nesting. The serial path (pairCount < 4) is unchanged — it retains its `Parallel.Invoke` and uses `_safeMulDop = ProcessorCount` (restored after each parallel level).

**1B-digit trace results (§87 vs §86 baseline):**

| Metric | §86 | §87 | Δ |
|--------|-----|-----|---|
| `LowLevelLifoSemaphore.WaitForSignal` (excl) | 22.55% | 13.9% | −8.65 pp |
| `Parallel.Invoke` (incl) | 20.45% | not in top 50 | eliminated |
| `SafeMpzMul` (excl) | 19.24% | 17.98% | −1.26 pp |
| `GmpAllocFunc` (excl) | 17.6% | 23.4% | +5.8 pp ¹ |
| `GmpFreeFunc` (excl) | 16.49% | 22.43% | +5.94 pp ¹ |
| Wall-clock (1B digits) | ~28 min | 27:47 | marginal |

¹ `GmpAllocFunc`/`GmpFreeFunc` appear higher only because they're a larger fraction of what remains — absolute thunk cost is unchanged. The bottleneck has fully shifted to the managed→native P/Invoke crossing (~46% combined exclusive). Issue #24 closed.

---

## Section 86 — SafeMpzMul: Shared Shifted Buffer (issue #23)

**Branch:** PerfWork

The slow-path accumulation loop in `SafeMpzMul` (`Form1.vb` ~line 1809) previously called `VirtualAlloc` + `VirtualFree` for each of the 8 non-zero-shift k iterations — a total of 8 VirtualAlloc + 8 VirtualFree syscalls per slow-path call.

**Fix:** A single shared buffer is now pre-allocated before the loop, sized to the maximum any iteration could need (`≤ 3·mA + 3·mB + 4` limbs, where `mA = ⌈szA/3⌉`, `mB = ⌈szB/3⌉`). Each iteration resets only `_mp_size = 0` and reuses the same buffer; `GmpRaw_mul_2exp` writes in place since the pre-alloc is always sufficient. The shared buffer is freed once after the loop and `shifted` is reinitialised to a 1-limb stub so the subsequent `mpz_clears` call remains safe.

This reduces per-slow-path-call VirtualAlloc/VirtualFree count from 8+8 to 1+1 — saving 7 kernel round-trips each way.

---

## Section 85 — GMP Pool Allocator Hot-Path Optimisation (issues #20, #21, #22)

**Branch:** PerfWork

Three targeted fixes to the GMP limb-buffer pool, all motivated by the 1B-digit dotnet-trace report where `GmpAllocFunc` (18.08% exclusive) and `GmpFreeFunc` (17.25% exclusive) together accounted for **35% of total CPU time**.

### §20 — Replace O(n) ConcurrentStack.Count with atomic counter (issue #20)

`PoolReturn` previously called `_gmpPool(b).Count` to decide whether the pool was full. `ConcurrentStack(Of T).Count` is O(n) — it walks the entire linked list. With `POOL_CAP = 256`, every free could walk 256 nodes.

**Fix:** Added `_gmpPoolCount(POOL_BUCKETS - 1) As Integer` — one `Interlocked` counter per bucket. `PoolReturn` now increments atomically and rolls back if the cap is exceeded; `PoolGet` decrements on a successful pop; `FlushGmpPool` decrements as it drains each bucket.

### §21 — Remove Try/Catch from GmpAllocFunc / GmpFreeFunc / GmpReallocFunc (issue #21)

All three native GMP callbacks were wrapped in `Try/Catch`. The .NET JIT cannot inline through `Try/Catch` boundaries, and even in the non-exception path the handler frame adds overhead on every call. Since the corrupt-size and failed-alloc paths already return `IntPtr.Zero` (causing GMP to abort, which is the correct behaviour), the `Catch` clause added no real safety.

**Fix:** Removed the outer `Try/Catch` from all three functions.

### §22 — PoolBucket bit-counting loop → BitOperations.Log2 (issue #22)

`PoolBucket` used a managed `While` loop to count leading bits — up to 25 iterations for a typical 32 MB block. It is called on every alloc and free.

**Fix:** Replaced with `System.Numerics.BitOperations.Log2(CULng(sz - 1L)) + 1`, which the JIT lowers to a single `LZCNT`/`BSR` instruction on x64.

---

## Section 84 — Power-of-10 Test Suite (issue #18)

**Branch:** PerfWork

### `--output-dir D` CLI argument (Form1.vb)

Added `--output-dir D` to the exe's CLI arg parser. Sets `_outputDir` at startup, overriding the default `C:\PiOutput`. All derived paths (pi_digits.txt, pi_phase_log.txt, NodeCache) inherit the new directory. This allows multiple simultaneous or sequential runs to write to isolated directories.

### `-Test` switch (Run-PiCompute.ps1)

Runs the exe at every power of 10 from 10 up to `-Digits` (default 1,000,000,000). Each run writes to its own subdirectory (`test_10`, `test_100`, …) under `-OutputDir` via `--output-dir`.

**Verification pass/fail rules:**

| Sequence | Expected position | Checked when |
|---|---|---|
| `999999` | 762 | digits ≥ 768 |
| `777777777` | 24,658,601 | digits ≥ 24,658,610 |
| `27182818284` (e-digits) | unknown | always — informational only |

Runs with too few digits to contain a sequence report `N/A` (not a failure). The e-digits check never causes a FAIL — its position in Pi is not known to be within 1B digits.

**Output:** a combined timing and pass/fail table printed to the console and saved as `test_suite_report_YYYYMMDD_HHMMSS.txt` in `-OutputDir`.

```
.\Run-PiCompute.ps1 -Test                      # full suite, 10 → 1B
.\Run-PiCompute.ps1 -Test -Digits 1000000      # suite up to 1M only
```

### Bug fix: log pattern anchor (Run-PiCompute.ps1)

`WriteToLog` prefixes every log entry with a timestamp, thread ID, elapsed time, and RAM reading — so log lines read `2026-04-01 12:00:00 | T1 | ... | [Verify] Verify OK: ...`. The original `Select-String` pattern was `'^\[Verify\]'` (anchored to start of line), which never matched. Changed to `'\[Verify\]'` (unanchored) so the verify result is correctly parsed from any run's phase log.

---

## Section 83 — Runtime Logging Level (issue #15)

**Branch:** PerfWork

**Problem:** the logging detail level was controlled by `#Const LOGGING_DETAIL` — a compile-time constant requiring a rebuild to change. The three levels (0/1/2) were poorly defined and the existing level 1 was too coarse, mixing stage detail with full-trace diagnostics.

**Changes:**

### New 6-level runtime system

| Level | Name | What it logs |
|---|---|---|
| **0** | None | Errors and crashes only. Silent on success. |
| **1** | Performance *(default)* | `[PHASE]` markers with wall-clock timing — enough to reconstruct per-phase timing. |
| **2** | Stages | Level 1 + serialization file names/sizes, node digit-count summaries, initial calc steps (pow, sqrt, mul_ui), division description, `mpz_get_str` wall time. |
| **3** | Last stage | Level 2 + full per-operation trace for the final BinarySplitGMP combine level and all `ComputePiGMP` steps (§61 serial multiply call trace, combine A/B/C/D call trace, RAM snapshots). |
| **4** | Full trace | Level 3 + `SafeMpzMul` fast-path and slow-path diagnostics, accum pre-alloc confirmation, B-piece extraction details, `BinarySplitChunk` entry/exit on every call, bit-size predictions for combine steps. |
| **5** | Allocator | Level 4 + pool/affinity diagnostics (reserved for future use). |

### Implementation

- `#Const LOGGING_DETAIL` removed; all 74 `#If LOGGING_DETAIL` blocks converted to `If _logLevel >= N Then` runtime checks.
- `Private _logLevel As Integer = 1` field added (default: Performance).
- `--log-level N` CLI argument parsed in `Form1_Load`.
- **`NudLogLevel` spinner** (0–5, default 1) added to the control panel row 1; read into `_logLevel` when Start is clicked. Disabled in headless mode.
- Logging mode description in the phase log header updated to show the runtime level name.

### Run-PiCompute.ps1

- `-LogLevel` parameter added (default: 1); passed as `--log-level $LogLevel` to the exe in both normal and trace-mode runs.
- Level names documented in `.PARAMETER LogLevel`.

---

## Section 82 — Auto-Verify Checkbox; Verification Results in Status Bar (issue #12)

**Branch:** PerfWork

**Changes (closes issue #12 / issue #3):**

### UI changes
- **`BtnTest` renamed to "Verify Now"** for clarity.
- **`ChkAutoVerify` checkbox** ("Verify after compute") added to the control panel, defaulting to checked. Placed between `ChkboxWriteToFile` and "Verify Now" on the same row. `LblRamThreshold` and `NudRamThreshold` shifted right to make room.

### Verification results: status bar only — no modal dialogs
All verification results (built-in checks and `--verify-at` / `--verify-contains` custom checks) are now written to `LblStatus` and the phase log. **No `MessageBox.Show` is called** at any point during verification, in either interactive or headless mode. The status bar shows a compact summary:

```
Verify OK: 999999@762 OK | 777777777@24,658,601 OK | e-digits@{pos} OK
Verify: 999999 not found | 777777777@24,658,601 OK | e-digits not found
```

### Auto-verify triggers
- **After streaming completes** (`DisplayTimer_Tick` completion path): if `ChkAutoVerify.Checked`, `RunVerification()` is called automatically.
- **When display is off** (`StreamPiToScreen` fast path): same check — `RunVerification()` is called after `WriteResultToFile`.
- **Headless `--autoverify` flag**: calls `RunVerification()` directly (previously called `BtnTest_Click`).
- **"Verify Now" button**: calls `RunVerification()` on demand at any time.

### Code structure
Verify logic consolidated into a single `RunVerification()` sub (previously duplicated across `BtnTest_Click` and the headless path). `BtnTest_Click` is now a one-liner that calls `RunVerification()`. `RunCustomVerifications` updated to write to `LblStatus`/log instead of `MessageBox`.

---

## Section 81 — Display Streaming Performance Improvements (issue #16)

**Branch:** PerfWork

**Problem:** `DisplayTimer_Tick` called `Marshal.ReadByte` in a loop (one P/Invoke per byte), rebuilt a `StringBuilder` character by character, called `ScrollToCaret` every 500-char tick, and used a fixed chunk size of 500 chars — severely limiting throughput.

**Changes:**

1. **Bulk copy in native path** — `Marshal.ReadByte` loop replaced by a single `Marshal.Copy` into a pre-allocated `_displayBuf()` byte array, followed by `Encoding.ASCII.GetString`. One P/Invoke per tick instead of one per byte. `_displayBuf` is a Form-level field reused across all ticks (no per-tick allocation).

2. **Adaptive chunk size** — `_displayChunkSize` starts at 4,096 chars and is doubled each tick if the tick completes in under 60 ms, halved if it exceeds 90 ms, capped at 1,000,000. This converges quickly to the maximum throughput the machine can sustain without UI jank.

3. **Scroll throttle** — `ScrollToCaret` (which forces a layout pass) is now called at most once per 10,000 chars displayed instead of every tick, tracked by `_displayScrollAccum`.

4. **`mpz_get_str` wall-time logging** — a `Stopwatch` now wraps the `mpz_get_str` call; the elapsed time is written to the phase log so the conversion cost is visible alongside the compute phases.

5. **`_displayChunkSize` and `_displayScrollAccum` reset** in `StreamPiToScreen` so each new computation starts fresh.

---

## Section 79 — Fix Pool Corruption: Pre-Alloc Blocks Must Use PoolGet Not VirtualAlloc

**Branch:** PerfWork

**Root cause (confirmed by struct-field diagnostics):** Every pre-alloc block that stores a limb buffer directly into an `mpz_t._mp_d` field via `Marshal.WriteInt64` was allocated with `VirtualAlloc(_xBytes)`, giving exactly `_xBytes` bytes (page-rounded). When the `mpz_t` is later freed via `mpz_clear` → `GmpFreeFunc(ptr, _mp_alloc * 8 = _xBytes)` → `PoolReturn(ptr, _xBytes)`, the pool places the block in bucket `PoolBucket(_xBytes) = b`. The pool's invariant for bucket `b` is that all blocks are `1L << b` bytes — but `_xBytes ≤ 1L << b`. When a subsequent `GmpAllocFunc` request in the same bucket pops this block, it uses up to `1L << b` bytes from a block that only has `_xBytes` (page-rounded) bytes → buffer overrun → access violation in native GMP.

**Specific crash:** After `mpz_clear(tmpHigh)`, the `tmpHigh` pre-alloc block (`VirtualAlloc(571,736 bytes)` → only ~572 KB actual) enters bucket 20 (`1L << 20 = 1,048,576 bytes`). When `GmpRaw_mul` → `mpn_mul` → FFT internally calls `GmpAllocFunc` for a ~700 KB scratch buffer (bucket 20), it pops the 572 KB block and writes 700 KB to it → AV. This crash was consistent across all three SafeMpzMul fast-path calls for 1M digits.

**Fix:** Replace `VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_xBytes)), ...)` with `PoolGet(_xBytes)` at all pre-alloc sites. `PoolGet` allocates `1L << PoolBucket(_xBytes)` bytes — exactly the capacity the bucket assumes — so any subsequent pop of that block uses memory that was actually allocated.

**Sites fixed** (all in `Form1.vb`):
- `DeserializeOneMpz`: `limbs` buffer
- Three-pass multiply: `tmpHigh`, `mpQ1`, `mpQ2`, `mpR0`, `mpR1`, `mpR2`
- Combine A/B/C/D: `_bigBufA`, `_bigBufB`, `_bigBufC`, `_bigBufD`
- Pi quotient: `_bigBufPi`

**Why 1B-digit runs were unaffected:** For 1B digits, the pre-alloc sizes are hundreds of MB, which are above `POOL_MAX_BLOCK = 16 MB`. `PoolReturn` calls `VirtualFree` directly for oversized blocks (they never enter the pool), so there's no bucket-size assumption to violate.

## Section 78 — Fix SafeMpzMul Fast Path: Use Raw P/Invoke to Avoid Managed Wrapper Corruption

**Branch:** PerfWork

**Symptom:** App crashed at `[ComputePi] §61 calling r0 = N*Q0...` on 1M-digit runs. The granular §77 logging narrowed it to the very first `SafeMpzMul(mpR0, gmpNumer, finalQ)` call. Same 1B-digit run never crashed.

**Root cause:** `SafeMpzMul`'s fast path (`szA+szB ≤ SAFE_LIMB_THRESHOLD`) called `gmp_lib.mpz_mul(result, opA, opB)` — the Math.Gmp.Native managed wrapper. This wrapper is known to corrupt `mpz_t.Pointer` fields during native calls (the §42 root cause, which the slow path already fixes by using raw `IntPtr` accumulators). For 1B-digit runs, `szA+szB ≈ 87.6M > SAFE_LIMB_THRESHOLD`, so those runs always take the slow path (already safe). For 1M-digit runs, `szA+szB = 87,639 ≤ SAFE_LIMB_THRESHOLD`, so they take the fast path and hit the wrapper corruption — crashing inside the native `__gmpz_mul` call when it tries to write to a corrupted result pointer.

**Fix:** Added `GmpRaw_mul` P/Invoke declaration (`[DllImport("libgmp-10.dll", EntryPoint:="__gmpz_mul")]`) and replaced `gmp_lib.mpz_mul(result, opA, opB)` with `GmpRaw_mul(result.Pointer, opA.Pointer, opB.Pointer)` in the fast path. Passing the raw `IntPtr` values bypasses the managed wrapper entirely, so the wrapper never touches the `mpz_t` objects and cannot corrupt their Pointer fields.

**Why 1B-digit runs were unaffected:** The §61 multiply operand sizes for 1B digits (`szA ≈ 51.9M`, `szB ≈ 35.7M`, sum ≈ 87.6M) exceed `SAFE_LIMB_THRESHOLD = 33,554,431`, so the slow path (which already uses raw IntPtrs) was always taken. 1M-digit operand sizes (87,639 total) fall well below the threshold, exposing the fast path wrapper bug for the first time.

## Section 77 — Granular Per-Call Logging in §61 Multiply Block

**Branch:** PerfWork

**Change:** Added `WriteToLog` calls immediately before and after each of the six operations in the §61 serial multiply block (`Form1.vb` lines ~2779–2789):

```
[ComputePi] §61 calling r0 = N*Q0...
[ComputePi] §61 r0 done
[ComputePi] §61 calling r1 = N*Q1...
[ComputePi] §61 r1 done
[ComputePi] §61 calling r2 = N*Q2...
[ComputePi] §61 r2 done
[ComputePi] §61 calling mpz_clears(finalQ, mpQ1, mpQ2)...
[ComputePi] §61 clears done
[ComputePi] §61 calling mpz_swap(gmpNumer, mpR2)...
[ComputePi] §61 swap done
[ComputePi] §61 calling mpz_clear(mpR2)...
[ComputePi] §61 clear r2 done
```

These logs are unconditional (not guarded by `LOGGING_DETAIL`) so they appear regardless of build configuration.

**Why:** After the §76 fix, the app still crashes at the same log point on small (1M-digit) runs. The crash occurs somewhere in the six-operation block but without per-call markers it is impossible to tell which operation is responsible. The new markers will appear in the log before whichever operation crashes, pinpointing the exact call.

## Section 76 — Fix Three-Pass Multiply Pre-Alloc Crash on Small Digit Counts

**Branch:** PerfWork

**Symptom:** App crashed immediately after `[ComputePi] §61 serial multiply start` log entry on any run with a small digit count (e.g. 1,000,000 digits). No log entry from the native crash handler.

**Root cause:** The pre-alloc blocks for `tmpHigh`, `mpQ1`, `mpQ2`, `mpR0`, `mpR1`, and `mpR2` unconditionally replaced the `mpz_init2` limb buffer (524 KB, VirtualAlloc'd) with a smaller VirtualAlloc'd buffer sized to the actual result. For small digit counts, this replacement buffer was below `GMP_LARGE_THRESHOLD` (524,288 bytes). For example with 1M digits: `mpQ1` result = 35,733 limbs × 8 = 285,864 bytes. When `mpz_clears` later freed these objects, `GmpFreeFunc` saw `_mp_alloc * 8 < GMP_LARGE_THRESHOLD` and routed to `_savedGmpFree` (CRT `free()`). Calling CRT `free()` on a VirtualAlloc'd pointer is undefined behaviour; in .NET Core it terminates the process immediately without firing managed exception handlers or `SetUnhandledExceptionFilter`.

**Fix:** Each pre-alloc block now guards the buffer replacement with `If _xBytes >= GMP_LARGE_THRESHOLD`. Below the threshold the `mpz_init2` buffer (65,536 limbs = 524,288 bytes, VirtualAlloc'd) is already large enough for the result — no replacement is needed and `GmpFreeFunc` correctly routes the free through `VirtualFree`. Above the threshold the behaviour is unchanged.

**Why the 1B-digit run was unaffected:** All six buffers are hundreds of MB for a 1B-digit run, well above `GMP_LARGE_THRESHOLD`, so the guard condition is always true and the code path is identical to before.

## Section 75 — Button Text Centering, Uniform Size, and Equal Row Spacing

**Branch:** PerfWork

**Change:** Three related UI polish fixes in `Form1.Designer.vb`:

**1. Button text alignment**
`TextAlign = ContentAlignment.MiddleCenter` set explicitly on `BtnCompute`, `BtnPause`, and `BtnTest`.

**2. Uniform button size**
`BtnTest` width corrected from 112 px to 134 px, matching `BtnCompute` and `BtnPause` (all now 134×47).

**3. Equal row spacing**
Panel1 height reduced from 319 px to 205 px. The three control rows are now evenly spaced with 12 px top/bottom margins and 20 px gaps between rows:
- Row 1 (Start / Cancel / Digits / Displayed / Running Time): Y = 12
- Row 2 (Display / Write to File / Test / RAM Threshold): Y = 79
- Row 3 (Status bar): Y = 146

Non-button controls within each row are vertically centred relative to the 47 px row height. `LstBoxPhases` height reduced from 279 px to 181 px to match the new panel height. `RtbPiDigits` design-time origin updated from Y=319 to Y=205 (runtime docking overrides this, but kept consistent for the designer).

**Why:** `BtnTest` was 22 px narrower than the other buttons, and its text had unequal left/right margins. The gaps between rows were 16 px (rows 1→2) and 35 px (rows 2→3), making the header panel look uneven.

## Section 74 — Revert Output Directory to C:\PiOutput

**Branch:** PerfWork

**Change:** `_outputDir` in `Form1.vb` and the `-OutputDir` default in `Run-PiCompute.ps1` both changed from the portable `%LOCALAPPDATA%\PI-BillionDigits` / `.\PiOutput` paths (introduced in §71) back to the fixed `C:\PiOutput`.

Both output files derive from `_outputDir`:
- `pi_digits.txt` → `C:\PiOutput\pi_digits.txt`
- `pi_phase_log.txt` → `C:\PiOutput\pi_phase_log.txt`

The directory is auto-created at compute time if it does not exist (via `Directory.CreateDirectory` in `StreamPiToScreen`). The script also creates it if missing before launching the exe.

**Why:** The machine used for production runs always has `C:\PiOutput` available and the user prefers output there. The script's `.\PiOutput` default was resolving relative to the source tree, so output appeared under the project folder rather than `C:\PiOutput`.

## Section 67 — `--verify-at` and `--verify-contains` CLI Options

**Branch:** AdvPerfWork

**Change:** Added two repeatable CLI arguments parsed in `Form1_Load`:

- `--verify-at "DIGITS:POSITION"` — after computation, checks that the digit sequence `DIGITS` appears at exactly position `POSITION` (0-based, including the leading `3`). Multiple `--verify-at` arguments are all checked.
- `--verify-contains "DIGITS"` — checks that `DIGITS` appears anywhere in the output. Multiple `--verify-contains` arguments are all checked.

Both options populate `_verifyAt As List(Of Tuple(Of String, Long))` and `_verifyContains As List(Of String)` at startup. The new helper `RunCustomVerifications(piText As String)` is called at the end of `BtnTest_Click` (after the built-in fixed digit checks) when either list is non-empty.

Results in interactive mode: `MessageBox.Show`. In headless mode: written to the phase log with a `[DIALOG]` prefix and `LblStatus.Text` is updated.

**Example usage:**
```
PI-BillionDigits.exe --digits 1000000000 --autostart --autoverify ^
    --verify-at "999999:762" ^
    --verify-contains "27182818284"
```

**Why:** The built-in test checks three fixed digit sequences hardcoded in `BtnTest_Click`. For automated regression testing or validating known Pi digit positions, it is useful to pass expected values on the command line without modifying the source. The `--verify-at` form gives an exact-position assertion (fails if the sequence is found elsewhere); `--verify-contains` is a looser sanity check useful for sequences like Euler's number embedded in Pi.
