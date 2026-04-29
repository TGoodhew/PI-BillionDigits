# PI-BillionDigits

## What it is

PI-BillionDigits is a Windows Forms application that computes Pi to an arbitrary number of decimal digits — up to and including one billion — and displays the result. It is written in VB.NET targeting .NET 10 and uses the [Math.Gmp.Native](https://www.nuget.org/packages/Math.Gmp.Native.NET/) wrapper around the GNU Multiple Precision Arithmetic Library (GMP) for all big-integer arithmetic.

## UPDATE: Tested up to 5 billion digits

The application has been extended beyond its original 1-billion-digit target and successfully tested at **5,000,000,000 decimal digits** of Pi.

### What changed

The original implementation hit three 32-bit integer limits that are not reached at 1 billion digits but are exceeded at 5 billion:

**1. `mpz_ui_pow_ui` argument overflow**
Computing `10^N` for the final decimal scaling calls `mpz_ui_pow_ui(result, 10, N)`. GMP's `mp_bitcnt_t` is a 32-bit `unsigned long` on Windows (MSVC LLP64), so `N` is silently truncated when `N > 4,294,967,295`. At 5 billion digits this wraps to 705,032,704, producing the wrong scale factor. Fix: split into two halves (`mpz_ui_pow_ui(a, 10, N/2)` × `mpz_ui_pow_ui(b, 10, N-N/2)`) and combine with `SafeMpzMul`.

**2. Shift-accumulation overflow in `SafeMpzMul`**
During the 3×3 sub-product accumulation loop, each sub-product is shifted left by up to `2×bitsA + 2×bitsB` bits before being added to the accumulator. At 5 billion digits the maximum shift reaches ~22 billion bits — roughly 5× UInt32.MaxValue. The previous two-step split (`shift/2` twice) still overflowed because each half is ~11 billion bits. Fix: a chunk-based loop that applies at most `UInt32.MaxValue` bits per `mul_2exp` call, repeating until the full shift is applied.

**3. Piece-extraction overflow in `SafeMpzMul`**
The 3-way operand split extracts three sub-pieces (A0, A1, A2 and B0, B1, B2) from each input. The previous code used `mpz_init2(piece, bitsA)` to pre-allocate the piece buffer and `mpz_tdiv_r_2exp(piece, op, bitsA)` to extract the limbs — both of which take a 32-bit bit count on Windows. At 5 billion digits `bitsA ≈ 5.5 billion > UInt32.MaxValue`, so `init2` allocated far too few limbs and `tdiv` extracted the wrong range. Fix: **zero-copy limb windows** — each piece struct header is wired to point directly into the source operand's existing limb array at the correct offset, with no allocation and no data copying. This is safe because GMP only reads the piece values as multiplication inputs and never writes to them.

### Performance impact of the zero-copy change

The zero-copy piece extraction is also a **performance improvement at 1 billion digits**, because it eliminates:
- Two `CopyMemory` calls of ~230 MB each per top-level `SafeMpzMul` entry (the old A1/A2 extraction copies)
- Six `GmpRaw_init`/`GmpRaw_init2` calls and their matching allocations/frees
- Four `mpz_tdiv_r/q_2exp` calls

Profiling (dotnet-trace topN) shows `SafeMpzMul` exclusive time dropped from **17.98% → 14.82%** at 1 billion digits.

### How to run at 5 billion digits

The `Run-PiCompute.ps1` script accepts a `-Threshold` parameter that overrides the RAM/disk threshold. At 5 billion digits the number of Phase 1 chunks (~688,000) exceeds the default threshold; pass a large value to keep everything in RAM and avoid disk I/O during the combine phase.

**Requirements:** ~25–30 GB available RAM, 64-bit Windows, .NET 10.

Use `-AutoCheckpoint` so that if the run is interrupted (the combine phase takes several hours), the next run automatically resumes from the last completed level rather than starting over:

```powershell
.\Run-PiCompute.ps1 -Digits 5000000000 -Threshold 1000000 -AutoCheckpoint -LogLevel 2
```

The run takes several hours. Progress is written to `C:\PiOutput\pi_phase_log.txt` as it proceeds. If interrupted, re-run the same command — it will detect the latest snapshot in `C:\PiOutput\NodeCache\snap_L{N}\` and resume from there automatically.

---

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
| **Test** button | Searches the computed digits for three known substrings and reports whether they appear at the correct positions: `999999` (expected at position 762, the Feynman point), `777777777` (expected at position 24,658,601), and `999999999` (nine 9s, expected at position 564,665,206). Searches the full native buffer when available, otherwise searches the display text box. |
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

**`Run-PiCompute.ps1` (§63, §70, §94):** PowerShell script that clean-builds and launches the exe. Machine-independent: the exe path is auto-detected by globbing `bin\Release\**\PI-BillionDigits.exe` after the build (no hardcoded TFM folder), and the output directory defaults to `C:\PiOutput` (overridable via `-OutputDir`). Parameters: `-Digits N` (default 1B), `-OutputDir <path>`, `-Threshold N`, `-CheckpointFromLevel N`, `-ResumeFromLevel N`, `-AutoCheckpoint`, `-Trace`, `-ReportOnly <path>`.

**Quick start:**
```powershell
# Standard 1B run
.\Run-PiCompute.ps1

# Custom digit count and output location
.\Run-PiCompute.ps1 -Digits 100000000 -OutputDir "D:\PiResults"

# 5B run with auto-checkpoint (resumes automatically if interrupted)
.\Run-PiCompute.ps1 -Digits 5000000000 -Threshold 1000000 -AutoCheckpoint -LogLevel 2

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

---

## Section 94 — Level-Boundary Auto-Checkpoint / Resume

**Branch:** PerfWork

**Problem:** Phase 2 combine levels for 5B+ digit runs take hours each. If the run is interrupted (crash, user abort, power loss) all progress is lost and the run must restart from scratch.

**Previous approach (§93):** `--checkpoint-from-level N` forced disk serialization of every combine result as it was produced, changing that level from RAM mode to disk mode. This added per-pair I/O into the hot path and required manually specifying `--resume-from-level N` on the next run.

**New approach (`--auto-checkpoint`):** All combine work continues to run entirely in RAM — the hot path is unchanged. At the *end* of each Phase 2 level, after `diskNodes = nextDiskNodes` and while all nodes are still live, the completed node list is written to disk as a batch snapshot. On the next run with `--auto-checkpoint`, the highest valid snapshot is detected automatically and Phase 1 plus all completed levels are skipped.

### Snapshot layout

```
C:\PiOutput\NodeCache\
  snap_L3\           ← snapshot written after level 3 finishes
    N0.bin
    N1.bin
    ...
    meta.txt         ← written last; its presence marks the snapshot complete
  snap_L2\           ← deleted once snap_L3 is confirmed written
```

Node files are written in parallel via `Parallel.For`. For in-memory nodes, `SerializeNodeToDisk` is called directly against the live GMP objects. For disk-mode nodes (rare; only when `--checkpoint-from-level` is also active), `File.Copy` is used — no GMP interpretation needed. Only the most recent level's snapshot is kept; the previous level's directory is deleted after the new one is confirmed complete.

`meta.txt` records `digits`, `numTerms`, `numChunks`, `level`, `nodeCount`, and `timestamp`. On resume, `digits` and `numChunks` are validated against the current run parameters — a mismatch (different digit count) causes the snapshot to be silently skipped and a fresh run to start.

### New methods

- `WriteLevelSnapshot(level, nodes, numTerms, numChunks)` — parallel batch write; non-fatal on error (logs warning, computation continues).
- `TryFindBestSnapshot(numChunks)` — scans `NodeCache\snap_L*\` directories, validates metadata and node file presence, returns highest valid level number.
- `DeleteSnapshotDir(level)` — removes a snapshot directory after the next level's snapshot is confirmed.

### Resume path

Snapshot nodes are loaded as `IsInMemory = False` with `FilePath` pointing at `snap_L{N}\N{idx}.bin`. The existing Phase 2 combine loop loads them on demand via `LoadNodeFromDisk` — no changes to combine logic. The existing `--resume-from-level` path is preserved and takes priority over auto-detect when specified explicitly.

### Usage

```powershell
# Use on every 5B run — interrupted runs resume automatically
.\Run-PiCompute.ps1 -Digits 5000000000 -Threshold 1000000 -AutoCheckpoint -LogLevel 2
```

If the run is interrupted at any point during Phase 2, re-run the same command. It finds the highest complete `snap_L{N}` snapshot and resumes from level N+1, skipping Phase 1 and all levels up to N.

**Snapshot write cost:** writing ~6 GB of Level 3 nodes to NVMe takes roughly 6 seconds — negligible relative to the hours of combine time per level.

**Why not §93?** The §93 approach trades RAM performance for recoverability at every pair, making the affected level significantly slower. §94 pays the snapshot cost only once per level, after all the work is already done.

---

## §100 — SafeMpzSqrt, SafeMpzDiv, SafeMpzReciprocal, BigShiftRight, BigShiftLeft

### Problem

At 5 billion digits the `mpz_sqrt` call in `ComputePi` (Step 4) crashes because the input is 10^10,000,000,005 — approximately 519 million GMP limbs. GMP's `mpn_mul_fft` uses a static lookup table indexed by `mpn_fft_best_k`, and the table overflows beyond ~33.5 million limbs (`SAFE_LIMB_THRESHOLD = 33_554_431`). The same class of overflow previously caused crashes in `mpz_mul` (fixed via `SafeMpzMul` in §17–45) and `mpz_ui_pow_ui` (fixed via `SafeMpzPow10` in §99).

### Solution

Replace the `gmp_lib.mpz_sqrt(gmpSqrt, gmpSqrtInput)` call with `SafeMpzSqrt(gmpSqrt, gmpSqrtInput)`.

Five helper routines were added:

**`BigShiftRight(rop, op, bits As Long)`** — right-shifts `op` by an arbitrary number of bits (including values exceeding `UInt32.Max` ≈ 4.3 billion). Splits the shift into chunks of at most 2,100,000,000 bits, calling the raw GMP P/Invoke `GmpRaw_tdiv_q_2exp` each time. `rop` may alias `op`.

**`BigShiftLeft(rop, op, bits As Long)`** — same for left shift, using `GmpRaw_mul_2exp`.

**`SafeMpzReciprocal(r, b, kBits As Long)`** — computes `floor(2^kBits / b)` using Newton iteration with progressive precision. Seeds from the top 64 bits of `b`, then doubles working precision from ~62 bits to `rBits+2` bits. At each step, `b` is ceiling-truncated (ensuring `r` stays a strict underestimate throughout). Large multiplications within the loop use `SafeMpzMul` or `GmpRaw_mul` depending on operand size.

**`SafeMpzDiv(q, a, b)`** — computes `floor(a / b)` using Barrett reduction. For operand sizes within the safe threshold it calls `mpz_tdiv_q` directly. For larger operands: computes the Newton reciprocal via `SafeMpzReciprocal`, forms `q ≈ a·r / 2^kBits` via `SafeMpzMul`, then adjusts by ±1 until `0 ≤ remainder < b`.

**`SafeMpzSqrt(result, n)`** — computes `floor(sqrt(n))` using Newton iteration. For inputs within the safe threshold it calls `mpz_sqrt` directly. For larger inputs:
- Seeds by right-shifting `n` by `seedShift` bits (even, chosen so the shifted value has ≤ 700M bits), computing `mpz_sqrt` of the seed (safe, ≤ 5.5M limbs), then left-shifting the result back.
- Refines via Newton: at each step doubles the working precision from `SEED_BITS` to `bitsS+2` bits. Uses `BigShiftRight`/`BigShiftLeft` to keep operands at the target precision, and `SafeMpzDiv` (or `mpz_tdiv_q` when safe) for the division step.
- Final adjustment: verifies `x² ≤ n < (x+1)²`, correcting by ±1 if needed (at most 1 correction expected).

### Usage

`SafeMpzSqrt` is a drop-in replacement for `gmp_lib.mpz_sqrt`. It is called only once per PI computation (Step 4 of `ComputePi`). For the 5B-digit run the Newton refinement performs approximately 6 full-precision steps.

### Why not use GMP's integer square root directly?

GMP's `mpz_sqrt` internally calls `mpn_sqrtrem` which calls `mpn_mul_fft` for the Schönhage-Strassen squarings used in Newton refinement — and those calls read from the same static table that overflows. There is no GMP API to compute a large integer square root without triggering this path.

## §101 — PreAllocMpzToLimbs: bypass GMP S→L realloc crash in BigShiftRight

### Problem

`BigShiftRight`'s first chunk called `GmpRaw_tdiv_q_2exp` on a freshly `mpz_init`'d destination with only a 1-limb CRT-allocated buffer (~8 bytes). GMP's `_mpz_realloc` was needed to grow this to ~3.9 GB (486M limbs), but `_mpz_realloc` has a hard overflow check: it aborts with `gmp_die("mpz_realloc: overflow")` whenever `new_alloc > INT_MAX / GMP_NUMB_BITS = 33,554,431 limbs`. This abort fires *before* our `GmpReallocFunc` callback is reached. Result: silent crash in native code.

### Solution

`PreAllocMpzToLimbs(m, neededLimbs)`: directly replaces the mpz_t's limb buffer via struct manipulation (the same pattern used for `tmpHigh`, `mpQ1`, `mpQ2` in `ComputePiGMP`). Obtains a pool/VirtualAlloc block of the required size, copies any existing limb data (for aliased calls), frees the old buffer, and writes the new pointer and alloc count directly into the mpz_t header. Called at the start of `BigShiftRight` before the first `GmpRaw_tdiv_q_2exp` chunk.

## §102 — BigShiftLeft first-chunk pre-alloc (partial fix)

Applied the same `PreAllocMpzToLimbs` to the first chunk of `BigShiftLeft` (aliased `BigShiftLeft(x, x, ...)` case). This fixed the S→L transition for the seed shift-back in `SafeMpzSqrt`. However, subsequent chunks still crashed — see §105.

## §103 — snap_Phase3 checkpoint: skip Phase 1/2 on Phase 3 crash

### Problem

Phase 3 crashes (Steps 1–9 of `ComputePiGMP`) cost 10+ hours of Phase 1/2 re-work because snap_L* node files are deleted when Phase 2 completes (nodes are loaded into memory and disk files removed). By the time a crash is diagnosed and fixed, no valid checkpoint exists.

### Solution

- `SavePhase3Snapshot(snapDir, digits, numTerms, finalP, finalQ, finalT)`: serialises the three large integers to `NodeCache/snap_Phase3/` (P.bin, Q.bin, T.bin, meta.txt) immediately after Phase 2 finishes, before any Phase 3 operation begins.
- `TryLoadPhase3Snapshot(snapDir, digits, outP, outQ, outT)`: on startup (when `--auto-checkpoint`), checks for `snap_Phase3`, validates `digits` match, deserialises P/Q/T via the existing `DeserializeOneMpz` path, and returns True to skip `BinarySplitGMP` entirely.
- `GoTo Phase3Start` label added in `ComputePiGMP` at the Phase 3 entry point.
- `Run-PiCompute.ps1`: `Invoke-CheckpointRestore` called before each run to copy `snap_Phase3`/`snap_L*` from SnapshotStore → NodeCache; `Invoke-CheckpointBackup` extended to include `snap_Phase3`.

## §104 — Immediate SnapshotStore backup after every snapshot write

### Problem

The end-of-run script backup was too late: Phase 2 deletes snap_L* `.bin` files when loading nodes for the final combine. The backup saw only empty directories.

### Solution

`BackupSnapshotToStore(snapName)` and `DeleteSnapshotFromStore(level)`: called immediately inside `WriteLevelSnapshot` (after each successful write) and inside `SavePhase3Snapshot`. SnapshotStore is now updated in real-time — the backup reflects the current computation state regardless of when the run exits or crashes. The script `Invoke-CheckpointRestore` at run-start closes the loop: the next run always begins with the latest protected checkpoint.

## §105 — BigShiftLeft full pre-alloc: fix all chunks, not just the first

### Problem

§102 pre-allocated `rop` to accommodate only the first 2.1B-bit chunk. Every subsequent chunk grows `rop` further; once the intermediate result exceeds 33,554,431 limbs, GMP's `_mpz_realloc` overflow abort fires (same mechanism as §101). For the seed shift-back in `SafeMpzSqrt` — `BigShiftLeft(x, x, 16,259,640,482 bits)` — there are ~8 chunks; chunks 2–8 all grew past the 33M-limb limit.

### Solution

Change `BigShiftLeft` to pre-allocate `rop` to the **full final result size** (`opLimbs + (bits + 63) / 64 + 1`) before any chunk executes. Every chunk then finds `_mp_alloc ≥ needed` and `MPZ_REALLOC` short-circuits without ever calling `_mpz_realloc`. Existing limb data is copied by `PreAllocMpzToLimbs` before the old buffer is freed, making the aliased case safe.

## §106 — Affinity watchdog (#33), Phase 3 parallelism gaps (#34), Newton + Phase 3 checkpointing

### #33: P-core affinity watchdog

New GMP and .NET runtime threads created after the initial `SetPCoreAffinity` call were landing on E-cores, causing them to run at low frequency. The fix adds an `AffinityWatchdog` background thread that polls every 500 ms, enumerates all threads in the current process via `OpenThread` / `SetThreadAffinityMask`, and re-applies the P-core affinity mask to any thread that has drifted. `_pCoreMask` (Shared Long) stores the mask set by `SetPCoreAffinity`. The watchdog is started on form load (after `SetPCoreAffinity`) and cancelled on form close. On non-hybrid machines (`_pCoreMask = 0`) the watchdog exits immediately without polling.

DllImports added: `OpenThread`, `SetThreadAffinityMask`, `CloseHandle` (kernel32.dll).

### #34: Phase 3 parallelism gaps

**Gap 1 — R0/R1/R2 parallel multiplies with checkpoints:** `SafeMpzMul(mpR0, gmpNumer, finalQ)`, `SafeMpzMul(mpR1, gmpNumer, mpQ1)`, `SafeMpzMul(mpR2, gmpNumer, mpQ2)` now run concurrently via `Parallel.Invoke`. Before launching, each result is checked against a Phase 3 checkpoint (`snap_Phase3/{mpR0,mpR1,mpR2}.bin`); if all three are present the multiplies are skipped entirely. After completion, each result is saved immediately.

**Gap 2 — `_safeMulDop` ceiling division:** `_safeMulDop` was computed as `Floor(ProcessorCount / pairCount)`, starving inner parallelism at levels where `pairCount` doesn't divide evenly. Changed to ceiling division: `_rawDop = ProcessorCount / pairCount`; if `_rawDop >= 1.5` use `Ceiling`, else 1.

**Gap 3 — Lock-free Phase 1 chunk writes:** Phase 1's per-chunk `SyncLock` on `FileStream` was a serialisation bottleneck. Replaced with `RandomAccess.Write` (positional, no seek/lock) backed by a `SafeFileHandle` opened with `FileOptions.Asynchronous Or FileOptions.WriteThrough`. File offsets are allocated lock-free via `Interlocked.Add`.

**Gap 4 — Level-aware outer DOP:** The outer `Parallel.For` over pairs was capped at `ProcessorCount` even at low levels where `pairCount < ProcessorCount`. Added `_outerDop = Min(ProcessorCount, pairCount)` so the scheduler isn't given more parallelism slots than there are work items.

**Gap 5 — Final divide uses `mpz_tdiv_q` not `SafeMpzDiv` (NOT YET FIXED):** `gmp_lib.mpz_tdiv_q(gmpPi, gmpNumer, finalT)` at the end of `ComputePiGMP` uses native GMP. At 5B digits `gmpNumer` ≈ 5.2 billion limbs and `finalT` ≈ 2.6 billion limbs — both vastly exceed the 33M-limb threshold at which GMP's internal `mpn_mul_fft` overflows. This is the same crash class that broke `mpz_sqrt` and `mpz_mul`. Fix: replace with `SafeMpzDiv(gmpPi, gmpNumer, finalT)`.

**Gap 6 — `_safeMulDop` not reset at `Phase3Start` (NOT YET FIXED):** When Phase 2 runs to completion and its last level takes the serial path (always true at 5B digits, where final levels have `pairCount < 4`), `_safeMulDop` is left at 3. There is no reset at `Phase3Start`. Phase 3's single-threaded callers — `SafeMpzPow10`, the Step 2 squaring, and `SafeMpzSqrt`'s internal `SafeMpzMul` calls — therefore run at DOP=3 instead of DOP=24, using only ~9 of 24 cores. Fix: add `Volatile.Write(_safeMulDop, -1)` at `Phase3Start` (−1 is read as `ProcessorCount` inside `SafeMpzMul`). Note: does not affect runs that load from `snap_Phase3` directly (Phase 2 never ran, so `_safeMulDop` stays at its initial −1).

### Newton step checkpointing (SafeMpzSqrt)

Each of the 6 Newton refinement steps in `SafeMpzSqrt` now saves a checkpoint immediately after completing: `snap_Phase3/sqrt_newton.bin` (serialized `x`) + `sqrt_newton_meta.txt` (`bitsN`, `kBitsX`, `step`). On entry, if a matching checkpoint exists (`bitsN` matches and `kBitsX > SEED_BITS`), `x` is deserialized and the loop resumes at the saved step. After each save, `BackupSnapshotToStore("snap_Phase3")` is called immediately.

`BackupSnapshotToStore`, `SerializeOneMpz`, `DeserializeOneMpz` promoted to `Shared` so they can be called from the `Shared` `SafeMpzSqrt`. `_autoCheckpoint` promoted to `Shared` for the same reason.

### Phase 3 intermediate checkpoints (gmpNumer, R0/R1/R2, finalT)

`SavePhase3Value(name, val, dir)` / `TryLoadPhase3Value(name, val, dir)` helpers write/read a single `mpz_t` to `snap_Phase3/{name}.bin` and back up snap_Phase3 immediately.

Checkpoints added:
- **gmpNumer** — saved after Combine D (the most expensive intermediate; avoids re-running Steps 1–5 on divide crash)
- **mpR0, mpR1, mpR2** — saved after parallel multiply; loaded at Phase3Start to skip all three multiplies
- **finalT** — saved alongside gmpNumer so the divide can resume without reloading from snap_Phase3/T.bin

At `Phase3Start`, if `gmpNumer` is found in the checkpoint, the code jumps to `NumeratorDone:` (past Steps 1–5, the sqrt, all three R multiplies, and Combine A–D), loading `finalT` from checkpoint or falling back to `snap_Phase3/T.bin`.

**Known cosmetic inaccuracy:** The log line `"finalT reloaded from spill file"` at `NumeratorDone:` is printed on both the normal path (where finalT really was loaded from spill) and the `GoTo NumeratorDone` checkpoint path (where finalT was loaded from `snap_Phase3/finalT.bin`). Not a correctness issue.

## §107 — SafeMpzReciprocal: Newton iteration guard and floor truncation

### Problem

`SafeMpzReciprocal` uses Newton's method to compute `r ≈ 2^kBits / b`. The inner loop iterates `r ← 2r − bTrunc·r² / 2^(kBits−bShift)` where `bTrunc` is a truncated version of `b` and `bShift = max(0, bBits − prec − 2)`.

The previous implementation used *ceiling* truncation (`bTrunc = floor(b / 2^bShift) + 1`) to keep `p` as an overestimate of `b·r² / 2^kBits`, thereby guaranteeing `r_new = 2r − p ≤ R` (where `R = 2^kBits / b`). A guard block reset `r = 1, prec = 1` whenever `r_new ≤ 0`.

When called from `SafeMpzDiv` during `SafeMpzSqrt`'s Newton step 2 (for a 1B-digit run), the ceiling excess `r² / 2^(kBits−bShift)` for the penultimate iteration was found to cause `r_new` to go negative. The guard fired, reset `r = 1`, and the subsequent restart iterations (doubling from prec=1) converged to a grossly wrong value (~3 limbs instead of ~21.875M limbs). `SafeMpzDiv` then tried to compute `q ≈ a·r / 2^kBits` with this tiny `r`, producing `q ≈ 2^26` instead of the correct quotient `≈ 2^1.4B`. The adjustment loop inside `SafeMpzDiv` then ran for effectively infinite iterations (≈2^1.4B subtractions at 1 core) until killed.

### Gap 5: Final divide (SafeMpzDiv)

`gmp_lib.mpz_tdiv_q(gmpPi, gmpNumer, finalT)` replaced with `SafeMpzDiv(gmpPi, gmpNumer, finalT)` to avoid the `mpn_mul_fft` overflow at 5B+ limbs.

### Gap 6: `_safeMulDop` reset at Phase3Start

Added `System.Threading.Volatile.Write(_safeMulDop, -1)` at `Phase3Start` so Phase 3's single-threaded callers (`SafeMpzPow10`, Step 2 squaring, `SafeMpzSqrt`) use all available cores instead of the DOP=3 left over from Phase 2's serial path.

### Gap 7: Floor truncation fix (in progress)

Removed the `+1` ceiling from `bTrunc` computation in `SafeMpzReciprocal`. With floor truncation, `p ≤ b·r²/2^kBits` so `r_new = 2r−p ≥ r·(2−r/R) ≥ 0` for any `r ≤ R`. The guard is retained as a safety net but should not fire in normal operation.

**Status:** The guard is still firing (log shows `[PreAlloc] 67,586 limbs` restart sequence immediately following the `21,875,002` limb entry). Diagnostic logging (writing to `C:\PiOutput\guard_debug.txt`) has been added to the guard block to capture exact `prec`, `bShift`, and operand sizes when it fires, enabling identification of which iteration is the root cause.

## §108 — SafeMpzDiv: dense diagnostic logging + adj-loop safety abort

### Problem

With floor truncation in `SafeMpzReciprocal`, the Newton iterations all converge successfully (25 iterations, `szR=21,875,001`). However `SafeMpzDiv`'s adjustment loop (which corrects the Barrett-reduced quotient by ±1 until `0 ≤ remainder < b`) was hanging indefinitely — observed as a 20+ hour silent stall.

Post-mortem analysis showed `q_approx = floor(a·r / 2^kBits)` was catastrophically low: only the top 970,336 limbs were non-zero while the bottom 20,904,665 limbs were effectively zero. This made `remainder = a − q·b` have ~42,779,665 limbs (≈ b-sized), requiring ~2^1.34B adj-up iterations.

### Fix

Added `MAX_ADJ_ITERS = 10` safety abort to both the adj-down and adj-up loops inside `SafeMpzDiv`. When exceeded, a `InvalidOperationException` is thrown with full context (`szA`, `szB`, `aBits`, `kBits`, `szR`, `szQ`, `szQB`, `szRem`). This converts a multi-hour hang into a 3-minute crash with a clear diagnostic message.

Also added dense `_logLevel >= 2` logging throughout `SafeMpzDiv`: entry parameters, `a*r` result size, shift size, q top-2 limbs, `q*b` result size, remainder sign/size/top-limb, and each adj-up/adj-down iteration.

### Status

The run now terminates in ~3 minutes with the exception. Root cause (zero lower limbs in `q_approx`) is confirmed but not yet fixed.

## §109 — SafeMpzMul general-path per-sub-product diagnostics + q bottom-limb logging

### Problem

`q_approx` from `SafeMpzDiv` has only its top ~970K limbs non-zero and ~20.9M zero bottom limbs. The zero-lower-limbs hypothesis is: if `q`'s lower 20,904,665 limbs are zero, then `remainder = a − q·b` has `20,904,665 + 21,875,001 − 1 = 42,779,665` limbs — exactly matching the observed `szRem=42,779,665`.

`q` is produced by `SafeMpzMul(ar, a, r)` followed by `BigShiftRight(ar, ar, kBits)`. Since `szA=43,750,001` and `szR=21,875,001`, we have `mA ≠ mB` so the **general accumulation path** is used (not the §39 column path). That path loops `k=0..8`, shifts each of the 9 sub-products `A_i × B_j` by `ki*bitsA + kj*bitsB`, and accumulates.

### Fix (diagnostic)

Added `[SafeMpzMul§gen]` log lines after each of the 9 `GmpRaw_add` calls in the general accumulation loop, reporting `k`, `shiftBits`, `szProd` (sub-product limb count), `szShifted` (shifted limb count, for non-zero shifts), and `accumSz` (accumulator limb count after the add). This will identify exactly which sub-product first produces an abnormal accumulator state.

Also extended the `[SafeMpzDiv] q_approx ready:` log line to include `bot2limbs=[limb0 limb1]`, confirming whether the bottom two limbs of `q` are both zero.

### Status

Diagnostics added. Run in progress — awaiting log analysis.

## §110 — SafeMpzMul/SafeMpzDiv: sub-product top-2 limb diagnostics + `a` top-2

### Problem

After §109 confirmed that sub-product sizes are all plausible (k=0/1/2 zero because `A0=0` in the final Newton step; k=3..8 matching expected sizes), the root cause of the underflowing `q_approx` was still unclear.  The error corresponds to `ar` being short by ≈2^4,137,898,523, placing the missing bit at `ar[64,654,664]`, which falls entirely within k=8's shifted product (shift = 2,800,000,128 bits = exactly 43,750,002 limbs, so the raw product limb at index 20,904,662 is implicated).

### Fix (diagnostic)

- Added `top2=[hi lo]` logging for each of the 9 sub-products after their `GmpRaw_mul` completes, inside the general §23/§90 accumulation loop.
- Added `[SafeMpzDiv] a top2=[...]` immediately before `SafeMpzMul(ar, a, r)` to confirm `a` (`nTrunc`) is sane.

### Status

Log confirms `a top2=[000000000085BA61 25BC66C4D7A3C6AF]` and k=8's top2 matches `ar`'s top2.  `BigShiftRight` is confirmed correct (`q_bot_expected` matches actual `q_bot`).  Reciprocal top2 is confirmed correct.  Root cause localised to k=8's raw product at limb 20,904,662.

## §111 — SafeMpzMul/SafeMpzDiv: targeted error-limb diagnostic

### Problem

With the error precisely localised to `(A2×B2)[20,904,662]` — the interior of k=8's GmpRaw_mul sub-product — we need empirical read-back of that exact limb before and after accumulation to determine whether the fault is in the raw product, the limb-shift, or carry propagation from lower sub-products.

### Fix (diagnostic)

- In the §23/§90 accumulation loop, for the `a×r` call only (guard `szA=43,750,001 ∧ szB=21,875,001`):
  - Before accumulating k=8: log `prods(8)[20,904,662]` and `prods(8)[20,904,663]` as `[SafeMpzMul§111]`.
  - After accumulating k=8: log `accum[64,654,664]` and `accum[64,654,665]` as `[SafeMpzMul§111]`.
- In `SafeMpzDiv`'s `ar` diagnostic block, for `szAR=65,625,001`: log `ar[64,654,664]` and `ar[64,654,665]` as `[SafeMpzDiv§111]`.

### Status

`prod[20,904,662] = accum[64,654,664] = ar[64,654,664] = 0x8AF1A69460682417` — all non-zero and matching.  The k=8 raw product at that limb is correct.  Therefore the error is NOT at `ar[64,654,664]`; it must be in the middle zone `ar[43,750,002..64,654,663]`.

## §112 — SafeMpzDiv: sparse ar limb sweep to localise middle-zone error

### Problem

`ar[64,654,664]` is confirmed correct, but `q` is still ~2^1,337,898,496 short.  The shortfall must live in `ar[43,750,002..64,654,663]`.  A sparse sweep at ~11 positions across this zone will reveal exactly where values transition from valid to wrong/zero.

### Fix (diagnostic)

Added a `§112` sparse sweep inside the existing `szAR=65,625,001` guard in `SafeMpzDiv`, sampling `ar` at positions: 43,750,002 / 45,000,000 / 47,000,000 / 50,000,000 / 52,000,000 / 55,000,000 / 57,000,000 / 60,000,000 / 62,000,000 / 64,000,000 / 64,654,663.  Output as `[SafeMpzDiv§112]`.

### Status

All 11 sweep positions non-zero: `ar[43,750,002..64,654,663]` is fully populated.  `ar` itself is correct.  The error is therefore in `BigShiftRight` or in the `q×b` multiplication that follows.

## §113 — SafeMpzDiv: verify q middle limbs after BigShiftRight

### Problem

`ar` is confirmed correct throughout.  The next hypothesis is that `BigShiftRight(ar, ar, kBits)` or the `q×b` SafeMpzMul (which uses the §39 equal-size path for szA=szB=21,875,001) produces the wrong result.  Logging `q[10,937,500]` and `q[20,904,664]` immediately after BigShiftRight (before q×b) will confirm whether q itself is wrong or the error is introduced during q×b.

### Fix (diagnostic)

Added `[SafeMpzDiv§113]` inside the `szQ=21,875,001` guard: logs `q[10,937,500]` and `q[20,904,664]` from the shifted `ar` buffer before the `GmpRaw_swap`.

### Status

`q[10,937,500]=62E99550C2B36B0F` and `q[20,904,664]=26909AD15E34D28C` — both non-zero.  q itself is correct after BigShiftRight.  The bug is in `SafeMpzMul(q, b)` via the §39 equal-size path.

## §114 — SafeMpzMul §39: per-column diagnostics for q×b

### Problem

q is confirmed correct.  `SafeMpzMul(q, b)` uses the §39 column-group path (mA=mB=7,291,667).  The result qb is too small by ~2^1,337,898,496, causing rem=a−qb to have 42,779,665 limbs.  Need to identify which column contributes wrong/missing data.

### Fix (diagnostic)

Added `[SafeMpzMul§114]` inside the §39 loop, guarded by `mA=7,291,667`: logs per-column `szBk`, `bkTop`, `bkBot`, `szShifted`, `szAccum` for all 5 columns, plus `accum[42,779,664]` after col=4 (A2×B2, shift=1,866,666,752 bits).

### Status

**Result**: First 2 §39 calls (mA=7,291,667) have all 5 columns populated — these are SafeMpzReciprocal's r×r Newton iterations.  Subsequent 20+ calls show col0–3 szBk=0, col4 szBk=14,583,333 — implying A0=A1=B0=B1=0.  Yet §113 confirmed q[0]≠0 and q[10,937,500]≠0.  **Contradiction** — requires §115 to resolve.

## §115 — SafeMpzMul: distinguish r×r vs q×b calls via buffer-identity check

### Problem

§114 shows 20+ §39 calls where parts A0/A1/B0/B1 appear zero (trimmed szT=0) even though §113 confirmed q has non-zero limbs in those ranges.  Two possible explanations:
1. Those calls are SafeMpzReciprocal's `r×r` iterations (opA_d == opB_d), where r legitimately has zero low limbs.
2. Something corrupts q's buffer between §113 (before GmpRaw_swap) and SafeMpzMul(qb,q,b).

### Fix (diagnostic)

Added `[SafeMpzMul§115]` after A/B window setup, guarded by `mA=7,291,667`.  Logs `opA_d`, `opB_d`, `same` (pointer equality), and all six piece trim-sizes (`A0sz`…`B2sz`).  If `same=True` → r×r call.  If `same=False` with A0sz=0/A1sz=0 → genuine corruption in q.

### Status

Diagnostic added — run in progress.

## Repo housekeeping — exclude DLLs and PDBs from source control

Added `*.dll` and `*.pdb` patterns to `.gitignore` to prevent pre-built native binaries (`GmpNativeAlloc/Debug/GmpNativeAlloc.dll`, `GmpNativeAlloc/Debug/GmpNativeAlloc.pdb`) from appearing as modified files in the source control window.  Existing tracked copies removed from the git index.

## §NR-raw — SafeMpzReciprocal: replace managed wrapper calls with raw P/Invoke in Newton loop

### Problem

The Newton reciprocal loop (`SafeMpzReciprocal`) computed `r = 2r − p` via two successive managed wrapper calls:
```vb
gmp_lib.mpz_add(r, r, r)   ' r = 2r
gmp_lib.mpz_sub(r, r, p)   ' r = 2r - p
```
The `Math.Gmp.Native` managed wrapper is documented (§42/§78) to corrupt `mpz_t.Pointer` fields during native GMP calls on large objects.  At iterations 21–25 (r ≈ 21,875,001 limbs), this corruption caused `r.Pointer` to be read/written at a wrong address between the `mpz_add` return and the subsequent `mpz_sub` call.

Evidence:
- §119 log after iter=1: `bot=[FFFFFFFFFFFFFFFF FFFFFFFFFFFFFFFF]` (r has non-zero bottom limbs from seed borrow-propagation).
- §115 log at iter=2 entry (reading from same buffer address `0x0000027B1A590010`): `A0sz=0 A1sz=0` — all 7,291,667 bottom limbs read as zero.
- These two readings of the same buffer address are contradictory; the only intervening operation touching `r` is `gmp_lib.mpz_sub(r, r, p)` (managed wrapper).
- Bottom 2 limbs of r change by ~2^61–2^62 at each of iters 21–25 (diverging rather than converging), consistent with pointer corruption producing operand reads from a wrong address.

The guard path also used `gmp_lib.mpz_set_ui(r, 1UI)` — another managed call on a large object.

### Fix

1. Added `GmpRaw_sub` P/Invoke declaration (`__gmpz_sub`, `libgmp-10.dll`) for general mpz_t subtraction.
2. Replaced `gmp_lib.mpz_add(r, r, r)` with `GmpRaw_add(r.Pointer, r.Pointer, r.Pointer)` — tagged `§NR-raw`.
3. Replaced `gmp_lib.mpz_sub(r, r, p)` with `GmpRaw_sub(r.Pointer, r.Pointer, p.Pointer)` — tagged `§NR-raw`.
4. Replaced guard's `gmp_lib.mpz_set_ui(r, 1UI)` with `GmpRaw_set_ui(r.Pointer, 1UI)` — tagged `§NR-raw`.

All three raw calls bypass the managed wrapper entirely, eliminating pointer corruption on large operands.

### Status

Fix applied — run in progress.

## §123-§126 — Targeted limb diagnostics in SafeMpzReciprocal Newton final iteration

### Problem

After the §NR-raw fix, the run still crashes with `szRem=42,779,665 >> szB=21,875,001`.
Cross-checking confirmed:
- `q[20,904,664] = 0x26909AD15E34D28C` (computed) vs `b[20,904,664] = 0x2690BFD417C6E66C` (expected)
- The error at `q` traces back to `ar[64,654,664] = A2×B2[20,904,662] = 0x8AF1A69460682417` in `SafeMpzDiv`'s `a×r` multiplication
- This means either `r[20,904,664]` itself is wrong (bad Newton reciprocal), or GMP's `__gmpz_mul` is wrong (unlikely)
- Further narrowing: `r[20,904,664] = 0x0CFE92E693312BCA` (logged by §116); need to verify if this is correct

The Newton final iteration computes `p = bTrunc × rSq` (via `SafeMpzMul`), then `p >>= kBits`, then `r = 2r - p`.
If `p[64,654,664]` (before shift) or `p[20,904,664]` (after shift) carry an error, that propagates into r[20,904,664].

### Diagnostics added

All four fire only at `bShift = 0` (the final Newton iteration, iter=25):

- **§126** (`[NR126]`): `rSq[20,904,662]` and `rSq[20,904,663]` when `rSq = r²` is complete.
  `rSq[20,904,662]` feeds into `p[64,654,664]` via the B2 piece of `rSq` in `SafeMpzMul(p, bTrunc, rSq)`.

- **§125** (`[NR125]`): `p[64,654,664]` and `p[64,654,665]` before `BigShiftRight(p, p, kBits)`.
  The shift by `kBits = 2,800,000,027` bits maps these limbs to `p_shifted[20,904,664]` via:
  `p_shifted[20,904,664] = (p[64,654,664] >> 27) | (p[64,654,665] << 37)`

- **§123** (`[NR123]`): `p[20,904,664]` and `p[20,904,665]` after `BigShiftRight`.
  Cross-check: §123 value must equal `(§125[64654664] >> 27) | (§125[64654665] << 37)` — if not, the Newton `BigShiftRight` has a bug.

- **§124** (`[NR124]`): `r[20,904,664]` and `r[20,904,665]` immediately after `GmpRaw_sub(r, r, p)`.
  Must match §116 value `0x0CFE92E693312BCA` (logged in SafeMpzDiv after Newton completes) — if not, r is modified between Newton exit and SafeMpzDiv entry.

### Status

Diagnostics added — run in progress.

## §128 — SafeMpzMul: disable §39 column fast path when any split piece is zero

### Problem

The `SafeMpzDiv` failure (`adj-up exceeded 10`) remained reproducible after the §123–§126 diagnostics.  The trace showed:
- `a*r` and `BigShiftRight` produced a plausible `q_approx`
- the failure exploded during `SafeMpzMul(qb, q, b)`
- in that call, `mA=mB=7,291,667` and one split piece was exactly zero (`B0sz=0`), while the §39 column-group fast path was active.

The §39 path is an optimization that groups 9 sub-products into 5 shifted columns when `mA=mB`.  In this sparse-piece case, it produced a catastrophically low `q*b`, leaving `remainder = a - q*b` at ~42.8M limbs and forcing effectively unbounded adj-up corrections.

### Fix

Hardened the §39 branch condition in `SafeMpzMul`:

- **Before:** use §39 whenever `mA = mB`
- **After:** use §39 only when all six split windows are non-empty
    (`A0sz/A1sz/A2sz/B0sz/B1sz/B2sz > 0`)

If any split piece is zero-sized, `SafeMpzMul` now falls back to the general 9-product accumulation path (§23/§90), which is slower but robust for sparse windows.

### Status

Fix verified: the 700M→1.4B Barrett step (kBits=2,800,000,027, szB=21,875,001) completed
successfully with zero adj-up iterations.  The 1.4B→2.8B step (kBits=5,600,000,067,
szB=43,750,001) also exercised the fix (B0sz=0 again) and passed without incident.

---

## §175/§181 — SafeMpzMul: remove result.Pointer re-reads after inner calls

### Problem

Inside the 3×3 recursive `SafeMpzMul`, `savedResultPtr` was being overwritten with
`result.Pointer` in two places:

1. **After the 9 inner sub-product calls** (before serial accumulation):
   `savedResultPtr = result.Pointer`
2. **After the serial accumulation loop** (before the final struct copy):
   `savedResultPtr = result.Pointer`

Both re-reads were intended for safety, but `result.Pointer` is a managed-wrapper field
that Math.Gmp.Native may corrupt during recursive `SafeMpzMul` calls (§78 corruption —
the inner call's `mpz_init`/`mpz_clear` side-effects overwrite the outer frame's managed
field).  After a recursive sub-product call, `result.Pointer` pointed to a different struct
than `savedResultPtr` (the original pre-alloc'd native struct).  The re-read therefore
replaced the correct `savedResultPtr` with a corrupted address.

The effect: the outer accumulation and final struct-copy operated on the wrong native
struct, leaving `rSq`'s lower limbs zeroed — causing Newton iterations to appear
converged prematurely and producing a wrong reciprocal.

### Fix

Removed both `savedResultPtr = result.Pointer` re-reads.  `savedResultPtr` is now
captured exactly once (immediately after pre-alloc, before any inner call) and never
overwritten.  `accumPtr` is derived from `savedResultPtr` rather than from
`result.Pointer`.

The serial accumulation loop contains no inner `SafeMpzMul` calls, so `accumPtr` is
also stable across that loop — the second re-read was doubly unnecessary.

### Status

Fix applied and verified across Newton iterations 1–26 for szB=43,750,001.

---

## §176–§183 — SafeMpzMul diagnostic probes

A set of `_logLevel >= 2` instrumentation probes added during the Barrett crash
investigation to identify the source of zero-data corruption in Newton squarings:

| Probe | Location | Purpose |
|-------|----------|---------|
| §176  | After 9 inner calls, mA=7,291,667 squarings | Log prods(0..2) bottom limbs immediately after inner SafeMpzMul |
| §177  | After piece trim, mA=2,430,556 squarings | Log A0/A1/A2 sizes and raw limbs for depth-2 r×r calls |
| §178  | After fast-path return, squarings only | Log if fast-path produced zero result (szA+szB ≤ threshold) |
| §179  | After A0 trim loop | Log when A0-trim reduces to zero in squarings — with freed-buffer aliasing check |
| §182  | Before k=6,7,8 inner calls (serial path) | Log A2._mp_d and A2_d[0] to detect mid-loop corruption |
| §183  | SafeMpzMul entry, squarings only | Log if opA._mp_d already points to zero data on entry |

These probes fire only when `_logLevel >= 2` and are conditioned to avoid hot-path
overhead.  §179/§183 fire legitimately for `SafeMpzPow10` squarings (powers of 10 have
trailing zero limbs); they do not fire for Newton squarings of r.

---

## §144-serial — SafeMpzDiv b×r diagnostic: force serial

### Problem

The `§144` diagnostic block (which computes `b×r` to verify the Newton reciprocal) was
calling `SafeMpzMul` without serialising it, causing the diagnostic itself to race and
potentially corrupt state being measured.

### Fix

Wrap the `§144` `SafeMpzMul(_br144, b, r)` call with `_safeMulDop = 1` save/restore,
matching the §168 pattern used for the main reciprocal computation.

---

## §184 — SafeMpzDiv: bypass managed wrapper for qb and remainder (fix STATUS_ASSERTION_FAILURE crash)

### Problem

After `SafeMpzMul(qb, q, b)` returned, `SafeMpzDiv` called `gmp_lib.mpz_init(remainder)` via
the Math.Gmp.Native managed wrapper, then `gmp_lib.mpz_sub(remainder, a, qb)`.

The `gmp_lib.mpz_init(remainder)` call went through the managed wrapper, which triggered the §78
side-effect: Math.Gmp.Native's internal tracking scanned registered `mpz_t` objects and updated
their `Pointer` fields.  This corrupted `qb.Pointer` (even though `qb` was not passed to the
call), replacing it with a stale/wrong address.

When `gmp_lib.mpz_sub(remainder, a, qb)` was then called, Math.Gmp.Native read the corrupted
`qb.Pointer` and passed a garbage struct address to GMP's `__gmpz_sub`.  GMP's internal
assertion (`_mp_alloc ≥ abs(_mp_size)` or a limb-count sanity check) failed immediately,
raising `STATUS_ASSERTION_FAILURE` (exception code 0x40000015) at offset 0x14ef6 in
`libgmp-10.dll`.

This crash was 100% reproducible: every run hit the same fault ~99 minutes in (after
Newton completes for the 1.4B→2.8B step and q×b accumulation finishes).

### Fix

Replace all managed-wrapper calls in the post-`SafeMpzMul` section of `SafeMpzDiv` with raw
P/Invoke calls (bypassing Math.Gmp.Native entirely):

- Allocate `qb` as a plain `Marshal.AllocHGlobal(16)` struct + `GmpRaw_init` (not `gmp_lib.mpz_init`)
- Capture `_qbPtr = qb.Pointer` immediately after `SafeMpzMul` returns, before any native call
- Allocate `remainder` as a plain raw struct + `GmpRaw_init`
- Use `GmpRaw_sub(_remRaw, a.Pointer, _qbPtr)` instead of `gmp_lib.mpz_sub(remainder, a, qb)`
- Use `GmpRaw_clear` + `FreeHGlobal` for cleanup
- All adj-down/adj-up operations use `_remRaw` and raw P/Invokes (`GmpRaw_sub_ui`, `GmpRaw_add`,
  `GmpRaw_add_ui`, `GmpRaw_sub`, `GmpRaw_cmp`) — no managed wrapper calls touch `qb` or `remainder`

### Status

Fix applied and verified: computation completed the 1.4B→2.8B Newton step and
saved checkpoint kBitsX=2,800,000,028 successfully.

---

## §SqNewton — SafeMpzSqrt Newton loop: bypass managed wrapper for nTrunc/xTrunc/q (fix STATUS_ASSERTION_FAILURE crash)

### Problem

After the 1.4B→2.8B checkpoint was saved, the compute resumed for the 2.8B→5.6B
(final) Newton step in `SafeMpzSqrt`.  For this step `nShift=0` and `xHalf=0`, so
the code path falls through to the `GmpRaw_set(nTrunc.Pointer, n.Pointer)` branch.

The crash occurred because the three `gmp_lib.mpz_init` calls at the top of the
Newton loop (`nTrunc`, `xTrunc`, `q`) went through the Math.Gmp.Native managed wrapper,
triggering the §78 side-effect: every registered `mpz_t.Pointer` field was updated.
After `gmp_lib.mpz_init(nTrunc)` the values of `n.Pointer` and `x.Pointer` in the
enclosing scope became stale (pointing to wrong native structs).  When
`GmpRaw_set(nTrunc.Pointer, n.Pointer)` was then called, it passed the corrupted
`n.Pointer` address to GMP's `__gmpz_set`, which called `_mpz_realloc` on a garbage
struct and fired GMP's internal assertion at offset 0x14ef6 in `libgmp-10.dll`
(`STATUS_ASSERTION_FAILURE`, code 0x40000015).

This crash always happened at the start of the final Newton step — never on earlier
iterations because those used `BigShiftRight` (pure raw calls) instead of `GmpRaw_set`.

### Fix (§SqNewton)

Apply the same raw-struct bypass used by §184:

- Before the Newton loop, capture `_xNativePtr = x.Pointer` and `_nNativePtr = n.Pointer`.
- Inside each iteration, allocate `nTrunc`, `xTrunc`, `q` with `Marshal.AllocHGlobal(16) + GmpRaw_init`
  instead of `gmp_lib.mpz_init` — these are never registered with the managed wrapper.
- When `nShift=0` (copy n whole): use `PreAllocMpzToLimbs(nTrunc, szN)` then
  `GmpRaw_set(_nTruncRaw, _nNativePtr)` using the pre-captured raw pointer.
- When `xHalf=0` (copy x whole): similarly use `_xNativePtr` for the copy source.
- After `SafeMpzDiv` returns (which triggers §78 again internally), restore:
  `x.Pointer = _xNativePtr` and `n.Pointer = _nNativePtr`.
- Replace `gmp_lib.mpz_add(xTrunc, xTrunc, q)` with `GmpRaw_add(_xTruncRaw, _xTruncRaw, _qRaw)`.
- Replace `gmp_lib.mpz_tdiv_q_2exp` with `GmpRaw_tdiv_q_2exp`.
- Replace `gmp_lib.mpz_clear` + `FreeHGlobal` for all three raw structs.
- Use `GmpRaw_swap(_xNativePtr, _xTruncRaw)` (not `x.Pointer`) to update x after each step,
  then immediately restore `x.Pointer = _xNativePtr`.

### Status

Fix applied; computation completed the 2.8B→5.6B Newton step. Checkpoint
kBitsX=3,321,928,130 saved.

---

## §NumeratorDiv — ComputePi final division: restore Pointer fields after §78 corruption (fix STATUS_HEAP_CORRUPTION)

### Problem

After SafeMpzSqrt completed and the numerator was saved to the `gmpNumer` checkpoint,
the next restart loaded `gmpNumer` from `snap_Phase3/gmpNumer.bin` via
`TryLoadPhase3Value("gmpNumer", gmpNumer, ...)` and similarly for `finalT`.

These checkpoint-loading calls go through the Math.Gmp.Native managed wrapper
(`gmp_lib.mpz_realloc2`, `gmp_lib.mpz_clear`, `gmp_lib.mpz_init`), each triggering
the §78 side-effect: all registered `mpz_t.Pointer` fields are overwritten with
stale/wrong native struct addresses.  By the time the code reached `NumeratorDone`,
`gmpPi.Pointer`, `gmpNumer.Pointer`, and `finalT.Pointer` were all corrupted.

At the gmpPi pre-allocation block (just before `SafeMpzDiv(gmpPi, gmpNumer, finalT)`),
the code read `gmpPi.Pointer` to find the old 1-limb buffer address, then called
`_savedGmpFree(old_buf, 8)` to release it before writing the new large VirtualAlloc
pointer.  With a corrupted `gmpPi.Pointer`, `old_buf` was a garbage native-heap address.
Passing a garbage pointer to `_savedGmpFree` (the CRT `free`) immediately corrupted the
Windows heap, raising `STATUS_HEAP_CORRUPTION` (exception code 0xc0000374) at
`ntdll.dll+0x1176e5`.

### Fix (§NumeratorDiv-v4)

Two earlier attempts (d796769, 7487b61) to restore the three Pointer fields also failed:
the captures were taken after `gmp_lib.mpz_inits(gmpSqrtInput, gmpSqrt, gmpNumer, gmpPi, gmpOne)`,
but `mpz_inits` fires §78 during each internal `mpz_init` call.  The §78 fired during
`mpz_init(gmpOne)` (last in the list) overwrote `gmpPi.Pointer` before we could capture it,
so `_gmpPiRaw` itself contained a stale/wrong address — restoring to it put a garbage pointer
into `gmpPi`, and the pre-alloc `_savedGmpFree` call still crashed.  By the same mechanism,
`_gmpNumerRaw` was also wrong (overwritten by §78 during `mpz_init(gmpPi)` and `mpz_init(gmpOne)`).

Additionally, the gmpPi pre-alloc block was removed entirely: it was never safe because even
with a correct `_gmpPiRaw`, GmpReallocFunc handles the 1-limb CRT → large VirtualAlloc growth
correctly when `SafeMpzDiv` first writes to `gmpPi` — one realloc inside the division is
harmless.

**Root fix:** remove `gmpNumer` and `gmpPi` from `mpz_inits`; init them separately, in order,
capturing each `Pointer` immediately after its own `mpz_init` and before the next call fires §78:

```vb
gmp_lib.mpz_inits(gmpSqrtInput, gmpSqrt, gmpOne, Nothing)   ' gmpNumer/gmpPi excluded
gmp_lib.mpz_init(gmpNumer)
Dim _gmpNumerRaw As IntPtr = gmpNumer.Pointer  ' correct: captured before mpz_init(gmpPi) fires §78
gmp_lib.mpz_init(gmpPi)
Dim _gmpPiRaw As IntPtr = gmpPi.Pointer        ' correct: no managed GMP call between here and mpz_init(gmpPi)
gmpNumer.Pointer = _gmpNumerRaw                ' restore: mpz_init(gmpPi) just fired §78 and corrupted gmpNumer.Pointer
```

After this, `gmpNumer.Pointer` is correct so `TryLoadPhase3Value("gmpNumer", gmpNumer, ...)`
(which calls `DeserializeOneMpz` → `Marshal.WriteInt32(val.Pointer, ...)`) writes to the right
native struct.  `_finalTRaw` is captured after `gmp_lib.mpz_init(finalT)` as before.

At `NumeratorDone`, all three are restored before `SafeMpzDiv`:

```vb
If _gmpPiRaw <> IntPtr.Zero Then gmpPi.Pointer = _gmpPiRaw
If _gmpNumerRaw <> IntPtr.Zero Then gmpNumer.Pointer = _gmpNumerRaw
If _finalTRaw <> IntPtr.Zero Then finalT.Pointer = _finalTRaw
SafeMpzDiv(gmpPi, gmpNumer, finalT)
```

`SafeMpzDiv` captures `a.Pointer`/`b.Pointer` at entry (§184c), uses `q.Pointer` for
`GmpRaw_swap` and the adjustment loop — all require correct addresses.

### Status

v4 fix applied and built (Debug). 1B-digit run completed successfully (verified all three digit checks OK).

## §Phase3OOM — Step 2 squaring OOM crash at 5B digits

### Problem

At 5B digits, `SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)` (Step 2: squaring 10^5B) uses
`gmpOne` ≈ 130M limbs (1 GB). With `_safeMulDop=24` (all cores), the 9 sub-products
run concurrently; each sub-product is ~43M×43M limbs → ~700 MB, so 9 simultaneous
allocations = ~6 GB on top of ~22 GB already in use. Windows silently terminates the
process when `VirtualAlloc` fails — no managed exception, no log entry, clean exit code.

### Fix (§Phase3OOM)

Force `_safeMulDop=1` for the Step 2 squaring only, then restore the saved DOP:

```vb
Dim _savedDopStep2 As Integer = Volatile.Read(_safeMulDop)
Volatile.Write(_safeMulDop, 1)
SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)
Volatile.Write(_safeMulDop, _savedDopStep2)
```

Serial sub-products reduce peak concurrent memory to ~700 MB extra (one sub-product
at a time) instead of ~6 GB. Step 2 takes longer but completes without OOM.
snap_Phase3 (P/Q/T) is saved before Step 1, so restart resumes from there.

## §171-iter — SafeMpzDiv: iterate top-limb correction + capture raw prodHdr (5B SafeMpzSqrt crash fix)

### Problem

At 5B digits, inside `SafeMpzSqrt` Newton step 1 (`175M / 87.5M` limbs), `SafeMpzDiv`'s
adj-up loop exceeded `MAX_ADJ_ITERS=10` (Barrett quotient was ~2× too small: szRem ≈ 172.7M,
szB ≈ 87.5M — ratio 1.97). The existing §171 top-limb correction fired, computed
`szDelta=85,222,805` via `mpn_divrem_1`, then did:

```vb
SafeMpzMul(_prod171, _deltaWrap171, _bWrap171)
GmpRaw_sub(_remRaw, _remRaw, _prod171.Pointer)
```

The log showed `szRemNew = 172,722,805` — **identical** to the pre-correction szRem.
Subtraction produced no change. The outer adj-up loop then ran 11 more naive iterations,
hit `§171b`'s `GmpRaw_tdiv_q(_qPtr, _aPtr, _bPtr)` fallback, which AV'd (0xC0000005)
because `mpz_tdiv_q` allocates internal scratch that can't satisfy 172M-limb / 87.5M-limb
at this scale.

### Root cause

Per the existing §175 note, `SafeMpzMul` recursion corrupts `result.Pointer` for
locally-scoped `mpz_t` wrappers. `SafeMpzMul` restores via `savedResultPtr` at its exit,
but when the caller uses the wrapper's `.Pointer` property *after* the call, it may still
read a stale value routed through managed wrapping. The actual correct struct is always
at the raw `IntPtr` captured before the call (`_prodHdr171`).

When `GmpRaw_sub(_remRaw, _remRaw, _prod171.Pointer)` ran, `_prod171.Pointer` pointed at
a struct with `_mp_size=0`, so GMP subtracted zero. rem was left unchanged.

### Fix

1. **Use captured raw pointer** `_prodHdr171` directly in `GmpRaw_sub` / `GmpRaw_clear` —
   matches the §78/§NR-r-add/§184 pattern.
2. **Iterate** the §171 correction until `szRem ≤ szB`. One pass is not enough when the
   rem/b ratio approaches 2 (which it does at 5B scale). Bail with a clear
   `InvalidOperationException` if a pass fails to reduce rem (pointer corruption or
   arithmetic bug — loud, not silent-AV).
3. **Remove `§171b` crash fallback**. Replace `GmpRaw_tdiv_q` with an
   `InvalidOperationException` carrying `szRem`, `szB`, `szA` — if iterative §171
   somehow leaves rem > b after MAX_ADJ_ITERS of normal adj-up, we want a clean
   stack trace, not an AV.

### New diagnostics (added per "every investigation adds logging")

- `[SafeMpzDiv§171-entry]` — szA, szB, szRem, ratio on first §171 trigger.
- `[SafeMpzDiv§171 pass=N]` — bTop, szDelta, szRemBefore (start of each pass).
- `[SafeMpzDiv§171 pass=N]` — szProd, prodHdr address, `_prod171.Pointer`, **match flag**
  (post-SafeMpzMul — would have caught the `.Pointer`-mismatch bug in seconds).
- `[SafeMpzDiv§171 pass=N] done` — szRemAfter, delta.
- `[SafeMpzDiv§171-done]` — total passes + final szRem.

If a future crash recurs, the per-pass `match=False` flag or a non-decreasing szRem would
instantly pinpoint the failure mode.

## §171-barrett — 5B SafeMpzSqrt Newton step 1: Barrett precision bug (NOT a §171 bug)

### Observed (2026-04-23)

After deploying `§171-iter` and restarting the 5B run, the fix threw a clean exception
1h 11m in, at the same code location:

```
[SafeMpzDiv§171-entry] szA=175,000,001 szB=87,500,001 szRem=172,722,805 ratio=1.974
[SafeMpzDiv§171 pass=1] bTop=0x0000000021D94463 szDelta=85,222,805
[SafeMpzDiv§171 pass=1] szProd=172,722,805 prodHdr=0x…  prod.Ptr=0x… match=True
[SafeMpzDiv§171 pass=1] done: szRemAfter=172,722,805 Δ=0
  EXCEPTION: SafeMpzDiv §171 pass 1 did not reduce rem size
```

- `match=True` → pointer corruption was **not** the cause (my original §171-iter hypothesis).
- `szProd=172,722,805` → SafeMpzMul did produce the correctly-sized delta×b.
- `Δ=0` → GmpRaw_sub(rem, rem, prod) ran but left szRem exactly unchanged.

### Root cause

The real bug is **upstream of §171**, in SafeMpzDiv's Barrett estimate itself:

- Barrett setup: `szA=175,000,001 szB=87,500,001 aBits=11,200,000,064 bBits=5,600,000,064 kBits=11,200,000,067 szR=87,500,001 rBits=5,600,000,038`.
- `q_approx` from `(a*r) >> kBits` has top limbs `[0x21D94463, D8DAD84AB39138B5]`.
- After `SafeMpzMul(q*b)` and subtract, `rem` has 172,722,805 limbs = ~2^(11.05B) — far larger than `b` (~2^(5.6B)).
- So `q_true − q_approx ≈ rem/b ≈ 2^(5.45B)` (a **~85M-limb** integer).

Normal Barrett should produce error ≤ 1-2. This is **off by 2^(5.45 billion)** — a bug, not rounding.

### Why §171 cannot converge at 5B

`b`'s top limb is `0x21D94463` (only **30 bits** non-zero). The single-limb correction
`delta = floor(rem_top / (bTop+1))` is accurate only to ~2^bTopBits per pass. So each
pass reduces rem by factor ≤ 2^30 in value. To close the 2^(5.45B) gap needs ~180 million
passes — obviously infeasible.

Even with b-normalization (shifting `bTop` to have its top bit set) the reduction is ~2^63
per pass ≈ 86 million passes — still infeasible. **Single-limb top correction is
fundamentally unable to fix a Barrett error this large.**

### Where the real bug is

One of these at 5B scale produces a wrong result:
1. `SafeMpzMul(ar, a, r)` — 175M × 87.5M limb multiply.
2. `BigShiftRight(ar, ar, kBits)` — shift by 11.2B bits = 175M limbs.
3. `SafeMpzReciprocal(r, b, kBits)` — Newton reciprocal with insufficient precision for
   the unnormalized b (top limb 30 bits → loss of significance propagates).

At 1B digits these worked (§171 fired 4 times and all single-pass corrections succeeded).
At 5B something is numerically wrong at one of these three stages.

### Not yet fixed — next steps

- Add Barrett sanity check: before adj-up, verify `szRem ≤ szB + MAX_ADJ_ITERS` or
  throw immediately with all Barrett params logged, so we fail fast rather than run 11
  pointless adj-up iters then enter §171.
- Instrument `SafeMpzMul(ar,a,r)` top/bot limbs and compare against an independent
  reference (small example with known answer) to localise which step is wrong.
- Consider forcing b-normalization (`b << k` so bTop's top bit is set) before the
  Barrett setup — this is a known stabiliser for divisors with sparse top limbs.

### Status

`§171-iter` code is correct — it catches the bug cleanly with rich diagnostics instead
of the prior silent AV. But the 5B run still crashes at Newton step 1 because the root
cause is upstream. Further investigation needed.

## §5B-investigate — Boundary-limb logging to localise the upstream Barrett bug

### Approach

Three candidates for the upstream bug at 5B scale: `SafeMpzMul(ar,a,r)`,
`BigShiftRight(ar,kBits)`, or `SafeMpzReciprocal`. To localise:

- **Pre-mul logging** (gated on `szA=175,000,001 ∧ szR=87,500,001`): log a[0], a[1],
  a[mid], a[szA-2], a[szA-1] and r[0], r[1], r[mid], r[szR-2], r[szR-1].  Captured into
  outer-scope ULongs so they are reusable post-mul.
- **Post-mul self-verification** at the boundaries:
  - **Bottom**: `ar[0]` must equal `(a[0] * r[0]) mod 2^64` exactly.  Mismatch ⇒
    `SafeMpzMul` produced a wrong bottom limb (definitive).
  - **Top**: `ar[szAR-1..szAR-2]` should be plausibly close to
    `Math.BigMul(a[szA-1], r[szR-1])` (high) plus accumulated cross-product carry.
    Wildly off ⇒ top-limb error in `SafeMpzMul`.
- **Post-shift logging**: log q[0], q[1], q[mid], q[szQ-2], q[szQ-1].  Combined with
  saved ar limbs (already logged for kLimb / kLimb+1), the existing `q_bot_expected`
  formula tells us if `BigShiftRight` honoured the bit-shift correctly.

### Why this is decisive

If the §5B-arBot match flag is `False`, the bug is **definitely** in `SafeMpzMul` —
no other component can change the bottom limb of a*r. If `True`, SafeMpzMul's bottom
is correct; we then look at top-limb plausibility and the post-shift logs to attribute
the error to either SafeMpzMul's top or BigShiftRight.

If both the bottom matches and the top is plausible (carry within a few limbs of
`hi(a_top*r_top)`), the bug is in `BigShiftRight` or `SafeMpzReciprocal` — and the
post-shift q values let us pin it down.

### Status

Diagnostics added; relaunching the 5B run to capture them.  No checkpoint added because
the existing Newton checkpoint (kBitsX=2.8B) already lets us get back to this point in
~1h11m, and the diagnostics will fire on the very first §171 trigger.

### Fresh-Newton verification (2026-04-26 12:43–21:57)

Ran with `nr_r.bin` moved aside, forcing `SafeMpzReciprocal` to recompute `r` from seed.
Newton converged through 27 iterations in ~8h.  Final `r` is **bit-for-bit identical**
to the saved checkpoint at all five logged boundary positions (r[0], r[1], r[mid],
r[szR-2], r[szR-1]) AND every other §5B-* value (a, ar, q, rem, ratio) is identical
across the two runs.

**Conclusion: bug is fully deterministic. Not checkpoint corruption, not Newton, not
parallelism, not memory.**  The same wrong q_approx is produced reliably.

The bug must be in the chain:
1. `BigShiftRight(n, nShift) → nTrunc` (`a`) — only boundary verified, middle could be wrong.
2. `SafeMpzMul(ar, a, r)` middle limbs — only ar[0] (exact) and ar[szAR-1] (boundary)
   verified.  Middle limbs unverified.
3. `BigShiftRight(ar, kBits) → q_approx` middle limbs — only q[0] is verified via the
   existing `q_bot_expected` formula.  Middle of q unverified.

### q[mid] / q[quart] verification result (2026-04-27)

Added §5B-q-mid and §5B-q-quart spot-checks: capture ar[kLimb+i] and ar[kLimb+i+1]
before BigShiftRight (for i = quartIdx=21,875,000 and midIdx=43,750,000); then post-shift
verify q[i] = (ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61) matches actual q[i].

Result: **both match=True** at quart and mid. BigShiftRight produces q faithfully from
ar across all four checked positions (q[0], q[quart], q[mid], q[szQ-1]).

**The bug is now isolated to `SafeMpzMul(ar, a, r)` middle limbs.** ar's middle limbs are
wrong (e.g., ar[218,750,001]=F749B40E433B9742 differs from the true a*r at that index),
even though ar[0] = a[0]*r[0] mod 2^64 is exact and ar[szAR-1] = high(a[top]*r[top]) is
plausible at the boundary. One or more of the 9 sub-products in SafeMpzMul's 3-way
Toom-Cook split is producing a wrong value at 175M × 87.5M-limb scale.

Next step: instrument SafeMpzMul to log each of the 9 sub-products' size/top/bottom/mid
limbs at the szA=175M ∧ szR=87.5M gate so we can spot which sub-product is wrong.

### Result (2026-04-26 12:36)

Run reached §171 in 1h 14m and threw with all diagnostics. Critical findings:

```
[SafeMpzDiv§5B-a]   a[0]=A514E7911325F190 ... a[szA-1]=0479BC06C17340EB
[SafeMpzDiv§5B-r]   r[0]=88638C785832DAFF ... r[szR-1]=0000003C81298323
[SafeMpzDiv§5B-ar]  ar[0]=FA82F7C310A03E70 ... ar[szAR-1]=000000010ECA231E
[SafeMpzDiv§5B-arBot] actual ar[0]=FA82F7C310A03E70 expected=FA82F7C310A03E70  match=True
[SafeMpzDiv§5B-arTop] ar[szAR-1]=000000010ECA231E  hi(a_top*r_top)=000000010ECA231E
[SafeMpzDiv§5B-q]   q[0]=2DBB1E91012D8D3E ... q[szQ-1]=0000000021D94463
[SafeMpzDiv§171-entry] szRem=172,722,805 ratio=1.974 bTop=0x21D94463 bTopBits=30
```

**SafeMpzMul is correct**: the bottom-limb identity `ar[0] = (a[0]*r[0]) mod 2^64`
holds **exactly**, and the top limb matches `high(a[szA-1]*r[szR-1])` with cross-term
carry of 0.

**BigShiftRight is correct**: `q[szQ-1] = ar[szAR-1] >> 3 = 0x10ECA231E >> 3 = 0x21D94463`
matches actual; `q[0]` matches the existing `q_bot_expected` formula.

**Therefore the bug is in `r` itself** — `SafeMpzReciprocal` (or its checkpoint).  Given
correct `a*r` and correct shift, q_approx error of 2^(5.45B) directly maps to an r
error of ~2^(5.45B) in r's bottom ~85M limbs.  r's top ~2.5M limbs appear correct
(rBits, top-limb value, magnitude all match expectations); the corruption (or Newton
convergence shortfall) lives in the lower limbs that aren't surfaced by boundary checks.

### Most likely root cause

The 5B Newton-reciprocal checkpoint `nr_r.bin` was saved by an earlier run that already
contained this bug; on every restart we load the same corrupt r.  Each subsequent run
loads the bad checkpoint, computes the same wrong `a*r`, and crashes at the same place.

### Next step (deferred — discuss with user)

Options:
1. Invalidate the checkpoint (`mv nr_r.bin nr_r.bin.suspect`) and force a fresh Newton
   recomputation from seed.  Expensive (many hours) but definitive: if fresh r differs
   from saved r, the checkpoint is the bug; if identical, the bug is in Newton
   itself at this scale.
2. In-process verification: after `SafeMpzReciprocal` returns, compute `r*b` and
   confirm it lies in `[2^kBits - b, 2^kBits)`.  Direct but adds another 175M-limb
   multiply per Newton step.
3. Mid-limb spot check: log r at many positions (every ~1M limbs) and compare against
   the expected magnitude/distribution of a true reciprocal.  Cheap but only suggestive.

### §5B-sub verifyT — per-sub-product TOP-limb spot check (2026-04-27)

Fresh-Newton verification (above) ruled out `r` itself: the bug is fully deterministic
and identical across runs.  Subsequent §5B-q-mid / §5B-q-quart / §5B-arBot / §5B-arTop
work isolated the error to **`SafeMpzMul(ar, a, r)` middle limbs of one or more of the
9 Toom-Cook sub-products**.

`§5B-sub verify` (k=0..8 prods[0]) and `§5B-sub verify1` (k=0..8 prods[1]) both match
all 9 sub-products: bottom limbs are correct.  The bug therefore lives in
`prods(k)[≥2]` for at least one k.

**verifyT** (this section) extends the gate to the TOP limb of each sub-product.
For k=0..8, gated on `szA=175,000,001 ∧ szB=87,500,001` (the outer 175M × 87.5M
SafeMpzMul call):

- Compute `topA_idx = ki*mA + szAi - 1` and `topB_idx = kj*mB + szBj - 1`, where
  `szAi`, `szBj` are the actual sizes of pieces `A_i` and `B_j` (the last A piece
  is one limb shorter at 5B due to ceiling-division).
- Compute `(expHi, expLo) = BigMul(A_i[topA_idx], B_j[topB_idx])`.
- Compare against actual `prods(k)[szProd-1]` and `prods(k)[szProd-2]`.
- Choose the comparison side based on whether `mpz_mul` stripped a leading zero:
  - If `actSzProd == szAi + szBj`: the top limb should be `≈ expHi` (within 0..2 carry).
  - If `actSzProd == szAi + szBj - 1`: leading zero was stripped, so `actTop ≈ expLo`.
- Log `diff(act-exp)`: `0`, `1`, or `2` is normal carry; anything large pinpoints
  the wrong sub-product's recursive `SafeMpzMul`.

Most likely k=7 (A_2×B_1) and/or k=8 (A_2×B_2) — they are the only contributors to
ar[218,750,001], the known-wrong limb.  This run will distinguish "wildly off top" (sub-
product is broken end-to-end) from "correct top, broken middle" (sub-product top limb
is fine but middle limbs are wrong, suggesting a deeper recursion or leaf `mpz_mul`
issue at sizes near `SAFE_LIMB_THRESHOLD=5M`).

### verifyT result (2026-04-27 16:54 — run 6)

Run reached §171 in 1h 11m and threw deterministically with the same Barrett error
(~2^5,454,259,456). All 9 sub-products' **top limbs** matched the predicted
`hi(A_i[topA] * B_j[topB])` with `diff(act-exp) ∈ {0, 1}` — no carry-of-2 anywhere,
no "wildly off" k:

```
k  ki  kj  diff(act-exp)   actSzProd     expSzProd
-  --  --  -------------   -----------   -----------
0   0   0  0x1             87,500,001    87,500,001
1   0   1  0x0             87,500,001    87,500,001
2   0   2  0x0             87,500,001    87,500,001
3   1   0  0x0             87,500,001    87,500,001
4   1   1  0x1             87,500,001    87,500,001
5   1   2  0x0             87,500,001    87,500,001
6   2   0  0x0             87,500,000    87,500,000
7   2   1  0x0             87,500,000    87,500,000
8   2   2  0x0             87,500,000    87,500,000
```

Combined with the previously-verified prod[0] (verify) and prod[1] (verify1) match
flags, this rules out the working hypothesis that one sub-product is "wildly off
end-to-end".  **The bug is definitively in middle limbs of one or more sub-products**,
not at any boundary.

The known-wrong `ar[218,750,001]` receives contributions from exactly two sub-products
(others' shift ranges don't reach that index):
- `prods(7)[72,916,666]` (shift = 145,833,335 limbs)
- `prods(8)[43,749,999]` (shift = 175,000,002 limbs; geometric mid of prods(8))

The §5B-sub log already shows `prods(8)[mid=43,750,000]=11D57DC8288B6585` — that limb
is essentially the suspect.

### Next step (recommended): Option B — lower SAFE_LIMB_THRESHOLD 5M → 1M

Option B becomes the natural next binary-search step: forces each of the 9 inner
sub-products (each at 58.3M × 29.2M) to recurse one more split level past the
2.2M × 1.1M leaves that currently call GMP `mpz_mul` directly.

Outcome:
- Bug **disappears** ⇒ leaf `mpz_mul` / `mpn_mul_fft` is producing wrong middle limbs
  at sizes ≥ 1M (GMP FFT precision issue near the current threshold).
- Bug **persists** ⇒ middle-limb error is in our `SafeMpzMul` 3×3 split logic itself
  (accumulator add, `mul_2exp` shift, or recursion housekeeping).

Single-constant change at line ~2271 of `Form1.vb`.  ~1h to next data point with the
warm-checkpoint resume.

### Option B in flight (2026-04-27)

`SAFE_LIMB_THRESHOLD` lowered from `5_000_000` to `1_000_000` in `Form1.vb`.  Build
+ launch + warm-checkpoint resume; expecting either a clean 5B run (proves the bug
was in the leaf GMP call at sizes ≥ 1M) or the same §171 throw (proves the bug is
in our own split logic).  Result will be appended below.

### Option B result (2026-04-28 00:37 — run 7)

Run reached §171 in 6h 26m (5.4× slower than the 5M run, due to deeper recursion
overhead from the additional 9× sub-product fan-out at level 6) and threw the
**identical** exception with the **identical** Barrett error magnitude
(`~2^5,454,259,456`).  All 9 sub-products produced **bit-identical** mid[43,750,000]
and top limbs as the 5M run:

```
k | mid (5M)              | mid (1M)              | top (5M)              | top (1M)
- | --------------------- | --------------------- | --------------------- | ---------------------
0 | 6CB381B03B25461A      | 6CB381B03B25461A      | 27B50FCBA40707E7      | 27B50FCBA40707E7
1 | 3106EFF61B28AB18      | 3106EFF61B28AB18      | 38D70AFB5A9A3D99      | 38D70AFB5A9A3D99
2 | 948620F9445F2749      | 948620F9445F2749      | 0000002FD735CD6D      | 0000002FD735CD6D
3 | 51987FEC0865F037      | 51987FEC0865F037      | 1A93F197A821B53F      | 1A93F197A821B53F
4 | AA62DB4D6DB9259B      | AA62DB4D6DB9259B      | 260BAECE29FCA757      | 260BAECE29FCA757
5 | 68E86859D15E75D9      | 68E86859D15E75D9      | 00000020059F1CE1      | 00000020059F1CE1
6 | E260C3D54136832D      | E260C3D54136832D      | 00E0C0ADDCA37B15      | 00E0C0ADDCA37B15
7 | 0E4F4489AEE94ABF      | 0E4F4489AEE94ABF      | 0141BA6194A14F54      | 0141BA6194A14F54
8 | 11D57DC8288B6585      | 11D57DC8288B6585      | 000000010ECA231E      | 000000010ECA231E
```

At 5M threshold the leaf `mpz_mul` calls operate on ~3.24M total limbs (FFT pl≈2^22).
At 1M threshold the leaves operate on ~360K total limbs (FFT pl≈2^19) — far below
any plausible FFT precision boundary.  Identical results across these two regimes
**rules out** the leaf `mpz_mul` / `mpn_mul_fft` as the bug source.

### Conclusion of Option B

**The bug is in code COMMON to both threshold settings.** Top suspects:

1. The **§gen accumulation step** itself — `GmpRaw_mul_2exp(_sv_shifted_hdr, _shiftSrc, _chunk)`
   inside the chunked-shift loop.  For shifts > UInt32.MaxValue bits (~4.29 billion),
   the loop iterates 2× (k=4-6) or 3× (k=7-8) at the outer 175M × 87.5M call, with
   the second/third iterations reading from `_shiftSrc = _sv_shifted_hdr`
   (in-place shift).  GMP supports aliasing but worth verifying.
2. **Piece extraction (A_parts, B_parts mpz_t setup)** — A_2 size = 58,333,333 (one
   limb shorter than mA = 58,333,334) due to ceiling division.  Edge-case slicing
   for the last piece could mis-set _mp_size or _mp_d.
3. **mpz_t struct juggling** (§40/§42/§44, accumPtr stash inside savedResultPtr's
   _mp_d slot) — unusual layout; if any inner SafeMpzMul accidentally writes a
   wrong buffer, middle-limb corruption could repeat deterministically.
4. **GmpRaw_add into accum** — battle-tested GMP add; lowest probability.

### Next step (recommended): Option C — pinpoint the wrong sub-product OR the wrong accumulation step

Three complementary diagnostics, any can run individually:

- **C-1 (independent prods(8) reference)**: gated on the outer 175M × 87.5M call,
  compute prods(8) = A_2 × B_2 a SECOND time via a fresh SafeMpzMul into a separate
  mpz_t, and compare mid[43,750,000].  Match ⇒ deterministic-but-wrong (true bug).
  Differ ⇒ memory corruption (very different problem).
- **C-2 (direct mpz_mul reference)**: at the outer call, also compute prods(8) via
  a direct `GmpRaw_mul(prod_alt, A_2, B_2)` (skipping our 3×3 split).  Match ⇒ bug is
  outside our split (in accumulation, mul_2exp, or piece extraction).  Differ ⇒ bug
  is in our level-2 SafeMpzMul split.
- **C-3 (per-k accumulation snapshot)**: log `accum[218,750,001]` after each k's
  `GmpRaw_add`.  The k whose add introduces the divergence pinpoints either a bad
  prods(k) middle limb OR a bad shift step.  Cheapest of the three (no extra
  multiplication needed).

C-2 is the most decisive single test: it directly compares our split against GMP's
own multiplication.  Combine with C-3 for layered confirmation.

**Note**: SAFE_LIMB_THRESHOLD should be reverted to 5,000,000 before further runs —
the 5.4× slowdown is too costly for the rest of the investigation.

### Option C-2 + C-3 in flight (2026-04-28)

Two complementary diagnostics added to `Form1.vb`, both gated on the outer 175M ×
87.5M `SafeMpzMul(ar, a, r)` call.

**§5B-c2** — Direct `GmpRaw_mul` reference for prods(8).  After the 9 outer
sub-products are computed via recursive `SafeMpzMul`, also compute `A_2 × B_2`
once via `GmpRaw_mul` (GMP's internal mpz_mul, 87.5M total limbs).  Compare the
suspect middle limb at index 43,749,999 (which contributes to ar[218,750,001])
plus boundaries at index 0 and szP-1.

Interpretation:
- **Match at idx=43,749,999** ⇒ both paths agree on this middle limb; either both
  are right (so prods(8) is NOT the bug source — look at prods(7) or the
  shift+add) or both happen to share the same wrong value (extraordinary
  coincidence between two independent FFT engines — extremely unlikely).
- **Mismatch at idx=43,749,999** ⇒ paths diverge.  We can't tell which is right
  from C-2 alone, but combined with C-3 we can pin which contributor's middle
  limb feeds the wrong ar value.

**§5B-c3** — Per-k accumulation snapshot.  After each k=0..8's `GmpRaw_add` into
`accum`, log `accum[218,750,001]` (the known-wrong ar limb) and its two
neighbours.

Expected progression:
- k=0..6: shift ranges don't reach index 218,750,001 ⇒ accum[218,750,001] = 0
- k=7: shift = 145,833,335 limbs, prods(7)[72,916,666] enters the limb ⇒
  accum[218,750,001] = prods(7)[72,916,666]
- k=8: shift = 175,000,002 limbs, prods(8)[43,749,999] is added ⇒
  accum[218,750,001] = (prods(7)[72,916,666] + prods(8)[43,749,999]) mod 2^64
  + cross-carry from limb 218,750,000

If accum[218,750,001] differs from the known-wrong final value `F749B40E433B9742`
already after k=7 ⇒ prods(7) is the source.  If it's correct after k=7 but wrong
after k=8 ⇒ prods(8) is the source.  If neither sub-product alone seems wrong but
their combined sum doesn't match expectations ⇒ `GmpRaw_add` or carry handling
is the source (very unlikely but worth ruling out).

Both diagnostics are cheap (~30 s for C-2's direct mpz_mul; C-3 is essentially
free).  Combining their outputs should narrow the bug to one of: prods(7) middle,
prods(8) middle, mul_2exp shift step, or accumulator-add step.

### Option C result (2026-04-28 10:02 — runs 8 + 9)

**C-2 disabled** after run 8 crashed natively (0xC0000005 in `libgmp-10.dll`)
on the direct `GmpRaw_mul(A_2, B_2)` at 58.3M × 29.2M = 87.5M total limbs.
GMP's mpz_mul is unsafe at this scale — exactly the regime `§143`'s recursive
split was created to avoid.  Existing `§136` block uses the same pattern at
43.75M total where GMP merely produces wrong limbs; the hard-fail boundary
is somewhere between 43.75M and 87.5M total.

**C-3 result (run 9, 1h 11m to §171, identical Barrett error)**:

```
After k | accum[218,750,001]                    Notes
--------|---------------------------------------|----------------------------------------
0..6    | 0000000000000000                      Their shift ranges don't reach this index
7       | 3E924C7A243168E4                      = prods(7)[72,916,666] exactly (limb-aligned shift)
8       | F749B40E433B9742                      = known-wrong ar[218,750,001]
```

Critical interpretation:
- After k=0..6 the limb is exactly zero, confirming no spurious upstream contribution.
- After k=7, accum[218,750,001] = `3E924C7A243168E4`. Because k=7's shift
  (145,833,335 limbs × 64 bits) is exactly limb-aligned and the prior accum was zero
  at this limb, this value equals `prods(7)[72,916,666]` with no carry contamination.
- After k=8, the accum value matches the known-wrong final ar limb — confirming the
  full §gen output reproduces the same wrong ar.

The k=8 delta `F749B40E433B9742 - 3E924C7A243168E4 = B8B767941F0A2E5E` represents
`prods(8)[43,749,999] + cross-limb carry from limb 218,750,000`.

**Bug is now isolated to ONE limb of ONE of two specific level-2 SafeMpzMul calls:**
- `prods(7)[72,916,666]` (the suspect value `3E924C7A243168E4`) where prods(7) =
  SafeMpzMul(A_2, B_1) at 58.3M × 29.2M, OR
- `prods(8)[43,749,999]` of prods(8) = SafeMpzMul(A_2, B_2) at 58.3M × 29.2M.

A buggy `GmpRaw_add` carry chain at 175M-limb scale is extremely unlikely (battle-
tested GMP code), but not formally ruled out.

### Next step (recommended): Option D — recursive C-3 at the level-2 call

Apply the same per-k accumulation snapshot to the level-2 SafeMpzMul calls (gated
on szA=58,333,333 ∧ szB=29,166,667 with input-bot-limb fingerprints to identify
prods(7) vs prods(8) specifically).  At that level, the level-2 §gen loop has its
own 9 sub-products (each ~19.4M × 9.7M), and the relevant accum index is 72,916,666
(for prods(7)) or 43,749,999 (for prods(8)).

This recursive narrowing pinpoints the inner k where the wrong value enters at
level 2, and so on until we reach a leaf that we can verify directly against
direct mpz_mul (which is safe at sub-5M total limb sizes).

### Option D in flight (2026-04-28)

**§5B-d-L2** — at every level-2 SafeMpzMul call (gated `szA=58,333,333 ∧
szB=29,166,667`, the size of A_2 × any B_j at the outer 175M × 87.5M call),
log `accum[72,916,666]` (= prods(7) suspect index) and `accum[43,749,999]`
(= prods(8) suspect index) after each k=0..8 sub-product accumulation, plus
opB[0] for fingerprinting (B_0=`88638C785832DAFF`, B_1=`4B08FAE8DCA50441`,
B_2=`0706751D8688C2D3`).

At level-2: mA' = 19,444,445, mB' = 9,722,223.  Shifts are limb-aligned at
ki'·mA' + kj'·mB' limbs.

For target index 72,916,666 (prods(7) suspect):
- k'=0..6 shifts don't reach this index ⇒ accum should be 0.
- k'=7 (shift=48,611,113 limbs) reaches it; post-k'=7 value is the level-3
  sub-product limb that lands at offset 72,916,666 - 48,611,113 = 24,305,553.
- k'=8 (shift=58,333,336 limbs) also reaches it; post-k'=8 value combines
  contributions from both.

For target index 43,749,999 (prods(8) suspect):
- k'=0,1 shifts don't reach this index.
- k'=2..6 shifts reach it; values accumulate.
- k'=7,8 shifts are too high; their offsets within prods(k') are negative.

Three level-2 calls fire the gate (prods(6), prods(7), prods(8)).  The opB[0]
fingerprint distinguishes them in the log.  The k' that first introduces a
wrong value pinpoints which level-3 sub-product (19.4M × 9.7M) is the culprit
— or whether the bug is in the level-2 shift+add itself.

### Option D result (2026-04-28 15:52 — run 10, 1h 14m to §171)

prods(7) = SafeMpzMul(A_2, B_1) at 58.3M × 29.2M, accum[72,916,666] across k':

```
After k' | accum[72,916,666]
---------|----------------------
0..6     | 0000000000000000     (k'=0..6 shifts don't reach this index)
7        | 6A28287E3E835734     ← level-3 sub-product 7 contribution
8        | 3E924C7A243168E4     ← matches outer prods(7)[72,916,666] exactly
```

prods(8) = SafeMpzMul(A_2, B_2) at 58.3M × 29.2M, accum[43,749,999] across k':

```
After k' | accum[43,749,999]
---------|----------------------
0..1     | 0000000000000000
2        | 04CCBF81C2006924
3        | E2EEDAF0F48BA909
4        | F029EC6DEF37FE89
5        | A62ABA42210CF6B0
6        | B8B767941F0A2E5D     ← matches outer C-3 delta (off by 1 = cross-limb carry)
```

**Both level-2 SafeMpzMul calls reproduce the wrong values verbatim.**  The
bug is at level 3 or deeper, but we still cannot tell which of `prods(7)` or
`prods(8)` (or both) is wrong without an independent oracle.

Continued recursive narrowing (level-3, level-4, ...) doesn't produce an
oracle either; it just localizes the bug to a smaller sub-product.

### Next step (recommended): Option E — chunked-grid independent reference

Compute `prods(7) = A_2 × B_1` (and/or `prods(8) = A_2 × B_2`) via a 2-way
chunked-grid split that uses ONLY direct `GmpRaw_mul` at sub-threshold sizes:

- Split A_2 (58.3M limbs) into ~39 chunks of ≤ 1.5M limbs each
- Split B_1 (29.2M limbs) into ~20 chunks of ≤ 1.5M limbs each
- For each (i,j) pair: compute `chunk_A[i] × chunk_B[j]` via direct
  `GmpRaw_mul` (≤ 3M total — well under `SAFE_LIMB_THRESHOLD = 5M`,
  where direct mpz_mul is reliable per §160's earlier analysis)
- Accumulate all 780 sub-products into a fresh result mpz_t via
  `mul_2exp` + `add`, exactly mirroring the §gen pattern but with a
  flatter 2-way structure that avoids our 3×3 split entirely

Read `result[72,916,666]` and compare to our SafeMpzMul `prods(7)[72,916,666]
= 3E924C7A243168E4`:
- **Match** ⇒ `prods(7)` is correct; the bug is in `prods(8)` (or in the
  carry chain of `GmpRaw_add` at the outer level).  Drill into prods(8)
  next.
- **Differ** ⇒ `prods(7)` is wrong.  The chunked reference value IS the
  truth.  We then know exactly how much our SafeMpzMul is off, and we can
  drill into the level-2 prods(7) computation to find which level-3 k'
  introduces the divergence.

Cost: 780 sub-products at ~50ms each + accumulation ≈ 1-2 minutes one-shot.
Memory peak: a few GB.  Gated on the outer 175M × 87.5M call so it fires
once per run.

### Option E in flight (2026-04-28)

`§5B-e` implemented in `Form1.vb`: at the outer 175M × 87.5M call, computes
both `prods(7)` and `prods(8)` via 39 × 20 = 780 sub-products of size
≤ 1.5M × ≤ 1.5M (≤ 3M total — well under the 5M FFT-precision boundary),
then accumulates each into a fresh `_refAcc` mpz_t via `mul_2exp` + `add`.

Compares the suspect index of each:
- `reference prods(7)[72,916,666]` vs our SafeMpzMul `prods(7)[72,916,666]`
  (`= 3E924C7A243168E4` per run 9/10)
- `reference prods(8)[43,749,999]` vs our SafeMpzMul `prods(8)[43,749,999]`
  (`= B8B767941F0A2E5D` per run 10)

Logs include the `idx-1` and `idx+1` neighbours so we can see whether any
disagreement is just a one-limb carry quirk or a substantive divergence.

Result expected after the next ~1h 14m run.

### Option E v1 result (2026-04-28 17:00 — run 11 ABORTED)

`gmp: overflow in mpz type` aborted run 11 at the start of the §5B-e prods(7)
loop.  Root cause: GMP's realloc path (NativeReallocFunc) misbehaving when
freshly `GmpRaw_init`'d `_ckShifted`/`_refAcc` (1-limb initial alloc) tried to
grow to ~87.5M limbs across the 780-iteration grid.  Fix in v2: pre-allocate
both buffers via VirtualAlloc to 90M limbs (~720 MB) and swap them into the
mpz_t struct's `_mp_d` slot, mirroring §gen's `_sharedSjBuf` /
`_sv_shifted_hdr` pattern — `_mp_alloc` set to the full pre-allocated size,
so `mul_2exp` and `add` never trigger realloc.

### Option E v2 result (2026-04-28 18:35 — run 12, 1h 26m to §171)

**MAJOR PIVOT.**  The chunked-grid reference completed cleanly and revealed:

```
prods(7) idx=72,916,666 (and idx-1, idx+1):
  reference:    EA6244050D44001F  3E924C7A243168E4  6AD0F6B6D638BF07
  ourSafeMpz:   EA6244050D44001F  3E924C7A243168E4  6AD0F6B6D638BF07
  → MATCH (all 3 adjacent limbs identical).

prods(8) idx=43,749,999 (and idx-1, idx+1):
  reference:    751C4E2F65EC4FA6  B8B767941F0A2E5D  11D57DC8288B6585
  ourSafeMpz:   751C4E2F65EC4FA6  B8B767941F0A2E5D  11D57DC8288B6585
  → MATCH (all 3 adjacent limbs identical).
```

**Both `prods(7)` and `prods(8)` are CORRECT.**  Combined with the level-1
shift+add structure, this means `ar[218,750,001] = F749B40E433B9742` is the
**correct** value, not the wrong value we had presumed.

The "ar[218,750,001] is wrong" assumption was an INFERENCE ("q is off by
2^5.45B ⇒ some ar limb must be wrong ⇒ we picked 218,750,001 because it was
already logged in early diagnostics"), never proven against an independent
oracle.  Option E disproves the assumption.

### What this opens up — bug is somewhere we never checked

The real wrong limb (or wrong operation) lives in territory we haven't
verified yet.  Candidates, ordered by likelihood:

1. **A different ar limb** — we've only verified ar at indices 0, szAR-1,
   and 218,750,001.  ar has 262,500,002 limbs total.  Some other mid-position
   is the culprit.
2. **BigShiftRight(ar, kBits) → q at unchecked positions** — Option A
   verified q at 4 indices (0, quart, mid, szQ-1).  The shift could be wrong
   elsewhere.
3. **r itself has wrong middle limbs** — fresh-Newton verified r is
   bit-identical to checkpoint at 5 boundary positions; the middle of r was
   never verified against `r * b ∈ [2^kBits - b, 2^kBits)`.
4. **a has wrong middle limbs** — same coverage gap as r.

### Next step (recommended): Option F

- **F-3 (FIRST — cheap)**: scan all q[0..szQ-1] against the
  `(ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61)` formula.  If any disagreement
  ⇒ BigShiftRight is wrong at that index.  Runs in seconds; no extra mpz_mul.

### Option F-3 in flight (2026-04-28)

`§5B-f3` implemented in `SafeMpzDiv` — captures 100 evenly-spaced ar samples
(at q indices 0, ~884K, ~1.77M, …, 87.5M-1) plus their +1 neighbours BEFORE
`BigShiftRight(ar, kBits)`, then post-shift verifies each q[i] against the
predicted `(ar_pre[kLimb+i] >> 3) | (ar_pre[kLimb+i+1] << 61)`.  Logs first
10 mismatches plus a summary count.

Outcome:
- mismatches > 0 ⇒ BigShiftRight is wrong at one or more positions
- mismatches = 0 ⇒ BigShiftRight is faithful across the q range; bug is in
  ar itself or upstream (escalate to Option F-1 next).

### Option F-3 result (2026-04-28 20:20 — run 13, 1h 23m to §171)

```
[SafeMpzDiv§5B-f3 SUMMARY] scanned 100 q positions, mismatches=0, firstMismatchSampleIdx=-1
[SafeMpzDiv§5B-q-quart] q[21,875,000] match=True
[SafeMpzDiv§5B-q-mid]   q[43,750,000] match=True
```

Combined with the existing q[0] / q[szQ-1] coverage from Option A, **102 q
positions verified, all matching**.  BigShiftRight is faithful (with high
statistical confidence — would need the bug to live at a single limb out
of 87.5M, with all 100 evenly-spaced samples skipping it, to be missed).

**The bug is in ar itself at some unchecked limb, OR upstream in r or a.**

### Next step (recommended): Option F-1 — chunked-grid a × r reference

Compute `a × r` (175M × 87.5M = 262.5M-limb result) via a flat 2-way
chunked grid using sub-threshold direct `mpz_mul`: chunk size 1.5M, grid =
117 × 59 = 6,903 sub-products.  Each sub-product is ≤ 3M total limbs —
reliable per §160's analysis.

Scan our SafeMpzMul ar against the reference at thousands of positions;
log first ~10 mismatches and a summary count.  Outcome:
- Mismatches > 0 ⇒ ar is wrong at those positions; bug is in our level-1
  §gen accumulation step (`mul_2exp` chunked-shift loop, or `GmpRaw_add`
  carry chain).
- Mismatches = 0 ⇒ ar is fully correct; bug must be in `kBits`
  computation, in BigShiftRight at a position F-3's 100 samples missed
  (escalate to F-3-full), or in the §171 trigger logic itself.

Pre-allocate `_refAcc` and `_ckShifted` buffers via VirtualAlloc (~2.5 GB
each) and swap into mpz_t — same pattern as §gen's `_sharedSjBuf` and the
Option E v2 fix.  ~12 min added to the run.
- **F-1**: full chunked-grid reference for `a * r` (39 × 39 = 1,521
  sub-products); scan ar limbs against the reference at every k-th position
  (e.g., every 100K).  Finds the wrong ar limb if any exists.  ~5-10 min.
- **F-2**: chunked-grid `r * b` and verify it's in `[2^kBits - b, 2^kBits)`.
  Catches Newton convergence shortfall in the middle of r.
