# PI-BillionDigits

## Origin

This project started with a request from my friend Mike Iem, one of the nicest guys I've ever worked with, who asked me to help him calculate Pi to a billion digits. I have no idea why he would want this, but it's Mike — and I'll help him out on whatever he asks for.

Once I had it working at 1 billion digits, I figured: let's see if we can push it to 5 billion. That's where we are now.

Details on the project below.

## What it is

PI-BillionDigits is a Windows Forms application that computes Pi to an arbitrary number of decimal digits — verified at up to five billion — and displays the result. It is written in VB.NET targeting .NET 10 and uses the [Math.Gmp.Native](https://www.nuget.org/packages/Math.Gmp.Native.NET/) wrapper around the GNU Multiple Precision Arithmetic Library (GMP) for all big-integer arithmetic.

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

The `Run-PiCompute.ps1` script accepts a `-Threshold` parameter that overrides the RAM/disk threshold. At 5 billion digits the number of Phase 1 chunks (~43,000) exceeds the default threshold; pass a large value to keep everything in RAM and avoid disk I/O during the combine phase. (The chunk count does not scale linearly with digits — `CHUNK_SIZE` saturates at 8,192 terms per chunk, so 1 billion digits yields ~137,700 chunks and 5 billion yields ~43,000.)

**Requirements:** ~40 GB available RAM for an all-in-RAM 5-billion-digit run (peak occurs during the final division), 64-bit Windows, .NET 10. The app auto-detects available RAM at startup and lowers the RAM/disk threshold (spilling the binary-split tree to the NVMe cache) on smaller machines, so it still runs — more slowly — with less RAM.

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
| **Digits of PI** text box | Number of decimal digits to compute. Accepts values like `1,000,000` or `1000000000`. Auto-formatted with commas as you type. Default: 1,000,000. |
| **Start** button | Begins the computation on a high-priority background thread (256 MB stack). Disabled while a run is in progress. |
| **Cancel** button | Cancels the current run via a cancellation token. All in-progress work is **discarded** — nothing is written to file (an interactive run has no mid-run checkpoint). A confirmation dialog warns that all progress will be lost. |
| **Display** checkbox | When checked, the computed digits are shown in the output panel after computation completes — streamed in for runs up to 250,000 digits, or presented via the navigable window (below) for larger runs. Unchecking this is useful when only the file output matters, since rendering a billion digits is expensive. |
| **Write to File** checkbox | When checked, the full digit string is saved to `pi_digits.txt` in the output directory (default `C:\PiOutput`) after computation. |
| **Verify after compute** checkbox | When checked, the known-digit verification (see **Verify Now**) runs automatically as soon as the computation finishes, with the result reported in the status bar — no dialog boxes. |
| **RAM Threshold** spinner | Node-count threshold deciding RAM vs disk per combine level: if a level's node count is ≤ this value it stays in RAM (faster); above it, nodes are written to the NVMe cache (less RAM). Auto-detected from available RAM at startup (≥16 GB → 200,000; ≥8 GB → 100,000; <8 GB → 1). Mirrors the `-Threshold` script parameter / `--threshold` CLI flag. |
| **Log Level** spinner | Runtime logging verbosity, 0–5 (0 = errors only, 2 = phase milestones [default], rising to 5 = exceptionally detailed per-operation dumps). Mirrors the `--log-level` CLI flag. |
| **Verify Now** button | Searches the computed digits for three known substrings and reports whether they appear at the correct positions: `999999` (expected at position 762, the Feynman point), `777777777` (expected at position 24,658,601), and `999999999` (nine 9s, expected at position 564,665,206). Searches the full native buffer. |
| **Status** bar | Shows the current phase (e.g., "Streaming 1,000,000,000 digits...") or any error message. |
| **Running Time** label | Elapsed wall-clock time since Start was clicked, updated every second. |
| **Displayed** label | Running count of digits shown in the output panel so far. |
| **Phase log** list box | Timestamped log of major computation phases (chunk processing, combine levels, string conversion, streaming). Each entry shows elapsed time since Start. |
| **Output panel** | Black-background, lime-on-black RichTextBox showing the Pi digits, prefixed with `3.`. For runs larger than 250,000 digits it becomes a movable 250,000-digit window with a TrackBar beneath it that scrubs across the full result — each slice is read on demand from the native buffer (O(1) per move), avoiding the O(n²) cost of streaming a billion digits into the control (§271). |

---

## Command-line options

The executable accepts the following flags (most are also surfaced through `Run-PiCompute.ps1` parameters). Headless/automation runs typically combine `--autostart` with `--autoverify`.

| Flag | Argument | Purpose |
|------|----------|---------|
| `--digits` | `N` | Digit count to compute (commas optional, e.g. `--digits 1000000000`). |
| `--autostart` | — | Suppress all dialogs and begin computing immediately (headless mode). |
| `--autoverify` | — | After the run, auto-run verification and exit. |
| `--verify-at` | `"D:P"` | Assert that digit string `D` occurs at position `P` (e.g. `"999999:762"`). Repeatable. |
| `--verify-contains` | `"D"` | Assert that digit string `D` occurs somewhere in the result. Repeatable. |
| `--threshold` | `N` | Override the RAM/disk node-count threshold (see **RAM Threshold** above). |
| `--log-level` | `N` | Runtime logging level 0–5 (default **2**). |
| `--output-dir` | `D` | Output directory for `pi_digits.txt`, the phase log, and `NodeCache\`. |
| `--checkpoint-from-level` | `N` | Serialize nodes at level ≥ `N` to disk (enables resume). |
| `--resume-from-level` | `N` | Skip Phase 1 and levels `1..N-1`; load checkpoint files for level `N`. |
| `--auto-checkpoint` | — | Write a RAM snapshot at the end of each level; auto-resume on the next launch. |

**Diagnostic / benchmark harnesses** (each runs after GMP init, writes results to `%TEMP%\*_test.txt`, then exits; several are tuned by the test-only environment variables above):

| Flag | Harness |
|------|---------|
| `--test-mulhigh` | `SafeMpzMulHigh` correctness self-test (§250). |
| `--test-chunkedgrid` | `SafeMpzMul_ChunkedGrid` full/HIGH correctness self-test (§251). |
| `--test-eta` | ETA-estimator self-test (§259). |
| `--test-advisor` | Performance-advisor self-test (§260). |
| `--test-dopscan` | DOP / memory-bandwidth saturation sweep (§263). |
| `--test-gridscan` | Split-factor (k×k grid) comparison (§265). |
| `--test-cellsweep` | Chunked-grid cell-size sweep at 5 B sizes (§266). |
| `--test-recipconv` | Reciprocal-Newton convergence probe (§272). |

> There is currently no `--help` flag; unknown arguments are ignored. (Tracked separately.)

---

## Cumulative Summary of Changes

A high-level overview of everything that was changed from the original implementation to reach a working 1-billion-digit computation. The detailed [Change Log](Change_LOG.md) documents each individual change and its root cause.

### Architecture

**Disk-based binary split (§3):** The original code held all ~137,000 chunk P/Q/T values in RAM simultaneously — feasible for small digit counts but tens of GB at 1 billion digits. The rewrite streams each chunk to disk immediately after computation and loads one pair at a time during the combine phase. Only the final combine pair is held in memory at once.

**Three-pass multiply (§7, §46, §47):** The final `gmpNumer *= finalQ` multiplication (~1.1 GB × ~1.1 GB) peaks at ~2.3 GB, exceeding available headroom after the other live buffers. `finalQ` is split into three equal bit-thirds (Q0, Q1, Q2) and multiplied separately; the three partial products are shifted and summed to reconstruct the full result. Peak per-pass is ~1.2 GB.

**`SafeMpzMul` (§17–§45, §160):** GMP's internal FFT uses a 32-bit `mp_size_t` (signed `int` on Windows MSVC), and very large operands also push GMP's Schönhage-Strassen FFT past the range where it returns reliably. `SafeMpzMul` therefore splits whenever the **combined** operand size `szA + szB` exceeds `SAFE_LIMB_THRESHOLD = 5,000,000` limbs (the conservative cap set by §160). It is a schoolbook 3×3 split: each operand is divided into three equal thirds by bit position and the nine sub-products are computed separately with GMP's fast routines, which never see an operand large enough to trigger the problem. Recursive: sub-products that still exceed the threshold recurse. (At the very largest scales the dominant multiplies route instead through the chunked-grid path — see the Change Log, §251/§262/§267–§269.)

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

**`mp_size_t` 32-bit overflow (§17, §160):** GMP's FFT code computes intermediate sizes as `mp_size_t` (32-bit signed int on Windows), and large operands also strain its FFT accuracy. `SafeMpzMul` ensures GMP never sees a multiply whose combined operand size exceeds `SAFE_LIMB_THRESHOLD = 5,000,000` limbs, splitting 3×3 (and recursing) before that point.

**`Chr()` encoding (§24):** VB.NET's `Chr()` uses Windows-1252 code page encoding, unavailable in .NET Core. Replaced with `ChrW()` (Unicode) throughout.

**Delegate lifetime (§2, §6):** All delegate objects passed to native APIs (`SetUnhandledExceptionFilter`, `mp_set_memory_functions`) are stored as `Shared` fields. Local-variable delegates are collected by the GC, leaving dangling function pointers.

---

### Observability

- Structured log file with timestamp, thread ID, elapsed time, and RAM per entry; synchronous flush on every write guarantees the last entry before a crash is on disk (§2).
- `SetUnhandledExceptionFilter` native crash handler catches GMP `abort()` and writes a marker before the process exits (§2).
- Runtime logging level (§83, §252, §257): a single 0–5 integer set via the **Log Level** UI spinner or the `--log-level N` CLI flag (default 2). `AppendLog(msg, level)` writes a line only when `level ≤ _logLevel`, giving a strict-superset ladder — 0 = silent/errors only, 1 = errors + result, 2 = phase milestones, 3 = sub-phase, 4 = detailed trace, 5 = exceptionally detailed per-operation limb dumps. (This replaced the original `LOGGING_DETAIL` compile-time constant.)
- Native buffer streaming: the billion-digit result is kept as a native `char*` rather than a managed string, avoiding a 1 GB managed allocation and GC pressure during display (§13).
- Thread priority `AboveNormal` + `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` to prevent Windows from throttling the compute thread (§27).
- Headless / automation mode (§63): `--digits N --autostart --autoverify` runs end-to-end without any UI dialogs; suppressed dialogs written to the phase log with a `[DIALOG]` prefix.
- Custom digit verification (§67): `--verify-at "DIGITS:POSITION"` and `--verify-contains "DIGITS"` CLI options for automated correctness checks.

### P-Core Affinity on Hybrid CPUs

Intel 12th-gen+ (Alder Lake, Raptor Lake) and AMD Zen 4c CPUs expose two classes of cores: **P-cores** (full-power, high IPC, preferred for GMP math) and **E-cores** (lower power, lower single-thread performance, shared L2). Without affinity control, the Windows thread pool schedules tasks onto whichever logical processors are available — including E-cores — which can unpredictably slow down bandwidth-bound GMP operations.

The original approach (§66) restricted the whole process to P-cores. That was **superseded by §247 (#48/#49)**: pinning the entire process to P-cores left the E-cores idle, but the E-cores are useful for overlapping disk I/O with compute. The current design keeps the **process** affinity mask on **all cores (P | E)** and instead uses an *affinity watchdog* (§106) that hard-pins the heavy **compute** threads to P-cores, while the §248/§249 I/O threads (chunk serializers, Phase-2 prefetch) pin *themselves* to the E-cores and are exempted from the watchdog. Because a thread's affinity mask can never exceed the process mask, the E-cores must remain in the process mask for that E-core I/O pinning to take effect.

**How P/E detection works (§66):**

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

            ' Step 3: only act if this is actually a hybrid CPU
            If pCoreMask <> 0L AndAlso eCoreMask <> 0L Then
                ' §247 (#48/#49): keep the PROCESS mask on ALL cores (P | E) — NOT a hard
                ' lock to P.  The affinity watchdog (below) hard-pins the compute threads to
                ' P-cores, while the otherwise-idle E-cores stay AVAILABLE for the §248/§249
                ' I/O threads (serializers, prefetch) that pin themselves there.  A thread's
                ' affinity mask cannot exceed the process mask, so E must remain in it.
                SetProcessAffinityMask(GetCurrentProcess(), New IntPtr(pCoreMask Or eCoreMask))
                _pCoreMask = pCoreMask   ' §106: saved for the watchdog
                ' Log: $"Hybrid CPU. P=0x{pCoreMask:X} E=0x{eCoreMask:X}"
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

**Call site — invoke once during startup, before Phase 1:**
```vb
SetPCoreAffinity()        ' set the process mask to P | E and record the P-core mask
StartAffinityWatchdog()   ' §106: hard-pin compute threads to P throughout the run
ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount)
```

**Key points:**
- The two-call pattern (size query with `IntPtr.Zero`, then data query) is required — the buffer size varies with the number of cores.
- `Size` at offset +4 is the actual record length and must be used to advance the offset; do not assume a fixed struct size.
- On a non-hybrid machine all records have the same `EfficiencyClass`, so `eCoreMask` stays 0 and the affinity mask is left unchanged — the function is safe to call unconditionally.
- `GetCurrentProcess()` returns a pseudo-handle that is always valid; no `CloseHandle` required.
- The process mask is inherited by all threads; the **per-thread** P-core pinning is enforced continuously by the watchdog, and E-core I/O threads opt out of it (§247–§249).

### Automation

**Headless mode (§63):** All three `MessageBox.Show` dialogs are gated behind `If Not _headless Then`. In headless mode the text is written to the phase log with a `[DIALOG]` prefix so automated runs leave a full audit trail without blocking.

**`Run-PiCompute.ps1` (§63, §70, §94):** PowerShell script that clean-builds and launches the exe. Machine-independent: it builds in **Debug** by default (pass `-UseRelease` for a Release build) and auto-detects the exe by globbing `bin\<config>\**\PI-BillionDigits.exe` after the build (no hardcoded TFM folder); the output directory defaults to `C:\PiOutput` (overridable via `-OutputDir`). Parameters: `-Digits N` (default 1B), `-OutputDir <path>`, `-LogLevel N` (0–5, default 1), `-Threshold N`, `-CheckpointFromLevel N`, `-ResumeFromLevel N`, `-AutoCheckpoint`, `-BackupCheckpoint`, `-UseRelease`, `-Trace`, `-ReportOnly <path>`.

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

**P-core affinity + thread pool pre-warm (§66, §247):** On hybrid CPUs (Intel P+E core), `GetLogicalProcessorInformationEx` detects P-cores by `EfficiencyClass`. The process affinity mask is kept on all cores (P | E); a watchdog (§106) hard-pins compute threads to the P-cores while the §248/§249 I/O threads use the E-cores. `ThreadPool.SetMinThreads(ProcessorCount, ProcessorCount)` pre-warms the thread pool before Phase 1 to eliminate first-task latency.

---

## Environment variables

The application reads a number of `PI_*` environment variables to tune or override behaviour. They are read at process/operation start, so set them before launching (the `--test-*` ones only matter when running the corresponding self-test harness).

**Production flags** (affect a real compute run):

| Variable | Default | Effect |
|----------|---------|--------|
| `PI_CONV_PARALLEL` | on (`0` = off) | Use the parallel recursive-halving decimal converter (§270/§226) at ≥ 1.5 B digits; `0` reverts to the §216 serial converter. |
| `PI_CG_ADAPTIVE` | on (`0` = off) | Adaptive chunked-grid cell size (§267/§268); `0` restores the fixed 1.5 M-limb cell. |
| `PI_CG_CELL_MAX` | `16000000` | Maximum chunked-grid cell size in limbs (clamped 1,500,000–16,700,000); keeps the cell below GMP's FFT limit. |
| `PI_CG_DOP` | `ProcessorCount` (capped 16) | Degree of parallelism for chunked-grid cells. |
| `PI_RECIP_SHORTMUL` | on (`0` = off) | Route the reciprocal-Newton capped-iteration multiplies through the chunked grid (§251/§254); `0` uses the §gen path. |
| `PI_RECIP_SHORTMUL_MAXDOP` | `9` | DOP gate — engage the chunked reciprocal only when §gen's DOP ≤ this (the low-DOP 5 B regime). |
| `PI_DIV_AR_SHORTMUL` | on (`0` = off) | Compute the divide's `a×r` as a chunked-grid **HIGH** product (§262). |
| `PI_DIV_QB_CHUNKED` | on (`0` = off) | Compute the divide's `q×b` as a chunked-grid **full** product (§269). |
| `PI_MEMBUDGET_HEADROOM_GB` | `5` | RAM headroom (GB) reserved by the memory-budget DOP planner; a large value forces a low-RAM downshift for testing. |

**Diagnostic / test-only flags** (read only by the `--test-*` harnesses and probes — see [CLI options](#command-line-options)):

| Variable | Default | Effect |
|----------|---------|--------|
| `PI_RECIP_SHORTMUL_VERIFY` | off (`1` = on) | Cross-check each chunked `RecipMul` against §gen (slow correctness probe). |
| `PI_TEST_DOPGATE` | off (`1` = on) | At startup, dump the would-be §gen DOP for the 1 B/5 B reciprocal sizes to `%TEMP%\dopgate_test.txt`. |
| `PI_CG_ISOLATE` | off (`1` = on) | `--test-chunkedgrid`: run only the 68 M×52 M case. |
| `PI_CELLSWEEP_GEN` | off (`1` = on) | `--test-cellsweep`: also run the §gen recursive baseline (RAM-heavy; may page). |
| `PI_DOPSCAN_LIMBS` | `24000000` | `--test-dopscan` / `--test-gridscan` operand size in limbs (min 6,000,000). |
| `PI_RECIPCONV_LIMBS` | `1000000` | `--test-recipconv` reciprocal operand size in limbs (min 1,000). |
| `PI_RECIPCONV_KDIV` | `1` | `--test-recipconv`: `kBits = bBits + bBits/KDIV`; set `3` to mimic the real divide-reciprocal regime. |

---

## Change Log

The detailed change log — every individual change from the original implementation to the current code, with root-cause explanations — has been moved to a separate file:

**→ [Change_LOG.md](Change_LOG.md)**
