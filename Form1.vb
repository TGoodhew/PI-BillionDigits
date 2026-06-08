Option Strict On
Option Explicit On

' ── Logging level (runtime, set via --log-level N or the UI spinner) ─────────
' 0  None        Errors and crashes only. Silent on success.
' 1  Performance [PHASE] markers with timing (default).
' 2  Stages      Per-phase step detail: file I/O, initial calc steps, node sizes.
' 3  Last stage  Full per-operation trace for the final combine and ComputePiGMP.
' 4  Full trace  Everything in 3, plus SafeMpzMul diagnostics and BinarySplitChunk.
' 5  Allocator   Everything in 4, plus pool/affinity diagnostics.
' ────────────────────────────────────────────────────────────────────────────

Imports System.Numerics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Collections.Concurrent
Imports System.Runtime.InteropServices
Imports Math.Gmp.Native
Imports System.Diagnostics
Imports System.Security.Cryptography

Public Class Form1

    Private stopWatch As New Stopwatch()
    Private phaseStopWatch As New Stopwatch()
    Private cts As CancellationTokenSource
    Private DIGITS As Long
    ' Output directory: defaults to %LOCALAPPDATA%\PI-BillionDigits — always writable,
    ' no admin rights required, works on any machine regardless of drive letter.
    ' All output paths (digits file, log file, node cache) derive from this one field.
    Private Shared _outputDir As String = "C:\PiOutput"
    Private ReadOnly Property outputFile As String
        Get
            Return System.IO.Path.Combine(_outputDir, "pi_digits.txt")
        End Get
    End Property
    Private displayStr As String = ""
    Private displayIdx As Integer = 0
    Private displayTotal As Long = 0
    ' Native streaming: pointer into the GMP-allocated char buffer + length.
    ' When non-zero, DisplayTimer_Tick reads bytes via Marshal.Copy bulk copy.
    Private _displayNativePtr As IntPtr = IntPtr.Zero
    Private _displayNativeLen As Long = 0
    Private _displayNativeBufSize As Long = 0   ' GmpAllocFunc alloc size; >= GMP_LARGE_THRESHOLD → VirtualAlloc'd
    ' §117 (#117): WriteResultToFile records the file-save outcome here so the terminal verify status
    ' can compose it.  A save failure must NOT be clobbered by the in-memory verify (which reads the
    ' native buffer, not the file on disk), or a broken/partial/missing pi_digits.txt reads as success.
    ' False also covers "no save attempted" (Write-to-File off).
    Private _saveFailed As Boolean = False
    Private _saveErrorMsg As String = ""
    ' §74 (issue #74): chunked decimal converter progress.  Written by the compute thread
    ' inside ChunkedMpzGetStr, read by the _strConvTimer status-bar callback.  64-bit
    ' aligned ordinary longs — on x64 aligned 64-bit writes are atomic, so the timer never
    ' observes a torn read.  Both reset to 0 in ChunkedMpzGetStr's Finally so a subsequent
    ' run that skips the chunked path doesn't show stale "12 of 99" data.
    Private _chunkConvCurrent As Long = 0
    Private _chunkConvTotal As Long = 0
    ' §81 display perf: pre-allocated byte buffer reused across ticks (avoids per-tick allocation).
    Private _displayBuf() As Byte = New Byte(65535) {}   ' initial 64 KB; grown as adaptive chunk size increases
    ' §81 adaptive chunk size: starts at 4096, adjusted each tick to target ~80 ms of UI work.
    Private _displayChunkSize As Integer = 4096
    ' §81 scroll throttle: accumulates chars since last ScrollToCaret; scroll only every 10,000 chars.
    Private _displayScrollAccum As Integer = 0
    ' §271 (#98): MOVABLE WINDOW over the full digit range.  Streaming a 1B/5B-digit string into a
    ' RichTextBox is ~O(n²) (AppendText is O(current length)) and holds GB of text on top of the
    ' native buffer.  Instead, show a bounded NAV_WINDOW_DIGITS-wide window read on demand from the
    ' native buffer, and a TrackBar that scrubs the window across all the digits (O(1) per move,
    ' constant memory).  Built lazily when a large native result is shown.
    Private Const NAV_WINDOW_DIGITS As Integer = 250_000
    Private Const NAV_TRACKBAR_STEPS As Integer = 10000   ' slider resolution (offset = value/steps × maxOffset)
    Private _navTrackBar As System.Windows.Forms.TrackBar = Nothing
    Private _navLabel As System.Windows.Forms.Label = Nothing
    Private _navTotalDigits As Long = 0
    Private _navOffset As Long = 0
    Private WithEvents displayTimer As New System.Windows.Forms.Timer()
    Private gmpC3Const As mpz_t = Nothing

    ' ── Headless / command-line mode ─────────────────────────────────────────
    ' Set by --autostart (suppress all dialogs) and --autoverify (run verify +
    ' Application.Exit after computation completes).
    Private _headless As Boolean = False
    ' §119 (#119): "Auto-OK dialogs" — suppress spontaneous run-blocking dialogs (info/error/global
    ' unhandled-exception) the same way _headless does, for an UNATTENDED interactive run.  Shared so
    ' the global handler in ApplicationEvents.vb can read it; set True whenever _headless is set so the
    ' headless path never blocks on the global modal either.  Destructive Close/Cancel confirms are NOT
    ' auto-answered (they are user-initiated; auto-answering would risk silent data loss).
    Friend Shared _suppressDialogs As Boolean = False
    ' §119 (#119): Windows Event Log source for last-resort crash preservation (used when the global
    ' unhandled-exception handler's normal pi_phase_log.txt write fails).  Scanned at startup so a crash
    ' whose file-log could not be written is surfaced into the next run's log instead of being lost.
    Friend Const EVENTLOG_SOURCE As String = "PI-BillionDigits"
    Private _priorCrashNote As String = ""
    ' §252 (#95): single integer logging scale.  Every log goes through AppendLog(msg, level);
    ' a message is written iff level <= _logLevel.  Strict superset ladder:
    '   0 = Silent (nothing)
    '   1 = Errors + final result (crashes/exceptions/OOM, final digit count + verify outcome)
    '   2 = Phase milestones (default): Phase 1/2/3 start/done, checkpoints, reciprocal/sqrt/divide
    '       start+done, DOP/MemoryBudget decisions, verify detail
    '   3 = Sub-phase progress: per-level combine progress, per-Newton-iter, divide stages
    '   4 = Detailed diagnostics: per-large-mul path/size decisions, reuse (§201/§230) detail
    '   5 = Exceptionally detailed (debugging, expected slow): per-sub-product §gen limb dumps,
    '       [NR1xx] limb traces, [BSR§129] per-chunk, §5B-* references, native per-alloc logging
    ' Helpers: AppendLog(msg, Optional level=2), WriteToLog(msg, Optional level=2) [adds timestamp],
    ' LogPhase [milestone, level 2 + UI].  --log-level N / UI spinner set _logLevel (0-5, default 2).
    Private Shared _logLevel As Integer = 2
    Private _autoVerify As Boolean = False
    ' §253 (#52): UI status hook so Shared compute code (SafeMpzReciprocal/SafeMpzDiv) can post a
    ' one-line progress string to LblStatus.  Set by Form1_Load to marshal onto the UI thread;
    ' null/try-wrapped so headless runs and races are harmless.  Not gated by _logLevel (it's the UI).
    Private Shared _statusHook As Action(Of String) = Nothing
    ' §259 (#62): per-iter reciprocal ETA refinement hook (iterDone, minIters). Set by Form1_Load.
    Private Shared _etaReciprocalHook As Action(Of Integer, Integer) = Nothing
    ' §93: Checkpoint/resume support.
    ' --checkpoint-from-level N: serialize nodes at level >= N regardless of threshold.
    ' --resume-from-level N: skip Phase 1 + levels 1..N-1; reload checkpoint files for level N.
    Private _checkpointFromLevel As Integer = Integer.MaxValue   ' disabled by default
    Private _resumeFromLevel As Integer = 0                      ' 0 = full run from scratch
    ' §94: Level-boundary auto-checkpoint/resume.
    ' --auto-checkpoint: write a RAM snapshot at the end of each Phase 2 level and
    '   auto-resume from the highest valid snapshot on the next run.  All combine
    '   work still runs in RAM; the snapshot is written as a batch after each level
    '   completes, before GC/FlushGmpPool while nodes are still live.
    Private Shared _autoCheckpoint As Boolean = False
    ' §171-ckpt: scope label set by SafeMpzDiv callers (e.g., "sqrt_step_5", "phase4")
    ' to disambiguate div_q.bin checkpoints across distinct call sites.
    Private Shared _divCkptScope As String = ""
    ' §214 (2026-05-15, issue #67): True iff TryLoadPhase3SnapshotTOnly fired during the
    ' snap_Phase3 load — means finalP and finalQ are mpz_init'd to 0 (NOT populated).  The
    ' gmpNumer-resume path at line ~6250 MUST fire when this is True; if it fails (corrupt
    ' gmpNumer.bin or other surprise), control would fall through to Step 1+ which assumes
    ' P and Q are loaded.  The defensive check at the §214-assert site throws if this
    ' invariant is violated.
    Private Shared _p3TOnlyLoadActive As Boolean = False
    ' Custom verify checks supplied via --verify-at "DIGITS:POSITION" and
    ' --verify-contains "DIGITS".  Populated during CLI arg parsing; consumed
    ' by RunCustomVerifications() which is called from BtnTest_Click.
    Private _verifyAt As New List(Of Tuple(Of String, Long))()   ' (digits, expectedPos)
    Private _verifyContains As New List(Of String)()

    ' §73: RAM/Disk threshold — read from NudRamThreshold at compute start.
    ' Controls whether Phase 1 chunks and Phase 2 levels stay in RAM or spill to disk.
    Private _diskThreshold As Integer = 200_000

    ' §69: Controls SafeMpzMul inner Parallel.For DOP.
    ' Set to 1 before Phase 2 parallel Parallel.For so sub-products run serially —
    ' eliminates the thread-pool park/unpark cycle (18.77% LowLevelLifoSemaphore in
    ' trace 4) caused by 24-outer × 2-Invoke × 9-inner = 432 tasks on 24 threads.
    ' Restored to ProcessorCount before Phase 2 serial path and ComputePiGMP so
    ' those single-pair operations still use all cores.
    Private Shared _safeMulDop As Integer = -1   ' -1 = not yet set; reads as ProcessorCount
    ' §265 (#88): chunked-grid cell-size override for the --test-gridscan split-factor experiment.
    ' 0 = production default (1.5M).  Only the benchmark ever writes it.
    Private Shared _cgCellOverride As Integer = 0

    ' §238 (issue #87, 2026-05-28): thread-local nesting cap.  Set True inside the
    ' Parallel.For sub-product lambda in SafeMpzMul; any recursive SafeMpzMul that
    ' runs on the same thread sees True and forces _smmDop=1.  Caps the nested-
    ' Parallel.For memory explosion that crashed sqrt_step_6 at 5 B (§220 had
    ' lifted §168/§166's force-serial protection; this restores the design intent
    ' in one central place instead of policing it at every call site).
    <System.ThreadStatic> Private Shared _smm_innerForceSerial As Boolean

    ' §250 (issue #94): high-half ("short") product flags for the capped-precision
    ' reciprocal Newton iters.  _testMulHigh: --test-mulhigh ran the standalone bit-
    ' correctness self-test (then exit).  Set in the arg loop; honoured after the
    ' GMP allocator is installed.
    Private Shared _testMulHigh As Boolean = False
    ' §251 (#70): --test-chunkedgrid ran the standalone chunked-grid self-test (then exit).
    Private Shared _testChunkedGrid As Boolean = False
    ' §259/§260 (#62/#63): --test-eta / --test-advisor run the estimator + advisor self-tests, then exit.
    Private Shared _testEta As Boolean = False
    Private Shared _testAdvisor As Boolean = False
    Private Shared _testDopScan As Boolean = False   ' §263 (#88): DOP/bandwidth-saturation microbenchmark
    Private Shared _testGridScan As Boolean = False  ' §265 (#88): split-factor (k×k) experiment
    Private Shared _testCellSweep As Boolean = False ' §266 (#88): cell-size sweep at 5B operand sizes
    Private Shared _testRecipConv As Boolean = False ' §272 (#88): reciprocal-Newton convergence probe
    ' §272 (#88): when set (only by --test-recipconv), SafeMpzReciprocal logs correct-bits per
    ' Newton iter against this reference R_ref = floor(2^kBits / b).  Reveals whether the seed
    ' delivers ~62 correct bits (⇒ the §200 forced tail iters are wasted, recoverable by an
    ' early-exit detector) or only ~1 bit (⇒ the seed scaling is lossy and needs fixing).
    ' Strictly test-gated; the instrumentation is a null-check no-op in production runs.
    Private Shared _recipConvRef As mpz_t = Nothing
    ' Set at SafeMpzReciprocal entry from env: PI_RECIP_SHORTMUL=1 enables the high-half
    ' product in capped iters; PI_RECIP_SHORTMUL_VERIFY=1 additionally computes the full
    ' product, compares the exact region + overestimate, and falls back to full on any
    ' mismatch (safety net + per-iter diagnostic for the validation gate).
    Private Shared _recipShortMul As Boolean = True    ' §254 (#70): chunked reciprocal ON by default (opt-out PI_RECIP_SHORTMUL=0)
    Private Shared _recipShortMulVerify As Boolean = False
    ' §251 (#70): DOP gate.  pt1 (serial cells) only beat §gen at low §gen DOP, so this gated on
    ' MemBudget_SuggestSafeMulDop ≤ threshold.  pt2 PARALLELISES the chunked cells (tiny, fit at
    ' 16-way even under memory pressure), so chunked now beats §gen at EVERY DOP — measured 2.81×
    ' (rSq 26M²) / 6.97× (p 68M×52M) vs §gen at DOP=9.  Default raised to 9 (§gen's max DOP) ⟹
    ' chunked engages broadly; env PI_RECIP_SHORTMUL_MAXDOP can lower it to restrict.
    Private Shared _recipShortMulMaxDop As Integer = 9
    ' §262 (#42): route the dominant SafeMpzDiv a×r through the chunked-grid HIGH product.  Only the
    ' bits above the >>kBits cut survive (q = ar >> kBits), so the low cells are computed-then-thrown-
    ' away by the full §gen multiply.  Computing a×r as a high product (skip those cells, round-up
    ' overestimate ⇒ q overestimate ⇒ §171 adj-down corrects, the §107 contract) attacks the
    ' dominant 5B divide cost (a×r ≈ 5h40m vs q×b ≈ 1h34m) AND ~halves its peak RAM.  ON by default
    ' (opt-out PI_DIV_AR_SHORTMUL=0); reuses the proven #70 SafeMpzMul_ChunkedGrid + DOP gate.
    Private Shared _divArShortMul As Boolean = True
    ' §269 (#88): route q×b (full product) through the chunked grid too.  §gen recursive q×b is the
    ' divide's remaining bottleneck (~1h34m at 5B) — §267 only accelerated the reciprocal+a×r.  q×b
    ' is a FULL product (keepLimbs=0; need all of it for rem = a−q×b); chunked-full is bit-exact and,
    ' with the §268 adaptive 16M cell, far faster than §gen recursion.  ON by default (opt-out
    ' PI_DIV_QB_CHUNKED=0).
    Private Shared _divQbChunked As Boolean = True
    ' §273 (#121/#122): route the top binary-split combine merges through the chunked-grid path
    ' instead of §gen at the §231 serial-path DOP cap (3 of 24 cores at ≥250M terms).  The §231
    ' cap is RAM-bound — §gen's recursive sub-products are GB-scale, so its DOP^3 buffer growth
    ' OOMs above DOP=3 at 5B.  Chunked-grid cells are tiny (≤16M-limb ⇒ ≤256MB cell products) so
    ' it parallelises at PI_CG_DOP (default ProcessorCount, capped 16) at low RAM — the same reason
    ' it already won the divide (§262 a×r, §269 q×b).  Full product (keepLimbs=0) is bit-identical
    ' to §gen (proven by --test-chunkedgrid); SafeMpzMulCG adds the sign handling the alternating-
    ' series T merges need.  ON by default (opt-out PI_COMBINE_CG=0); engages only when numTerms ≥
    ' _combineCgMinTerms (default 250M — exactly the levels §231 pins to DOP=3), tunable via
    ' PI_COMBINE_CG_MINTERMS.
    Private Shared _combineChunkedGrid As Boolean = True
    Private Shared _combineCgMinTerms As Long = 250_000_000L
    ' §274 (#121): the same routing for the divide's three numerator R-multiplies (r0/r1/r2 =
    ' gmpNumer × Q_i — the §7/§46/§47 three-pass `gmpNumer *= finalQ`, computed via §233).  They
    ' also run at the §233 DOP cap (3 of 24 cores at ≥250M terms) and cost ~4h27m at 5B (r0 ~62m,
    ' r1 ~99m, r2 ~104m on the 2026-06-08 baseline).  Full products routed through SafeMpzMulCG.
    ' ON by default (opt-out PI_NUMER_CG=0); same numTerms ≥ _numerCgMinTerms gate (default 250M,
    ' tunable via PI_NUMER_CG_MINTERMS) — exactly the levels §233 pins to DOP=3.
    Private Shared _numerChunkedGrid As Boolean = True
    Private Shared _numerCgMinTerms As Long = 250_000_000L
    ' §275 (#121): route SafeMpzSqrt's final-adjustment squarings (xSq=x², x1Sq=(x+1)²) through the
    ' chunked grid.  The sqrt-Newton loop already rides on SafeMpzDiv (chunked via §262/§269/§272),
    ' but the final adjustment squares the ~half-width root via §gen SafeMpzMul — "Step 4" ≈ 2h42m at
    ' 5B on the 2026-06-08 baseline.  Chunked-grid squaring is bit-exact (--test-chunkedgrid sq=True)
    ' and 3.5–6.8× faster; when it engages the two squarings run sequentially (each already saturates
    ' PI_CG_DOP cores, so no Parallel.Invoke oversubscription).  ON by default (opt-out PI_SQRT_CG=0);
    ' engages when the squaring operand ≥ PI_SQRT_CG_MINLIMBS (default 30M limbs ≈ the ≥3.5B-digit
    ' regime; smaller sqrt work is already fast).  Gated on operand SIZE (SafeMpzSqrt has no numTerms).
    Private Shared _sqrtChunkedGrid As Boolean = True
    Private Shared _sqrtCgMinLimbs As Long = 30_000_000L

    ' ── Thread-safe logging for GMP allocator callbacks ──────────────────────
    ' VirtualAlloc / VirtualFree / CRT malloc / CRT free are all intrinsically
    ' thread-safe.  Only the File.AppendAllText log writes need serialisation so
    ' that concurrent allocator callbacks from parallel worker threads don't race
    ' on the log file and lose entries (or silently throw IOException).
    Private Shared ReadOnly _logLock As New Object()

    ' §252 (#95): single gated sink.  Writes iff level <= _logLevel.  Default level 2 (phase
    ' milestones) so the ~155 historically-ungated AppendLog(msg) calls become level-2 (and are
    ' suppressed at level 0/1).  Pass level:=1 for errors/result, 3-4 for progress/detail, 5 for
    ' per-op limb-dump spam.  See the ladder at the _logLevel declaration.
    Private Shared Sub AppendLog(message As String, Optional level As Integer = 2)
        If level > System.Threading.Volatile.Read(_logLevel) Then Return   ' §27 cross-thread read
        SyncLock _logLock
            Try
                System.IO.File.AppendAllText(LOG_FILE, message)
            Catch
                ' Swallow — log failures must never crash the allocator callbacks.
            End Try
        End SyncLock
    End Sub

    ' Disk-based node storage for massive computations
    Private Shared ReadOnly Property DISK_CACHE_DIR As String
        Get
            Return System.IO.Path.Combine(_outputDir, "NodeCache") & System.IO.Path.DirectorySeparatorChar
        End Get
    End Property

    ' ── Issue #4 fix: DiskNode changed from Class to Structure ──────────────
    ' Value types stored in List(Of DiskNode) live inside the list's internal
    ' array as contiguous memory — no individual heap allocations per node,
    ' no Gen0/Gen1 pressure from the ~137 K nodes created per billion digits.
    '
    ' Issue #6 fix (partial): MemP/MemQ/MemT replace Tuple(Of mpz_t,mpz_t,mpz_t)
    ' so no throw-away Tuple object is created when storing in-memory nodes.
    Private Structure DiskNode
        Public FilePath As String
        Public FileOffset As Long  ' byte offset within FilePath; 0 for individual node files
        Public IsInMemory As Boolean
        Public MemP As mpz_t   ' used only when IsInMemory = True
        Public MemQ As mpz_t
        Public MemT As mpz_t
        Public Level As Integer
        Public Index As Integer
    End Structure

    ' Structures for iterative binary splitting
    Private Structure WorkItem
        Public a As Long
        Public b As Long
        Public resultIndex As Integer
        Public isComplete As Boolean
        Public leftChildIndex As Integer
        Public rightChildIndex As Integer
    End Structure

    Private Structure Result
        Public P As mpz_t
        Public Q As mpz_t
        Public T As mpz_t
    End Structure

    ' ═══════════════════════════════════════════════════════════════════════
    ' (§261 / #40) Removed dead code: an earlier custom GMP memory pool — a bump
    ' allocator over a 20 GB VirtualAlloc reservation — was abandoned because it
    ' violated GMP's free/realloc contract (freed blocks must become reusable;
    ' CInt on the pool offset also overflowed at 2 GB), corrupting metadata and
    ' crashing after ~300 iterations.  Production uses GmpNativeAlloc.dll (native
    ' SLIST pool, #30) with the managed system allocator as fallback; the live
    ' VirtualAlloc/VirtualFree wrappers for ≥512 KB blocks are declared below.
    ' The ~78-line commented-out implementation lived here until §261 — see git.
    ' ═══════════════════════════════════════════════════════════════════════

    ' ── GMP VirtualAlloc custom memory functions ─────────────────────────────
    ' Problem: GMP's default Windows CRT allocator keeps freed large blocks in
    ' its private heap free-list instead of calling VirtualFree.  When several
    ' large multiply+free cycles happen sequentially (binary split combine,
    ' sqrt, then 3-pass multiply), the "committed but idle" pages accumulate
    ' until the system commit limit is reached and malloc() returns NULL, which
    ' GMP turns into abort().  The working-set reading looks fine (e.g. 450 MB)
    ' but the actual committed bytes can be 2-3x higher.
    '
    ' Fix: replace GMP's allocator with one that uses VirtualAlloc/VirtualFree
    ' directly for allocations >= 512 KB.  VirtualFree immediately decommits
    ' those pages, keeping committed memory proportional to live data.
    ' Allocations < 512 KB delegate to GMP's own default CRT allocator so that
    ' we never mix the CRT heap with Marshal.AllocHGlobal or VirtualAlloc for
    ' the same small block — heap mismatches corrupt GMP's internal state.
    Private Const GMP_LARGE_THRESHOLD As Long = 524288L   ' 512 KB
    ' §111 (#111): GMP's Schönhage–Strassen FFT keeps internal sizes in a 32-bit mp_size_t on Windows;
    ' a single operand must stay below 2^25−1 = 33,554,431 limbs or the FFT can return wrong products.
    ' SafeMpzMul / the reciprocal / divide / sqrt all split below this cap.  Named here so the bound is
    ' not three bare 33_554_431 literals.
    Friend Const GMP_FFT_LIMB_CAP As Integer = 33_554_431
    ' §111 (#111): bytes per MB, for readability where a size is divided/multiplied to report MB.  (Most
    ' of the ~120 existing `\ 1048576L` sites are left as-is for now — a blanket sweep is a churny,
    ' merge-risky change deferred out of the pre-5B batch; new code should prefer this constant.)
    Friend Const BYTES_PER_MB As Long = 1048576L

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function VirtualAlloc(lpAddress As IntPtr,
                                          dwSize As UIntPtr,
                                          flAllocationType As UInteger,
                                          flProtect As UInteger) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function VirtualFree(lpAddress As IntPtr,
                                         dwSize As UIntPtr,
                                         dwFreeType As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", EntryPoint:="RtlMoveMemory")>
    Private Shared Sub CopyMemory(dest As IntPtr, src As IntPtr, length As UIntPtr)
    End Sub

    ' §229 (issue #56, 2026-05-23): native zero-fill used by ParallelBigShiftLeftOOP to clear
    ' the limb-offset region when the destination buffer is reused (not freshly VirtualAlloc'd).
    <DllImport("kernel32.dll", EntryPoint:="RtlZeroMemory")>
    Private Shared Sub ZeroMemory(dest As IntPtr, length As UIntPtr)
    End Sub

    ' §216: strlen used to measure GMP-returned char buffers in chunked decimal conversion.
    <DllImport("msvcrt.dll", CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function strlen(s As IntPtr) As UIntPtr
    End Function

    Private Const MEM_COMMIT_RESERVE As UInteger = &H3000UI  ' MEM_COMMIT | MEM_RESERVE
    Private Const MEM_RELEASE As UInteger = &H8000UI
    Private Const VA_PAGE_READWRITE As UInteger = &H4UI

    ' ── Power throttling — prevent Windows from routing this process to E-cores ──
    ' On hybrid CPUs (Intel 12th gen+) Windows Efficiency Mode moves backgrounded
    ' processes to efficiency cores and halves their scheduler quota.  Opting out
    ' via SetProcessInformation keeps the compute thread on P-cores at full boost.
    Private Const PROCESS_POWER_THROTTLING_CURRENT_VERSION As UInteger = 1UI
    Private Const PROCESS_POWER_THROTTLING_EXECUTION_SPEED As UInteger = 1UI
    Private Const ProcessPowerThrottling As Integer = 9  ' PROCESS_INFORMATION_CLASS

    <StructLayout(LayoutKind.Sequential)>
    Private Structure PROCESS_POWER_THROTTLING_STATE
        Public Version As UInteger
        Public ControlMask As UInteger
        Public StateMask As UInteger
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetProcessInformation(
        hProcess As IntPtr,
        ProcessInformationClass As Integer,
        ByRef ProcessInformation As PROCESS_POWER_THROTTLING_STATE,
        ProcessInformationSize As UInteger) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetCurrentProcess() As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetProcessAffinityMask(
        hProcess As IntPtr,
        dwProcessAffinityMask As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function OpenThread(
        dwDesiredAccess As UInteger,
        bInheritHandle As Boolean,
        dwThreadId As UInteger) As IntPtr
    End Function

    ' §247 (#48/#49): OS thread id of the calling thread, to register E-core I/O threads
    ' with the affinity watchdog's exempt set (the watchdog keys on ProcessThread.Id).
    <DllImport("kernel32.dll")>
    Private Shared Function GetCurrentThreadId() As UInteger
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetThreadAffinityMask(
        hThread As IntPtr,
        dwThreadAffinityMask As IntPtr) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    Private Const THREAD_SET_INFORMATION As UInteger = &H20UI
    Private Const THREAD_QUERY_INFORMATION As UInteger = &H40UI

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetLogicalProcessorInformationEx(
        relationshipType As Integer,
        buffer As IntPtr,
        ByRef returnedLength As UInteger) As Boolean
    End Function

    Private Const RelationProcessorCore As Integer = 0

    ''' <summary>
    ''' Detects hybrid CPU topology (P-cores vs E-cores) via
    ''' GetLogicalProcessorInformationEx and restricts the process affinity mask
    ''' to P-cores only.  On a non-hybrid CPU, or if detection fails, the affinity
    ''' mask is left unchanged.
    '''
    ''' Intel 12th gen+ (Alder Lake) and AMD Zen 4c hybrid designs expose
    ''' EfficiencyClass in PROCESSOR_RELATIONSHIP:
    '''   EfficiencyClass = 0  → E-core (lower power, lower single-thread perf)
    '''   EfficiencyClass > 0  → P-core (full-power, preferred for GMP math)
    '''
    ''' Restricting to P-cores prevents the thread-pool from placing GMP arithmetic
    ''' workers on E-cores, which run at reduced clock speeds and share L2 caches
    ''' differently, causing unexpected slowdowns in data-parallel workloads.
    ''' </summary>
    Private Shared Sub SetPCoreAffinity()
        Try
            ' First call: get required buffer size
            Dim bufferSize As UInteger = 0
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, bufferSize)
            If bufferSize = 0 Then
                AppendLog($"[Affinity] GetLogicalProcessorInformationEx size query failed{vbCrLf}")
                Return
            End If

            Dim buffer As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(CInt(bufferSize))
            Try
                If Not GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, bufferSize) Then
                    AppendLog($"[Affinity] GetLogicalProcessorInformationEx data query failed{vbCrLf}")
                    Return
                End If

                ' Parse SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records.
                ' Each record layout (RelationProcessorCore):
                '   +0  Relationship  : DWORD (4)
                '   +4  Size          : DWORD (4)
                '   +8  Flags         : BYTE  (1)
                '   +9  EfficiencyClass: BYTE (1) — 0=E-core, >0=P-core
                '  +10  Reserved[20]  : BYTE (20)
                '  +30  GroupCount    : WORD  (2)
                '  +32  GroupMask[0].Mask : ULONG_PTR (8 on x64)
                '  +40  GroupMask[0].Group: WORD (2)
                '  +42  GroupMask[0].Reserved: 6 bytes
                Dim pCoreMask As Long = 0L
                Dim eCoreMask As Long = 0L
                Dim offset As Integer = 0

                Do While offset < CInt(bufferSize)
                    Dim recordSize As Integer = Runtime.InteropServices.Marshal.ReadInt32(buffer, offset + 4)
                    If recordSize <= 0 Then Exit Do

                    Dim efficiencyClass As Byte = Runtime.InteropServices.Marshal.ReadByte(buffer, offset + 9)
                    Dim mask As Long = Runtime.InteropServices.Marshal.ReadInt64(buffer, offset + 32)

                    If efficiencyClass > 0 Then
                        pCoreMask = pCoreMask Or mask
                    Else
                        eCoreMask = eCoreMask Or mask
                    End If

                    offset += recordSize
                Loop

                If pCoreMask <> 0L AndAlso eCoreMask <> 0L Then
                    ' §247 (#48/#49): keep the process mask on ALL cores (P|E) — NOT a hard
                    ' lock to P.  The watchdog hard-pins compute threads to P (where it benefits
                    ' the algorithm); the otherwise-idle E-cores stay AVAILABLE for the I/O
                    ' threads that pin themselves there (and are exempted from the watchdog).
                    ' A thread's SetThreadAffinityMask cannot exceed the process mask, so the
                    ' process must include E for E-core pinning to take effect.
                    If SetProcessAffinityMask(GetCurrentProcess(), New IntPtr(pCoreMask Or eCoreMask)) Then
                        AppendLog($"[Affinity] Hybrid CPU. P-core mask=0x{pCoreMask:X} E-core mask=0x{eCoreMask:X}. Process on all cores; watchdog pins compute to P, E-cores free for I/O.{vbCrLf}")
                        System.Threading.Volatile.Write(_pCoreMask, pCoreMask)   ' §106: save for watchdog
                    Else
                        AppendLog($"[Affinity] Hybrid CPU detected but SetProcessAffinityMask failed. P=0x{pCoreMask:X} E=0x{eCoreMask:X}{vbCrLf}")
                    End If
                Else
                    ' Uniform CPU — all cores same efficiency class; no change needed
                    AppendLog($"[Affinity] Uniform CPU (no E-cores detected). P=0x{pCoreMask:X} E=0x{eCoreMask:X}. Affinity unchanged.{vbCrLf}")
                End If
            Finally
                Runtime.InteropServices.Marshal.FreeHGlobal(buffer)
            End Try
        Catch ex As Exception
            AppendLog($"[Affinity] Exception during P-core detection: {ex.Message}{vbCrLf}")
        End Try
    End Sub

    ' §106: Affinity watchdog — re-applies the P-core mask to every thread every 500 ms.
    ' SetProcessAffinityMask only constrains new threads; existing threads that drifted
    ' to E-cores under competing load are never migrated back automatically.  The watchdog
    ' iterates all process threads and calls SetThreadAffinityMask on each one, forcing
    ' them back to P-cores within one watchdog interval.
    Private Shared Sub StartAffinityWatchdog()
        Dim mask As Long = System.Threading.Volatile.Read(_pCoreMask)
        If mask = 0L Then Return   ' uniform CPU or detection failed — nothing to do

        _affinityWatchdogToken = New System.Threading.CancellationTokenSource()
        Dim token As System.Threading.CancellationToken = _affinityWatchdogToken.Token
        Dim t As New System.Threading.Thread(
            Sub()
                Dim affinityPtr As New IntPtr(mask)
                While Not token.IsCancellationRequested
                    Try
                        For Each pt As Diagnostics.ProcessThread In
                                Diagnostics.Process.GetCurrentProcess().Threads
                            ' §247: skip E-core I/O threads (Phase 1 serializers / Phase 2
                            ' prefetch) — they pin themselves to E-cores and must not be
                            ' dragged back to P.  Compute/general threads are still pinned to P.
                            If _affinityExempt.ContainsKey(CUInt(pt.Id)) Then Continue For
                            Dim h As IntPtr = OpenThread(
                                THREAD_SET_INFORMATION Or THREAD_QUERY_INFORMATION,
                                False, CUInt(pt.Id))
                            If h <> IntPtr.Zero Then
                                SetThreadAffinityMask(h, affinityPtr)
                                CloseHandle(h)
                            End If
                        Next
                    Catch
                        ' Process.Threads can throw if a thread exits mid-enumeration
                    End Try
                    System.Threading.Thread.Sleep(500)
                End While
            End Sub)
        t.IsBackground = True
        t.Name = "AffinityWatchdog"
        t.Priority = System.Threading.ThreadPriority.BelowNormal
        t.Start()
        AppendLog($"[Affinity] Watchdog started (P-core mask=0x{mask:X}, interval=500ms){vbCrLf}")
    End Sub

    Private Shared Sub StopAffinityWatchdog()
        Try
            _affinityWatchdogToken?.Cancel()
        Catch
        End Try
    End Sub

    Private Shared Sub DisablePowerThrottling()
        Dim state As New PROCESS_POWER_THROTTLING_STATE With {
            .Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            .ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            .StateMask = 0UI   ' 0 = disable throttling (1 would enable it)
        }
        SetProcessInformation(GetCurrentProcess(),
                              ProcessPowerThrottling,
                              state,
                              CUInt(Runtime.InteropServices.Marshal.SizeOf(state)))
    End Sub

    ' §106: P-core affinity mask saved by SetPCoreAffinity for use by the watchdog.
    Private Shared _pCoreMask As Long = 0L
    Private Shared _affinityWatchdogToken As System.Threading.CancellationTokenSource = Nothing
    ' §247 (#48/#49): TIDs the affinity watchdog must NOT re-pin to P-cores — the E-core I/O
    ' threads (Phase 1 serializers, Phase 2 prefetch).  Registered by PinCurrentThreadToECores,
    ' cleared by UnpinCurrentThreadFromECores.  Implements the user's "prefer P, don't lock out
    ' E unless it benefits the algorithm": compute stays hard-pinned to P (watchdog), the
    ' otherwise-idle E-cores carry I/O.  Empty/no-op on a non-hybrid host.
    Private Shared ReadOnly _affinityExempt As New System.Collections.Concurrent.ConcurrentDictionary(Of UInteger, Byte)()

    ' GC-anchor ALL six delegates — collected delegates crash the process.
    ' Shared so the Shared callback methods can reach the saved defaults.
    Private Shared _gmpAlloc As allocate_function
    Private Shared _gmpRealloc As reallocate_function
    Private Shared _gmpFree As free_function
    Private Shared _savedGmpAlloc As allocate_function   ' GMP's original CRT alloc
    Private Shared _savedGmpRealloc As reallocate_function
    Private Shared _savedGmpFree As free_function

    ' ── GMP limb buffer pool ──────────────────────────────────────────────────
    ' Trace 1: GmpAllocFunc (21.8%) + GmpFreeFunc (19.0%) = 40.8% — VirtualAlloc/Free kernel cost.
    ' Trace 2: ConcurrentDictionary pool → Monitor.Wait appeared at 5.55% because
    '   GetOrAdd acquires an internal dictionary lock when inserting a new size class.
    '   Under 24 parallel threads hitting new size classes simultaneously, threads
    '   blocked on each other inside the dictionary.
    '
    ' Fix: power-of-2 bucketed fixed array.  64 pre-allocated ConcurrentStack slots,
    ' indexed by floor(log2(sz)).  No dictionary, no Monitor — only
    ' Interlocked.CompareExchange inside ConcurrentStack.TryPop/Push.
    '
    ' Bucket b covers requests in [2^(b-1)+1, 2^b].  We allocate 2^b bytes so the
    ' block always satisfies the request.  GmpFreeFunc receives the original requested
    ' size, computes the same bucket, and returns the block correctly.
    '
    ' POOL_MAX_BLOCK = 16 MB: blocks above this bypass the pool (VirtualFree directly).
    ' Top-level combine blocks grow with every level; pooling them accumulated 38 GB of
    ' committed-but-idle pages that caused VirtualAlloc to fail (observed crash at L16).
    ' Flushing between Phase 2 levels further ensures committed memory tracks live data.
    '
    ' Cap = POOL_CAP blocks per bucket; excess blocks are VirtualFree'd immediately.
    ' §68: raised from 32 → 256. With outer Phase 2 DOP=24 each doing Parallel.Invoke(2
    ' SafeMpzMul), up to 48 concurrent GMP operations can return blocks simultaneously.
    ' A cap of 32 caused constant pool eviction (VirtualFree) + re-miss (VirtualAlloc).
    Private Const POOL_CAP As Integer = 256
    Private Const POOL_MAX_BLOCK As Long = 16L * 1024L * 1024L  ' 16 MB — max pooled block
    Private Const POOL_BUCKETS As Integer = 64
    ' Initialised in InitGmpVirtualAllocFunctions before the first GMP call.
    Private Shared _gmpPool(POOL_BUCKETS - 1) As ConcurrentStack(Of IntPtr)
    ' §20: Per-bucket atomic counters replacing ConcurrentStack.Count (which is O(n)).
    ' Interlocked.Increment/Decrement are single atomic instructions vs. linked-list walk.
    Private Shared _gmpPoolCount(POOL_BUCKETS - 1) As Integer

    ''' <summary>
    ''' Returns the pool bucket index for a given byte size.
    ''' §22: Uses BitOperations.Log2 (single LZCNT/BSR instruction on x64) replacing
    ''' the previous O(log n) bit-counting While loop.
    ''' </summary>
    Private Shared Function PoolBucket(sz As Long) As Integer
        If sz <= 1L Then Return 0
        Dim b As Integer = System.Numerics.BitOperations.Log2(CULng(sz - 1L)) + 1
        Return System.Math.Min(b, POOL_BUCKETS - 1)
    End Function

    Private Shared Function PoolGet(sz As Long) As IntPtr
        If sz > 0L AndAlso sz <= POOL_MAX_BLOCK Then
            Dim b As Integer = PoolBucket(sz)
            ' §96 (#96): the MANAGED pool (_gmpPool) is initialized ONLY in the fallback path
            ' (InitGmpVirtualAllocFunctions, when GmpNativeAlloc_LoadGmp fails).  In normal NATIVE
            ' mode _gmpPool(b) is Nothing, so this method NRE'd whenever a §79 compute-path pre-alloc
            ' (tmpHigh/mpQ1/mpQ2/mpR0-2/Combine A-D in the §226 small-scale conversion) called it —
            ' crashing small from-scratch runs in Phase 3.  Route to the native pool instead.
            If _gmpPool(b) Is Nothing Then Return GmpNativeAlloc_PoolGet(sz)
            Dim ptr As IntPtr
            If _gmpPool(b).TryPop(ptr) Then
                Interlocked.Decrement(_gmpPoolCount(b))  ' §20: maintain atomic count
                Return ptr
            End If
            ' Pool miss — allocate rounded-up bucket size (>= sz, no Monitor needed)
            Dim allocSz As Long = 1L << b
            Return VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(allocSz)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        End If
        ' sz=0 or oversized — allocate exactly
        Return VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(sz)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
    End Function

    Private Shared Sub PoolReturn(ptr As IntPtr, sz As Long)
        If sz > 0L AndAlso sz <= POOL_MAX_BLOCK Then
            Dim b As Integer = PoolBucket(sz)
            ' §20: Interlocked.Increment is O(1) — replaces ConcurrentStack.Count which is O(n).
            ' Increment first; if we exceeded the cap, decrement back and fall through to VirtualFree.
            If Interlocked.Increment(_gmpPoolCount(b)) <= POOL_CAP Then
                _gmpPool(b).Push(ptr)
                Return
            End If
            Interlocked.Decrement(_gmpPoolCount(b))  ' rolled back — pool is full
        End If
        ' Oversized, zero-size, or pool full — release to OS immediately
        VirtualFree(ptr, UIntPtr.Zero, MEM_RELEASE)
    End Sub

    ''' <summary>
    ''' Returns all pooled limb blocks to the OS.  Call between Phase 2 levels and
    ''' after the full computation so committed memory tracks the live working set.
    ''' Safe while _displayNativePtr is alive — GMP never calls GmpFreeFunc on it.
    ''' </summary>
    Private Shared Sub FlushGmpPool()
        For b As Integer = 0 To POOL_BUCKETS - 1
            Dim ptr As IntPtr
            While _gmpPool(b).TryPop(ptr)
                Interlocked.Decrement(_gmpPoolCount(b))  ' §20: keep counter in sync
                VirtualFree(ptr, UIntPtr.Zero, MEM_RELEASE)
            End While
        Next
        AppendLog($"[GmpPool] flushed{vbCrLf}")
    End Sub

    ' Large allocations (>= 512 KB) use VirtualAlloc so VirtualFree immediately
    ' decommits the pages.  Small allocations delegate to GMP's own default CRT
    ' allocator — the static CRT heap inside libgmp-10.dll — which is the SAME
    ' heap GMP would have used without our override.  Mixing that heap with
    ' Marshal.AllocHGlobal (process default heap) for the same blocks corrupts
    ' GMP's internal state (crash/NullReferenceException in BinarySplitChunk).

    ' §21: Try/Catch removed — prevents JIT inlining and adds per-call overhead.
    ' Corrupt-size and PoolGet-failure paths already handle errors via early return / null.
    ' If an unexpected exception occurs GMP will receive a null pointer and abort, which
    ' is the correct behaviour for a corrupted allocator state (no silent data corruption).
    Private Shared Function GmpAllocFunc(alloc_size As size_t) As void_ptr
        Dim rawSz As ULong = CULng(alloc_size)
        If rawSz > CULng(Long.MaxValue) Then
            ' Size > 9.2 EB — clearly corrupted GMP internal state.
            ' Return null so GMP will abort cleanly; native crash handler logs it.
            AppendLog($"[GmpAllocFunc] CORRUPT SIZE ({rawSz}) — returning null{vbCrLf}", 1)   ' §252 (#95): allocator corruption → crash → level 1
            Return New void_ptr(IntPtr.Zero)
        End If
        Dim sz As Long = CLng(rawSz)
        If sz >= GMP_LARGE_THRESHOLD Then
            Dim ptr As IntPtr = PoolGet(sz)
            If ptr = IntPtr.Zero Then
                AppendLog($"[GmpAlloc] PoolGet({sz:N0} bytes) FAILED — GMP will abort{vbCrLf}", 1)   ' §252 (#95): OOM/abort → level 1
            End If
            Return New void_ptr(ptr)
        End If
        Return _savedGmpAlloc(alloc_size)
    End Function

    ' §21: Try/Catch removed — same rationale as GmpAllocFunc.
    Private Shared Function GmpReallocFunc(old_ptr As void_ptr,
                                            old_size As size_t,
                                            new_size As size_t) As void_ptr
        Dim rawOld As ULong = CULng(old_size)
        Dim rawNew As ULong = CULng(new_size)
        If rawOld > CULng(Long.MaxValue) OrElse rawNew > CULng(Long.MaxValue) Then
            AppendLog($"[GmpReallocFunc] CORRUPT SIZE (old={rawOld}, new={rawNew}) — returning null{vbCrLf}", 1)   ' §252 (#95): allocator corruption → crash → level 1
            Return New void_ptr(IntPtr.Zero)
        End If
        Dim oldSz As Long = CLng(rawOld)
        Dim newSz As Long = CLng(rawNew)

        If oldSz < GMP_LARGE_THRESHOLD AndAlso newSz < GMP_LARGE_THRESHOLD Then
            ' small → small: unchanged CRT behaviour
            Return _savedGmpRealloc(old_ptr, old_size, new_size)
        End If

        Dim oldP As IntPtr = old_ptr.ToIntPtr()
        Dim newP As IntPtr = IntPtr.Zero
        Dim copyBytes As UIntPtr = New UIntPtr(CULng(System.Math.Min(oldSz, newSz)))

        ' Step-level logging threshold: 400 MB — catches Combine-section reallocs
        ' without flooding the log during binary-split smaller operations.
        Const LOG_STEP_THRESHOLD As Long = 400L * 1024L * 1024L

        If oldSz >= GMP_LARGE_THRESHOLD AndAlso newSz >= GMP_LARGE_THRESHOLD Then
            ' large → large: pool-get new block, copy, pool-return old block
            If newSz >= LOG_STEP_THRESHOLD Then
                AppendLog($"[GmpRealloc] L→L enter: new={newSz:N0} old={oldSz:N0}{vbCrLf}")
            End If
            newP = PoolGet(newSz)
            If newP <> IntPtr.Zero Then
                If copyBytes.ToUInt64() > 0UL Then CopyMemory(newP, oldP, copyBytes)
                PoolReturn(oldP, oldSz)
                If newSz >= LOG_STEP_THRESHOLD Then
                    AppendLog($"[GmpRealloc] L→L done → OK{vbCrLf}")
                End If
            Else
                AppendLog($"[GmpRealloc] large→large PoolGet({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}", 1)   ' §252 (#95): OOM/abort → level 1
            End If
        ElseIf newSz >= GMP_LARGE_THRESHOLD Then
            ' small → large: pool-get new block, CRT-free old block
            If newSz >= LOG_STEP_THRESHOLD Then
                AppendLog($"[GmpRealloc] S→L enter: new={newSz:N0} old={oldSz:N0}{vbCrLf}")
            End If
            newP = PoolGet(newSz)
            If newP <> IntPtr.Zero Then
                If copyBytes.ToUInt64() > 0UL Then CopyMemory(newP, oldP, copyBytes)
                _savedGmpFree(old_ptr, old_size)
                If newSz >= LOG_STEP_THRESHOLD Then
                    AppendLog($"[GmpRealloc] S→L done → OK{vbCrLf}")
                End If
            Else
                AppendLog($"[GmpRealloc] small→large PoolGet({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}", 1)   ' §252 (#95): OOM/abort → level 1
            End If
        Else
            ' large → small: CRT-alloc new block, pool-return old block
            Dim newVoid As void_ptr = _savedGmpAlloc(new_size)
            newP = newVoid.ToIntPtr()
            If newP <> IntPtr.Zero Then
                If copyBytes.ToUInt64() > 0UL Then CopyMemory(newP, oldP, copyBytes)
                PoolReturn(oldP, oldSz)
            Else
                AppendLog($"[GmpRealloc] large→small CRT alloc({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}", 1)   ' §252 (#95): OOM/abort → level 1
            End If
        End If

        Return New void_ptr(newP)
    End Function

    ' §21: Try/Catch removed — same rationale as GmpAllocFunc.
    Private Shared Sub GmpFreeFunc(ptr As void_ptr, size As size_t)
        Dim p As IntPtr = ptr.ToIntPtr()
        If p = IntPtr.Zero Then Return
        Dim rawSz As ULong = CULng(size)
        If rawSz > CULng(Long.MaxValue) Then
            ' Corrupted size — can't determine allocator; log and leak.
            AppendLog($"[GmpFreeFunc] CORRUPT SIZE ({rawSz}) ptr={p:X} — leaking{vbCrLf}")
            Return
        End If
        Dim sz As Long = CLng(rawSz)
        If sz >= GMP_LARGE_THRESHOLD Then
            PoolReturn(p, sz)
        Else
            _savedGmpFree(ptr, size)
        End If
    End Sub

    ' Direct P/Invoke to set GMP's native memory function table, bypassing the
    ' Math.Gmp.Native managed wrapper.  Math.Gmp.Native's mp_set_memory_functions
    ' calls __gmp_set_memory_functions and then immediately re-reads the table via
    ' _get_memory_functions(), updating its internal allocate_func_ptr lambda to
    ' capture our managed thunk pointers.  Under .NET 10, Marshal.GetDelegateForFunctionPointer
    ' on a managed thunk pointer returns the ORIGINAL delegate (our allocate_function)
    ' rather than creating a new _allocate_function_x64, so the subsequent cast inside
    ' the lambda fails with InvalidCastException.  Calling __gmp_set_memory_functions
    ' directly avoids that re-read; Math.Gmp.Native's lambda retains the original
    ' CRT malloc IntPtr and continues to work normally.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmp_set_memory_functions",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpSetMemoryFunctionsNative(
        allocFn As IntPtr,
        reallocFn As IntPtr,
        freeFn As IntPtr)
    End Sub

    ' §224 (issue #41, 2026-05-22): P/E core detection + thread-affinity helpers.
    ' Win32 surface: GetLogicalProcessorInformationEx(RelationProcessorCore=0) returns a
    ' variable-length buffer of SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX entries. Each
    ' processor-core entry has an EfficiencyClass byte (0 = E-core, 1+ = P-core on hybrid;
    ' uniform on symmetric CPUs). Re-uses the existing GetLogicalProcessorInformationEx +
    ' SetThreadAffinityMask P/Invokes at Form1.vb:327-345 (originally added for the simpler
    ' process-wide SetPCoreAffinity helper at line 364); this §224 module adds richer
    ' per-thread pinning + mask-splitting helpers for the pipelined Phase 2 work (#43/#42).

    <DllImport("kernel32.dll")>
    Private Shared Function GetCurrentThread() As IntPtr
    End Function

    ' Detected at startup. Defaults are safe-degradation for symmetric CPUs: both lists
    ' contain all logicals, IsHybrid=False, masks cover the full process affinity.
    Private Shared _topoInit As Integer = 0   ' 0=not yet, 1=done
    Private Shared _topoPCoreIds As Integer() = Nothing
    Private Shared _topoECoreIds As Integer() = Nothing
    Private Shared _topoIsHybrid As Boolean = False
    Private Shared _topoPCoreMask As ULong = 0UL
    Private Shared _topoECoreMask As ULong = 0UL
    Private Shared _topoTotalLogicals As Integer = 0

    Private Shared Sub EnsureCpuTopologyInitialized()
        If System.Threading.Interlocked.CompareExchange(_topoInit, 1, 0) <> 0 Then Return
        Try
            Dim _retLen As UInteger = 0UI
            ' First call: NULL buffer → returns required size in retLen, error 122 (insufficient buffer).
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, _retLen)
            If _retLen = 0UI Then
                AppendLog($"[CpuTopology§224] GetLogicalProcessorInformationEx size-probe returned 0 — falling back to symmetric defaults{vbCrLf}")
                ApplyTopologyDefault()
                Return
            End If
            Dim _buf As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(CInt(_retLen))
            Try
                If Not GetLogicalProcessorInformationEx(RelationProcessorCore, _buf, _retLen) Then
                    AppendLog($"[CpuTopology§224] GetLogicalProcessorInformationEx fill failed — falling back to symmetric defaults{vbCrLf}")
                    ApplyTopologyDefault()
                    Return
                End If
                ' Walk entries. Each: DWORD Relationship + DWORD Size + variant body.
                ' Variant for ProcessorCore: BYTE Flags + BYTE EfficiencyClass + 20 BYTE Reserved +
                ' WORD GroupCount + GROUP_AFFINITY[GroupCount].
                ' GROUP_AFFINITY: ULONG_PTR Mask (8 B on x64) + WORD Group + WORD Reserved[3] (6 B) = 16 B.
                Dim _pHi As New Dictionary(Of Byte, List(Of Integer))()  ' efficiencyClass → logical IDs
                Dim _pos As Long = 0L
                Dim _bufEnd As Long = CLng(_retLen)
                While _pos + 8L <= _bufEnd
                    Dim _entry As IntPtr = New IntPtr(_buf.ToInt64() + _pos)
                    Dim _rel As Integer = Runtime.InteropServices.Marshal.ReadInt32(_entry, 0)
                    Dim _sz As Integer = Runtime.InteropServices.Marshal.ReadInt32(_entry, 4)
                    If _sz <= 0 OrElse _pos + CLng(_sz) > _bufEnd Then Exit While
                    If _rel = RelationProcessorCore Then
                        Dim _effClass As Byte = Runtime.InteropServices.Marshal.ReadByte(_entry, 9)  ' offset 8 + 1
                        Dim _grpCount As Short = Runtime.InteropServices.Marshal.ReadInt16(_entry, 30)  ' 8 + 22
                        For _g As Integer = 0 To CInt(_grpCount) - 1
                            Dim _gaOff As Integer = 32 + _g * 16
                            Dim _mask As Long = Runtime.InteropServices.Marshal.ReadInt64(_entry, _gaOff)
                            ' Decode mask bits → logical IDs (we only handle group 0 — single-group machines).
                            Dim _grp As Short = Runtime.InteropServices.Marshal.ReadInt16(_entry, _gaOff + 8)
                            If _grp = 0 Then
                                If Not _pHi.ContainsKey(_effClass) Then _pHi(_effClass) = New List(Of Integer)()
                                Dim _u As ULong = CULng(_mask)
                                For _b As Integer = 0 To 63
                                    If (_u And (1UL << _b)) <> 0UL Then _pHi(_effClass).Add(_b)
                                Next
                            End If
                        Next
                    End If
                    _pos += CLng(_sz)
                End While
                ' Classify: highest EfficiencyClass = P-cores, lowest = E-cores.
                If _pHi.Count = 0 Then
                    AppendLog($"[CpuTopology§224] no ProcessorCore entries found — falling back to symmetric defaults{vbCrLf}")
                    ApplyTopologyDefault()
                    Return
                End If
                Dim _maxClass As Byte = 0
                Dim _minClass As Byte = 255
                For Each _kvp In _pHi
                    If _kvp.Key > _maxClass Then _maxClass = _kvp.Key
                    If _kvp.Key < _minClass Then _minClass = _kvp.Key
                Next
                _topoIsHybrid = (_maxClass <> _minClass)
                If _topoIsHybrid Then
                    _topoPCoreIds = _pHi(_maxClass).ToArray()
                    _topoECoreIds = _pHi(_minClass).ToArray()
                Else
                    ' Symmetric: all logicals are equivalent; populate both lists with the full set.
                    Dim _all As New List(Of Integer)()
                    For Each _kvp In _pHi : _all.AddRange(_kvp.Value) : Next
                    _topoPCoreIds = _all.ToArray()
                    _topoECoreIds = _all.ToArray()
                End If
                _topoPCoreMask = 0UL
                For Each _id In _topoPCoreIds : _topoPCoreMask = _topoPCoreMask Or (1UL << _id) : Next
                _topoECoreMask = 0UL
                For Each _id In _topoECoreIds : _topoECoreMask = _topoECoreMask Or (1UL << _id) : Next
                _topoTotalLogicals = Environment.ProcessorCount
                AppendLog($"[CpuTopology§224] hybrid={_topoIsHybrid} P-cores={_topoPCoreIds.Length} logicals (mask=0x{_topoPCoreMask:X16}) E-cores={_topoECoreIds.Length} logicals (mask=0x{_topoECoreMask:X16}) total={_topoTotalLogicals}{vbCrLf}")
            Finally
                Runtime.InteropServices.Marshal.FreeHGlobal(_buf)
            End Try
        Catch ex As Exception
            AppendLog($"[CpuTopology§224] detection failed: {ex.Message} — falling back to symmetric defaults{vbCrLf}")
            ApplyTopologyDefault()
        End Try
    End Sub

    Private Shared Sub ApplyTopologyDefault()
        _topoTotalLogicals = Environment.ProcessorCount
        Dim _all(_topoTotalLogicals - 1) As Integer
        For _i As Integer = 0 To _topoTotalLogicals - 1 : _all(_i) = _i : Next
        _topoPCoreIds = _all
        _topoECoreIds = _all
        _topoIsHybrid = False
        _topoPCoreMask = If(_topoTotalLogicals >= 64, ULong.MaxValue, (1UL << _topoTotalLogicals) - 1UL)
        _topoECoreMask = _topoPCoreMask
    End Sub

    Public Shared ReadOnly Property CpuTopologyIsHybrid As Boolean
        Get
            EnsureCpuTopologyInitialized() : Return _topoIsHybrid
        End Get
    End Property

    Public Shared ReadOnly Property CpuTopologyPCoreIds As Integer()
        Get
            EnsureCpuTopologyInitialized() : Return _topoPCoreIds
        End Get
    End Property

    Public Shared ReadOnly Property CpuTopologyECoreIds As Integer()
        Get
            EnsureCpuTopologyInitialized() : Return _topoECoreIds
        End Get
    End Property

    Public Shared ReadOnly Property CpuTopologyPCoreMask As ULong
        Get
            EnsureCpuTopologyInitialized() : Return _topoPCoreMask
        End Get
    End Property

    Public Shared ReadOnly Property CpuTopologyECoreMask As ULong
        Get
            EnsureCpuTopologyInitialized() : Return _topoECoreMask
        End Get
    End Property

    ''' <summary>
    ''' Pin the calling thread to the given affinity mask. Returns the prior mask (caller
    ''' must save and restore via PinCurrentThreadTo(prior) if it wants to undo).
    ''' Uses the existing IntPtr-typed SetThreadAffinityMask P/Invoke.
    ''' </summary>
    Public Shared Function PinCurrentThreadTo(mask As ULong) As ULong
        EnsureCpuTopologyInitialized()
        Dim _prior As IntPtr = SetThreadAffinityMask(GetCurrentThread(), New IntPtr(CLng(mask)))
        Return CULng(_prior.ToInt64())
    End Function

    Public Shared Function PinCurrentThreadToPCores() As ULong
        Return PinCurrentThreadTo(CpuTopologyPCoreMask)
    End Function

    ' §247 (#48/#49): pin the calling thread to E-cores AND register it with the affinity
    ' watchdog's exempt set so it is NOT dragged back to P-cores.  Adaptive: on a non-hybrid
    ' host (no E-cores) this is a harmless no-op — the thread keeps the (all-core) process mask.
    Public Shared Function PinCurrentThreadToECores() As ULong
        If Not CpuTopologyIsHybrid OrElse CpuTopologyECoreMask = 0UL Then Return 0UL
        _affinityExempt(GetCurrentThreadId()) = 0
        Return PinCurrentThreadTo(CpuTopologyECoreMask)
    End Function

    ' §247: undo PinCurrentThreadToECores — drop the watchdog exemption and return the thread
    ' to the P-core mask (where the watchdog will keep it).  No-op on a non-hybrid host.
    Public Shared Sub UnpinCurrentThreadFromECores()
        If Not CpuTopologyIsHybrid Then Return
        Dim _dummy As Byte
        _affinityExempt.TryRemove(GetCurrentThreadId(), _dummy)
        If CpuTopologyPCoreMask <> 0UL Then SetThreadAffinityMask(GetCurrentThread(), New IntPtr(CLng(CpuTopologyPCoreMask)))
    End Sub

    ''' <summary>
    ''' Restore the affinity mask previously returned by PinCurrentThreadTo.
    ''' Pass 0 to clear the affinity restriction entirely (effectively pinned to process mask).
    ''' </summary>
    Public Shared Sub RestoreCurrentThreadAffinity(priorMask As ULong)
        SetThreadAffinityMask(GetCurrentThread(), New IntPtr(CLng(priorMask)))
    End Sub

    ''' <summary>
    ''' Build two disjoint halves of the P-core mask, for use by pipelined stages
    ''' that should not share memory bandwidth. On symmetric CPUs both halves are
    ''' subsets of the full mask; on hybrid CPUs they're disjoint subsets of P-cores.
    ''' Returns (firstHalfMask, secondHalfMask).
    ''' </summary>
    Public Shared Function SplitPCoreMaskInHalf() As Tuple(Of ULong, ULong)
        EnsureCpuTopologyInitialized()
        Dim _ids As Integer() = _topoPCoreIds
        Dim _half As Integer = _ids.Length \ 2
        Dim _maskA As ULong = 0UL, _maskB As ULong = 0UL
        For _i As Integer = 0 To _ids.Length - 1
            If _i < _half Then _maskA = _maskA Or (1UL << _ids(_i)) Else _maskB = _maskB Or (1UL << _ids(_i))
        Next
        Return Tuple.Create(_maskA, _maskB)
    End Function

    ' §42: Raw P/Invoke to libgmp-10.dll — bypass mpz_t wrapper entirely for accumulation.
    ' Math.Gmp.Native corrupts mpz_t.Pointer for locally-scoped mpz_t objects during
    ' recursive SafeMpzMul calls, even for objects not passed to inner calls.  Using plain
    ' IntPtr (Marshal.AllocHGlobal) for the accumulator and saved _sv_xxx IntPtrs for
    ' product/shifted avoids the corruption entirely.
    ' §78: Fast-path SafeMpzMul must bypass Math.Gmp.Native's managed wrapper for the
    ' same reason the slow path uses raw IntPtrs (§42): the wrapper corrupts mpz_t.Pointer
    ' fields during native calls.  For the slow path this was fixed by using accum (a raw
    ' IntPtr).  For the fast path (szA+szB ≤ SAFE_LIMB_THRESHOLD) the direct mpz_mul call
    ' must also go through a raw P/Invoke so the wrapper never touches the mpz_t objects.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_mul",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_mul(rop As IntPtr, op1 As IntPtr, op2 As IntPtr)
    End Sub

    ' §219 (issue #79, 2026-05-21): Drain the .NET finalizer queue at known
    ' idle break points in the compute. The Math.Gmp.Native mpz_t class has a
    ' finalizer (no IDisposable surface) — over long single-threaded compute
    ' stretches (sqrt-Newton, SafeMpzDiv), the finalizer queue accumulates
    ' faster than the single finalizer thread can drain, and the finalizer
    ' thread starts competing for CPU with the sole compute thread.
    ' Observed at 1B: GC.RunFinalizers 17.12% exclusive (vs 0.61% at 500M)
    ' — 28× jump for 2× run duration. Force-draining at known idle points
    ' (between Newton iterations, between Phase 2 levels) bounds the backlog.
    ' Cost per call: ~10-50 ms; fires ~50-100 times across a multi-hour run.
    Private Shared Sub DrainFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, blocking:=True)
        GC.WaitForPendingFinalizers()
        ' Second pass: some finalizers schedule more cleanups that need a follow-up GC.
        GC.Collect(2, GCCollectionMode.Forced, blocking:=True)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_add",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_add(rop As IntPtr, op1 As IntPtr, op2 As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_mul_2exp",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_mul_2exp(rop As IntPtr, op1 As IntPtr, op2 As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_neg",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_neg(rop As IntPtr, op As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_sizeinbase",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function GmpRaw_sizeinbase(op As IntPtr, base As Integer) As ULong
    End Function

    ' §25: Additional raw DllImports for SafeMpzMul slow path — eliminates Math.Gmp.Native
    ' delegate dispatch from init/clear/tdiv operations. Struct headers for locally-created
    ' mpz_t objects are allocated via Marshal.AllocHGlobal(16); GmpRaw_init/init2 fills in
    ' the limb buffer via GmpAllocFunc exactly as gmp_lib.mpz_init does internally.
    ' Cleanup: GmpRaw_clear frees the limb buffer via GmpFreeFunc; Marshal.FreeHGlobal
    ' frees the struct header — matching the AllocHGlobal on the way in.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_init",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_init(rop As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_init2",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_init2(rop As IntPtr, n As ULong)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_clear",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_clear(rop As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_tdiv_r_2exp",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_tdiv_r_2exp(rop As IntPtr, op As IntPtr, n As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_tdiv_q_2exp",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_tdiv_q_2exp(rop As IntPtr, op As IntPtr, n As UInteger)
    End Sub

    ' §108: Additional raw DllImports for BinarySplitChunk hot path (~11 thunked calls per term).
    ' Eliminates Math.Gmp.Native delegate dispatch on the term-generation inner loop.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_set_ui",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_set_ui(rop As IntPtr, op As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_set_si",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_set_si(rop As IntPtr, op As Integer)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_mul_ui",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_mul_ui(rop As IntPtr, op1 As IntPtr, op2 As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_add_ui",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_add_ui(rop As IntPtr, op1 As IntPtr, op2 As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_sub_ui",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_sub_ui(rop As IntPtr, op1 As IntPtr, op2 As UInteger)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_pow_ui",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_pow_ui(rop As IntPtr, base As IntPtr, exp As UInteger)
    End Sub

    ' §35: Raw DllImports for cold-path functions in SafeMpzReciprocal/Div/Sqrt.
    ' Eliminates Math.Gmp.Native delegate dispatch from these operations.
    ' Note: mpz_sgn is a GMP macro (reads _mp_size at offset +4) — NOT an exported
    ' function.  Replace gmp_lib.mpz_sgn(x) with Math.Sign(Marshal.ReadInt32(x.Pointer, 4)).
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_tdiv_q",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_tdiv_q(rop As IntPtr, op1 As IntPtr, op2 As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_cmp",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function GmpRaw_cmp(op1 As IntPtr, op2 As IntPtr) As Integer
    End Function

    ' §171: mpn-level single-limb division — no TMP_ALLOC, so safe for any szB.
    ' Divides np[0..nn-1] by d (single 64-bit limb), writes quotient to qp[0..nn-1].
    ' Returns the remainder as a ULong.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpn_divrem_1",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function GmpRaw_mpn_divrem_1(qp As IntPtr, qxn As Integer, np As IntPtr, nn As Integer, d As ULong) As ULong
    End Function

    ' §229 (issue #56): mpn-level left shift by 0 < count < 64 bits, parallelized over limb
    ' chunks in ParallelBigShiftLeftOOP.  Returns the carry shifted out of the top limb.
    ' Source and dest may alias when rp >= sp; per-chunk shifts use disjoint ranges so
    ' aliasing is irrelevant across threads.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpn_lshift",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function GmpRaw_mpn_lshift(rp As IntPtr, sp As IntPtr, n As Integer, count As UInteger) As ULong
    End Function

    ' §251 (issue #70): mpn add with unequal lengths — rp[0..s1n) = s1p[0..s1n) + s2p[0..s2n),
    ' s1n >= s2n, carry propagated through the high limbs of s1.  Returns the final carry out
    ' of limb s1n.  rp may alias s1p.  Used by SafeMpzMul_ChunkedGrid to add each cell product
    ' into the result buffer at a limb offset WITHOUT shifting the whole accumulator.
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpn_add",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Function GmpRaw_mpn_add(rp As IntPtr, s1p As IntPtr, s1n As Integer, s2p As IntPtr, s2n As Integer) As ULong
    End Function

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_swap",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_swap(rop1 As IntPtr, rop2 As IntPtr)
    End Sub

    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_set",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_set(rop As IntPtr, op As IntPtr)
    End Sub

    ' §NR-raw: general subtraction — two mpz_t operands; bypasses managed wrapper pointer corruption
    <DllImport("libgmp-10.dll", EntryPoint:="__gmpz_sub",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpRaw_sub(rop As IntPtr, op1 As IntPtr, op2 As IntPtr)
    End Sub

    ' §30: Native pool DLL exports — replace managed VirtualAlloc pool delegates.
    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_LoadGmp",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function GmpNativeAlloc_LoadGmp(
        logLevel As Integer,
        <Runtime.InteropServices.MarshalAs(Runtime.InteropServices.UnmanagedType.LPStr)>
        optLogPath As String) As Boolean
    End Function

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_Install",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_Install()
    End Sub

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_Flush",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_Flush()
    End Sub

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_FreeRaw",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_FreeRaw(ptr As IntPtr, sz As Long)
    End Sub

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_PoolGet",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function GmpNativeAlloc_PoolGet(sz As Long) As IntPtr
    End Function

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_PoolReturn",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_PoolReturn(ptr As IntPtr, sz As Long)
    End Sub

    ' §241 (issue #69): pool census + phase-boundary trim.
    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_PoolCensus",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_PoolCensus(ByRef outBytes As ULong, ByRef outBlocks As UInteger)
    End Sub

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpNativeAlloc_Trim",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpNativeAlloc_Trim(minBucketBytes As ULong, ByRef outBuffersFreed As UInteger, ByRef outBytesFreed As ULong)
    End Sub

    ' §241 (issue #69): census the native pool, trim buckets >= minBytes, log retention,
    ' freed amount, and working-set delta.  Called at phase boundaries.  The census-before
    ' value is the measurement that tells us whether the pool retains GB (trim worthwhile)
    ' or MB (low-value / tune threshold).  minBytes must be <= POOL_MAX_BLOCK (16 MB) to
    ' free anything — the pool never holds larger blocks.
    Private Shared Sub TrimPoolAtBoundary(ctx As String, minBytes As ULong)
        Try
            Dim wsBefore As Long = Process.GetCurrentProcess().WorkingSet64
            Dim pooledBefore As ULong = 0UL
            Dim blocksBefore As UInteger = 0UI
            GmpNativeAlloc_PoolCensus(pooledBefore, blocksBefore)
            Dim freedBuffers As UInteger = 0UI
            Dim freedBytes As ULong = 0UL
            GmpNativeAlloc_Trim(minBytes, freedBuffers, freedBytes)
            Dim wsAfter As Long = Process.GetCurrentProcess().WorkingSet64
            AppendLog($"[Trim§241 ctx={ctx}] pooled-before={pooledBefore / 1048576.0:N0} MB ({blocksBefore:N0} blks) | freed {freedBuffers:N0} blks {freedBytes / 1073741824.0:N3} GB (minBucket={minBytes / 1048576.0:N0} MB) | WS {wsBefore \ 1048576L:N0} -> {wsAfter \ 1048576L:N0} MB{vbCrLf}")
        Catch _ex As Exception
            AppendLog($"[Trim§241 ctx={ctx}] FAILED: {_ex.Message}{vbCrLf}")
        End Try
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §243 (issue #68): MemoryBudget — live RAM feedback + adaptive DOP floor.
    ' ════════════════════════════════════════════════════════════════════════
    ' Reads available physical RAM AND commit headroom via GlobalMemoryStatusEx
    ' (commit is the metric that predicts the §238 commit-limit OOM), projects a
    ' SafeMpzMul §gen peak, and suggests a DOP that keeps the peak under budget.
    ' Used ONLY as a FLOOR (Min with the existing policy) — it can only REDUCE
    ' DOP under pressure, never raise it, so on a healthy box with ample RAM the
    ' behaviour is identical to before.  Readings are cached (~2 s) so the per-call
    ' cost on the hot SafeMpzMul path is negligible.
    <Runtime.InteropServices.StructLayout(Runtime.InteropServices.LayoutKind.Sequential)>
    Private Structure MEMORYSTATUSEX
        Public dwLength As UInteger
        Public dwMemoryLoad As UInteger
        Public ullTotalPhys As ULong
        Public ullAvailPhys As ULong
        Public ullTotalPageFile As ULong
        Public ullAvailPageFile As ULong
        Public ullTotalVirtual As ULong
        Public ullAvailVirtual As ULong
        Public ullAvailExtendedVirtual As ULong
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GlobalMemoryStatusEx(ByRef lpBuffer As MEMORYSTATUSEX) As Boolean
    End Function

    ' Cached MEMORYSTATUSEX (refreshed every ~2 s via Stopwatch ticks — Date.Now is
    ' unavailable/forbidden; Stopwatch.GetTimestamp is monotonic and cheap).
    Private Shared _memStatusCache As MEMORYSTATUSEX
    Private Shared _memStatusStamp As Long = 0L
    Private Shared ReadOnly _memStatusLock As New Object()
    Private Const MEM_CACHE_TICKS As Long = 2L  ' seconds; multiplied by Stopwatch.Frequency at use

    Private Shared Function MemBudget_Status() As MEMORYSTATUSEX
        Dim _now As Long = System.Diagnostics.Stopwatch.GetTimestamp()
        SyncLock _memStatusLock
            If _memStatusStamp = 0L OrElse (_now - _memStatusStamp) > MEM_CACHE_TICKS * System.Diagnostics.Stopwatch.Frequency Then
                Dim _m As New MEMORYSTATUSEX()
                _m.dwLength = CUInt(Runtime.InteropServices.Marshal.SizeOf(_m))
                If GlobalMemoryStatusEx(_m) Then
                    _memStatusCache = _m
                    _memStatusStamp = _now
                End If
            End If
            Return _memStatusCache
        End SyncLock
    End Function

    Private Shared Function MemBudget_AvailablePhysicalGB() As Double
        Return MemBudget_Status().ullAvailPhys / 1073741824.0
    End Function
    Private Shared Function MemBudget_TotalPhysicalGB() As Double
        Return MemBudget_Status().ullTotalPhys / 1073741824.0
    End Function
    ' Commit headroom = available pagefile (RAM + pagefile not yet committed).  This
    ' is the ceiling that, when hit, fails VirtualAlloc — the §238 OOM signature.
    Private Shared Function MemBudget_AvailableCommitGB() As Double
        Return MemBudget_Status().ullAvailPageFile / 1073741824.0
    End Function

    ' §245 (#85 fix): project the INCREMENTAL new allocation for a §gen SafeMpzMul —
    '   result accumulator (szA+szB limbs) + shifted buffer (~result)
    '   + dop concurrent sub-products (each ~(szA+szB)/3 limbs for the 3×3 split).
    ' Does NOT include current PrivateMemorySize64: that memory is already resident and
    ' already excluded from availPhys, so adding it double-counts.  The §244 5B run floored
    ' DOP to 1 ~6,466× because persist (~30 GB: P/Q/T + GMP pool) made even DOP=1 "not fit"
    ' the available headroom.  The new buffers must fit in FREE space — SuggestSafeMulDop
    ' compares this incremental projection to (min(availPhys,availCommit) − headroom).
    ' Safety: total peak = resident + incremental; since incremental ≤ availPhys − headroom
    ' and resident = total − availPhys, total peak ≤ total − headroom → never overruns RAM.
    Private Shared Function MemBudget_ProjectMulPeakGB(szA As Long, szB As Long, dop As Integer) As Double
        Const GB As Double = 1073741824.0
        Dim resultGB As Double = CDbl(szA + szB) * 8.0 / GB
        Dim shiftedGB As Double = resultGB
        Dim subGB As Double = CDbl(szA + szB) / 3.0 * 8.0 / GB
        Return resultGB + shiftedGB + CDbl(System.Math.Max(1, dop)) * subGB
    End Function

    ' Suggest a §gen DOP (9/6/3/1) whose projected peak fits under (availCommit - headroom).
    ' headroomGB overridable via env PI_MEMBUDGET_HEADROOM_GB (default 5) — lets validation
    ' force a downshift without a constrained VM.
    Private Shared Function MemBudget_SuggestSafeMulDop(szA As Long, szB As Long) As Integer
        Dim headroomGB As Double = 5.0
        Dim _env As String = Environment.GetEnvironmentVariable("PI_MEMBUDGET_HEADROOM_GB")
        Dim _h As Double
        If _env IsNot Nothing AndAlso Double.TryParse(_env, _h) AndAlso _h >= 0.0 Then headroomGB = _h
        ' §244 (#85): budget against MIN(physical, commit) − headroom.  Commit alone (huge
        ' with the pagefile) would let DOP exceed physical RAM → thrash (not OOM but kills the
        ' speedup).  Using physical too keeps the projected peak resident, avoiding paging.
        Dim budgetGB As Double = System.Math.Min(MemBudget_AvailablePhysicalGB(), MemBudget_AvailableCommitGB()) - headroomGB
        For Each d As Integer In New Integer() {9, 6, 3, 1}
            If MemBudget_ProjectMulPeakGB(szA, szB, d) <= budgetGB Then Return d
        Next
        Return 1
    End Function

    ' Trigger a #69 pool trim when commit headroom drops below triggerGB.  Returns True
    ' if a trim ran.  Frees the ≤16 MB pooled granules (can be GB-scale) before a big alloc.
    Private Shared Function MemBudget_MaybeTrimUnderPressure(triggerGB As Double) As Boolean
        If MemBudget_AvailableCommitGB() >= triggerGB Then Return False
        Try
            Dim _freed As UInteger = 0UI, _bytes As ULong = 0UL
            GmpNativeAlloc_Trim(1048576UL, _freed, _bytes)
            If _logLevel >= 2 Then AppendLog($"[MemoryBudget§243] pressure trim (commit {MemBudget_AvailableCommitGB():F1}GB < {triggerGB:F0}GB): freed {_freed:N0} blks {_bytes / 1073741824.0:N2} GB{vbCrLf}")
            Return True
        Catch _ex As Exception
            Return False
        End Try
    End Function

    ' §70 (#70 RAM-cap dispatch): fall back to the chunked grid (SafeMpzMul_ChunkedGrid full mode)
    ' when even §gen at DOP=1 would exceed the budget.  §gen-DOP1 peak ≈ result + shifted +
    ' one sub-product (~2.3× result); chunked-full ≈ result + tiny per-cell temps (~1× result), so
    ' it completes where §gen OOMs (the ~40-70 GB depth-0 peak this issue targets).  On a roomy box
    ' §gen-DOP1 fits → this returns False → no-op, no perf regression (chunked-full is ~1.4× slower).
    ' Budget = min(availPhys, availCommit) − headroom (PI_MEMBUDGET_HEADROOM_GB; large value forces
    ' the fallback for 1B testing).  §68 Phase C consumer.
    Private Shared Function MemBudget_ShouldFallbackToChunkedGrid(szA As Long, szB As Long) As Boolean
        Dim headroomGB As Double = 5.0
        Dim _env As String = Environment.GetEnvironmentVariable("PI_MEMBUDGET_HEADROOM_GB")
        Dim _h As Double
        If _env IsNot Nothing AndAlso Double.TryParse(_env, _h) AndAlso _h >= 0.0 Then headroomGB = _h
        Dim budgetGB As Double = System.Math.Min(MemBudget_AvailablePhysicalGB(), MemBudget_AvailableCommitGB()) - headroomGB
        Return MemBudget_ProjectMulPeakGB(szA, szB, 1) > budgetGB
    End Function

    Private Shared Sub MemBudget_LogSnapshot(context As String)
        If _logLevel < 2 Then Return
        Dim _ws As Double = Process.GetCurrentProcess().WorkingSet64 / 1073741824.0
        Dim _pv As Double = Process.GetCurrentProcess().PrivateMemorySize64 / 1073741824.0
        AppendLog($"[MemoryBudget§243 {context}] availPhys={MemBudget_AvailablePhysicalGB():F1}GB availCommit={MemBudget_AvailableCommitGB():F1}GB WS={_ws:F1}GB priv={_pv:F1}GB{vbCrLf}")
    End Sub

    ' §32B: Batch GMP operations — single managed→native crossing per term/combine.
    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpBatch_ComputeTerm",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpBatch_ComputeTerm(
        a As Long,
        pPtr As IntPtr, qPtr As IntPtr, tPtr As IntPtr,
        c3Ptr As IntPtr)
    End Sub

    <DllImport("GmpNativeAlloc.dll", EntryPoint:="GmpBatch_CombineNodes",
               CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub GmpBatch_CombineNodes(
        resPPtr As IntPtr, resQPtr As IntPtr, resTPtr As IntPtr,
        lPPtr As IntPtr,   lQPtr As IntPtr,   lTPtr As IntPtr,
        rPPtr As IntPtr,   rQPtr As IntPtr,   rTPtr As IntPtr,
        tempAPtr As IntPtr, tempBPtr As IntPtr)
    End Sub

    Private Sub InitGmpVirtualAllocFunctions()
        ' §30: Replaced managed VirtualAlloc pool with native SLIST-based pool.
        ' The native DLL (GmpNativeAlloc.dll) installs its own alloc/realloc/free
        ' callbacks directly into GMP's native function table — zero managed→native
        ' thunk overhead per GMP alloc/free.
        '
        ' Step 1: Force Math.Gmp.Native's lazy static initializer to run NOW so it
        ' captures the default CRT malloc/realloc/free delegates BEFORE we install
        ' our native hooks.  This prevents .NET's GetDelegateForFunctionPointer from
        ' seeing our hook pointers and creating a broken wrapper on first access.
        gmp_lib.mp_get_memory_functions(_savedGmpAlloc, _savedGmpRealloc, _savedGmpFree)
        AppendLog($"[GmpPool] Managed GMP lazy-init triggered (saved CRT alloc delegates){vbCrLf}")

        ' Step 2: Load GMP function pointers into the native DLL and install hooks.
        ' Map VB log levels to native log levels:
        '   VB 0-4 -> native 1 (init messages only): no per-alloc logging; all 24 threads
        '             contend on g_logLock for every GMP op at native level 2+, killing
        '             parallel throughput (observed: ~1 core instead of 24 at VB level 2).
        '   VB 5+  -> native 2 (pool diagnostics): useful for allocator debugging only.
        Dim nativeLogLevel As Integer = If(_logLevel >= 5, 2, 1)
        Dim logPath As String = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.ExecutablePath),
            "gmpnativealloc.log")
        Dim loaded As Boolean = GmpNativeAlloc_LoadGmp(nativeLogLevel, logPath)
        If Not loaded Then
            AppendLog($"[GmpPool] GmpNativeAlloc_LoadGmp FAILED — falling back to managed pool{vbCrLf}")
            ' Fallback: install managed pool delegates (legacy path)
            For b As Integer = 0 To POOL_BUCKETS - 1
                _gmpPool(b) = New ConcurrentStack(Of IntPtr)()
            Next
            _gmpAlloc = New allocate_function(AddressOf GmpAllocFunc)
            _gmpRealloc = New reallocate_function(AddressOf GmpReallocFunc)
            _gmpFree = New free_function(AddressOf GmpFreeFunc)
            GmpSetMemoryFunctionsNative(
                Marshal.GetFunctionPointerForDelegate(_gmpAlloc),
                Marshal.GetFunctionPointerForDelegate(_gmpRealloc),
                Marshal.GetFunctionPointerForDelegate(_gmpFree))
            Return
        End If
        AppendLog($"[GmpPool] GmpNativeAlloc_LoadGmp OK — log: {logPath}{vbCrLf}")
        GmpNativeAlloc_Install()
        AppendLog($"[GmpPool] GmpNativeAlloc_Install OK — native SLIST pool active{vbCrLf}")
    End Sub

    ' ── Native crash handler (Issue #10 / native-code crash capture) ────────
    ' Keeps the delegate alive so the GC never collects it while the process runs.
    <DllImport("kernel32.dll", SetLastError:=False)>
    Private Shared Function SetUnhandledExceptionFilter(
        lpTopLevelExceptionFilter As NativeCrashFilterCallback) As IntPtr
    End Function

    ' Return value: EXCEPTION_EXECUTE_HANDLER = 1 (don't re-call default handler;
    ' Windows will terminate the process after our callback returns 0).
    Private Delegate Function NativeCrashFilterCallback(exceptionInfo As IntPtr) As Integer
    Private _nativeCrashCallback As NativeCrashFilterCallback  ' must be a field — prevents GC collection

    ''' <summary>
    ''' Called by Windows for any unhandled exception that reaches the OS,
    ''' including native SEH exceptions from inside GMP that bypass .NET's
    ''' exception machinery.  The managed heap may be corrupted at this point,
    ''' so we keep the implementation minimal: one synchronous file write, then
    ''' return 0 (EXCEPTION_CONTINUE_SEARCH) so Windows can write a crash dump.
    ''' </summary>
    Private Function HandleNativeCrash(exceptionInfo As IntPtr) As Integer
        Try
            AppendLog(
                $"[NATIVE CRASH] Process terminating — unhandled native exception at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}" &
                vbCrLf &
                "[NATIVE CRASH] Review the last log entries above to identify the failing GMP call." &
                vbCrLf, 1)   ' §252 (#95): native crash = level 1 (error)
        Catch
        End Try
        Return 0   ' EXCEPTION_CONTINUE_SEARCH — let Windows handle it (WER, minidump, etc.)
    End Function

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' ── Parse command-line arguments ─────────────────────────────────────
        ' Supported flags (see README "Command-line options" for the full reference):
        '   --digits N                  Set the digit count (no commas required)
        '   --autostart                 Suppress all dialogs and auto-begin computation (headless)
        '   --autoverify                After computation, auto-run verify + exit
        '   --verify-at "D:P"           Assert digit string D occurs at position P (repeatable)
        '   --verify-contains "D"       Assert digit string D occurs anywhere (repeatable)
        '   --threshold N               Override the RAM/disk threshold (nodes)
        '   --log-level N               Set runtime logging level 0–5 (default 2)
        '   --output-dir D              Override output directory for digits, log, and node cache
        '   --checkpoint-from-level N   Serialize nodes at level >= N to disk (for resume)
        '   --resume-from-level N       Skip Phase 1 + levels 1..N-1; load checkpoint files for level N
        '   --auto-checkpoint           Write RAM snapshot at end of each level; auto-resume on next run
        '   --test-mulhigh | --test-chunkedgrid | --test-eta | --test-advisor |
        '   --test-dopscan | --test-gridscan | --test-cellsweep | --test-recipconv
        '                               Run the named self-test / benchmark harness, then exit
        Dim args() As String = Environment.GetCommandLineArgs()
        ' Log all received args so we can diagnose unexpected headless activation.
        ' args(0) is always the exe path; user args start at index 1.
        Try
            Dim logDir As String = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PI-BillionDigits")
            System.IO.Directory.CreateDirectory(logDir)
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(logDir, "startup_args.txt"),
                $"Started: {DateTime.Now}" & vbCrLf &
                $"Args ({args.Length}): " & String.Join(" | ", args) & vbCrLf)
        Catch
        End Try
        Dim i As Integer = 1
        Do While i < args.Length
            Select Case args(i).ToLower()
                Case "--digits"
                    If i + 1 < args.Length Then
                        Dim d As Long
                        If Long.TryParse(args(i + 1).Replace(",", ""), d) Then
                            TxtDigitsofPI.Text = d.ToString("N0")
                        End If
                        i += 1
                    End If
                Case "--autostart"
                    _headless = True
                    _suppressDialogs = True   ' §119: headless ⇒ the global handler must not block either
                Case "--autoverify"
                    _autoVerify = True
                Case "--verify-at"
                    ' Format: DIGITS:POSITION  e.g. "999999:762"
                    If i + 1 < args.Length Then
                        Dim parts() As String = args(i + 1).Split(":"c)
                        Dim pos As Long
                        If parts.Length = 2 AndAlso Long.TryParse(parts(1), pos) AndAlso parts(0).Length > 0 Then
                            _verifyAt.Add(Tuple.Create(parts(0), pos))
                        End If
                        i += 1
                    End If
                Case "--verify-contains"
                    ' Format: DIGITS  e.g. "27182818284"
                    If i + 1 < args.Length Then
                        If args(i + 1).Length > 0 Then
                            _verifyContains.Add(args(i + 1))
                        End If
                        i += 1
                    End If
                Case "--threshold"
                    If i + 1 < args.Length Then
                        Dim t As Integer
                        If Integer.TryParse(args(i + 1), t) AndAlso t >= 1 Then
                            _diskThreshold = t
                        End If
                        i += 1
                    End If
                Case "--checkpoint-from-level"
                    If i + 1 < args.Length Then
                        Dim cfl As Integer
                        If Integer.TryParse(args(i + 1), cfl) AndAlso cfl >= 1 Then
                            _checkpointFromLevel = cfl
                        End If
                        i += 1
                    End If
                Case "--resume-from-level"
                    If i + 1 < args.Length Then
                        Dim rfl As Integer
                        If Integer.TryParse(args(i + 1), rfl) AndAlso rfl >= 1 Then
                            _resumeFromLevel = rfl
                        End If
                        i += 1
                    End If
                Case "--auto-checkpoint"
                    _autoCheckpoint = True
                Case "--require-free-ram"
                    Environment.SetEnvironmentVariable("PI_REQUIRE_FREE_RAM", "1")   ' §120 (#120): CLI form
                Case "--help", "-h", "/?", "/help"
                    PrintUsageAndExit()   ' §103 (#103): print all flags + exit
                Case "--test-mulhigh"
                    _testMulHigh = True   ' §250 (#94): run SafeMpzMulHigh self-test after GMP init, then exit
                Case "--test-chunkedgrid"
                    _testChunkedGrid = True   ' §251 (#70): run SafeMpzMul_ChunkedGrid self-test, then exit
                Case "--test-eta"
                    _testEta = True           ' §259 (#62): run ETA-estimator self-test, then exit
                Case "--test-advisor"
                    _testAdvisor = True       ' §260 (#63): run performance-advisor self-test, then exit
                Case "--test-dopscan"
                    _testDopScan = True       ' §263 (#88): run DOP/bandwidth-saturation sweep, then exit
                Case "--test-gridscan"
                    _testGridScan = True      ' §265 (#88): run split-factor (k×k) comparison, then exit
                Case "--test-cellsweep"
                    _testCellSweep = True     ' §266 (#88): run cell-size sweep at 5B sizes, then exit
                Case "--test-recipconv"
                    _testRecipConv = True     ' §272 (#88): probe reciprocal-Newton convergence, then exit
                Case "--log-level"
                    If i + 1 < args.Length Then
                        Dim lvl As Integer
                        If Integer.TryParse(args(i + 1), lvl) AndAlso lvl >= 0 AndAlso lvl <= 5 Then
                            System.Threading.Volatile.Write(_logLevel, lvl)  ' §27
                        End If
                        i += 1
                    End If
                Case "--output-dir"
                    If i + 1 < args.Length AndAlso args(i + 1).Length > 0 Then
                        _outputDir = args(i + 1)
                        i += 1
                    End If
            End Select
            i += 1
        Loop

        LblStatus.Text = "Ready"
        ' §253 (#52): wire the Shared status hook so SafeMpzReciprocal can show NR-iter progress.
        ' BeginInvoke marshals onto the UI thread; try-wrapped so a closing form can't throw.
        _statusHook = Sub(s As String)
                          Try
                              If Not Me.IsDisposed Then Me.BeginInvoke(Sub() LblStatus.Text = s)
                          Catch
                          End Try
                      End Sub
        ' §259 (#62): wire the per-iter reciprocal ETA refinement onto this instance.
        _etaReciprocalHook = Sub(iterDone As Integer, minIters As Integer)
                                 Try : Eta_OnReciprocalProgress(iterDone, minIters) : Catch : End Try
                             End Sub
        TxtDigitsofPI.Text = If(TxtDigitsofPI.Text <> "", TxtDigitsofPI.Text, "1,000,000")
        ChkboxDisplay.Checked = Not _headless
        Label3.Visible = ChkboxDisplay.Checked
        LblDigitsDisplayed.Visible = ChkboxDisplay.Checked

        ' ── Auto-detect RAM threshold ─────────────────────────────────────────
        ' If --threshold was not supplied on the CLI, estimate a safe default
        ' based on available system RAM so the app works on any machine.
        If _diskThreshold = 200_000 Then   ' only override if not set by CLI
            Try
                Dim availGB As Double = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 ^ 3)
                If availGB >= 16 Then
                    _diskThreshold = 200_000   ' full RAM — all 137,739 chunks in memory
                ElseIf availGB >= 8 Then
                    _diskThreshold = 100_000   ' Phase 2 in RAM, Phase 1 streams to L0.bin
                Else
                    _diskThreshold = 1         ' full disk — safe on any machine
                End If
            Catch
                _diskThreshold = 1   ' safe fallback if GC info unavailable
            End Try
        End If
        NudRamThreshold.Value = System.Math.Max(NudRamThreshold.Minimum,
                                System.Math.Min(NudRamThreshold.Maximum, _diskThreshold))
        NudRamThreshold.Enabled = Not _headless
        NudLogLevel.Value = System.Math.Max(NudLogLevel.Minimum,
                            System.Math.Min(NudLogLevel.Maximum, _logLevel))
        NudLogLevel.Enabled = Not _headless
        RtbPiDigits.MaxLength = 0
        RtbPiDigits.ReadOnly = False
        RtbPiDigits.Font = New Font("Consolas", 10)
        RtbPiDigits.BackColor = Color.Black
        RtbPiDigits.ForeColor = Color.Lime
        RtbPiDigits.WordWrap = True
        RtbPiDigits.ScrollBars = RichTextBoxScrollBars.Vertical
        displayTimer.Interval = 100
        displayTimer.Enabled = False
        RtbPiDigits.Dock = DockStyle.Fill
        If _headless Then ChkboxWriteToFile.Checked = True
        LstBoxPhases.Items.Clear()

        ' ── Ctrl+C / Ctrl+Break from the console ─────────────────────────────
        ' When launched via "dotnet run", the terminal and the WinForms process
        ' share a console.  Pressing Ctrl+C sends a CTRL_C_EVENT to every
        ' process in the console group, which kills the WinForms app immediately
        ' without going through FormClosing.  Suppress that so the normal
        ' FormClosing confirmation dialog can run instead.
        If Not _headless Then
            Try
                AddHandler Console.CancelKeyPress,
                    Sub(s As Object, ce As ConsoleCancelEventArgs)
                        ce.Cancel = True   ' suppress default kill
                        Me.BeginInvoke(Sub() Me.Close())   ' route through FormClosing
                    End Sub
            Catch
            End Try
        End If

        ' ── Subscribe to AppDomain.UnhandledException ─────────────────────────
        ' This fires for any managed exception that is not caught anywhere,
        ' including AccessViolationException marshaled back from a P/Invoke call
        ' into GMP.  It complements ApplicationEvents.vb which handles the VB
        ' application-level equivalent.
        AddHandler AppDomain.CurrentDomain.UnhandledException,
            AddressOf OnAppDomainUnhandledException

        ' ── Register native crash filter ──────────────────────────────────────
        ' SetUnhandledExceptionFilter installs a Win32-level last-resort handler
        ' that runs even when the CLR cannot marshal the exception to managed
        ' code (e.g., GMP abort(), stack overflow deep in native code).
        ' Note: in .NET 5+ the CLR may override this for some exception types;
        ' the handler is therefore "best-effort" for truly native crashes.
        _nativeCrashCallback = New NativeCrashFilterCallback(AddressOf HandleNativeCrash)
        SetUnhandledExceptionFilter(_nativeCrashCallback)

        ' Opt the process out of Windows power throttling (EcoQoS / Efficiency Mode).
        ' On hybrid CPUs this prevents the scheduler from routing background threads
        ' to E-cores and halving their CPU quota.
        DisablePowerThrottling()

        ' Affinity on hybrid CPUs (Intel 12th gen+, AMD Zen 4c).  E-cores run GMP
        ' arithmetic ~30-50% slower, so the compute threads belong on P-cores.  §247
        ' keeps the PROCESS mask on all cores (P | E) and the watchdog hard-pins the
        ' compute threads to P, leaving E-cores free for the §248/§249 I/O threads.
        SetPCoreAffinity()
        StartAffinityWatchdog()   ' §106: keep compute threads on P-cores throughout the run

        ' §119 (#119): surface any prior crash records preserved in the Windows Event Log (runs whose
        ' pi_phase_log.txt write had failed) — written into this run's log at compute start, plus a
        ' dialog for an attended interactive run (never a blocking modal headless / Auto-OK).
        ScanEventLogForPriorCrashes()

        ' Install VirtualAlloc/VirtualFree custom GMP allocator so large limb
        ' buffers are immediately decommitted on free, preventing commit-charge
        ' accumulation that caused abort() in multi-pass multiply.
        InitGmpVirtualAllocFunctions()

        ' §250 (#94): standalone SafeMpzMulHigh bit-correctness self-test — runs now that the
        ' GMP allocator is installed, writes results to %TEMP%\mulhigh_test.txt, then exits.
        ' §264 (issue #97): run the self-test harnesses on a BACKGROUND thread (like the compute
        ' path), not inline on the UI thread.  Inline they blocked Form1_Load ⇒ the message loop
        ' never pumped WM_PAINT ⇒ the window never painted.  On a worker thread Form1_Load returns,
        ' the loop runs, the window paints, and the harness pushes live status via _statusHook
        ' (already wired above).  Each harness still Environment.Exit(0/1) — now from the worker.
        If _testMulHigh OrElse _testChunkedGrid OrElse _testEta OrElse _testAdvisor OrElse _testDopScan OrElse _testGridScan OrElse _testCellSweep OrElse _testRecipConv Then
            Dim _testThread As New System.Threading.Thread(
                Sub()
                    Dim ok As Boolean = True, tag As String = "Test"
                    Try
                        If _testMulHigh Then
                            tag = "TestMulHigh" : If _statusHook IsNot Nothing Then _statusHook("Running --test-mulhigh…")
                            ok = TestMulHigh()
                        ElseIf _testChunkedGrid Then
                            tag = "TestChunkedGrid" : If _statusHook IsNot Nothing Then _statusHook("Running --test-chunkedgrid…")
                            ok = TestChunkedGrid()
                        ElseIf _testEta Then
                            tag = "TestEta" : If _statusHook IsNot Nothing Then _statusHook("Running --test-eta…")
                            ok = TestEta()                              ' §259 (#62)
                        ElseIf _testAdvisor Then
                            tag = "TestAdvisor" : If _statusHook IsNot Nothing Then _statusHook("Running --test-advisor…")
                            ok = TestAdvisor()                          ' §260 (#63)
                        ElseIf _testDopScan Then
                            tag = "TestDopScan" : If _statusHook IsNot Nothing Then _statusHook("Running --test-dopscan…")
                            ok = TestDopScan()                          ' §263 (#88)
                        ElseIf _testGridScan Then
                            tag = "TestGridScan" : If _statusHook IsNot Nothing Then _statusHook("Running --test-gridscan…")
                            ok = TestGridScan()                         ' §265 (#88)
                        ElseIf _testCellSweep Then
                            tag = "TestCellSweep" : If _statusHook IsNot Nothing Then _statusHook("Running --test-cellsweep…")
                            ok = TestCellSweep()                        ' §266 (#88)
                        ElseIf _testRecipConv Then
                            tag = "TestRecipConv" : If _statusHook IsNot Nothing Then _statusHook("Running --test-recipconv…")
                            ok = TestRecipConv()                        ' §272 (#88)
                        End If
                        AppendLog($"[{tag}] OVERALL: {If(ok, "DONE/PASS", "FAIL")}{vbCrLf}", 1)
                        If _statusHook IsNot Nothing Then _statusHook($"{tag}: {If(ok, "DONE", "FAIL")} — results in %TEMP%")
                    Catch ex As Exception
                        ok = False
                        Try : WriteExceptionToLog(tag, ex) : Catch : End Try
                    End Try
                    System.Threading.Thread.Sleep(250)   ' let the final status BeginInvoke paint before exit
                    Environment.Exit(If(ok, 0, 1))
                End Sub, 256 * 1024 * 1024)
            _testThread.IsBackground = True
            _testThread.Priority = Threading.ThreadPriority.AboveNormal
            _testThread.Start()
            Return   ' let Form1_Load return → message loop runs → window paints + shows status
        End If
        If Environment.GetEnvironmentVariable("PI_TEST_DOPGATE") = "1" Then
            ' §251 (#70): print the would-be §gen DOP for the 1B & 5B reciprocal mul sizes, so we can
            ' confirm the DOP gate declines chunked at 1B (high DOP) and may engage at 5B (low DOP).
            Dim _p As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dopgate_test.txt")
            Dim _szR1 As Long = 52_000_000L, _szR5 As Long = 87_500_000L
            Dim _txt As String =
                $"availPhys={MemBudget_AvailablePhysicalGB():F1}GB availCommit={MemBudget_AvailableCommitGB():F1}GB{vbCrLf}" &
                $"1B  rSq(52M×52M)  DOP={MemBudget_SuggestSafeMulDop(_szR1, _szR1)}  p(52M×104M) DOP={MemBudget_SuggestSafeMulDop(_szR1, _szR1 * 2L)}{vbCrLf}" &
                $"5B  rSq(87M×87M)  DOP={MemBudget_SuggestSafeMulDop(_szR5, _szR5)}  p(68M×175M) DOP={MemBudget_SuggestSafeMulDop(68_000_000L, 175_000_000L)}{vbCrLf}"
            Try : System.IO.File.WriteAllText(_p, _txt) : Catch : End Try
            AppendLog("[DopGate] " & _txt)
            Environment.Exit(0)
        End If

        ' Initialize constant for Chudnovsky algorithm
        gmpC3Const = New mpz_t()
        gmp_lib.mpz_init(gmpC3Const)
        gmp_lib.mpz_set_str(gmpC3Const, New char_ptr("10939058860032000"), 10)

        ' Ensure output directories exist
        Try
            Dim outputDir As String = System.IO.Path.GetDirectoryName(outputFile)
            If Not String.IsNullOrEmpty(outputDir) AndAlso Not System.IO.Directory.Exists(outputDir) Then
                System.IO.Directory.CreateDirectory(outputDir)
            End If

            ' Create disk cache directory
            If Not System.IO.Directory.Exists(DISK_CACHE_DIR) Then
                System.IO.Directory.CreateDirectory(DISK_CACHE_DIR)
            End If
        Catch ex As Exception
            If Not _headless Then
                MessageBox.Show("Warning: Could not create output directory: " & ex.Message)
            Else
                WriteToLog("[DIALOG] Warning: Could not create output directory: " & ex.Message)
            End If
        End Try

        ' Verify which libgmp DLL is loaded
        Dim gmpDllPath As String = "Unknown"
        Try
            For Each pm As ProcessModule In Process.GetCurrentProcess().Modules
                If pm.ModuleName.ToLower().Contains("libgmp") Then
                    gmpDllPath = pm.FileName
                    Exit For
                End If
            Next
        Catch
        End Try

        Dim processInfoMsg As String =
            "64-bit process: " & Environment.Is64BitProcess.ToString() & vbCrLf &
            "IntPtr.Size: " & IntPtr.Size.ToString() & " (must be 8)" & vbCrLf &
            "Available RAM: " & (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes \ 1048576).ToString() & "MB" & vbCrLf &
            "GMP DLL: " & gmpDllPath & vbCrLf &
            "GMP Memory: System allocator (default)"
        If Not _headless Then
            MessageBox.Show(processInfoMsg, "Process Info")
        Else
            WriteToLog("[DIALOG] Process Info: " & processInfoMsg.Replace(vbCrLf, " | "))
        End If

        ' ── Tooltips ─────────────────────────────────────────────────────────
        TipMain.AutoPopDelay = 10000   ' 10 s — long tooltips need time to read
        TipMain.InitialDelay = 400
        TipMain.ReshowDelay = 200
        TipMain.SetToolTip(BtnCompute,
            "Start computing Pi to the number of digits shown.")
        TipMain.SetToolTip(BtnPause,
            "Cancel the current computation. All progress will be lost.")
        TipMain.SetToolTip(TxtDigitsofPI,
            "Number of Pi digits to compute. For a full run use 1,000,000,000.")
        TipMain.SetToolTip(Label2,
            "Number of Pi digits to compute. For a full run use 1,000,000,000.")
        TipMain.SetToolTip(ChkboxDisplay,
            "Stream computed digits to the display panel below. " &
            "Uncheck for faster headless runs — digits are still written to file if 'Write to File' is checked.")
        TipMain.SetToolTip(ChkboxWriteToFile,
            "Write the computed digits to a text file in the output directory when computation completes.")
        TipMain.SetToolTip(NudRamThreshold,
            "Controls when computation levels are kept in RAM vs written to disk. " &
            "If the node count at a level is ≤ this value, that level stays in RAM (faster). " &
            "If it exceeds this value, nodes are written to the NVMe cache (uses less RAM). " &
            "Auto-detected from available system RAM at startup: ≥16 GB → 200,000 (all RAM); ≥8 GB → 100,000; <8 GB → 1 (full disk). " &
            "Lower this if you get out-of-memory errors.")
        TipMain.SetToolTip(LblRamThreshold,
            "Controls when computation levels are kept in RAM vs written to disk. " &
            "If the node count at a level is ≤ this value, that level stays in RAM (faster). " &
            "If it exceeds this value, nodes are written to the NVMe cache (uses less RAM). " &
            "Auto-detected from available system RAM at startup: ≥16 GB → 200,000 (all RAM); ≥8 GB → 100,000; <8 GB → 1 (full disk). " &
            "Lower this if you get out-of-memory errors.")
        TipMain.SetToolTip(NudLogLevel,
            "Logging detail level (0–5). " &
            "0=None (errors only)  1=Performance (phase timings, default)  " &
            "2=Stages (file I/O, step detail)  3=Last stage (final combine trace)  " &
            "4=Full trace (SafeMpzMul, BinarySplitChunk)  5=Allocator (pool/affinity).")
        TipMain.SetToolTip(LblLogLevel,
            "Logging detail level (0–5). " &
            "0=None (errors only)  1=Performance (phase timings, default)  " &
            "2=Stages (file I/O, step detail)  3=Last stage (final combine trace)  " &
            "4=Full trace (SafeMpzMul, BinarySplitChunk)  5=Allocator (pool/affinity).")
        TipMain.SetToolTip(ChkAutoVerify,
            "When checked, verification runs automatically after computation completes. " &
            "Results appear in the status bar — no dialog boxes.")
        TipMain.SetToolTip(BtnTest,
            "Run verification now against the full computed Pi buffer. " &
            "Checks: 999999 at position 762, 777777777 at position 24,658,601, and nine-9s (999999999) at position 564,665,206. " &
            "Results appear in the status bar.")
        TipMain.SetToolTip(LblStatus,
            "Current computation status and phase timing.")
        TipMain.SetToolTip(Label1,
            "Current computation status and phase timing.")
        TipMain.SetToolTip(LblRunningTime,
            "Elapsed wall-clock time since computation started.")
        TipMain.SetToolTip(Label4,
            "Elapsed wall-clock time since computation started.")
        TipMain.SetToolTip(LblDigitsDisplayed,
            "Number of digits streamed to the display panel so far.")
        TipMain.SetToolTip(Label3,
            "Number of digits streamed to the display panel so far.")
        TipMain.SetToolTip(LstBoxPhases,
            "Phase-by-phase timing log for the current run.")
        TipMain.SetToolTip(RtbPiDigits,
            "Computed Pi digits streamed here in real time (when Display is checked).")
    End Sub

    Private Sub Form1_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        ' When --autostart is supplied, kick off computation as soon as the
        ' form is fully visible (so all Load-time initialisation is complete).
        If _headless Then
            BtnCompute_Click(Me, EventArgs.Empty)
        End If
    End Sub

    Private Sub ChkboxDisplay_CheckedChanged(sender As Object, e As EventArgs) Handles ChkboxDisplay.CheckedChanged
        Label3.Visible = ChkboxDisplay.Checked
        LblDigitsDisplayed.Visible = ChkboxDisplay.Checked
    End Sub

    ' ── AppDomain-level unhandled exception handler ──────────────────────────
    Private Sub OnAppDomainUnhandledException(sender As Object, e As UnhandledExceptionEventArgs)
        Try
            Dim ex As Exception = TryCast(e.ExceptionObject, Exception)
            If ex IsNot Nothing Then
                WriteExceptionToLog("AppDomain.UnhandledException", ex)
            Else
                WriteToLog($"[APPDOMAIN CRASH] Non-Exception object: {e.ExceptionObject?.GetType()?.FullName}")
            End If
            WriteToLog($"[APPDOMAIN CRASH] IsTerminating={e.IsTerminating}")
        Catch
        End Try
    End Sub

    ' ── Form closing — confirmation dialog ───────────────────────────────────
    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        Try
            WriteToLog($"[FormClosing] Reason={e.CloseReason}")
        Catch
        End Try
        ' §232: drain pending async backup tail before exit so SnapshotStore
        ' reflects the canonical final state (rather than ending mid-copy).
        ' 5-minute timeout caps the wait in pathological cases.
        WaitForPendingBackups(timeoutMs:=300000)
        StopAffinityWatchdog()   ' §106

        ' Headless runs exit unattended; autoverify path uses ApplicationExitCall.
        If _headless OrElse e.CloseReason = CloseReason.ApplicationExitCall Then Return

        Dim msg As String
        If BtnCompute.Enabled = False Then
            ' Computation is in progress
            msg = "A computation is currently running. Closing now will lose all progress." &
                  vbCrLf & vbCrLf & "Are you sure you want to close?"
        Else
            msg = "Are you sure you want to close the application?"
        End If

        Dim result As DialogResult = MessageBox.Show(
            msg, "Confirm Close",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2)
        If result <> DialogResult.Yes Then
            e.Cancel = True
        End If
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Logging helpers
    ' ════════════════════════════════════════════════════════════════════════

    Private Shared ReadOnly Property LOG_FILE As String
        Get
            Return System.IO.Path.Combine(_outputDir, "pi_phase_log.txt")
        End Get
    End Property

    ''' <summary>
    ''' Low-level log writer. Thread-safe, no UI interaction.
    ''' Includes timestamp (ms precision), thread ID, elapsed time, and RAM.
    ''' File.AppendAllText opens, writes, and closes synchronously, so the
    ''' entry is guaranteed on disk before the next GMP call — which means the
    ''' last entry in the log identifies the operation that crashed the process.
    ''' </summary>
    ' §252 (#95): timestamped log line, gated via AppendLog(level).  Default level 2 (milestone);
    ' the procMem/elapsed read is skipped when the level is above _logLevel so suppressed lines cost nothing.
    Private Sub WriteToLog(message As String, Optional level As Integer = 2)
        If level > System.Threading.Volatile.Read(_logLevel) Then Return
        Try
            Dim elapsed As TimeSpan = stopWatch.Elapsed
            Dim threadId As Integer = Thread.CurrentThread.ManagedThreadId
            Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | T{threadId} | {elapsed:hh\:mm\:ss\.fff} | RAM:{procMem:N0}MB | {message}" & vbCrLf, level)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Writes an exception with full stack trace and inner exception chain to
    ''' the log file.  Walks the entire InnerException chain so nested causes
    ''' from native interop are not lost.
    ''' </summary>
    Private Sub WriteExceptionToLog(context As String, ex As Exception)
        Try
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine($"*** EXCEPTION in {context} ***")
            Dim current As Exception = ex
            Dim depth As Integer = 0
            While current IsNot Nothing
                Dim prefix As String = If(depth = 0, "Exception", $"InnerException[{depth}]")
                sb.AppendLine($"  {prefix}: {current.GetType().FullName}")
                sb.AppendLine($"  Message: {current.Message}")
                sb.AppendLine($"  StackTrace:")
                sb.AppendLine(current.StackTrace)
                current = current.InnerException
                depth += 1
            End While
            WriteToLog(sb.ToString(), 1)   ' §252 (#95): exceptions/crashes = level 1 (errors)
        Catch
        End Try
    End Sub

    Private Sub LogPhase(phaseName As String)
        Telemetry_OnPhase(phaseName)   ' §258 (#62/#63): record canonical stage timing + refresh ETA
        Dim elapsed As TimeSpan = stopWatch.Elapsed
        Dim phaseTime As TimeSpan = phaseStopWatch.Elapsed
        phaseStopWatch.Restart()
        Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
        Dim virtMem As Long = Process.GetCurrentProcess().VirtualMemorySize64 \ 1048576
        Dim entry As String = $"{elapsed:hh\:mm\:ss\.ff} | +{phaseTime:mm\:ss\.ff} | RAM:{procMem:N0}MB | VIRT:{virtMem:N0}MB | {phaseName}"
        WriteToLog($"[PHASE] {phaseName}")
        Me.BeginInvoke(Sub()
                           LstBoxPhases.Items.Add(entry)
                           LstBoxPhases.SelectedIndex = LstBoxPhases.Items.Count - 1
                           LblStatus.Text = phaseName
                       End Sub)
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  UI event handlers
    ' ════════════════════════════════════════════════════════════════════════

    <DllImport("kernel32.dll")>
    Private Shared Function AttachConsole(dwProcessId As Integer) As Boolean
    End Function
    Private Const ATTACH_PARENT_PROCESS As Integer = -1

    ' §103 (#103): print the full CLI flag reference and exit.  The app is a WinForms GUI with no
    ' console of its own, so attach to the parent terminal (if launched from one) to print there, and
    ' always also drop the text in %TEMP%\pi_usage.txt as a fallback.
    Private Shared Sub PrintUsageAndExit()
        Dim u As String =
            "PI-BillionDigits — Chudnovsky π computation (GMP).  CLI flags:" & vbCrLf &
            "  --digits N                 Digit count (commas optional). Default 1,000,000." & vbCrLf &
            "  --autostart                Suppress dialogs and auto-begin (headless)." & vbCrLf &
            "  --autoverify               After compute, auto-run verify, then exit." & vbCrLf &
            "  --verify-at ""D:P""          Assert digit string D occurs at position P (repeatable)." & vbCrLf &
            "  --verify-contains ""D""      Assert digit string D occurs anywhere (repeatable)." & vbCrLf &
            "  --threshold N              RAM/disk node threshold (high ⇒ stay in RAM)." & vbCrLf &
            "  --log-level N              Logging 0–5 (default 2). 0 Silent · 1 Errors+result ·" & vbCrLf &
            "                             2 Phase milestones · 3 Sub-phase · 4 Detailed · 5 Allocator." & vbCrLf &
            "  --output-dir D             Output dir for digits, log, and node cache." & vbCrLf &
            "  --auto-checkpoint          Snapshot each level; auto-resume next run." & vbCrLf &
            "  --checkpoint-from-level N  Serialize nodes at level ≥ N to disk (for resume)." & vbCrLf &
            "  --resume-from-level N      Skip Phase 1 + levels 1..N-1; load level-N checkpoint." & vbCrLf &
            "  --require-free-ram         Headless: abort (exit 3) on memory contention (#120)." & vbCrLf &
            "  --help | -h | /?           Show this help and exit." & vbCrLf &
            "  Self-tests (run, then exit): --test-mulhigh --test-chunkedgrid --test-eta" & vbCrLf &
            "    --test-advisor --test-dopscan --test-gridscan --test-cellsweep --test-recipconv" & vbCrLf &
            "  See README.md (""Command-line options"") and docs/ for full detail."
        Try
            If AttachConsole(ATTACH_PARENT_PROCESS) Then
                Console.Out.WriteLine()
                Console.Out.WriteLine(u)
                Console.Out.Flush()
            End If
        Catch
        End Try
        Try
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pi_usage.txt"), u)
        Catch
        End Try
        Environment.Exit(0)
    End Sub

    ' §120 (#120): memory-contention pre-flight.  Predicts whether the §70/§243 governor will throttle
    ' (availPhys < projected peak + headroom) BEFORE the run starts, so a starved many-hour run is
    ' caught up front rather than discovered hours in.  Interactive: a Proceed/Cancel dialog.  Headless:
    ' a level-1 [WARN] log line (+ opt-in hard abort via PI_REQUIRE_FREE_RAM=1, exit code 3).  Uses the
    ' same availPhys/headroom math as the governor so the warning predicts its real decision.  Returns
    ' True to proceed.  Never blocks a run on its own error.  See docs/MEMORY_STARVATION_PLAYBOOK.md.
    Private Function MemPreflight_ShouldProceed(digits As Long) As Boolean
        Try
            Dim availPhysGB As Double = MemBudget_AvailablePhysicalGB()
            Dim availCommitGB As Double = MemBudget_AvailableCommitGB()
            ' Telemetry-anchored peak estimate: ~5 GB @ 1B, ~45 GB @ 5B (observed top-combine/divide peaks).
            Dim projPeakGB As Double = 5.0 + System.Math.Max(0.0, digits / 1.0E9 - 1.0) * 10.0
            Dim headroomGB As Double = 5.0
            Dim _env As String = Environment.GetEnvironmentVariable("PI_MEMBUDGET_HEADROOM_GB")
            Dim _hp As Double
            If _env IsNot Nothing AndAlso Double.TryParse(_env, _hp) AndAlso _hp >= 0.0 Then headroomGB = _hp
            If availPhysGB >= projPeakGB + headroomGB Then Return True   ' roomy/idle box — no contention
            Dim consumers As String = MemPreflight_TopConsumers()
            Dim facts As String = $"availPhys {availPhysGB:F1}GB < projected peak ~{projPeakGB:F0}GB + {headroomGB:F0}GB headroom (availCommit {availCommitGB:F1}GB). Top memory consumers: {consumers}"
            If _headless Then
                AppendLog($"[MemPreflight§120] WARNING: {facts}. The governor will likely serialize the hot path (much slower run). See docs/MEMORY_STARVATION_PLAYBOOK.md.{vbCrLf}", 1)
                If Environment.GetEnvironmentVariable("PI_REQUIRE_FREE_RAM") = "1" Then
                    AppendLog($"[MemPreflight§120] PI_REQUIRE_FREE_RAM=1 — aborting before start (exit 3).{vbCrLf}", 1)
                    Environment.Exit(3)
                End If
                Return True
            Else
                AppendLog($"[MemPreflight§120] {facts} — showing contention dialog.{vbCrLf}", 1)
                Dim dlg As String = $"Memory contention detected." & vbCrLf & vbCrLf &
                    $"This {digits:N0}-digit run is expected to peak ~{projPeakGB:F0} GB, but only {availPhysGB:F1} GB of physical RAM is free." & vbCrLf &
                    $"Top memory consumers: {consumers}." & vbCrLf & vbCrLf &
                    "Running now will likely force the memory governor to serialize the computation (much slower)." & vbCrLf &
                    "Close other applications and retry for best performance." & vbCrLf & vbCrLf &
                    "Proceed anyway?"
                Return (MessageBox.Show(dlg, "Memory contention", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) = DialogResult.Yes)
            End If
        Catch
            Return True   ' never block a run on a pre-flight error
        End Try
    End Function

    ' §120 (#120): name the top-3 external processes by working set, to identify the RAM contender.
    Private Function MemPreflight_TopConsumers() As String
        Try
            Dim selfId As Integer = Process.GetCurrentProcess().Id
            Dim list As New System.Collections.Generic.List(Of ValueTuple(Of String, Long))()
            For Each p As Process In Process.GetProcesses()
                Try
                    If p.Id = selfId Then Continue For
                    list.Add((p.ProcessName, p.WorkingSet64))
                Catch
                End Try
            Next
            list.Sort(Function(a, b) b.Item2.CompareTo(a.Item2))
            Dim sb As New System.Text.StringBuilder()
            For i As Integer = 0 To System.Math.Min(2, list.Count - 1)
                If i > 0 Then sb.Append(", ")
                sb.Append($"{list(i).Item1} {list(i).Item2 / 1073741824.0:F1}GB")
            Next
            Return If(sb.Length > 0, sb.ToString(), "(none)")
        Catch
            Return "(unavailable)"
        End Try
    End Function

    ' §119 (#119): last-resort crash preservation.  Called by the global unhandled-exception handler
    ' (ApplicationEvents.vb) only when the normal pi_phase_log.txt write fails, so the crash is not
    ' lost.  Best-effort: registering the event source needs admin the FIRST time — if it can't be
    ' created and doesn't already exist, this degrades silently (the clipboard copy is the last resort).
    Friend Shared Sub WriteCrashToEventLog(text As String)
        Try
            If Not System.Diagnostics.EventLog.SourceExists(EVENTLOG_SOURCE) Then
                System.Diagnostics.EventLog.CreateEventSource(EVENTLOG_SOURCE, "Application")
            End If
            ' Event Log messages are capped at ~32 KB.
            Dim msg As String = If(text.Length > 30000, text.Substring(0, 30000) & "…(truncated)", text)
            System.Diagnostics.EventLog.WriteEntry(EVENTLOG_SOURCE, msg, System.Diagnostics.EventLogEntryType.Error)
        Catch
        End Try
    End Sub

    ' §119 (#119): at startup, scan the Application event log for prior crash records written by
    ' WriteCrashToEventLog (runs whose normal log write had failed).  Stores them in _priorCrashNote,
    ' which BtnCompute_Click writes into THIS run's log.  For an attended interactive run a dialog is
    ' also shown; headless / Auto-OK runs only get the log line (never a blocking modal).  Bounded scan.
    Private Sub ScanEventLogForPriorCrashes()
        Try
            Dim found As New System.Collections.Generic.List(Of String)()
            Using elog As New System.Diagnostics.EventLog("Application")
                Dim entries = elog.Entries
                Dim cutoff As DateTime = DateTime.Now.AddDays(-30)
                Dim scanned As Integer = 0
                For idx As Integer = entries.Count - 1 To 0 Step -1
                    scanned += 1
                    If scanned > 500 OrElse found.Count >= 10 Then Exit For
                    Try
                        Dim en As System.Diagnostics.EventLogEntry = entries(idx)
                        If en.TimeGenerated < cutoff Then Exit For
                        If String.Equals(en.Source, EVENTLOG_SOURCE) AndAlso en.EntryType = System.Diagnostics.EventLogEntryType.Error Then
                            Dim m As String = en.Message
                            found.Add($"  {en.TimeGenerated:yyyy-MM-dd HH:mm:ss}: {m.Substring(0, System.Math.Min(160, m.Length)).Replace(vbCrLf, " ").Replace(vbLf, " ")}")
                        End If
                    Catch
                    End Try
                Next
            End Using
            If found.Count = 0 Then Return
            _priorCrashNote = $"[Startup§119] {found.Count} prior crash record(s) found in the Windows Event Log (source {EVENTLOG_SOURCE}) — runs whose pi_phase_log.txt write had failed. View full detail in Event Viewer → Windows Logs → Application:" & vbCrLf & String.Join(vbCrLf, found)
            ' Attended interactive only: a dialog.  Headless / Auto-OK never block on a modal here.
            If Not _headless AndAlso Not _suppressDialogs Then
                Try : MessageBox.Show(_priorCrashNote, "Prior crash records found", MessageBoxButtons.OK, MessageBoxIcon.Warning) : Catch : End Try
            End If
        Catch
        End Try
    End Sub

    Private Sub BtnCompute_Click(sender As Object, e As EventArgs) Handles BtnCompute.Click
        ' Free any retained Pi buffer from the previous run before starting a new one.
        If _displayNativePtr <> IntPtr.Zero Then
            If _displayNativeBufSize >= GMP_LARGE_THRESHOLD Then
                VirtualFree(_displayNativePtr, UIntPtr.Zero, MEM_RELEASE)
            Else
                _savedGmpFree(New void_ptr(_displayNativePtr), New size_t(CULng(_displayNativeBufSize)))
            End If
            _displayNativePtr = IntPtr.Zero
            WriteToLog("[BtnCompute] retained native pi buffer freed before new computation")
        End If
        RtbPiDigits.Clear()
        LblDigitsDisplayed.Text = "0"
        BtnCompute.Enabled = False
        BtnPause.Enabled = True

        ' Capture threshold and log level from UI (or keep CLI-supplied values in headless mode).
        _diskThreshold = CInt(NudRamThreshold.Value)
        If Not _headless Then System.Threading.Volatile.Write(_logLevel, CInt(NudLogLevel.Value))  ' §27

        ' Pre-warm the thread pool to ProcessorCount threads before the compute
        ' thread starts.  Without this, the thread pool ramps up one thread at a
        ' time as Parallel.For enqueues work, causing LowLevelLifoSemaphore stalls
        ' during Phase 1 (137K tasks) and the early Phase 2 levels.
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount)

        DIGITS = CLng(TxtDigitsofPI.Text.Replace(",", ""))
        stopWatch.Restart()
        phaseStopWatch.Restart()
        cts = New CancellationTokenSource()
        Timer1.Start()
        LstBoxPhases.Items.Clear()
        LstBoxPhases.Items.Add($"Starting {DIGITS:N0} digits at {DateTime.Now:HH:mm:ss}")
        Try
            ' §252 (#95): names match the 0-5 ladder.  At level 0 write an EMPTY file (truly silent —
            ' the banner is the last ungated write; gating it makes level 0 byte-empty).
            Dim _levelNames() As String = {"Silent", "Errors+result", "Phase milestones", "Sub-phase", "Detailed", "Exceptionally detailed"}
            Dim loggingMode As String = $"{_logLevel} ({If(_logLevel >= 0 AndAlso _logLevel < _levelNames.Length, _levelNames(_logLevel), "Custom")})"
            System.IO.File.WriteAllText(LOG_FILE,
                If(System.Threading.Volatile.Read(_logLevel) >= 1,
                   $"=== PI Computation Started {DateTime.Now} ===" & vbCrLf &
                   $"=== Digits: {DIGITS:N0} ===" & vbCrLf &
                   $"=== Logging: {loggingMode} ===" & vbCrLf,
                   ""))
        Catch
        End Try
        ' §119 (#119): if startup found prior crash records in the Event Log, record them in THIS run's
        ' log now (after the header is written so they survive the truncate).  Applies in every mode —
        ' in particular headless / Auto-OK, where there was no startup dialog.
        If _priorCrashNote <> "" Then
            AppendLog(_priorCrashNote & vbCrLf, 1)
            _priorCrashNote = ""   ' once per run; don't repeat if Compute is clicked again
        End If
        ' §118 (#118): large-run safety guard.  A pure-UI direct launch defaults to no file + no
        ' checkpoint + Display on — so a multi-hour large run could finish with nothing saved, no resume
        ' point, and (per #117) still report Verify OK against the in-memory buffer.  For a large
        ' interactive run, auto-enable Write-to-File + AutoCheckpoint and turn Display off, logging what
        ' was forced.  Headless runs already set these via the script/flags, so they are left untouched.
        If Not _headless AndAlso DIGITS >= 100_000_000L Then
            Dim _forced As New System.Collections.Generic.List(Of String)()
            If Not ChkboxWriteToFile.Checked Then ChkboxWriteToFile.Checked = True : _forced.Add("Write-to-File ON")
            If Not _autoCheckpoint Then _autoCheckpoint = True : _forced.Add("AutoCheckpoint ON")
            If ChkboxDisplay.Checked Then ChkboxDisplay.Checked = False : _forced.Add("Display OFF")
            If _forced.Count > 0 Then
                AppendLog($"[LargeRun§118] {DIGITS:N0} digits ≥ 100M — auto-enabled for safety: {String.Join(", ", _forced)} (a large run must not finish with no file / no resume point).{vbCrLf}", 1)
            End If
        End If
        ' §120 (#120): memory-contention pre-flight (after the log exists so the [WARN] is captured,
        ' before the compute thread starts).  Aborts cleanly if the user cancels the contention dialog.
        If Not MemPreflight_ShouldProceed(DIGITS) Then
            AppendLog($"[MemPreflight§120] run cancelled before start (memory contention).{vbCrLf}", 1)
            LblStatus.Text = "Cancelled — memory contention (see log)."
            BtnCompute.Enabled = True
            BtnPause.Enabled = False
            Timer1.Stop()
            Return
        End If
        RtbPiDigits.AppendText("Starting computation..." & vbCrLf)
        Dim computeThread As New System.Threading.Thread(
            Sub()
                Dim _runOutcome As String = "crashed"   ' §258 (#62/#63): run_history.json outcome
                Dim _telDone As Boolean = False
                ' §260 (#63): surface the evergreen hardware advice (XMP/channel/DRAM speed) at run
                ' start.  coresActive=0 ⇒ the live compute-utilisation rules are skipped (they need a
                ' CPU sample, deferred); only the hardware rules fire.  Also warms the HW fingerprint.
                Try : Advisor_Render(Advisor_CurrentMetrics(0.0, RunStageId.Phase1)) : Catch : End Try
                Try
                    Dim result As String = ComputePiGMP(DIGITS, cts.Token)
                    _runOutcome = If(cts.IsCancellationRequested, "aborted", "success")
                    ' §258: write the run record HERE (all stages are recorded inside ComputePiGMP,
                    ' which has returned).  The headless autoverify path below calls Application.Exit
                    ' via a synchronous Invoke and the process can tear down before the Finally runs,
                    ' so the success/abort write must happen synchronously on this thread first.
                    Try : Telemetry_WriteRunHistory(_runOutcome, DIGITS) : _telDone = True : Catch : End Try
                    If _displayNativePtr <> IntPtr.Zero OrElse result <> "" Then
                        Me.Invoke(Sub() StreamPiToScreen(result))
                    End If
                    ' --autoverify: run verify logic headlessly then exit.
                    ' Requires both _headless AND _autoVerify so interactive runs
                    ' never auto-exit even if --autoverify was somehow received.
                    If _headless AndAlso _autoVerify Then
                        Me.Invoke(Sub()
                                      RunVerification()
                                      Application.Exit()
                                  End Sub)
                    End If
                Catch oex As OutOfMemoryException
                    WriteExceptionToLog("ComputeThread/OutOfMemoryException", oex)
                    Me.Invoke(Sub()
                                  If Not _headless Then
                                      MessageBox.Show("OUT OF MEMORY!" & vbCrLf & oex.Message & vbCrLf & oex.StackTrace)
                                  Else
                                      ' §76 (issue #76): headless must exit on exception, otherwise the
                                      ' form's message loop keeps the process alive at 0% CPU and blocks
                                      ' Run-PiCompute.ps1's post-run BackupCheckpoint step.
                                      WriteToLog("[DIALOG] OUT OF MEMORY: " & oex.Message)
                                      Environment.ExitCode = 1
                                      Application.Exit()
                                  End If
                                  LblStatus.Text = "Error: Out of memory"
                                  BtnCompute.Enabled = True
                                  BtnPause.Enabled = False
                                  Timer1.Stop()
                              End Sub)
                Catch ovex As OverflowException
                    WriteExceptionToLog("ComputeThread/OverflowException", ovex)
                    Me.Invoke(Sub()
                                  If Not _headless Then
                                      MessageBox.Show("OVERFLOW!" & vbCrLf & ovex.Message & vbCrLf & ovex.StackTrace)
                                  Else
                                      ' §76 (issue #76): headless must exit on exception.
                                      WriteToLog("[DIALOG] OVERFLOW: " & ovex.Message)
                                      Environment.ExitCode = 1
                                      Application.Exit()
                                  End If
                                  LblStatus.Text = "Error: Overflow"
                                  BtnCompute.Enabled = True
                                  BtnPause.Enabled = False
                                  Timer1.Stop()
                              End Sub)
                Catch ex As Exception
                    WriteExceptionToLog("ComputeThread", ex)
                    Me.Invoke(Sub()
                                  If Not _headless Then
                                      MessageBox.Show("EXCEPTION: " & ex.GetType().Name & vbCrLf & ex.Message & vbCrLf & ex.StackTrace)
                                  Else
                                      ' §76 (issue #76): headless must exit on exception.
                                      WriteToLog("[DIALOG] EXCEPTION: " & ex.GetType().Name & ": " & ex.Message)
                                      Environment.ExitCode = 1
                                      Application.Exit()
                                  End If
                                  LblStatus.Text = "Error: " & ex.Message
                                  BtnCompute.Enabled = True
                                  BtnPause.Enabled = False
                                  Timer1.Stop()
                              End Sub)
                Finally
                    ' §258 (#62/#63): fallback for the crash/abort paths (the success path already
                    ' wrote synchronously above).  Best-effort — never rethrows.
                    If Not _telDone Then
                        Try : Telemetry_WriteRunHistory(_runOutcome, DIGITS) : Catch : End Try
                    End If
                End Try
            End Sub, 256 * 1024 * 1024)
        computeThread.IsBackground = True
        computeThread.Priority = Threading.ThreadPriority.AboveNormal
        computeThread.Start()
    End Sub

    Private Sub BtnPause_Click_1(sender As Object, e As EventArgs) Handles BtnPause.Click
        ' Confirmation dialog — cancellation discards all progress (no checkpoint exists).
        If Not _headless Then
            Dim confirm As DialogResult = MessageBox.Show(
                "This will cancel the computation and all progress will be lost." & vbCrLf &
                "Are you sure you want to cancel?",
                "Cancel Computation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2)
            If confirm <> DialogResult.Yes Then Return
        End If

        cts.Cancel()
        displayTimer.Enabled = False
        BtnPause.Enabled = False
        BtnCompute.Enabled = True
        Timer1.Stop()
        LblStatus.Text = "Cancelled."
        WriteToLog("Computation cancelled by user.")
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Chudnovsky binary splitting — chunk level
    ' ════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Recursively binary-splits the Chudnovsky term range [a, b) into the three integer partial
    ''' products P, Q, T for that range (the leaf-to-chunk level of the binary-splitting tree).
    ''' Serial recursion; the parallel variant is <see cref="BinarySplitChunkParallelTop"/>.
    ''' </summary>
    ''' <param name="a">Inclusive start term index.</param>
    ''' <param name="b">Exclusive end term index.</param>
    ''' <param name="Pab">Receives P for [a, b).</param>
    ''' <param name="Qab">Receives Q for [a, b).</param>
    ''' <param name="Tab">Receives T for [a, b).</param>
    Private Sub BinarySplitChunk(a As Long, b As Long,
                          ByRef Pab As mpz_t,
                          ByRef Qab As mpz_t,
                          ByRef Tab As mpz_t)
        If _logLevel >= 4 Then WriteToLog($"[BinarySplitChunk] Enter  a={a:N0}  b={b:N0}  terms={b - a:N0}")

        ' Issue #5 fix: pre-size both collections to avoid internal array resizes.
        ' maxDepth is an upper bound on the stack depth for this range.
        ' The dictionary holds at most (b-a) simultaneous results.
        Dim maxDepth As Integer = CInt(System.Math.Ceiling(System.Math.Log(b - a, 2))) * 2
        Dim workStack As New Stack(Of WorkItem)(maxDepth + 4)
        Dim results As New Dictionary(Of Integer, Result)(CInt(b - a))
        Dim nextIndex As Integer = 0

        ' Push initial work
        workStack.Push(New WorkItem With {.a = a, .b = b, .resultIndex = 0, .isComplete = False})

        Dim currentWorkItem As WorkItem  ' declared outside loop to log on exception
        Try
            While workStack.Count > 0
                currentWorkItem = workStack.Pop()

                ' Base case: single term
                If currentWorkItem.b - currentWorkItem.a = 1 Then
                    Dim res As New Result With {
                        .P = New mpz_t(),
                        .Q = New mpz_t(),
                        .T = New mpz_t()
                    }
                    gmp_lib.mpz_inits(res.P, res.Q, res.T, Nothing)

                    If currentWorkItem.a = 0 Then
                        ' §108/§32B: a=0 special case: P=Q=1, T=13591409
                        GmpRaw_set_ui(res.P.Pointer, 1UI)
                        GmpRaw_set_ui(res.Q.Pointer, 1UI)
                        GmpRaw_set_ui(res.T.Pointer, 13591409UI)
                    Else
                        ' §32B: Single managed→native crossing for all 11 GMP ops.
                        ' GmpBatch_ComputeTerm allocates/frees its own temps via the native pool.
                        GmpBatch_ComputeTerm(currentWorkItem.a, res.P.Pointer, res.Q.Pointer, res.T.Pointer, gmpC3Const.Pointer)
                    End If

                    results(currentWorkItem.resultIndex) = res

                ElseIf currentWorkItem.isComplete Then
                    ' Combine results from left and right children
                    Dim leftRes As Result = results(currentWorkItem.leftChildIndex)
                    Dim rightRes As Result = results(currentWorkItem.rightChildIndex)

                    Dim res As New Result With {
                        .P = New mpz_t(),
                        .Q = New mpz_t(),
                        .T = New mpz_t()
                    }
                    Dim tempA As New mpz_t()
                    Dim tempB As New mpz_t()
                    gmp_lib.mpz_inits(res.P, res.Q, res.T, tempA, tempB, Nothing)

                    ' §32B: Single managed→native crossing for all 5 combine GMP ops.
                    GmpBatch_CombineNodes(
                        res.P.Pointer, res.Q.Pointer, res.T.Pointer,
                        leftRes.P.Pointer, leftRes.Q.Pointer, leftRes.T.Pointer,
                        rightRes.P.Pointer, rightRes.Q.Pointer, rightRes.T.Pointer,
                        tempA.Pointer, tempB.Pointer)

                    gmp_lib.mpz_clears(leftRes.P, leftRes.Q, leftRes.T, Nothing)
                    gmp_lib.mpz_clears(rightRes.P, rightRes.Q, rightRes.T, Nothing)
                    gmp_lib.mpz_clears(tempA, tempB, Nothing)

                    results.Remove(currentWorkItem.leftChildIndex)
                    results.Remove(currentWorkItem.rightChildIndex)
                    results(currentWorkItem.resultIndex) = res
                Else
                    ' Split into two sub-problems
                    Dim mid As Long = (currentWorkItem.a + currentWorkItem.b) \ 2
                    nextIndex += 1
                    Dim leftIdx As Integer = nextIndex
                    nextIndex += 1
                    Dim rightIdx As Integer = nextIndex

                    ' Push marker to combine results later
                    workStack.Push(New WorkItem With {
                        .a = currentWorkItem.a,
                        .b = currentWorkItem.b,
                        .resultIndex = currentWorkItem.resultIndex,
                        .isComplete = True,
                        .leftChildIndex = leftIdx,
                        .rightChildIndex = rightIdx
                    })

                    ' Push right child first (processed second)
                    workStack.Push(New WorkItem With {
                        .a = mid,
                        .b = currentWorkItem.b,
                        .resultIndex = rightIdx,
                        .isComplete = False
                    })

                    ' Push left child (processed first)
                    workStack.Push(New WorkItem With {
                        .a = currentWorkItem.a,
                        .b = mid,
                        .resultIndex = leftIdx,
                        .isComplete = False
                    })
                End If
            End While

        Catch ex As Exception
            ' Log the exact work item that triggered the failure before re-throwing.
            WriteExceptionToLog(
                $"BinarySplitChunk(a={a},b={b}) — failed on WorkItem(a={currentWorkItem.a},b={currentWorkItem.b},isComplete={currentWorkItem.isComplete})",
                ex)
            Throw
        End Try

        ' Return the final result
        Dim finalResult As Result = results(0)
        Pab = finalResult.P
        Qab = finalResult.Q
        Tab = finalResult.T
        If _logLevel >= 4 Then WriteToLog($"[BinarySplitChunk] Exit   a={a:N0}  b={b:N0}  stackPeak={maxDepth}")
    End Sub

    ' §234 (issue #59, 2026-05-23): tail-mode parallel top-split for
    ' BinarySplitChunk.  Splits the term range [a, b) in half and computes the
    ' two halves concurrently via Parallel.Invoke; each half recurses serially
    ' through the standard BinarySplitChunk path, then the top combine uses
    ' GmpBatch_CombineNodes (same as the serial DFS's combine).
    '
    ' Used only by Phase 1's outer Parallel.For when the chunk index falls in
    ' the last ~24 chunks AND the chunk has ≥ 512 terms (enough to amortize
    ' Parallel.Invoke scheduling).  The outer queue depth has dropped below the
    ' 24-core DOP at that point, so the inner Parallel.Invoke fills idle cores
    ' without oversubscribing.
    ''' <summary>
    ''' Top-split parallel variant of <see cref="BinarySplitChunk"/> (§234): splits [a, b) at the
    ''' midpoint and computes the two halves via Parallel.Invoke before combining. Used only by Phase 1's
    ''' outer Parallel.For for the last ~24 chunks (≥ 512 terms each), where the outer queue depth has
    ''' dropped below the core count so the inner parallelism fills idle cores without oversubscribing.
    ''' </summary>
    ''' <param name="a">Inclusive start term index.</param>
    ''' <param name="b">Exclusive end term index.</param>
    ''' <param name="Pab">Receives P for [a, b).</param>
    ''' <param name="Qab">Receives Q for [a, b).</param>
    ''' <param name="Tab">Receives T for [a, b).</param>
    Private Sub BinarySplitChunkParallelTop(a As Long, b As Long,
                                            ByRef Pab As mpz_t,
                                            ByRef Qab As mpz_t,
                                            ByRef Tab As mpz_t)
        If _logLevel >= 4 Then WriteToLog($"[BinarySplitChunkParallelTop§234] Enter  a={a:N0}  b={b:N0}  terms={b - a:N0}")
        Dim mid As Long = (a + b) \ 2

        Dim Pl As mpz_t = Nothing, Ql As mpz_t = Nothing, Tl As mpz_t = Nothing
        Dim Pr As mpz_t = Nothing, Qr As mpz_t = Nothing, Tr As mpz_t = Nothing

        System.Threading.Tasks.Parallel.Invoke(
            Sub() BinarySplitChunk(a, mid, Pl, Ql, Tl),
            Sub() BinarySplitChunk(mid, b, Pr, Qr, Tr))

        ' Combine the two halves using the same GMP batch routine as the inner
        ' DFS's combine step (line ~1860).
        Pab = New mpz_t()
        Qab = New mpz_t()
        Tab = New mpz_t()
        Dim tempA As New mpz_t()
        Dim tempB As New mpz_t()
        gmp_lib.mpz_inits(Pab, Qab, Tab, tempA, tempB, Nothing)

        GmpBatch_CombineNodes(
            Pab.Pointer, Qab.Pointer, Tab.Pointer,
            Pl.Pointer, Ql.Pointer, Tl.Pointer,
            Pr.Pointer, Qr.Pointer, Tr.Pointer,
            tempA.Pointer, tempB.Pointer)

        gmp_lib.mpz_clears(Pl, Ql, Tl, Nothing)
        gmp_lib.mpz_clears(Pr, Qr, Tr, Nothing)
        gmp_lib.mpz_clears(tempA, tempB, Nothing)

        If _logLevel >= 4 Then WriteToLog($"[BinarySplitChunkParallelTop§234] Exit   a={a:N0}  b={b:N0}")
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Disk serialization / deserialization
    ' ════════════════════════════════════════════════════════════════════════

    ' Issue #2 fix: replaced per-field managed byte arrays (which land on the
    ' LOH and never get compacted) with a single staging buffer that is
    ' reused for all three fields.  The 4 MB buffer (§56) exceeds the 85 KB LOH
    ' threshold, but it is short-lived (local to each call) and allocated only
    ' once per node serialized, so LOH fragmentation is not a practical concern.
    ' The 64x larger buffer reduces Marshal.Copy loop iterations 64x for large
    ' mpz_t values (e.g. Level-17 ~560 MB: ~8,738 → ~137 iterations).
    '
    ' Issue #6 fix (partial): signature takes three mpz_t directly instead of a
    ' Tuple(Of mpz_t,mpz_t,mpz_t), eliminating one throw-away heap allocation
    ' per call (~137 K calls for 1 B digits).
    ''' <summary>
    ''' Writes one binary-split node's (P, Q, T) to a single binary file, using a reused 4 MB staging
    ''' buffer (§56) to avoid per-field LOH allocations. The unit of Phase 1's disk-based node cache.
    ''' </summary>
    ''' <param name="p">Node P value.</param>
    ''' <param name="q">Node Q value.</param>
    ''' <param name="t">Node T value.</param>
    ''' <param name="filePath">Destination file (overwritten if present).</param>
    ''' <param name="detailLog">When True, emit per-node serialize logging.</param>
    Private Sub SerializeNodeToDisk(p As mpz_t, q As mpz_t, t As mpz_t, filePath As String,
                                    Optional detailLog As Boolean = True)
        If _logLevel >= 2 Then WriteToLog($"[Serialize] Writing {System.IO.Path.GetFileName(filePath)}")
        Dim staging(4194303) As Byte  ' 4 MB staging buffer (§56) — reused for all three fields
        Try
            Using fs As New FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(p, bw, staging)
                    SerializeOneMpz(q, bw, staging)
                    SerializeOneMpz(t, bw, staging)
                End Using
            End Using
            If _logLevel >= 2 Then
                Dim fileSize As Long = New FileInfo(filePath).Length
                WriteToLog($"[Serialize] Done   {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
            End If
        Catch ex As Exception
            WriteExceptionToLog($"SerializeNodeToDisk({filePath})", ex)
            LogPhase($"Error serializing node to {filePath}: {ex.Message}")
            Throw
        End Try
    End Sub

    ' §94: Write a complete snapshot of diskNodes to NodeCache\snap_L{level}\.
    ' Called after diskNodes = nextDiskNodes at the end of each Phase 2 level,
    ' before GC/FlushGmpPool so that in-memory nodes are still live.
    ' meta.txt is written last; its presence on disk signals a complete snapshot.
    ' numTerms and numChunks are embedded in meta.txt for validation on resume.
    ''' <summary>
    ''' Writes a complete Phase 2 level snapshot to NodeCache\snap_L{level}\ (§94). meta.txt is written
    ''' last and embeds numTerms/numChunks; its presence signals a complete, resumable snapshot.
    ''' </summary>
    ''' <param name="level">Phase 2 combine level being snapshotted.</param>
    ''' <param name="nodes">The level's disk nodes to record.</param>
    ''' <param name="numTerms">Run's total term count (validated on resume).</param>
    ''' <param name="numChunks">Run's chunk count (validated on resume).</param>
    ''' <returns>True if the snapshot was written completely.</returns>
    Private Function WriteLevelSnapshot(level As Integer,
                                       nodes As List(Of DiskNode),
                                       numTerms As Long,
                                       numChunks As Long) As Boolean
        Dim snapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, $"snap_L{level}") &
                                System.IO.Path.DirectorySeparatorChar
        LogPhase($"[Snapshot] Writing level {level} snapshot: {nodes.Count:N0} nodes → {snapDir}")
        Try
            System.IO.Directory.CreateDirectory(snapDir)
            Dim failCount As Long = 0L
            Parallel.For(0, nodes.Count,
                Sub(idx As Integer)
                    Dim node As DiskNode = nodes(idx)
                    Dim destPath As String = snapDir & $"N{node.Index}.bin"
                    Try
                        If node.IsInMemory Then
                            SerializeNodeToDisk(node.MemP, node.MemQ, node.MemT, destPath)
                        Else
                            System.IO.File.Copy(node.FilePath, destPath, overwrite:=True)
                        End If
                    Catch ex As Exception
                        Interlocked.Increment(failCount)
                        WriteToLog($"[Snapshot] WARN: failed to write N{node.Index}.bin: {ex.Message}")
                    End Try
                End Sub)

            If failCount > 0L Then
                LogPhase($"[Snapshot] WARNING: {failCount:N0} node(s) failed — snapshot incomplete, skipping meta.txt")
                Return False
            End If

            ' Write meta.txt last — its presence marks the snapshot as complete.
            Dim metaPath As String = snapDir & "meta.txt"
            System.IO.File.WriteAllText(metaPath,
                $"digits={DIGITS}" & vbCrLf &
                $"numTerms={numTerms}" & vbCrLf &
                $"numChunks={numChunks}" & vbCrLf &
                $"level={level}" & vbCrLf &
                $"nodeCount={nodes.Count}" & vbCrLf &
                $"timestamp={DateTime.Now:yyyy-MM-dd HH:mm:ss}" & vbCrLf)
            LogPhase($"[Snapshot] Level {level} snapshot complete")
            Return True
        Catch ex As Exception
            WriteExceptionToLog($"WriteLevelSnapshot(level={level})", ex)
            LogPhase($"[Snapshot] ERROR writing snapshot: {ex.Message} — continuing without checkpoint")
            Return False
        End Try
    End Function

    ' §94: Scan NodeCache for the highest-level complete snapshot that matches
    ' the current run parameters (digits + numChunks).  Returns the level number
    ' or -1 if no valid snapshot is found.
    ''' <summary>
    ''' Scans NodeCache for the highest-level complete snapshot whose metadata (digits + numChunks)
    ''' matches the current run, so Phase 2 can resume from it (§94).
    ''' </summary>
    ''' <param name="numChunks">Current run's chunk count, matched against each snapshot's meta.txt.</param>
    ''' <returns>The matching level number, or -1 if no valid snapshot exists.</returns>
    Private Function TryFindBestSnapshot(numChunks As Long) As Integer
        If Not System.IO.Directory.Exists(DISK_CACHE_DIR) Then Return -1
        Dim bestLevel As Integer = -1
        For Each subDir As String In System.IO.Directory.GetDirectories(DISK_CACHE_DIR, "snap_L*")
            Dim dirName As String = System.IO.Path.GetFileName(subDir)
            Dim lvl As Integer = 0
            If Not Integer.TryParse(dirName.Substring(6), lvl) Then Continue For
            Dim metaPath As String = System.IO.Path.Combine(subDir, "meta.txt")
            If Not System.IO.File.Exists(metaPath) Then Continue For
            Try
                Dim meta As New Dictionary(Of String, String)()
                For Each line As String In System.IO.File.ReadAllLines(metaPath)
                    Dim eq As Integer = line.IndexOf("="c)
                    If eq > 0 Then meta(line.Substring(0, eq)) = line.Substring(eq + 1)
                Next
                Dim snapDigits As Long = 0L, snapChunks As Long = 0L
                Dim snapNodeCount As Integer = 0, snapLevel As Integer = 0
                If Not meta.ContainsKey("digits") OrElse Not Long.TryParse(meta("digits"), snapDigits) Then Continue For
                If Not meta.ContainsKey("numChunks") OrElse Not Long.TryParse(meta("numChunks"), snapChunks) Then Continue For
                If Not meta.ContainsKey("nodeCount") OrElse Not Integer.TryParse(meta("nodeCount"), snapNodeCount) Then Continue For
                If Not meta.ContainsKey("level") OrElse Not Integer.TryParse(meta("level"), snapLevel) Then Continue For
                If snapDigits <> DIGITS OrElse snapChunks <> numChunks Then
                    WriteToLog($"[Snapshot] Skipping snap_L{lvl}: digits/chunks mismatch")
                    Continue For
                End If
                ' Verify node file count matches metadata.
                ' Node indices are not guaranteed to be 0..N-1 (parallel path uses pairIdx),
                ' so count N*.bin files rather than probing for each sequential index.
                Dim actualCount As Integer = System.IO.Directory.GetFiles(subDir, "N*.bin").Length
                If actualCount <> snapNodeCount Then
                    WriteToLog($"[Snapshot] Skipping snap_L{lvl}: expected {snapNodeCount} node files, found {actualCount}")
                    Continue For
                End If
                If lvl > bestLevel Then bestLevel = lvl
            Catch ex As Exception
                WriteToLog($"[Snapshot] Skipping snap_L{lvl}: error reading meta — {ex.Message}")
            End Try
        Next
        Return bestLevel
    End Function

    ' §104: Immediately copy a NodeCache snapshot to SnapshotStore after it is
    ' written, so the backup is current before Phase 2 loads and deletes the files.
    ''' <summary>
    ''' Synchronously mirrors a NodeCache snapshot directory to SnapshotStore (§104) so a current backup
    ''' exists before Phase 2 consumes (and deletes) the live NodeCache files. See
    ''' <see cref="BackupSnapshotToStoreAsync"/> for the off-critical-path variant.
    ''' </summary>
    ''' <param name="snapName">Snapshot directory name (e.g. "snap_L7", "snap_Phase3").</param>
    Private Shared Sub BackupSnapshotToStore(snapName As String)
        Try
            Dim storeDir As String = System.IO.Path.Combine(_outputDir, "SnapshotStore")
            Dim src As String = System.IO.Path.Combine(DISK_CACHE_DIR, snapName)
            Dim dst As String = System.IO.Path.Combine(storeDir, snapName)
            If Not System.IO.Directory.Exists(src) Then Return
            If Not System.IO.Directory.Exists(storeDir) Then
                System.IO.Directory.CreateDirectory(storeDir)
            End If
            If System.IO.Directory.Exists(dst) Then
                System.IO.Directory.Delete(dst, recursive:=True)
            End If
            System.IO.Directory.CreateDirectory(dst)
            For Each srcFile As String In System.IO.Directory.GetFiles(src)
                System.IO.File.Copy(srcFile, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(srcFile)))
            Next
            AppendLog($"[Snapshot] Backed up {snapName} to SnapshotStore{vbCrLf}")
        Catch ex As Exception
            AppendLog($"[Snapshot] WARN: backup of {snapName} to SnapshotStore failed: {ex.Message}{vbCrLf}")
        End Try
    End Sub

    ' §232 (issue #46, 2026-05-23): async tail-chained BackupSnapshotToStore.
    '
    ' Synchronous BackupSnapshotToStore copies the entire snap directory contents
    ' to SnapshotStore — at 1 B snap_Phase3 is ~25 GB, at 5 B ~150 GB.  Per Newton
    ' iter (called inside §NR-ckpt save) this was on the synchronous compute
    ' critical path: ~1-2 s per iter × ~35 iters = 30-60 s blocked compute at 1 B
    ' (~5-15 s × 35 iters = 3-9 min at 5 B).
    '
    ' Design: maintain a "tail" Task that represents the latest enqueued backup.
    ' Each call to BackupSnapshotToStoreAsync chains a continuation that runs
    ' AFTER the prior tail completes — serial execution + every commit eventually
    ' reflected in SnapshotStore.  Compute thread doesn't wait.
    '
    ' Per-call cost: enqueue + lock = microseconds.  Compute resumes immediately;
    ' the disk I/O happens on a background ThreadPool thread, overlapping with
    ' the next Newton iter (typically 60-90 s of compute at 5 B).
    '
    ' Shutdown: WaitForPendingBackups drains the chain at FormClosing so the
    ' SnapshotStore has the canonical final state before the process exits.
    Private Shared _bkstoreTail As Task = Task.CompletedTask
    Private Shared ReadOnly _bkstoreTailLock As New Object()

    ''' <summary>
    ''' Tail-chained async variant of <see cref="BackupSnapshotToStore"/> (§232): queues the mirror on a
    ''' background ThreadPool thread so the copy overlaps the next Newton iteration. Backups serialize via
    ''' a single tail Task; WaitForPendingBackups drains the chain at shutdown.
    ''' </summary>
    ''' <param name="snapName">Snapshot directory name to mirror to SnapshotStore.</param>
    Private Shared Sub BackupSnapshotToStoreAsync(snapName As String)
        SyncLock _bkstoreTailLock
            Dim _capturedSnap As String = snapName  ' explicit capture to avoid closure surprises
            _bkstoreTail = _bkstoreTail.ContinueWith(
                Sub(prior)
                    BackupSnapshotToStore(_capturedSnap)
                End Sub,
                TaskContinuationOptions.None)
        End SyncLock
    End Sub

    Private Shared Sub WaitForPendingBackups(timeoutMs As Integer)
        Dim _t As Task
        SyncLock _bkstoreTailLock
            _t = _bkstoreTail
        End SyncLock
        If _t Is Nothing OrElse _t.IsCompleted Then Return
        Try
            AppendLog($"[Snapshot§232] WaitForPendingBackups: draining pending backup tail (timeout={timeoutMs}ms){vbCrLf}")
            Dim _startTicks As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            Dim _ok As Boolean = _t.Wait(timeoutMs)
            Dim _elapsedSec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) / System.Diagnostics.Stopwatch.Frequency
            AppendLog($"[Snapshot§232] WaitForPendingBackups: {(If(_ok, "drained", "TIMED OUT"))} in {_elapsedSec:F2}s{vbCrLf}")
        Catch ex As Exception
            AppendLog($"[Snapshot§232] WaitForPendingBackups exception: {ex.Message}{vbCrLf}")
        End Try
    End Sub

    ' §104: Remove a superseded snapshot from SnapshotStore.
    Private Sub DeleteSnapshotFromStore(level As Integer)
        Try
            Dim dst As String = System.IO.Path.Combine(_outputDir, "SnapshotStore", $"snap_L{level}")
            If System.IO.Directory.Exists(dst) Then
                System.IO.Directory.Delete(dst, recursive:=True)
                WriteToLog($"[Snapshot] Removed superseded SnapshotStore entry snap_L{level}")
            End If
        Catch ex As Exception
            WriteToLog($"[Snapshot] WARN: could not remove SnapshotStore snap_L{level}: {ex.Message}")
        End Try
    End Sub

    ' §103: Save finalP/finalQ/finalT to snap_Phase3/ before Phase 3 begins.
    ' Allows Phase 3 to be re-run from this checkpoint without repeating Phase 1/2.
    ' §106: Save a single named mpz_t to snap_Phase3/ for mid-Phase-3 resumption.
    ' name should be a safe filename stem (e.g. "gmpNumer", "mpR0").
    ' Backs up snap_Phase3 to SnapshotStore immediately after writing.
    Private Sub SavePhase3Value(name As String, val As mpz_t, p3SnapDir As String)
        If Not _autoCheckpoint Then Return
        Try
            If Not System.IO.Directory.Exists(p3SnapDir) Then
                System.IO.Directory.CreateDirectory(p3SnapDir)
            End If
            Dim path As String = System.IO.Path.Combine(p3SnapDir, name & ".bin")
            Dim staging(4194303) As Byte
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(val, bw, staging)
                End Using
            End Using
            BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
            LogPhase($"[ComputePi] Checkpoint: {name} saved (~{CLng(gmp_lib.mpz_sizeinbase(val, 10)):N0} digits)")
        Catch ex As Exception
            LogPhase($"[ComputePi] Checkpoint: {name} save failed: {ex.Message}")
        End Try
    End Sub

    ' §106: Load a single named mpz_t from snap_Phase3/ if it exists.
    ' Returns True and populates val on success; returns False if the file is missing.
    ' val must already be mpz_init'd by the caller.
    Private Function TryLoadPhase3Value(name As String, val As mpz_t, p3SnapDir As String) As Boolean
        If Not _autoCheckpoint Then Return False
        Try
            Dim path As String = System.IO.Path.Combine(p3SnapDir, name & ".bin")
            If Not System.IO.File.Exists(path) Then Return False
            Dim staging(4194303) As Byte
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(val, br, staging)
                End Using
            End Using
            LogPhase($"[ComputePi] Checkpoint: {name} loaded (~{CLng(gmp_lib.mpz_sizeinbase(val, 10)):N0} digits)")
            Return True
        Catch ex As Exception
            LogPhase($"[ComputePi] Checkpoint: {name} load failed: {ex.Message} — recomputing")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Saves the Phase 3 entry checkpoint — the root binary-split values (finalP, finalQ, finalT) to
    ''' snap_Phase3\ (§103) — so a crash during the long Phase 3 arithmetic resumes without re-running
    ''' Phase 1/2. Backed up to SnapshotStore asynchronously (§232).
    ''' </summary>
    ''' <param name="snapDir">Target snapshot directory.</param>
    ''' <param name="digits">Run digit count (recorded for resume validation).</param>
    ''' <param name="numTerms">Run term count (recorded for resume validation).</param>
    ''' <param name="finalP">Root P.</param>
    ''' <param name="finalQ">Root Q.</param>
    ''' <param name="finalT">Root T.</param>
    Private Sub SavePhase3Snapshot(snapDir As String, digits As Long, numTerms As Long,
                                    finalP As mpz_t, finalQ As mpz_t, finalT As mpz_t)
        Try
            LogPhase("[ComputePi] Saving snap_Phase3 checkpoint (P, Q, T)...")
            If System.IO.Directory.Exists(snapDir) Then
                System.IO.Directory.Delete(snapDir, recursive:=True)
            End If
            System.IO.Directory.CreateDirectory(snapDir)
            Dim staging(4194303) As Byte   ' 4 MB staging buffer
            Using fs As New FileStream(System.IO.Path.Combine(snapDir, "P.bin"),
                                       FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(finalP, bw, staging)
                End Using
            End Using
            Using fs As New FileStream(System.IO.Path.Combine(snapDir, "Q.bin"),
                                       FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(finalQ, bw, staging)
                End Using
            End Using
            Using fs As New FileStream(System.IO.Path.Combine(snapDir, "T.bin"),
                                       FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(finalT, bw, staging)
                End Using
            End Using
            Dim metaContent As String =
                $"digits={digits}{vbLf}" &
                $"numTerms={numTerms}{vbLf}" &
                $"P_digits={CLng(gmp_lib.mpz_sizeinbase(finalP, 10)):N0}{vbLf}" &
                $"Q_digits={CLng(gmp_lib.mpz_sizeinbase(finalQ, 10)):N0}{vbLf}" &
                $"T_digits={CLng(gmp_lib.mpz_sizeinbase(finalT, 10)):N0}{vbLf}"
            System.IO.File.WriteAllText(System.IO.Path.Combine(snapDir, "meta.txt"), metaContent)
            LogPhase($"[ComputePi] snap_Phase3 saved OK" &
                     $" (P~{CLng(gmp_lib.mpz_sizeinbase(finalP, 10)):N0}" &
                     $" Q~{CLng(gmp_lib.mpz_sizeinbase(finalQ, 10)):N0}" &
                     $" T~{CLng(gmp_lib.mpz_sizeinbase(finalT, 10)):N0} digits)")
            ' §104: Back up snap_Phase3 immediately — don't wait for end-of-run script backup.
            BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
        Catch ex As Exception
            LogPhase($"[ComputePi] snap_Phase3 save FAILED: {ex.Message} — continuing without checkpoint")
        End Try
    End Sub

    ' §103: Load finalP/finalQ/finalT from snap_Phase3/ if it exists and matches digits.
    ' Returns True and populates outP/outQ/outT on success; returns False on any mismatch or error.
    ' outP/outQ/outT must already be mpz_init'd by the caller.
    ''' <summary>
    ''' Loads the Phase 3 checkpoint (finalP/finalQ/finalT) from snap_Phase3\ if present and matching the
    ''' current digit count (§103), letting Phase 3 resume without recomputing Phase 1/2.
    ''' </summary>
    ''' <param name="snapDir">Snapshot directory to load from.</param>
    ''' <param name="digits">Expected digit count; a mismatch is rejected.</param>
    ''' <param name="outP">Receives root P (must be mpz_init'd by the caller).</param>
    ''' <param name="outQ">Receives root Q (must be mpz_init'd by the caller).</param>
    ''' <param name="outT">Receives root T (must be mpz_init'd by the caller).</param>
    ''' <returns>True on a successful, validated load; False on any mismatch or error.</returns>
    Private Function TryLoadPhase3Snapshot(snapDir As String, digits As Long,
                                            outP As mpz_t, outQ As mpz_t, outT As mpz_t) As Boolean
        Try
            If Not System.IO.Directory.Exists(snapDir) Then Return False
            Dim pPath As String = System.IO.Path.Combine(snapDir, "P.bin")
            Dim qPath As String = System.IO.Path.Combine(snapDir, "Q.bin")
            Dim tPath As String = System.IO.Path.Combine(snapDir, "T.bin")
            Dim metaPath As String = System.IO.Path.Combine(snapDir, "meta.txt")
            If Not (System.IO.File.Exists(pPath) AndAlso System.IO.File.Exists(qPath) AndAlso
                    System.IO.File.Exists(tPath) AndAlso System.IO.File.Exists(metaPath)) Then
                WriteToLog("[Phase3Snap] snap_Phase3 missing one or more files — skipping")
                Return False
            End If
            ' Verify digits match
            Dim meta As New Dictionary(Of String, String)()
            For Each metaLine As String In System.IO.File.ReadAllLines(metaPath)
                Dim eq As Integer = metaLine.IndexOf("="c)
                If eq > 0 Then meta(metaLine.Substring(0, eq)) = metaLine.Substring(eq + 1)
            Next
            Dim snapDigits As Long = 0L
            If Not meta.ContainsKey("digits") OrElse
               Not Long.TryParse(meta("digits"), snapDigits) OrElse
               snapDigits <> digits Then
                WriteToLog($"[Phase3Snap] snap_Phase3 digits mismatch (want {digits:N0}, " &
                           $"have {If(meta.ContainsKey("digits"), meta("digits"), "?")}) — skipping")
                Return False
            End If
            ' Deserialize — DeserializeOneMpz handles large limb counts via PoolGet path.
            Dim staging(4194303) As Byte   ' 4 MB staging buffer
            LogPhase("[ComputePi] snap_Phase3 found — loading P, Q, T (skipping Phase 1/2)...")
            Using fs As New FileStream(pPath, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(outP, br, staging)
                End Using
            End Using
            LogPhase($"[ComputePi] snap_Phase3 P loaded (~{CLng(gmp_lib.mpz_sizeinbase(outP, 10)):N0} digits)")
            Using fs As New FileStream(qPath, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(outQ, br, staging)
                End Using
            End Using
            LogPhase($"[ComputePi] snap_Phase3 Q loaded (~{CLng(gmp_lib.mpz_sizeinbase(outQ, 10)):N0} digits)")
            Using fs As New FileStream(tPath, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(outT, br, staging)
                End Using
            End Using
            LogPhase($"[ComputePi] snap_Phase3 T loaded (~{CLng(gmp_lib.mpz_sizeinbase(outT, 10)):N0} digits)")
            Return True
        Catch ex As Exception
            WriteToLog($"[Phase3Snap] Load FAILED: {ex.Message} — will run Phase 1/2")
            Return False
        End Try
    End Function

    ' §214 (2026-05-15, issue #67): T-only sibling of TryLoadPhase3Snapshot.
    ' When the caller has confirmed gmpNumer.bin will resume the run (skipping Steps 1-5),
    ' P and Q are dead weight — only T is needed downstream as the divisor in SafeMpzDiv.
    ' Skipping P + Q loads saves ~9.3 GB of working set at startup (P ~3.6 GB + Q ~5.6 GB
    ' at 5B scale).  outP and outQ are left mpz_init'd as 0; downstream code on the
    ' gmpNumer-resume path never touches them.  Falls back gracefully (returns False) if
    ' the snap dir is incomplete — caller then retries the full load.
    ''' <summary>
    ''' T-only sibling of <see cref="TryLoadPhase3Snapshot"/> (§214). When the caller has confirmed the
    ''' gmpNumer.bin resume path (which skips Steps 1–5), only T is needed downstream as the SafeMpzDiv
    ''' divisor, so P and Q are not loaded — saving ~9.3 GB of working set at 5 B startup.
    ''' </summary>
    ''' <param name="snapDir">Snapshot directory to load from.</param>
    ''' <param name="digits">Expected digit count; a mismatch is rejected.</param>
    ''' <param name="outT">Receives root T (must be mpz_init'd by the caller).</param>
    ''' <returns>True if T was loaded; False (graceful) if the snapshot is incomplete or mismatched.</returns>
    Private Function TryLoadPhase3SnapshotTOnly(snapDir As String, digits As Long,
                                                 outT As mpz_t) As Boolean
        Try
            If Not System.IO.Directory.Exists(snapDir) Then Return False
            Dim tPath As String = System.IO.Path.Combine(snapDir, "T.bin")
            Dim metaPath As String = System.IO.Path.Combine(snapDir, "meta.txt")
            If Not (System.IO.File.Exists(tPath) AndAlso System.IO.File.Exists(metaPath)) Then
                WriteToLog("[Phase3Snap§214] T.bin or meta.txt missing — falling back to full load")
                Return False
            End If
            Dim meta As New Dictionary(Of String, String)()
            For Each metaLine As String In System.IO.File.ReadAllLines(metaPath)
                Dim eq As Integer = metaLine.IndexOf("="c)
                If eq > 0 Then meta(metaLine.Substring(0, eq)) = metaLine.Substring(eq + 1)
            Next
            Dim snapDigits As Long = 0L
            If Not meta.ContainsKey("digits") OrElse
               Not Long.TryParse(meta("digits"), snapDigits) OrElse
               snapDigits <> digits Then
                WriteToLog($"[Phase3Snap§214] digits mismatch (want {digits:N0}, " &
                           $"have {If(meta.ContainsKey("digits"), meta("digits"), "?")}) — falling back to full load")
                Return False
            End If
            Dim staging(4194303) As Byte
            LogPhase("[ComputePi§214] gmpNumer.bin resume detected — loading T only (skipping P + Q, saves ~9 GB)")
            Using fs As New FileStream(tPath, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(outT, br, staging)
                End Using
            End Using
            LogPhase($"[ComputePi] snap_Phase3 T loaded (~{CLng(gmp_lib.mpz_sizeinbase(outT, 10)):N0} digits)")
            Return True
        Catch ex As Exception
            WriteToLog($"[Phase3Snap§214] T-only load FAILED: {ex.Message} — falling back to full load")
            Return False
        End Try
    End Function

    ' §94: Delete a snapshot directory (called after the next level's snapshot is confirmed).
    Private Sub DeleteSnapshotDir(level As Integer)
        Dim dir As String = System.IO.Path.Combine(DISK_CACHE_DIR, $"snap_L{level}")
        Try
            If System.IO.Directory.Exists(dir) Then
                System.IO.Directory.Delete(dir, recursive:=True)
                WriteToLog($"[Snapshot] Deleted old snapshot snap_L{level}")
            End If
        Catch ex As Exception
            WriteToLog($"[Snapshot] WARN: could not delete snap_L{level}: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Streams one mpz_t's raw GMP limb data into the BinaryWriter without any
    ''' intermediate allocation.  Reads _mp_size and _mp_d directly from the
    ''' native __mpz_struct, bypassing mpz_export entirely.
    ''' </summary>
    ''' <remarks>
    ''' Issue #12 fix: mpz_export has a 32-bit overflow for numbers with
    ''' |_mp_size| > 67,108,864 limbs.  GMP's MSVC build computes the bit count
    ''' as _mp_size * GMP_NUMB_BITS using unsigned long (32-bit on Windows), which
    ''' overflows for L16 N1's Q (_mp_size=68,132,407).  The workaround is to
    ''' bypass mpz_export and read the limb array (_mp_d) directly.
    '''
    ''' Disk format: Int32 _mp_size (signed — encodes both limb count and sign of
    ''' the number), followed by |_mp_size| * 8 bytes of raw limb data in the
    ''' platform-native byte order (little-endian on x64 Windows).
    ''' </remarks>
    Private Shared Sub SerializeOneMpz(val As mpz_t, bw As BinaryWriter, staging As Byte())
        ' Read _mp_size from the native __mpz_struct (Int32 at byte offset 4).
        ' Positive = positive number, negative = negative number.
        Dim mpSize As Integer = Marshal.ReadInt32(val.Pointer, 4)
        bw.Write(mpSize)
        If mpSize = 0 Then Return
        Dim limbCount As Long = CLng(System.Math.Abs(mpSize))
        Dim byteCount As Long = limbCount * 8L
        ' Read _mp_d (pointer to the limb array) at byte offset 8.
        Dim mpD As IntPtr = Marshal.ReadIntPtr(val.Pointer, 8)
        If _logLevel >= 2 AndAlso byteCount > 400L * 1024L * 1024L Then
            AppendLog(
                $"[SerializeOneMpz] large: _mp_size={mpSize:N0} byteCount={byteCount:N0}{vbCrLf}")
        End If
        ' Stream raw limb bytes in 4 MB chunks using the staging buffer.
        ' No intermediate allocation needed — data is read straight from _mp_d.
        ' §96: Use 64-bit pointer arithmetic (mpD.ToInt64() + offset) to avoid the
        ' IntPtr.Add(mpD, CInt(offset)) overflow that occurs when offset exceeds
        ' Int32.MaxValue (2 GB) for large fields at 5B+ digits.  RemoveIntegerChecks=True
        ' means CInt() wraps silently to a negative value, making IntPtr.Add point 2 GB
        ' before mpD — an invalid address — causing a fatal AccessViolationException.
        Dim remaining As Long = byteCount
        Dim offset As Long = 0L
        Dim mpDBase As Long = mpD.ToInt64()
        While remaining > 0
            Dim chunkSize As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
            Marshal.Copy(New IntPtr(mpDBase + offset), staging, 0, chunkSize)
            bw.Write(staging, 0, chunkSize)
            offset += chunkSize
            remaining -= chunkSize
        End While
    End Sub

    ' Issue #1 fix: replaced GCHandle.Alloc(Pinned) with Marshal.AllocHGlobal.
    '   • Pinned managed arrays prevent GC compaction of the heap segment they
    '     live in, creating permanent holes.  If an exception occurs between
    '     Alloc and Free the pin leaks for the life of the process.
    '   • Unmanaged memory allocated by Marshal.AllocHGlobal is invisible to
    '     the GC compactor, so it causes zero heap fragmentation.  It is freed
    '     in a Finally block so it cannot leak on exceptions.
    '
    ' Issue #2 fix (deserialization side): data is read from the BinaryReader
    ' into the same small 64 KB SOH staging buffer, then copied chunk-by-chunk
    ' into the unmanaged destination.  No LOH-sized managed byte array is ever
    ' created for the full number.
    '
    ' Issue #6 fix (partial): returns via ByRef parameters instead of a Tuple,
    ' eliminating one throw-away heap allocation per call.
    Private Sub LoadNodeFromDisk(filePath As String,
                                 fileOffset As Long,
                                 ByRef p As mpz_t,
                                 ByRef q As mpz_t,
                                 ByRef t As mpz_t,
                                 Optional detailLog As Boolean = True)
        If _logLevel >= 2 Then
            Dim fileSize As Long = If(System.IO.File.Exists(filePath), New FileInfo(filePath).Length, -1)
            WriteToLog($"[Deserialize] Loading {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
        End If

        p = New mpz_t()
        q = New mpz_t()
        t = New mpz_t()
        gmp_lib.mpz_inits(p, q, t, Nothing)

        Dim staging(4194303) As Byte  ' 4 MB staging buffer (§56)
        Try
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536)
                If fileOffset > 0L Then fs.Seek(fileOffset, SeekOrigin.Begin)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(p, br, staging)
                    DeserializeOneMpz(q, br, staging)
                    DeserializeOneMpz(t, br, staging)
                End Using
            End Using
        Catch ex As Exception
            gmp_lib.mpz_clears(p, q, t, Nothing)
            WriteExceptionToLog($"LoadNodeFromDisk({filePath})", ex)
            LogPhase($"Error loading node from {filePath}: {ex.Message}")
            Throw
        End Try
        If _logLevel >= 2 Then WriteToLog($"[Deserialize] Done {System.IO.Path.GetFileName(filePath)}")
    End Sub

    ''' <summary>
    ''' Reads one serialized mpz_t from the BinaryReader directly into GMP's limb
    ''' array, bypassing mpz_import entirely.
    ''' </summary>
    ''' <remarks>
    ''' Issue #12 fix: mpz_import has the same 32-bit overflow as mpz_export for
    ''' numbers with |_mp_size| > 67,108,864 limbs.  The workaround is to allocate
    ''' the limb buffer and write to the GMP struct directly.
    '''
    ''' For numbers with limbCount &lt; 67,108,864 (bit count fits in 32 bits), we
    ''' use mpz_realloc2 to let GMP manage the allocation normally.  For larger
    ''' numbers we call mpz_clear to free GMP's existing allocation, VirtualAlloc
    ''' our own limb buffer, and write _mp_alloc/_mp_size/_mp_d directly into the
    ''' native __mpz_struct.  When the number is later cleared via mpz_clear, GMP
    ''' calls GmpFreeFunc with (limbs, limbCount*8); since limbCount*8 &gt;
    ''' GMP_LARGE_THRESHOLD, GmpFreeFunc calls VirtualFree — matching the alloc.
    '''
    ''' Disk format: Int32 _mp_size (signed), followed by |_mp_size| * 8 bytes of
    ''' raw limb data in the platform-native byte order (little-endian on x64).
    ''' </remarks>
    Private Shared Sub DeserializeOneMpz(val As mpz_t, br As BinaryReader, staging As Byte())
        Dim mpSize As Integer = br.ReadInt32()
        If mpSize = 0 Then Return
        Dim limbCount As Long = CLng(System.Math.Abs(mpSize))
        Dim byteCount As Long = limbCount * 8L
        ' Numbers with limbCount < 67,108,864 have bit count < 2^32, so mpz_realloc2
        ' is safe (no 32-bit overflow in GMP's internal bit-count arithmetic).
        ' Numbers at or above this threshold use direct struct manipulation instead.
        Const REALLOC2_SAFE_LIMIT As Long = 67_108_864L
        If limbCount < REALLOC2_SAFE_LIMIT Then
            ' Let GMP manage the allocation via mpz_realloc2.
            ' limbCount < 67,108,864 here, so limbCount * 64 ≤ 4,294,967,232 — fits in UInt32.
            gmp_lib.mpz_realloc2(val, New mp_bitcnt_t(CUInt(limbCount * 64L)))
            Dim mpD As IntPtr = Marshal.ReadIntPtr(val.Pointer, 8)
            Dim remaining As Long = byteCount
            Dim offset As Long = 0L
            Dim mpDBase As Long = mpD.ToInt64()  ' §98: 64-bit arithmetic to avoid Int32 overflow at >2GB
            While remaining > 0
                Dim toRead As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
                Dim bytesRead As Integer = br.Read(staging, 0, toRead)
                If bytesRead <= 0 Then _
                    Throw New EndOfStreamException($"Unexpected end of stream in DeserializeOneMpz (small)")
                Marshal.Copy(staging, 0, New IntPtr(mpDBase + offset), bytesRead)
                offset += bytesRead
                remaining -= bytesRead
            End While
            Marshal.WriteInt32(val.Pointer, 4, mpSize)   ' set _mp_size (encodes sign)
        Else
            ' Large number: mpz_realloc2 would overflow GMP's 32-bit bit-count.
            ' VirtualAlloc our own limb buffer and write directly to the struct.
            ' We do NOT call mpz_clear first — doing so can leave the managed
            ' Math.Gmp.Native wrapper in an inconsistent state, causing a crash
            ' on the next native call.  The initial 8-byte CRT allocation from
            ' mpz_init is simply overwritten (trivial leak).
            ' byteCount >= 67M * 8 = ~536 MB >> GMP_LARGE_THRESHOLD, so when
            ' mpz_clear is later called (during computation cleanup), GmpFreeFunc
            ' will see size >= GMP_LARGE_THRESHOLD and call PoolReturn(ptr, byteCount).
            ' §79: Use PoolGet so the block is sized to 1L<<PoolBucket(byteCount) — the
            ' exact capacity the pool bucket assumes.  VirtualAlloc(byteCount) only gave
            ' byteCount bytes (page-rounded), which is ≤ 1L<<bucket, so any subsequent
            ' PoolGet from the same bucket would get an undersized block → buffer overrun.
            Dim limbs As IntPtr = PoolGet(CLng(byteCount))
            If limbs = IntPtr.Zero Then _
                Throw New OutOfMemoryException($"VirtualAlloc({byteCount:N0}) failed in DeserializeOneMpz")
            ' Write the struct immediately so val is in a consistent (zero-valued) state
            ' before we start reading.  If an exception escapes during the read, the
            ' caller can safely call mpz_clear and GmpFreeFunc will VirtualFree the buffer.
            Marshal.WriteInt32(val.Pointer, 0, CInt(limbCount))  ' _mp_alloc
            Marshal.WriteInt32(val.Pointer, 4, 0)                ' _mp_size = 0 (safe interim)
            Marshal.WriteIntPtr(val.Pointer, 8, limbs)            ' _mp_d
            Dim remaining As Long = byteCount
            Dim offset As Long = 0L
            Dim limbsBase As Long = limbs.ToInt64()  ' §98: 64-bit arithmetic to avoid Int32 overflow at >2GB
            While remaining > 0
                Dim toRead As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
                Dim bytesRead As Integer = br.Read(staging, 0, toRead)
                If bytesRead <= 0 Then _
                    Throw New EndOfStreamException($"Unexpected end of stream in DeserializeOneMpz (large)")
                Marshal.Copy(staging, 0, New IntPtr(limbsBase + offset), bytesRead)
                offset += bytesRead
                remaining -= bytesRead
            End While
            Marshal.WriteInt32(val.Pointer, 4, mpSize)   ' set _mp_size now data is valid
        End If
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Safe large-integer multiply (avoids GMP 32-bit mp_size_t overflow)
    ' ════════════════════════════════════════════════════════════════════════

    ' §250 (issue #94): High-half ("short") product for the capped-precision reciprocal
    ' Newton iterations.  Computes opA*opB but returns an OVERESTIMATE of the true product
    ' whose top limbs (down to ~cutoffLo) are exact.  The low-column 3×3 sub-products lying
    ' entirely below the exact-region cutoff are skipped; an upper bound of their omitted
    ' mass is then ADDED (round-up) so result ≥ true product.  Used where the caller
    ' immediately right-shifts and forms r = 2r − (result>>S):
    '     result ≥ true  ⟹  (result>>S) ≥ floor(true>>S)  ⟹  r stays a strict UNDERESTIMATE
    ' (the §107 invariant), with a bounded few-ulp low error that SafeMpzDiv's §171/§218
    ' quotient adjustment corrects to the exact quotient (so final π is bit-identical).
    ' Reuses the proven SafeMpzMul per surviving sub-product; accumulates via BigShiftLeft +
    ' GmpRaw_add into a pre-grown result (no GMP realloc abort).  The OVERESTIMATE contract
    ' (result ≥ full, and (result>>S) within ~1 ulp of (full>>S)) is checked by --test-mulhigh.
    ' Requires result to be a DISTINCT mpz_t from opA/opB (true at the reciprocal call sites).
    ''' <summary>
    ''' High-half ("short") product for the capped-precision reciprocal-Newton iterations (§250, #94).
    ''' Returns an OVERESTIMATE of opA·opB whose top limbs (down to the kept region) are exact: low
    ''' 3×3 sub-products entirely below the cutoff are skipped and an upper bound of their omitted mass
    ''' is added (round-up). Contract: result ≥ true product, so after the caller's right-shift the
    ''' reciprocal r = 2r − (result≫S) stays a strict UNDERESTIMATE (the §107 invariant); the bounded
    ''' few-ulp low error is corrected to exactness by SafeMpzDiv's §171/§218 quotient adjustment.
    ''' </summary>
    ''' <param name="result">Receives the high product; must be a DISTINCT mpz_t from opA/opB.</param>
    ''' <param name="opA">First operand.</param>
    ''' <param name="opB">Second operand.</param>
    ''' <param name="keepLimbs">Number of high-order product limbs required to be exact; cells entirely below that region are dropped.</param>
    Private Shared Sub SafeMpzMulHigh(result As mpz_t, opA As mpz_t, opB As mpz_t, keepLimbs As Long)
        Const SAFE_LIMB_THRESHOLD As Long = 5_000_000L
        Const GUARD_LIMBS As Long = 8L
        Dim szA As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4))
        Dim szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4))
        Dim fullLimbs As Long = CLng(szA) + CLng(szB)
        ' Fall back to the exact full product when truncation can't help or operands are small.
        If keepLimbs <= 0L OrElse keepLimbs + GUARD_LIMBS >= fullLimbs OrElse fullLimbs <= SAFE_LIMB_THRESHOLD Then
            SafeMpzMul(result, opA, opB)
            Return
        End If
        Dim cutoffLo As Long = fullLimbs - keepLimbs - GUARD_LIMBS   ' result limbs < cutoffLo may be omitted
        Dim mA As Long = (CLng(szA) + 2L) \ 3L
        Dim mB As Long = (CLng(szB) + 2L) \ 3L
        Dim opA_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
        Dim opB_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)

        ' Build 3 zero-copy limb-window pieces per operand (headers only; data aliases opA/opB).
        Dim Ahdr(2) As IntPtr, Bhdr(2) As IntPtr
        Dim Asz(2) As Integer, Bsz(2) As Integer
        For idx As Integer = 0 To 2
            Dim aBase As Long = opA_d + CLng(idx) * mA * 8L
            Dim aCnt As Integer = CInt(System.Math.Max(0L, System.Math.Min(CLng(szA) - CLng(idx) * mA, mA)))
            Dim aT As Integer = aCnt
            While aT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(aBase + CLng(aT - 1) * 8L)) = 0L
                aT -= 1
            End While
            Ahdr(idx) = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            Runtime.InteropServices.Marshal.WriteInt32(Ahdr(idx), 0, CInt(mA))
            Runtime.InteropServices.Marshal.WriteInt32(Ahdr(idx), 4, aT)
            Runtime.InteropServices.Marshal.WriteInt64(Ahdr(idx), 8, aBase)
            Asz(idx) = aT
            Dim bBase As Long = opB_d + CLng(idx) * mB * 8L
            Dim bCnt As Integer = CInt(System.Math.Max(0L, System.Math.Min(CLng(szB) - CLng(idx) * mB, mB)))
            Dim bT As Integer = bCnt
            While bT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(bBase + CLng(bT - 1) * 8L)) = 0L
                bT -= 1
            End While
            Bhdr(idx) = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            Runtime.InteropServices.Marshal.WriteInt32(Bhdr(idx), 0, CInt(mB))
            Runtime.InteropServices.Marshal.WriteInt32(Bhdr(idx), 4, bT)
            Runtime.InteropServices.Marshal.WriteInt64(Bhdr(idx), 8, bBase)
            Bsz(idx) = bT
        Next idx

        ' result := 0, pre-grown to full width so accumulation adds never trigger a GMP realloc.
        Runtime.InteropServices.Marshal.WriteInt32(result.Pointer, 4, 0)   ' _mp_size = 0  ⟹ value 0
        PreAllocMpzToLimbs(result, fullLimbs + 2L)

        Dim tmp As New mpz_t()
        tmp.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        GmpRaw_init(tmp.Pointer)
        Dim anySkipped As Boolean = False
        For i As Integer = 0 To 2
            For j As Integer = 0 To 2
                If Asz(i) = 0 OrElse Bsz(j) = 0 Then Continue For
                Dim colLimb As Long = CLng(i) * mA + CLng(j) * mB
                Dim termTop As Long = colLimb + CLng(Asz(i)) + CLng(Bsz(j))
                If termTop < cutoffLo Then
                    anySkipped = True
                    Continue For
                End If
                Dim Ai As New mpz_t() : Ai.Pointer = Ahdr(i)
                Dim Bj As New mpz_t() : Bj.Pointer = Bhdr(j)
                SafeMpzMul(tmp, Ai, Bj)
                If colLimb > 0L Then BigShiftLeft(tmp, tmp, colLimb * 64L)
                GmpRaw_add(result.Pointer, result.Pointer, tmp.Pointer)
            Next j
        Next i

        ' Round UP by an upper bound of the omitted mass.  Omitted < 9·2^(64·cutoffLo)
        ' < 2^(64·cutoffLo+4); adding 2^(64·cutoffLo+4) guarantees result ≥ true product.
        If anySkipped Then
            GmpRaw_set_ui(tmp.Pointer, 1UI)
            BigShiftLeft(tmp, tmp, cutoffLo * 64L + 4L)
            GmpRaw_add(result.Pointer, result.Pointer, tmp.Pointer)
        End If

        GmpRaw_clear(tmp.Pointer)
        Runtime.InteropServices.Marshal.FreeHGlobal(tmp.Pointer)
        For idx As Integer = 0 To 2
            Runtime.InteropServices.Marshal.FreeHGlobal(Ahdr(idx))
            Runtime.InteropServices.Marshal.FreeHGlobal(Bhdr(idx))
        Next idx
    End Sub

    ' §250 (#94): reciprocal capped-iter multiply dispatcher.  Computes dst = A·B for the two
    ' Newton multiplies (rSq = r², p = bTrunc·rSq) using the high-half product when
    ' PI_RECIP_SHORTMUL is on.  In VERIFY mode it also computes the full product, checks the
    ' OVERESTIMATE contract (dst ≥ full, and the guaranteed-exact region matches), FALLS BACK
    ' to full on any mismatch, and logs the outcome per iter — so a mis-tuned keepLimbs can
    ' never corrupt π during the gate run.  keepLimbs is chosen by the caller with margin.
    Private Shared Sub RecipMul(dst As mpz_t, A As mpz_t, B As mpz_t, keepLimbs As Long, tag As String, iter As Integer)
        ' §251 (#70): use the chunked-grid high product (fine cells skip ~½–⅔ + small FFT-safe
        ' mults) — 2.55× (rSq) / 6.42× (p) vs §gen at DOP=1, vs §250 SafeMpzMulHigh's 1.13×.
        If Not _recipShortMulVerify Then
            SafeMpzMul_ChunkedGrid(dst, A, B, keepLimbs)
            If _logLevel >= 2 Then AppendLog($"[SafeMpzReciprocal§251] {tag} chunked-grid high keep={keepLimbs:N0} iter={iter}{vbCrLf}")
            Return
        End If
        ' VERIFY: full into scratch, high into dst, compare guaranteed-exact region.
        Dim szA As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(A.Pointer, 4))
        Dim szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(B.Pointer, 4))
        Dim fullLimbs As Long = CLng(szA) + CLng(szB)
        Dim full As New mpz_t() : full.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(full.Pointer)
        Dim _tg As Long = System.Diagnostics.Stopwatch.GetTimestamp()
        SafeMpzMul(full, A, B)
        Dim _msGen As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _tg) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
        Dim _tc As Long = System.Diagnostics.Stopwatch.GetTimestamp()
        SafeMpzMul_ChunkedGrid(dst, A, B, keepLimbs)
        Dim _msCg As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _tc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
        AppendLog($"[SafeMpzReciprocal§251 TIMING] {tag} iter={iter} §gen={_msGen:F0}ms chunked={_msCg:F0}ms speedup={If(_msCg > 0.0, _msGen / _msCg, 0.0):F2}x{vbCrLf}", 4)   ' §252 (#95): per-iter timing diagnostic → level 4
        Dim cmp As Integer = GmpRaw_cmp(dst.Pointer, full.Pointer)             ' want ≥ 0 (overestimate)
        Dim cs As Long = System.Math.Max(0L, (fullLimbs - keepLimbs) * 64L)    ' guaranteed-exact region start
        Dim fs As New mpz_t() : fs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(fs.Pointer)
        Dim hs As New mpz_t() : hs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(hs.Pointer)
        BigShiftRight(fs, full, cs)
        BigShiftRight(hs, dst, cs)
        Dim regionEq As Integer = GmpRaw_cmp(hs.Pointer, fs.Pointer)
        Dim ok As Boolean = (cmp >= 0) AndAlso (regionEq = 0)
        If Not ok Then
            GmpRaw_set(dst.Pointer, full.Pointer)   ' FALLBACK to the exact product
            AppendLog($"[SafeMpzReciprocal§250 VERIFY] {tag} iter={iter} MISMATCH (high>=full={cmp >= 0} regionCmp={regionEq}) keep={keepLimbs:N0} — FELL BACK to full product{vbCrLf}")
        Else
            AppendLog($"[SafeMpzReciprocal§250 VERIFY] {tag} iter={iter} OK (overestimate + exact region) keep={keepLimbs:N0}{vbCrLf}", 4)   ' §252 (#95): per-iter VERIFY-OK confirmation → level 4 (MISMATCH stays at 2)
        End If
        GmpRaw_clear(fs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(fs.Pointer)
        GmpRaw_clear(hs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(hs.Pointer)
        GmpRaw_clear(full.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(full.Pointer)
    End Sub

    ' §251 (issue #70): chunked-grid multiply — generalized from the §5B-f1 verification grid.
    ' Computes result = opA·opB as a grid of ≤CHUNK×CHUNK cells (each a GMP-FFT-safe small mul),
    ' accumulated into a zeroed pool buffer via mpn_add at the cell's LIMB offset (no whole-buffer
    ' shift — O(cell) not O(N) per cell).  Two wins from one path:
    '   • keepLimbs = 0  → exact full product; bounded peak RAM (one accumulator + ≤3M-limb cell
    '     temp), the depth-0 RAM cap this issue targets for a×r / q×b / sqrt.
    '   • keepLimbs > 0  → high product: skip cells entirely below the exact-region cutoff and add
    '     an upper bound of the omitted mass (round-up) so result ≥ true product (the §94 reciprocal
    '     short-mul, now parallel-ready and memory-light).  result must be DISTINCT from opA/opB.
    ' NOTE (pt1): cells run SERIALLY here; parallelisation of the cell mults is the pt2 perf step.
    ''' <summary>
    ''' Chunked-grid multiply (§251, #70) — the production path for the dominant 5 B multiplies
    ''' (reciprocal RecipMul §254, divide a×r §262, q×b §269). Computes opA·opB as a grid of
    ''' ≤CHUNK×CHUNK GMP-FFT-safe cells, accumulated by mpn_add at each cell's limb offset (O(cell)
    ''' per cell, no whole-buffer shift). Cell size is adaptive (§267, see PI_CG_ADAPTIVE / PI_CG_CELL_MAX)
    ''' and cells run in parallel (PI_CG_DOP). result must be DISTINCT from opA/opB.
    ''' </summary>
    ''' <param name="result">Receives the product.</param>
    ''' <param name="opA">First operand.</param>
    ''' <param name="opB">Second operand.</param>
    ''' <param name="keepLimbs">0 = exact full product; &gt; 0 = HIGH product keeping that many top limbs exact (cells fully below the cutoff are skipped and an upper bound of the omitted mass added, so result ≥ true — the §94/§107 overestimate contract).</param>
    Private Shared Sub SafeMpzMul_ChunkedGrid(result As mpz_t, opA As mpz_t, opB As mpz_t, keepLimbs As Long)
        Const GUARD_LIMBS As Long = 8L
        Dim szA As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4))
        Dim szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4))
        ' §267 (#88): CELL SIZE.  Production has always used a fixed 1.5M cell.  But per-cell overhead
        ' (parallel-wave sync + serial accumulate) scales with cell COUNT, so a tiny cell is
        ' catastrophic at 5B operand sizes — measured 260M² = 32.4 min at 1.5M (30,276 cells) vs
        ' 3.8 min at 16M (289 cells), bit-identical (§266 --test-cellsweep).  ADAPTIVE mode
        ' (§267 §268: ENABLED BY DEFAULT 2026-06-05; opt out with PI_CG_ADAPTIVE=0) sizes the cell ≈
        ' maxSz/3 (the measured sweet spot) capped at the FFT-safe maximum (PI_CG_CELL_MAX, default
        ' 16M: a cell product 2·16M = 32M < the 33,554,431-limb GMP-FFT size cap), floored at 1.5M.
        ' §160's lower "FFT accuracy" cap is a misdiagnosis (GMP uses INTEGER Schönhage–Strassen, not
        ' float; the wrong products it chased were the §200/§201 Newton bug, lifted by §220).
        ' VALIDATED bit-identical at 1B (~38% faster Phase 3) AND 5B (SHA 2218ee06…, divide ~30-40%
        ' faster, RAM peak ~40 GB).  _cgCellOverride>0 (the --test-gridscan/cellsweep benchmarks) wins;
        ' PI_CG_ADAPTIVE=0 restores the old fixed 1.5M cell.
        Dim CHUNK As Integer
        If _cgCellOverride > 0 Then
            CHUNK = _cgCellOverride
        ElseIf Environment.GetEnvironmentVariable("PI_CG_ADAPTIVE") <> "0" Then
            Dim cellMax As Long = 16_000_000L
            Dim cmEnv As String = Environment.GetEnvironmentVariable("PI_CG_CELL_MAX")
            Dim cmParsed As Integer
            If cmEnv IsNot Nothing AndAlso Integer.TryParse(cmEnv, cmParsed) AndAlso cmParsed >= 1_500_000 AndAlso cmParsed <= 16_700_000 Then cellMax = CLng(cmParsed)
            Dim adaptive As Long = (CLng(System.Math.Max(szA, szB)) + 2L) \ 3L
            CHUNK = CInt(System.Math.Max(1_500_000L, System.Math.Min(cellMax, adaptive)))
            If CHUNK <> 1_500_000 Then AppendLog($"[ChunkedGrid§267] adaptive cell={CHUNK:N0} (szA={szA:N0} szB={szB:N0} ⇒ {(CLng(szA) + CHUNK - 1L) \ CHUNK}×{(CLng(szB) + CHUNK - 1L) \ CHUNK} cells; keep={keepLimbs:N0}){vbCrLf}", 2)
        Else
            CHUNK = 1_500_000
        End If
        Dim aD As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
        Dim bD As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)
        Dim fullLimbs As Long = CLng(szA) + CLng(szB)
        Dim maxLimbs As Long = fullLimbs + 2L
        Dim cutoffLo As Long = -1L
        If keepLimbs > 0L AndAlso keepLimbs + GUARD_LIMBS < fullLimbs Then cutoffLo = fullLimbs - keepLimbs - GUARD_LIMBS

        ' Zeroed, pool-managed accumulator (becomes result's buffer at the end).
        Dim accBytes As Long = maxLimbs * 8L
        Dim accBuf As IntPtr = GmpNativeAlloc_PoolGet(accBytes)
        If accBuf = IntPtr.Zero Then Throw New OutOfMemoryException($"SafeMpzMul_ChunkedGrid: PoolGet {accBytes \ 1048576L} MB failed")
        ZeroMemory(accBuf, New UIntPtr(CULng(accBytes)))

        Dim numA As Integer = (szA + CHUNK - 1) \ CHUNK
        Dim numB As Integer = (szB + CHUNK - 1) \ CHUNK
        Dim anySkipped As Boolean = False
        ' §251 pt2: build the kept-cell list (trailing-zero-trimmed; high-mode skips cells below
        ' the cutoff), then multiply cells in PARALLEL waves with a serial carry-safe accumulate.
        Dim cAOff As New System.Collections.Generic.List(Of Long)()
        Dim cASz As New System.Collections.Generic.List(Of Integer)()
        Dim cBOff As New System.Collections.Generic.List(Of Long)()
        Dim cBSz As New System.Collections.Generic.List(Of Integer)()
        Dim cOff As New System.Collections.Generic.List(Of Long)()
        For i As Integer = 0 To numA - 1
            Dim aOff As Long = CLng(i) * CLng(CHUNK)
            Dim aSzCk As Integer = CInt(System.Math.Min(CLng(CHUNK), CLng(szA) - aOff))
            If aSzCk <= 0 Then Continue For
            While aSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(aD + (aOff + CLng(aSzCk - 1)) * 8L)) = 0L
                aSzCk -= 1
            End While
            If aSzCk <= 0 Then Continue For
            For j As Integer = 0 To numB - 1
                Dim bOff As Long = CLng(j) * CLng(CHUNK)
                Dim bSzCk As Integer = CInt(System.Math.Min(CLng(CHUNK), CLng(szB) - bOff))
                If bSzCk <= 0 Then Continue For
                While bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(bD + (bOff + CLng(bSzCk - 1)) * 8L)) = 0L
                    bSzCk -= 1
                End While
                If bSzCk <= 0 Then Continue For
                Dim offset As Long = aOff + bOff
                If cutoffLo >= 0L AndAlso (offset + CLng(aSzCk) + CLng(bSzCk)) < cutoffLo Then
                    anySkipped = True
                    Continue For
                End If
                cAOff.Add(aOff) : cASz.Add(aSzCk) : cBOff.Add(bOff) : cBSz.Add(bSzCk) : cOff.Add(offset)
            Next j
        Next i

        ' §251 pt2: cells are tiny (≤3M-limb products ≈ 24MB), so high DOP fits in RAM even when
        ' §gen's GB-scale sub-products force MemoryBudget to floor §gen's DOP — this is why parallel
        ' chunked beats §gen at 5B.  DOP from env PI_CG_DOP, default ProcessorCount (capped 16).
        Dim _cgDop As Integer = Environment.ProcessorCount
        Dim _cgEnv As String = Environment.GetEnvironmentVariable("PI_CG_DOP")
        Dim _cgParsed As Integer
        If _cgEnv IsNot Nothing AndAlso Integer.TryParse(_cgEnv, _cgParsed) AndAlso _cgParsed >= 1 Then _cgDop = _cgParsed
        _cgDop = System.Math.Max(1, System.Math.Min(_cgDop, 16))
        Dim nCells As Integer = cOff.Count
        Dim prods(_cgDop - 1) As mpz_t
        Dim ckAh(_cgDop - 1) As IntPtr, ckBh(_cgDop - 1) As IntPtr
        For w As Integer = 0 To _cgDop - 1
            prods(w) = New mpz_t() : prods(w).Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(prods(w).Pointer)
            ckAh(w) = Runtime.InteropServices.Marshal.AllocHGlobal(16) : ckBh(w) = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Next
        Dim _cgOpts As New System.Threading.Tasks.ParallelOptions() With {.MaxDegreeOfParallelism = _cgDop}
        Dim waveStart As Integer = 0
        While waveStart < nCells
            Dim waveN As Integer = System.Math.Min(_cgDop, nCells - waveStart)
            ' Parallel: each slot multiplies one cell (independent; concurrent GmpRaw_mul on
            ' distinct prods(w) is safe — same pattern as §gen's parallel sub-products).
            System.Threading.Tasks.Parallel.For(0, waveN, _cgOpts,
                Sub(w As Integer)
                    Dim c As Integer = waveStart + w
                    Runtime.InteropServices.Marshal.WriteInt32(ckAh(w), 0, CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(ckAh(w), 4, cASz(c))
                    Runtime.InteropServices.Marshal.WriteInt64(ckAh(w), 8, aD + cAOff(c) * 8L)
                    Runtime.InteropServices.Marshal.WriteInt32(ckBh(w), 0, CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(ckBh(w), 4, cBSz(c))
                    Runtime.InteropServices.Marshal.WriteInt64(ckBh(w), 8, bD + cBOff(c) * 8L)
                    GmpRaw_mul(prods(w).Pointer, ckAh(w), ckBh(w))
                End Sub)
            ' Serial carry-safe accumulate of this wave into accBuf at each cell's limb offset.
            For w As Integer = 0 To waveN - 1
                Dim c As Integer = waveStart + w
                Dim pSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(w).Pointer, 4))
                If pSz > 0 Then
                    Dim pD As Long = Runtime.InteropServices.Marshal.ReadInt64(prods(w).Pointer, 8)
                    Dim offset As Long = cOff(c)
                    GmpRaw_mpn_add(New IntPtr(accBuf.ToInt64() + offset * 8L), New IntPtr(accBuf.ToInt64() + offset * 8L),
                                   CInt(maxLimbs - offset), New IntPtr(pD), pSz)
                End If
            Next w
            waveStart += waveN
        End While
        For w As Integer = 0 To _cgDop - 1
            GmpRaw_clear(prods(w).Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(prods(w).Pointer)
            Runtime.InteropServices.Marshal.FreeHGlobal(ckAh(w)) : Runtime.InteropServices.Marshal.FreeHGlobal(ckBh(w))
        Next w
        ' Round UP by an upper bound of the omitted mass (< 9·2^(64·cutoffLo) < 16·2^(64·cutoffLo))
        ' ⟹ result ≥ true product, so the caller's r = 2r − (result>>S) stays an underestimate.
        If anySkipped AndAlso cutoffLo >= 0L Then
            Dim sxPtr As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(8)
            Runtime.InteropServices.Marshal.WriteInt64(sxPtr, 0, 16L)   ' 16·2^(64·cutoffLo) = 2^(64·cutoffLo+4)
            GmpRaw_mpn_add(New IntPtr(accBuf.ToInt64() + cutoffLo * 8L), New IntPtr(accBuf.ToInt64() + cutoffLo * 8L),
                           CInt(maxLimbs - cutoffLo), sxPtr, 1)
            Runtime.InteropServices.Marshal.FreeHGlobal(sxPtr)
        End If
        ' Normalize size (highest nonzero limb) and swap accBuf into result.
        Dim sz As Integer = CInt(maxLimbs)
        While sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(accBuf.ToInt64() + CLng(sz - 1) * 8L)) = 0L
            sz -= 1
        End While
        Dim oldAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 0))
        Dim oldPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(result.Pointer, 8))
        GmpNativeAlloc_FreeRaw(oldPtr, oldAlloc * 8L)
        Runtime.InteropServices.Marshal.WriteInt32(result.Pointer, 0, CInt(maxLimbs))
        Runtime.InteropServices.Marshal.WriteInt32(result.Pointer, 4, sz)
        Runtime.InteropServices.Marshal.WriteInt64(result.Pointer, 8, accBuf.ToInt64())
    End Sub

    ' §273/§274 (#121): sign-aware chunked-grid FULL multiply — a drop-in replacement for
    ' SafeMpzMul on large operands that breaks the §231/§233 DOP cap (chunked-grid parallelises
    ' cells at PI_CG_DOP, not _safeMulDop).  SafeMpzMul_ChunkedGrid itself computes only the
    ' unsigned magnitude product — it reads Abs(_mp_size) and writes a positive result, because the
    ' divide (§262/§269) only ever fed it non-negative operands.  The Phase-2 combine's T merges
    ' (§273: tempA=leftT·rightQ, tempB=leftP·rightT) can be signed (the Chudnovsky series
    ' alternates), so this applies the product sign exactly as SafeMpzMul does (negative iff exactly
    ' one operand is negative).  With that, SafeMpzMulCG is bit-identical to SafeMpzMul on every
    ' operand sign.  Used by §273 (combine merges) and §274 (numerator r0/r1/r2 = gmpNumer·Q_i).
    Private Shared Sub SafeMpzMulCG(result As mpz_t, opA As mpz_t, opB As mpz_t)
        Dim sA As Integer = Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4)   ' signed _mp_size
        Dim sB As Integer = Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4)
        SafeMpzMul_ChunkedGrid(result, opA, opB, 0L)                                    ' |opA|·|opB|, positive
        If (sA < 0) <> (sB < 0) Then
            Dim rs As Integer = Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4)
            If rs <> 0 Then Runtime.InteropServices.Marshal.WriteInt32(result.Pointer, 4, -rs)
        End If
    End Sub

    ' §250: fill m with nLimbs pseudo-random limbs (top limb forced nonzero), value positive.
    ' m must be a fresh mpz_t header (AllocHGlobal(16)).  Allocate by LIMBS via PreAllocMpzToLimbs
    ' (Long byte math) — NOT GmpRaw_init2(bits), whose mp_bitcnt_t is 32-bit on Windows and
    ' overflows past ~67M limbs (nLimbs·64 > 2^32), allocating a tiny buffer → heap smash.
    Private Shared Sub FillRandomMpz(m As mpz_t, nLimbs As Integer, rng As Random)
        GmpRaw_init(m.Pointer)                                    ' 1-limb buffer
        PreAllocMpzToLimbs(m, CLng(nLimbs) + 2L)                  ' grow by limbs (no 32-bit bit overflow)
        Dim d As Long = Runtime.InteropServices.Marshal.ReadInt64(m.Pointer, 8)
        Dim buf(7) As Byte
        For k As Integer = 0 To nLimbs - 1
            rng.NextBytes(buf)
            Runtime.InteropServices.Marshal.WriteInt64(New IntPtr(d + CLng(k) * 8L), BitConverter.ToInt64(buf, 0))
        Next k
        If Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(d + CLng(nLimbs - 1) * 8L)) = 0L Then
            Runtime.InteropServices.Marshal.WriteInt64(New IntPtr(d + CLng(nLimbs - 1) * 8L), &H4000000000000000L)
        End If
        Runtime.InteropServices.Marshal.WriteInt32(m.Pointer, 4, nLimbs)   ' _mp_size = nLimbs (positive)
    End Sub

    ' §250 (issue #94): standalone bit-correctness self-test for SafeMpzMulHigh.  For each
    ' (szA, szB, keepLimbs) case it builds random operands, computes the full product and the
    ' high product, and verifies the OVERESTIMATE contract:
    '   (1) high ≥ full, and
    '   (2) (high >> S) − (full >> S) ∈ {0,1} where S = (szA+szB−keep)·64  (≤ ~1 ulp at the cut).
    ' Writes results to the temp file mulhigh_test.txt and returns True iff every case passes.
    Private Shared Function TestMulHigh() As Boolean
        Dim rng As New Random(12345)
        Dim allPass As Boolean = True
        ' Mirror production: serial sub-products (§238 forces inner serial in real runs).  Without
        ' this the default DOP=0 → SafeMpzMul spawns 9 parallel sub-products → memory blow-up.
        System.Threading.Volatile.Write(_safeMulDop, 1)
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mulhigh_test.txt")
        Try : System.IO.File.WriteAllText(outPath, $"[TestMulHigh] start {DateTime.Now}{vbCrLf}") : Catch : End Try
        ' sq=True ⟹ exercise the squaring path (opA.Pointer = opB.Pointer), as rSq = r² does.
        ' Last two cases mirror the real 500M capped-iter operand sizes (rSq: 26M², keep 26M;
        ' p: bTrunc 68M × rSq 52M, keep 26M) so the timing ratio = the in-reciprocal speedup.
        Dim caseA() As Integer = {8_000_000, 8_000_000, 10_000_000, 7_000_000, 8_000_000, 12_000_000, 26_000_000, 68_000_000}
        Dim caseB() As Integer = {6_000_000, 8_000_000, 5_000_000, 7_000_000, 8_000_000, 12_000_000, 26_000_000, 52_000_000}
        Dim caseK() As Long = {5_000_000L, 6_000_000L, 4_000_000L, 3_000_000L, 5_000_000L, 7_000_000L, 26_004_096L, 26_004_096L}
        Dim caseSq() As Boolean = {False, False, False, False, True, True, True, False}
        For ci As Integer = 0 To caseA.Length - 1
            Dim szA As Integer = caseA(ci), szB As Integer = caseB(ci)
            Dim keep As Long = caseK(ci)
            Dim sq As Boolean = caseSq(ci)
            Dim a As New mpz_t() : a.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            Dim b As New mpz_t()
            FillRandomMpz(a, szA, rng)
            If sq Then
                b.Pointer = a.Pointer   ' squaring: same operand buffer (rSq = r²)
            Else
                b.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                FillRandomMpz(b, szB, rng)
            End If
            Dim full As New mpz_t() : full.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(full.Pointer)
            Dim high As New mpz_t() : high.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(high.Pointer)
            Dim _swF As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            SafeMpzMul(full, a, b)
            Dim _msF As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _swF) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            Dim _swH As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            SafeMpzMulHigh(high, a, b, keep)
            Dim _msH As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _swH) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            Dim cmp As Integer = GmpRaw_cmp(high.Pointer, full.Pointer)   ' want ≥ 0
            Dim fullLimbs As Long = CLng(szA) + CLng(szB)
            Dim S As Long = (fullLimbs - keep) * 64L
            Dim fs As New mpz_t() : fs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(fs.Pointer)
            Dim hs As New mpz_t() : hs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(hs.Pointer)
            BigShiftRight(fs, full, S)
            BigShiftRight(hs, high, S)
            Dim diff As New mpz_t() : diff.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(diff.Pointer)
            GmpRaw_sub(diff.Pointer, hs.Pointer, fs.Pointer)
            Dim diffSz As Integer = Runtime.InteropServices.Marshal.ReadInt32(diff.Pointer, 4)   ' signed
            Dim diffLow As Long = 0L
            If diffSz <> 0 Then
                Dim dD As Long = Runtime.InteropServices.Marshal.ReadInt64(diff.Pointer, 8)
                diffLow = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(dD), 0)
            End If
            Dim pass As Boolean = (cmp >= 0) AndAlso (diffSz >= 0) AndAlso (diffSz <= 1)
            Dim _spd As Double = If(_msH > 0.0, _msF / _msH, 0.0)
            Dim line As String = $"[TestMulHigh] szA={szA:N0} szB={szB:N0} keep={keep:N0} sq={sq}: high>=full={cmp >= 0} shiftDiffSz={diffSz} diffLow={diffLow} | full={_msF:F0}ms high={_msH:F0}ms speedup={_spd:F2}x -> {If(pass, "PASS", "FAIL")}{vbCrLf}"
            Try : System.IO.File.AppendAllText(outPath, line) : Catch : End Try
            AppendLog(line)
            If Not pass Then allPass = False
            GmpRaw_clear(a.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(a.Pointer)
            If Not sq Then
                GmpRaw_clear(b.Pointer)
                Runtime.InteropServices.Marshal.FreeHGlobal(b.Pointer)
            End If
            GmpRaw_clear(full.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(full.Pointer)
            GmpRaw_clear(high.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(high.Pointer)
            GmpRaw_clear(fs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(fs.Pointer)
            GmpRaw_clear(hs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(hs.Pointer)
            GmpRaw_clear(diff.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(diff.Pointer)
        Next ci
        Try : System.IO.File.AppendAllText(outPath, $"[TestMulHigh] OVERALL {If(allPass, "PASS", "FAIL")}{vbCrLf}") : Catch : End Try
        Return allPass
    End Function

    ' §251 (#70): standalone self-test for SafeMpzMul_ChunkedGrid.  For each case it checks:
    '   (1) full mode (keep=0) is BIT-IDENTICAL to SafeMpzMul (the §gen oracle);
    '   (2) high mode (keep>0) is a strict OVERESTIMATE whose guaranteed-exact region matches;
    ' and times full(§gen) vs chunked-full vs chunked-high so we can see the real speedup.
    Private Shared Function TestChunkedGrid() As Boolean
        Dim rng As New Random(2468)
        Dim allPass As Boolean = True
        ' §251 pt2: compare against §gen at DOP=9 (what 5B actually floors §gen to) for a FAIR
        ' fight — the chunked grid parallelizes its cells independently via _cgDop (ProcessorCount).
        System.Threading.Volatile.Write(_safeMulDop, 9)
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chunkedgrid_test.txt")
        Try : System.IO.File.WriteAllText(outPath, $"[TestChunkedGrid] start {DateTime.Now}{vbCrLf}") : Catch : End Try
        ' §70 isolation: PI_CG_ISOLATE=1 runs ONLY the 68M×52M case (the one that crashed back-to-back).
        Dim caseA() As Integer, caseB() As Integer, caseSq() As Boolean
        Dim caseK() As Long
        If Environment.GetEnvironmentVariable("PI_CG_ISOLATE") = "1" Then
            caseA = New Integer() {68_000_000} : caseB = New Integer() {52_000_000}
            caseK = New Long() {26_004_096L} : caseSq = New Boolean() {False}
        Else
            caseA = New Integer() {8_000_000, 10_000_000, 8_000_000, 26_000_000, 68_000_000}
            caseB = New Integer() {6_000_000, 5_000_000, 8_000_000, 26_000_000, 52_000_000}
            caseK = New Long() {5_000_000L, 4_000_000L, 5_000_000L, 26_004_096L, 26_004_096L}
            caseSq = New Boolean() {False, False, True, True, False}
        End If
        For ci As Integer = 0 To caseA.Length - 1
            Dim szA As Integer = caseA(ci), szB As Integer = caseB(ci)
            Dim keep As Long = caseK(ci)
            Dim sq As Boolean = caseSq(ci)
            Dim a As New mpz_t() : a.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            Dim b As New mpz_t()
            FillRandomMpz(a, szA, rng)
            If sq Then
                b.Pointer = a.Pointer
            Else
                b.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                FillRandomMpz(b, szB, rng)
            End If
            Dim full As New mpz_t() : full.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(full.Pointer)
            Dim cgF As New mpz_t() : cgF.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(cgF.Pointer)
            Dim cgH As New mpz_t() : cgH.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(cgH.Pointer)
            Dim t0 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            SafeMpzMul(full, a, b)
            Dim msF As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            Dim t1 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            SafeMpzMul_ChunkedGrid(cgF, a, b, 0L)
            Dim msCF As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            Dim t2 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
            SafeMpzMul_ChunkedGrid(cgH, a, b, keep)
            Dim msCH As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - t2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
            Dim fullEq As Boolean = (GmpRaw_cmp(cgF.Pointer, full.Pointer) = 0)         ' chunked full == §gen
            Dim overEst As Boolean = (GmpRaw_cmp(cgH.Pointer, full.Pointer) >= 0)       ' high ≥ full
            Dim fullLimbs As Long = CLng(szA) + CLng(szB)
            Dim cs As Long = System.Math.Max(0L, (fullLimbs - keep) * 64L)
            Dim fs As New mpz_t() : fs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(fs.Pointer)
            Dim hs As New mpz_t() : hs.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(hs.Pointer)
            BigShiftRight(fs, full, cs)
            BigShiftRight(hs, cgH, cs)
            Dim regionEq As Boolean = (GmpRaw_cmp(hs.Pointer, fs.Pointer) = 0)
            Dim pass As Boolean = fullEq AndAlso overEst AndAlso regionEq
            Dim line As String = $"[TestChunkedGrid] szA={szA:N0} szB={szB:N0} keep={keep:N0} sq={sq}: fullEq={fullEq} highOver={overEst} highRegionEq={regionEq} | gen={msF:F0}ms cgFull={msCF:F0}ms cgHigh={msCH:F0}ms (cgHigh speedup={If(msCH > 0.0, msF / msCH, 0.0):F2}x) -> {If(pass, "PASS", "FAIL")}{vbCrLf}"
            Try : System.IO.File.AppendAllText(outPath, line) : Catch : End Try
            AppendLog(line)
            If Not pass Then allPass = False
            GmpRaw_clear(a.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(a.Pointer)
            If Not sq Then
                GmpRaw_clear(b.Pointer)
                Runtime.InteropServices.Marshal.FreeHGlobal(b.Pointer)
            End If
            GmpRaw_clear(full.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(full.Pointer)
            GmpRaw_clear(cgF.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(cgF.Pointer)
            GmpRaw_clear(cgH.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(cgH.Pointer)
            GmpRaw_clear(fs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(fs.Pointer)
            GmpRaw_clear(hs.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(hs.Pointer)
        Next ci
        Try : System.IO.File.AppendAllText(outPath, $"[TestChunkedGrid] OVERALL {If(allPass, "PASS", "FAIL")}{vbCrLf}") : Catch : End Try
        Return allPass
    End Function

    ''' <summary>
    ''' GMP's MSVC build uses signed 32-bit mp_size_t.  mpn_mul_fft internally
    ''' computes  pl * GMP_NUMB_BITS  (pl = nl + ml, GMP_NUMB_BITS = 64) as an
    ''' int32 expression.  When pl >= 33,554,432 limbs (= 2^31/64) this product
    ''' overflows int32, corrupting the FFT scratch-size calculation and causing
    ''' GmpAllocFunc to receive an invalid (huge/negative) size.
    '''
    ''' This wrapper detects when szA + szB >= the overflow threshold and falls
    ''' back to a 3-way schoolbook split: each operand is decomposed into three
    ''' pieces of ceil(sz/3) limbs, giving 9 safe sub-products.  Each
    ''' sub-product has at most ceil(szA/3)+ceil(szB/3) limbs = ~(szA+szB)/3*2
    ''' limbs, which stays well below the 33,554,431-limb threshold.
    ''' </summary>
    Private Shared Sub SafeMpzMul(result As mpz_t, opA As mpz_t, opB As mpz_t)
        ' §143: Threshold lowered from 33,554,431 to 10,000,000 to prevent GMP FFT precision errors.
        ' At ~7.3M × 7.3M limbs (total 14.6M), GMP's FFT transform size M≈2^24 causes rounding
        ' errors because 64-bit limb data makes intermediate FFT coefficients exceed double precision
        ' (53-bit mantissa), producing silently wrong products.  Lowering the threshold forces the
        ' 3×3 recursive split, breaking the sub-products down to ≈2.4M × 2.4M limbs each (≈4.9M
        ' total) which are well within safe FFT accuracy range.
        ' Upper bound constraint: pl * 64 must fit in int32 → pl_max = floor((2^31-1)/64) = 33,554,431.
        '
        ' §160: Threshold further lowered from 10,000,000 to 5,000,000.
        ' The a×r multiplication (szA=43750001, szB=21875001) uses two levels of 3×3 §gen:
        '   Outer §gen → inner SafeMpzMul(14583333, 7291667) [21.9M total, uses §gen again]
        '   Inner §gen → inner-inner SafeMpzMul(4861111, 2430555) [7.29M total]
        ' At 10M threshold, the inner-inner call (7.29M total) fell below the threshold and used
        ' GmpRaw_mul directly. But 7.29M total requires GMP FFT transform size M≈2^23, which
        ' still triggers the same double-precision rounding errors — silently producing wrong limbs
        ' at position ar[64654664] and causing the Barrett rem to exceed b (szRem=42779665 crash).
        ' q×b and b×r inner-inner products are only ≈4.86M total (M≈2^22) and are unaffected.
        ' Lowering to 5M forces SafeMpzMul(4861111, 2430555) into §gen, breaking it into
        ' sub-products of ≈2.43M total (M≈2^22), which are within safe FFT accuracy range.
        '
        ' §5B-Option-B (2026-04-27/28): briefly tried 1M to force one more recursion level
        ' past the 2.2M × 1.1M leaves. Run produced bit-identical wrong middle limbs
        ' for ALL 9 outer sub-products and threw the same Barrett error at §171 — proving
        ' the leaf mpz_mul / mpn_mul_fft is NOT the source of the 5B middle-limb bug.
        ' Reverted to 5M; investigation continues into the §gen accumulation step itself.
        Const SAFE_LIMB_THRESHOLD As Integer = 5_000_000

        Dim szA_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4)
        Dim szB_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4)
        Dim szA As Integer = System.Math.Abs(szA_signed)
        Dim szB As Integer = System.Math.Abs(szB_signed)

        ' §183: At SafeMpzMul entry, detect if opA._mp_d already points to zero data (pre-corruption).
        ' If opA._mp_d is zero-filled here, the bug happened in the CALLER before this call.
        If _logLevel >= 2 AndAlso opA.Pointer = opB.Pointer AndAlso szA > 0 Then
            Dim _183_opAd As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
            If _183_opAd <> 0L Then
                Dim _183_r0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_183_opAd), 0)
                Dim _183_r1 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_183_opAd + 8L), 0)
                If _183_r0 = 0L AndAlso _183_r1 = 0L Then
                    AppendLog($"[SafeMpzMul§183] ENTRY zero-data squaring: szA={szA:N0} opA_d={_183_opAd:X16} r[0]={_183_r0:X16} r[1]={_183_r1:X16} opAptr={opA.Pointer.ToInt64():X16} resPtr={result.Pointer.ToInt64():X16}{vbCrLf}", 5)   ' §252 (#95)
                End If
            End If
        End If

        If szA + szB <= SAFE_LIMB_THRESHOLD Then
            If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-PRE  szA={szA:N0} szB={szB:N0} | " &
                    $"opA.Ptr={opA.Pointer.ToInt64():X} opA_sz={szA_signed:N0} opA_d={Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8):X} " &
                    $"opB.Ptr={opB.Pointer.ToInt64():X} opB_sz={szB_signed:N0}{vbCrLf}")
            End If
            ' §78: Use raw P/Invoke to bypass Math.Gmp.Native's managed wrapper, which
            ' corrupts mpz_t.Pointer fields during native calls (same root cause as §42).
            GmpRaw_mul(result.Pointer, opA.Pointer, opB.Pointer)
            If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-POST result.Ptr={result.Pointer.ToInt64():X} result_sz={Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4):N0} " &
                    $"result_d={Runtime.InteropServices.Marshal.ReadInt64(result.Pointer, 8):X}{vbCrLf}")
            End If
            ' §178: Diagnose zero-result squarings in the fast path (szA+szB ≤ threshold).
            ' Fires when opA=opB (squaring) AND the result is unexpectedly zero.
            ' Case A: szA=0 means depth-1 trim incorrectly found all-zeros in r[0..mA-1].
            ' Case B: szA>0 but result=0 means GmpRaw_mul itself produced a wrong answer.
            ' In both cases, log szA and the raw limb at opA._mp_d to identify root cause.
            If _logLevel >= 2 AndAlso opA.Pointer = opB.Pointer Then
                Dim _178rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4))
                If _178rSz = 0 Then
                    Dim _178aD As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
                    Dim _178r0 As Long = If(_178aD <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_178aD), 0), 0L)
                    Dim _178r1 As Long = If(_178aD <> 0L AndAlso szA >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_178aD + 8L), 0), 0L)
                    AppendLog($"[SafeMpzMul§178] zero-result squaring: szA={szA:N0} opA.Ptr={opA.Pointer.ToInt64():X16} opA._mp_d={_178aD:X16} raw[0]={_178r0:X16} raw[1]={_178r1:X16}{vbCrLf}", 5)   ' §252 (#95)
                End If
            End If
            Return
        End If

        ' §70 (#70 RAM-cap dispatch): at depth-0 (Not _smm_innerForceSerial), if even §gen at DOP=1
        ' would exceed the memory budget, route to the chunked grid (full mode) instead — it has a
        ' ~half-size peak (no shifted buffer, tiny per-cell temps) so it completes where §gen OOMs,
        ' capping the ~40-70 GB depth-0 peak (§68 Phase C).  No-op on a roomy box (§gen-DOP1 fits);
        ' chunked-full is bit-identical to §gen (validated --test-chunkedgrid) but ~1.4× slower.
        If (Not _smm_innerForceSerial) AndAlso MemBudget_ShouldFallbackToChunkedGrid(CLng(szA), CLng(szB)) Then
            If _logLevel >= 2 Then AppendLog($"[MemoryBudget§70] §gen DOP=1 peak {MemBudget_ProjectMulPeakGB(CLng(szA), CLng(szB), 1):F1}GB > budget — routing {szA:N0}×{szB:N0} to chunked-grid full product (RAM cap){vbCrLf}")
            SafeMpzMul_ChunkedGrid(result, opA, opB, 0L)
            Return
        End If

        ' §143: Log when recursion is triggered for large multiplications (szA+szB > threshold).
        If _logLevel >= 5 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
            AppendLog($"[SafeMpzMul§143] RECURSE szA={szA:N0} szB={szB:N0} total={CLng(szA)+CLng(szB):N0} (threshold={SAFE_LIMB_THRESHOLD:N0}) — using 3×3 split to avoid GMP FFT precision error{vbCrLf}", 5)
        End If

        Dim resultSign As Integer = System.Math.Sign(szA_signed) * System.Math.Sign(szB_signed)

        ' Piece widths in limbs (ceiling division by 3) and bits.
        Dim mA As ULong = CULng((szA + 2) \ 3)
        Dim mB As ULong = CULng((szB + 2) \ 3)
        Dim bitsA As ULong = mA * 64UL
        Dim bitsB As ULong = mB * 64UL

        ' §40 — Accumulate into a separate `accum` object instead of `result`.
        ' §39 (restoring result.Pointer after each inner call) was insufficient: inner
        ' SafeMpzMul calls corrupt not only result.Pointer but also the contents of the
        ' native __mpz_struct at savedResultPtr itself (_mp_alloc is overwritten with the
        ' inner's pre-alloc size, making all accumulated data invisible to the caller).
        ' Since `accum` is never passed to any inner SafeMpzMul call, it is immune to
        ' this corruption.  At the end, accum's struct fields are written directly to
        ' savedResultPtr and result.Pointer is restored, giving the caller a valid result.
        Dim _resultLimbs As Long = CLng(szA) + CLng(szB) + 2L
        Dim _resultBytes As Long = _resultLimbs * 8L

        ' Save result's struct address, free its old limb buffer, then blank the struct.
        Dim savedResultPtr As IntPtr = result.Pointer
        Dim _oldResultAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(savedResultPtr, 0))
        Dim _oldResultPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))
        Dim _oldResultSz As Long = CLng(_oldResultAlloc) * 8L
        ' §30: All GMP limb buffers now come from native pool (VirtualAlloc-backed).
        ' GmpNativeAlloc_FreeRaw returns them to the SLIST or VirtualFrees directly.
        ' Replaces the old large/small branching (_savedGmpFree vs VirtualFree).
        GmpNativeAlloc_FreeRaw(_oldResultPtr, _oldResultSz)
        ' Blank result's struct _mp_alloc and _mp_size; _mp_d will hold the accumPtr stash (§44).
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, 0)
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, 0)

        ' §42: Allocate the large accumulation buffer and wire it into a raw accumPtr struct.
        ' Using Marshal.AllocHGlobal(16) for the struct header bypasses mpz_t wrapper
        ' corruption: Math.Gmp.Native cannot modify a plain IntPtr.
        ' §30 fix: use GmpNativeAlloc_PoolGet so NativeFreeFunc can correctly compute raw=ptr-16.
        ' Raw VirtualAlloc here would crash: NativeFreeFunc subtracts GMP_BLOCK_PREFIX (16) expecting
        ' the SLIST_ENTRY header, but a direct VirtualAlloc'd pointer has no such prefix.
        Dim accumBuf As IntPtr = GmpNativeAlloc_PoolGet(_resultBytes)
        If accumBuf = IntPtr.Zero Then
            AppendLog(
                $"[SafeMpzMul] accum pre-alloc FAILED for {_resultBytes \ 1048576L:N0} MB — throwing OOM{vbCrLf}", 1)   ' §252 (#95): OOM → level 1
            Throw New OutOfMemoryException($"SafeMpzMul: GmpNativeAlloc_PoolGet failed for accum buffer ({_resultBytes \ 1048576L} MB)")
        End If
        Dim accumPtr As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 0, CInt(_resultLimbs)) ' _mp_alloc
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 4, 0)                  ' _mp_size = 0
        Runtime.InteropServices.Marshal.WriteInt64(accumPtr, 8, accumBuf.ToInt64()) ' _mp_d
        ' §44: stash accumPtr in result's own native CRT struct (_mp_d slot, offset +8).
        ' Inner calls use `prod` as their result — they never touch outer result's struct.
        ' This slot survives all native GMP calls and managed-stack corruption.
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, accumPtr.ToInt64())
        If _logLevel >= 4 Then AppendLog(
            $"[SafeMpzMul] accum pre-alloc OK: {_resultLimbs:N0} limbs ({_resultBytes \ 1048576L:N0} MB){vbCrLf}")

        ' Split opB into three pieces upfront: opB is small so all three pieces coexist cheaply.
        ' opA and opB are Q/P values from Chudnovsky binary split, always non-negative.
        ' §90: Zero-copy limb-window pieces — struct headers point directly into opB's limb array.
        ' No init/tdiv needed; safe for bitsB > UInt32.Max. GMP reads pieces but never rewrites them.
        ' Cleanup: FreeHGlobal(header) only — GmpRaw_clear would free opB's buffer (catastrophic).
        Dim B0 As New mpz_t(), B1 As New mpz_t(), B2 As New mpz_t()
        B0.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        B1.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        B2.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Dim _opB_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)
        ' B0 = limbs [0, mB) of opB
        Dim _B0_szT As Integer = CInt(System.Math.Min(CLng(szB), CLng(mB)))
        While _B0_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_opB_d + CLng(_B0_szT - 1) * 8L)) = 0L
            _B0_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B0.Pointer, 0, CInt(mB))
        Runtime.InteropServices.Marshal.WriteInt32(B0.Pointer, 4, _B0_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B0.Pointer, 8, _opB_d)
        ' B1 = limbs [mB, 2*mB) of opB
        Dim _B1_d As Long = _opB_d + CLng(mB) * 8L
        Dim _B1_szT As Integer = CInt(System.Math.Min(CLng(szB) - CLng(mB), CLng(mB)))
        If _B1_szT < 0 Then _B1_szT = 0
        While _B1_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_B1_d + CLng(_B1_szT - 1) * 8L)) = 0L
            _B1_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B1.Pointer, 0, CInt(mB))
        Runtime.InteropServices.Marshal.WriteInt32(B1.Pointer, 4, _B1_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B1.Pointer, 8, _B1_d)
        ' B2 = limbs [2*mB, szB) of opB
        Dim _B2_d As Long = _opB_d + 2L * CLng(mB) * 8L
        Dim _B2_limbs As Long = System.Math.Max(0L, CLng(szB) - 2L * CLng(mB))
        Dim _B2_szT As Integer = CInt(_B2_limbs)
        While _B2_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_B2_d + CLng(_B2_szT - 1) * 8L)) = 0L
            _B2_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B2.Pointer, 0, CInt(_B2_limbs))
        Runtime.InteropServices.Marshal.WriteInt32(B2.Pointer, 4, _B2_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B2.Pointer, 8, _B2_d)
        If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 10_000_000L Then
            AppendLog(
                $"[SafeMpzMul] B-pieces | " &
                $"B0.Ptr={B0.Pointer.ToInt64():X} B0_sz={Runtime.InteropServices.Marshal.ReadInt32(B0.Pointer, 4):N0} " &
                $"B1.Ptr={B1.Pointer.ToInt64():X} B1_sz={Runtime.InteropServices.Marshal.ReadInt32(B1.Pointer, 4):N0} " &
                $"B2.Ptr={B2.Pointer.ToInt64():X} B2_sz={Runtime.InteropServices.Marshal.ReadInt32(B2.Pointer, 4):N0}{vbCrLf}")
        End If

        ' §90: Zero-copy limb-window pieces for A — headers point directly into opA's limb array.
        ' No init2/tdiv/CopyMemory needed; safe for bitsA > UInt32.Max.
        ' Cleanup: FreeHGlobal(header) only — GmpRaw_clear would free opA's buffer (catastrophic).
        Dim A0 As New mpz_t(), A1 As New mpz_t(), A2 As New mpz_t()
        A0.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        A1.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        A2.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Dim _pre_opA_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
        ' A0 = limbs [0, mA) of opA
        Dim _A0_szT As Integer = CInt(System.Math.Min(CLng(szA), CLng(mA)))
        While _A0_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + CLng(_A0_szT - 1) * 8L)) = 0L
            _A0_szT -= 1
        End While
        ' §179: Catch A0-trim-to-zero in squarings — diagnoses wrong _pre_opA_d vs genuine all-zero data.
        If _logLevel >= 2 AndAlso _A0_szT = 0 AndAlso CInt(System.Math.Min(CLng(szA), CLng(mA))) > 0 AndAlso _pre_opA_d = _opB_d Then
            Dim _179_raw0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0)
            Dim _179_raw1 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + 8L), 0)
            Dim _179_opAptr As Long = opA.Pointer.ToInt64()
            Dim _179_freed As Long = _oldResultPtr.ToInt64()
            Dim _179_accBuf As Long = accumBuf.ToInt64()
            Dim _179_same As Boolean = (_179_freed = _pre_opA_d)
            Dim _179_accumSame As Boolean = (_179_accBuf = _pre_opA_d)
            AppendLog($"[SafeMpzMul§179] A0-trim-ZERO squaring: szA={szA:N0} mA={mA:N0} opA_d={_pre_opA_d:X16} raw[0]={_179_raw0:X16} raw[1]={_179_raw1:X16}" &
                      $" opAptr={_179_opAptr:X16} savedResPtr={savedResultPtr.ToInt64():X16} freedBuf={_179_freed:X16} freed==opA_d={_179_same}" &
                      $" accumBuf={_179_accBuf:X16} accumBuf==opA_d={_179_accumSame}{vbCrLf}")
        End If
        Runtime.InteropServices.Marshal.WriteInt32(A0.Pointer, 0, CInt(mA))
        Runtime.InteropServices.Marshal.WriteInt32(A0.Pointer, 4, _A0_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A0.Pointer, 8, _pre_opA_d)
        ' A1 = limbs [mA, 2*mA) of opA
        Dim _A1_d As Long = _pre_opA_d + CLng(mA) * 8L
        Dim _A1_szT As Integer = CInt(System.Math.Min(CLng(szA) - CLng(mA), CLng(mA)))
        If _A1_szT < 0 Then _A1_szT = 0
        While _A1_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A1_d + CLng(_A1_szT - 1) * 8L)) = 0L
            _A1_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A1.Pointer, 0, CInt(mA))
        Runtime.InteropServices.Marshal.WriteInt32(A1.Pointer, 4, _A1_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A1.Pointer, 8, _A1_d)
        ' A2 = limbs [2*mA, szA) of opA
        Dim _A2_d As Long = _pre_opA_d + 2L * CLng(mA) * 8L
        Dim _A2_limbs As Long = System.Math.Max(0L, CLng(szA) - 2L * CLng(mA))
        Dim _A2_szT As Integer = CInt(_A2_limbs)
        While _A2_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A2_d + CLng(_A2_szT - 1) * 8L)) = 0L
            _A2_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A2.Pointer, 0, CInt(_A2_limbs))
        Runtime.InteropServices.Marshal.WriteInt32(A2.Pointer, 4, _A2_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A2.Pointer, 8, _A2_d)

        Dim A_parts() As mpz_t = {A0, A1, A2}
        Dim B_parts() As mpz_t = {B0, B1, B2}

        ' §115: log piece trim-sizes and buffer identity to distinguish r*r vs q*b calls.
        ' If opA._mp_d == opB._mp_d → r*r (same buffer). Else → q*b or other.
        If _logLevel >= 2 AndAlso mA = 7291667UL Then
            Dim _115same As Boolean = (_pre_opA_d = _opB_d)
            AppendLog($"[SafeMpzMul§115] mA=mB=7291667 call: opA_d={_pre_opA_d:X16} opB_d={_opB_d:X16} same={_115same}" &
                      $" A0sz={_A0_szT:N0} A1sz={_A1_szT:N0} A2sz={_A2_szT:N0}" &
                      $" B0sz={_B0_szT:N0} B1sz={_B1_szT:N0} B2sz={_B2_szT:N0}{vbCrLf}")
        End If

        ' §177: For depth-2 r×r squaring calls (mA=2430556, same buffer), log opA_d, szA,
        ' piece trim sizes, and the actual limb values at key positions in the sub-piece.
        ' This diagnoses why A_sub0_2 appears as size=0 even though r[0]≠0.
        If _logLevel >= 2 AndAlso mA = 2430556UL AndAlso _pre_opA_d = _opB_d Then
            Dim _177b0 As Long = If(_A0_szT >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0), 0L)
            Dim _177b1 As Long = If(_A0_szT >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 8), 0L)
            Dim _177top As Long = If(_A0_szT >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + CLng(_A0_szT - 1) * 8L), 0), 0L)
            Dim _177rawAt0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0)
            AppendLog($"[SafeMpzMul§177] depth2-sq mA=2430556 szA={szA:N0} opA_d={_pre_opA_d:X16}" &
                      $" A0sz={_A0_szT:N0} A1sz={_A1_szT:N0} A2sz={_A2_szT:N0}" &
                      $" sub0[0]={_177b0:X16} sub0[1]={_177b1:X16} sub0[top]={_177top:X16} raw[0]@opA_d={_177rawAt0:X16}{vbCrLf}")
        End If

        ' Allocate one result buffer per sub-product (k = i*3 + j, k ∈ 0..8).
        ' §25: raw init — Marshal.AllocHGlobal(16) struct header + GmpRaw_init limb buffer.
        Dim prods(8) As mpz_t
        For k As Integer = 0 To 8
            prods(k) = New mpz_t()
            prods(k).Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(prods(k).Pointer)
        Next k

        ' §59: Run all 9 sub-products A_i × B_j simultaneously on the thread pool.
        ' Each call uses a distinct prods(k) as result; A_parts and B_parts are read-only shared.
        ' GMP arithmetic on non-aliased objects is thread-safe. The §44 accumPtr stash lives in
        ' result's native struct (_mp_d slot); inner calls stash into prods(k) structs instead
        ' and never touch result's struct, so the outer stash is preserved across the Parallel.For.
        ' §69: Use _safeMulDop to control inner parallelism. When called from Phase 2's
        ' Parallel.For (outer DOP=24), _safeMulDop=1 so sub-products run serially — eliminates
        ' the thread-pool park/unpark overhead. When called from the serial Phase 2 top levels
        ' or ComputePiGMP, _safeMulDop=ProcessorCount to use all cores.
        ' §238 (issue #87): if this thread is already running inside a parent SafeMpzMul's
        ' Parallel.For sub-product lambda, force serial sub-products here — caps the nested
        ' parallel memory blow-up that crashed 5B sqrt_step_6.  See _smm_innerForceSerial decl.
        Dim _smmDop As Integer
        If _smm_innerForceSerial Then
            _smmDop = 1
        Else
            _smmDop = System.Threading.Volatile.Read(_safeMulDop)  ' §27: cross-thread read
        End If
        If _smmDop <= 0 Then _smmDop = Environment.ProcessorCount
        ' §243 (issue #68): MemoryBudget DOP floor.  Only at top-level large muls (_smmDop>1,
        ' i.e. depth-0 — inner recursion is forced to 1 by §238).  First trim the pool if
        ' commit headroom is low (frees GB-scale ≤16MB granules per #69), then reduce DOP if
        ' the projected 9-sub-product §gen peak would exceed available commit.  FLOOR ONLY
        ' (Min): never raises DOP, so on a healthy box with ample RAM this is a no-op.  Reading
        ' is cached (~2 s) so the cost is negligible even though SafeMpzMul is called often.
        If _smmDop > 1 Then
            MemBudget_MaybeTrimUnderPressure(10.0)
            Dim _budgetDop As Integer = MemBudget_SuggestSafeMulDop(CLng(szA), CLng(szB))
            If _budgetDop < _smmDop Then
                If _logLevel >= 2 Then AppendLog($"[MemoryBudget§243] §gen DOP floored {_smmDop}→{_budgetDop} (szA={szA:N0} szB={szB:N0} availPhys={MemBudget_AvailablePhysicalGB():F1}GB availCommit={MemBudget_AvailableCommitGB():F1}GB projIncr@{_smmDop}={MemBudget_ProjectMulPeakGB(CLng(szA), CLng(szB), _smmDop):F1}GB){vbCrLf}")
                _smmDop = _budgetDop
            End If
        End If
        ' §138/§165 LIFTED by §221 (issue #44, 2026-05-22): the size-gate that forced
        ' serial 9-sub-product computation for q×b (szA=szB=21875001) and a×r
        ' (szA=43750001, szB=21875001) when opA_d≠opB_d. The original "wrong prods(8)"
        ' symptom was the SAME upstream Newton-convergence bug §200/§201 fixed and the
        ' SAME issue §220 (issue #55) just lifted at SafeMpzDiv. With §200/§201 in place
        ' the parallel SafeMpzMul path produces correct prods regardless of opA_d/opB_d
        ' identity. The 9 sub-products inside this Parallel.For are independent results
        ' written to distinct prods(k) slots; concurrent GMP mpz_mul on distinct mpz_t
        ' destinations has no shared mutable state outside the GmpNativeAlloc pool
        ' (which is per-thread-safe by design).
        ' Original gate preserved as comment for easy revert (grep §221):
        '   Dim _forceSerialQxB As Boolean = ((szA = 21875001 OrElse szA = 43750001) AndAlso szB = 21875001 AndAlso _pre_opA_d <> _opB_d)
        '   If _smmDop <= 1 OrElse _forceSerialQxB Then
        '       If _logLevel >= 2 AndAlso _forceSerialQxB Then AppendLog($"[SafeMpzMul§138] forcing serial sub-products...")
        ' The §144/§170/§169 in-loop verifiers stay enabled and would catch any regression.
        If _smmDop <= 1 Then
            ' Serial path: no thread pool involvement, no park/unpark overhead.
            For k As Integer = 0 To 8
                ' §182: Before each inner call involving A2 (k=6,7,8), log A2._mp_d and its raw[0].
                ' Detects when A2._mp_d gets corrupted between piece setup and the k=8 call.
                If _logLevel >= 2 AndAlso k >= 6 AndAlso _pre_opA_d = _opB_d Then
                    Dim _182_A2d As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
                    Dim _182_A2r0 As Long = If(_182_A2d <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_182_A2d), 0), 0L)
                    AppendLog($"[SafeMpzMul§182] pre-k={k} squaring szA={szA:N0} mA={mA:N0} A2_ptr={A_parts(2).Pointer.ToInt64():X16} A2_d={_182_A2d:X16} A2_d[0]={_182_A2r0:X16}{vbCrLf}")
                End If
                SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
            Next k
        Else
            Dim _smm_opts As New System.Threading.Tasks.ParallelOptions() With {
                .MaxDegreeOfParallelism = _smmDop
            }
            Parallel.For(0, 9, _smm_opts, Sub(k As Integer)
                ' §238 (issue #87): mark this worker thread as "inside parallel sub-product"
                ' so recursive SafeMpzMul below sees the flag and forces _smmDop=1.  Save/restore
                ' for ThreadPool thread reuse — the worker may host unrelated work later.
                Dim _was238 As Boolean = _smm_innerForceSerial
                _smm_innerForceSerial = True
                Try
                    SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
                Finally
                    _smm_innerForceSerial = _was238
                End Try
            End Sub)
        End If

        ' §5B-c2 (DISABLED 2026-04-28): originally compared prods(8) against a direct
        ' GmpRaw_mul on A_2 × B_2 (58.3M × 29.2M).  At 87.5M total limbs, GMP's mpz_mul
        ' AVs (0xC0000005 inside libgmp-10.dll) — the FFT workspace either OOMs or fails
        ' an internal bound check.  This is the very failure §143 exists to avoid via the
        ' recursive 3×3 split.  Existing §136 block uses the same pattern at 21.9M × 21.9M
        ' (43.75M total) where GMP merely produces wrong limbs instead of crashing.
        ' Replaced by §5B-e below: chunked-grid reference using sub-threshold direct
        ' GmpRaw_mul calls then independent accumulation.

        ' §5B-e: Chunked-grid independent reference for prods(7) = A_2 × B_1 and
        ' prods(8) = A_2 × B_2 at the outer 175M × 87.5M call.  Compute each via
        ' a 39×20 grid (≤ 1.5M × 1.5M = ≤ 3M total per sub-product, well under
        ' SAFE_LIMB_THRESHOLD = 5M where direct GmpRaw_mul is reliable per §160's
        ' analysis), then accumulate via mul_2exp + add into a reference mpz_t
        ' whose limb buffer is PRE-ALLOCATED via VirtualAlloc to the max final size
        ' (avoiding GMP-internal realloc paths through NativeReallocFunc that aborted
        ' run 11 with "gmp: overflow in mpz type").  Same swap-in pattern as §gen's
        ' _sharedSjBuf / _sv_shifted_hdr.
        '
        ' Compare the suspect index to our SafeMpzMul prods(k):
        '   Match  ⇒ our prods(k) is correct (bug elsewhere — likely the other
        '             prods(k) or in the level-1 GmpRaw_add carry chain).
        '   Differ ⇒ our prods(k) is wrong; the chunked reference IS the truth, and
        '             we have an exact delta + a known-good limb to investigate
        '             our SafeMpzMul split with.
        If _logLevel >= 2 AndAlso szA = 175000001 AndAlso szB = 87500001 Then
            Const _CHUNK_E As Integer = 1500000
            ' Max final/intermediate buffer size: prods(7|8) = ≤ 87.5M limbs; pad to 90M for safety.
            Const _E_MAX_LIMBS As Integer = 90_000_000
            Dim _E_MAX_BYTES As Long = CLng(_E_MAX_LIMBS) * 8L
            AppendLog($"[SafeMpzMul§5B-e] starting chunked-grid reference (chunk={_CHUNK_E:N0}, prealloc={_E_MAX_LIMBS:N0} limbs/buf, {_E_MAX_BYTES \ 1048576L:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            For Each _refIdx As Integer In New Integer() {7, 8}
                Dim _refTargetIdx As Long = If(_refIdx = 7, 72916666L, 43749999L)
                Dim _ref_A_d As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
                Dim _ref_A_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(A_parts(2).Pointer, 4))
                Dim _ref_B_partIdx As Integer = If(_refIdx = 7, 1, 2)
                Dim _ref_B_d As Long = Runtime.InteropServices.Marshal.ReadInt64(B_parts(_ref_B_partIdx).Pointer, 8)
                Dim _ref_B_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(B_parts(_ref_B_partIdx).Pointer, 4))
                AppendLog($"[SafeMpzMul§5B-e prods({_refIdx})] A_2 sz={_ref_A_sz:N0} B_{_ref_B_partIdx} sz={_ref_B_sz:N0} target idx={_refTargetIdx:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Per-refIdx VirtualAlloc'd buffers (zeroed by VirtualAlloc, freed at end)
                Dim _eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_E_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                Dim _eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_E_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                If _eAccBuf = IntPtr.Zero OrElse _eShiftBuf = IntPtr.Zero Then
                    AppendLog($"[SafeMpzMul§5B-e prods({_refIdx})] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                    If _eAccBuf <> IntPtr.Zero Then VirtualFree(_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                    If _eShiftBuf <> IntPtr.Zero Then VirtualFree(_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
                    Continue For
                End If
                ' Setup _refAcc with swapped-in pre-allocated buffer (mirror §gen's _sv_shifted_hdr setup).
                Dim _refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_refAcc)
                Dim _ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_refAcc, 0))
                Dim _ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_ra_initPtr, _ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_refAcc, 0, _E_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_refAcc, 8, _eAccBuf.ToInt64())
                ' Same swap-in for _ckShifted
                Dim _ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_ckShifted)
                Dim _cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_ckShifted, 0))
                Dim _cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_cs_initPtr, _cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 0, _E_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_ckShifted, 8, _eShiftBuf.ToInt64())
                ' _ckPartial uses GMP-managed buffer (small, ~3M limbs max — realloc is safe at this size).
                Dim _ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_ckPartial)
                ' Zero-copy chunk headers (do NOT GmpRaw_clear — they alias opA/opB data).
                Dim _ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _numCkA As Integer = (_ref_A_sz + _CHUNK_E - 1) \ _CHUNK_E
                Dim _numCkB As Integer = (_ref_B_sz + _CHUNK_E - 1) \ _CHUNK_E
                Dim _ckCount As Integer = 0
                For i As Integer = 0 To _numCkA - 1
                    Dim _ckA_off As Long = CLng(i) * CLng(_CHUNK_E)
                    Dim _ckA_sz As Integer = CInt(System.Math.Min(CLng(_CHUNK_E), CLng(_ref_A_sz) - _ckA_off))
                    If _ckA_sz <= 0 Then Continue For
                    While _ckA_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_ref_A_d + (_ckA_off + CLng(_ckA_sz - 1)) * 8L)) = 0L
                        _ckA_sz -= 1
                    End While
                    If _ckA_sz <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_ckA, 0, _CHUNK_E)
                    Runtime.InteropServices.Marshal.WriteInt32(_ckA, 4, _ckA_sz)
                    Runtime.InteropServices.Marshal.WriteInt64(_ckA, 8, _ref_A_d + _ckA_off * 8L)
                    For j As Integer = 0 To _numCkB - 1
                        Dim _ckB_off As Long = CLng(j) * CLng(_CHUNK_E)
                        Dim _ckB_sz As Integer = CInt(System.Math.Min(CLng(_CHUNK_E), CLng(_ref_B_sz) - _ckB_off))
                        If _ckB_sz <= 0 Then Continue For
                        While _ckB_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_ref_B_d + (_ckB_off + CLng(_ckB_sz - 1)) * 8L)) = 0L
                            _ckB_sz -= 1
                        End While
                        If _ckB_sz <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_ckB, 0, _CHUNK_E)
                        Runtime.InteropServices.Marshal.WriteInt32(_ckB, 4, _ckB_sz)
                        Runtime.InteropServices.Marshal.WriteInt64(_ckB, 8, _ref_B_d + _ckB_off * 8L)
                        GmpRaw_mul(_ckPartial, _ckA, _ckB)
                        Dim _ckShiftBits As ULong = CULng(_ckA_off + _ckB_off) * 64UL
                        If _ckShiftBits = 0UL Then
                            GmpRaw_add(_refAcc, _refAcc, _ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 4, 0)
                            Dim _shiftSrc As IntPtr = _ckPartial
                            Dim _shiftRem As ULong = _ckShiftBits
                            While _shiftRem > 0UL
                                Dim _chunkBits As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_ckShifted, _shiftSrc, _chunkBits)
                                _shiftSrc = _ckShifted
                                _shiftRem -= CULng(_chunkBits)
                            End While
                            GmpRaw_add(_refAcc, _refAcc, _ckShifted)
                        End If
                        _ckCount += 1
                    Next j
                Next i
                Dim _refAccSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_refAcc, 4))
                Dim _refAccD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_refAcc, 8))
                Dim _refV As ULong = If(_refTargetIdx < CLng(_refAccSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt(_refTargetIdx * 8L))), 0UL)
                Dim _refV_lo As ULong = If(_refTargetIdx - 1L < CLng(_refAccSz) AndAlso _refTargetIdx - 1L >= 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt((_refTargetIdx - 1L) * 8L))), 0UL)
                Dim _refV_hi As ULong = If(_refTargetIdx + 1L < CLng(_refAccSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt((_refTargetIdx + 1L) * 8L))), 0UL)
                Dim _ourSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_refIdx).Pointer, 4))
                Dim _ourD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(_refIdx).Pointer, 8))
                Dim _ourV As ULong = If(_refTargetIdx < CLng(_ourSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt(_refTargetIdx * 8L))), 0UL)
                Dim _ourV_lo As ULong = If(_refTargetIdx - 1L < CLng(_ourSz) AndAlso _refTargetIdx - 1L >= 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt((_refTargetIdx - 1L) * 8L))), 0UL)
                Dim _ourV_hi As ULong = If(_refTargetIdx + 1L < CLng(_ourSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt((_refTargetIdx + 1L) * 8L))), 0UL)
                AppendLog($"[SafeMpzMul§5B-e prods({_refIdx}) idx={_refTargetIdx:N0}] subProducts={_ckCount:N0} refSz={_refAccSz:N0} ourSz={_ourSz:N0} reference[idx-1,idx,idx+1]=[{_refV_lo:X16} {_refV:X16} {_refV_hi:X16}] ourSafeMpzMul[idx-1,idx,idx+1]=[{_ourV_lo:X16} {_ourV:X16} {_ourV_hi:X16}] match@idx={(_refV = _ourV)}{vbCrLf}", 5)   ' §252 (#95)
                ' Cleanup — _refAcc and _ckShifted have swapped-in VirtualAlloc'd buffers.
                ' Do NOT GmpRaw_clear (would call NativeFreeFunc on a pointer that has no
                ' SLIST_ENTRY prefix → catastrophic).  Free the 16-byte struct headers and
                ' VirtualFree the limb buffers separately.
                Runtime.InteropServices.Marshal.FreeHGlobal(_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckShifted)
                ' _ckPartial uses GMP-managed buffer — full GmpRaw_clear is correct.
                GmpRaw_clear(_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_ckPartial)
                ' Chunk headers point into A_2/B_j data — header-only free.
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckB)
                ' Release pre-allocated VirtualAlloc'd buffers.
                VirtualFree(_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Next
            AppendLog($"[SafeMpzMul§5B-e] chunked-grid reference complete{vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §136: directly call GmpRaw_mul for A2×B2 and compare to prods(8)[13612996] for q×b.
        ' After §143 threshold fix: prods(8) is computed via recursive SafeMpzMul (correct),
        ' but §136's direct GmpRaw_mul call bypasses the threshold and hits the GMP FFT precision bug.
        ' Expected post-fix: match=False (§136 direct=wrong, prods(8) recursive=correct).
        ' This mismatch CONFIRMS the GMP FFT bug: same inputs, different code paths, different results.
        If _logLevel >= 2 AndAlso szA = 21875001 AndAlso szB = 21875001 AndAlso _pre_opA_d <> _opB_d Then
            Dim _fr136 As New mpz_t()
            _fr136.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_fr136.Pointer)
            GmpRaw_mul(_fr136.Pointer, A_parts(2).Pointer, B_parts(2).Pointer)
            Dim _fr136sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_fr136.Pointer, 4))
            Dim _fr136D As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_fr136.Pointer, 8))
            Const _IDX136 As Long = 13612996L
            Dim _fr136v As Long = If(_IDX136 < CLng(_fr136sz), Runtime.InteropServices.Marshal.ReadInt64(_fr136D, CInt(_IDX136 * 8L)), 0L)
            Dim _p8sz136 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(8).Pointer, 4))
            Dim _p8D136 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(8).Pointer, 8))
            Dim _p8v136 As Long = If(_IDX136 < CLng(_p8sz136), Runtime.InteropServices.Marshal.ReadInt64(_p8D136, CInt(_IDX136 * 8L)), 0L)
            AppendLog($"[SafeMpzMul§136] serial A2×B2[{_IDX136:N0}]={_fr136v:X16} prods(8)[{_IDX136:N0}]={_p8v136:X16} match={_fr136v = _p8v136}{vbCrLf}", 5)   ' §252 (#95)
            GmpRaw_clear(_fr136.Pointer)
            Runtime.InteropServices.Marshal.FreeHGlobal(_fr136.Pointer)
        End If

        ' §134: after all sub-products are computed, log A2×B2 (prods(8)) bot/top/[13612996]
        ' for the q×b call (szA=szB=21875001). Verifies GmpRaw_mul output before accumulation.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§134-probe] szA={szA} szB={szB} cond={szA = 21875001 AndAlso szB = 21875001}{vbCrLf}")
        If _logLevel >= 2 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
            Dim _p8Ptr134 As IntPtr = prods(8).Pointer
            Dim _p8Sz134 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_p8Ptr134, 4))
            Dim _p8D134 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_p8Ptr134, 8))
            Dim _p8Bot134 As Long = If(_p8Sz134 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p8D134, 0), 0L)
            Dim _p8Bot1134 As Long = If(_p8Sz134 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p8D134, 8), 0L)
            ' §237 (issue #86): 64-bit safe pointer arithmetic — latent at 5 B since this block is gated by szA=21875001 (1 B-tuned).
            Dim _p8Top134 As Long = If(_p8Sz134 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p8D134.ToInt64() + (CLng(_p8Sz134) - 1L) * 8L), 0), 0L)
            Const _IDX134 As Long = 13612996L
            Dim _p8Mid134 As Long = If(_IDX134 < CLng(_p8Sz134), Runtime.InteropServices.Marshal.ReadInt64(_p8D134, CInt(_IDX134 * 8L)), 0L)
            ' Also log A2[0] and B2[0] to verify piece contents
            Dim _a2D134 As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
            Dim _b2D134 As Long = Runtime.InteropServices.Marshal.ReadInt64(B_parts(2).Pointer, 8)
            Dim _a2Bot134 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_a2D134), 0)
            Dim _b2Bot134 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_b2D134), 0)
            AppendLog($"[SafeMpzMul§134] A2×B2: A2[0]={_a2Bot134:X16} B2[0]={_b2Bot134:X16}" &
                      $" szProd={_p8Sz134:N0} [0]={_p8Bot134:X16} [1]={_p8Bot1134:X16}" &
                      $" [top]={_p8Top134:X16} [{_IDX134:N0}]={_p8Mid134:X16}{vbCrLf}")
        End If
        ' §176: For the r×r squaring call (same buffer, mA=mB=7291667), read prods(0..2)[0]
        ' immediately after inner SafeMpzMul completes — before §44 recovery or any shift.
        ' If prods(0)[0]=0 here, the bug is inside depth-2 computation of A_piece0^2.
        ' If prods(0)[0]≠0 here but =0 at §114, something between here and §114 corrupts it.
        If _logLevel >= 2 AndAlso mA = 7291667UL AndAlso _pre_opA_d = _opB_d Then
            For _p176 As Integer = 0 To 2
                Dim _ptr176 As IntPtr = prods(_p176).Pointer
                If _ptr176 <> IntPtr.Zero Then
                    Dim _sz176 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_ptr176, 4))
                    Dim _d176 As Long = Runtime.InteropServices.Marshal.ReadInt64(_ptr176, 8)
                    Dim _b0_176 As Long = If(_sz176 >= 1 AndAlso _d176 <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_d176), 0), 0L)
                    Dim _b1_176 As Long = If(_sz176 >= 2 AndAlso _d176 <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_d176), 8), 0L)
                    AppendLog($"[SafeMpzMul§176] post-inner k={_p176} prods[{_p176}].Ptr={_ptr176.ToInt64():X16} sz={_sz176:N0} [0]={_b0_176:X16} [1]={_b1_176:X16}{vbCrLf}")
                End If
            Next _p176
        End If

        ' §44: recover accumPtr from result's stash after Parallel.For.
        ' §181-fix: Do NOT re-read result.Pointer here — Math.Gmp.Native may have corrupted it
        ' during inner SafeMpzMul calls (§175/§78 corruption). savedResultPtr was captured at
        ' line 2274 as a plain IntPtr, immune to managed-wrapper corruption, and remains correct.
        ' The old `savedResultPtr = result.Pointer` overwrite was the root cause of opA._mp_d
        ' being corrupted across recursive depths (§179 diagnosis).
        accumPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] szA={szA:N0} accumPtr recovered; allocating shifted buffer ({CLng(mA) + CLng(mB) + CLng((2UL * bitsA + 2UL * bitsB) \ 64UL) + 4L:N0} limbs){vbCrLf}")

        ' Serial accumulation: shift each prod_k into its positional slot and add to accum.
        ' No inner SafeMpzMul calls — no §42/§44 managed-stack corruption in this loop.
        '
        ' §23: Pre-allocate ONE shared shifted buffer sized for the largest iteration,
        ' replacing the original 8 per-k VirtualAlloc/VirtualFree pairs with a single pair.
        ' Upper bound for any k=8 (max shift = 2*bitsA + 2*bitsB):
        '   sub-product limbs ≤ mA + mB,  shift limbs = (2*bitsA+2*bitsB)/64 + 1
        '   which simplifies to ≤ 3*mA + 3*mB + 4 total limbs.
        Dim _maxShiftBitsShared As ULong = 2UL * bitsA + 2UL * bitsB
        Dim _maxShiftedLimbs As Long = CLng(mA) + CLng(mB) + CLng(_maxShiftBitsShared \ 64UL) + 4L
        Dim _sharedSjBuf As IntPtr = VirtualAlloc(IntPtr.Zero,
                                                   New UIntPtr(CULng(_maxShiftedLimbs * 8L)),
                                                   MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If _sharedSjBuf = IntPtr.Zero Then
            GmpNativeAlloc_FreeRaw(accumBuf, _resultBytes)   ' §30 fix: match PoolGet allocation above
            Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
            AppendLog($"[SafeMpzMul] shared shifted pre-alloc FAILED for {_maxShiftedLimbs * 8L \ 1048576L:N0} MB — throwing OOM{vbCrLf}", 1)   ' §252 (#95): OOM → level 1
            Throw New OutOfMemoryException($"SafeMpzMul: VirtualAlloc failed for shared shifted ({_maxShiftedLimbs * 8L \ 1048576L} MB)")
        End If
        ' §122 (#122): the real §39 decision (symmetric + ≤50M total + all 6 pieces dense) is made
        ' below and logged there — this line previously printed a premature "§39=" using the wrong
        ' 100M threshold and ignoring the dense check, which did not reflect what the code did.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] shifted buffer OK ({_maxShiftedLimbs * 8L \ 1048576L:N0} MB); starting accumulation{vbCrLf}")
        ' §25: raw init for shifted — struct header via Marshal.AllocHGlobal, limb buffer via GmpRaw_init.
        Dim shifted As New mpz_t()
        shifted.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        GmpRaw_init(shifted.Pointer)
        ' Replace shifted's initial 1-limb CRT buffer with the shared VirtualAlloc'd buffer.
        Dim _sv_shifted_hdr As IntPtr = shifted.Pointer
        Dim _shiftedInitAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_sv_shifted_hdr, 0))
        Dim _shiftedInitPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_shifted_hdr, 8))
        ' §30: Free the initial 1-limb buffer allocated by GmpRaw_init via native pool.
        GmpNativeAlloc_FreeRaw(_shiftedInitPtr, CLng(_shiftedInitAlloc) * 8L)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 0, CInt(_maxShiftedLimbs))
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
        Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted_hdr, 8, _sharedSjBuf.ToInt64())

        ' §39: When mA=mB=m sub-products sharing the same (i+j) have identical shift
        ' = (i+j)*m*64.  Group into 5 columns and shift once per column instead of 9
        ' times individually.  Saves 4 large shift operations at the cost of 4 adds.
        ' Column 0: prod(0)              shift 0
        ' Column 1: prod(1)+prod(3)      shift 1*bitsA
        ' Column 2: prod(2)+prod(4)+prod(6) shift 2*bitsA
        ' Column 3: prod(5)+prod(7)      shift 3*bitsA
        ' Column 4: prod(8)              shift 4*bitsA
          ' §128: The §39 column-group path assumes all split windows behave like
          ' dense m-limb blocks. In the failing SafeMpzDiv q*b case, B0 is exactly
          ' zero (B0sz=0), and the grouped accumulation produced a catastrophic
          ' quotient under-estimate. Fall back to the general 9-product path when
          ' any split piece is zero-sized; keep §39 only for fully dense windows.
          ' §Phase3ColAdd: The §39 column adds (e.g. prods(2)+=prods(4)) trigger a GMP
          ' internal realloc of the destination sub-product buffer: our NativeReallocFunc
          ' does VirtualAlloc(new) + memcpy + VirtualFree(old). When each sub-product
          ' exceeds ~800 MB (mA+mB > 100 M limbs) this VirtualAlloc can fail at peak
          ' memory pressure — GMP receives NULL from its realloc callback and calls
          ' abort(), killing the process silently. The §gen path (below) accumulates
          ' each sub-product one at a time into the pre-sized accumulator and shifted
          ' buffer, so no GMP realloc is triggered. Skip §39 for large sub-products.
          ' Option G (2026-04-29): disable §39 column-group fast path to test the
          ' hypothesis that §39 produces a wrong q×b at 5B scale.  Set _OPT_G_DISABLE_S39
          ' = True to force every symmetric SafeMpzMul to go through §gen.  If §171 stops
          ' throwing with §39 disabled, the bug is in §39's accumulation logic.
          Const _OPT_G_DISABLE_S39 As Boolean = False  ' Reverted after run 16 ruled out §39
          ' §122 (#122): authoritative §39 gate — symmetric (mA=mB), ≤50M total limbs, and all 6
          ' split pieces dense (non-zero).  Captured into a boolean so the log reflects the real
          ' decision (the earlier shifted-buffer log used a wrong 100M threshold + no dense check).
          Dim _s39Dense As Boolean = (_A0_szT > 0 AndAlso _A1_szT > 0 AndAlso _A2_szT > 0 AndAlso
                                      _B0_szT > 0 AndAlso _B1_szT > 0 AndAlso _B2_szT > 0)
          Dim _s39Engaged As Boolean = (Not _OPT_G_DISABLE_S39) AndAlso
              mA = mB AndAlso
              CLng(mA) + CLng(mB) <= 50_000_000L AndAlso
              _s39Dense
          If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] §39={_s39Engaged} (mA={mA:N0} mB={mB:N0} sum={CLng(mA) + CLng(mB):N0} cap=50,000,000 dense={_s39Dense}){vbCrLf}")
          If _s39Engaged Then
            If _logLevel >= 4 Then AppendLog($"[SafeMpzMul] §39 column-group fast path (mA=mB={mA:N0}){vbCrLf}")
            ' Per-column: base product index and list of additional product indices to add first
            Dim _col_base As Integer() = {0, 1, 2, 5, 8}
            Dim _col_extra As Integer()() = New Integer()() {
                New Integer() {},
                New Integer() {3},
                New Integer() {4, 6},
                New Integer() {7},
                New Integer() {}
            }
            ' §246 (issue #45 Layer 1): parallel per-column add-chains.  Each column's adds
            ' target a DISTINCT prods slot (col0:{0} col1:{1,3} col2:{2,4,6} col3:{5,7}
            ' col4:{8} — disjoint), the GmpNativeAlloc pool is per-CPU-safe, and the pre-grow
            ' below prevents any GMP realloc — so the 5 columns' add-chains are independent and
            ' run concurrently.  Gated on _smmDop>1: when this §39 call is itself a nested
            ' sub-product, §238 has set _smmDop=1 and we keep the plain serial path (no nested
            ' parallel blow-up, no Parallel.For overhead on the hot 1B path).  The shift +
            ' accumulate that follows stays SERIAL (shared _sv_shifted_hdr + ordered accumPtr).
            Dim _doColAdd As Action(Of Integer) =
                Sub(_colA As Integer)
                    Dim _bkA As Integer = _col_base(_colA)
                    ' §Phase3ColAdd: pre-grow prods(_bkA) before each add so GMP never calls its
                    ' realloc callback (a VirtualAlloc+memcpy+VirtualFree that can fail → abort()).
                    For Each _ak As Integer In _col_extra(_colA)
                        Dim _bk_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_bkA).Pointer, 4))
                        Dim _ak_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_ak).Pointer, 4))
                        Dim _needed As Integer = System.Math.Max(_bk_sz, _ak_sz) + 2  ' +2 for carry safety
                        Dim _bk_alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(prods(_bkA).Pointer, 0)
                        If _bk_alloc < _needed Then
                            Dim _oldBuf As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(_bkA).Pointer, 8))
                            Dim _oldBytes As Long = CLng(_bk_alloc) * 8L
                            Dim _newBytes As Long = CLng(_needed) * 8L
                            Dim _newBuf As IntPtr = GmpNativeAlloc_PoolGet(_newBytes)
                            If _newBuf = IntPtr.Zero Then
                                Throw New OutOfMemoryException($"SafeMpzMul §39 pre-grow: GmpNativeAlloc_PoolGet failed ({_newBytes \ 1048576L} MB)")
                            End If
                            CopyMemory(_newBuf, _oldBuf, New UIntPtr(CULng(_bk_sz) * 8UL))
                            GmpNativeAlloc_FreeRaw(_oldBuf, _oldBytes)
                            Runtime.InteropServices.Marshal.WriteInt32(prods(_bkA).Pointer, 0, _needed)
                            Runtime.InteropServices.Marshal.WriteInt64(prods(_bkA).Pointer, 8, _newBuf.ToInt64())
                        End If
                        GmpRaw_add(prods(_bkA).Pointer, prods(_bkA).Pointer, prods(_ak).Pointer)
                        GmpRaw_clear(prods(_ak).Pointer)
                        Dim _tmp_ak = prods(_ak).Pointer : prods(_ak).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_tmp_ak)
                    Next
                End Sub
            If _smmDop > 1 Then
                Dim _colOpts As New System.Threading.Tasks.ParallelOptions() With {.MaxDegreeOfParallelism = System.Math.Min(5, _smmDop)}
                System.Threading.Tasks.Parallel.For(0, 5, _colOpts, _doColAdd)
            Else
                For _colS As Integer = 0 To 4 : _doColAdd(_colS) : Next
            End If

            ' §256 (#45 Layer 2/3): mpn OFFSET-accumulation — replaces the per-column mul_2exp shift
            ' (+ shared _sv_shifted_hdr buffer + ordered GmpRaw_add) with a direct mpn_add at limb
            ' offset col·mA.  bitsA = mA·64, so a column's shift of col·bitsA bits is exactly col·mA
            ' LIMBS (no sub-limb shift) — the column-sum adds straight into accumBuf at that offset
            ' (the chunked-grid pattern).  Eliminates the O(result) shift copy + the ~1.2 GB shifted
            ' buffer; accumulate is now O(Σ col-sum) not O(5×result).  accumBuf is zeroed first (the
            ' offset adds read the gaps).  Shifts are memory-bandwidth-bound, so this beats the
            ' originally-planned "parallelise the shifts" (which would just contend on RAM bandwidth).
            ZeroMemory(accumBuf, New UIntPtr(CULng(CLng(_resultLimbs) * 8L)))
            For _col As Integer = 0 To 4
                Dim _bk As Integer = _col_base(_col)
                Dim _sv_bk As IntPtr = prods(_bk).Pointer
                Dim _bkSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_bk, 4))
                If _bkSz > 0 Then
                    Dim _bkD As Long = Runtime.InteropServices.Marshal.ReadInt64(_sv_bk, 8)
                    Dim _off As Long = CLng(_col) * CLng(mA)   ' col·bitsA bits = col·mA limbs
                    GmpRaw_mpn_add(New IntPtr(accumBuf.ToInt64() + _off * 8L), New IntPtr(accumBuf.ToInt64() + _off * 8L),
                                   CInt(CLng(_resultLimbs) - _off), New IntPtr(_bkD), _bkSz)
                End If
                GmpRaw_clear(_sv_bk)
                prods(_bk).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_sv_bk)
            Next _col
            ' Normalize accumPtr _mp_size (highest nonzero limb) — mpn_add wrote raw limbs into accumBuf.
            Dim _accSz As Integer = CInt(_resultLimbs)
            While _accSz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(accumBuf.ToInt64() + CLng(_accSz - 1) * 8L)) = 0L
                _accSz -= 1
            End While
            Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 4, _accSz)
        Else
            ' §222 (issue #60, 2026-05-22): parallel shift pre-pass for asymmetric §gen path.
            '
            ' The original loop did serial: for each k, shift prods(k) by ki·bitsA+kj·bitsB
            ' into a single shared _sv_shifted_hdr buffer, then add to accumPtr. The shifts
            ' (mpz_mul_2exp) are O(N) memmoves and don't overlap across k's because they
            ' all write into the same shared buffer.
            '
            ' Phase 2 win: allocate 9 separate shifted buffers and compute all shifts in
            ' parallel. The serial reduction below (k=0..8 add into accumPtr) is preserved
            ' EXACTLY — same diagnostics, same accumPtr update order — only the shift work
            ' is parallelized. Memory cost: 9 buffers × max-shifted-size ≈ 5.4 GB at 5B,
            ' ~1.1 GB at 1B (well within 64 GB budget).
            '
            ' Gated on _smmDop > 1 AND szA+szB > 500M limbs: at 1B operand scale
            ' (szA+szB ~192M for q×b) the per-call overhead of pre-allocating 9 buffers +
            ' Parallel.For startup × 800+ §gen invocations OUTWEIGHS the parallel-shift gain.
            ' Measured 2026-05-22: §222 ON at 1M-limb threshold made 1B q×b ~5% slower
            ' (8m 57s vs 8m 32s baseline). At 5B-scale operands (szA+szB ~500M+) the shift
            ' cost grows as O(N) while the overhead stays fixed, so the gain dominates per
            ' the #60 issue body's 5-10% projection. The 500M threshold ensures §222 fires
            ' only at the top-level a×r / q×b calls in a 5B run (where it pays off) and
            ' stays inactive at 1B (where it doesn't).
            Dim _useS222 As Boolean = (_smmDop > 1) AndAlso (CLng(szA) + CLng(szB) > 500_000_000L)
            Dim _shifted222 As IntPtr() = Nothing
            If _useS222 Then
                _shifted222 = New IntPtr(8) {}
                ' Worst-case shifted size for each k: prods(k) is at most szA+szB+1 limbs;
                ' shift = (ki·bitsA + kj·bitsB) bits = (ki·mA + kj·mB) limbs. So shifted_k
                ' has at most szA+szB+1 + ki·mA + kj·mB limbs. PreAlloc each one to bypass
                ' GMP's 33.5M-limb realloc abort (same §216a-class issue as §218).
                For _k222 As Integer = 0 To 8
                    _shifted222(_k222) = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_shifted222(_k222))
                Next
                Dim _gen222_opts As New System.Threading.Tasks.ParallelOptions() With {.MaxDegreeOfParallelism = System.Math.Min(9, _smmDop)}
                System.Threading.Tasks.Parallel.For(0, 9, _gen222_opts, Sub(k As Integer)
                    Dim ki As Integer = k \ 3
                    Dim kj As Integer = k Mod 3
                    Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
                    If shiftBits = 0UL Then
                        ' k=0: no shift; copy prods(0) value into shifted222(0) so the
                        ' serial reduction below has a uniform API (read from shifted222(k)).
                        Dim _szP0 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(0).Pointer, 4))
                        Dim _p0Wrap As New mpz_t() With {.Pointer = _shifted222(0)}
                        PreAllocMpzToLimbs(_p0Wrap, CLng(_szP0) + 2L)
                        GmpRaw_set(_shifted222(0), prods(0).Pointer)
                    Else
                        ' Pre-alloc to (prods(k) size + shift_limbs + 2) limbs.
                        Dim _szPk As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(k).Pointer, 4))
                        Dim _shiftLimbs As Long = CLng(shiftBits \ 64UL) + 1L
                        Dim _shWrap As New mpz_t() With {.Pointer = _shifted222(k)}
                        PreAllocMpzToLimbs(_shWrap, CLng(_szPk) + _shiftLimbs + 2L)
                        Dim _shiftSrc As IntPtr = prods(k).Pointer
                        Dim _shiftRem As ULong = shiftBits
                        While _shiftRem > 0UL
                            Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                            GmpRaw_mul_2exp(_shifted222(k), _shiftSrc, _chunk)
                            _shiftSrc = _shifted222(k)
                            _shiftRem -= CULng(_chunk)
                        End While
                    End If
                End Sub)
                If _logLevel >= 3 Then AppendLog($"[SafeMpzMul§222] parallel shift pre-pass complete (DOP={_gen222_opts.MaxDegreeOfParallelism}){vbCrLf}", 3)
            End If

            ' §23/§90: Original per-product accumulation for asymmetric case (mA ≠ mB).
            ' When §222 is active, the inline shift is REPLACED by a read from _shifted222(k);
            ' the serial add + all diagnostics are preserved unchanged.
            For k As Integer = 0 To 8
                Dim ki As Integer = k \ 3
                Dim kj As Integer = k Mod 3
                Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
                Dim _sv_prod As IntPtr = prods(k).Pointer
                Dim _logPre As Integer = If(_logLevel >= 5, System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_prod, 4)), 0)   ' §252 (#95): per-sub-product spam → level 5
                If _logLevel >= 5 Then
                    ' §215: Int32 overflow in offset arithmetic.  _logPre is Integer; (_logPre-1)*8
                    ' overflows Int32 when _logPre > 2^28 = 268,435,456 limbs.  At the topmost a×r
                    ' recursion level for 5B (szA=998M × szB=259M), prods(k) reaches 419M limbs;
                    ' (419,352,782)*8 = 3,354,822,256 wraps to -940,145,040 → AccessViolation in
                    ' Marshal.ReadInt64.  Fix: compute the absolute limb address in 64-bit, then
                    ' read at offset 0.  Same fix applied at §SafeMpzDiv a/ar/b log sites.
                    Dim _prodDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                    Dim _prodTop As Long = If(_logPre >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_prodDPtr.ToInt64() + (CLng(_logPre) - 1L) * 8L), 0), 0L)
                    Dim _prodTop2 As Long = If(_logPre >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_prodDPtr.ToInt64() + (CLng(_logPre) - 2L) * 8L), 0), 0L)
                    AppendLog($"[SafeMpzMul§gen] k={k} ki={ki} kj={kj} shift={shiftBits:N0} szProd={_logPre:N0} top2=[{_prodTop:X16} {_prodTop2:X16}]{vbCrLf}")
                    ' §111: For the final a*r call only, log product limb at the known error position.
                    If k = 8 AndAlso szA = 43750001 AndAlso szB = 21875001 Then
                        Const _EL As Long = 20904662L
                        Dim _pDPtrErr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _pErrL As Long = If(_logPre > CInt(_EL), Runtime.InteropServices.Marshal.ReadInt64(_pDPtrErr, CInt(_EL * 8L)), 0L)
                        Dim _pErrL1 As Long = If(_logPre > CInt(_EL + 1L), Runtime.InteropServices.Marshal.ReadInt64(_pDPtrErr, CInt((_EL + 1L) * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§111] k=8 prod[{_EL:N0}]={_pErrL:X16} prod[{_EL+1:N0}]={_pErrL1:X16}{vbCrLf}")
                    End If
                    ' §130: q*b A2*B2 — log product limb that maps to accum[42,779,664].
                    ' Only k=8 (A2×B2, shift=1,866,666,752 bits=29,166,668 limbs) reaches that position:
                    '   accum[42,779,664] = prods(8)[42,779,664 - 29,166,668] = prods(8)[13,612,996]
                    If k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                        Const _PL130 As Long = 13612996L
                        Dim _p130dPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _p130v As Long = If(_PL130 < CLng(_logPre), Runtime.InteropServices.Marshal.ReadInt64(_p130dPtr, CInt(_PL130 * 8L)), 0L)
                        Dim _p130v1 As Long = If(_PL130 + 1L < CLng(_logPre), Runtime.InteropServices.Marshal.ReadInt64(_p130dPtr, CInt((_PL130 + 1L) * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§130] k=8 A2*B2 prod[{_PL130:N0}]={_p130v:X16} prod[{_PL130+1:N0}]={_p130v1:X16} szProd={_logPre:N0}{vbCrLf}")
                    End If
                    ' §5B-sub: at the outer 175M × 87.5M call only, log each of the 9 sub-products'
                    ' bot/mid/top limbs AND verify prods(k)[0] = A_i[0] * B_j[0] mod 2^64 (exact).
                    ' Mismatch ⇒ that sub-product (the recursive SafeMpzMul call for A_i × B_j)
                    ' produced a wrong bottom limb — narrows the 5B bug to a specific k.
                    If szA = 175000001 AndAlso szB = 87500001 AndAlso _logPre > 0 Then
                        Dim _sp5DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _sp5Bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, 0))
                        Dim _sp5Bot2 As ULong = If(_logPre >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, 8)), 0UL)
                        Dim _sp5Mid As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre \ 2) * 8L)))
                        Dim _sp5Top2 As ULong = If(_logPre >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre - 2) * 8L))), 0UL)
                        Dim _sp5Top As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre - 1) * 8L)))
                        ' Read A_i[0] and B_j[0] via opA/opB data + ki*mA, kj*mB offsets.
                        Dim _opA_d5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8))
                        Dim _opB_d5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8))
                        Dim _ai5_bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt(CLng(ki) * CLng(mA) * 8L)))
                        Dim _bj5_bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt(CLng(kj) * CLng(mB) * 8L)))
                        Dim _exp5SpBot As ULong = _ai5_bot * _bj5_bot
                        AppendLog($"[SafeMpzMul§5B-sub k={k} ki={ki} kj={kj}] szProd={_logPre:N0} bot=[{_sp5Bot:X16} {_sp5Bot2:X16}] mid[{_logPre\2:N0}]={_sp5Mid:X16} top=[{_sp5Top2:X16} {_sp5Top:X16}]{vbCrLf}", 5)   ' §252 (#95)
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verify] A_{ki}[0]={_ai5_bot:X16} B_{kj}[0]={_bj5_bot:X16} (A_{ki}*B_{kj})_lo={_exp5SpBot:X16} actual prod[0]={_sp5Bot:X16} match={(_exp5SpBot = _sp5Bot)}{vbCrLf}", 5)   ' §252 (#95)
                        ' §5B-sub-1: verify prods(k)[1].  prods[0] receives only one term
                        ' (A_i[0]*B_j[0].lo) so its carry to limb 1 is 0.  prods[1] receives:
                        '   hi(A_i[0]*B_j[0]) + lo(A_i[0]*B_j[1]) + lo(A_i[1]*B_j[0])
                        ' all reduced mod 2^64.  Mismatch ⇒ that prods(k) is wrong starting
                        ' from limb 1, narrowing the bug to a specific recursive sub-product.
                        If _logPre >= 2 Then
                            Dim _ai51_1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt((CLng(ki) * CLng(mA) + 1L) * 8L)))
                            Dim _bj51_1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt((CLng(kj) * CLng(mB) + 1L) * 8L)))
                            Dim _hi00_lo As ULong = 0UL
                            Dim _hi00 As ULong = System.Math.BigMul(_ai5_bot, _bj5_bot, _hi00_lo)
                            Dim _lo01 As ULong = _ai5_bot * _bj51_1
                            Dim _lo10 As ULong = _ai51_1 * _bj5_bot
                            Dim _expProd1 As ULong = _hi00 + _lo01 + _lo10
                            Dim _actProd1 As ULong = _sp5Bot2
                            AppendLog($"[SafeMpzMul§5B-sub k={k} verify1] A_{ki}[1]={_ai51_1:X16} B_{kj}[1]={_bj51_1:X16} hi00={_hi00:X16} lo01={_lo01:X16} lo10={_lo10:X16}  expected prod[1]={_expProd1:X16}  actual prod[1]={_actProd1:X16}  match={(_actProd1 = _expProd1)}{vbCrLf}", 5)   ' §252 (#95)
                        End If
                        ' §5B-sub-T: verify prods(k) TOP limb ≈ hi(A_i[topA] * B_j[topB]).
                        ' The top limb of A_i × B_j is dominated by hi(A_i[topA]*B_j[topB])
                        ' plus a tiny carry from below (typically 0..2 for random data).
                        ' If mpz_mul stripped a leading zero the actual size will be one less
                        ' than expected and actTop ≈ lo(A_i[topA]*B_j[topB]).  A "wildly off"
                        ' sub-product (the working hypothesis) will diverge by a huge amount,
                        ' pinpointing which k's recursive SafeMpzMul produced a wrong top.
                        Dim _szAi As ULong = If(ki = 2, CULng(szA) - 2UL * mA, mA)
                        Dim _szBj As ULong = If(kj = 2, CULng(szB) - 2UL * mB, mB)
                        Dim _topAOff As Long = (CLng(ki) * CLng(mA) + CLng(_szAi) - 1L) * 8L
                        Dim _topBOff As Long = (CLng(kj) * CLng(mB) + CLng(_szBj) - 1L) * 8L
                        Dim _ai5_top As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt(_topAOff)))
                        Dim _bj5_top2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt(_topBOff)))
                        Dim _expTopLo As ULong = 0UL
                        Dim _expTopHi As ULong = System.Math.BigMul(_ai5_top, _bj5_top2, _expTopLo)
                        Dim _expSzProd As ULong = _szAi + _szBj
                        Dim _expTopForCmp As ULong = If(_expSzProd = CULng(_logPre), _expTopHi, _expTopLo)
                        Dim _topDiff As ULong = _sp5Top - _expTopForCmp
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verifyT] A_{ki}[{_szAi - 1UL:N0}]={_ai5_top:X16} B_{kj}[{_szBj - 1UL:N0}]={_bj5_top2:X16} expHi={_expTopHi:X16} expLo={_expTopLo:X16} expSzProd={_expSzProd:N0} actSzProd={_logPre:N0} actTop={_sp5Top:X16} actTop-1={_sp5Top2:X16} cmpExp={_expTopForCmp:X16} diff(act-exp)={_topDiff:X16}{vbCrLf}", 5)   ' §252 (#95)
                    End If
                End If
                ' §222 (issue #60): use the parallel-pre-shifted buffer when available.
                Dim _shiftedForK222 As IntPtr = If(_useS222, _shifted222(k), _sv_shifted_hdr)
                If shiftBits = 0UL Then
                    ' k=0 has shift=0. When §222 is active, _shifted222(0) holds a COPY of
                    ' prods(0) (we used GmpRaw_set in the pre-pass); adding it is equivalent
                    ' to adding _sv_prod directly. When §222 is OFF, fall through to the
                    ' original direct-add of _sv_prod.
                    If _useS222 Then
                        GmpRaw_add(accumPtr, accumPtr, _shifted222(0))
                    Else
                        GmpRaw_add(accumPtr, accumPtr, _sv_prod)
                    End If
                    If _logLevel >= 5 Then   ' §252 (#95): per-sub-product accumulation spam → level 5
                        Dim _accumSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        AppendLog($"[SafeMpzMul§gen] k={k} shift=0 szProd={_logPre:N0} accumSz={_accumSz:N0}{vbCrLf}")
                    End If
                Else
                    If Not _useS222 Then
                        ' Original inline shift path — runs when §222 pre-pass was skipped
                        ' (caller DOP=1 or operand too small to amortise the parallel cost).
                        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
                        Dim _shiftSrc As IntPtr = _sv_prod
                        Dim _shiftRem As ULong = shiftBits
                        While _shiftRem > 0UL
                            Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                            GmpRaw_mul_2exp(_sv_shifted_hdr, _shiftSrc, _chunk)
                            _shiftSrc = _sv_shifted_hdr
                            _shiftRem -= CULng(_chunk)
                        End While
                    End If
                    ' §150: q*b pre-add check — verify accum[42779664]=0 before adding k=8.
                    ' Only k=8 (shift=29166668 limbs) can reach position 42779664; no k<8 does.
                    ' A nonzero pre-k8 value would reveal an unexpected earlier sub-product bug.
                    If _logLevel >= 2 AndAlso k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                        Dim _pre150sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        Dim _pre150DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                        Dim _pre150v As Long = If(42779664L < CLng(_pre150sz), Runtime.InteropServices.Marshal.ReadInt64(_pre150DPtr, CInt(42779664L * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§150] pre-k8-add accum[42779664]={_pre150v:X16} (expect 0000000000000000){vbCrLf}")
                    End If
                    GmpRaw_add(accumPtr, accumPtr, _shiftedForK222)
                    If _logLevel >= 5 Then   ' §252 (#95): per-sub-product accumulation spam → level 5
                        Dim _shiftedSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_shiftedForK222, 4))
                        Dim _accumSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        AppendLog($"[SafeMpzMul§gen] k={k} shift={shiftBits:N0} szProd={_logPre:N0} szShifted={_shiftedSz:N0} accumSz={_accumSz:N0}{vbCrLf}")
                        ' §111: For the final a*r call only, log accum at the known error position.
                        If k = 8 AndAlso szA = 43750001 AndAlso szB = 21875001 Then
                            Const _AL As Long = 64654664L
                            Dim _accDiagSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                            Dim _accDiagDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                            Dim _aErrL As Long = If(_AL < CLng(_accDiagSz), Runtime.InteropServices.Marshal.ReadInt64(_accDiagDPtr, CInt(_AL * 8L)), 0L)
                            Dim _aErrL1 As Long = If(_AL + 1L < CLng(_accDiagSz), Runtime.InteropServices.Marshal.ReadInt64(_accDiagDPtr, CInt((_AL + 1L) * 8L)), 0L)
                            AppendLog($"[SafeMpzMul§111] k=8 accum[{_AL:N0}]={_aErrL:X16} accum[{_AL+1:N0}]={_aErrL1:X16}{vbCrLf}")
                        End If
                        ' §130: log accum[42,779,664] after k=8 for q*b call (only contributor to that limb).
                        If k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                            Const _AL130 As Long = 42779664L
                            Dim _acc130sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                            Dim _acc130dPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                            Dim _acc130v As Long = If(_AL130 < CLng(_acc130sz), Runtime.InteropServices.Marshal.ReadInt64(_acc130dPtr, CInt(_AL130 * 8L)), 0L)
                            Dim _acc130v1 As Long = If(_AL130 + 1L < CLng(_acc130sz), Runtime.InteropServices.Marshal.ReadInt64(_acc130dPtr, CInt((_AL130 + 1L) * 8L)), 0L)
                            AppendLog($"[SafeMpzMul§130] k=8 accum[{_AL130:N0}]={_acc130v:X16} accum[{_AL130+1:N0}]={_acc130v1:X16} accumSz={_acc130sz:N0}{vbCrLf}")
                        End If
                    End If
                End If
                ' §5B-c3: at the outer 175M × 87.5M call, log accum[218,750,001] (and neighbours)
                ' after each k's accumulation.  Only k=7 (shift=145.83M limbs) and k=8 (shift=175.0M
                ' limbs) reach that index; for k<7 the value should remain zero.  The k that first
                ' introduces the wrong value pinpoints whether the bug is in prods(7)'s middle,
                ' prods(8)'s middle, or the shift+add at one of those k's.
                If _logLevel >= 2 AndAlso szA = 175000001 AndAlso szB = 87500001 Then
                    Const _IDX_C3 As Long = 218750001L
                    Dim _accC3sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                    Dim _accC3DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                    Dim _accC3v0 As ULong = If(_IDX_C3 - 1L < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt((_IDX_C3 - 1L) * 8L))), 0UL)
                    Dim _accC3v1 As ULong = If(_IDX_C3 < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt(_IDX_C3 * 8L))), 0UL)
                    Dim _accC3v2 As ULong = If(_IDX_C3 + 1L < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt((_IDX_C3 + 1L) * 8L))), 0UL)
                    AppendLog($"[SafeMpzMul§5B-c3 k={k}] post-add accum[{_IDX_C3 - 1L:N0}]={_accC3v0:X16} accum[{_IDX_C3:N0}]={_accC3v1:X16} accum[{_IDX_C3 + 1L:N0}]={_accC3v2:X16} accumSz={_accC3sz:N0}{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' §5B-d-L2: Level-2 recursive C-3 — at the inner SafeMpzMul calls that produce
                ' prods(6/7/8) of the outer 175M × 87.5M call (gated by szA=58,333,333 ∧
                ' szB=29,166,667 — the size of A_2 × any B_j), log accum at the two suspect
                ' indices after each k=0..8 sub-product accumulation.
                '   prods(7) suspect: accum[72,916,666] (the limb that became the wrong outer
                '     prods(7)[72,916,666] = 3E924C7A243168E4 in run 9).
                '   prods(8) suspect: accum[43,749,999] (the limb that contributes to outer
                '     ar[218,750,001] from prods(8) at level 1).
                ' Fingerprint via opB[0] (cached pre-loop):
                '   B_0[0]=88638C785832DAFF (prods(6)), B_1[0]=4B08FAE8DCA50441 (prods(7)),
                '   B_2[0]=0706751D8688C2D3 (prods(8)).
                ' The k' that first introduces a wrong value at the matching index pinpoints
                ' which level-3 sub-product (or which level-2 shift+add step) is the culprit.
                If _logLevel >= 2 AndAlso szA = 58333333 AndAlso szB = 29166667 Then
                    Const _IDX_D_P7 As Long = 72916666L
                    Const _IDX_D_P8 As Long = 43749999L
                    Dim _accDsz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                    Dim _accDDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                    Dim _accD7 As ULong = If(_IDX_D_P7 < CLng(_accDsz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accDDPtr, CInt(_IDX_D_P7 * 8L))), 0UL)
                    Dim _accD8 As ULong = If(_IDX_D_P8 < CLng(_accDsz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accDDPtr, CInt(_IDX_D_P8 * 8L))), 0UL)
                    Dim _opBd_d2 As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)
                    Dim _opB0_d2 As ULong = If(_opBd_d2 <> 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_opBd_d2), 0)), 0UL)
                    AppendLog($"[SafeMpzMul§5B-d-L2 k={k} opB[0]={_opB0_d2:X16}] post-add accum[{_IDX_D_P7:N0}]={_accD7:X16} accum[{_IDX_D_P8:N0}]={_accD8:X16} accumSz={_accDsz:N0}{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' §212 (2026-05-15): depth-0 RAM diagnostics for top-level §gen calls.
                ' Gated on szA + szB > 800M limbs — at 5B scale this fires ONLY at the
                ' outermost a×r (998M × 259M = 1.26B total).  The 5/15 crash was an
                ' AccessViolation in KernelBase during k=1's shift+add at exactly this depth;
                ' working-set + private-memory + accumPtr alloc-headroom logged here lets us
                ' confirm whether the next crash (if any) is heap pressure vs mpz_t size limit
                ' vs allocator corruption.
                If CLng(szA) + CLng(szB) > 800_000_000L Then
                    Try
                        Dim _212proc As System.Diagnostics.Process = System.Diagnostics.Process.GetCurrentProcess()
                        Dim _212ws As Long = _212proc.WorkingSet64
                        Dim _212priv As Long = _212proc.PrivateMemorySize64
                        Dim _212accSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        Dim _212accAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 0)
                        AppendLog($"[SafeMpzMul§212] depth-0 k={k} END  szA={szA:N0} szB={szB:N0} WS={_212ws \ 1048576L:N0}MB Priv={_212priv \ 1048576L:N0}MB accumSz={_212accSz:N0} accumAlloc={_212accAlloc:N0}{vbCrLf}", 5)   ' §252 (#95)
                    Catch _212ex As Exception
                        AppendLog($"[SafeMpzMul§212] depth-0 k={k} diag failed: {_212ex.Message}{vbCrLf}", 5)   ' §252 (#95)
                    End Try
                End If
                GmpRaw_clear(prods(k).Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(prods(k).Pointer)
            Next k

            ' §222 (issue #60): free the 9 parallel-shifted buffers, if used.
            If _useS222 Then
                For _k222free As Integer = 0 To 8
                    GmpRaw_clear(_shifted222(_k222free))
                    Runtime.InteropServices.Marshal.FreeHGlobal(_shifted222(_k222free))
                Next
            End If
        End If
        ' §23: Free the shared buffer once, then re-init shifted with a fresh 1-limb stub
        ' so the final GmpRaw_clear below frees it cleanly without double-freeing _sharedSjBuf.
        ' §25: raw re-init — zero the struct fields so GmpRaw_init allocates a fresh limb buffer.
        VirtualFree(_sharedSjBuf, UIntPtr.Zero, MEM_RELEASE)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 0, 0)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
        Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted_hdr, 8, 0L)
        GmpRaw_init(_sv_shifted_hdr)   ' allocates fresh 1-limb buffer; freed by GmpRaw_clear below
        ' §175: Do NOT re-read result.Pointer here.  Math.Gmp.Native corrupts mpz_t.Pointer
        ' for locally-scoped objects during recursive SafeMpzMul calls (§78), so result.Pointer
        ' may point to a wrong struct after sub-product computation.  The original savedResultPtr
        ' (line 2260) and local accumPtr (line 2284) are plain IntPtr locals — immune to
        ' managed-wrapper corruption — and are still correct here (accumulation loop has no
        ' inner SafeMpzMul calls).  Re-reading was unnecessary and caused result.Pointer to be
        ' restored to a corrupted address, making rSq.Pointer point at the wrong struct and
        ' rSq bot/lower-limbs to appear as zero (§121 symptom), giving wrong Newton r.

        ' Copy accumPtr struct to savedResultPtr, then free the 16-byte accumPtr header.
        ' accumBuf ownership transfers to result (via savedResultPtr._mp_d); do NOT free it here.
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 0))
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
        result.Pointer = savedResultPtr
        Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
        accumPtr = IntPtr.Zero
        If _logLevel >= 4 Then AppendLog(
            $"[SafeMpzMul] done: szA={szA:N0} szB={szB:N0} → {GmpRaw_sizeinbase(savedResultPtr, 10):N0} digits{vbCrLf}")

        ' §42: negate via raw P/Invoke so result.Pointer corruption cannot affect the call.
        If resultSign < 0 Then GmpRaw_neg(savedResultPtr, savedResultPtr)

        ' §59: prods(0..8) are already cleared inside the accumulation loop.
        GmpRaw_clear(shifted.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(shifted.Pointer)
        ' §90: zero-copy pieces — only free the 16-byte struct headers; _mp_d aliases opA/opB so
        ' GmpRaw_clear must NOT be called (it would free opA/opB's limb buffer — catastrophic).
        ' Null out Pointer before FreeHGlobal: mpz_t's GC finalizer calls mpz_clear if Pointer≠null,
        ' which would pass opA/opB's _mp_d to GmpFreeFunc — a concurrent double-free.
        Dim _A0p = A0.Pointer : A0.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A0p)
        Dim _A1p = A1.Pointer : A1.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A1p)
        Dim _A2p = A2.Pointer : A2.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A2p)
        Dim _B0p = B0.Pointer : B0.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B0p)
        Dim _B1p = B1.Pointer : B1.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B1p)
        Dim _B2p = B2.Pointer : B2.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B2p)
        AppendLog(
            $"[SafeMpzMul] cleared: szA={szA:N0} szB={szB:N0}{vbCrLf}")
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    ' §100 BigShiftRight / BigShiftLeft / SafeMpzReciprocal / SafeMpzDiv / SafeMpzSqrt
    '
    ' GMP's mpz_sqrt (and the division it uses internally) calls mpn_mul_fft.
    ' That routine has a static FFT-size table whose index overflows when the
    ' operand exceeds ~33 M limbs — exactly the crash seen at 5 B digits when
    ' computing sqrt(10^10,000,000,000 * 10005).
    '
    ' Fix: implement SafeMpzSqrt via Newton iteration that routes every large
    ' multiplication through SafeMpzMul (which already handles arbitrarily large
    ' operands via 3×3 recursive splitting).  Division is handled by
    ' SafeMpzDiv (Barrett-style Newton reciprocal), which also uses only
    ' SafeMpzMul internally.
    ' ════════════════════════════════════════════════════════════════════════

    ' Compute floor(op / 2^bits) → rop.  Handles bits > UInt32.Max.  rop may alias op.
    ' Pre-allocate an mpz_t's limb buffer to at least neededLimbs without going
    ' through GmpReallocFunc.  Required before any GMP operation that would trigger
    ' a Small→Large realloc (CRT-alloc'd 1-limb init buffer → VirtualAlloc'd multi-GB
    ' buffer), because GMP calls __gmp_realloc which reaches GmpReallocFunc BEFORE
    ' writing anything to rop — if that path crashes the diagnostic log shows nothing
    ' between "about to call" and the process exit.
    '
    ' Pattern: identical to the tmpHigh / mpQ1 / mpQ2 pre-alloc blocks in ComputePiGMP.
    ' If neededLimbs is already satisfied, or neededBytes < GMP_LARGE_THRESHOLD, returns
    ' immediately (GmpReallocFunc handles small→small transitions fine).
    ' Pre-allocate m's limb buffer to neededLimbs via our pool/VirtualAlloc so that
    ' GMP's MPZ_REALLOC macro skips _mpz_realloc (whose overflow check aborts when
    ' new_alloc > INT_MAX/8 ≈ 268 M limbs on Windows 64-bit with 32-bit mp_size_t).
    '
    ' Existing limb data is copied into the new buffer so this is safe for the
    ' aliased case (rop.Pointer == op.Pointer) used by BigShiftLeft.  When m has
    ' no data (_mp_size = 0), the copy is a zero-byte no-op.
    Private Shared Sub PreAllocMpzToLimbs(m As mpz_t, neededLimbs As Long)
        If neededLimbs <= 0L Then Return
        Dim currentAlloc As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(m.Pointer, 0)))
        If currentAlloc >= neededLimbs Then Return   ' already large enough

        Dim neededBytes As Long = neededLimbs * 8L
        If neededBytes < GMP_LARGE_THRESHOLD Then Return  ' small; GmpReallocFunc handles it

        Dim currentBuf As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(m.Pointer, 8))
        Dim currentBytes As Long = currentAlloc * 8L

        ' §30: Use native pool for allocation. GmpNativeAlloc_PoolGet handles all sizes.
        Dim newBuf As IntPtr = GmpNativeAlloc_PoolGet(neededBytes)
        If newBuf = IntPtr.Zero Then
            AppendLog($"[PreAlloc] PoolGet({neededBytes:N0} B) FAILED — will rely on GmpReallocFunc{vbCrLf}")
            Return
        End If

        ' Copy any existing limb data into the new buffer (needed for aliased BigShiftLeft).
        Dim szAbsLimbs As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(m.Pointer, 4)))
        Dim dataBytes As Long = szAbsLimbs * 8L
        If dataBytes > 0L Then
            CopyMemory(newBuf, currentBuf, New UIntPtr(CULng(dataBytes)))
        End If

        ' §30: All pool buffers are VirtualAlloc-backed; GmpNativeAlloc_FreeRaw handles all cases.
        GmpNativeAlloc_FreeRaw(currentBuf, currentBytes)

        Runtime.InteropServices.Marshal.WriteInt32(m.Pointer, 0, CInt(neededLimbs))
        Runtime.InteropServices.Marshal.WriteInt64(m.Pointer, 8, newBuf.ToInt64())
        AppendLog($"[PreAlloc] {neededLimbs:N0} limbs ({neededBytes \ 1048576L:N0} MB) OK{vbCrLf}")
    End Sub

    ' Compute floor(op / 2^bits) → rop.  Handles bits > UInt32.Max.  rop may alias op.
    '
    ' §101 fix: GMP's mpz_tdiv_q_2exp calls _mpz_realloc before writing any data.
    ' When rop has a small (CRT-alloc'd) 1-limb buffer and op is multi-GB, that
    ' realloc is a Small→Large transition that triggers GmpReallocFunc.  Empirically
    ' GMP crashes before our callback fires (no S→L log line appears).  Fix: pre-alloc
    ' rop to hold the first chunk's result using PreAllocMpzToLimbs (direct VirtualAlloc,
    ' bypassing GmpReallocFunc entirely).  Subsequent chunks are always Large→Large.
    Private Shared Sub BigShiftRight(rop As mpz_t, op As mpz_t, bits As Long)
        If bits <= 0L Then
            If rop.Pointer <> op.Pointer Then GmpRaw_set(rop.Pointer, op.Pointer)  ' §35
            Return
        End If

        ' Pre-alloc rop for the first chunk result before calling GmpRaw_tdiv_q_2exp.
        Dim opLimbs As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(op.Pointer, 4)))
        Dim firstChunkLimbs As Long = System.Math.Min(bits, 2_100_000_000L) \ 64L
        Dim firstResultLimbs As Long = opLimbs - firstChunkLimbs + 1L   ' +1 safety margin
        If firstResultLimbs > 0L Then PreAllocMpzToLimbs(rop, firstResultLimbs)

        Dim src As IntPtr = op.Pointer
        Dim dst As IntPtr = rop.Pointer
        Dim bitsLeft As Long = bits
        Do
            Dim chunk As UInteger = CUInt(System.Math.Min(bitsLeft, 2_100_000_000L))
            If _logLevel >= 5 Then AppendLog($"[BSR§129] dst={dst.ToInt64():X16} src={src.ToInt64():X16} chunk={chunk:N0} bitsLeft={bitsLeft:N0} rop_alloc={Runtime.InteropServices.Marshal.ReadInt32(dst, 0):N0} rop_sz={Runtime.InteropServices.Marshal.ReadInt32(dst, 4):N0} src_sz={Runtime.InteropServices.Marshal.ReadInt32(src, 4):N0}{vbCrLf}")
            GmpRaw_tdiv_q_2exp(dst, src, chunk)
            If _logLevel >= 5 Then AppendLog($"[BSR§129] done chunk={chunk:N0} rop_sz={Runtime.InteropServices.Marshal.ReadInt32(dst, 4):N0}{vbCrLf}")
            src = dst
            bitsLeft -= CLng(chunk)
        Loop While bitsLeft > 0L
    End Sub

    ' Compute op * 2^bits → rop.  Handles bits > UInt32.Max.  rop may alias op.
    '
    ' §102 / §105 fix: GMP's _mpz_realloc aborts with "overflow" when new_alloc
    ' exceeds INT_MAX/GMP_NUMB_BITS = 33,554,431 limbs on Windows 64-bit (32-bit
    ' mp_size_t).  This check fires BEFORE our GmpReallocFunc callback.
    '
    ' Every chunk of a left-shift grows the result, so chunk 2, 3, … would each
    ' trigger _mpz_realloc with a progressively larger new_alloc — all of them
    ' above the 33M-limb limit when shifting by billions of bits.
    '
    ' Fix: pre-alloc rop to the FULL final result size before any GmpRaw_mul_2exp
    ' chunk runs.  Every chunk then finds _mp_alloc >= needed and MPZ_REALLOC
    ' short-circuits without ever calling _mpz_realloc.
    Private Shared Sub BigShiftLeft(rop As mpz_t, op As mpz_t, bits As Long)
        If bits <= 0L Then
            If rop.Pointer <> op.Pointer Then GmpRaw_set(rop.Pointer, op.Pointer)  ' §35
            Return
        End If

        ' Pre-alloc rop to the FULL final result size — not just the first chunk.
        ' Each intermediate chunk grows rop; without this any chunk whose result
        ' exceeds 33M limbs hits GMP's _mpz_realloc overflow abort.
        Dim opLimbs As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(op.Pointer, 4)))
        Dim finalResultLimbs As Long = opLimbs + (bits + 63L) \ 64L + 1L   ' +1 safety margin
        If finalResultLimbs > 0L Then PreAllocMpzToLimbs(rop, finalResultLimbs)

        ' §229 (issue #56, 2026-05-23): parallel limb-range partition for out-of-place shifts
        ' on operands ≥ 5 M limbs.  Each chunk calls __gmpn_lshift on its own limb range;
        ' cross-chunk carries are stitched by a serial fixup pass.  At 1 B Combine A-D
        ' (108 M-limb gmpNumer << ~900 M bits) this cuts the shift from ~1 s serial to ~150 ms.
        ' At 5 B (~500 M-limb gmpNumer << ~4.6 B bits) the savings are larger because the
        ' chunked GMP mul_2exp loop runs 2-3 iterations and serial mpn_lshift dominates.
        ' In-place shifts (rop == op) keep the existing GMP path: the parallel partition
        ' would either need top-down processing or extra scratch.
        Const PARALLEL_THRESHOLD As Long = 5_000_000L
        If rop.Pointer <> op.Pointer AndAlso opLimbs >= PARALLEL_THRESHOLD Then
            ParallelBigShiftLeftOOP(rop, op, bits, opLimbs)
            Return
        End If

        ' Sequential chunked GMP shift (in-place or small operand).
        Dim src As IntPtr = op.Pointer
        Dim dst As IntPtr = rop.Pointer
        Dim bitsLeft As Long = bits
        Do
            Dim chunk As UInteger = CUInt(System.Math.Min(bitsLeft, 2_100_000_000L))
            GmpRaw_mul_2exp(dst, src, chunk)
            src = dst
            bitsLeft -= CLng(chunk)
        Loop While bitsLeft > 0L
    End Sub

    ' §229 (issue #56): out-of-place parallel left shift.  Decomposes total shift into
    ' limb-offset (whole limbs) + bit-shift (0..63 bits).  When bit-shift = 0, only the
    ' limb-copy step runs (parallel CopyMemory of disjoint byte ranges).  When bit-shift
    ' > 0, each thread calls __gmpn_lshift on its slice and returns the top-bit carry;
    ' a serial fixup ORs each prior chunk's carry into the next chunk's bottom limb.
    Private Shared Sub ParallelBigShiftLeftOOP(rop As mpz_t, op As mpz_t, bits As Long, opLimbs As Long)
        Dim limbOffset As Long = bits \ 64L
        Dim bitShift As Integer = CInt(bits Mod 64L)

        Dim opSign As Integer = System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(op.Pointer, 4))
        Dim spBase As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(op.Pointer, 8))
        Dim rpBase As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rop.Pointer, 8))
        Dim rpShifted As IntPtr = New IntPtr(rpBase.ToInt64() + limbOffset * 8L)

        ' Zero the low limbOffset limbs of rop.  PreAllocMpzToLimbs hands back fresh
        ' VirtualAlloc pages (already zero) when growing, but if the rop buffer was
        ' reused above its current size, stale data may live in the low limbs — zero
        ' defensively to keep the result canonical.
        If limbOffset > 0L Then
            ZeroMemory(rpBase, New UIntPtr(CULng(limbOffset * 8L)))
        End If

        Dim topCarry As ULong = 0UL
        Dim newSize As Long = opLimbs + limbOffset

        Const COPY_CHUNK As Long = 33_554_432L  ' 32 MB per task — saturates NVMe bandwidth without thread thrash
        Dim totalBytes As Long = opLimbs * 8L
        Dim numCopyChunks As Integer = CInt(System.Math.Max(1L, System.Math.Min(16L, (totalBytes + COPY_CHUNK - 1L) \ COPY_CHUNK)))

        If bitShift = 0 Then
            ' Pure limb shift — parallel memcpy from source to shifted dest.
            If numCopyChunks = 1 Then
                CopyMemory(rpShifted, spBase, New UIntPtr(CULng(totalBytes)))
            Else
                Dim chunkBytes As Long = totalBytes \ numCopyChunks
                System.Threading.Tasks.Parallel.For(0, numCopyChunks,
                    Sub(i As Integer)
                        Dim startByte As Long = CLng(i) * chunkBytes
                        Dim sizeBytes As Long = If(i = numCopyChunks - 1, totalBytes - startByte, chunkBytes)
                        CopyMemory(New IntPtr(rpShifted.ToInt64() + startByte),
                                   New IntPtr(spBase.ToInt64() + startByte),
                                   New UIntPtr(CULng(sizeBytes)))
                    End Sub)
            End If
        Else
            ' Bit shift via parallel __gmpn_lshift on limb chunks.
            Const NUM_CHUNKS As Integer = 8
            Dim chunkSize As Long = (opLimbs + CLng(NUM_CHUNKS) - 1L) \ CLng(NUM_CHUNKS)
            Dim carries(NUM_CHUNKS - 1) As ULong

            System.Threading.Tasks.Parallel.For(0, NUM_CHUNKS,
                Sub(i As Integer)
                    Dim startLimb As Long = CLng(i) * chunkSize
                    Dim countLimbs As Long = System.Math.Min(chunkSize, opLimbs - startLimb)
                    If countLimbs <= 0L Then Return
                    Dim spChunk As IntPtr = New IntPtr(spBase.ToInt64() + startLimb * 8L)
                    Dim rpChunk As IntPtr = New IntPtr(rpShifted.ToInt64() + startLimb * 8L)
                    carries(i) = GmpRaw_mpn_lshift(rpChunk, spChunk, CInt(countLimbs), CUInt(bitShift))
                End Sub)

            ' Serial fixup: OR each prior chunk's carry into the next chunk's bottom limb.
            For i As Integer = 1 To NUM_CHUNKS - 1
                Dim startLimb As Long = CLng(i) * chunkSize
                If startLimb >= opLimbs Then Exit For
                Dim rpChunk As IntPtr = New IntPtr(rpShifted.ToInt64() + startLimb * 8L)
                Dim cur As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(rpChunk))
                cur = cur Or carries(i - 1)
                Runtime.InteropServices.Marshal.WriteInt64(rpChunk, CLng(cur))
            Next

            topCarry = carries(NUM_CHUNKS - 1)
            If topCarry <> 0UL Then
                Dim topPtr As IntPtr = New IntPtr(rpShifted.ToInt64() + opLimbs * 8L)
                Runtime.InteropServices.Marshal.WriteInt64(topPtr, CLng(topCarry))
                newSize += 1L
            End If
        End If

        ' Write back the new size with the original sign.
        Runtime.InteropServices.Marshal.WriteInt32(rop.Pointer, 4, If(opSign < 0, -CInt(newSize), CInt(newSize)))
    End Sub

    ' §230 (issue #81, 2026-05-23): SHA-256 hash of an mpz_t's limb data, used to verify
    ' divisor identity for the §201-raise exact-scale fast-path.  Streams in 16 MB chunks
    ' via Marshal.Copy + IncrementalHash so the buffer can exceed Int32 size.  Returns a
    ' lower-case hex string; signs are not included (only magnitude matters for matching).
    Private Shared Function ComputeMpzSig(m As mpz_t) As String
        Dim szLimbs As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(m.Pointer, 4))
        Dim totalBytes As Long = CLng(szLimbs) * 8L
        If totalBytes <= 0L Then Return "0000000000000000000000000000000000000000000000000000000000000000"
        Dim dataPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(m.Pointer, 8))
        Const CHUNK_BYTES As Integer = 16 * 1024 * 1024   ' 16 MB Marshal.Copy chunks
        Dim buf(CHUNK_BYTES - 1) As Byte
        Using inc As IncrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            Dim offset As Long = 0L
            Do While offset < totalBytes
                Dim chunkSize As Integer = CInt(System.Math.Min(CLng(CHUNK_BYTES), totalBytes - offset))
                Runtime.InteropServices.Marshal.Copy(New IntPtr(dataPtr.ToInt64() + offset), buf, 0, chunkSize)
                inc.AppendData(buf, 0, chunkSize)
                offset += CLng(chunkSize)
            Loop
            Return Convert.ToHexString(inc.GetHashAndReset()).ToLowerInvariant()
        End Using
    End Function

    ' Compute r = floor(2^kBits / b) for b > 0, kBits > sizeinbase(b,2).
    ' Newton iteration with progressive precision; r is always an underestimate.
    ' All large multiplications use SafeMpzMul — no direct mpn_mul_fft calls.
    ''' <summary>
    ''' Barrett-style reciprocal: computes r ≈ floor(2^kBits / b) by progressive-precision Newton
    ''' iteration (r ← 2r − r²·b≫…), routing every large multiply through the chunked grid / SafeMpzMul
    ''' so GMP's FFT is never handed an over-large operand. Seeded with a ~126-bit estimate and exited
    ''' by a sound r-stability detector (§272).
    ''' </summary>
    ''' <param name="r">Receives the reciprocal.</param>
    ''' <param name="b">Divisor.</param>
    ''' <param name="kBits">Fixed-point scale — the reciprocal is of 2^kBits.</param>
    ''' <remarks>
    ''' CONTRACT: r is always a strict UNDERESTIMATE of floor(2^kBits/b) (the §107 invariant — the
    ''' high-product short-muls round up, so r never overshoots). <see cref="SafeMpzDiv"/> depends on
    ''' this and corrects the resulting quotient to the exact value with its §171/§218 adjust loop.
    ''' Convergence must be gated only on real r-stability, NEVER on the precision schedule (#93).
    ''' </remarks>
    Private Shared Sub SafeMpzReciprocal(r As mpz_t, b As mpz_t, kBits As Long)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        ' §250 (#94): read the high-half short-product flags once per reciprocal call.
        ' §254 (#70): chunked-grid reciprocal is now ON BY DEFAULT (opt-out with PI_RECIP_SHORTMUL=0).
        ' Validated: 1B π bit-identical to oracle, 500M VERIFY all-OK incl bShift>0, 5B engages cleanly
        ' (259.5M² ~33min/iter), harness 2.81×(rSq)/6.97×(p) vs §gen-DOP9.  Only routes the reciprocal
        ' capped-iter muls (RecipMul); the gate (size>SAFE + DOP≤MAXDOP) fires it only where it wins.
        _recipShortMul = (Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL") <> "0")
        _recipShortMulVerify = (Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL_VERIFY") = "1")   ' =1 cross-checks each chunked RecipMul vs §gen (default off)
        Dim _rsmDopEnv As String = Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL_MAXDOP")  ' §251 (#70) DOP gate
        Dim _rsmDopParsed As Integer
        If _rsmDopEnv IsNot Nothing AndAlso Integer.TryParse(_rsmDopEnv, _rsmDopParsed) AndAlso _rsmDopParsed >= 1 Then _recipShortMulMaxDop = _rsmDopParsed Else _recipShortMulMaxDop = 9
        _divArShortMul = (Environment.GetEnvironmentVariable("PI_DIV_AR_SHORTMUL") <> "0")   ' §262 (#42): chunked-HIGH a×r, opt-out
        _divQbChunked = (Environment.GetEnvironmentVariable("PI_DIV_QB_CHUNKED") <> "0")      ' §269 (#88): chunked-full q×b, opt-out
        ' §174-fix: mpz_sizeinbase returns UInt32 — overflows when bBits > 2^31 (szB > 33M limbs).
        ' Compute exact bBits from top limb via CLZ; avoids any overflow and is always precise.
        Dim _szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
        Dim _bDataPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
        Dim _topLimbPtr As IntPtr = New IntPtr(_bDataPtr.ToInt64() + CLng(_szB - 1) * 8L)
        Dim _topLimb As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_topLimbPtr))
        Dim _topLimbBits As Integer = 64 - System.Numerics.BitOperations.LeadingZeroCount(_topLimb)
        Dim bBits As Long = CLng(_szB - 1) * 64L + CLng(_topLimbBits)
        Dim rBits As Long = kBits - bBits + 1L   ' r has at most rBits significant bits
        If rBits <= 0L Then
            gmp_lib.mpz_set_ui(r, 0UI)
            Return
        End If

        ' §201-raise: check for a prior converged Newton r at smaller scale.
        ' If found at kBits ≈ new_kBits / 2, load and left-shift to use as a high-precision
        ' seed for the new Newton. With prior_rBits worth of correct precision, only ~3-5
        ' iters are needed (vs ~37 from a 1-bit seed via prec doubling).
        Dim _raiseUsed As Boolean = False
        Dim _exactReuse As Boolean = False   ' §230 (issue #81): set when saved r is bit-identical to the new call's r
        Dim _raisePriorRBits As Long = 0L
        Dim _nrSnapDirRaise As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _nrRaiseBin As String = System.IO.Path.Combine(_nrSnapDirRaise, "nr_raise.bin")
        Dim _nrRaiseMeta As String = System.IO.Path.Combine(_nrSnapDirRaise, "nr_raise_meta.txt")
        If _autoCheckpoint AndAlso System.IO.File.Exists(_nrRaiseBin) AndAlso System.IO.File.Exists(_nrRaiseMeta) Then
            Try
                Dim _rmLines As String() = System.IO.File.ReadAllLines(_nrRaiseMeta)
                Dim _rmDict As New Dictionary(Of String, String)()
                For Each _ml As String In _rmLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _rmDict(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _priorKBits As Long = 0L, _priorBBits As Long = 0L, _priorRBits As Long = 0L
                If _rmDict.ContainsKey("kBits") AndAlso Long.TryParse(_rmDict("kBits"), _priorKBits) AndAlso
                   _rmDict.ContainsKey("bBits") AndAlso Long.TryParse(_rmDict("bBits"), _priorBBits) AndAlso
                   _rmDict.ContainsKey("rBits") AndAlso Long.TryParse(_rmDict("rBits"), _priorRBits) Then
                    ' §230 (issue #81, 2026-05-23): exact-scale fast-path.  When the saved r is at
                    ' the IDENTICAL (kBits, bBits, rBits) AND the divisor b is bit-identical
                    ' (verified via SHA-256 bSig stored in meta), the saved r is already the
                    ' correct reciprocal — load it and skip Newton entirely.  Without this
                    ' fast-path, the pre-§230 ratio check (0.4..0.7) rejects ratio=1.000 and
                    ' Newton runs from scratch (~30 min at 1B for the post-§225 deterministic
                    ' Phase 4 reciprocal).  bSig guards against the unlikely case where a
                    ' different divisor shares the same bit-length: without it, blindly using
                    ' the saved r would silently produce a wrong reciprocal.
                    Dim _priorScopeForExact As String = If(_rmDict.ContainsKey("scope"), _rmDict("scope"), "")
                    Dim _curScopeForExact As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                    Dim _scopeOkForExact As Boolean = (_priorScopeForExact.StartsWith("sqrt_step_") AndAlso _curScopeForExact.StartsWith("sqrt_step_")) OrElse
                                                       (_priorScopeForExact.Length > 0 AndAlso _priorScopeForExact = _curScopeForExact)
                    If _scopeOkForExact AndAlso _priorKBits = kBits AndAlso _priorBBits = bBits AndAlso _priorRBits = rBits AndAlso _rmDict.ContainsKey("bSig") Then
                        Dim _priorBSig As String = _rmDict("bSig").Trim().ToLowerInvariant()
                        AppendLog($"[SafeMpzReciprocal§230] exact-scale match candidate (scope={_priorScopeForExact} kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0}); verifying bSig{vbCrLf}")
                        Dim _t230Start As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                        Dim _curBSig As String = ComputeMpzSig(b)
                        Dim _t230SigSec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t230Start) / System.Diagnostics.Stopwatch.Frequency
                        If _curBSig = _priorBSig Then
                            ' bSig confirms saved r is for THIS exact b — load r and signal the
                            ' downstream §NR-ckpt + Newton gates to short-circuit (no Newton work).
                            Dim _staging230(4194303) As Byte
                            Using _fs230 As New FileStream(_nrRaiseBin, FileMode.Open, FileAccess.Read)
                                Using _br230 As New BinaryReader(_fs230)
                                    DeserializeOneMpz(r, _br230, _staging230)
                                End Using
                            End Using
                            _raiseUsed = True
                            _exactReuse = True
                            _raisePriorRBits = _priorRBits
                            AppendLog($"[SafeMpzReciprocal§230] EXACT-REUSE: bSig verified in {_t230SigSec:F2}s; loaded saved r directly — Newton skipped{vbCrLf}")
                        Else
                            AppendLog($"[SafeMpzReciprocal§230] bSig mismatch after {_t230SigSec:F2}s (cur={_curBSig.Substring(0, 16)}... prior={_priorBSig.Substring(0, System.Math.Min(16, _priorBSig.Length))}...) — falling through to existing §201-raise logic{vbCrLf}")
                        End If
                    End If

                    ' §225 (issue #80, 2026-05-22): scope-compatibility gate.
                    ' Pre-§225 the kBits ratio check alone was used, on the assumption that
                    ' the saved r came from a structurally similar divisor. That assumption
                    ' holds between consecutive sqrt-Newton steps (xTrunc only changes in
                    ' low-precision bits across step transitions), but FAILS across the
                    ' sqrt-Newton → phase4 transition: phase4's finalT = T·sqrt(N) is
                    ' structurally unrelated to xTrunc, so the loaded r is garbage as a
                    ' seed and the §201-raise _minNrIters=5 override (vs §200's ~35) leaves
                    ' Newton far short of converged. Empirical: 1B run 2026-05-22 produced
                    ' a wrong r at phase4 → adj-up hit MAX_ADJ_ITERS → §218+§171 entered →
                    ' §171 grind at Δ=1 limb/pass would have throw at 64-pass hard cap.
                    Dim _priorScope As String = If(_rmDict.ContainsKey("scope"), _rmDict("scope"), "")
                    Dim _curScope As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                    Dim _scopeOk As Boolean = (_priorScope.StartsWith("sqrt_step_") AndAlso _curScope.StartsWith("sqrt_step_")) OrElse
                                              (_priorScope.Length > 0 AndAlso _priorScope = _curScope)
                    Dim _ratio As Double = If(kBits > 0L, CDbl(_priorKBits) / CDbl(kBits), 0.0)
                    If _scopeOk AndAlso _ratio > 0.4 AndAlso _ratio < 0.7 AndAlso _priorRBits > 0L AndAlso _priorRBits < rBits Then
                        Dim _staging(4194303) As Byte
                        Using _fs As New FileStream(_nrRaiseBin, FileMode.Open, FileAccess.Read)
                            Using _br As New BinaryReader(_fs)
                                DeserializeOneMpz(r, _br, _staging)
                            End Using
                        End Using
                        Dim _scaleShift As Long = rBits - _priorRBits
                        If _scaleShift > 0L Then
                            BigShiftLeft(r, r, _scaleShift)
                        End If
                        AppendLog($"[SafeMpzReciprocal] §201-raise: loaded prior r (priorScope={_priorScope} priorKBits={_priorKBits:N0} priorRBits={_priorRBits:N0}), scaled by 2^{_scaleShift:N0} → seed for Newton (curScope={_curScope} kBits={kBits:N0} rBits={rBits:N0}){vbCrLf}")
                        _raiseUsed = True
                        _raisePriorRBits = _priorRBits
                    ElseIf Not _scopeOk Then
                        ' §225: scope mismatch — saved r is for a different divisor family.
                        ' Discard the stale file so future calls don't trip the same check.
                        AppendLog($"[SafeMpzReciprocal§225] scope mismatch — skipping §201-raise and deleting stale nr_raise.bin (priorScope='{_priorScope}' curScope='{_curScope}' priorKBits={_priorKBits:N0} newKBits={kBits:N0}){vbCrLf}")
                        Try
                            System.IO.File.Delete(_nrRaiseBin)
                            System.IO.File.Delete(_nrRaiseMeta)
                        Catch _exDel As Exception
                            AppendLog($"[SafeMpzReciprocal§225] stale-file delete failed ({_exDel.Message}) — will retry on next call{vbCrLf}")
                        End Try
                    Else
                        AppendLog($"[SafeMpzReciprocal] §201-raise: prior found but ratio={_ratio:F3} or rBits mismatch — skipping raise (priorScope={_priorScope} priorKBits={_priorKBits:N0} priorRBits={_priorRBits:N0} newKBits={kBits:N0} newRBits={rBits:N0}){vbCrLf}")
                    End If
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §201-raise load failed ({_ex.Message}) — falling back to fresh seed{vbCrLf}")
                _raiseUsed = False
            End Try
        End If

        ' ── Seed: ~126-bit approximation from the top 128 bits of b ────────
        ' Skipped if §201-raise loaded a prior r as the seed.
        ' §272 (#88): the old seed used numerator 2^64 against the top 64 bits of b (bHi ≈ 2^63),
        ' so floor(2^64 / bHi) ≈ 2 — a 1-2 BIT quotient, NOT the ~62-bit reciprocal the prec
        ' schedule (which starts at 62) was designed for.  --test-recipconv measured SEED
        ' correctBits = 2 and correct-bits doubling from 2 (≈2^iter), which is exactly why §200
        ' had to force min_nrIters = ceil(log2(rBits))+3 extra full-width iters to converge.
        ' A P-bit quotient needs numerator 2^(bitlen(bHi)+P): keep the top SEED_BBITS=128 bits of
        ' b and divide 2^(SEED_BBITS+SEED_PREC) by bHi to get a genuine ~SEED_PREC=126-bit seed.
        ' The underestimate invariant is preserved for ANY numerator: bHi = ceil(b/2^bHiShift) ≥
        ' b/2^bHiShift ⟹ floor(2^N/bHi) ≤ 2^N·2^bHiShift/b ⟹ r ≤ 2^kBits/b after scaling.  With
        ' accuracy now ~126 ≥ the prec-schedule's 62, accuracy tracks prec and the Newton loop
        ' converges ~9 iters sooner (the §272 detector below exits on real r-stability).
        If Not _raiseUsed Then
            Const SEED_BBITS As Long = 128L   ' bits of b retained in bHi
            Const SEED_PREC As Long = 126L    ' target correct bits in the seed (≤ SEED_BBITS−2)
            Dim bHiShift As Long = System.Math.Max(0L, bBits - SEED_BBITS)
            Dim bHi As New mpz_t()
            gmp_lib.mpz_init(bHi)
            If bHiShift > 0L Then
                BigShiftRight(bHi, b, bHiShift)
                gmp_lib.mpz_add_ui(bHi, bHi, 1UI)   ' ceiling → underestimate of reciprocal guaranteed
            Else
                GmpRaw_set(bHi.Pointer, b.Pointer)  ' §35
            End If
            ' rSeed = floor(2^(SEED_BBITS+SEED_PREC) / bHi)  [both operands ≤ ~256 bits ⇒ tiny/fast]
            Dim rSeed As New mpz_t()
            gmp_lib.mpz_init(rSeed)
            gmp_lib.mpz_set_ui(rSeed, 1UI)
            gmp_lib.mpz_mul_2exp(rSeed, rSeed, New mp_bitcnt_t(CUInt(SEED_BBITS + SEED_PREC)))
            GmpRaw_tdiv_q(rSeed.Pointer, rSeed.Pointer, bHi.Pointer)  ' §35
            gmp_lib.mpz_clear(bHi)
            ' Scale to r's domain: rSeed * 2^(kBits-(SEED_BBITS+SEED_PREC)-bHiShift) ≈ 2^kBits / b
            Dim seedScale As Long = kBits - (SEED_BBITS + SEED_PREC) - bHiShift
            If seedScale > 0L Then
                BigShiftLeft(rSeed, rSeed, seedScale)
            ElseIf seedScale < 0L Then
                BigShiftRight(rSeed, rSeed, -seedScale)
            End If
            ' §35: mpz_sgn is a GMP macro — read _mp_size field (offset +4) directly.
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(rSeed.Pointer, 4)) > 0 Then gmp_lib.mpz_sub_ui(rSeed, rSeed, 2UI)
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(rSeed.Pointer, 4)) <= 0 Then gmp_lib.mpz_set_ui(rSeed, 1UI)
            GmpRaw_swap(r.Pointer, rSeed.Pointer)  ' §35
            gmp_lib.mpz_clear(rSeed)
        End If

        ' §NR-ckpt: Resume from a mid-NR checkpoint if one exists for this exact kBits/bBits.
        ' Saves r (the reciprocal estimate) and prec so a crash during a later NR iteration
        ' does not require restarting from the seed.  bTrunc is re-derived each iteration
        ' from b, so only r + prec need to be saved.
        Dim _nrSnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _nrBin As String = System.IO.Path.Combine(_nrSnapDir, "nr_r.bin")
        Dim _nrMeta As String = System.IO.Path.Combine(_nrSnapDir, "nr_meta.txt")
        ' §201-raise: when raised, prec starts already at priorRBits+2 (Newton's seed has
        ' priorRBits worth of correct bits).  Otherwise default to 62 (1-bit ε from rSeed).
        Dim prec As Long = If(_raiseUsed, _raisePriorRBits + 2L, 62L)
        Dim _resumedIter As Long = 0L  ' §200: iter count from a resumed §NR-ckpt; 0 if no resume
        ' §NR-ckpt match check (_snapKBits=kBits) takes precedence over §201-raise when both
        ' apply: a matching mid-Newton snapshot is more recent than any prior-scale raise.
        ' §230: an exact-reuse load above already gives r at full precision — skip §NR-ckpt
        ' resume entirely (it would overwrite the loaded r with a mid-Newton snapshot).
        If Not _exactReuse AndAlso _autoCheckpoint AndAlso System.IO.File.Exists(_nrBin) AndAlso System.IO.File.Exists(_nrMeta) Then
            Try
                Dim _metaLines As String() = System.IO.File.ReadAllLines(_nrMeta)
                Dim _meta As New Dictionary(Of String, String)()
                For Each _ml As String In _metaLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _meta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _snapKBits As Long = 0L, _snapBBits As Long = 0L, _snapPrec As Long = 0L, _snapIter As Long = 0L
                If _meta.ContainsKey("kBits") AndAlso Long.TryParse(_meta("kBits"), _snapKBits) AndAlso
                   _meta.ContainsKey("bBits") AndAlso Long.TryParse(_meta("bBits"), _snapBBits) AndAlso
                   _meta.ContainsKey("prec")  AndAlso Long.TryParse(_meta("prec"),  _snapPrec)  AndAlso
                   _snapKBits = kBits AndAlso _snapBBits = bBits AndAlso _snapPrec > 62L Then
                    ' §200: also parse iter so resumed Newton can continue from the right iter count.
                    If _meta.ContainsKey("iter") Then Long.TryParse(_meta("iter"), _snapIter)
                    _resumedIter = _snapIter
                    Dim _nrStaging(4194303) As Byte
                    Using _fs As New FileStream(_nrBin, FileMode.Open, FileAccess.Read)
                        Using _br As New BinaryReader(_fs)
                            DeserializeOneMpz(r, _br, _nrStaging)
                        End Using
                    End Using
                    prec = _snapPrec
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt resumed: prec={prec:N0} bBits={bBits:N0} kBits={kBits:N0}{vbCrLf}")
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §NR-ckpt load failed ({_ex.Message}) — starting from seed{vbCrLf}")
                prec = 62L
            End Try
        End If

        ' §272 (#88): record the seed's correct-bit count for the --test-recipconv probe.
        ' Null-check no-op in production (only the harness sets _recipConvRef).  If the seed shows
        ' ~62 correct bits the §200 forced-tail iters are largely wasted (early-exit recoverable);
        ' if it shows ~1 bit the seed scaling is lossy and a better seed is the real lever.
        If _recipConvRef IsNot Nothing Then
            AppendLog($"[RecipConv§272] SEED correctBits={RecipConv_CorrectBits(r, rBits):N0}/{rBits:N0} (kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0}){vbCrLf}", 1)
        End If

        ' ── Newton: r ← 2r - ceil(b/2^bShift) · r² / 2^(kBits-bShift) ────
        ' Progressive precision: prec doubles each step from ~62 → rBits+2.
        ' Ceiling truncation of b maintains r as a strict underestimate throughout.
        ' §36: Allocate bTrunc/rSq/p once outside the loop — eliminates ~18 large
        '      VirtualAlloc/Free per sqrt call (each Newton step would otherwise init
        '      and clear these, each touching the allocator twice for large operands).
        Dim bTrunc As New mpz_t()
        gmp_lib.mpz_init(bTrunc)
        Dim rSq As New mpz_t()
        gmp_lib.mpz_init(rSq)
        Dim p As New mpz_t()
        gmp_lib.mpz_init(p)
        ' prec is declared and initialised in the §NR-ckpt block above (default 62L, or restored value).
        Dim _nrIter As Integer = CInt(_resumedIter)  ' §200: continue from the resumed iter count
        ' §200 (2026-04-29): Newton must iterate until full precision is reached.  The seed has
        ' relative error ε_0 < 1/2 (since rSeed ≈ r_true / 2).  With Newton's quadratic convergence
        ' (ε_n = ε_0^(2^n)), reaching ε ≤ 2^-rBits requires n ≥ log2(rBits) iterations — about 33
        ' for rBits ≈ 5.6B.  The original loop terminated at prec >= rBits+2 (which happens after
        ' 27 iters when prec doubles from 62), but that is FEWER iterations than Newton's true
        ' convergence needs.  Result: r is short by ~2^(rBits - 2^27) ≈ 2^5.45B (verified
        ' empirically via Option H r×b chunked-grid logging).  Fix: require min_nrIters =
        ' ceil(log2(rBits)) + 3 slack iterations.  Subsequent iters at capped prec use bShift=0
        ' (full b), which keeps doubling Newton's precision until ε is below 2^-rBits.
        ' §201-raise: when seed already has priorRBits ≈ rBits/2 worth of correct bits,
        ' Newton's quadratic convergence reaches full precision in 1-2 iters.  Use 5 for
        ' headroom (covers seed scaling rounding + convergence slack).  Without raising,
        ' the seed has only 1 bit of precision, so log2(rBits)+3 iters are required.
        Dim _minNrIters As Integer = If(_raiseUsed, 5, CInt(System.Math.Ceiling(System.Math.Log(System.Math.Max(2L, rBits), 2))) + 3)
        ' §242 (issue #93 cand 1): bTrunc cache.  bShift = max(0, bBits-prec-2) DECREASES while
        ' prec doubles (iters 1..~27), then is CONSTANT once prec caps at rBits+2 (iters 28-37;
        ' at the 5B final divide bShift ≈ 30.7 Gbit).  Since b is constant too, floor(b/2^bShift)
        ' is bit-identical across the capped iters, yet BigShiftRight (a chunked, ~0.35-core,
        ' bandwidth-bound truncation of the ~47 Gbit b — minutes per iter) recomputes it each
        ' time.  Track the last bShift and skip the recompute when unchanged.  Safe: bTrunc is
        ' read-only after it is set (only consumed by p = bTrunc·rSq).
        Dim _prevBShift As Long = -1L
        ' §230: exact-reuse loaded r at full precision — skip the Newton loop body entirely.
        Do While Not _exactReuse AndAlso (prec < rBits + 2L OrElse _nrIter < _minNrIters)
            _nrIter += 1
            prec = System.Math.Min(prec * 2L + 4L, rBits + 2L)
            ' §253 (#52): surface reciprocal-Newton progress in the UI status box (not gated by
            ' _logLevel — it's the UI).  Shows iter / target-iters and precision reached.
            If _statusHook IsNot Nothing Then _statusHook($"Reciprocal Newton: iter {_nrIter}/{_minNrIters}  prec {prec:N0}/{rBits + 2L:N0} bits")
            ' §259 (#62): refine the Divide-stage ETA from the §200 fixed iteration schedule.
            If _etaReciprocalHook IsNot Nothing Then _etaReciprocalHook(_nrIter, _minNrIters)

            ' §107-fix: do NOT truncate r.  Keep r in full domain (magnitude ~2^rBits).
            ' The shift formula kBits-bShift is calibrated for r at full domain.
            ' Truncating r to prec bits (as the old code did) reduces r from ~2^rBits
            ' to ~2^prec, causing p→0 (early iters) or p>>2r (overshoot), both wrong.
            ' With r kept full-size, bTrunc's increasing width drives progressive precision.

            ' bTrunc = floor(b / 2^bShift), bShift = max(0, bBits - prec - 2)
            ' §107: Use floor (not ceiling) truncation.  The +1 ceiling added to bTrunc
            ' introduces an extra error of r²/2^(kBits-bShift) ≈ 2^(bShift+1)*r/R into p.
            ' In the final Newton iteration bShift is small (~56 bits for sqrt inputs), so
            ' this extra term ≈ 2^57*r/R dwarfs the true Newton correction e²/R (which is
            ' near zero at convergence), pushing p > 2r and making r = 2r-p go deeply
            ' negative.  The guard then resets r=1 and the loop exits with r ≈ 2^29 (3
            ' limbs) instead of the correct ~21.875M-limb reciprocal.
            ' Floor is safe: any slight overestimate of r is corrected by SafeMpzDiv's
            ' adjustment loop (which already handles q too-large by decrementing).
            Dim bShift As Long = System.Math.Max(0L, bBits - prec - 2L)
            If bShift = _prevBShift Then
                ' §242 (#93): bShift unchanged since last iter and b is constant, so bTrunc
                ' already holds the correct floor(b/2^bShift) — skip the recompute.  This is
                ' the win: on capped-prec iters (28-37 at 5B) it elides a minutes-long,
                ' bandwidth-bound BigShiftRight.  bTrunc is read-only after this block.
                If _logLevel >= 2 Then AppendLog($"[SafeMpzReciprocal§242] iter={_nrIter} bShift={bShift:N0} unchanged — reusing cached bTrunc (BigShiftRight skipped){vbCrLf}")
            ElseIf bShift > 0L Then
                BigShiftRight(bTrunc, b, bShift)
                ' No ceiling +1: floor truncation avoids catastrophic overshoot in final step.
                _prevBShift = bShift
            Else
                ' §PreAlloc-bTrunc: bTrunc._mp_alloc from prior BigShiftRight may be < _szB.
                ' GMP's __gmpz_realloc aborts when new_alloc > 33,554,431 limbs (INT_MAX/64,
                ' 32-bit mp_size_t overflow check fires BEFORE our GmpReallocFunc callback).
                ' Pre-allocate via our pool to bypass it, same pattern as BigShiftRight/BigShiftLeft.
                PreAllocMpzToLimbs(bTrunc, CLng(_szB))
                GmpRaw_set(bTrunc.Pointer, b.Pointer)  ' §35
                _prevBShift = 0L
            End If

            ' §127: log r[20,904,662..665] BEFORE rSq — this is r_24 (input to final Newton step)
            ' §147: extend to 20,904,662..663 — unverified range, hypothesized error site
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz127 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
                Const _idx127 As Integer = 20_904_664
                Dim _r127DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r127Vm2 As Long = If(_sz127 > _idx127 - 2, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 - 2) * 8L), 0L)
                Dim _r127Vm1 As Long = If(_sz127 > _idx127 - 1, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 - 1) * 8L), 0L)
                Dim _r127Val As Long = If(_sz127 > _idx127, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127) * 8L), 0L)
                Dim _r127Val1 As Long = If(_sz127 > _idx127 + 1, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 + 1) * 8L), 0L)
                AppendLog($"[NR127] iter={_nrIter} r_before_rSq[{_idx127-2:N0}]={_r127Vm2:X16} [{_idx127-1:N0}]={_r127Vm1:X16} [{_idx127:N0}]={_r127Val:X16} [{_idx127+1:N0}]={_r127Val1:X16} sz={_sz127:N0}{vbCrLf}")
            End If
            ' rSq = r²
            Dim szR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            ' §129: verify r and rSq pointers are valid before rSq=r² call
            If _logLevel >= 2 Then
                Dim _r129Alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 0)
                Dim _rSq129Alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 0)
                Dim _rSq129Sz As Integer = Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4)
                Dim _bTrunc129Sz As Integer = Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4)
                AppendLog($"[NR§129] iter={_nrIter} szR={szR:N0} r_alloc={_r129Alloc:N0} rSq_alloc={_rSq129Alloc:N0} rSq_sz={_rSq129Sz:N0} bTrunc_sz={_bTrunc129Sz:N0}{vbCrLf}")
            End If
            If CLng(szR) * 2L <= SAFE Then
                GmpRaw_mul(rSq.Pointer, r.Pointer, r.Pointer)
            ElseIf _recipShortMul AndAlso MemBudget_SuggestSafeMulDop(CLng(szR), CLng(szR)) <= _recipShortMulMaxDop Then
                ' §250/§251 (#94/#70): chunked-grid high product for the large reciprocal squaring.
                ' Engages on SIZE (already past szR·2 ≤ SAFE) + low §gen DOP — NOT bShift=0.  §251-fix:
                ' at the 5B DIVIDE the denominator bBits (≈47e9) >> rBits (≈16.6e9), so bShift stays
                ' ≈30e9 and NEVER reaches 0 — the old bShift=0 gate wrongly excluded the 5B reciprocal.
                ' r² is 2·szR limbs; keep top szR + margin (overestimate-rounded), skipping the low half.
                If _logLevel >= 2 Then AppendLog($"[§251-gate] rSq iter={_nrIter} ENGAGE szR={szR:N0} prec={prec:N0} rBits={rBits:N0} bShift={bShift:N0} dop={MemBudget_SuggestSafeMulDop(CLng(szR), CLng(szR))}{vbCrLf}")
                RecipMul(rSq, r, r, CLng(szR) + 4096L, "rSq", _nrIter)
            Else
                SafeMpzMul(rSq, r, r)
            End If
            ' §121: log rSq top+bot at final iteration to verify r×r correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz121 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
                Dim _rSq121DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rSq.Pointer, 8))
                Dim _rSq121B0 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 0), 0L)
                Dim _rSq121B1 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 8), 0L)
                ' §237 (issue #86): compute the absolute limb address in Int64.  Old `(sz - 1) * 8`
                ' overflowed Int32 at 5 B when sz > 2^28 (~268 M limbs) → AV in Marshal.ReadInt64.
                Dim _rSq121T1 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rSq121DPtr.ToInt64() + (CLng(_sz121) - 1L) * 8L), 0), 0L)
                Dim _rSq121T0 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rSq121DPtr.ToInt64() + (CLng(_sz121) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR121] iter={_nrIter} rSq sz={_sz121:N0} bot=[{_rSq121B0:X16} {_rSq121B1:X16}] top=[{_rSq121T0:X16} {_rSq121T1:X16}]{vbCrLf}")
            End If
            ' §126: log rSq[20,904,662..663] at final iter — B2[6,321,328] of rSq feeds into p[64,654,664]
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz126 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
                Const _idx126 As Integer = 20_904_662
                Dim _rSq126DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rSq.Pointer, 8))
                Dim _rSq126Val As Long = If(_sz126 > _idx126, Runtime.InteropServices.Marshal.ReadInt64(_rSq126DPtr, CLng(_idx126) * 8L), 0L)
                Dim _rSq126Val1 As Long = If(_sz126 > _idx126 + 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq126DPtr, CLng(_idx126 + 1) * 8L), 0L)
                AppendLog($"[NR126] iter={_nrIter} rSq[{_idx126:N0}]={_rSq126Val:X16} rSq[{_idx126 + 1:N0}]={_rSq126Val1:X16} sz={_sz126:N0}{vbCrLf}")
            End If

            ' p = bTrunc · rSq
            Dim szBt As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4))
            Dim szRsq As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
            If CLng(szBt) + CLng(szRsq) <= SAFE Then
                GmpRaw_mul(p.Pointer, bTrunc.Pointer, rSq.Pointer)
            ElseIf _recipShortMul AndAlso MemBudget_SuggestSafeMulDop(CLng(szBt), CLng(szRsq)) <= _recipShortMulMaxDop Then
                ' §250/§251 (#94/#70): chunked-grid high product for p = bTrunc·rSq.  Engages on SIZE
                ' + low §gen DOP — NOT bShift=0 (§251-fix; see rSq site).  p is shifted right by
                ' (kBits−bShift); keep the surviving top limbs + margin (overestimate ⟹ r underestimate).
                ' keepP uses bShift, so it's correct for the 5B bShift≈30e9 regime too.
                Dim _keepP As Long = CLng(szBt) + CLng(szRsq) - (kBits - bShift) \ 64L + 4096L
                If _logLevel >= 2 Then AppendLog($"[§251-gate] p iter={_nrIter} ENGAGE szBt={szBt:N0} szRsq={szRsq:N0} keepP={_keepP:N0} bShift={bShift:N0} dop={MemBudget_SuggestSafeMulDop(CLng(szBt), CLng(szRsq))}{vbCrLf}")
                RecipMul(p, bTrunc, rSq, _keepP, "p", _nrIter)
            Else
                SafeMpzMul(p, bTrunc, rSq)
            End If
            ' §122: log p top+bot before BigShiftRight at final iteration to verify b×rSq correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz122 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Dim _p122DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p122B0 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 0), 0L)
                Dim _p122B1 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _p122T1 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p122DPtr.ToInt64() + (CLng(_sz122) - 1L) * 8L), 0), 0L)
                Dim _p122T0 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p122DPtr.ToInt64() + (CLng(_sz122) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR122] iter={_nrIter} p_before_shift sz={_sz122:N0} bot=[{_p122B0:X16} {_p122B1:X16}] top=[{_p122T0:X16} {_p122T1:X16}]{vbCrLf}")
            End If
            ' §125: log p[64,654,663..665] before BigShiftRight
            ' p[64654663] maps to p_shifted[20904663]; p[64654664] maps to p_shifted[20904664]
            ' §147: add 64654663 to verify p_shifted[20904663] = (p[64654663]>>27)|(p[64654664]<<37)
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz125 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Const _idx125 As Integer = 64_654_664
                Dim _p125DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p125Vm1 As Long = If(_sz125 > _idx125 - 1, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125 - 1) * 8L), 0L)
                Dim _p125Val As Long = If(_sz125 > _idx125, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125) * 8L), 0L)
                Dim _p125Val1 As Long = If(_sz125 > _idx125 + 1, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125 + 1) * 8L), 0L)
                Dim _p125Exp As Long = CLng(CULng(_p125Vm1) >> 27) Or (_p125Val << 37)  ' expected p_shifted[20904663] — unsigned shift to match GMP
                AppendLog($"[NR125] iter={_nrIter} p_before_shift[{_idx125-1:N0}]={_p125Vm1:X16} [{_idx125:N0}]={_p125Val:X16} [{_idx125+1:N0}]={_p125Val1:X16} sz={_sz125:N0} expected_psh[{_idx125-43750001:N0}]={_p125Exp:X16}{vbCrLf}")
            End If

            ' p >>= (kBits - bShift);  r = 2r - p
            ' §107-fix: revert to kBits-bShift.  This formula is correct when r is in
            ' "full domain" (magnitude ~2^rBits).  The bug was the truncation above
            ' which reduced r to ~2^prec, invalidating the shift.  With r kept at
            ' full domain the formula converges correctly.
            BigShiftRight(p, p, kBits - bShift)
            ' §120: log p top+bot after BigShiftRight (only at bShift=0, i.e. final iter)
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz120 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Dim _p120DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p120B0 As Long = If(_sz120 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, 0), 0L)
                Dim _p120B1 As Long = If(_sz120 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _p120T1 As Long = If(_sz120 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p120DPtr.ToInt64() + (CLng(_sz120) - 1L) * 8L), 0), 0L)
                Dim _p120T0 As Long = If(_sz120 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p120DPtr.ToInt64() + (CLng(_sz120) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR120] iter={_nrIter} p_after_shift: sz={_sz120:N0} bot=[{_p120B0:X16} {_p120B1:X16}] top=[{_p120T0:X16} {_p120T1:X16}]{vbCrLf}")
            End If
            ' §123: log p[20,904,662..665] after BigShiftRight — §147: add 20904662..663 for cross-check with §125
            ' Verify: p_shifted[20904663] should == (p_before[64654663]>>27)|(p_before[64654664]<<37) from §125
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz123 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Const _idx123 As Integer = 20_904_664
                Dim _p123DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p123Vm2 As Long = If(_sz123 > _idx123 - 2, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 - 2) * 8L), 0L)
                Dim _p123Vm1 As Long = If(_sz123 > _idx123 - 1, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 - 1) * 8L), 0L)
                Dim _p123Val As Long = If(_sz123 > _idx123, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123) * 8L), 0L)
                Dim _p123Val1 As Long = If(_sz123 > _idx123 + 1, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 + 1) * 8L), 0L)
                AppendLog($"[NR123] iter={_nrIter} p_after_shift[{_idx123-2:N0}]={_p123Vm2:X16} [{_idx123-1:N0}]={_p123Vm1:X16} [{_idx123:N0}]={_p123Val:X16} [{_idx123+1:N0}]={_p123Val1:X16} sz={_sz123:N0}{vbCrLf}")
            End If
            ' §272 (#88): sound convergence detector.  At the fixed point r = 2r − p ⟹ p == r, so
            ' r == p means this iteration is a no-op and r is frozen at the iteration's fixed point
            ' (within the §107 ±1-2 ulp that SafeMpzDiv's §171 adjust already corrects).  Compare BEFORE
            ' the 2r−p update (r is still the previous estimate, p is this iter's product).  Gated on
            ' prec having reached its cap (prec >= rBits+2) so it can only fire at FULL target precision
            ' — NOT on bShift, which at the real divide stays ≫0 because bBits ≫ rBits (the §251-fix
            ' regime: bTrunc keeps only the top ~rBits bits of b, the rest don't affect r's rBits sig
            ' bits).  It detects ACTUAL r-stability, never a prec proxy, so it cannot undershoot (while r
            ' still gains low bits, p ≠ r and we iterate).  Self-validating: it exits only when r is
            ' exactly stable, so the result is bit-identical to running the remaining §200 min_nrIters
            ' tail.  §272 also fixed the 1-bit seed ⇒ accuracy now tracks prec and reaches rBits as prec
            ' caps (~9-13 iters before the old min_nrIters floor at 1B/5B).
            Dim _converged272 As Boolean = (prec >= rBits + 2L) AndAlso (GmpRaw_cmp(r.Pointer, p.Pointer) = 0)
            ' §PreAlloc-r-add: After checkpoint restore r._mp_alloc equals _mp_size exactly.
            ' GmpRaw_add(r,r,r) → 2r may need one extra limb → __gmpz_realloc > 33.5M limit → GMP abort.
            ' Pre-allocate 2 extra limbs via our pool to bypass it.
            PreAllocMpzToLimbs(r, CLng(szR) + 2L)
            GmpRaw_add(r.Pointer, r.Pointer, r.Pointer)    ' §NR-raw: r = 2r — bypass managed wrapper pointer corruption
            GmpRaw_sub(r.Pointer, r.Pointer, p.Pointer)    ' §NR-raw: r = 2r - p — bypass managed wrapper pointer corruption
            ' §272 (#88): per-iter convergence probe for --test-recipconv (null-check no-op in production).
            ' Shows whether correct-bits doubles from ~1 (lossy seed) or jumps to ~62 then doubles (good
            ' seed), and whether it plateaus at rBits before _minNrIters (⇒ forced tail iters are wasted).
            If _recipConvRef IsNot Nothing Then
                AppendLog($"[RecipConv§272] iter={_nrIter}/{_minNrIters} prec={prec:N0} bShift={bShift:N0} correctBits={RecipConv_CorrectBits(r, rBits):N0}/{rBits:N0}{vbCrLf}", 1)
            End If
            ' §272 (#88): exit as soon as the full-precision iteration is a proven no-op (r frozen).
            If _converged272 Then
                AppendLog($"[SafeMpzReciprocal§272] converged at iter={_nrIter} (r==p fixed point, prec={prec:N0} rBits={rBits:N0}) — exiting Newton ({_minNrIters - _nrIter} min-iter tail skipped){vbCrLf}", 2)
                Exit Do
            End If
            ' §124: log r[20,904,662..665] immediately after r = 2r - p (final iter)
            ' §147: extend to 20904662..663 — check if r[20904663] has a gross error vs §127/§123
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz124 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
                Const _idx124 As Integer = 20_904_664
                Dim _r124DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r124Vm2 As Long = If(_sz124 > _idx124 - 2, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 - 2) * 8L), 0L)
                Dim _r124Vm1 As Long = If(_sz124 > _idx124 - 1, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 - 1) * 8L), 0L)
                Dim _r124Val As Long = If(_sz124 > _idx124, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124) * 8L), 0L)
                Dim _r124Val1 As Long = If(_sz124 > _idx124 + 1, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 + 1) * 8L), 0L)
                AppendLog($"[NR124] iter={_nrIter} r_after_sub[{_idx124-2:N0}]={_r124Vm2:X16} [{_idx124-1:N0}]={_r124Vm1:X16} [{_idx124:N0}]={_r124Val:X16} [{_idx124+1:N0}]={_r124Val1:X16} sz={_sz124:N0}{vbCrLf}")
            End If
            If _logLevel >= 3 Then   ' §252 (#95): per-Newton-iter detail → level 3 (sub-phase progress)
                Dim _szR_after As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)
                Dim _szP As Integer = Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4)
                ' §119: log r bottom+top 2 limbs at every NR iteration to track lower-bit convergence
                Dim _sz119 As Integer = System.Math.Abs(_szR_after)
                Dim _r119DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r119B0 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 0), 0L)
                Dim _r119B1 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _r119T1 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_r119DPtr.ToInt64() + (CLng(_sz119) - 1L) * 8L), 0), 0L)
                Dim _r119T0 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_r119DPtr.ToInt64() + (CLng(_sz119) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR§119] iter={_nrIter} szR={_sz119:N0} bot=[{_r119B0:X16} {_r119B1:X16}] top=[{_r119T0:X16} {_r119T1:X16}]{vbCrLf}")
                AppendLog($"[NR] iter={_nrIter} prec={prec:N0} bShift={bShift:N0} kBitsMinusBShift={kBits - bShift:N0} szP={_szP:N0} szR_after={_szR_after:N0}{vbCrLf}")
            End If

            ' §NR-ckpt: Save r and prec after each Newton iteration so a crash during
            ' the NEXT iteration's SafeMpzMul can resume from here rather than the seed.
            ' r.Pointer is valid here — no managed GMP call since the GmpRaw_sub above.
            If _autoCheckpoint Then
                Try
                    If Not System.IO.Directory.Exists(_nrSnapDir) Then
                        System.IO.Directory.CreateDirectory(_nrSnapDir)
                    End If
                    Dim _nrSaveStaging(4194303) As Byte
                    Using _fs As New FileStream(_nrBin, FileMode.Create, FileAccess.Write)
                        Using _bw As New BinaryWriter(_fs)
                            SerializeOneMpz(r, _bw, _nrSaveStaging)
                        End Using
                    End Using
                    System.IO.File.WriteAllText(_nrMeta,
                        $"kBits={kBits}{vbLf}bBits={bBits}{vbLf}prec={prec}{vbLf}iter={_nrIter}{vbLf}")
                    BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt saved: iter={_nrIter} prec={prec:N0}{vbCrLf}")
                Catch _ex As Exception
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt save failed: {_ex.Message}{vbCrLf}")
                End Try
            End If

            ' Guard: reset if r went non-positive.  With floor truncation (§107) this
            ' should not happen in normal operation.  Retained as a safety net for
            ' pathological seeds only.
            ' §35: mpz_sgn is a GMP macro — read _mp_size field directly.
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)) <= 0 Then
                Dim _guardSzR As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)
                Dim _guardSzBTrunc As Integer = Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4)
                Dim _guardMsg As String = $"[SafeMpzReciprocal] GUARD fired: prec={prec:N0} bShift={bShift:N0} szR={_guardSzR:N0} szBTrunc={_guardSzBTrunc:N0} kBits={kBits:N0}" & vbCrLf
                Try : System.IO.File.AppendAllText("C:\PiOutput\guard_debug.txt", _guardMsg) : Catch : End Try
                AppendLog(_guardMsg)
                GmpRaw_set_ui(r.Pointer, 1UI)    ' §NR-raw: bypass managed wrapper
                prec = 1L
            End If

            ' §173-removed: Do NOT zero lower bits of r.  §173 was introduced to fix a
            ' hypothesised "garbage lower bits" problem, but it actually CAUSES the fixed-point
            ' convergence failure: zeroing r's lower bits each step resets them to 0, so the
            ' final iteration starts with lower bits=0 instead of the partially-converged value
            ' from prior steps, causing p_after_shift top = r top (fixed point, szDelta≈42.8M).
            ' Correct behaviour without §173: lower bits of r are "garbage" in early iterations
            ' but they do NOT pollute the upper bits (the garbage contribution to p is confined
            ' to low-order positions after the shift).  Newton converges normally.

            ' §219 (issue #79): drain finalizer queue at the Newton iteration boundary.
            ' Each iteration creates and drops many mpz_t wrappers (rSq, p, sub-products,
            ' etc.); over a multi-hour SafeMpzReciprocal run the finalizer backlog grows
            ' and starts stealing CPU from the single compute thread. Bounded cleanup here.
            DrainFinalizers()
        Loop
        ' §108-diag: log top 4 limbs of r to verify value (not just size)
        If _logLevel >= 2 Then
            Dim _szRFinal As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            Dim _rDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
            Dim _rLimb2 As Long = If(_szRFinal >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rDPtr.ToInt64() + (CLng(_szRFinal) - 1L) * 8L), 0), 0L)
            Dim _rLimb1 As Long = If(_szRFinal >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rDPtr.ToInt64() + (CLng(_szRFinal) - 2L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzReciprocal] done: szR={_szRFinal:N0} top2limbs=[{_rLimb2:X16} {_rLimb1:X16}] kBits={kBits:N0} rBits={rBits:N0}{vbCrLf}")
        End If

        ' §201-raise: save converged r as nr_raise.bin so the NEXT (larger-scale)
        ' SafeMpzReciprocal call can load it as a high-precision seed.  Overwrites any
        ' prior nr_raise.bin — only the most recently converged r is kept.  The next call
        ' decides whether to use it based on the kBits ratio (0.4..0.7).
        If _autoCheckpoint Then
            Try
                If Not System.IO.Directory.Exists(_nrSnapDirRaise) Then
                    System.IO.Directory.CreateDirectory(_nrSnapDirRaise)
                End If
                Dim _raiseSaveStaging(4194303) As Byte
                Using _fs As New FileStream(_nrRaiseBin, FileMode.Create, FileAccess.Write)
                    Using _bw As New BinaryWriter(_fs)
                        SerializeOneMpz(r, _bw, _raiseSaveStaging)
                    End Using
                End Using
                ' §225 (issue #80): write _divCkptScope so the next call can verify the
                ' saved r is for a compatible divisor family (sqrt_step_* across consecutive
                ' steps; same scope otherwise).
                ' §230 (issue #81): also write SHA-256 bSig of b's limbs so the next call's
                ' exact-scale fast-path can verify the saved r is for the IDENTICAL divisor
                ' before bypassing Newton.  Old meta files without bSig: §230 fast-path
                ' won't fire (safe — falls through to existing §201-raise logic).
                Dim _saveScope As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                Dim _t230SigStart As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                Dim _saveBSig As String = ComputeMpzSig(b)
                Dim _t230SigSaveSec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t230SigStart) / System.Diagnostics.Stopwatch.Frequency
                System.IO.File.WriteAllText(_nrRaiseMeta,
                    $"kBits={kBits}{vbLf}bBits={bBits}{vbLf}rBits={rBits}{vbLf}scope={_saveScope}{vbLf}bSig={_saveBSig}{vbLf}")
                BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                AppendLog($"[SafeMpzReciprocal] §201-raise: saved converged r (scope={_saveScope} kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0} bSig={_saveBSig.Substring(0, 16)}... computed in {_t230SigSaveSec:F2}s) for future raise{vbCrLf}")
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §201-raise save failed: {_ex.Message}{vbCrLf}")
            End Try
        End If

        ' §211 (2026-05-15): DEFER §NR-ckpt cleanup until SafeMpzDiv §202-exit fires.
        ' Originally deleted here at end of SafeMpzReciprocal — but that left the entire
        ' post-recip stretch (a×r → BigShiftRight → §171-ckpt save) UNPROTECTED.  Empirical
        ' impact: 5/15 09:55 crash in top-level depth-0 §gen of a×r at 5B scale (kBits=63.9B)
        ' lost iter=37 §NR-ckpt because this delete had already fired, forcing recovery from
        ' the iter=36 backup and re-running the entire 13h Newton loop.  Stale-data concern
        ' from the original cleanup (cross-call kBits mismatch) is already handled by the
        ' §NR-ckpt resume check (_snapKBits = kBits AndAlso _snapBBits = bBits at line ~3352),
        ' so leftover files are safely ignored by future calls.  Actual cleanup moved to
        ' SafeMpzDiv §202-exit, which only fires when the entire divide succeeds — i.e., the
        ' point at which the §NR-ckpt is guaranteed no longer needed.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzReciprocal] §211: deferring §NR-ckpt cleanup to SafeMpzDiv §202-exit (kBits={kBits:N0}){vbCrLf}")

        ' §36: Clear loop-external temporaries once after loop completes.
        gmp_lib.mpz_clears(bTrunc, rSq, p, Nothing)
    End Sub

    ' Compute q = floor(a / b).  Safe for any operand size.
    ' Uses Barrett-style Newton reciprocal + SafeMpzMul — no direct GMP division
    ' for large inputs (which would crash via mpn_mul_fft overflow at 5B digits).
    ''' <summary>
    ''' Computes q = floor(a / b), safe for any operand size. Forms the Barrett quotient from
    ''' <see cref="SafeMpzReciprocal"/> (q ≈ (a · r) ≫ kBits, the a×r §262 and q×b §269 multiplies
    ''' running on the chunked grid), then corrects it to the exact floor. No direct GMP division —
    ''' which would crash via mpn_mul_fft overflow at 5 B digits.
    ''' </summary>
    ''' <param name="q">Receives the exact integer quotient floor(a / b).</param>
    ''' <param name="a">Dividend.</param>
    ''' <param name="b">Divisor.</param>
    ''' <remarks>
    ''' CONTRACT: the reciprocal r is a strict underestimate (§107), so the raw Barrett quotient is at
    ''' or just below the true value; the §171/§218 adjust loop then nudges q by ±1 (bounded by
    ''' MAX_ADJ_ITERS) until a − q·b ∈ [0, b), yielding the exact floor. This is why over-estimating the
    ''' a×r / q×b high-products (§262/§269) is safe — the adjustment converges either way and π is
    ''' bit-identical.
    ''' </remarks>
    Private Shared Sub SafeMpzDiv(q As mpz_t, a As mpz_t, b As mpz_t)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        Const MAX_ADJ_ITERS As Integer = 10   ' Barrett should need ≤ 2; >10 means reciprocal is wrong
        ' §184c: Capture a.Pointer and b.Pointer as plain IntPtrs immediately on entry.
        ' Every SafeMpzMul call in this function (a×r and q×b) triggers the §78 side-effect,
        ' corrupting ALL registered mpz_t Pointer fields — including a.Pointer and b.Pointer.
        ' These pre-captured values remain valid for the lifetime of SafeMpzDiv.
        Dim _aPtr As IntPtr = a.Pointer
        Dim _bPtr As IntPtr = b.Pointer
        Dim szA As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(a.Pointer, 4))
        Dim szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
        If CLng(szA) + CLng(szB) <= SAFE Then
            GmpRaw_tdiv_q(q.Pointer, a.Pointer, b.Pointer)  ' §35
            Return
        End If

        ' §172: Removed — §173 (zero lower bits after each Newton iter) fixes SafeMpzReciprocal
        ' convergence for all szB, so the mpz_tdiv_q bypass is no longer needed.

        ' §174: Compute aBits/bBits directly from limb counts to avoid GMP's mpz_sizeinbase
        ' truncation bug: on GMP's MSVC build mp_bitcnt_t is 32-bit unsigned long, so
        ' mpz_sizeinbase overflows for numbers > ~67M limbs (2^32 / 64 = 67.1M), returning
        ' a truncated value.  For a=103.8M limbs (6.64B bits) the truncated result is 2.35B
        ' which makes kBits < bBits → rBits negative → Newton loop skipped → Barrett fails.
        ' Using szA*64 and szB*64 gives correct upper bounds (within 63 bits of actual).
        Dim aBits As Long = CLng(szA) * 64L
        Dim bBits As Long = CLng(szB) * 64L
        ' r = floor(2^kBits / b), kBits = aBits+3 (Barrett: quotient_bits + divisor_bits + margin)
        Dim kBits As Long = aBits + 3L
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] entry: szA={szA:N0} szB={szB:N0} aBits={aBits:N0} bBits={bBits:N0} kBits={kBits:N0}{vbCrLf}")

        ' §171-ckpt: lifted declarations so the resume path can populate them.
        ' Original assignments stay where they were (now without "Dim").
        Dim _qPtr As IntPtr = IntPtr.Zero
        Dim szQ As Integer = 0
        Dim _ckpQResumed As Boolean = False

        ' §171-ckpt: Resume from a saved div_q checkpoint if one exists for this exact call.
        ' div_q.bin holds the post-shift Barrett quotient; on resume we skip the Newton
        ' reciprocal, a×r, BigShiftRight, and the swap, and jump straight to q×b.
        Dim _divCkptDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _divCkptBin As String = System.IO.Path.Combine(_divCkptDir, "div_q.bin")
        Dim _divCkptMeta As String = System.IO.Path.Combine(_divCkptDir, "div_meta.txt")
        If _autoCheckpoint AndAlso System.IO.File.Exists(_divCkptBin) AndAlso System.IO.File.Exists(_divCkptMeta) Then
            Try
                Dim _metaLines As String() = System.IO.File.ReadAllLines(_divCkptMeta)
                Dim _meta As New Dictionary(Of String, String)()
                For Each _ml As String In _metaLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _meta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _snapSzA As Integer = 0, _snapSzB As Integer = 0
                Dim _snapABits As Long = 0L, _snapKBits As Long = 0L
                Dim _snapScope As String = ""
                If _meta.ContainsKey("szA") AndAlso Integer.TryParse(_meta("szA"), _snapSzA) AndAlso
                   _meta.ContainsKey("szB") AndAlso Integer.TryParse(_meta("szB"), _snapSzB) AndAlso
                   _meta.ContainsKey("aBits") AndAlso Long.TryParse(_meta("aBits"), _snapABits) AndAlso
                   _meta.ContainsKey("kBits") AndAlso Long.TryParse(_meta("kBits"), _snapKBits) AndAlso
                   _meta.ContainsKey("scope") Then
                    _snapScope = _meta("scope")
                    If _snapSzA = szA AndAlso _snapSzB = szB AndAlso _snapABits = aBits AndAlso
                       _snapKBits = kBits AndAlso _snapScope = _divCkptScope Then
                        Dim _qStaging(4194303) As Byte
                        Using _fs As New FileStream(_divCkptBin, FileMode.Open, FileAccess.Read)
                            Using _br As New BinaryReader(_fs)
                                DeserializeOneMpz(q, _br, _qStaging)
                            End Using
                        End Using
                        _qPtr = q.Pointer
                        szQ = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qPtr, 4))
                        _ckpQResumed = True
                        AppendLog($"[SafeMpzDiv§171-ckpt] resumed: skipping Newton+a×r+shift (szQ={szQ:N0} scope={_divCkptScope}){vbCrLf}")
                    End If
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzDiv§171-ckpt] load failed ({_ex.Message}) — running full path{vbCrLf}")
            End Try
        End If
        If _ckpQResumed Then GoTo PostShiftCheckpoint

        Dim r As New mpz_t()
        gmp_lib.mpz_init(r)
        ' §220 (issue #55, 2026-05-22): §168 force-serial LIFTED.
        ' Original §168 forced _safeMulDop=1 for SafeMpzReciprocal because bTrunc×rSq
        ' inside iter=25 produced wrong r under parallel execution. Root cause turned
        ' out to be the Newton premature-convergence bug fixed by §200 (_minNrIters =
        ' log2(rBits)+3) + §201-raise. With those fixes in place, a wrong reciprocal
        ' can no longer be produced regardless of DOP. The §144/§170/§169 in-loop
        ' verifiers stay enabled and would catch any regression early.
        ' Original lines preserved as comments for easy revert (grep §220):
        '   Dim _saved168Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
        '   System.Threading.Volatile.Write(_safeMulDop, 1)
        '   ...AppendLog($"[SafeMpzDiv§168] forcing all-serial...
        '   System.Threading.Volatile.Write(_safeMulDop, _saved168Dop)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §168 lifted — SafeMpzReciprocal runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        SafeMpzReciprocal(r, b, kBits)
        Dim szR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] reciprocal done: szR={szR:N0}{vbCrLf}")

        ' §116: verify r interior limbs — are lower limbs within each piece zero?
        ' §147: extend to include r[20904662] and r[20904663] — hypothesized error site
        If _logLevel >= 2 Then
            Dim _r116DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r116Bot0 As Long = If(szR >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 0), 0L)
            Dim _r116Bot1 As Long = If(szR >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 8), 0L)
            Dim _r116Mid As Long = If(szR >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 10937500 * 8), 0L)
            Dim _r116Near2 As Long = If(szR >= 20904663, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904662 * 8), 0L)
            Dim _r116Near1 As Long = If(szR >= 20904664, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904663 * 8), 0L)
            Dim _r116Near As Long = If(szR >= 20904665, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904664 * 8), 0L)
            AppendLog($"[SafeMpzDiv§116] r limbs: bot=[{_r116Bot0:X16} {_r116Bot1:X16}] mid[10937500]={_r116Mid:X16} [20904662]={_r116Near2:X16} [20904663]={_r116Near1:X16} [20904664]={_r116Near:X16}{vbCrLf}")
            Dim _a116DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _a116Bot As Long = If(szA >= 1, Runtime.InteropServices.Marshal.ReadInt64(_a116DPtr, 0), 0L)
            Dim _a116Mid As Long = If(szA >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_a116DPtr, 10937500 * 8), 0L)
            AppendLog($"[SafeMpzDiv§116b] a limbs: bot={_a116Bot:X16} mid[10937500]={_a116Mid:X16}{vbCrLf}")
        End If

        ' §117: verify r limbs in the unsampled near-top region (20,904,665..21,874,998)
        If _logLevel >= 2 Then
            Dim _r117DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r117_a As Long = If(szR >= 20904666, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 20904665 * 8), 0L)
            Dim _r117_b As Long = If(szR >= 21000001, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21000000 * 8), 0L)
            Dim _r117_c As Long = If(szR >= 21500001, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21500000 * 8), 0L)
            Dim _r117_d As Long = If(szR >= 21874999, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21874998 * 8), 0L)
            AppendLog($"[SafeMpzDiv§117] r near-top: [20904665]={_r117_a:X16} [21000000]={_r117_b:X16} [21500000]={_r117_c:X16} [21874998]={_r117_d:X16}{vbCrLf}")
        End If

        ' §144: verify b×r ≈ 2^kBits — correct r means b×r < 2^kBits exactly.
        ' Checks limb kBits\64 (should have bits kBits%64..63 = 0) and limb kBits\64+1 (should be 0).
        ' A nonzero result means SafeMpzReciprocal overestimated r, which would cause Barrett to fail.
        If _logLevel >= 2 AndAlso szR = 21875001 Then
            Dim _br144 As New mpz_t()
            gmp_lib.mpz_init(_br144)
            AppendLog($"[SafeMpzDiv§144] computing b*r to verify reciprocal (szB={szB:N0} szR={szR:N0})...{vbCrLf}")
            ' §144-serial: force serial to avoid parallel SafeMpzMul race condition corrupting diagnostic
            Dim _saved144Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
            System.Threading.Volatile.Write(_safeMulDop, 1)
            SafeMpzMul(_br144, b, r)
            System.Threading.Volatile.Write(_safeMulDop, _saved144Dop)
            Dim _szBR144 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_br144.Pointer, 4))
            Dim _kLimb144 As Long = kBits \ 64L
            Dim _kRem144 As Integer = CInt(kBits Mod 64L)
            Dim _br144DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_br144.Pointer, 8))
            Dim _br144_km1 As Long = If(_szBR144 > _kLimb144 - 1, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144 - 1) * 8), 0L)
            Dim _br144_k As Long = If(_szBR144 > _kLimb144, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144) * 8), 0L)
            Dim _br144_kp1 As Long = If(_szBR144 > _kLimb144 + 1, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144 + 1) * 8), 0L)
            Dim _rOk144 As Boolean = (_br144_kp1 = 0L) AndAlso (CULng(_br144_k) < CULng(1L << _kRem144))
            AppendLog($"[SafeMpzDiv§144] b*r sz={_szBR144:N0} kLimb={_kLimb144:N0} kRem={_kRem144} b*r[kLimb-1]={_br144_km1:X16} b*r[kLimb]={_br144_k:X16} b*r[kLimb+1]={_br144_kp1:X16} maxOK={(1L << _kRem144) - 1L:X16} r_valid={_rOk144}{vbCrLf}")
            ' §169: Check LOWER bound — b*(r+1) > 2^kBits. If false, r < floor(2^kBits/b), i.e., r is too small.
            ' b*(r+1) = b*r + b. Check if adding b to b*r causes bit kBits to be set.
            ' This is cheap (O(n) add) since b*r is already computed. No extra SafeMpzMul needed.
            gmp_lib.mpz_add(_br144, _br144, b)
            Dim _szBRp1 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_br144.Pointer, 4))
            Dim _brp1DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_br144.Pointer, 8))
            Dim _brp1_k As Long = If(_szBRp1 > _kLimb144, Runtime.InteropServices.Marshal.ReadInt64(_brp1DPtr, CInt(_kLimb144) * 8), 0L)
            Dim _brp1_kp1 As Long = If(_szBRp1 > _kLimb144 + 1, Runtime.InteropServices.Marshal.ReadInt64(_brp1DPtr, CInt(_kLimb144 + 1) * 8), 0L)
            ' b*(r+1) > 2^kBits iff bit kBits is set, i.e., b*(r+1)[kLimb] has bit kRem set OR [kLimb+1]≠0
            Dim _rTight169 As Boolean = (CULng(_brp1_k) >= CULng(1L << _kRem144)) OrElse (_brp1_kp1 <> 0L)
            AppendLog($"[SafeMpzDiv§169] b*(r+1) sz={_szBRp1:N0} [kLimb]={_brp1_k:X16} [kLimb+1]={_brp1_kp1:X16} r_tight(lower_bound_ok)={_rTight169}{vbCrLf}")
            ' §170: Measure exact error magnitude of r. delta = 2^kBits - b*r.
            ' At this point _br144 = b*(r+1) = b*r + b, so:
            '   delta = 2^kBits - b*r = 2^kBits + b - b*(r+1) = 2^kBits + b - _br144
            ' szDelta > szB means r is wrong by more than 1 full unit, i.e. massively off.
            ' r_error ~ delta/b in limbs: szDelta - szB = approximate limb-count of r's error.
            Dim _pow170 As New mpz_t()
            gmp_lib.mpz_init(_pow170)
            gmp_lib.mpz_set_ui(_pow170, 1UI)
            gmp_lib.mpz_mul_2exp(_pow170, _pow170, New mp_bitcnt_t(CUInt(kBits)))
            gmp_lib.mpz_add(_pow170, _pow170, b)        ' 2^kBits + b
            gmp_lib.mpz_sub(_pow170, _pow170, _br144)   ' 2^kBits + b - b*(r+1) = delta
            Dim _szDelta170 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_pow170.Pointer, 4))
            Dim _delta170DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_pow170.Pointer, 8))
            Dim _delta170Top As Long = If(_szDelta170 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_delta170DPtr, CInt((_szDelta170 - 1) * 8L)), 0L)
            Dim _delta170Top2 As Long = If(_szDelta170 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_delta170DPtr, CInt((_szDelta170 - 2) * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§170] delta=2^kBits-b*r szDelta={_szDelta170:N0} szB={szB:N0} r_error_limbs~={_szDelta170 - szB:N0} top2=[{_delta170Top:X16} {_delta170Top2:X16}]{vbCrLf}")
            gmp_lib.mpz_clear(_pow170)
            gmp_lib.mpz_clear(_br144)
        End If

        ' q_approx = floor(a · r / 2^kBits)
        Dim ar As New mpz_t()
        gmp_lib.mpz_init(ar)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] computing a*r (szA={szA:N0} szR={szR:N0})...{vbCrLf}")
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szA can be 998M at 5B).
            Dim _aDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _aTop As Long = If(szA >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_aDPtr.ToInt64() + (CLng(szA) - 1L) * 8L), 0), 0L)
            Dim _aTop2 As Long = If(szA >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_aDPtr.ToInt64() + (CLng(szA) - 2L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzDiv] a top2=[{_aTop:X16} {_aTop2:X16}]{vbCrLf}")
        End If
        ' §154: log r at key positions for independent ar verification.
        If _logLevel >= 2 AndAlso szR = 21875001 Then
            Dim _r154DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r154_pos() As Long = {20904664L, 20904665L, 21000000L, 21500000L, 21874999L, 21875000L}
            Dim _sb154 As New System.Text.StringBuilder()
            _sb154.Append($"[SafeMpzDiv§154] r at key positions (szR={szR:N0}):{vbCrLf}")
            For Each _p154 As Long In _r154_pos
                Dim _v154 As Long = If(_p154 < CLng(szR), Runtime.InteropServices.Marshal.ReadInt64(_r154DPtr, CInt(_p154 * 8L)), 0L)
                _sb154.Append($"  r[{_p154:N0}]={_v154:X16}{vbCrLf}")
            Next
            AppendLog(_sb154.ToString())
        End If
        ' §5B-investigate: capture a, r boundary limbs BEFORE SafeMpzMul (r is cleared after).
        ' These are used post-mul to verify ar[0] = a[0]*r[0] mod 2^64 (exact) and that
        ' ar's top limbs are consistent with a[szA-1]*r[szR-1] (approximate, plus carries).
        ' If pre-mul a/r values look plausible but post-mul ar values are wildly off, the
        ' bug is in SafeMpzMul itself.  If pre-mul values are wrong, the bug is upstream
        ' (SafeMpzReciprocal for r; whoever produced a for a).
        Dim _5b_verify As Boolean = (szA = 175000001 AndAlso szR = 87500001)
        Dim _5b_aBot As ULong = 0UL, _5b_aTop As ULong = 0UL, _5b_aTop2 As ULong = 0UL
        Dim _5b_aMid As ULong = 0UL, _5b_aBot2 As ULong = 0UL
        Dim _5b_rBot As ULong = 0UL, _5b_rTop As ULong = 0UL, _5b_rTop2 As ULong = 0UL
        Dim _5b_rMid As ULong = 0UL, _5b_rBot2 As ULong = 0UL
        ' §5B-q-mid: capture ar at the kLimb+midIdx and kLimb+quartIdx positions BEFORE
        ' BigShiftRight, so post-shift we can verify q[mid] and q[quart] derive correctly
        ' from those ar limbs via q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem)).
        ' Mismatch ⇒ BigShiftRight middle bug.  Agreement ⇒ narrows bug to SafeMpzMul middle.
        Dim _5b_arMid0 As ULong = 0UL, _5b_arMid1 As ULong = 0UL
        Dim _5b_arQuart0 As ULong = 0UL, _5b_arQuart1 As ULong = 0UL
        ' §5B-f3: capture 100 evenly-spaced ar samples (and their +1 neighbours) pre-shift
        ' for post-shift verification of q[i] = (ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61).
        ' Any mismatch across the 100 samples ⇒ BigShiftRight has a bug at that index;
        ' all 100 matching ⇒ shift is faithful, and the bug must be in ar itself or upstream.
        Dim _f3_qIdx(99) As Long
        Dim _f3_arLo(99) As ULong
        Dim _f3_arHi(99) As ULong
        If _logLevel >= 2 AndAlso _5b_verify Then
            Dim _aD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _rD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            _5b_aBot = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, 0))
            _5b_aBot2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, 8))
            _5b_aMid = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA \ 2) * 8L)))
            _5b_aTop2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA - 2) * 8L)))
            _5b_aTop = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA - 1) * 8L)))
            _5b_rBot = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, 0))
            _5b_rBot2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, 8))
            _5b_rMid = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR \ 2) * 8L)))
            _5b_rTop2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR - 2) * 8L)))
            _5b_rTop = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR - 1) * 8L)))
            AppendLog($"[SafeMpzDiv§5B-a] a[0]={_5b_aBot:X16} a[1]={_5b_aBot2:X16} a[mid={szA \ 2:N0}]={_5b_aMid:X16} a[szA-2]={_5b_aTop2:X16} a[szA-1]={_5b_aTop:X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-r] r[0]={_5b_rBot:X16} r[1]={_5b_rBot2:X16} r[mid={szR \ 2:N0}]={_5b_rMid:X16} r[szR-2]={_5b_rTop2:X16} r[szR-1]={_5b_rTop:X16}{vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §220 (issue #55, 2026-05-22): §166 force-serial LIFTED.
        ' Original §166 forced _safeMulDop=1 for a×r because §138/§165 only forced the
        ' outer Parallel.For while inner recursive SafeMpzMul calls bypassed the gate
        ' and ran parallel, producing wrong a×r values. Like §168, the root cause was
        ' Newton premature-convergence (now fixed by §200/§201) producing a wrong r;
        ' a×r was correctly computing (wrong_r × a). With §200/§201 in place, r is
        ' always correct so a×r is always correct regardless of DOP.
        ' Original lines preserved as comments for easy revert (grep §220).
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §166 lifted — a×r runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        ' §262 (#42): a×r is computed in full then `BigShiftRight(ar, ar, kBits)` throws away the low
        ' kLimb=kBits\64 limbs — so only the HIGH part is ever used (q = ar >> kBits).  Route it
        ' through the #70 chunked-grid HIGH product: keep only the top (fullLimbs − kLimb) result
        ' limbs (+GUARD), skipping the cells whose entire output lies below the cut.  The round-up
        ' overestimate ⇒ q overestimate ⇒ §171 adj-DOWN corrects (the §107 contract), so π stays
        ' bit-identical.  This attacks the dominant divide cost (a×r ≈ 5h40m vs q×b ≈ 1h34m at 5B)
        ' AND ~halves a×r's peak RAM (chunked accumulator vs the full §gen result+shifted+sub).
        ' Gated: flag + size (>1 cell) + DOP, and OFF under _5b_verify (those diagnostics read the
        ' low limbs, which a high product does not compute).
        Dim _arFull As Long = CLng(szA) + CLng(szR)
        Dim _arKLimb As Long = kBits \ 64L
        Dim _arKeep As Long = _arFull - _arKLimb
        If _divArShortMul AndAlso (Not _5b_verify) AndAlso _arKeep > 0L _
           AndAlso (CLng(szA) > 1500000L OrElse CLng(szR) > 1500000L) _
           AndAlso MemBudget_SuggestSafeMulDop(CLng(szA), CLng(szR)) <= _recipShortMulMaxDop Then
            AppendLog($"[SafeMpzDiv§262-gate] a×r HIGH ENGAGE szA={szA:N0} szR={szR:N0} fullLimbs={_arFull:N0} kLimb={_arKLimb:N0} keep={_arKeep:N0}{vbCrLf}", 2)
            SafeMpzMul_ChunkedGrid(ar, a, r, _arKeep)
        Else
            SafeMpzMul(ar, a, r)
        End If
        ' §5B-f1: r's data buffer is needed by the §5B-f1 chunked-grid reference (below).
        ' Defer mpz_clear(r) until AFTER §5B-f1 completes to keep r alive.  Before §5B-f1
        ' was added the clear lived here directly; now it's at the end of the §5B-f1 block.
        Dim szAR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))

        ' §213 (2026-05-15, issue #66): when _5b_verify is False (which is ALL 5B-scale calls
        ' — _5b_verify is gated to szA=175,000,001 AndAlso szR=87,500,001, the 1B sqrt-step-4
        ' shape), §5B-f1 and §5B-f2 below are skipped entirely, so r is dead from this point
        ' on.  Free it now to drop ~2 GB of working set during the dangerous depth-0 §gen
        ' window that follows.  The deferred clear at the end of the §5B-f1/§5B-f2 block
        ' becomes conditional on _5b_verify so 1B-scale runs still defer.
        If Not _5b_verify Then
            gmp_lib.mpz_clear(r)
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§213] r cleared eagerly (_5b_verify=False, ~{CLng(szR) * 8L \ 1048576L:N0} MB freed){vbCrLf}")
        End If

        ' §241 (issue #69): trim pooled FFT temporaries left over from a×r before q×b
        ' allocates fresh. Measures pool retention at this boundary (census-before).
        TrimPoolAtBoundary("post-axr", 1048576UL)

        ' §5B-investigate (post-mul): verify ar boundary limbs against pre-mul a, r values.
        ' Bottom: ar[0] = (a[0]*r[0]) mod 2^64 — EXACT relation, mismatch ⇒ SafeMpzMul bug.
        ' Top: ar[szAR-1] should be ≈ high(a[szA-1]*r[szR-1]) plus accumulated carry from cross
        ' products; off-by-many-orders-of-magnitude indicates SafeMpzMul produced wrong top.
        If _logLevel >= 2 AndAlso _5b_verify Then
            Dim _arD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _arBot5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, 0))
            Dim _arBot5_2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, 8))
            Dim _arMid5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR \ 2) * 8L)))
            Dim _arTop5_2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR - 2) * 8L)))
            Dim _arTop5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR - 1) * 8L)))
            Dim _expArBot As ULong = _5b_aBot * _5b_rBot
            Dim _expArTopLow As ULong = 0UL
            Dim _expArTopHigh As ULong = System.Math.BigMul(_5b_aTop, _5b_rTop, _expArTopLow)
            AppendLog($"[SafeMpzDiv§5B-ar] ar[0]={_arBot5:X16} ar[1]={_arBot5_2:X16} ar[mid={szAR \ 2:N0}]={_arMid5:X16} ar[szAR-2]={_arTop5_2:X16} ar[szAR-1]={_arTop5:X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-arBot] actual ar[0]={_arBot5:X16}  expected (a[0]*r[0])_lo={_expArBot:X16}  match={(_arBot5 = _expArBot)}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-arTop] actual ar[szAR-1..szAR-2]=[{_arTop5:X16} {_arTop5_2:X16}]  a[top]*r[top]=[hi={_expArTopHigh:X16} lo={_expArTopLow:X16}]  (top should be ≈ hi+carry; lo+carry should be near {_arTop5_2:X16}){vbCrLf}", 5)   ' §252 (#95)
            ' §5B-q-mid: capture ar at the q-mid and q-quart shift positions for post-shift verification.
            ' kBits=11,200,000,067 ⇒ kLimb=175,000,001, kRem=3.  q has szQ=87,500,001 limbs.
            ' q[mid=43,750,000] derives from ar[218,750,001] and ar[218,750,002].
            ' q[quart=21,875,000] derives from ar[196,875,001] and ar[196,875,002].
            Dim _5bKLimb As Long = kBits \ 64L
            Dim _5bMidArIdx As Long = _5bKLimb + 43750000L
            Dim _5bQuartArIdx As Long = _5bKLimb + 21875000L
            _5b_arMid0 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_5bMidArIdx * 8L)))
            _5b_arMid1 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt((_5bMidArIdx + 1L) * 8L)))
            _5b_arQuart0 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_5bQuartArIdx * 8L)))
            _5b_arQuart1 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt((_5bQuartArIdx + 1L) * 8L)))
            AppendLog($"[SafeMpzDiv§5B-arQ-src] ar[{_5bQuartArIdx:N0}]={_5b_arQuart0:X16} ar[{_5bQuartArIdx+1:N0}]={_5b_arQuart1:X16} ar[{_5bMidArIdx:N0}]={_5b_arMid0:X16} ar[{_5bMidArIdx+1:N0}]={_5b_arMid1:X16}{vbCrLf}", 5)   ' §252 (#95)
            ' §5B-f3 capture: 100 evenly-spaced ar samples covering q's full range [0..szQ-1].
            ' szQ = 87,500,001, kLimb = 175,000,001.  Sample positions evenly across q indices.
            For _f3s As Integer = 0 To 99
                Dim _f3_qi As Long = CLng(_f3s) * 87499999L \ 99L  ' 0, 884K, 1.77M, ..., 87.5M-1
                _f3_qIdx(_f3s) = _f3_qi
                Dim _f3_arIdxLo As Long = _5bKLimb + _f3_qi
                Dim _f3_arIdxHi As Long = _5bKLimb + _f3_qi + 1L
                _f3_arLo(_f3s) = If(_f3_arIdxLo >= 0L AndAlso _f3_arIdxLo < CLng(szAR), CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_f3_arIdxLo * 8L))), 0UL)
                _f3_arHi(_f3s) = If(_f3_arIdxHi >= 0L AndAlso _f3_arIdxHi < CLng(szAR), CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_f3_arIdxHi * 8L))), 0UL)
            Next
            AppendLog($"[SafeMpzDiv§5B-f3] captured 100 ar samples for post-shift verification (kLimb={_5bKLimb:N0} kRem=3){vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §5B-f1 (DONE — run 14 confirmed ar = a × r is fully correct via 1000
        ' evenly-spaced position scans, mismatches=0).  Disabled to save ~80 min.
        ' Re-enable by flipping _F1_ENABLED to True if the diagnostic needs to re-run.
        Const _F1_ENABLED As Boolean = False
        ' §5B-f1: Chunked-grid independent reference for the FULL a × r product.
        ' Computes the 262.5M-limb result via a 117 × 59 = 6,903-cell grid of
        ' ≤ 1.5M × 1.5M (≤ 3M total per cell — reliable mpz_mul per §160), then
        ' scans our SafeMpzMul ar against the reference at 1,000 evenly-spaced
        ' positions.  Mirrors the §5B-e pattern but for the full a × r product.
        '
        ' Outcome:
        '   Mismatches > 0 ⇒ ar is wrong at those positions; bug is in §gen
        '                    accumulation (mul_2exp shift / GmpRaw_add carry).
        '   Mismatches = 0 ⇒ ar is fully correct; the §171 trigger must be
        '                    driven by something else (kBits computation off,
        '                    BigShiftRight wrong at a position F-3 missed, or
        '                    the §171 trigger logic itself misjudges Barrett).
        '
        ' Pre-allocates _refAcc and _ckShifted buffers via VirtualAlloc to
        ' 270M limbs (~2.16 GB each) — _mp_alloc set to full pre-allocated
        ' size so mul_2exp and add never trigger realloc (same pattern as §gen).
        If _logLevel >= 2 AndAlso _5b_verify AndAlso _F1_ENABLED Then
            Const _F1_CHUNK As Integer = 1500000
            Const _F1_MAX_LIMBS As Integer = 270_000_000
            Dim _F1_MAX_BYTES As Long = CLng(_F1_MAX_LIMBS) * 8L
            Dim _F1_aD As Long = Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8)
            Dim _F1_aSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(a.Pointer, 4))
            Dim _F1_rD As Long = Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8)
            Dim _F1_rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            AppendLog($"[SafeMpzDiv§5B-f1] starting chunked-grid a × r reference (chunk={_F1_CHUNK:N0}, prealloc={_F1_MAX_LIMBS:N0} limbs/buf, {_F1_MAX_BYTES \ 1048576L:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f1] a sz={_F1_aSz:N0} r sz={_F1_rSz:N0}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F1_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F1_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F1_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F1_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F1_eAccBuf = IntPtr.Zero OrElse _F1_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f1] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F1_eAccBuf <> IntPtr.Zero Then VirtualFree(_F1_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F1_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F1_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F1_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_refAcc)
                Dim _F1_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F1_refAcc, 0))
                Dim _F1_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F1_ra_initPtr, _F1_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_refAcc, 0, _F1_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F1_refAcc, 8, _F1_eAccBuf.ToInt64())
                Dim _F1_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_ckShifted)
                Dim _F1_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F1_ckShifted, 0))
                Dim _F1_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F1_cs_initPtr, _F1_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 0, _F1_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F1_ckShifted, 8, _F1_eShiftBuf.ToInt64())
                Dim _F1_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_ckPartial)
                Dim _F1_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F1_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F1_numCkA As Integer = (_F1_aSz + _F1_CHUNK - 1) \ _F1_CHUNK
                Dim _F1_numCkB As Integer = (_F1_rSz + _F1_CHUNK - 1) \ _F1_CHUNK
                Dim _F1_ckCount As Integer = 0
                For i As Integer = 0 To _F1_numCkA - 1
                    Dim _F1_aOff As Long = CLng(i) * CLng(_F1_CHUNK)
                    Dim _F1_aSzCk As Integer = CInt(System.Math.Min(CLng(_F1_CHUNK), CLng(_F1_aSz) - _F1_aOff))
                    If _F1_aSzCk <= 0 Then Continue For
                    While _F1_aSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F1_aD + (_F1_aOff + CLng(_F1_aSzCk - 1)) * 8L)) = 0L
                        _F1_aSzCk -= 1
                    End While
                    If _F1_aSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F1_ckA, 0, _F1_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F1_ckA, 4, _F1_aSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F1_ckA, 8, _F1_aD + _F1_aOff * 8L)
                    For j As Integer = 0 To _F1_numCkB - 1
                        Dim _F1_bOff As Long = CLng(j) * CLng(_F1_CHUNK)
                        Dim _F1_bSzCk As Integer = CInt(System.Math.Min(CLng(_F1_CHUNK), CLng(_F1_rSz) - _F1_bOff))
                        If _F1_bSzCk <= 0 Then Continue For
                        While _F1_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F1_rD + (_F1_bOff + CLng(_F1_bSzCk - 1)) * 8L)) = 0L
                            _F1_bSzCk -= 1
                        End While
                        If _F1_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F1_ckB, 0, _F1_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F1_ckB, 4, _F1_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F1_ckB, 8, _F1_rD + _F1_bOff * 8L)
                        GmpRaw_mul(_F1_ckPartial, _F1_ckA, _F1_ckB)
                        Dim _F1_shiftBits As ULong = CULng(_F1_aOff + _F1_bOff) * 64UL
                        If _F1_shiftBits = 0UL Then
                            GmpRaw_add(_F1_refAcc, _F1_refAcc, _F1_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 4, 0)
                            Dim _F1_shiftSrc As IntPtr = _F1_ckPartial
                            Dim _F1_shiftRem As ULong = _F1_shiftBits
                            While _F1_shiftRem > 0UL
                                Dim _F1_chunkBits As UInteger = CUInt(System.Math.Min(_F1_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F1_ckShifted, _F1_shiftSrc, _F1_chunkBits)
                                _F1_shiftSrc = _F1_ckShifted
                                _F1_shiftRem -= CULng(_F1_chunkBits)
                            End While
                            GmpRaw_add(_F1_refAcc, _F1_refAcc, _F1_ckShifted)
                        End If
                        _F1_ckCount += 1
                    Next j
                Next i
                Dim _F1_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F1_refAcc, 4))
                Dim _F1_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_refAcc, 8))
                Dim _F1_arDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                AppendLog($"[SafeMpzDiv§5B-f1] reference complete: subProducts={_F1_ckCount:N0} refSz={_F1_refSz:N0} ourArSz={szAR:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Scan ar against reference at 1,000 evenly-spaced positions across the full range.
                Const _F1_NUM_SAMPLES As Integer = 1000
                Dim _F1_mismatchCount As Integer = 0
                Dim _F1_firstMismatchIdx As Long = -1L
                Dim _F1_logCount As Integer = 0
                Dim _F1_maxIdx As Long = CLng(System.Math.Min(_F1_refSz, szAR)) - 1L
                For _F1s As Integer = 0 To _F1_NUM_SAMPLES - 1
                    Dim _F1_idx As Long = If(_F1_NUM_SAMPLES > 1, CLng(_F1s) * _F1_maxIdx \ CLng(_F1_NUM_SAMPLES - 1), 0L)
                    Dim _F1_refV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F1_refDPtr, CInt(_F1_idx * 8L)))
                    Dim _F1_arV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F1_arDPtr, CInt(_F1_idx * 8L)))
                    If _F1_refV <> _F1_arV Then
                        _F1_mismatchCount += 1
                        If _F1_firstMismatchIdx = -1L Then _F1_firstMismatchIdx = _F1_idx
                        If _F1_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f1 MISMATCH] sample={_F1s} ar[{_F1_idx:N0}] reference={_F1_refV:X16} ourSafeMpzMul={_F1_arV:X16}{vbCrLf}", 5)   ' §252 (#95)
                            _F1_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f1 SUMMARY] scanned {_F1_NUM_SAMPLES} ar positions across [0..{_F1_maxIdx:N0}], mismatches={_F1_mismatchCount}, firstMismatchArIdx={_F1_firstMismatchIdx}{vbCrLf}", 5)   ' §252 (#95)
                ' Cleanup — _refAcc and _ckShifted have swapped-in VirtualAlloc'd buffers.
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckShifted)
                GmpRaw_clear(_F1_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckB)
                VirtualFree(_F1_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F1_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If
        ' §5B-f2: Verify r is a true reciprocal by computing r × b via chunked-grid
        ' and checking the result lies in [2^kBits - b, 2^kBits).  If r is correct,
        ' the top limb (r*b)[kLimb] should be in [0, 2^kRem-1] and limbs above kLimb
        ' should be zero.  If r is short (Newton converged early), the result will
        ' be MUCH smaller — fewer total limbs, with high-zone limbs missing entirely.
        ' Grid: 59 × 59 = 3,481 sub-products at ≤ 1.5M × 1.5M (≤ 3M total).
        ' Result: 175M limbs ≈ 1.4 GB.  Pre-alloc 180M-limb buffers ≈ 1.44 GB each.
        ' Estimated time: ~40 min (fewer sub-products than F-1, smaller buffers).
        ' Re-enabled for Option H: F-2's original check was TOO WEAK (only verified top
        ' ~130 bits of r×b).  Extended logging at multiple intermediate positions to
        ' discriminate "r correct" from "r short by 2^5.45B".  See Option H discussion
        ' in README §5B-investigate.
        Const _F2_ENABLED As Boolean = True
        If _logLevel >= 2 AndAlso _5b_verify AndAlso _F2_ENABLED Then
            Const _F2_CHUNK As Integer = 1500000
            Const _F2_MAX_LIMBS As Integer = 180_000_000
            Dim _F2_MAX_BYTES As Long = CLng(_F2_MAX_LIMBS) * 8L
            Dim _F2_rD As Long = Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8)
            Dim _F2_rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            Dim _F2_bD As Long = Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8)
            Dim _F2_bSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
            Dim _F2_kLimb As Long = kBits \ 64L
            Dim _F2_kRem As Integer = CInt(kBits Mod 64L)
            AppendLog($"[SafeMpzDiv§5B-f2] starting r×b chunked-grid reference (chunk={_F2_CHUNK:N0}, prealloc={_F2_MAX_LIMBS:N0} limbs/buf, {_F2_MAX_BYTES \ 1048576L:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f2] r sz={_F2_rSz:N0} b sz={_F2_bSz:N0} kBits={kBits:N0} kLimb={_F2_kLimb:N0} kRem={_F2_kRem}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F2_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F2_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F2_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F2_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F2_eAccBuf = IntPtr.Zero OrElse _F2_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f2] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F2_eAccBuf <> IntPtr.Zero Then VirtualFree(_F2_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F2_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F2_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F2_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_refAcc)
                Dim _F2_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F2_refAcc, 0))
                Dim _F2_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F2_ra_initPtr, _F2_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_refAcc, 0, _F2_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F2_refAcc, 8, _F2_eAccBuf.ToInt64())
                Dim _F2_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_ckShifted)
                Dim _F2_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F2_ckShifted, 0))
                Dim _F2_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F2_cs_initPtr, _F2_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 0, _F2_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F2_ckShifted, 8, _F2_eShiftBuf.ToInt64())
                Dim _F2_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_ckPartial)
                Dim _F2_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F2_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F2_numCkR As Integer = (_F2_rSz + _F2_CHUNK - 1) \ _F2_CHUNK
                Dim _F2_numCkB As Integer = (_F2_bSz + _F2_CHUNK - 1) \ _F2_CHUNK
                Dim _F2_ckCount As Integer = 0
                For i As Integer = 0 To _F2_numCkR - 1
                    Dim _F2_rOff As Long = CLng(i) * CLng(_F2_CHUNK)
                    Dim _F2_rSzCk As Integer = CInt(System.Math.Min(CLng(_F2_CHUNK), CLng(_F2_rSz) - _F2_rOff))
                    If _F2_rSzCk <= 0 Then Continue For
                    While _F2_rSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F2_rD + (_F2_rOff + CLng(_F2_rSzCk - 1)) * 8L)) = 0L
                        _F2_rSzCk -= 1
                    End While
                    If _F2_rSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F2_ckA, 0, _F2_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F2_ckA, 4, _F2_rSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F2_ckA, 8, _F2_rD + _F2_rOff * 8L)
                    For j As Integer = 0 To _F2_numCkB - 1
                        Dim _F2_bOff As Long = CLng(j) * CLng(_F2_CHUNK)
                        Dim _F2_bSzCk As Integer = CInt(System.Math.Min(CLng(_F2_CHUNK), CLng(_F2_bSz) - _F2_bOff))
                        If _F2_bSzCk <= 0 Then Continue For
                        While _F2_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F2_bD + (_F2_bOff + CLng(_F2_bSzCk - 1)) * 8L)) = 0L
                            _F2_bSzCk -= 1
                        End While
                        If _F2_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F2_ckB, 0, _F2_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F2_ckB, 4, _F2_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F2_ckB, 8, _F2_bD + _F2_bOff * 8L)
                        GmpRaw_mul(_F2_ckPartial, _F2_ckA, _F2_ckB)
                        Dim _F2_shiftBits As ULong = CULng(_F2_rOff + _F2_bOff) * 64UL
                        If _F2_shiftBits = 0UL Then
                            GmpRaw_add(_F2_refAcc, _F2_refAcc, _F2_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 4, 0)
                            Dim _F2_shiftSrc As IntPtr = _F2_ckPartial
                            Dim _F2_shiftRem As ULong = _F2_shiftBits
                            While _F2_shiftRem > 0UL
                                Dim _F2_chunkBits As UInteger = CUInt(System.Math.Min(_F2_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F2_ckShifted, _F2_shiftSrc, _F2_chunkBits)
                                _F2_shiftSrc = _F2_ckShifted
                                _F2_shiftRem -= CULng(_F2_chunkBits)
                            End While
                            GmpRaw_add(_F2_refAcc, _F2_refAcc, _F2_ckShifted)
                        End If
                        _F2_ckCount += 1
                    Next j
                Next i
                Dim _F2_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F2_refAcc, 4))
                Dim _F2_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_refAcc, 8))
                AppendLog($"[SafeMpzDiv§5B-f2] r×b reference complete: subProducts={_F2_ckCount:N0} refSz={_F2_refSz:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Inspect (r×b) at and around kLimb to assess r's correctness.
                ' If r is exact: refSz should be kLimb or kLimb+1 (with limb kLimb in [0, 2^kRem)).
                ' If r is short: refSz < kLimb (high zone is empty), or limb kLimb-1 is much smaller than 2^64.
                ' If r is large: refSz > kLimb+1, or limb kLimb has bits set above bit kRem.
                Dim _f2GetLimb = Function(_idx As Long) As ULong
                                     If _idx >= 0L AndAlso _idx < CLng(_F2_refSz) Then
                                         Return CULng(Runtime.InteropServices.Marshal.ReadInt64(_F2_refDPtr, CInt(_idx * 8L)))
                                     End If
                                     Return 0UL
                                 End Function
                Dim _F2_v_kL As ULong = _f2GetLimb(_F2_kLimb)
                Dim _F2_v_kLm1 As ULong = _f2GetLimb(_F2_kLimb - 1L)
                Dim _F2_v_kLm2 As ULong = _f2GetLimb(_F2_kLimb - 2L)
                Dim _F2_v_kLp1 As ULong = _f2GetLimb(_F2_kLimb + 1L)
                Dim _F2_v_kLp2 As ULong = _f2GetLimb(_F2_kLimb + 2L)
                Dim _F2_v_top As ULong = _f2GetLimb(CLng(_F2_refSz) - 1L)
                Dim _F2_v_top2 As ULong = _f2GetLimb(CLng(_F2_refSz) - 2L)
                Dim _F2_v_bot As ULong = _f2GetLimb(0L)
                Dim _F2_v_bot1 As ULong = _f2GetLimb(1L)
                Dim _F2_kBitsBoundary As ULong = If(_F2_kRem >= 64, 0UL, CULng(1L << _F2_kRem) - 1UL)
                Dim _F2_v_kL_aboveKRem As ULong = _F2_v_kL >> _F2_kRem
                Dim _F2_v_kL_inRange As Boolean = (_F2_v_kL <= _F2_kBitsBoundary)
                Dim _F2_aboveKLimbAllZero As Boolean = (_F2_v_kLp1 = 0UL AndAlso _F2_v_kLp2 = 0UL)
                AppendLog($"[SafeMpzDiv§5B-f2 inspect] r×b[0]={_F2_v_bot:X16} r×b[1]={_F2_v_bot1:X16} r×b[kLimb-2]={_F2_v_kLm2:X16} r×b[kLimb-1]={_F2_v_kLm1:X16} r×b[kLimb]={_F2_v_kL:X16} r×b[kLimb+1]={_F2_v_kLp1:X16} r×b[kLimb+2]={_F2_v_kLp2:X16} r×b[top-1]={_F2_v_top2:X16} r×b[top]={_F2_v_top:X16}{vbCrLf}", 5)   ' §252 (#95)
                AppendLog($"[SafeMpzDiv§5B-f2 verdict] refSz={_F2_refSz:N0} kLimb={_F2_kLimb:N0} kRem={_F2_kRem} kRemMask={_F2_kBitsBoundary:X16} r×b[kLimb]>>kRem={_F2_v_kL_aboveKRem:X16} (should be 0 if r×b<2^kBits) inKBitsRange={_F2_v_kL_inRange} aboveKLimbAllZero={_F2_aboveKLimbAllZero}{vbCrLf}", 5)   ' §252 (#95)
                ' Option H: extended r×b logging at intermediate positions to discriminate
                ' "r correct" (FF block extends ~87.5M limbs from kLimb-1 down) from
                ' "r short by 2^5.45B" (FF block extends only ~3M limbs).
                ' Distances from kLimb (descending): 3M, 5M, 10M, 50M, 87M, 90M, 130M.
                ' For correct r: all of 3M..87M should be 0xFFFFFFFFFFFFFFFF; 90M, 130M
                ' may differ (within δ region).
                ' For r short by 2^5.45B: only 3M is in FF block; 5M..130M all NOT FF.
                Dim _F2_offsets As Long() = New Long() {3000000L, 5000000L, 10000000L, 50000000L, 87000000L, 90000000L, 130000000L}
                Dim _F2_ffBoundary As Long = -1L  ' first non-FF position from top going down
                For Each _F2_off As Long In _F2_offsets
                    Dim _F2_idx As Long = _F2_kLimb - _F2_off
                    Dim _F2_v As ULong = _f2GetLimb(_F2_idx)
                    Dim _F2_isFF As Boolean = (_F2_v = &HFFFFFFFFFFFFFFFFUL)
                    AppendLog($"[SafeMpzDiv§5B-f2 H] r×b[kLimb-{_F2_off:N0}={_F2_idx:N0}]={_F2_v:X16} isFF={_F2_isFF}{vbCrLf}", 5)   ' §252 (#95)
                    If Not _F2_isFF AndAlso _F2_ffBoundary = -1L Then _F2_ffBoundary = _F2_off
                Next
                If _F2_ffBoundary = -1L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] All checked positions are 0xFF...FF: FF block extends >130M limbs → r is essentially correct.{vbCrLf}", 5)   ' §252 (#95)
                ElseIf _F2_ffBoundary < 5000000L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends within {_F2_ffBoundary:N0} limbs of top → r SHORT BY ≥ ~2^5.45B (Newton precision failure).{vbCrLf}", 5)   ' §252 (#95)
                ElseIf _F2_ffBoundary < 87000000L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends within {_F2_ffBoundary:N0} limbs of top → r SHORT BY some amount; investigate Newton precision.{vbCrLf}", 5)   ' §252 (#95)
                Else
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends at {_F2_ffBoundary:N0} limbs (at/past expected ≈87.5M boundary) → r appears correct; bug is elsewhere.{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' Cleanup
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckShifted)
                GmpRaw_clear(_F2_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckB)
                VirtualFree(_F2_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F2_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If

        ' §5B-f1/f2 (deferred from §166 site): now that the chunked-grid references are done,
        ' release r.  When both diagnostics are gated off, this clears r at exactly the same
        ' logical point as the original code.
        ' §213 (2026-05-15, issue #66): now conditional — when _5b_verify=False the eager
        ' clear above already fired.  Only the 1B sqrt-step-4 path (_5b_verify=True) reaches
        ' the deferred clear here.
        If _5b_verify Then gmp_lib.mpz_clear(r)
        ' §135 save slots: ar[64654664/65] captured inside §111 block, used after BigShiftRight.
        Dim _ar135_v0 As Long = 0L
        Dim _ar135_v1 As Long = 0L
        ' §141 save slots: ar[65139832/33] → expected q[21389832] (A2/B2 midpoint).
        Dim _ar141_v0 As Long = 0L
        Dim _ar141_v1 As Long = 0L
        ' §142: A2-range q verification — save ar pairs for q[14583334+j] at j=0,1M..6M.
        ' ar index = kLimb + q_index = 43750000 + (14583334 + j) = 58333334 + j.
        ' Evenly-spaced j: 0, 1000000, 2000000, 3000000, 4000000, 5000000, 6000000.
        Dim _ar142_pairs(6, 1) As Long  ' (7 points) × (v0, v1)
        Dim _ar142_qIdx() As Long = {14583334L, 15583334L, 16583334L, 17583334L, 18583334L, 19583334L, 20583334L}
        ' §149: Top q-range — save ar pairs for q[k] at k in [20950000..21875000].
        ' These q limbs (q[20904664..21875000]) are the inputs to A2×B2[13612996] in q×b,
        ' and were NOT verified by §135/§141/§142.  A mismatch here pinpoints the bug.
        ' ar index = 43750000 + k.  Last valid ar index = 65625000 (szAR=65625001).
        Dim _ar149_pairs(10, 1) As Long  ' (11 points) × (v0, v1)
        Dim _ar149_qIdx() As Long = {20950000L, 21000000L, 21100000L, 21200000L, 21300000L, 21500000L, 21600000L, 21700000L, 21800000L, 21874999L, 21875000L}
        ' §151: Gap q-range [20583334..20950000] — unverified by §142/§149, all contribute to A2×B2[13612996].
        Dim _ar151_pairs(3, 1) As Long  ' (4 points) × (v0, v1)
        Dim _ar151_qIdx() As Long = {20600000L, 20700000L, 20800000L, 20900000L}
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szAR can be 1.26B at 5B).
            Dim _arDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _arTop As Long = If(szAR >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (CLng(szAR) - 1L) * 8L), 0), 0L)
            Dim _arTop2 As Long = If(szAR >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (CLng(szAR) - 2L) * 8L), 0), 0L)
            ' Boundary limbs: at index kBits\64 and kBits\64+1. q_bot = (bnd0 >> kBits%64) | (bnd1 << (64 - kBits%64))
            Dim _kLimb As Long = kBits \ 64L
            Dim _kRem As Integer = CInt(kBits Mod 64L)
            ' §239 (2026-05-31, issue #71 residual): 64-bit-safe absolute-address read.
            ' szAR ≈ 1.26B at 5B, so _kLimb (= kBits\64 ≈ 998M) × 8 = 7.99 GB overflows
            ' Int32; with overflow checks off CInt wraps to a NEGATIVE offset → Marshal
            ' reads ~601 MB before the buffer → AccessViolation. The §215 fix at line 5066
            ' fixed _arTop but missed these two boundary reads. Same pattern as §237/c7a0c76.
            Dim _arBnd0 As Long = If(_kLimb >= 0L AndAlso _kLimb < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + _kLimb * 8L), 0), 0L)
            Dim _arBnd1 As Long = If(_kLimb + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (_kLimb + 1L) * 8L), 0), 0L)
            Dim _arBot As Long = If(szAR >= 1, Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, 0), 0L)
            Dim _qBotExpected As Long = CLng(CULng(_arBnd0) >> _kRem) Or CLng(CULng(_arBnd1) << (64 - _kRem))
            AppendLog($"[SafeMpzDiv] ar pre-shift: szAR={szAR:N0} top2=[{_arTop:X16} {_arTop2:X16}] bot=[{_arBot:X16}] bnd=[{_arBnd0:X16} {_arBnd1:X16}] q_bot_expected={_qBotExpected:X16}{vbCrLf}")
            ' §111/§112: log error-limb and sparse sweep of ar[43750002..64654663].
            If szAR = 65625001 Then
                Const _ARD As Long = 64654664L
                Dim _arErrL As Long = If(_ARD < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ARD * 8L)), 0L)
                Dim _arErrL1 As Long = If(_ARD + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ARD + 1L) * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§111] ar[{_ARD:N0}]={_arErrL:X16} ar[{_ARD+1:N0}]={_arErrL1:X16}{vbCrLf}")
                _ar135_v0 = _arErrL   ' §135: save for post-BigShiftRight verification
                _ar135_v1 = _arErrL1
                ' §112: sparse sweep across the middle zone to find where ar goes wrong.
                ' Also includes 65139832/33 which correspond to q[21389832] (A2/B2 midpoint check).
                Dim _sweepPositions() As Long = {43750002L, 45000000L, 47000000L, 50000000L, 52000000L, 55000000L, 57000000L, 58333334L, 58333335L, 60000000L, 62000000L, 64000000L, 64654663L, 65139832L, 65139833L}
                Dim _sweepSb As New System.Text.StringBuilder()
                _sweepSb.Append($"[SafeMpzDiv§112] ar sparse sweep (szAR={szAR:N0}):{vbCrLf}")
                For Each _sp As Long In _sweepPositions
                    Dim _sv As Long = If(_sp < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_sp * 8L)), 0L)
                    _sweepSb.Append($"  ar[{_sp:N0}]={_sv:X16}{vbCrLf}")
                Next
                AppendLog(_sweepSb.ToString())
                ' Save ar[65139832/33] for §141 verification of q[21389832] after BigShiftRight.
                _ar141_v0 = If(65139832L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(65139832L * 8L)), 0L)
                _ar141_v1 = If(65139833L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(65139833L * 8L)), 0L)
                ' §149: save ar pairs for top q-range verification.
                For _i149 As Integer = 0 To 10
                    Dim _ar149_base As Long = 43750000L + _ar149_qIdx(_i149)
                    _ar149_pairs(_i149, 0) = If(_ar149_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar149_base * 8L)), 0L)
                    _ar149_pairs(_i149, 1) = If(_ar149_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar149_base + 1L) * 8L)), 0L)
                Next
                ' §151: save ar pairs for gap q-range [20583334..20950000] verification.
                For _i151 As Integer = 0 To 3
                    Dim _ar151_base As Long = 43750000L + _ar151_qIdx(_i151)
                    _ar151_pairs(_i151, 0) = If(_ar151_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar151_base * 8L)), 0L)
                    _ar151_pairs(_i151, 1) = If(_ar151_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar151_base + 1L) * 8L)), 0L)
                Next
                ' §142: save ar pairs for A2-range q verification.
                For _i142 As Integer = 0 To 6
                    Dim _ar142_base As Long = 43750000L + _ar142_qIdx(_i142)  ' = 58333334 + j
                    _ar142_pairs(_i142, 0) = If(_ar142_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar142_base * 8L)), 0L)
                    _ar142_pairs(_i142, 1) = If(_ar142_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar142_base + 1L) * 8L)), 0L)
                Next
                ' §132: verify q[14583334] from ar — q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem))
                ' kLimb=43750000, kRem=27 → q[14583334] = (ar[58333334] >> 27) | (ar[58333335] << 37)
                Const _Q132_IDX As Long = 14583334L
                Const _AR132_0 As Long = 58333334L   ' kLimb + Q132_IDX
                Const _AR132_1 As Long = 58333335L
                Dim _ar132_v0 As Long = If(_AR132_0 < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_AR132_0 * 8L)), 0L)
                Dim _ar132_v1 As Long = If(_AR132_1 < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_AR132_1 * 8L)), 0L)
                Dim _q132_expected As Long = CLng(CULng(_ar132_v0) >> 27) Or CLng(CULng(_ar132_v1) << 37)
                AppendLog($"[SafeMpzDiv§132] ar[{_AR132_0:N0}]={_ar132_v0:X16} ar[{_AR132_1:N0}]={_ar132_v1:X16} → expected q[{_Q132_IDX:N0}]={_q132_expected:X16}{vbCrLf}")
            End If
        End If
        AppendLog($"[SafeMpzDiv] a*r done: szAR={szAR:N0}; shifting right by kBits={kBits:N0}...{vbCrLf}")
        BigShiftRight(ar, ar, kBits)
        ' §171-ckpt: szQ assignment lifted (Dim moved to top of SafeMpzDiv).
        szQ = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szQ ≈ 259M at 5B — just under Int32 threshold).
            Dim _qDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _qTop As Long = If(szQ >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qDPtr.ToInt64() + (CLng(szQ) - 1L) * 8L), 0), 0L)
            Dim _qTop2 As Long = If(szQ >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qDPtr.ToInt64() + (CLng(szQ) - 2L) * 8L), 0), 0L)
            Dim _qBot As Long = If(szQ >= 1, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 0), 0L)
            Dim _qBot2 As Long = If(szQ >= 2, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 8), 0L)
            AppendLog($"[SafeMpzDiv] q_approx ready: szQ={szQ:N0} top2limbs=[{_qTop:X16} {_qTop2:X16}] bot2limbs=[{_qBot:X16} {_qBot2:X16}]{vbCrLf}")
            ' §5B-investigate (post-shift): log q at multiple positions and verify BigShiftRight
            ' at the boundary.  q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem)).
            ' Mismatch between this expected value (computed from saved ar limbs) and actual q[i]
            ' would indicate BigShiftRight produced wrong output; agreement narrows the bug to
            ' SafeMpzMul.  At kBits=11,200,000,067: kLimb=175,000,001, kRem=3.  Top valid q
            ' index is szQ-1 = 87,500,000, which corresponds to ar[262,500,001] = ar[szAR-1].
            If szQ = 87500001 Then
                Dim _qMid5 As Long = If(CLng(szQ \ 2) < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(CLng(szQ \ 2) * 8L)), 0L)
                Dim _qBotPos5 As Long = If(1L < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 8), 0L)
                Dim _qQuart5 As Long = Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(21875000L * 8L))
                AppendLog($"[SafeMpzDiv§5B-q] q[0]={_qBot:X16} q[1]={_qBotPos5:X16} q[quart=21,875,000]={_qQuart5:X16} q[mid={szQ \ 2:N0}]={_qMid5:X16} q[szQ-2]={_qTop2:X16} q[szQ-1]={_qTop:X16}{vbCrLf}", 5)   ' §252 (#95)
                ' §5B-q-mid: verify q[mid] and q[quart] derive correctly from saved ar limbs.
                ' kRem=3, so q[i] = (ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61).
                ' If actual ≠ expected ⇒ BigShiftRight has a 5B middle-limb bug.
                ' If actual = expected ⇒ BigShiftRight is faithful at these positions, narrowing
                ' the bug to SafeMpzMul middle limbs.
                Dim _kLimbQ5 As Long = kBits \ 64L
                Dim _expQMid As ULong = (_5b_arMid0 >> 3) Or (_5b_arMid1 << 61)
                Dim _expQQuart As ULong = (_5b_arQuart0 >> 3) Or (_5b_arQuart1 << 61)
                Dim _actQMidU As ULong = CULng(_qMid5)
                Dim _actQQuartU As ULong = CULng(_qQuart5)
                AppendLog($"[SafeMpzDiv§5B-q-quart] actual q[21,875,000]={_actQQuartU:X16}  expected (ar[{21875000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQQuart:X16}  match={(_actQQuartU = _expQQuart)}{vbCrLf}", 5)   ' §252 (#95)
                AppendLog($"[SafeMpzDiv§5B-q-mid]   actual q[43,750,000]={_actQMidU:X16}  expected (ar[{43750000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQMid:X16}  match={(_actQMidU = _expQMid)}{vbCrLf}", 5)   ' §252 (#95)
                ' §5B-f3 verify: scan all 100 captured pre-shift samples against actual q.
                ' Mismatch ⇒ BigShiftRight is wrong at that index.  All match ⇒ shift is faithful
                ' (so the bug must be in ar itself, or in the kBits computation, or upstream).
                Dim _f3_mismatchCount As Integer = 0
                Dim _f3_firstMismatch As Integer = -1
                Dim _f3_logCount As Integer = 0
                For _f3s As Integer = 0 To 99
                    Dim _f3_expQ As ULong = (_f3_arLo(_f3s) >> 3) Or (_f3_arHi(_f3s) << 61)
                    Dim _f3_qi As Long = _f3_qIdx(_f3s)
                    Dim _f3_actQ As ULong = If(_f3_qi >= 0L AndAlso _f3_qi < CLng(szQ), CULng(Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(_f3_qi * 8L))), 0UL)
                    If _f3_expQ <> _f3_actQ Then
                        _f3_mismatchCount += 1
                        If _f3_firstMismatch = -1 Then _f3_firstMismatch = _f3s
                        If _f3_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f3 MISMATCH] sample={_f3s} q[{_f3_qi:N0}] expected={_f3_expQ:X16} actual={_f3_actQ:X16} (ar_pre[{_kLimbQ5 + _f3_qi:N0}]={_f3_arLo(_f3s):X16} ar_pre[+1]={_f3_arHi(_f3s):X16}){vbCrLf}", 5)   ' §252 (#95)
                            _f3_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f3 SUMMARY] scanned 100 q positions, mismatches={_f3_mismatchCount}, firstMismatchSampleIdx={_f3_firstMismatch}{vbCrLf}", 5)   ' §252 (#95)
            End If
            ' §113: log q middle limbs to verify BigShiftRight correctness.
            If szQ = 21875001 Then
                Dim _q113Positions() As Long = {10937500L, 20904664L}
                Dim _q113Sb As New System.Text.StringBuilder()
                _q113Sb.Append($"[SafeMpzDiv§113] q middle limbs (szQ={szQ:N0}):{vbCrLf}")
                For Each _qp As Long In _q113Positions
                    Dim _qv As Long = If(_qp < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(_qp * 8L)), 0L)
                    _q113Sb.Append($"  q[{_qp:N0}]={_qv:X16}{vbCrLf}")
                Next
                AppendLog(_q113Sb.ToString())
            End If
            ' §135: verify q[20904664] is consistent with ar[64654664/65] saved in §111.
            ' kBits=2800000027 → kLimb=43750000, kRem=27.
            ' q[20904664] = (ar[64654664] >> 27) | (ar[64654665] << 37)
            If szQ = 21875001 AndAlso _ar135_v0 <> 0L Then
                Const _Q135_IDX As Long = 20904664L
                Dim _q135_expected As Long = CLng(CULng(_ar135_v0) >> 27) Or CLng(CULng(_ar135_v1) << 37)
                Dim _qDPtr135 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _q135_actual As Long = If(_Q135_IDX < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr135, CInt(_Q135_IDX * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§135] ar[64654664]={_ar135_v0:X16} ar[64654665]={_ar135_v1:X16} → q[{_Q135_IDX:N0}] expected={_q135_expected:X16} actual={_q135_actual:X16} match={_q135_expected = _q135_actual}{vbCrLf}")
            End If
            ' §142: A2-range q sweep — verify q at 7 evenly-spaced positions from saved ar pairs.
            If szQ = 21875001 AndAlso _ar142_pairs(0, 0) <> 0L Then
                Dim _qDPtr142 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _bDPtr142 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
                Dim _sb142 As New System.Text.StringBuilder()
                _sb142.Append($"[SafeMpzDiv§142] A2-range q vs expected-from-ar vs b (kRem=27):{vbCrLf}")
                For _i142 As Integer = 0 To 6
                    Dim _qj As Long = _ar142_qIdx(_i142)
                    Dim _exp142 As Long = CLng(CULng(_ar142_pairs(_i142, 0)) >> 27) Or CLng(CULng(_ar142_pairs(_i142, 1)) << 37)
                    Dim _act142 As Long = If(_qj < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr142, CInt(_qj * 8L)), 0L)
                    Dim _b142 As Long = If(_qj < CLng(szB), Runtime.InteropServices.Marshal.ReadInt64(_bDPtr142, CInt(_qj * 8L)), 0L)
                    _sb142.Append($"  q[{_qj:N0}] exp={_exp142:X16} act={_act142:X16} match={_exp142 = _act142} b={_b142:X16}{vbCrLf}")
                Next
                AppendLog(_sb142.ToString())
            End If
            ' §141: verify q[21389832] (A2/B2 midpoint) from ar[65139832/33] saved in §112 sweep.
            ' q[21389832] = (ar[65139832] >> 27) | (ar[65139833] << 37)
            ' Also log q[21389832] vs b[21389832] to check their relative magnitude.
            If szQ = 21875001 AndAlso _ar141_v0 <> 0L Then
                Const _Q141_IDX As Long = 21389832L
                Dim _q141_expected As Long = CLng(CULng(_ar141_v0) >> 27) Or CLng(CULng(_ar141_v1) << 37)
                Dim _qDPtr141 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _q141_actual As Long = If(_Q141_IDX < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr141, CInt(_Q141_IDX * 8L)), 0L)
                Dim _bDPtr141 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
                Dim _b141_val As Long = If(_Q141_IDX < CLng(szB), Runtime.InteropServices.Marshal.ReadInt64(_bDPtr141, CInt(_Q141_IDX * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§141] ar[65139832]={_ar141_v0:X16} ar[65139833]={_ar141_v1:X16} → q[{_Q141_IDX:N0}] expected={_q141_expected:X16} actual={_q141_actual:X16} match={_q141_expected = _q141_actual} b[{_Q141_IDX:N0}]={_b141_val:X16}{vbCrLf}")
            End If
            ' §149: verify q at top range [20950000..21875000] against pre-BigShiftRight ar values.
            ' These q limbs (q[20904664..21875000]) are the inputs to A2×B2[13612996] in q×b.
            ' §135 covered q[20904664], §141 covered q[21389832]; this fills the unchecked gap.
            ' A mismatch (exp≠act) means BigShiftRight is wrong at that q position.
            ' A wrong saved ar pair (verified only by §112 sweep) would point to wrong a×r instead.
            If szQ = 21875001 Then
                Dim _qDPtr149 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _sb149 As New System.Text.StringBuilder()
                _sb149.Append($"[SafeMpzDiv§149] top q-range verify — limbs contributing to A2×B2[13612996]:{vbCrLf}")
                Dim _149anyBad As Boolean = False
                For _i149 As Integer = 0 To 10
                    Dim _qk149 As Long = _ar149_qIdx(_i149)
                    Dim _e149 As Long = CLng(CULng(_ar149_pairs(_i149, 0)) >> 27) Or CLng(CULng(_ar149_pairs(_i149, 1)) << 37)
                    Dim _a149 As Long = If(_qk149 < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr149, CInt(_qk149 * 8L)), 0L)
                    If _e149 <> _a149 Then _149anyBad = True
                    _sb149.Append($"  q[{_qk149:N0}] exp={_e149:X16} act={_a149:X16} match={_e149 = _a149}{vbCrLf}")
                Next
                _sb149.Append($"  any_mismatch={_149anyBad}{vbCrLf}")
                AppendLog(_sb149.ToString())
            End If
            ' §151: verify q at gap range [20583334..20950000] against pre-BigShiftRight ar values.
            ' This is the last unverified range contributing to A2×B2[13612996] in q×b.
            If szQ = 21875001 Then
                Dim _qDPtr151 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _sb151 As New System.Text.StringBuilder()
                _sb151.Append($"[SafeMpzDiv§151] gap q-range verify [20583334..20950000]:{vbCrLf}")
                Dim _151anyBad As Boolean = False
                For _i151 As Integer = 0 To 3
                    Dim _qk151 As Long = _ar151_qIdx(_i151)
                    Dim _e151 As Long = CLng(CULng(_ar151_pairs(_i151, 0)) >> 27) Or CLng(CULng(_ar151_pairs(_i151, 1)) << 37)
                    Dim _a151 As Long = If(_qk151 < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr151, CInt(_qk151 * 8L)), 0L)
                    If _e151 <> _a151 Then _151anyBad = True
                    _sb151.Append($"  q[{_qk151:N0}] exp={_e151:X16} act={_a151:X16} match={_e151 = _a151}{vbCrLf}")
                Next
                _sb151.Append($"  any_mismatch={_151anyBad}{vbCrLf}")
                AppendLog(_sb151.ToString())
            End If
        End If
        GmpRaw_swap(q.Pointer, ar.Pointer)  ' §35
        _qPtr = q.Pointer     ' §§78-qptr: capture before mpz_clear(ar) fires §78 and corrupts q.Pointer
        gmp_lib.mpz_clear(ar)

PostShiftCheckpoint:
        ' §171-ckpt resume target.  On the normal path: _qPtr was captured above and
        ' szQ was set after BigShiftRight.  On the resumed path: both were populated
        ' from the loaded checkpoint at SafeMpzDiv entry.  Either way, q.Pointer holds
        ' the post-shift Barrett quotient and we now save (if not resumed) and proceed.

        ' §171-ckpt: save q so a crash during q×b or §171 adj loops can resume from here.
        ' We skip the save when we just resumed from this same checkpoint — the file
        ' on disk already matches our state.
        If _autoCheckpoint AndAlso (Not _ckpQResumed) Then
            Try
                If Not System.IO.Directory.Exists(_divCkptDir) Then
                    System.IO.Directory.CreateDirectory(_divCkptDir)
                End If
                Dim _saveStaging(4194303) As Byte
                Dim _qMpz As New mpz_t()
                _qMpz.Pointer = _qPtr
                Using _fs As New FileStream(_divCkptBin, FileMode.Create, FileAccess.Write)
                    Using _bw As New BinaryWriter(_fs)
                        SerializeOneMpz(_qMpz, _bw, _saveStaging)
                    End Using
                End Using
                System.IO.File.WriteAllText(_divCkptMeta,
                    $"szA={szA}{vbLf}szB={szB}{vbLf}aBits={aBits}{vbLf}kBits={kBits}{vbLf}scope={_divCkptScope}{vbLf}")
                BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                AppendLog($"[SafeMpzDiv§171-ckpt] saved div_q.bin (szQ={szQ:N0} scope={_divCkptScope}){vbCrLf}")
            Catch _ex As Exception
                AppendLog($"[SafeMpzDiv§171-ckpt] save failed: {_ex.Message}{vbCrLf}")
            End Try
        End If

        ' §118: log b limbs at key positions to compare vs q, and verify a at critical position
        If _logLevel >= 2 AndAlso szB = 21875001 Then
            Dim _b118DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
            Dim _b118_0 As Long = If(szB >= 1, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, 0), 0L)
            Dim _b118_1 As Long = If(szB >= 2, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, 8), 0L)
            Dim _b118_mid As Long = If(szB >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(10937500L * 8L)), 0L)
            Dim _b118_b2start As Long = If(szB >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(14583334L * 8L)), 0L)
            Dim _b118_near As Long = If(szB >= 20904665, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(20904664L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§118] b limbs: bot=[{_b118_0:X16} {_b118_1:X16}] mid[10937500]={_b118_mid:X16} [14583334]={_b118_b2start:X16} [20904664]={_b118_near:X16}{vbCrLf}")
            Dim _a118DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _a118_4277 As Long = If(szA >= 42779665, Runtime.InteropServices.Marshal.ReadInt64(_a118DPtr, CInt(42779664L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§118b] a[42779664]={_a118_4277:X16}{vbCrLf}")
            ' §131: log q and b limbs in A2/B2 range (A2,B2 start at limb 14,583,334).
            ' A2[13,612,996]=q[28,196,330] is the key limb in A2×B2 that maps to q*b[42,779,664].
            Dim _q131DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(q.Pointer, 8))
            Dim _q131sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(q.Pointer, 4))
            Dim _q131_14 As Long = If(_q131sz >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(14583334L * 8L)), 0L)
            Dim _q131_28 As Long = If(_q131sz >= 28196331, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(28196330L * 8L)), 0L)
            Dim _q131_top As Long = If(_q131sz >= 21875001, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(21875000L * 8L)), 0L)
            Dim _b131_14 As Long = If(szB >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(14583334L * 8L)), 0L)
            Dim _b131_28 As Long = If(szB >= 28196331, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(28196330L * 8L)), 0L)
            Dim _b131_top As Long = If(szB >= 21875001, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(21875000L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§131] q/b A2 limbs: q[14583334]={_q131_14:X16} b[14583334]={_b131_14:X16} q[28196330]={_q131_28:X16} b[28196330]={_b131_28:X16} q[21875000]={_q131_top:X16} b[21875000]={_b131_top:X16}{vbCrLf}")
        End If

        ' Adjustment: remainder = a - q·b; fix until 0 ≤ remainder < b  (at most 2 corrections)
        ' §184: Use raw struct header for qb — bypasses Math.Gmp.Native managed wrapper entirely.
        ' gmp_lib.mpz_init(qb) + gmp_lib.mpz_sub(remainder, a, qb) goes through the managed
        ' wrapper, which exhibits the §78 side-effect: after gmp_lib.mpz_init(remainder),
        ' Math.Gmp.Native corrupts qb.Pointer (it scans registered mpz_t objects and updates
        ' their Pointer fields).  When gmp_lib.mpz_sub then reads qb.Pointer, it gets a garbage
        ' address and passes it to GMP's __gmpz_sub, which hits a STATUS_ASSERTION_FAILURE
        ' (exception 0x40000015, offset 0x14ef6 in libgmp-10.dll) every time.
        ' Fix: allocate qb as a plain raw IntPtr struct (Marshal.AllocHGlobal(16)), use
        ' GmpRaw_init to fill it, pass SafeMpzMul the managed wrapper for result capture,
        ' then do all subsequent operations (sub, clear) via raw P/Invoke using the captured
        ' raw pointer — immune to §78 corruption.
        ' _aPtr and _bPtr were captured at SafeMpzDiv entry (§184c) — already correct here.
        Dim _qbRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        GmpRaw_init(_qbRaw)   ' sets _mp_alloc=1, _mp_size=0, allocates 1-limb buffer via GmpAllocFunc
        Dim qb As New mpz_t()
        qb.Pointer = _qbRaw
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] computing q*b (szQ={szQ:N0} szB={szB:N0})...{vbCrLf}")
        ' §220 (issue #55, 2026-05-22): §167 force-serial LIFTED.
        ' Same rationale as §166 (above): the original wrong-q×b was caused by upstream
        ' wrong-r from premature Newton convergence, fixed by §200/§201. q×b parallel
        ' execution is safe with current SafeMpzReciprocal.
        ' Original lines preserved as comments for easy revert (grep §220).
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §167 lifted — q×b runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        ' §269 (#88): route the FULL q×b through the chunked grid (bit-exact full mode) instead of the
        ' slow §gen recursion — the §268 adaptive 16M cell makes it far faster (§266: 260M² full ~8.6×
        ' vs the old 1.5M chunked, and far beyond §gen).  Gated: flag + size(>1 cell) + DOP, and OFF
        ' under _5b_verify (the §5B diagnostics read §gen internals of qb).  qb's buffer lifecycle is
        ' unchanged (both §gen and chunked swap a GmpNativeAlloc accumulator into qb).
        Dim _szQ269 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(q.Pointer, 4))
        If _divQbChunked AndAlso (Not _5b_verify) _
           AndAlso (CLng(_szQ269) > 1500000L OrElse CLng(szB) > 1500000L) _
           AndAlso MemBudget_SuggestSafeMulDop(CLng(_szQ269), CLng(szB)) <= _recipShortMulMaxDop Then
            AppendLog($"[SafeMpzDiv§269] q×b via chunked-full: szQ={_szQ269:N0} szB={szB:N0}{vbCrLf}", 2)
            SafeMpzMul_ChunkedGrid(qb, q, b, 0L)
        Else
            SafeMpzMul(qb, q, b)
        End If
        ' Capture qb's raw pointer immediately — before any native call that could corrupt qb.Pointer.
        Dim _qbPtr As IntPtr = qb.Pointer   ' = savedResultPtr set by SafeMpzMul
        Dim szQB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4))
        If _logLevel >= 4 Then AppendLog($"[SafeMpzDiv§184] qb raw: alloc={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 0):N0} size={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4):N0} _mp_d={Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8):X16}{vbCrLf}")

        ' §241 (issue #69): trim pooled temporaries left over from q×b. Census-before
        ' here captures the post-q×b retention (the highest-RAM point in the divide).
        TrimPoolAtBoundary("post-qxb", 1048576UL)

        ' §5B-f4: Cheap qb sanity checks (post-SafeMpzMul(qb, q, b), pre-subtract).
        ' Mathematically q ≈ q_true (within ±1 by Barrett), so q × b ≈ a (within b).
        ' qb's TOP limbs should match a's TOP limbs almost exactly; qb[0] should equal
        ' (q[0] × b[0]) mod 2^64 by integer-multiplication identity.
        ' If qb[top] differs significantly from a[top] ⇒ SafeMpzMul has a bug specific
        ' to symmetric q × b at this scale (the OPPOSITE of F-1's a × r verification).
        ' If qb[0] ≠ (q[0] × b[0]) mod 2^64 ⇒ SafeMpzMul has a bottom-limb bug for qb.
        If _logLevel >= 2 AndAlso szQ = 87500001 AndAlso szB = 87500001 Then
            Dim _f4_aD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_aPtr, 8))
            Dim _f4_qD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qPtr, 8))
            Dim _f4_bD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
            Dim _f4_qbD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8))
            Dim _f4_q0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qD, 0))
            Dim _f4_b0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_bD, 0))
            Dim _f4_qb0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, 0))
            Dim _f4_qb1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, 8))
            Dim _f4_a0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, 0))
            Dim _f4_a1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, 8))
            Dim _f4_expQb0 As ULong = _f4_q0 * _f4_b0
            Dim _f4_qbTop As ULong = If(szQB >= 1, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 1) * 8L))), 0UL)
            Dim _f4_qbTop1 As ULong = If(szQB >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 2) * 8L))), 0UL)
            Dim _f4_qbTop2 As ULong = If(szQB >= 3, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 3) * 8L))), 0UL)
            Dim _f4_aTop As ULong = If(szA >= 1, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 1) * 8L))), 0UL)
            Dim _f4_aTop1 As ULong = If(szA >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 2) * 8L))), 0UL)
            Dim _f4_aTop2 As ULong = If(szA >= 3, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 3) * 8L))), 0UL)
            AppendLog($"[SafeMpzDiv§5B-f4 inputs] q[0]={_f4_q0:X16} q[1]={CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qD, 8)):X16} b[0]={_f4_b0:X16} b[1]={CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_bD, 8)):X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f4 qbBot] qb[0]={_f4_qb0:X16} qb[1]={_f4_qb1:X16} (q[0]*b[0])_lo={_f4_expQb0:X16} match={(_f4_qb0 = _f4_expQb0)}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f4 qbTop] qb[szQB-1..-3]=[{_f4_qbTop:X16} {_f4_qbTop1:X16} {_f4_qbTop2:X16}] vs a[szA-1..-3]=[{_f4_aTop:X16} {_f4_aTop1:X16} {_f4_aTop2:X16}] szQB={szQB:N0} szA={szA:N0}{vbCrLf}", 5)   ' §252 (#95)
            ' Sanity: q × b should be ≤ a (since q = floor(a/b) ≈ q_true, q × b ≤ q_true × b ≤ a).
            ' qb's top limb should equal or be just below a's top.
            If _f4_qbTop > _f4_aTop Then
                AppendLog($"[SafeMpzDiv§5B-f4 ALARM] qb[top]={_f4_qbTop:X16} > a[top]={_f4_aTop:X16} — qb is bigger than a in top limb (extreme overshoot){vbCrLf}", 5)   ' §252 (#95)
            End If

            ' §5B-f6: re-read a at the same positions §5B-a captured, verify integrity.
            ' If a's data was modified (e.g., by SafeMpzMul writing through input pieces),
            ' we'd see a mismatch here.  Captures: a[0]=A514E7911325F190, a[1]=96FCE3B61243D2E0,
            ' a[mid=87,500,000]=9A776346843EEB7A, a[szA-2]=A4CCDE102251DA76, a[szA-1]=0479BC06C17340EB.
            Dim _f6_a0 As ULong = _f4_a0
            Dim _f6_a1 As ULong = _f4_a1
            Dim _f6_aMid As ULong = If(szA > 87500000, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(87500000L * 8L))), 0UL)
            Dim _f6_aTop2 As ULong = _f4_aTop1
            Dim _f6_aTop As ULong = _f4_aTop
            Const _F6_EXP_A0 As ULong = &HA514E7911325F190UL
            Const _F6_EXP_A1 As ULong = &H96FCE3B61243D2E0UL
            Const _F6_EXP_AMID As ULong = &H9A776346843EEB7AUL
            Const _F6_EXP_ATOP2 As ULong = &HA4CCDE102251DA76UL
            Const _F6_EXP_ATOP As ULong = &H479BC06C17340EBUL
            Dim _f6_okBot As Boolean = (_f6_a0 = _F6_EXP_A0 AndAlso _f6_a1 = _F6_EXP_A1)
            Dim _f6_okMid As Boolean = (_f6_aMid = _F6_EXP_AMID)
            Dim _f6_okTop As Boolean = (_f6_aTop2 = _F6_EXP_ATOP2 AndAlso _f6_aTop = _F6_EXP_ATOP)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[0]={_f6_a0:X16} (exp {_F6_EXP_A0:X16}) a[1]={_f6_a1:X16} (exp {_F6_EXP_A1:X16}) okBot={_f6_okBot}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[mid=87,500,000]={_f6_aMid:X16} (exp {_F6_EXP_AMID:X16}) okMid={_f6_okMid}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[szA-2]={_f6_aTop2:X16} (exp {_F6_EXP_ATOP2:X16}) a[szA-1]={_f6_aTop:X16} (exp {_F6_EXP_ATOP:X16}) okTop={_f6_okTop}{vbCrLf}", 5)   ' §252 (#95)
            If Not (_f6_okBot AndAlso _f6_okMid AndAlso _f6_okTop) Then
                AppendLog($"[SafeMpzDiv§5B-f6 ALARM] a was corrupted between SafeMpzDiv entry and qb completion!{vbCrLf}", 5)   ' §252 (#95)
            End If
        End If

        ' §5B-f5: Chunked-grid independent reference for q × b at the 5B SafeMpzDiv call.
        ' All other components verified: a intact (F-6), r correct (F-2), ar=a×r correct
        ' (F-1), q derived correctly via BigShiftRight (F-3), §39 not the bug (Option G).
        ' Yet rem = a - qb has size 172.7M limbs (> 2× szB).  The only remaining unverified
        ' component is qb = SafeMpzMul(q, b) middle limbs.
        '
        ' Compute reference qb via chunked-grid (59 × 59 = 3,481 sub-products at ≤ 3M total
        ' each — reliable mpz_mul per §160).  Scan our SafeMpzMul qb against the reference
        ' at 1,000 evenly-spaced positions across [0..szQB-1].
        '
        ' Outcome:
        '   Mismatches > 0 ⇒ SafeMpzMul has a bug specific to q × b at this scale; bug
        '                    is in §gen for symmetric q × b (87.5M × 87.5M).
        '   Mismatches = 0 ⇒ qb is correct; bug is in the §171 algorithm itself or in
        '                    rem subtraction.
        Const _F5_ENABLED As Boolean = False  ' DONE — run 19 confirmed qb matches reference at 1000 positions
        If _logLevel >= 2 AndAlso szQ = 87500001 AndAlso szB = 87500001 AndAlso _F5_ENABLED Then
            Const _F5_CHUNK As Integer = 1500000
            Const _F5_MAX_LIMBS As Integer = 180_000_000
            Dim _F5_MAX_BYTES As Long = CLng(_F5_MAX_LIMBS) * 8L
            Dim _F5_qD As Long = Runtime.InteropServices.Marshal.ReadInt64(_qPtr, 8)
            Dim _F5_qSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qPtr, 4))
            Dim _F5_bD As Long = Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8)
            Dim _F5_bSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_bPtr, 4))
            AppendLog($"[SafeMpzDiv§5B-f5] starting q×b chunked-grid reference (chunk={_F5_CHUNK:N0}, prealloc={_F5_MAX_LIMBS:N0} limbs/buf, {_F5_MAX_BYTES \ 1048576L:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f5] q sz={_F5_qSz:N0} b sz={_F5_bSz:N0}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F5_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F5_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F5_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F5_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F5_eAccBuf = IntPtr.Zero OrElse _F5_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f5] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F5_eAccBuf <> IntPtr.Zero Then VirtualFree(_F5_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F5_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F5_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F5_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_refAcc)
                Dim _F5_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F5_refAcc, 0))
                Dim _F5_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F5_ra_initPtr, _F5_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_refAcc, 0, _F5_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F5_refAcc, 8, _F5_eAccBuf.ToInt64())
                Dim _F5_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_ckShifted)
                Dim _F5_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F5_ckShifted, 0))
                Dim _F5_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F5_cs_initPtr, _F5_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 0, _F5_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F5_ckShifted, 8, _F5_eShiftBuf.ToInt64())
                Dim _F5_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_ckPartial)
                Dim _F5_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F5_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F5_numCkQ As Integer = (_F5_qSz + _F5_CHUNK - 1) \ _F5_CHUNK
                Dim _F5_numCkB As Integer = (_F5_bSz + _F5_CHUNK - 1) \ _F5_CHUNK
                Dim _F5_ckCount As Integer = 0
                For i As Integer = 0 To _F5_numCkQ - 1
                    Dim _F5_qOff As Long = CLng(i) * CLng(_F5_CHUNK)
                    Dim _F5_qSzCk As Integer = CInt(System.Math.Min(CLng(_F5_CHUNK), CLng(_F5_qSz) - _F5_qOff))
                    If _F5_qSzCk <= 0 Then Continue For
                    While _F5_qSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F5_qD + (_F5_qOff + CLng(_F5_qSzCk - 1)) * 8L)) = 0L
                        _F5_qSzCk -= 1
                    End While
                    If _F5_qSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F5_ckA, 0, _F5_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F5_ckA, 4, _F5_qSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F5_ckA, 8, _F5_qD + _F5_qOff * 8L)
                    For j As Integer = 0 To _F5_numCkB - 1
                        Dim _F5_bOff As Long = CLng(j) * CLng(_F5_CHUNK)
                        Dim _F5_bSzCk As Integer = CInt(System.Math.Min(CLng(_F5_CHUNK), CLng(_F5_bSz) - _F5_bOff))
                        If _F5_bSzCk <= 0 Then Continue For
                        While _F5_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F5_bD + (_F5_bOff + CLng(_F5_bSzCk - 1)) * 8L)) = 0L
                            _F5_bSzCk -= 1
                        End While
                        If _F5_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F5_ckB, 0, _F5_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F5_ckB, 4, _F5_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F5_ckB, 8, _F5_bD + _F5_bOff * 8L)
                        GmpRaw_mul(_F5_ckPartial, _F5_ckA, _F5_ckB)
                        Dim _F5_shiftBits As ULong = CULng(_F5_qOff + _F5_bOff) * 64UL
                        If _F5_shiftBits = 0UL Then
                            GmpRaw_add(_F5_refAcc, _F5_refAcc, _F5_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 4, 0)
                            Dim _F5_shiftSrc As IntPtr = _F5_ckPartial
                            Dim _F5_shiftRem As ULong = _F5_shiftBits
                            While _F5_shiftRem > 0UL
                                Dim _F5_chunkBits As UInteger = CUInt(System.Math.Min(_F5_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F5_ckShifted, _F5_shiftSrc, _F5_chunkBits)
                                _F5_shiftSrc = _F5_ckShifted
                                _F5_shiftRem -= CULng(_F5_chunkBits)
                            End While
                            GmpRaw_add(_F5_refAcc, _F5_refAcc, _F5_ckShifted)
                        End If
                        _F5_ckCount += 1
                    Next j
                Next i
                Dim _F5_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F5_refAcc, 4))
                Dim _F5_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_refAcc, 8))
                Dim _F5_qbDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8))
                AppendLog($"[SafeMpzDiv§5B-f5] reference complete: subProducts={_F5_ckCount:N0} refSz={_F5_refSz:N0} ourQbSz={szQB:N0}{vbCrLf}", 5)   ' §252 (#95)
                Const _F5_NUM_SAMPLES As Integer = 1000
                Dim _F5_mismatchCount As Integer = 0
                Dim _F5_firstMismatchIdx As Long = -1L
                Dim _F5_logCount As Integer = 0
                Dim _F5_maxIdx As Long = CLng(System.Math.Min(_F5_refSz, szQB)) - 1L
                For _F5s As Integer = 0 To _F5_NUM_SAMPLES - 1
                    Dim _F5_idx As Long = If(_F5_NUM_SAMPLES > 1, CLng(_F5s) * _F5_maxIdx \ CLng(_F5_NUM_SAMPLES - 1), 0L)
                    Dim _F5_refV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F5_refDPtr, CInt(_F5_idx * 8L)))
                    Dim _F5_qbV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F5_qbDPtr, CInt(_F5_idx * 8L)))
                    If _F5_refV <> _F5_qbV Then
                        _F5_mismatchCount += 1
                        If _F5_firstMismatchIdx = -1L Then _F5_firstMismatchIdx = _F5_idx
                        If _F5_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f5 MISMATCH] sample={_F5s} qb[{_F5_idx:N0}] reference={_F5_refV:X16} ourSafeMpzMul={_F5_qbV:X16}{vbCrLf}", 5)   ' §252 (#95)
                            _F5_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f5 SUMMARY] scanned {_F5_NUM_SAMPLES} qb positions across [0..{_F5_maxIdx:N0}], mismatches={_F5_mismatchCount}, firstMismatchQbIdx={_F5_firstMismatchIdx}{vbCrLf}", 5)   ' §252 (#95)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckShifted)
                GmpRaw_clear(_F5_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckB)
                VirtualFree(_F5_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F5_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If
        ' §184: Allocate remainder as raw struct with pre-allocated limb buffer large enough
        ' to hold the result of a - qb (max szA limbs) — avoids GmpReallocFunc being called
        ' inside __gmpz_sub, which could trigger the §78 side-effect or pool interaction.
        Dim _remLimbs As Long = CLng(szA) + 2L
        Dim _remBuf As IntPtr = GmpNativeAlloc_PoolGet(_remLimbs * 8L)
        Dim _remRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Runtime.InteropServices.Marshal.WriteInt32(_remRaw, 0, CInt(_remLimbs))  ' _mp_alloc
        Runtime.InteropServices.Marshal.WriteInt32(_remRaw, 4, 0)                ' _mp_size = 0
        Runtime.InteropServices.Marshal.WriteInt64(_remRaw, 8, _remBuf.ToInt64()) ' _mp_d
        Dim remainder As New mpz_t()
        remainder.Pointer = _remRaw
        ' §184: Use captured _aPtr and _qbPtr — both corrupted by §78 after SafeMpzMul.
        GmpRaw_sub(_remRaw, _aPtr, _qbPtr)
        GmpRaw_clear(_qbPtr) : Runtime.InteropServices.Marshal.FreeHGlobal(_qbRaw)
        qb.Pointer = IntPtr.Zero   ' prevent GC finalizer from double-freeing
        Dim remSign As Integer = System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
        Dim szRem As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szB can be 739M at 5B).
            Dim _remDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
            Dim _remTop As Long = If(szRem >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_remDPtr.ToInt64() + (CLng(szRem) - 1L) * 8L), 0), 0L)
            Dim _bDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
            Dim _bTop As Long = If(szB >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bDPtr.ToInt64() + (CLng(szB) - 1L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzDiv] q*b done: szQB={szQB:N0}; remainder sign={remSign} szRem={szRem:N0} remTop={_remTop:X16} bTop={_bTop:X16}{vbCrLf}")
        End If

        ' §184: All adj-down/adj-up operations use _remRaw directly (raw IntPtr) — immune to §78.
        ' §35: mpz_sgn is a GMP macro — read _mp_size field directly.
        Dim _adjDown As Integer = 0
        Do While System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4)) < 0  ' q too large
            _adjDown += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-down iter={_adjDown}{vbCrLf}")
            If _adjDown > MAX_ADJ_ITERS Then
                Dim _szRem2 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                GmpRaw_clear(_remRaw) : Runtime.InteropServices.Marshal.FreeHGlobal(_remRaw)
                remainder.Pointer = IntPtr.Zero
                Throw New InvalidOperationException($"SafeMpzDiv adj-down exceeded {MAX_ADJ_ITERS} iters — reciprocal likely wrong. szA={szA} szB={szB} aBits={aBits} kBits={kBits} szR={szR} szQ={szQ} szQB={szQB} szRem={_szRem2}")
            End If
            GmpRaw_sub_ui(_qPtr, _qPtr, 1UI)
            GmpRaw_add(_remRaw, _remRaw, _bPtr)
        Loop
        If _logLevel >= 3 Then AppendLog($"[SafeMpzDiv] adj-down complete: {_adjDown} iter(s){vbCrLf}", 3)

        Dim _adjUp As Integer = 0
        Dim _171Done As Boolean = False
        Do While GmpRaw_cmp(_remRaw, _bPtr) >= 0   ' §35: q too small
            _adjUp += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-up iter={_adjUp}{vbCrLf}")
            If _adjUp > MAX_ADJ_ITERS AndAlso Not _171Done Then
                ' §171: Barrett estimate is wildly off — rem >> b after adj loop.
                ' Iterative top-limb correction: each pass computes delta = floor(rem_top /
                ' (bTop+1)) via mpn_divrem_1 (no TMP_ALLOC, safe for any szB), then subtracts
                ' delta×b from rem and adds delta to q.  Loops until szRem ≤ szB, then normal
                ' adj-up finishes the last few iters.
                '
                ' §171-fix (5B crash, commit after 066f613): use captured _prodHdr171 (not
                ' _prod171.Pointer) for the subtract.  SafeMpzMul recursion can corrupt
                ' result.Pointer per §175 — at 5B the stale .Pointer pointed to a struct with
                ' _mp_size=0 so GmpRaw_sub effectively subtracted zero → szRem unchanged →
                ' §171b fallback → GmpRaw_tdiv_q AV on 172M-limb input.  The raw IntPtr
                ' _prodHdr171 is set by SafeMpzMul's savedResultPtr path (line 2815) and is
                ' always correct.
                _171Done = True
                Dim _szRem171 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                ' §171-entry: log bTop's bit width to quickly spot unnormalized divisors —
                ' these can't converge via single-limb top correction at 5B+ scale.
                Dim _bData171e As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
                Dim _bTop171e As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bData171e.ToInt64() + CLng(szB - 1) * 8L), 0))  ' §239: 64-bit-safe (szB ≈ 739M at 5B → CInt overflow)
                Dim _bTopBits171 As Integer = 0
                Dim _bTopScan As ULong = _bTop171e
                Do While _bTopScan <> 0UL
                    _bTopBits171 += 1
                    _bTopScan >>= 1
                Loop
                AppendLog($"[SafeMpzDiv§171-entry] szA={szA:N0} szB={szB:N0} szRem={_szRem171:N0} ratio={(CDbl(_szRem171)/szB):F3} bTop=0x{_bTop171e:X16} bTopBits={_bTopBits171} (if <48, normalizing per §218 below){vbCrLf}")

                ' §218 (issue #78, 2026-05-21): Knuth Algorithm D-style divisor normalization.
                ' When _bTopBits171 < 48, the previous single-limb correction
                ' delta = floor(remTop / (bTop+1)) over-estimates by up to 2^(64-bTopBits)×
                ' because the lower (szB-1) limbs of b contribute meaningfully to the actual
                ' divisor value that bTop+1 ignores. The over-estimate makes delta×b > rem;
                ' subtraction wraps to negative-magnitude same-size remainder; convergence
                ' check fires.
                '
                ' Fix: shift both rem and b LEFT by shift = 64 - bTopBits so the normalized
                ' bTop has its top bit set (bTopBits_norm = 64). The single-limb estimate
                ' is then tight (off by at most ±1-2 limbs, matching the pre-normalization
                ' design intent). The quotient delta is scale-invariant: (rem×2^s)/(b×2^s)
                ' = rem/b, so q is unaffected. At the end of correction we shift rem RIGHT
                ' by shift to restore original scale before returning to the outer adj-up loop.
                Dim _shift218 As Integer = 0
                Dim _bForCorr218 As IntPtr = _bPtr
                Dim _szBForCorr218 As Integer = szB
                Dim _bNormHdr218 As IntPtr = IntPtr.Zero
                If _bTopBits171 < 48 Then
                    _shift218 = 64 - _bTopBits171
                    AppendLog($"[SafeMpzDiv§218-norm] bTopBits={_bTopBits171} < 48; shift-normalizing b and rem by {_shift218} bits{vbCrLf}")
                    _bNormHdr218 = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_bNormHdr218)
                    ' §218 (issue #78 fix-up): mul_2exp internally calls __gmpz_realloc which
                    ' aborts when new_alloc > INT_MAX/64 = 33.5M limbs (same class of bug as
                    ' §216a). At 1B scale b is ~140M limbs; without PreAlloc, mul_2exp aborts
                    ' the process with "gmp: overflow in mpz type". Wrap each raw IntPtr in
                    ' an mpz_t struct and PreAllocMpzToLimbs to bypass GMP's check before
                    ' calling mul_2exp.
                    Dim _bNormWrap218 As New mpz_t()
                    _bNormWrap218.Pointer = _bNormHdr218
                    PreAllocMpzToLimbs(_bNormWrap218, CLng(szB) + 2L)
                    AppendLog($"[SafeMpzDiv§218-norm] _bNorm PreAlloc'd to {(CLng(szB) + 2L):N0} limbs{vbCrLf}")
                    GmpRaw_mul_2exp(_bNormHdr218, _bPtr, CUInt(_shift218))

                    Dim _remWrap218 As New mpz_t()
                    _remWrap218.Pointer = _remRaw
                    PreAllocMpzToLimbs(_remWrap218, CLng(_szRem171) + 2L)
                    AppendLog($"[SafeMpzDiv§218-norm] _rem PreAlloc'd to {(CLng(_szRem171) + 2L):N0} limbs{vbCrLf}")
                    GmpRaw_mul_2exp(_remRaw, _remRaw, CUInt(_shift218))

                    _bForCorr218 = _bNormHdr218
                    _szBForCorr218 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_bNormHdr218, 4))
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§218-norm] after normalization: szB_norm={_szBForCorr218:N0} szRem_norm={_szRem171:N0}{vbCrLf}")
                End If

                Dim _171Pass As Integer = 0
                Do While _szRem171 > _szBForCorr218
                    _171Pass += 1
                    If _171Pass > 64 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 failed to converge in 64 passes (szRem={_szRem171}, szB={_szBForCorr218}, szA={szA}, shift218={_shift218})")
                    End If
                    Dim _szRemBefore171 As Integer = _szRem171
                    Dim _remData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
                    Dim _bData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bForCorr218, 8))
                    Dim _bTop171 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bData171.ToInt64() + CLng(_szBForCorr218 - 1) * 8L), 0))  ' §239: 64-bit-safe (szB ≈ 739M at 5B → CInt overflow)
                    Dim _topSliceLen171 As Integer = _szRem171 - _szBForCorr218 + 1
                    Dim _deltaBytes171 As Long = CLng(_topSliceLen171) * 8L
                    Dim _deltaBuf171 As IntPtr = GmpNativeAlloc_PoolGet(_deltaBytes171)
                    If _deltaBuf171 = IntPtr.Zero Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pool alloc failed on pass {_171Pass}: requested {_deltaBytes171:N0} bytes")
                    End If
                    Dim _remTopPtr171 As IntPtr = New IntPtr(_remData171.ToInt64() + CLng(_szBForCorr218 - 1) * 8L)
                    GmpRaw_mpn_divrem_1(_deltaBuf171, 0, _remTopPtr171, _topSliceLen171, _bTop171 + 1UL)
                    Dim _deltaSz171 As Integer = _topSliceLen171
                    Do While _deltaSz171 > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_deltaBuf171.ToInt64() + CLng(_deltaSz171 - 1) * 8L), 0) = 0L  ' §239: 64-bit-safe
                        _deltaSz171 -= 1
                    Loop
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] bTop=0x{_bTop171:X16} szDelta={_deltaSz171:N0} szRemBefore={_szRemBefore171:N0}{vbCrLf}", 3)   ' §252 (#95)
                    If _deltaSz171 = 0 Then
                        GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                        Throw New InvalidOperationException($"SafeMpzDiv §171 delta=0 on pass {_171Pass}: top-limb ratio too small. szRem={_szRem171}, szB={_szBForCorr218}, bTop=0x{_bTop171:X16}, shift218={_shift218}")
                    End If
                    Dim _deltaHdr171 As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    Runtime.InteropServices.Marshal.WriteInt32(_deltaHdr171, 0, _topSliceLen171)
                    Runtime.InteropServices.Marshal.WriteInt32(_deltaHdr171, 4, _deltaSz171)
                    Runtime.InteropServices.Marshal.WriteInt64(_deltaHdr171, 8, _deltaBuf171.ToInt64())
                    GmpRaw_add(_qPtr, _qPtr, _deltaHdr171)
                    Dim _prodHdr171 As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_prodHdr171)
                    Dim _prod171 As New mpz_t()
                    _prod171.Pointer = _prodHdr171
                    Dim _bWrap171 As New mpz_t()
                    _bWrap171.Pointer = _bForCorr218
                    Dim _deltaWrap171 As New mpz_t()
                    _deltaWrap171.Pointer = _deltaHdr171
                    SafeMpzMul(_prod171, _deltaWrap171, _bWrap171)
                    ' §171-fix: read prod size from _prodHdr171 (captured raw) — _prod171.Pointer may be stale per §175.
                    Dim _szProd171 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_prodHdr171, 4))
                    Dim _ptrMatch171 As Boolean = (_prodHdr171 = _prod171.Pointer)
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] szProd={_szProd171:N0} prodHdr=0x{_prodHdr171.ToInt64():X} prod.Ptr=0x{_prod171.Pointer.ToInt64():X} match={_ptrMatch171}{vbCrLf}", 3)   ' §252 (#95)
                    GmpRaw_sub(_remRaw, _remRaw, _prodHdr171)
                    GmpRaw_clear(_prodHdr171)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_prodHdr171)
                    _prod171.Pointer = IntPtr.Zero
                    Runtime.InteropServices.Marshal.FreeHGlobal(_deltaHdr171)
                    GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] done: szRemAfter={_szRem171:N0} Δ={_szRemBefore171 - _szRem171:N0}{vbCrLf}", 3)   ' §252 (#95)
                    If _szRem171 >= _szRemBefore171 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pass {_171Pass} did not reduce rem SIZE: before={_szRemBefore171}, after={_szRem171}, szB={_szBForCorr218}, szProd={_szProd171}, ptrMatch={_ptrMatch171}, bTopBits_orig={_bTopBits171}, shift218={_shift218}. After §218 normalization this should not occur — investigate further.")
                    End If
                Loop
                AppendLog($"[SafeMpzDiv§171-done] {_171Pass} pass(es); szRem={_szRem171:N0} ≤ szB={_szBForCorr218:N0}{vbCrLf}", 3)   ' §252 (#95)

                ' §218 (issue #78): denormalize rem back to original scale, then free the
                ' normalized b copy. The outer adj-up loop and any downstream code references
                ' _remRaw at the original (unshifted) scale.
                If _shift218 > 0 Then
                    GmpRaw_tdiv_q_2exp(_remRaw, _remRaw, CUInt(_shift218))
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§218-norm] denormalized rem; szRem after rshift={_szRem171:N0}{vbCrLf}")
                    GmpRaw_clear(_bNormHdr218)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_bNormHdr218)
                End If

                _adjUp = 0
                Continue Do
            End If
            If _adjUp > MAX_ADJ_ITERS Then
                ' §171b: after iterative correction, adj-up must converge in ≤ MAX_ADJ_ITERS.
                ' If not, something is fundamentally wrong — throw with full diagnostics.
                ' (Do NOT fall back to GmpRaw_tdiv_q — it AVs for 170M+ limb inputs in the
                ' SafeMpzSqrt/Newton call stack; see investigation 2026-04-23.)
                Dim _szRem171b As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                Throw New InvalidOperationException($"SafeMpzDiv §171b: adj-up still exceeded {MAX_ADJ_ITERS} iters after §171 correction. szRem={_szRem171b}, szB={szB}, szA={szA}")
            End If
            GmpRaw_add_ui(_qPtr, _qPtr, 1UI)
            GmpRaw_sub(_remRaw, _remRaw, _bPtr)
        Loop
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-up complete: {_adjUp} iter(s); SafeMpzDiv done{vbCrLf}")

        ' §202-trace: dense logging through SafeMpzDiv exit cleanup.  The 5B-run-1 process
        ' died silently between "SafeMpzDiv done" and the next outer Newton checkpoint —
        ' this trace pinpoints which exit step (rem free, ckpt cleanup, return) was last.
        AppendLog($"[SafeMpzDiv§202-exit] start cleanup; scope={_divCkptScope} szQ={szQ:N0}{vbCrLf}", 3)   ' §252 (#95)
        q.Pointer = _qPtr  ' §§78-qptr: restore after adj loops used _qPtr directly
        GmpRaw_clear(_remRaw) : Runtime.InteropServices.Marshal.FreeHGlobal(_remRaw)
        remainder.Pointer = IntPtr.Zero
        AppendLog($"[SafeMpzDiv§202-exit] remainder cleared and freed{vbCrLf}", 3)   ' §252 (#95)

        ' §217 (2026-05-19, user directive after gmpPi.bin loss on 5B run):
        ' NO CHECKPOINT IS DELETED MID-RUN.  The previous §171-ckpt and §211 §NR-ckpt
        ' cleanup blocks fired at SafeMpzDiv exit — that is "this divide converged" but
        ' NOT "the whole run succeeded".  Multiple SafeMpzDiv calls happen per run
        ' (a×r, q×b, plus several in sqrt-Newton); deleting after the first one defeats
        ' the point of having checkpoints to recover from a later failure.
        '
        ' Stale-file safety is handled at the LOAD side, not the WRITE side:
        '   - §171-ckpt load at line ~3813 validates scope/szA/szB/aBits/kBits
        '   - §NR-ckpt load at line ~3440 validates kBits/bBits/prec
        '   - §piCkpt  load at line ~7341 validates digits
        ' A stale file with mismatched metadata is silently rejected ("load failed —
        ' running full path"), so leaving stale files on disk does not poison anything.
        '
        ' Cleanup happens externally between runs (Run-PiCompute.ps1's
        ' Invoke-CheckpointBackup + the §94 stale-snapshot purge at the start of a
        ' fresh non-resume run), never inside ComputePiGMP or SafeMpzDiv.
        AppendLog($"[SafeMpzDiv§202-exit] returning to caller (§217: ckpt files preserved){vbCrLf}", 3)   ' §252 (#95)
    End Sub

    ' Compute result = floor(sqrt(n)).  Safe for any size n.
    ' §100: GMP's mpz_sqrt crashes at 5B digits (519M-limb input triggers
    ' mpn_mul_fft table overflow).  This Newton implementation routes every
    ' large multiplication through SafeMpzMul and every large division through
    ' SafeMpzDiv — neither calls mpn_mul_fft directly.
    '
    ' Algorithm: progressive-precision Newton, working in sqrt(n)'s domain.
    ' At each step with current precision kBitsX:
    '   target   = min(2·kBitsX + 4, bitsS + 2)
    '   nShift   = max(0, bitsN - 2·target)  [keep even]
    '   xHalf    = nShift / 2
    '   nTrunc   = n >> nShift           (~2·target bits)
    '   xTrunc   = x >> xHalf            (~target bits, sqrt(nTrunc) domain)
    '   q        = floor(nTrunc / xTrunc) (~target bits)
    '   xNew     = ((xTrunc + q) >> 1) << xHalf  [scaled back to sqrt(n) domain]
    ' Convergence: Newton quadratic convergence, ~6 large iterations for 5B digits.
    ''' <summary>
    ''' Computes result = floor(sqrt(n)), safe for any size n (§100). A progressive-precision Newton
    ''' iteration in sqrt(n)'s domain that routes every large multiply through SafeMpzMul and every
    ''' large division through <see cref="SafeMpzDiv"/> — neither calls mpn_mul_fft directly, which
    ''' GMP's mpz_sqrt would and which overflows at 5 B digits. ~6 large iterations at 5 B.
    ''' </summary>
    ''' <param name="result">Receives floor(sqrt(n)).</param>
    ''' <param name="n">Radicand.</param>
    Private Shared Sub SafeMpzSqrt(result As mpz_t, n As mpz_t)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        Dim szN As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(n.Pointer, 4))
        If CLng(szN) <= SAFE Then
            gmp_lib.mpz_sqrt(result, n)
            Return
        End If

        ' §174: gmp_lib.mpz_sizeinbase uses GMP's MSVC mp_bitcnt_t (32-bit unsigned long on Windows).
        ' For szN > 67.1M limbs (bitsN > 4.29B) the return value truncates.  szN=103.8M → 6.64B
        ' truncates to 2.35B, giving bitsS=1.17B instead of 3.32B, which causes the Newton loop
        ' to be skipped entirely (kBitsX=2.8B > bitsS+2=1.17B).  Use szN*64 as a safe upper bound.
        ' bitsS upper bound is ceil(szN*64 / 2) = (szN*64+1) / 2 = szN*32 + (if odd, 1 else 0).
        Dim bitsN As Long = CLng(szN) * 64L   ' upper bound; actual bitsN ≤ szN*64 and ≥ szN*64-63
        Dim bitsS As Long = (bitsN + 1L) >> 1   ' bits in floor(sqrt(n)) — upper bound is fine for loop termination

        ' Seed: mpz_sqrt at safe scale — result ≤ SEED_BITS bits = ~5.5M limbs
        Const SEED_BITS As Long = 350_000_000L
        Dim seedShift As Long = System.Math.Max(0L, bitsN - 2L * SEED_BITS)
        If (seedShift And 1L) <> 0L Then seedShift += 1L  ' must be even

        Dim x As New mpz_t()
        gmp_lib.mpz_init(x)
        If seedShift = 0L Then
            gmp_lib.mpz_sqrt(x, n)
        Else
            Dim nSeed As New mpz_t()
            gmp_lib.mpz_init(nSeed)
            BigShiftRight(nSeed, n, seedShift)   ' PreAllocMpzToLimbs inside BigShiftRight
            gmp_lib.mpz_sqrt(x, nSeed)           ' safe: nSeed has 2·SEED_BITS bits
            gmp_lib.mpz_clear(nSeed)
            BigShiftLeft(x, x, seedShift >> 1)   ' x ≈ sqrt(n), correct to SEED_BITS bits
        End If

        ' §106: Newton step checkpoint — resume from the last completed step if available.
        ' Checkpoint lives in snap_Phase3/sqrt_newton.bin + sqrt_newton_meta.txt.
        ' Written immediately after each step completes and backed up to SnapshotStore.
        Dim sqrtSnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim sqrtCheckBin As String = System.IO.Path.Combine(sqrtSnapDir, "sqrt_newton.bin")
        Dim sqrtCheckMeta As String = System.IO.Path.Combine(sqrtSnapDir, "sqrt_newton_meta.txt")
        Dim kBitsX As Long = SEED_BITS
        ' §203: resumed step counter — must match the OLD run's labeling so that
        ' the next iter's _divCkptScope ("sqrt_step_N") agrees with any §171-ckpt
        ' div_meta.txt left on disk by the prior run.  Without this, _newtonStep
        ' restarted from 0 and §171-ckpt scope check would always fail on resume.
        Dim _resumedNewtonStep As Integer = 0

        ' Try to load an existing Newton checkpoint.
        If _autoCheckpoint AndAlso System.IO.File.Exists(sqrtCheckBin) AndAlso System.IO.File.Exists(sqrtCheckMeta) Then
            Try
                Dim metaLines As String() = System.IO.File.ReadAllLines(sqrtCheckMeta)
                Dim meta As New Dictionary(Of String, String)()
                For Each ml As String In metaLines
                    Dim eq As Integer = ml.IndexOf("="c)
                    If eq > 0 Then meta(ml.Substring(0, eq)) = ml.Substring(eq + 1)
                Next
                Dim snapBitsN As Long = 0L, snapKBitsX As Long = 0L
                If meta.ContainsKey("bitsN") AndAlso Long.TryParse(meta("bitsN"), snapBitsN) AndAlso
                   meta.ContainsKey("kBitsX") AndAlso Long.TryParse(meta("kBitsX"), snapKBitsX) AndAlso
                   snapBitsN = bitsN AndAlso snapKBitsX > SEED_BITS Then
                    Dim staging(4194303) As Byte
                    ' x was mpz_init'd above — DeserializeOneMpz handles large limb counts.
                    Using fs As New FileStream(sqrtCheckBin, FileMode.Open, FileAccess.Read)
                        Using br As New BinaryReader(fs)
                            DeserializeOneMpz(x, br, staging)
                        End Using
                    End Using
                    kBitsX = snapKBitsX
                    ' §203: read the prior run's step counter.  Optional for backwards
                    ' compat — older meta files may lack it; default 0 is harmless.
                    Dim _snapStep As Integer = 0
                    If meta.ContainsKey("step") AndAlso Integer.TryParse(meta("step"), _snapStep) Then
                        _resumedNewtonStep = _snapStep
                    End If
                    AppendLog($"[SafeMpzSqrt] Resumed from Newton checkpoint: kBitsX={kBitsX:N0} bits, prior step={_resumedNewtonStep}{vbCrLf}")
                End If
            Catch ex As Exception
                AppendLog($"[SafeMpzSqrt] Newton checkpoint load failed ({ex.Message}) — starting from seed{vbCrLf}")
            End Try
        End If

        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] seed ready ({CLng(gmp_lib.mpz_sizeinbase(x, 10)):N0} digits); beginning Newton refinement{vbCrLf}")

        ' §SqNewton: Capture raw x and n native struct pointers before the Newton loop.
        ' SafeMpzDiv (called inside the loop) makes managed GMP calls (mpz_init/clear for r, ar,
        ' bTrunc, rSq, p inside SafeMpzReciprocal) that trigger the §78 side-effect, corrupting
        ' ALL registered mpz_t.Pointer fields — including x.Pointer and n.Pointer in this scope.
        ' By capturing raw pointers here (before any managed calls in the loop), and restoring
        ' them at the end of each iteration, BigShiftRight/GmpRaw_set use valid native structs.
        Dim _xNativePtr As IntPtr = x.Pointer
        Dim _nNativePtr As IntPtr = n.Pointer

        ' Newton refinement — doubles precision each step
        ' §203: seed _newtonStep from the resumed step counter so that on resume,
        ' the first iter's _divCkptScope continues the OLD run's numbering.
        Dim _newtonStep As Integer = _resumedNewtonStep
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§175] Newton loop entry: kBitsX={kBitsX:N0} bitsS={bitsS:N0} bitsN={bitsN:N0} szN={szN:N0} _newtonStep={_newtonStep} loop_cond={kBitsX < bitsS + 2L}{vbCrLf}")
        Do While kBitsX < bitsS + 2L
            _newtonStep += 1
            Dim target As Long = System.Math.Min(kBitsX * 2L + 4L, bitsS + 2L)
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§175] Newton step {_newtonStep}: target={target:N0}{vbCrLf}")
            Dim nShift As Long = System.Math.Max(0L, bitsN - 2L * target)
            If (nShift And 1L) <> 0L Then nShift += 1L
            Dim xHalf As Long = nShift >> 1

            ' §SqNewton: Use raw AllocHGlobal structs for nTrunc/xTrunc/q instead of
            ' gmp_lib.mpz_init — bypasses the §78 managed-wrapper corruption.
            ' gmp_lib.mpz_init registers objects with Math.Gmp.Native, which then corrupts
            ' ALL registered Pointer fields on subsequent managed GMP calls inside SafeMpzDiv
            ' (mpz_init(r), SafeMpzReciprocal's mpz_init(bTrunc/rSq/p), mpz_clear(ar), etc.).
            ' Raw structs are invisible to the managed framework — their .Pointer fields
            ' cannot be corrupted — so SafeMpzDiv receives valid native struct addresses.
            Dim _nTruncRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_nTruncRaw)
            Dim nTrunc As New mpz_t()
            nTrunc.Pointer = _nTruncRaw
            If nShift > 0L Then
                BigShiftRight(nTrunc, n, nShift)
            Else
                PreAllocMpzToLimbs(nTrunc, CLng(szN))  ' pre-alloc to szN limbs; avoids small→large inside __gmpz_set
                GmpRaw_set(_nTruncRaw, _nNativePtr)    ' copy n using captured raw pointer — immune to §78
            End If

            Dim _xTruncRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_xTruncRaw)
            Dim xTrunc As New mpz_t()
            xTrunc.Pointer = _xTruncRaw
            If xHalf > 0L Then
                BigShiftRight(xTrunc, x, xHalf)
                ' §205: ensure xTrunc has +2 limbs of headroom for the in-place
                ' xTrunc += q add later — without it GMP must realloc 2 GB inside
                ' __gmpz_add at sqrt_step_2 which crashed reproducibly (3× on 5B run).
                Dim _szXT205a As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4))
                PreAllocMpzToLimbs(xTrunc, CLng(_szXT205a) + 2L)
            Else
                Dim _szX As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xNativePtr, 4))
                ' §205: +2 limb headroom (see comment above) — eliminates the in-place add realloc.
                PreAllocMpzToLimbs(xTrunc, CLng(_szX) + 2L)
                GmpRaw_set(_xTruncRaw, _xNativePtr)     ' copy x using captured raw pointer
            End If

            Dim _qRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_qRaw)
            Dim q As New mpz_t()
            q.Pointer = _qRaw
            Dim szNT As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_nTruncRaw, 4))
            Dim szXT As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4))

            ' §140: verify xTrunc at B2-range positions by reading x's raw limbs and comparing.
            ' xHalf=1921928090: xHalf_limb=30030126, xHalf_rem=26.
            ' xTrunc[j] = (x[30030126+j] >> 26) | (x[30030126+j+1] << 38)
            ' Check 7 evenly-spaced B2-range positions + 2 midpoint/top.
            If _logLevel >= 2 AndAlso szXT = 21875001 AndAlso xHalf = 1921928090L Then
                Dim _xHLimb140 As Long = 30030126L
                Dim _xSz140 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xNativePtr, 4))
                Dim _xD140 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_xNativePtr, 8))
                Dim _xtD140 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(xTrunc.Pointer, 8))
                ' §153: Extended check — add 8 positions in [14904664..21554666] (the B2 range contributing to A2×B2[13612996]).
                ' These positions have never been verified. A mismatch here proves BigShiftRight corrupts b at that location.
                Dim _j140_check() As Long = {14583334L, 14904664L, 15583334L, 15904664L, 16583334L, 17583334L, 17904664L, 18583334L, 19583334L, 19904664L, 20583334L, 20904664L, 21389832L, 21554666L, 21875000L}
                Dim _sb140 As New System.Text.StringBuilder()
                _sb140.Append($"[SafeMpzSqrt§140] szX={_xSz140:N0} xHalf=1921928090 xHalf_limb=30030126{vbCrLf}")
                For Each _j140 As Long In _j140_check
                    Dim _x0 As Long = If(_xHLimb140 + _j140 < CLng(_xSz140), Runtime.InteropServices.Marshal.ReadInt64(_xD140, CInt((_xHLimb140 + _j140) * 8L)), 0L)
                    Dim _x1 As Long = If(_xHLimb140 + _j140 + 1L < CLng(_xSz140), Runtime.InteropServices.Marshal.ReadInt64(_xD140, CInt((_xHLimb140 + _j140 + 1L) * 8L)), 0L)
                    Dim _xt_exp As Long = CLng(CULng(_x0) >> 26) Or CLng(CULng(_x1) << 38)
                    Dim _xt_act As Long = If(_j140 < CLng(szXT), Runtime.InteropServices.Marshal.ReadInt64(_xtD140, CInt(_j140 * 8L)), 0L)
                    _sb140.Append($"  j={_j140:N0}: x[{_xHLimb140+_j140}]={_x0:X16} x[{_xHLimb140+_j140+1}]={_x1:X16} exp={_xt_exp:X16} act={_xt_act:X16} match={_xt_exp = _xt_act}{vbCrLf}")
                Next
                AppendLog(_sb140.ToString())
            End If
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] Newton step {_newtonStep}: target={target:N0} bits, div {szNT:N0}/{szXT:N0} limbs{vbCrLf}")
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§133-probe] szNT={szNT} szXT={szXT} logLevel={_logLevel} cond={szNT = 43750001 AndAlso szXT = 21875001}{vbCrLf}")
            ' §133: verify nTrunc[42779664] by reading n directly, bypassing BigShiftRight.
            ' nTrunc = n >> nShift: limb_off = nShift\64, bit_shift = nShift Mod 64.
            ' nTrunc[42779664] = (n[limb_off+42779664] >> bit_shift) | (n[limb_off+42779665] << (64-bit_shift))
            ' If this matches nTrunc[42779664] AND §118b's a[42779664], BigShiftRight is correct.
            ' If they differ, BigShiftRight has a bug OR n is corrupted.
            If _logLevel >= 2 AndAlso szNT = 43750001 AndAlso szXT = 21875001 Then
                Const _TGT133 As Long = 42779664L
                Dim _limb133 As Long = nShift \ 64L
                Dim _bit133 As Integer = CInt(nShift Mod 64L)
                Dim _nSz133 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_nNativePtr, 4))
                Dim _nD133 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_nNativePtr, 8))
                Dim _idxLo133 As Long = _limb133 + _TGT133
                Dim _idxHi133 As Long = _limb133 + _TGT133 + 1L
                Dim _vLo133 As Long = If(_idxLo133 < CLng(_nSz133), Runtime.InteropServices.Marshal.ReadInt64(_nD133, CInt(_idxLo133 * 8L)), 0L)
                Dim _vHi133 As Long = If(_idxHi133 < CLng(_nSz133), Runtime.InteropServices.Marshal.ReadInt64(_nD133, CInt(_idxHi133 * 8L)), 0L)
                Dim _expected133 As Long = CLng(CULng(_vLo133) >> _bit133) Or CLng(CULng(_vHi133) << (64 - _bit133))
                Dim _nTruncSz133 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(nTrunc.Pointer, 4))
                Dim _nTruncD133 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(nTrunc.Pointer, 8))
                Dim _actual133 As Long = If(_TGT133 < CLng(_nTruncSz133), Runtime.InteropServices.Marshal.ReadInt64(_nTruncD133, CInt(_TGT133 * 8L)), 0L)
                AppendLog($"[SafeMpzSqrt§133] nShift={nShift:N0} limb_off={_limb133:N0} bit_shift={_bit133}" &
                          $" n[{_idxLo133:N0}]={_vLo133:X16} n[{_idxHi133:N0}]={_vHi133:X16}" &
                          $" expected nTrunc[{_TGT133:N0}]={_expected133:X16} actual nTrunc[{_TGT133:N0}]={_actual133:X16}" &
                          $" MATCH={_expected133 = _actual133}{vbCrLf}")
            End If
            If CLng(szNT) + CLng(szXT) <= SAFE Then
                GmpRaw_tdiv_q(q.Pointer, nTrunc.Pointer, xTrunc.Pointer)  ' §35
            Else
                _divCkptScope = $"sqrt_step_{_newtonStep}"
                SafeMpzDiv(q, nTrunc, xTrunc)
            End If
            AppendLog($"[SafeMpzSqrt§202-postdiv] step {_newtonStep}: SafeMpzDiv returned; entering post-divide cleanup (xHalf={xHalf:N0} target={target:N0}){vbCrLf}")
            ' §SqNewton: Use raw GmpRaw ops for cleanup — no managed mpz_clear/mpz_add.
            ' After SafeMpzDiv, all managed mpz_t.Pointer fields in this scope are potentially
            ' corrupted by §78. We use only the captured raw IntPtrs (_nTruncRaw, _xTruncRaw,
            ' _qRaw, _xNativePtr) for all post-SafeMpzDiv operations.
            GmpRaw_clear(_nTruncRaw)                                              ' free nTrunc limb buffer
            nTrunc.Pointer = IntPtr.Zero                                          ' prevent finalizer mpz_clear
            Runtime.InteropServices.Marshal.FreeHGlobal(_nTruncRaw)
            AppendLog($"[SafeMpzSqrt§202-postdiv] nTrunc cleared and freed{vbCrLf}")

            ' §204-trace: dump _xTruncRaw and _qRaw struct fields (alloc, size, _mp_d) immediately
            ' before GmpRaw_add — second 5B-run-1 death (08:49 PT, log frozen at "nTrunc cleared
            ' and freed" both times) is reproducibly inside or after this line.  If either struct
            ' is corrupted, this trace catches it; if both look healthy, the crash is inside
            ' __gmpz_add itself (allocator failure, GMP abort, etc.).
            Dim _xtAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 0)
            Dim _xtSize As Integer = Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4)
            Dim _xtD As Long = Runtime.InteropServices.Marshal.ReadInt64(_xTruncRaw, 8)
            Dim _qAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(_qRaw, 0)
            Dim _qSize As Integer = Runtime.InteropServices.Marshal.ReadInt32(_qRaw, 4)
            Dim _qD As Long = Runtime.InteropServices.Marshal.ReadInt64(_qRaw, 8)
            AppendLog($"[SafeMpzSqrt§204-pre-add] xTrunc struct: alloc={_xtAlloc:N0} size={_xtSize:N0} _mp_d={_xtD:X16} (raw={_xTruncRaw.ToInt64():X16}){vbCrLf}")
            AppendLog($"[SafeMpzSqrt§204-pre-add] q      struct: alloc={_qAlloc:N0} size={_qSize:N0} _mp_d={_qD:X16} (raw={_qRaw.ToInt64():X16}){vbCrLf}")
            ' Also probe first/last limb of each so we know the limb buffers are mapped (an AV
            ' inside __gmpz_add reading the limb buffer would happen here too, but at least the
            ' last log line written would point us at the operand whose buffer is bad).
            If _xtD <> 0 AndAlso _xtSize > 0 Then
                Dim _xtBot As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_xtD), 0)
                Dim _xtTop As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_xtD), CInt((CLng(_xtSize) - 1L) * 8L))
                AppendLog($"[SafeMpzSqrt§204-pre-add] xTrunc limbs: [0]={_xtBot:X16} [{_xtSize - 1}]={_xtTop:X16}{vbCrLf}")
            End If
            If _qD <> 0 AndAlso _qSize > 0 Then
                Dim _qBot As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qD), 0)
                Dim _qTop As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qD), CInt((CLng(_qSize) - 1L) * 8L))
                AppendLog($"[SafeMpzSqrt§204-pre-add] q      limbs: [0]={_qBot:X16} [{_qSize - 1}]={_qTop:X16}{vbCrLf}")
            End If
            AppendLog($"[SafeMpzSqrt§204-pre-add] calling GmpRaw_add (in-place xTrunc += q)…{vbCrLf}")

            GmpRaw_add(_xTruncRaw, _xTruncRaw, _qRaw)                           ' xTrunc += q
            AppendLog($"[SafeMpzSqrt§202-postdiv] xTrunc += q complete (szXT={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4)):N0}){vbCrLf}")
            GmpRaw_clear(_qRaw)                                                   ' free q limb buffer
            q.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_qRaw)
            GmpRaw_tdiv_q_2exp(_xTruncRaw, _xTruncRaw, 1UI)                     ' xTrunc >>= 1
            AppendLog($"[SafeMpzSqrt§202-postdiv] q freed; xTrunc >>= 1 done{vbCrLf}")

            If xHalf > 0L Then
                AppendLog($"[SafeMpzSqrt§202-postdiv] BigShiftLeft xHalf={xHalf:N0} starting{vbCrLf}")
                BigShiftLeft(xTrunc, xTrunc, xHalf)
                AppendLog($"[SafeMpzSqrt§202-postdiv] BigShiftLeft done{vbCrLf}")
            End If
            GmpRaw_swap(_xNativePtr, _xTruncRaw)  ' swap: x's native struct gets new Newton value
            x.Pointer = _xNativePtr               ' restore managed x.Pointer (corrupted by SafeMpzDiv §78)
            n.Pointer = _nNativePtr               ' restore managed n.Pointer for next iteration
            GmpRaw_clear(_xTruncRaw)                                              ' free old x limb buffer
            xTrunc.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_xTruncRaw)
            kBitsX = target
            AppendLog($"[SafeMpzSqrt§202-postdiv] swap+free complete; kBitsX advanced to {kBitsX:N0}{vbCrLf}")

            ' §106: Save Newton step checkpoint immediately after completion.
            If _autoCheckpoint Then
                Try
                    AppendLog($"[SafeMpzSqrt§202-ckpt] starting sqrt_newton.bin save (step={_newtonStep} kBitsX={kBitsX:N0}){vbCrLf}")
                    If Not System.IO.Directory.Exists(sqrtSnapDir) Then
                        System.IO.Directory.CreateDirectory(sqrtSnapDir)
                    End If
                    Dim staging(4194303) As Byte
                    Using fs As New FileStream(sqrtCheckBin, FileMode.Create, FileAccess.Write)
                        Using bw As New BinaryWriter(fs)
                            SerializeOneMpz(x, bw, staging)
                        End Using
                    End Using
                    AppendLog($"[SafeMpzSqrt§202-ckpt] sqrt_newton.bin written; writing meta{vbCrLf}")
                    System.IO.File.WriteAllText(sqrtCheckMeta,
                        $"bitsN={bitsN}{vbLf}kBitsX={kBitsX}{vbLf}step={_newtonStep}{vbLf}")
                    AppendLog($"[SafeMpzSqrt§202-ckpt] meta written; calling BackupSnapshotToStore{vbCrLf}")
                    BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                    AppendLog($"[SafeMpzSqrt] Newton step {_newtonStep} checkpoint saved (kBitsX={kBitsX:N0}){vbCrLf}")
                Catch ex As Exception
                    AppendLog($"[SafeMpzSqrt] Newton checkpoint save failed: {ex.Message}{vbCrLf}")
                End Try
            End If
            AppendLog($"[SafeMpzSqrt§202-postdiv] step {_newtonStep} fully complete; looping (kBitsX={kBitsX:N0} bitsS+2={bitsS + 2L:N0} cont={kBitsX < bitsS + 2L}){vbCrLf}")

            ' §219 (issue #79): drain finalizer queue at SafeMpzSqrt Newton step boundary.
            ' Each sqrt-Newton step runs a full SafeMpzDiv internally (which itself runs
            ' SafeMpzReciprocal Newton loop), creating thousands of mpz_t wrappers.
            ' Sqrt-Newton at 1B+ scale has 4 steps each ~2.5 hours single-threaded; the
            ' finalizer backlog accumulated during a step is best drained at the step
            ' boundary while we're momentarily in cleanup before the next step's SafeMpzMul.
            DrainFinalizers()
        Loop

        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] Newton done; final adjustment{vbCrLf}")

        ' Final adjustment: ensure result = floor(sqrt(n)) exactly (off by at most 1).
        '
        ' §228 (issue #54, 2026-05-23): parallelize the two initial squarings (xSq = x²,
        ' x1Sq = (x+1)²) via Parallel.Invoke.  Both have disjoint inputs and result buffers;
        ' the original §207 force-serial-DOP guard was for a 5B-run-6 crash inside SafeMpzMul's
        ' own recursive Parallel.For — that crash mode was removed by §220 (#55, force-serial
        ' caps lifted) and §221 (#44, size-gate lifted) so the recursion can now safely run at
        ' the caller's _safeMulDop.  Adj-down and adj-up loops remain serial (one squaring at
        ' a time, 0-1 iter typical) and use the inherited DOP — no extra outer parallelism.
        '
        ' Expected impact: ~halves final-adj wall time (~10-20 h saved at 5B).  §206 pre-alloc
        ' guards retained: x1 and x1Sq must be pre-sized before mpz_add_ui / SafeMpzMul to
        ' avoid silent realloc inside Parallel.Invoke tasks.
        Dim _szX228 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(x.Pointer, 4))
        AppendLog($"[SafeMpzSqrt§228] entering parallel final-adj: szX={_szX228:N0} limbs (currentDop={System.Threading.Volatile.Read(_safeMulDop)}){vbCrLf}")
        ' §275 (#121): decide whether to route the final-adjustment squarings through the chunked
        ' grid (parallel cells at PI_CG_DOP, low per-cell RAM) instead of §gen at the inherited DOP.
        _sqrtChunkedGrid = (Environment.GetEnvironmentVariable("PI_SQRT_CG") <> "0")
        Dim _scgEnv As String = Environment.GetEnvironmentVariable("PI_SQRT_CG_MINLIMBS")
        Dim _scgParsed As Long
        If _scgEnv IsNot Nothing AndAlso Long.TryParse(_scgEnv, _scgParsed) AndAlso _scgParsed >= 0L Then _sqrtCgMinLimbs = _scgParsed
        Dim _useCgSqrt As Boolean = _sqrtChunkedGrid AndAlso CLng(_szX228) >= _sqrtCgMinLimbs
        If _useCgSqrt Then AppendLog($"[SafeMpzSqrt§275] final-adj squarings via chunked-grid (szX={_szX228:N0} >= {_sqrtCgMinLimbs:N0}){vbCrLf}")

        ' Pre-compute x1 = x+1 before launching the parallel pair (cannot do this inside the
        ' Parallel.Invoke task without races on x).
        Dim x1 As New mpz_t()
        gmp_lib.mpz_init(x1)
        PreAllocMpzToLimbs(x1, CLng(_szX228) + 2L)  ' §206: avoid silent realloc inside __gmpz_add_ui
        gmp_lib.mpz_add_ui(x1, x, 1UI)
        AppendLog($"[SafeMpzSqrt§228] x1 = x+1 computed (size={Runtime.InteropServices.Marshal.ReadInt32(x1.Pointer, 4):N0}){vbCrLf}")

        ' Pre-alloc both result buffers up front so Parallel.Invoke tasks don't race on realloc.
        Dim xSq As New mpz_t()
        gmp_lib.mpz_init(xSq)
        PreAllocMpzToLimbs(xSq, 2L * CLng(_szX228) + 4L)  ' §206/§207: pre-size to 2·szX+4
        Dim x1Sq As New mpz_t()
        gmp_lib.mpz_init(x1Sq)
        PreAllocMpzToLimbs(x1Sq, 2L * CLng(_szX228) + 4L)
        AppendLog($"[SafeMpzSqrt§228] xSq and x1Sq pre-alloc'd ({(2L * CLng(_szX228) + 4L):N0} limbs each, ~{((2L * CLng(_szX228) + 4L) * 8L) \ (1024L * 1024L):N0} MB){vbCrLf}")

        ' §228: launch the two squarings concurrently.  Inner SafeMpzMul fans out per §220/§221.
        AppendLog($"[SafeMpzSqrt§228] Parallel.Invoke(xSq=x*x, x1Sq=x1*x1) starting{vbCrLf}")
        Dim _t228Ticks As Long = System.Diagnostics.Stopwatch.GetTimestamp()
        If _useCgSqrt Then
            ' §275: each chunked-grid squaring already saturates up to PI_CG_DOP cores, so run the
            ' two sequentially (no Parallel.Invoke ⇒ no core oversubscription).  CG squaring is
            ' bit-exact (--test-chunkedgrid sq=True) and far faster than §gen at these sizes.
            SafeMpzMulCG(xSq, x, x)
            SafeMpzMulCG(x1Sq, x1, x1)
        Else
            System.Threading.Tasks.Parallel.Invoke(
                Sub() SafeMpzMul(xSq, x, x),
                Sub() SafeMpzMul(x1Sq, x1, x1))
        End If
        Dim _t228Elapsed As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t228Ticks) / System.Diagnostics.Stopwatch.Frequency
        AppendLog($"[SafeMpzSqrt§228] Parallel.Invoke done; elapsed={_t228Elapsed:F2}s (szXSq={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(xSq.Pointer, 4)):N0} szX1Sq={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(x1Sq.Pointer, 4)):N0}){vbCrLf}")

        ' adj-down: x²>n means Newton overshot; rare (0-1 iter typical from Newton convergence).
        ' Keep x1 in sync so the post-adj x1Sq matches the new (x+1).
        Dim _adjDownSqrt As Integer = 0
        Do While GmpRaw_cmp(xSq.Pointer, n.Pointer) > 0   ' §35: x² > n → x too large
            _adjDownSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-down iter={_adjDownSqrt} (x²>n){vbCrLf}")
            gmp_lib.mpz_sub_ui(x, x, 1UI)
            gmp_lib.mpz_sub_ui(x1, x1, 1UI)
            If _useCgSqrt Then SafeMpzMulCG(xSq, x, x) Else SafeMpzMul(xSq, x, x)   ' §275
        Loop
        If _adjDownSqrt > 0 Then
            AppendLog($"[SafeMpzSqrt§228] adj-down ran {_adjDownSqrt} iter(s); recomputing x1Sq for new x1{vbCrLf}")
            If _useCgSqrt Then SafeMpzMulCG(x1Sq, x1, x1) Else SafeMpzMul(x1Sq, x1, x1)   ' §275
        Else
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-down done: 0 iter(s); x1Sq from parallel pair still valid{vbCrLf}")
        End If
        gmp_lib.mpz_clear(xSq)

        ' adj-up: (x+1)² ≤ n means Newton undershot; rare.
        Dim _adjUpSqrt As Integer = 0
        Do While GmpRaw_cmp(x1Sq.Pointer, n.Pointer) <= 0   ' §35: (x+1)² ≤ n → x too small
            _adjUpSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-up iter={_adjUpSqrt} ((x+1)²≤n){vbCrLf}")
            GmpRaw_swap(x.Pointer, x1.Pointer)  ' §35
            gmp_lib.mpz_add_ui(x1, x, 1UI)
            If _useCgSqrt Then SafeMpzMulCG(x1Sq, x1, x1) Else SafeMpzMul(x1Sq, x1, x1)   ' §275
        Loop
        AppendLog($"[SafeMpzSqrt§228] adj-up done: {_adjUpSqrt} iter(s); SafeMpzSqrt complete{vbCrLf}")
        gmp_lib.mpz_clear(x1)
        gmp_lib.mpz_clear(x1Sq)

        GmpRaw_swap(result.Pointer, x.Pointer)  ' §35
        gmp_lib.mpz_clear(x)
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Chudnovsky binary splitting — tree merge level
    ' ════════════════════════════════════════════════════════════════════════

    ' Issue #4 fix: DiskNode is now a Structure (see definition at top of class).
    ' Issue #6 fix: all Tuple(Of mpz_t,mpz_t,mpz_t) replaced with Result struct
    '   or individual mpz_t variables; SerializeNodeToDisk/LoadNodeFromDisk now
    '   take/return three mpz_t directly.
    ' Issue #7 fix: removed GC.Collect() every 10 combine pairs.  The GC is
    '   better left to make its own decisions; forcing it that frequently was
    '   interfering with compaction around live pinned objects (now gone) and
    '   adding overhead without benefit.  The between-level GC.Collect is kept
    '   since it runs only ~17 times per billion-digit computation.
    ' §248 (issue #48): one Phase-1 chunk's computed (P,Q,T) handed from a compute worker to
    ' an E-core serializer thread. Ownership of the three mpz_t transfers to the serializer,
    ' which writes them to L0.bin and frees them.
    Private NotInheritable Class ChunkWork
        Public ReadOnly P As mpz_t, Q As mpz_t, T As mpz_t
        Public ReadOnly Idx As Integer
        Public Sub New(p_ As mpz_t, q_ As mpz_t, t_ As mpz_t, idx_ As Integer)
            P = p_ : Q = q_ : T = t_ : Idx = idx_
        End Sub
    End Class

    ''' <summary>
    ''' Phase 1 driver for the Chudnovsky binary split: divides the full term range into adaptive-size
    ''' chunks (clamp(numTerms\10000, 512, 8192) terms each), computes each chunk's (P, Q, T) in parallel
    ''' via <see cref="BinarySplitChunk"/>, and streams the results to disk / collects them as the leaf
    ''' nodes that Phase 2 combines bottom-up into the root P, Q, T.
    ''' </summary>
    ''' <param name="numTerms">Total number of Chudnovsky series terms to sum.</param>
    ''' <param name="nodes">Receives the per-chunk leaf results (in RAM mode; disk mode streams to NodeCache).</param>
    Private Sub BinarySplitGMP(numTerms As Long,
                                ByRef nodes As List(Of Result))

        ' §38: Adaptive chunk size — clamp(numTerms\10000, 512, 8192).
        ' At 5B digits (~360M terms): 360M\10000=36000 → clamped to 8192.
        ' Fewer, larger chunks → fewer serialise/deserialise round-trips.
        Dim CHUNK_SIZE As Long = CLng(System.Math.Max(512L, System.Math.Min(8192L, numTerms \ 10000L)))
        If _logLevel >= 2 Then AppendLog($"[BinarySplit] §38 adaptive CHUNK_SIZE={CHUNK_SIZE:N0} for {numTerms:N0} terms{vbCrLf}")
        Const STOP_AT As Long = 1L
        ' §73: threshold read from UI/CLI at compute start — adapts to available RAM.
        Dim DISK_THRESHOLD As Integer = _diskThreshold

        Dim numChunks As Long = (numTerms + CHUNK_SIZE - 1) \ CHUNK_SIZE

        ' Validate array size before allocation
        If numChunks > Integer.MaxValue Then
            Throw New OverflowException($"Too many chunks: {numChunks:N0} exceeds Integer.MaxValue ({Integer.MaxValue:N0})")
        End If

        LogPhase($"Processing {numChunks:N0} chunks of {CHUNK_SIZE} terms each (streaming to disk)...")

        ' Clear old cache (skipped when resuming — checkpoint files must be preserved).
        ' §94: Also delete stale snap_L* snapshot subdirectories on a fresh non-auto-
        ' checkpoint run so leftover snapshots from a prior different run don't confuse
        ' a future --auto-checkpoint run.  When _autoCheckpoint is True the auto-detect
        ' step below validates metadata before using any snapshot, so no cleanup needed.
        If _resumeFromLevel = 0 Then
            Try
                If System.IO.Directory.Exists(DISK_CACHE_DIR) Then
                    Dim oldFiles = System.IO.Directory.GetFiles(DISK_CACHE_DIR, "*.bin")
                    Dim oldCount As Integer = oldFiles.Length
                    If oldCount > 0 Then
                        Me.BeginInvoke(Sub() LblStatus.Text = $"Clearing {oldCount:N0} cached files from previous run...")
                        For idx As Integer = 0 To oldFiles.Length - 1
                            System.IO.File.Delete(oldFiles(idx))
                            If (idx + 1) Mod 1000 = 0 Then
                                Dim snap As Integer = idx + 1
                                Me.BeginInvoke(Sub() LblStatus.Text = $"Clearing cache: {snap:N0} / {oldCount:N0} files deleted...")
                            End If
                        Next
                    End If
                    ' §94: Remove stale snapshot dirs when starting a fresh non-checkpoint run.
                    If Not _autoCheckpoint Then
                        For Each snapSubDir As String In System.IO.Directory.GetDirectories(DISK_CACHE_DIR, "snap_L*")
                            Try : System.IO.Directory.Delete(snapSubDir, recursive:=True) : Catch : End Try
                        Next
                    End If
                End If
            Catch
            End Try
        End If

        ' Issue #4: List(Of DiskNode) now holds value types — no per-element heap allocation.
        Dim diskNodes As New List(Of DiskNode)()
        Dim currentSize As Long = numChunks
        Dim level As Integer = 0

        ' ── §94: Auto-checkpoint resume — scan for highest valid snapshot ────
        ' When --auto-checkpoint is set and --resume-from-level was not supplied
        ' explicitly, find the best snapshot and set _resumeFromLevel so the
        ' existing resume path below handles the actual load.
        If _autoCheckpoint AndAlso _resumeFromLevel = 0 Then
            Dim bestSnap As Integer = TryFindBestSnapshot(numChunks)
            If bestSnap >= 1 Then
                _resumeFromLevel = bestSnap + 1
                LogPhase($"[Snapshot] Auto-resume: found snap_L{bestSnap}, resuming from level {_resumeFromLevel}")
            End If
        End If

        ' ── §93 / §94: Resume path — skip Phase 1 and load node files ────────
        ' Handles both --resume-from-level N (§93 hot-path disk files) and
        ' --auto-checkpoint auto-detect (§94 snapshot folder files).
        ' currentSize is reconstructed from numChunks using the same halving
        ' formula the Phase 2 While loop uses.
        If _resumeFromLevel > 0 Then
            ' Compute the expected node count at the resume level.
            Dim resumeSize As Long = numChunks
            For lvl As Integer = 1 To _resumeFromLevel - 1
                resumeSize = (resumeSize + 1) \ 2
            Next
            LogPhase($"RESUMING from level {_resumeFromLevel}: expecting {resumeSize:N0} nodes")
            If Not System.IO.Directory.Exists(DISK_CACHE_DIR) Then
                Throw New System.IO.DirectoryNotFoundException(
                    $"Checkpoint directory not found: {DISK_CACHE_DIR}")
            End If

            ' §94: Prefer snapshot folder (snap_L{N}\N{idx}.bin) when auto-checkpoint
            ' is active; fall back to flat hot-path files (L{N}_N{idx}.bin) otherwise.
            Dim snapFolder As String = System.IO.Path.Combine(
                DISK_CACHE_DIR, $"snap_L{_resumeFromLevel - 1}")
            Dim useSnapFolder As Boolean = _autoCheckpoint AndAlso
                                           System.IO.Directory.Exists(snapFolder)

            Dim cpFilesRaw() As String
            If useSnapFolder Then
                cpFilesRaw = System.IO.Directory.GetFiles(snapFolder, "N*.bin")
            Else
                cpFilesRaw = System.IO.Directory.GetFiles(
                    DISK_CACHE_DIR, $"L{_resumeFromLevel - 1}_N*.bin")
            End If

            ' Sort by node index — snapshot files are "N{idx}.bin",
            ' hot-path files are "L{lvl}_N{idx}.bin".
            Dim cpFiles = cpFilesRaw.OrderBy(Function(f)
                    Dim stem As String = System.IO.Path.GetFileNameWithoutExtension(f)
                    Dim n As Integer = 0
                    Dim sep As Integer = stem.LastIndexOf("_N", StringComparison.Ordinal)
                    If sep >= 0 Then
                        Integer.TryParse(stem.Substring(sep + 2), n)   ' hot-path: L{l}_N{idx}
                    ElseIf stem.StartsWith("N", StringComparison.OrdinalIgnoreCase) Then
                        Integer.TryParse(stem.Substring(1), n)          ' snapshot:  N{idx}
                    End If
                    Return n
                End Function).ToArray()

            If cpFiles.Length = 0 Then
                Dim pattern As String = If(useSnapFolder,
                    $"N*.bin in {snapFolder}",
                    $"L{_resumeFromLevel - 1}_N*.bin in {DISK_CACHE_DIR}")
                Throw New System.IO.FileNotFoundException(
                    $"No checkpoint files found ({pattern})")
            End If

            LogPhase($"Found {cpFiles.Length:N0} checkpoint file(s) for level {_resumeFromLevel - 1}" &
                     If(useSnapFolder, " [snapshot]", " [hot-path]"))
            For Each f As String In cpFiles
                Dim node As DiskNode
                node.FilePath = f
                node.FileOffset = 0
                node.MemP = Nothing
                node.MemQ = Nothing
                node.MemT = Nothing
                node.Level = _resumeFromLevel - 1
                node.Index = diskNodes.Count
                node.IsInMemory = False
                diskNodes.Add(node)
            Next
            currentSize = diskNodes.Count
            level = _resumeFromLevel - 1
            LogPhase($"Resume ready: {currentSize:N0} nodes at level {level}, continuing Phase 2")
            GoTo Phase2
        End If

        ' ── Phase 1: compute all chunks in parallel ──────────────────────────
        ' All numChunks chunks are fully independent — each BinarySplitChunk
        ' call uses only thread-local mpz_t objects and writes to a unique file.
        ' gmpC3Const is read-only throughout so concurrent GMP reads are safe.
        ' The custom allocator's memory operations (VirtualAlloc/VirtualFree,
        ' CRT malloc/free) are thread-safe Win32/CRT APIs; their file-logging
        ' paths are serialised via _logLock inside AppendLog.
        ' Results are written into a pre-sized array by index (no list locking).
        Dim chunkResults(CInt(numChunks) - 1) As DiskNode
        Dim completedChunks As Long = 0L
        Dim _serializedChunks As Long = 0L   ' §248: chunks fully written to L0.bin (async path)
        ' §54: single-file format — all Level-0 chunks written to one L0.bin.
        ' Eliminates 137K individual file creates/deletes (the ~2 min NVMe
        ' metadata overhead at the start of every run).
        '
        ' §106 Gap 3: lock-free writes via RandomAccess.Write + Interlocked offset.
        ' Each thread serializes to a MemoryStream, then atomically reserves a
        ' file region with Interlocked.Add, and writes directly to that offset
        ' via RandomAccess.Write (no seek, no lock needed).  The file is pre-opened
        ' with FileAccess.ReadWrite so RandomAccess can address any offset.
        Dim L0_BIN_PATH As String = DISK_CACHE_DIR & "L0.bin"
        Dim l0Handle As Microsoft.Win32.SafeHandles.SafeFileHandle = Nothing
        Dim l0NextOffset As Long = 0L   ' atomic file-position counter
        If numChunks > DISK_THRESHOLD Then
            l0Handle = System.IO.File.OpenHandle(L0_BIN_PATH,
                                                 FileMode.Create,
                                                 FileAccess.ReadWrite,
                                                 FileShare.None,
                                                 FileOptions.Asynchronous Or FileOptions.WriteThrough)
        End If
        ' §248 (issue #48): on a HYBRID host, offload chunk serialization (serialize-to-buffer
        ' + RandomAccess.Write) from the P-core compute workers to a few E-core serializer
        ' threads, so the compute workers don't stall on disk I/O.  Compute workers push the
        ' computed (P,Q,T) to a bounded queue; serializers drain it, write to L0.bin at an
        ' atomically-reserved offset, fill chunkResults, and free the mpz_t.  Adaptive per the
        ' host-adaptation rule: only when CpuTopologyIsHybrid (else the existing inline path runs
        ' — extra serializer threads would just oversubscribe a non-hybrid box).  Safe for resume:
        ' Phase 1 is all-or-nothing (no mid-phase checkpoint), so we only need to fully drain the
        ' queue before Phase 2 — done via Task.WaitAll below.
        Dim _asyncSer As Boolean = (numChunks > DISK_THRESHOLD) AndAlso CpuTopologyIsHybrid AndAlso l0Handle IsNot Nothing
        Dim _l0Queue As System.Collections.Concurrent.BlockingCollection(Of ChunkWork) = Nothing
        Dim _serTasks As System.Threading.Tasks.Task() = Nothing
        If _asyncSer Then
            Dim _nSer As Integer = System.Math.Max(1, System.Math.Min(4, CpuTopologyECoreIds.Length))
            _l0Queue = New System.Collections.Concurrent.BlockingCollection(Of ChunkWork)(System.Math.Max(8, Environment.ProcessorCount))
            _serTasks = New System.Threading.Tasks.Task(_nSer - 1) {}
            For s As Integer = 0 To _nSer - 1
                _serTasks(s) = System.Threading.Tasks.Task.Run(
                    Sub()
                        PinCurrentThreadToECores()   ' §247: run on E-cores, exempt from the P-core watchdog
                        Dim _serStaging(4194303) As Byte
                        For Each w As ChunkWork In _l0Queue.GetConsumingEnumerable()
                            Dim wn As DiskNode
                            wn.FilePath = Nothing : wn.MemP = Nothing : wn.MemQ = Nothing : wn.MemT = Nothing
                            wn.Level = 0 : wn.Index = w.Idx : wn.IsInMemory = False
                            Using ms As New System.IO.MemoryStream()
                                Using bw As New System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen:=True)
                                    SerializeOneMpz(w.P, bw, _serStaging)
                                    SerializeOneMpz(w.Q, bw, _serStaging)
                                    SerializeOneMpz(w.T, bw, _serStaging)
                                End Using
                                gmp_lib.mpz_clears(w.P, w.Q, w.T, Nothing)
                                Dim chunkLen As Long = ms.Length
                                Dim fileOffset As Long = Interlocked.Add(l0NextOffset, chunkLen) - chunkLen
                                wn.FileOffset = fileOffset
                                RandomAccess.Write(l0Handle, ms.GetBuffer().AsMemory(0, CInt(chunkLen)).Span, fileOffset)
                            End Using
                            wn.FilePath = L0_BIN_PATH
                            chunkResults(w.Idx) = wn
                            Interlocked.Increment(_serializedChunks)
                        Next
                        UnpinCurrentThreadFromECores()
                    End Sub)
            Next
            AppendLog($"[BinarySplitGMP§248] async Phase-1 serialization: {_nSer} E-core serializer thread(s) (hybrid host){vbCrLf}")
        End If

        ' Dedicated background thread (not thread-pool) polls completedChunks
        ' every 500 ms.  System.Threading.Timer callbacks run on thread-pool
        ' threads, which Parallel.For exhausts — causing ~2 min delay before
        ' the first update.  A dedicated Thread gets its own OS time-slice
        ' independent of thread-pool saturation.
        Dim phase1PollThread As New System.Threading.Thread(
            Sub()
                While Interlocked.Read(completedChunks) < numChunks
                    Dim snap As Long = Interlocked.Read(completedChunks)
                    Me.BeginInvoke(Sub()
                                       LblStatus.Text = $"Phase 1: {snap:N0} / {numChunks:N0} chunks ({snap * 100L \ numChunks:N0}%)"
                                   End Sub)
                    System.Threading.Thread.Sleep(500)
                End While
            End Sub)
        phase1PollThread.IsBackground = True
        phase1PollThread.Start()

        Parallel.For(0L, numChunks,
            Sub(i As Long)
                Dim chunkStart As Long = i * CHUNK_SIZE
                Dim chunkEnd As Long = System.Math.Min(chunkStart + CHUNK_SIZE, numTerms)

                Dim tempP As mpz_t = Nothing
                Dim tempQ As mpz_t = Nothing
                Dim tempT As mpz_t = Nothing
                ' §234 (issue #59, 2026-05-23; fix 2026-05-24): tail-mode parallel
                ' top-split.  Late chunks dominate end-of-Phase-1 wall time once
                ' outer Parallel.For queue depth drops below 24 cores.
                '
                ' Initial §234 used chunk *index* (i >= numChunks-24) as the
                ' trigger.  Parallel.For partitions the range across workers, so
                ' high-index chunks can execute while many other chunks are still
                ' in flight — the inner Parallel.Invoke then oversubscribes,
                ' costing wall time (62.4 s vs 59.4 s baseline at 1 B).
                '
                ' Fixed to use queue-depth proxy per issue #59 spec: trigger only
                ' when completedChunks >= numChunks-24, i.e. ≤24 chunks remain
                ' across all workers, so the inner Parallel.Invoke fills idle
                ' cores instead of oversubscribing.  Threshold chunkSize >= 512
                ' ensures the split has enough work to amortize scheduling.
                Dim _tailMode234 As Boolean = (Interlocked.Read(completedChunks) >= numChunks - 24L) AndAlso (chunkEnd - chunkStart >= 512L)
                If _tailMode234 Then
                    BinarySplitChunkParallelTop(chunkStart, chunkEnd, tempP, tempQ, tempT)
                Else
                    BinarySplitChunk(chunkStart, chunkEnd, tempP, tempQ, tempT)
                End If

                Dim _isMem As Boolean = (numChunks <= DISK_THRESHOLD)
                If (Not _isMem) AndAlso _asyncSer Then
                    ' §248: hand the computed (P,Q,T) to an E-core serializer thread; it writes
                    ' to L0.bin, fills chunkResults(i), and frees the mpz_t.  completedChunks
                    ' tracks COMPUTE completion here (drives the §234 tail-mode trigger + poll);
                    ' _serializedChunks tracks writes (drained below before Phase 2).
                    _l0Queue.Add(New ChunkWork(tempP, tempQ, tempT, CInt(i)))
                    Dim doneC As Long = Interlocked.Increment(completedChunks)
                    If doneC Mod 5000L = 0L Then
                        WriteToLog($"[Phase1] {doneC:N0}/{numChunks:N0} chunks computed (async serialize)")
                    End If
                Else
                    Dim node As DiskNode
                    node.FilePath = Nothing
                    node.MemP = Nothing
                    node.MemQ = Nothing
                    node.MemT = Nothing
                    node.Level = 0
                    node.Index = CInt(i)
                    node.IsInMemory = _isMem

                    If node.IsInMemory Then
                        node.MemP = tempP
                        node.MemQ = tempQ
                        node.MemT = tempT
                    Else
                        ' §106 Gap 3: serialize to MemoryStream (no lock), atomically reserve
                        ' a file region with Interlocked.Add, then write at the reserved offset
                        ' via RandomAccess.Write — completely lock-free.
                        Dim stagingBuf(4194303) As Byte  ' 4 MB staging buffer (§56)
                        Using ms As New System.IO.MemoryStream()
                            Using bw As New System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen:=True)
                                SerializeOneMpz(tempP, bw, stagingBuf)
                                SerializeOneMpz(tempQ, bw, stagingBuf)
                                SerializeOneMpz(tempT, bw, stagingBuf)
                            End Using
                            gmp_lib.mpz_clears(tempP, tempQ, tempT, Nothing)
                            Dim chunkLen As Long = ms.Length
                            ' Reserve [offset, offset+chunkLen) atomically — no lock needed.
                            Dim fileOffset As Long = Interlocked.Add(l0NextOffset, chunkLen) - chunkLen
                            node.FileOffset = fileOffset
                            Dim chunkData As ReadOnlyMemory(Of Byte) = ms.GetBuffer().AsMemory(0, CInt(chunkLen))
                            RandomAccess.Write(l0Handle, chunkData.Span, fileOffset)
                        End Using
                        node.FilePath = L0_BIN_PATH
                    End If

                    chunkResults(CInt(i)) = node

                    Dim done As Long = Interlocked.Increment(completedChunks)
                    If done Mod 5000L = 0L Then
                        WriteToLog($"[Phase1] {done:N0}/{numChunks:N0} chunks complete (parallel)")
                    End If
                End If
            End Sub)

        ' §248: all chunks computed + pushed — drain the serializer queue and wait for every
        ' write to land in L0.bin before Phase 2 reads the nodes (Phase 1's completion barrier).
        If _asyncSer Then
            _l0Queue.CompleteAdding()
            System.Threading.Tasks.Task.WaitAll(_serTasks)
            _l0Queue.Dispose()
            WriteToLog($"[Phase1§248] async serialization drained: {Interlocked.Read(_serializedChunks):N0}/{numChunks:N0} chunks written to L0.bin")
        End If

        phase1PollThread.Join()

        If l0Handle IsNot Nothing Then
            l0Handle.Dispose()
            l0Handle = Nothing
        End If

        diskNodes.AddRange(chunkResults)

        If currentSize > DISK_THRESHOLD Then
            LogPhase($"Parallel: {currentSize:N0} chunks streamed to L0.bin")
        Else
            LogPhase($"Parallel: {currentSize:N0} chunks computed in memory")
        End If

        ' ── Phase 2: combine levels until one node remains ──────────────────
Phase2:
        While currentSize > STOP_AT
            level += 1
            Dim nextSize As Long = (currentSize + 1) \ 2
            Dim nextDiskNodes As New List(Of DiskNode)()
            ' §93: --checkpoint-from-level forces disk serialization at and above
            ' the specified level, regardless of threshold. This ensures checkpoint
            ' files exist for a subsequent --resume-from-level run.
            Dim useDisk As Boolean = nextSize > DISK_THRESHOLD OrElse level >= _checkpointFromLevel
            ' True only for the final combine pass (2 nodes → 1).  Controls
            ' whether _logLevel >= 3 emits per-operation trace for this level.
            Dim isLastLevel As Boolean = (currentSize <= 2)
            ' True for the top ~4 levels (≤16 nodes).  Enables per-multiply
            ' operand-size logging so we can identify which mpz_mul produces a
            ' corrupt allocation size at Level 16.
            Dim isTopLevel As Boolean = (currentSize <= 16)

            If useDisk Then
                LogPhase($"Level {level}: Processing {currentSize:N0} → {nextSize:N0} nodes (DISK mode)")
            Else
                LogPhase($"Level {level}: Processing {currentSize:N0} → {nextSize:N0} nodes (MEMORY mode)")
            End If

            ' ── Choose serial or parallel combine ────────────────────────────
            ' Each pair (left, right) is fully independent: it reads from unique
            ' disk files, allocates only thread-local mpz_t objects, and writes
            ' to a unique output file.  Parallel.For is used when pairCount is
            ' large enough to justify the overhead.  At the top levels (few pairs,
            ' very large operands) we stay serial to avoid multiplying peak RAM.
            Dim pairCount As Long = diskNodes.Count \ 2L

            If pairCount >= 4L Then
                ' ── Parallel path ────────────────────────────────────────────
                Dim nextResults(CInt(pairCount) - 1) As DiskNode
                Dim completedPairs As Long = 0L
                Dim phase2PollThread As New System.Threading.Thread(
                    Sub()
                        While Interlocked.Read(completedPairs) < pairCount
                            Dim snap As Long = Interlocked.Read(completedPairs)
                            Me.BeginInvoke(Sub()
                                LblStatus.Text = $"Phase 2 Level {level}: {snap:N0} / {pairCount:N0} pairs"
                            End Sub)
                            System.Threading.Thread.Sleep(500)
                        End While
                    End Sub)
                phase2PollThread.IsBackground = True
                phase2PollThread.Start()
                ' §24: Outer DOP raised to ProcessorCount; inner Parallel.Invoke removed.
                ' Each outer task runs 4 SafeMpzMul calls sequentially, eliminating
                ' 2×pairCount task-scheduling round-trips (20.45% inclusive in trace).
                ' _safeMulDop scales inversely with pairCount so sub-product parallelism
                ' fills idle cores when the level has few pairs:
                '   pairCount ≥ ProcessorCount → _safeMulDop = 1 (outer For fills all cores)
                '   pairCount = 4              → _safeMulDop = 6 (4 × 6 = 24 active threads)
                '   pairCount = 8              → _safeMulDop = 3 (8 × 3 = 24 active threads)
                ' No deadlock: outer tasks no longer block on Parallel.Invoke; inner
                ' Parallel.For sub-tasks are short fast-path GmpRaw_mul calls with no nesting.
                ' §106 Gap 2: use ceiling division so fractional capacity (e.g. 24/16=1.5)
                ' rounds up to 2 instead of down to 1, giving each pair 2 inner sub-product
                ' threads rather than 1 when there is spare CPU capacity.
                Dim _rawDop As Double = CDbl(Environment.ProcessorCount) / CDbl(System.Math.Max(1L, pairCount))
                Dim _innerDop As Integer = If(_rawDop >= 1.5, CInt(System.Math.Ceiling(_rawDop)), 1)
                System.Threading.Volatile.Write(_safeMulDop, System.Math.Max(1, _innerDop))  ' §27
                ' §106 Gap 4: level-aware outer DOP — cap at pairCount so we don't
                ' spin up more outer tasks than there are pairs to process.
                ' outerDop × innerDop ≈ ProcessorCount keeps total active tasks bounded.
                Dim _outerDop As Integer = System.Math.Min(Environment.ProcessorCount, CInt(System.Math.Max(1L, pairCount)))
                Dim _p2opts As New System.Threading.Tasks.ParallelOptions() With {
                    .MaxDegreeOfParallelism = _outerDop
                }
                Parallel.For(0L, pairCount, _p2opts,
                    Sub(pairIdx As Long)
                        Dim leftIdx As Integer = CInt(pairIdx * 2L)
                        Dim rightIdx As Integer = CInt(pairIdx * 2L + 1L)

                        ' Load left
                        Dim leftP As mpz_t = Nothing
                        Dim leftQ As mpz_t = Nothing
                        Dim leftT As mpz_t = Nothing
                        If diskNodes(leftIdx).IsInMemory Then
                            leftP = diskNodes(leftIdx).MemP
                            leftQ = diskNodes(leftIdx).MemQ
                            leftT = diskNodes(leftIdx).MemT
                        Else
                            LoadNodeFromDisk(diskNodes(leftIdx).FilePath, diskNodes(leftIdx).FileOffset, leftP, leftQ, leftT, isLastLevel)
                            ' Level-0 nodes share L0.bin — skip per-node delete; L0.bin is
                            ' cleaned up by the cache clear at the start of the next run.
                            If diskNodes(leftIdx).Level > 0 Then
                                Try : System.IO.File.Delete(diskNodes(leftIdx).FilePath) : Catch : End Try
                            End If
                        End If

                        ' Load right
                        Dim rightP As mpz_t = Nothing
                        Dim rightQ As mpz_t = Nothing
                        Dim rightT As mpz_t = Nothing
                        If diskNodes(rightIdx).IsInMemory Then
                            rightP = diskNodes(rightIdx).MemP
                            rightQ = diskNodes(rightIdx).MemQ
                            rightT = diskNodes(rightIdx).MemT
                        Else
                            LoadNodeFromDisk(diskNodes(rightIdx).FilePath, diskNodes(rightIdx).FileOffset, rightP, rightQ, rightT, isLastLevel)
                            If diskNodes(rightIdx).Level > 0 Then
                                Try : System.IO.File.Delete(diskNodes(rightIdx).FilePath) : Catch : End Try
                            End If
                        End If

                        ' Combine (same early-free and in-place-add sequence as serial path)
                        Dim newP As New mpz_t()
                        Dim newQ As New mpz_t()
                        Dim tempA As New mpz_t()
                        Dim tempB As New mpz_t()
                        gmp_lib.mpz_inits(newP, newQ, tempA, tempB, Nothing)

                        ' §24: sequential within each outer task — parallelism comes from
                        ' the outer Parallel.For (DOP=ProcessorCount) and from _safeMulDop
                        ' sub-product parallelism within each SafeMpzMul call.
                        SafeMpzMul(newP, leftP, rightP)
                        SafeMpzMul(newQ, leftQ, rightQ)
                        gmp_lib.mpz_clears(rightP, Nothing)
                        gmp_lib.mpz_clears(leftQ, Nothing)

                        SafeMpzMul(tempA, leftT, rightQ)
                        SafeMpzMul(tempB, leftP, rightT)
                        gmp_lib.mpz_clears(leftT, rightQ, Nothing)
                        gmp_lib.mpz_clears(leftP, rightT, Nothing)

                        GmpRaw_add(tempA.Pointer, tempA.Pointer, tempB.Pointer)  ' §26: bypass wrapper dispatch
                        gmp_lib.mpz_clears(tempB, Nothing)

                        ' Store result
                        Dim resultNode As DiskNode
                        resultNode.FilePath = Nothing
                        resultNode.MemP = Nothing
                        resultNode.MemQ = Nothing
                        resultNode.MemT = Nothing
                        resultNode.Level = level
                        resultNode.Index = CInt(pairIdx)
                        resultNode.IsInMemory = Not useDisk

                        If useDisk Then
                            resultNode.FilePath = $"{DISK_CACHE_DIR}L{level}_N{pairIdx}.bin"
                            SerializeNodeToDisk(newP, newQ, tempA, resultNode.FilePath, isLastLevel)
                            gmp_lib.mpz_clears(newP, newQ, tempA, Nothing)
                        Else
                            resultNode.MemP = newP
                            resultNode.MemQ = newQ
                            resultNode.MemT = tempA
                        End If

                        nextResults(CInt(pairIdx)) = resultNode
                        Dim _done As Long = Interlocked.Increment(completedPairs)
                        If _done Mod 1000 = 0 Then
                            LogPhase($"  Processed {_done:N0}/{pairCount:N0} pairs")
                        End If
                    End Sub)
                phase2PollThread.Join()
                nextDiskNodes.AddRange(nextResults)
                ' §69: Restore full DOP for serial Phase 2 top levels and ComputePiGMP.
                System.Threading.Volatile.Write(_safeMulDop, Environment.ProcessorCount)  ' §27

            Else
                ' ── Serial path (top levels: few pairs, very large operands) ─
                ' §95: Cap inner DOP at 3 for serial-path levels.
                ' At these levels operands are large enough for SafeMpzMul to recurse
                ' 3 levels deep (each level splits into 9 sub-products).  With the
                ' default DOP=ProcessorCount=24, up to 24^3=13,824 sub-product tasks
                ' can run concurrently, each allocating a 300–1,000 MB accum buffer.
                ' Observed crash at Level 19 (5B digits): 81 concurrent depth-3 tasks
                ' × ~320 MB each = ~26 GB just for intermediates, exhausting VirtualAlloc.
                ' DOP=3 gives 3^3=27 concurrent leaf tasks — saturates 24 cores while
                ' bounding peak intermediate memory to ~9 GB (vs ~26 GB at DOP=24).
                '
                ' §231 (issue #58, 2026-05-23): scale-aware DOP — §95's cap of 3 is too
                ' conservative at smaller digit counts.  Phase-2 top-level operand sizes
                ' scale ~linearly with numTerms; per-leaf-task accum buffer is roughly
                ' (topLimbs / 3) limbs ≈ (numTerms × 2 / 3) limbs at top level.  Total
                ' leaf-task RAM = DOP^3 × bufPerLeaf.  Aim for ≤ ~40 GB (leaves 24 GB
                ' headroom on the 64 GB box) by stepping DOP based on numTerms:
                '   numTerms <  100 M (~ < 1.4 B digits): DOP=6 → 216 × 50 MB ≈ 10 GB
                '   numTerms <  250 M (1.4-3.5 B digits): DOP=4 → 64 × 200 MB ≈ 13 GB
                '   numTerms >= 250 M (>= 3.5 B digits) : DOP=3 → 27 × 500 MB ≈ 13 GB
                ' Bumps 1B Phase-2 top levels (levels 12, 13) from DOP=3 to DOP=6
                ' (~30-50 % faster); keeps original §95 behaviour at 5B+ scale.
                Dim _chosenDop231 As Integer
                If numTerms < 100_000_000L Then
                    _chosenDop231 = 6
                ElseIf numTerms < 250_000_000L Then
                    _chosenDop231 = 4
                Else
                    _chosenDop231 = 3
                End If
                System.Threading.Volatile.Write(_safeMulDop, _chosenDop231)  ' §231 (was hardcoded 3 in §95)
                AppendLog($"[BinarySplit§231] serial-path DOP at level={level}: numTerms={numTerms:N0}, pairCount={pairCount}, chosen DOP={_chosenDop231}{vbCrLf}")
                ' §273 (#121/#122): route THIS level's large merges through the chunked-grid path
                ' (parallel cells at PI_CG_DOP, low per-cell RAM) instead of §gen at the §231 cap
                ' above.  Engages only for the high-term runs §231 pins to DOP=3 (≥250M terms),
                ' where chunked-grid's cell parallelism is the proven win (§262/§269 in the divide).
                ' Read the opt-out/threshold env each serial level (cheap; a handful of times/run).
                _combineChunkedGrid = (Environment.GetEnvironmentVariable("PI_COMBINE_CG") <> "0")
                Dim _cgMtEnv As String = Environment.GetEnvironmentVariable("PI_COMBINE_CG_MINTERMS")
                Dim _cgMtParsed As Long
                If _cgMtEnv IsNot Nothing AndAlso Long.TryParse(_cgMtEnv, _cgMtParsed) AndAlso _cgMtParsed >= 0L Then _combineCgMinTerms = _cgMtParsed
                Dim _useCgLevel As Boolean = _combineChunkedGrid AndAlso numTerms >= _combineCgMinTerms
                If _useCgLevel Then AppendLog($"[Combine§273] level={level}: merges via chunked-grid (numTerms={numTerms:N0} ≥ {_combineCgMinTerms:N0}; §231 DOP={_chosenDop231} bypassed){vbCrLf}")
                Dim nodeIdx As Long = 0
                ' §249 (issue #49, Opportunity B): prefetch the NEXT pair's disk nodes on an
                ' E-core while the current pair combines, hiding the read I/O behind the
                ' multi-second combine.  (Opportunity A — compute on P-cores — is already
                ' delivered by §247.)  Bit-identical: this only changes WHERE/WHEN a node is
                ' read, not the data or the combine.  Adaptive: prefetch only on a hybrid host
                ' with on-disk inputs; otherwise nodes load inline exactly as before.
                Dim _loadNode As Func(Of Integer, ValueTuple(Of mpz_t, mpz_t, mpz_t)) =
                    Function(idx As Integer)
                        Dim nP As mpz_t = Nothing, nQ As mpz_t = Nothing, nT As mpz_t = Nothing
                        If diskNodes(idx).IsInMemory Then
                            nP = diskNodes(idx).MemP : nQ = diskNodes(idx).MemQ : nT = diskNodes(idx).MemT
                        Else
                            LoadNodeFromDisk(diskNodes(idx).FilePath, diskNodes(idx).FileOffset, nP, nQ, nT, isLastLevel)
                            If diskNodes(idx).Level > 0 Then
                                Try : System.IO.File.Delete(diskNodes(idx).FilePath) : Catch : End Try
                            End If
                        End If
                        Return (nP, nQ, nT)
                    End Function
                Dim _loadPair As Func(Of Integer, ValueTuple(Of mpz_t, mpz_t, mpz_t, mpz_t, mpz_t, mpz_t)) =
                    Function(baseIdx As Integer)
                        Dim l = _loadNode(baseIdx)
                        Dim r = _loadNode(baseIdx + 1)
                        Return (l.Item1, l.Item2, l.Item3, r.Item1, r.Item2, r.Item3)
                    End Function
                Dim _pfEnabled As Boolean = CpuTopologyIsHybrid AndAlso diskNodes.Count > 0 AndAlso Not diskNodes(0).IsInMemory
                Dim _pfTask As System.Threading.Tasks.Task(Of ValueTuple(Of mpz_t, mpz_t, mpz_t, mpz_t, mpz_t, mpz_t)) = Nothing
                Dim _pfBase As Long = -1L
                While nodeIdx < diskNodes.Count - 1

                    ' §249: current pair's operands — from the matching prefetch task, else inline.
                    Dim _cur As ValueTuple(Of mpz_t, mpz_t, mpz_t, mpz_t, mpz_t, mpz_t)
                    If _pfTask IsNot Nothing AndAlso _pfBase = nodeIdx Then
                        _cur = _pfTask.Result
                        _pfTask = Nothing
                    Else
                        _cur = _loadPair(CInt(nodeIdx))
                    End If
                    Dim leftP As mpz_t = _cur.Item1
                    Dim leftQ As mpz_t = _cur.Item2
                    Dim leftT As mpz_t = _cur.Item3
                    Dim rightP As mpz_t = _cur.Item4
                    Dim rightQ As mpz_t = _cur.Item5
                    Dim rightT As mpz_t = _cur.Item6

                    ' §249: kick off prefetch of the next pair on an E-core (hybrid + disk only).
                    Dim _nextBase As Integer = CInt(nodeIdx + 2L)
                    If _pfEnabled AndAlso CLng(_nextBase) < diskNodes.Count - 1 Then
                        _pfBase = CLng(_nextBase)
                        _pfTask = System.Threading.Tasks.Task.Run(
                            Function()
                                PinCurrentThreadToECores()
                                Dim _pp = _loadPair(_nextBase)
                                UnpinCurrentThreadFromECores()
                                Return _pp
                            End Function)
                    Else
                        _pfTask = Nothing
                    End If

                    ' Combine
                    Dim newP As New mpz_t()
                    Dim newQ As New mpz_t()
                    Dim tempA As New mpz_t()
                    Dim tempB As New mpz_t()
                    gmp_lib.mpz_inits(newP, newQ, tempA, tempB, Nothing)

                    ' Early-free optimisation: release each input operand immediately
                    ' after its last use so GMP can reuse that memory for the next
                    ' allocation.  Holding all 6 inputs alive through all 4 multiplies
                    ' was the primary cause of the Level-17 OOM crash — peak RAM was
                    ' ~2 GB higher than necessary.
                    '
                    ' Dependency map (determines earliest safe free point):
                    '   rightP  → only needed for newP      → free after step 1
                    '   leftQ   → only needed for newQ      → free after step 2
                    '   rightQ  → needed for newQ AND tempA → free after step 3
                    '   leftT   → only needed for tempA     → free after step 3
                    '   leftP   → needed for newP AND tempB → free after step 4
                    '   rightT  → only needed for tempB     → free after step 4
                    '
                    ' In-place add optimisation (Level-17 crash fix):
                    '   mpz_add(tempA, tempA, tempB) accumulates the T result into
                    '   tempA's already-allocated limb buffer (GMP §5.5 explicitly
                    '   permits an aliased destination).  This avoids allocating a
                    '   fresh ~443 MB block for newT while newP, newQ, tempA, and
                    '   tempB are all still live, which was pushing peak RAM from
                    '   ~1,781 MB to ~2,215 MB and triggering a GMP abort().
                    '   After the add, tempB is freed and tempA holds the T result.
                    ' Steps 1 & 2: newP = leftP*rightP and newQ = leftQ*rightQ.
                    ' §91: When pairCount=1 (a single pair at the very top of the combine tree)
                    ' the operands are the largest in the entire computation. Each SafeMpzMul
                    ' already saturates all cores via its inner Parallel.For (DOP=ProcessorCount).
                    ' Running both simultaneously via Parallel.Invoke doubles peak FFT scratch
                    ' (18 concurrent GmpRaw_mul × 200-250 MB each = 3-4 GB extra), which can
                    ' cause GmpAllocFunc to fail at 5B+ digits. Run sequentially instead — no
                    ' throughput loss since all cores are busy inside the single SafeMpzMul.
                    ' At pairCount >= 2 the operands are smaller (earlier combine levels) and
                    ' Parallel.Invoke remains beneficial.
                    If _logLevel >= 3 AndAlso isTopLevel Then
                        Dim _szLP As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftP.Pointer, 4)
                        Dim _szRP As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightP.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul newP  leftP={System.Math.Abs(_szLP):N0} rightP={System.Math.Abs(_szRP):N0} limbs")
                        Dim _szLQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftQ.Pointer, 4)
                        Dim _szRQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightQ.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul newQ  leftQ={System.Math.Abs(_szLQ):N0} rightQ={System.Math.Abs(_szRQ):N0} limbs")
                    End If
                    If _useCgLevel Then
                        ' §273 (#121): each chunked-grid mul already saturates up to PI_CG_DOP
                        ' cores, so run the two sequentially (no Parallel.Invoke ⇒ no core
                        ' oversubscription, no doubled FFT scratch — same rationale as §91).
                        SafeMpzMulCG(newP, leftP, rightP)
                        SafeMpzMulCG(newQ, leftQ, rightQ)
                    ElseIf pairCount >= 2L Then
                        System.Threading.Tasks.Parallel.Invoke(
                            Sub() SafeMpzMul(newP, leftP, rightP),
                            Sub() SafeMpzMul(newQ, leftQ, rightQ))
                    Else
                        SafeMpzMul(newP, leftP, rightP)
                        SafeMpzMul(newQ, leftQ, rightQ)
                    End If
                    gmp_lib.mpz_clears(rightP, Nothing)             ' rightP done
                    gmp_lib.mpz_clears(leftQ, Nothing)              ' leftQ done

                    ' Steps 3 & 4: tempA = leftT*rightQ and tempB = leftP*rightT.
                    ' §91: Same sequential-at-pairCount=1 rule as steps 1 & 2.
                    If _logLevel >= 3 AndAlso isTopLevel Then
                        Dim _szLT As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftT.Pointer, 4)
                        Dim _szRQ2 As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightQ.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul tempA  leftT={System.Math.Abs(_szLT):N0} rightQ={System.Math.Abs(_szRQ2):N0} limbs")
                        Dim _szLP2 As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftP.Pointer, 4)
                        Dim _szRT As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightT.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul tempB  leftP={System.Math.Abs(_szLP2):N0} rightT={System.Math.Abs(_szRT):N0} limbs")
                    End If
                    If _useCgLevel Then
                        ' §273 (#121): T merges — sign-aware (leftT/rightT may be negative).
                        SafeMpzMulCG(tempA, leftT, rightQ)
                        SafeMpzMulCG(tempB, leftP, rightT)
                    ElseIf pairCount >= 2L Then
                        System.Threading.Tasks.Parallel.Invoke(
                            Sub() SafeMpzMul(tempA, leftT, rightQ),
                            Sub() SafeMpzMul(tempB, leftP, rightT))
                    Else
                        SafeMpzMul(tempA, leftT, rightQ)
                        SafeMpzMul(tempB, leftP, rightT)
                    End If
                    gmp_lib.mpz_clears(leftT, rightQ, Nothing)      ' leftT, rightQ done
                    gmp_lib.mpz_clears(leftP, rightT, Nothing)      ' leftP, rightT done

                    If _logLevel >= 3 AndAlso isTopLevel Then
                        Dim _szTA As Integer = Runtime.InteropServices.Marshal.ReadInt32(tempA.Pointer, 4)
                        Dim _szTB As Integer = Runtime.InteropServices.Marshal.ReadInt32(tempB.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: add newT  tempA={System.Math.Abs(_szTA):N0} tempB={System.Math.Abs(_szTB):N0} limbs")
                    End If
                    GmpRaw_add(tempA.Pointer, tempA.Pointer, tempB.Pointer)  ' §26: bypass wrapper dispatch; T result in tempA's buffer
                    gmp_lib.mpz_clears(tempB, Nothing)              ' tempB done; tempA IS newT
                    If _logLevel >= 3 AndAlso isTopLevel Then WriteToLog($"[Combine] L{level} N{nodeIdx\2}: combine complete")

                    ' Store result
                    ' tempA holds the T result (renamed conceptually to newT below)
                    Dim resultNode As DiskNode
                    resultNode.FilePath = Nothing
                    resultNode.MemP = Nothing
                    resultNode.MemQ = Nothing
                    resultNode.MemT = Nothing
                    resultNode.Level = level
                    resultNode.Index = nextDiskNodes.Count
                    resultNode.IsInMemory = Not useDisk

                    If useDisk Then
                        resultNode.FilePath = $"{DISK_CACHE_DIR}L{level}_N{resultNode.Index}.bin"
                        If _logLevel >= 3 AndAlso isTopLevel Then
                            Dim _preSerNewQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(newQ.Pointer, 4)
                            WriteToLog($"[Combine] L{level} N{nodeIdx\2}: pre-serialize newQ._mp_size={_preSerNewQ:N0}")
                        End If
                        SerializeNodeToDisk(newP, newQ, tempA, resultNode.FilePath, isLastLevel)
                        gmp_lib.mpz_clears(newP, newQ, tempA, Nothing)
                    Else
                        resultNode.MemP = newP
                        resultNode.MemQ = newQ
                        resultNode.MemT = tempA
                    End If

                    nextDiskNodes.Add(resultNode)

                    Dim _done As Integer = nextDiskNodes.Count
                    Me.BeginInvoke(Sub()
                        LblStatus.Text = $"Phase 2 Level {level}: {_done:N0} / {pairCount:N0} pairs"
                    End Sub)

                    If nextDiskNodes.Count Mod 100 = 0 Then
                        LogPhase($"  Processed {nextDiskNodes.Count:N0}/{nextSize:N0} node pairs")
                    End If

                    nodeIdx += 2
                End While
            End If

            ' Handle odd node — carry it forward unchanged
            If diskNodes.Count Mod 2 = 1 Then
                nextDiskNodes.Add(diskNodes(diskNodes.Count - 1))
            End If

            diskNodes = nextDiskNodes
            currentSize = nextSize

            ' §61: Non-blocking GC between levels; blocking compacting only at the final level.
            ' Aggressive+blocking was pausing all threads for hundreds of ms at each of 17 levels.
            ' At lower levels an Optimized non-blocking collect is sufficient to reclaim the
            ' mpz_t wrapper objects from freed pairs without stalling the parallel combines.
            If isLastLevel Then
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, True, True)
                GC.WaitForPendingFinalizers()
            Else
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, False, False)
            End If

            ' §65: Flush the pool between levels.  Blocks freed during level N are
            ' unique sizes that double each level — they will never be reused at the
            ' same size in level N+1, so pooling them only commits pages that can
            ' never be reclaimed.  Flushing here keeps committed memory proportional
            ' to the current working set instead of accumulating across all levels.
            GmpNativeAlloc_Flush()  ' §30: native pool flush (replaced managed FlushGmpPool)

            ' §94: Auto-checkpoint — write level snapshot after GC/FlushGmpPool so that
            ' scratch memory from the completed multiplications is freed before the snapshot
            ' write adds serialization overhead.  Nodes are still live at this point.
            ' Skipped for the final level (1–2 nodes; Phase 3 is fast).
            ' After confirming the new snapshot, delete the previous level's snapshot.
            If _autoCheckpoint AndAlso Not isLastLevel Then
                If WriteLevelSnapshot(level, diskNodes, numTerms, numChunks) Then  ' §96
                    BackupSnapshotToStoreAsync($"snap_L{level}")   ' §104 + §232: async SnapshotStore backup
                    DeleteSnapshotFromStore(level - 1)        ' §104: remove superseded backup
                    DeleteSnapshotDir(level - 1)              ' remove superseded NodeCache entry
                End If
            End If

            Dim memNow As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            LogPhase($"Combine level {level}: {currentSize:N0} nodes remaining (RAM: {memNow:N0}MB)")
        End While

        ' ── Phase 3: load the single final node into memory ─────────────────
        ' Issue #6: returns List(Of Result) — no Tuple allocations.
        nodes = New List(Of Result)()
        For i As Integer = 0 To diskNodes.Count - 1
            If diskNodes(i).IsInMemory Then
                Dim r As New Result With {
                    .P = diskNodes(i).MemP,
                    .Q = diskNodes(i).MemQ,
                    .T = diskNodes(i).MemT
                }
                nodes.Add(r)
            Else
                Dim rP As mpz_t = Nothing
                Dim rQ As mpz_t = Nothing
                Dim rT As mpz_t = Nothing
                LoadNodeFromDisk(diskNodes(i).FilePath, diskNodes(i).FileOffset, rP, rQ, rT)
                nodes.Add(New Result With {.P = rP, .Q = rQ, .T = rT})
                Try
                    System.IO.File.Delete(diskNodes(i).FilePath)
                Catch
                End Try
            End If
        Next

        LogPhase($"Final {nodes.Count} node(s) loaded into memory")
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §99: Safe 10^n via repeated squaring using SafeMpzMul
    ' ════════════════════════════════════════════════════════════════════════
    ''' <summary>
    ''' Computes result = 10^exponent using binary (repeated squaring) with
    ''' SafeMpzMul for every multiplication, avoiding the GMP 32-bit mpn_mul_fft
    ''' overflow that crashes mpz_ui_pow_ui when the intermediate values exceed
    ''' ~33M limbs (~2GB).  At 5B digits, 10^2,500,000,000 ≈ 130M limbs — well
    ''' above the threshold.
    ''' </summary>
    Private Sub SafeMpzPow10(result As mpz_t, exponent As Long)
        ' Seed: result = 1
        gmp_lib.mpz_set_ui(result, 1UI)

        If exponent = 0L Then Return

        ' base = 10
        Dim base As New mpz_t()
        gmp_lib.mpz_init_set_ui(base, 10UI)

        Dim exp As Long = exponent
        Do
            If (exp And 1L) = 1L Then
                ' result *= base
                Dim tmp As New mpz_t()
                gmp_lib.mpz_init(tmp)
                SafeMpzMul(tmp, result, base)
                gmp_lib.mpz_swap(result, tmp)
                gmp_lib.mpz_clear(tmp)
            End If
            exp >>= 1
            If exp = 0L Then Exit Do
            ' base = base^2
            Dim tmp2 As New mpz_t()
            gmp_lib.mpz_init(tmp2)
            SafeMpzMul(tmp2, base, base)
            gmp_lib.mpz_swap(base, tmp2)
            gmp_lib.mpz_clear(tmp2)
        Loop

        gmp_lib.mpz_clear(base)
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §216: Chunked decimal conversion (replaces mpz_get_str for huge outputs)
    ' ════════════════════════════════════════════════════════════════════════
    ' GMP's mpz_get_str crashes with 0xC0000005 AccessViolation when the output
    ' exceeds approximately 2 GB.  Observed on the 2026-05-19 5B run: §piCkpt
    ' fired (gmpPi safely saved), then mpz_get_str was called on the 5B-digit
    ' result (output ≈ 5 GB) and crashed inside __gmpz_get_str.  The whole
    ' multi-hour SafeMpzDiv pipeline ran to completion successfully — only
    ' final decimal conversion failed.
    '
    ' Root cause is likely Int32 overflow in mpz_get_str's recursive
    ' divide-and-conquer (mpn_dc_get_str / mpn_dc_get_str_powtab in GMP source).
    ' Each recursion level computes positions in the output buffer using
    ' mp_size_t (int = 32 bits on Windows x64).  Once output_size > 2^31 bytes,
    ' those positions can wrap and dereference outside the buffer.
    '
    ' Fix: iteratively extract 300M-digit slabs from gmpPi via
    '   rem = pi mod 10^300M ; pi = pi // 10^300M
    ' and call mpz_get_str on each rem (output ≤ 300 MB, well within safe range).
    ' Write each slab right-to-left into a pre-allocated VirtualAlloc buffer,
    ' padding non-MSB slabs with leading zeros to 300M chars.  After all slabs
    ' extracted, memmove the contents to offset 0.
    '
    ' For 5B digits: 17 slabs.  Cost dominated by 17 × mpz_fdiv_qr at
    ' shrinking dividend sizes (5B → 4.7B → ... → 300M digits) divided by a
    ' fixed 300M-digit divisor.  Expected wall time at 5B: ~4-8 h.
    '
    ' NOTE: §270 (#90) shipped the parallel recursive-halving converter that issue #37 had only
    ' planned — see ParallelMpzGetStr.  As of §270 that parallel path is the DEFAULT at ≥ 1.5 B digits;
    ' this §216 serial slab converter is now the fallback (selected when PI_CONV_PARALLEL=0).
    ''' <summary>
    ''' Serial chunked decimal conversion (§216) — replaces GMP's mpz_get_str, which crashes with an
    ''' AccessViolation when the output exceeds ~2 GB (Int32 overflow in its divide-and-conquer). Extracts
    ''' fixed-size decimal slabs via repeated (rem = pi mod 10^k ; pi = pi \ 10^k), converts each slab
    ''' (safely small) and writes them right-to-left into a pre-allocated native buffer.
    ''' Superseded as the default by <see cref="ParallelMpzGetStr"/> (§270); used as the fallback.
    ''' </summary>
    ''' <param name="pi">The computed value to render (consumed/shrunk during extraction).</param>
    ''' <param name="totalDigitsEstimate">Estimated decimal digit count, used to size the output buffer.</param>
    Private Sub ChunkedMpzGetStr(pi As mpz_t, totalDigitsEstimate As Long)
        ' §216d: reduced chunk from 300M → 50M.  At 300M chunk, mpz_get_str crashed
        ' inside __gmpn_dc_get_str on a 15.5M-limb chunkRem producing 300M-char output.
        ' At 50M chunk: chunkRem is ~2.6M limbs, mpz_get_str internal temps drop from
        ' ~3 GB to ~200 MB.  Trade-off: 100 fdiv_qr iterations instead of 17, but each
        ' is faster (smaller 10^50M divisor).  Net throughput is similar.
        Const CHUNK_DIGITS As Long = 50_000_000L
        AppendLog($"[§216d] Chunked decimal conversion start: totalDigitsEstimate={totalDigitsEstimate:N0} chunk={CHUNK_DIGITS:N0}{vbCrLf}")

        ' Allocate output buffer: totalDigits + 16 bytes slack (room for null terminator + a safety margin)
        Dim bufSize As Long = totalDigitsEstimate + 16L
        Dim outBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(bufSize)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If outBuf = IntPtr.Zero Then
            Throw New OutOfMemoryException($"§216: VirtualAlloc {bufSize:N0} bytes for output buffer failed")
        End If
        AppendLog($"[§216] Output buffer: {bufSize:N0} bytes at 0x{outBuf.ToInt64():X16}{vbCrLf}")

        ' Compute 10^CHUNK_DIGITS — one-time cost.  Result is a CHUNK_DIGITS-digit number,
        ' ≈ 15.5M limbs ≈ 124 MB in mpz_t native form.  Cost: log2(CHUNK_DIGITS) ≈ 28 squarings,
        ' last of which is (CHUNK_DIGITS/2)-digit × (CHUNK_DIGITS/2)-digit.  Takes ~minutes.
        Dim _powStart As DateTime = DateTime.Now
        Dim D As New mpz_t()
        gmp_lib.mpz_init(D)
        gmp_lib.mpz_ui_pow_ui(D, 10UI, CULng(CHUNK_DIGITS))
        AppendLog($"[§216] 10^{CHUNK_DIGITS:N0} computed in {(DateTime.Now - _powStart).TotalMinutes:F2} min{vbCrLf}")

        ' piMutable: working copy of pi, divided down each iteration.
        ' §216a: PreAlloc to pi's size BEFORE mpz_set, otherwise GMP's MPZ_REALLOC inside
        ' mpz_set aborts with "overflow in mpz type" when needed > INT_MAX/64 = 33,554,431
        ' limbs.  At 5B digits pi is ~260M limbs.  PreAllocMpzToLimbs writes _mp_alloc
        ' directly via Marshal.WriteInt32, bypassing GMP's check entirely.
        Dim piMutable As New mpz_t()
        gmp_lib.mpz_init(piMutable)
        Dim _piSrcSize As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(pi.Pointer, 4)))
        PreAllocMpzToLimbs(piMutable, _piSrcSize + 2L)
        AppendLog($"[§216] piMutable PreAlloc'd to {(_piSrcSize + 2L):N0} limbs before mpz_set{vbCrLf}")
        gmp_lib.mpz_set(piMutable, pi)

        ' §216d: free the caller's pi (= gmpPi) buffer NOW — we've copied to piMutable
        ' and never reference pi again.  Saves ~2 GB during the chunked conversion,
        ' lowering peak commit pressure.  Reinit to a 1-limb stub so the caller's
        ' mpz_clear(gmpPi) at line 7192 is still safe.
        gmp_lib.mpz_clear(pi)
        gmp_lib.mpz_init(pi)
        AppendLog($"[§216d] caller's pi buffer freed (~2 GB) after copy to piMutable{vbCrLf}")

        ' chunkRem: pre-allocate to divisor size + a few limbs (max possible remainder size).
        ' 15.5M < 33.5M so GMP's auto-realloc would also work, but PreAlloc avoids the
        ' first-iteration small→large realloc round-trip.
        Dim chunkRem As New mpz_t()
        gmp_lib.mpz_init(chunkRem)
        Dim _dAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(D.Pointer, 0))
        PreAllocMpzToLimbs(chunkRem, _dAlloc + 2L)

        ' §216b: GMP's mpz_fdiv_qr crashes silently (stack overflow in TMP_ALLOC) when
        ' the quotient destination aliases the dividend (quot == num == piMutable).
        ' At 244M-limb quotient, GMP's internal aliasing-handler allocates ~2 GB of
        ' scratch via TMP_ALLOC_LIMBS, which on Windows's 1 MB default stack overflows
        ' before our NativeAllocFunc even gets called.  Use a separate quotient mpz_t
        ' to avoid aliasing entirely, then mpz_set the result back into piMutable for
        ' the next iteration (mpz_set's MPZ_REALLOC sees alloc >= needed, skips abort).
        Dim quotTmp As New mpz_t()
        gmp_lib.mpz_init(quotTmp)
        PreAllocMpzToLimbs(quotTmp, _piSrcSize + 2L)
        AppendLog($"[§216b] quotTmp PreAlloc'd to {(_piSrcSize + 2L):N0} limbs (de-aliased fdiv_qr){vbCrLf}")

        ' chunkEndPos: exclusive upper bound in outBuf where the next chunk will end.
        ' Starts at bufSize-1 (reserve last byte for null terminator).
        Dim chunkEndPos As Long = bufSize - 1L
        Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + chunkEndPos), CByte(0))   ' null terminator

        Dim chunkIdx As Long = 0

        ' §74 (issue #74): publish total-chunk count so the _strConvTimer UI callback can
        ' show "chunk N of M".  totalDigitsEstimate is mpz_sizeinbase(gmpPi, 10) — within
        ' ±1 of the true digit count.  Ceiling division gives the exact chunk count the
        ' loop below will produce.
        Dim totalChunks As Long = (totalDigitsEstimate + CHUNK_DIGITS - 1L) \ CHUNK_DIGITS
        _chunkConvTotal = totalChunks
        _chunkConvCurrent = 0

        Try

        While gmp_lib.mpz_sgn(piMutable) > 0
            Dim _chunkStart As DateTime = DateTime.Now
            Dim _piSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(piMutable.Pointer, 4)))
            AppendLog($"[§216c] iter {chunkIdx + 1L} start: piMutable_sz={_piSz:N0} → mpz_fdiv_qr...{vbCrLf}")
            _chunkConvCurrent = chunkIdx + 1L   ' §74: 1-based "current chunk" for the UI

            ' §216b: de-aliased call — quot=quotTmp, num=piMutable, rem=chunkRem, den=D.
            ' Then mpz_set(piMutable, quotTmp) for next iteration (no realloc, alloc already 260M).
            gmp_lib.mpz_fdiv_qr(quotTmp, chunkRem, piMutable, D)
            Dim _qSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(quotTmp.Pointer, 4)))
            Dim _rSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 4)))
            AppendLog($"[§216c] iter {chunkIdx + 1L}: fdiv_qr done, qSz={_qSz:N0} rSz={_rSz:N0} → mpz_set...{vbCrLf}")
            gmp_lib.mpz_set(piMutable, quotTmp)
            AppendLog($"[§216c] iter {chunkIdx + 1L}: mpz_set done → mpz_get_str on rem (chunkRem alloc={Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 0):N0} size={Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 4):N0})...{vbCrLf}")
            Dim _divElapsed As TimeSpan = DateTime.Now - _chunkStart

            ' §216e: log mpz_get_str's return value and strlen separately to pinpoint crash.
            Dim chunkCharPtr As char_ptr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, chunkRem)
            AppendLog($"[§216e] iter {chunkIdx + 1L}: mpz_get_str returned ptr=0x{chunkCharPtr.Pointer.ToInt64():X16}{vbCrLf}")

            Dim chunkLen As Long = CLng(strlen(chunkCharPtr.Pointer).ToUInt64())
            AppendLog($"[§216e] iter {chunkIdx + 1L}: strlen returned chunkLen={chunkLen:N0}{vbCrLf}")

            Dim isTop As Boolean = (gmp_lib.mpz_sgn(piMutable) = 0)
            Dim writeAt As Long
            Dim zeroPadCount As Long = 0

            If isTop Then
                ' MSB chunk: write actual chunkLen bytes; no leading-zero padding.
                writeAt = chunkEndPos - chunkLen
                AppendLog($"[§216e] iter {chunkIdx + 1L}: isTop=True writeAt={writeAt:N0} → CopyMemory({chunkLen:N0} bytes)...{vbCrLf}")
                CopyMemory(New IntPtr(outBuf.ToInt64() + writeAt), chunkCharPtr.Pointer, New UIntPtr(CULng(chunkLen)))
                AppendLog($"[§216e] iter {chunkIdx + 1L}: CopyMemory done{vbCrLf}")
            Else
                ' Non-top chunk: must fill exactly CHUNK_DIGITS columns, padded with leading zeros.
                writeAt = chunkEndPos - CHUNK_DIGITS
                zeroPadCount = CHUNK_DIGITS - chunkLen
                AppendLog($"[§216e] iter {chunkIdx + 1L}: isTop=False writeAt={writeAt:N0} zeroPad={zeroPadCount:N0} → write zeros...{vbCrLf}")
                Dim _padDest As Long = outBuf.ToInt64() + writeAt
                For i As Long = 0 To zeroPadCount - 1
                    Runtime.InteropServices.Marshal.WriteByte(New IntPtr(_padDest + i), CByte(48))   ' '0' = 0x30
                Next
                AppendLog($"[§216e] iter {chunkIdx + 1L}: zeros written → CopyMemory({chunkLen:N0} bytes)...{vbCrLf}")
                CopyMemory(New IntPtr(outBuf.ToInt64() + writeAt + zeroPadCount), chunkCharPtr.Pointer, New UIntPtr(CULng(chunkLen)))
                AppendLog($"[§216e] iter {chunkIdx + 1L}: CopyMemory done{vbCrLf}")
            End If

            chunkEndPos = writeAt

            ' §216f: use GmpNativeAlloc_FreeRaw, NOT _savedGmpFree.  _savedGmpFree is the
            ' ORIGINAL GMP allocator's free (CRT free) saved before GmpNativeAlloc_Install
            ' replaced the callbacks.  The chunk buffer was allocated by our REPLACEMENT
            ' NativeAllocFunc via VirtualAlloc — calling CRT free() on a VirtualAlloc'd
            ' pointer crashes the process.  GmpNativeAlloc_FreeRaw routes correctly to
            ' our NativeFreeFunc (VirtualFree for oversized blocks, pool return for ≤16 MB).
            AppendLog($"[§216f] iter {chunkIdx + 1L}: about to GmpNativeAlloc_FreeRaw chunkCharPtr...{vbCrLf}")
            GmpNativeAlloc_FreeRaw(chunkCharPtr.Pointer, chunkLen + 1L)
            AppendLog($"[§216f] iter {chunkIdx + 1L}: chunkCharPtr freed{vbCrLf}")

            chunkIdx += 1
            Dim _chunkTotal As TimeSpan = DateTime.Now - _chunkStart
            AppendLog($"[§216] chunk {chunkIdx} done: chunkLen={chunkLen:N0} isTop={isTop} pad={zeroPadCount:N0} writeAt={writeAt:N0} (div={_divElapsed.TotalMinutes:F1}m, total={_chunkTotal.TotalMinutes:F1}m){vbCrLf}")
        End While

        Finally
            ' §74 (issue #74): clear progress fields so the next mpz_get_str call (small-scale
            ' path that doesn't enter this function) shows the original "String conversion..."
            ' status instead of stale "chunk 100 of 100" text.
            _chunkConvCurrent = 0
            _chunkConvTotal = 0
        End Try

        ' Clean up GMP scratch.
        gmp_lib.mpz_clear(piMutable)
        gmp_lib.mpz_clear(chunkRem)
        gmp_lib.mpz_clear(D)
        gmp_lib.mpz_clear(quotTmp)

        ' The actual content lives in outBuf[chunkEndPos .. bufSize-1] with a null terminator at bufSize-1.
        ' Shift it back to offset 0 so downstream code sees the digits starting at outBuf[0].
        Dim actualStart As Long = chunkEndPos
        Dim actualLen As Long = (bufSize - 1L) - actualStart   ' excludes null terminator
        AppendLog($"[§216] actualStart={actualStart:N0} actualLen={actualLen:N0}{vbCrLf}")

        If actualStart > 0 Then
            Dim _moveStart As DateTime = DateTime.Now
            ' RtlMoveMemory handles overlap correctly.
            CopyMemory(outBuf, New IntPtr(outBuf.ToInt64() + actualStart), New UIntPtr(CULng(actualLen)))
            ' Re-place null terminator at the new end of content.
            Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + actualLen), CByte(0))
            AppendLog($"[§216] memmove of {actualLen:N0} bytes done in {(DateTime.Now - _moveStart).TotalSeconds:F2}s{vbCrLf}")
        End If

        ' Populate display state so downstream code (WritePiDigitsToFile, autoVerify) works unchanged.
        _displayNativePtr = outBuf
        _displayNativeLen = actualLen + 1L   ' includes null terminator (mirrors mpz_get_str path)
        _displayNativeBufSize = bufSize

        AppendLog($"[§216] Chunked decimal conversion complete: {actualLen:N0} digits in {chunkIdx} chunks{vbCrLf}", 1)   ' §252 (#95): final digit-count result → level 1
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §226: Parallel recursive-halving decimal conversion (issue #37)
    ' ════════════════════════════════════════════════════════════════════════
    ' Replaces the strictly-sequential §216 chunked converter (and GMP's serial
    ' internal mpz_get_str) with a parallel binary-tree halving:
    '
    '   ParallelBase10(n, digits, outBuf, offset):
    '     if digits <= LEAF: outBuf[offset..] = mpz_get_str(n), left-padded with '0'
    '     else:
    '       D = 10^(digits/2)         ' from pre-built power table
    '       (hi, lo) = mpz_fdiv_qr(n, D)
    '       Parallel.Invoke(
    '         HalveBase10(hi, hiDigits, outBuf, offset),
    '         HalveBase10(lo, halfDigits, outBuf, offset + hiDigits))
    '
    ' Key design points:
    '   * Power-of-10 table pre-built sequentially before any recursion fires.
    '     Sizes determined by walking the recursion tree (≤ log2(digits/LEAF)
    '     distinct powers).
    '   * Critical path = one fdiv_qr per level, sizes halving each level.
    '     Wall ≈ 2 × top-level-fdiv_qr (geometric series).
    '   * Each non-leaf node allocates its own hi/lo mpz_t — no shared mutable
    '     state during parallel recursion.  Power table is read-only.
    '   * PreAllocMpzToLimbs on hi/lo bypasses GMP's 33.5M-limb realloc abort
    '     for large operands (same hazard as §216a / §218 / §225).
    '   * outBuf is written left-to-right; each leaf knows its absolute offset.
    '     No final memmove needed (unlike §216's right-to-left strategy).
    '   * Verification: byte-identical output to GMP's mpz_get_str / §216
    '     chunked path.  Validated at 1B against the 2026-05-22 post-§225
    '     verified pi_digits.txt.
    ''' <summary>
    ''' Parallel recursive-halving decimal converter (§226/§270, #90) — the DEFAULT decimal conversion
    ''' at ≥ 1.5 B digits (opt out with PI_CONV_PARALLEL=0 to use the §216 serial path). Splits the value
    ''' against a pre-built power-of-10 table and recurses in parallel, writing each leaf's digits at its
    ''' absolute output offset. 5 B-safe via the §270 safe-peel split rule; byte-identical to mpz_get_str.
    ''' </summary>
    ''' <param name="pi">The computed value to render.</param>
    ''' <param name="totalDigitsEstimate">Estimated decimal digit count, used to size the output buffer and power table.</param>
    Private Sub ParallelMpzGetStr(pi As mpz_t, totalDigitsEstimate As Long)
        Const LEAF_THRESHOLD As Long = 50_000_000L

        Dim _t0 As DateTime = DateTime.Now
        AppendLog($"[§226] Parallel decimal conversion start: totalDigitsEstimate={totalDigitsEstimate:N0} leaf={LEAF_THRESHOLD:N0}{vbCrLf}")

        ' Allocate output buffer (totalDigits + 16 slack; null terminator at end).
        Dim bufSize As Long = totalDigitsEstimate + 16L
        Dim outBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(bufSize)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If outBuf = IntPtr.Zero Then
            Throw New OutOfMemoryException($"§226: VirtualAlloc {bufSize:N0} bytes failed")
        End If
        AppendLog($"[§226] Output buffer: {bufSize:N0} bytes at 0x{outBuf.ToInt64():X16}{vbCrLf}")

        ' Actual digit count (mpz_sizeinbase over-estimates by 1 at most).
        Dim actualDigits As Long = CLng(gmp_lib.mpz_sizeinbase(pi, 10UI))

        ' Walk the recursion tree to collect the unique D-sizes (10^halfDigits at each
        ' non-leaf node).  Worklist holds digit counts still to explore.
        Dim neededD As New System.Collections.Generic.HashSet(Of Long)()
        Dim seen As New System.Collections.Generic.HashSet(Of Long)()
        Dim work As New System.Collections.Generic.Queue(Of Long)()
        seen.Add(actualDigits)
        work.Enqueue(actualDigits)
        While work.Count > 0
            Dim d As Long = work.Dequeue()
            If d <= LEAF_THRESHOLD Then Continue While
            Dim halfD As Long = ConvSplitLowDigits(d)
            Dim hiD As Long = d - halfD
            neededD.Add(halfD)
            If seen.Add(halfD) Then work.Enqueue(halfD)
            If seen.Add(hiD) Then work.Enqueue(hiD)
        End While
        Dim _ordered As List(Of Long) = neededD.OrderBy(Function(x) x).ToList()
        AppendLog($"[§226] Powers-of-10 needed: {_ordered.Count} sizes ({String.Join(", ", _ordered)}){vbCrLf}")

        ' Compute each 10^k sequentially (mpz_ui_pow_ui's internal squaring already
        ' competes with itself for FFT scratch).  Cost dominated by the largest power.
        Dim powTable As New System.Collections.Generic.Dictionary(Of Long, IntPtr)()
        For Each k As Long In _ordered
            Dim _kt As DateTime = DateTime.Now
            Dim m As New mpz_t()
            gmp_lib.mpz_init(m)
            gmp_lib.mpz_ui_pow_ui(m, 10UI, CUInt(k))
            powTable(k) = m.Pointer
            AppendLog($"[§226] 10^{k:N0} computed in {(DateTime.Now - _kt).TotalSeconds:F1}s{vbCrLf}")
        Next
        Dim _powElapsed As Double = (DateTime.Now - _t0).TotalSeconds
        AppendLog($"[§226] Power table done in {_powElapsed:F1}s total{vbCrLf}")

        ' Working copy of pi so we can free the caller's buffer pre-conversion.
        ' §216a/d pattern: PreAlloc + mpz_set, then free pi + reinit to stub.
        Dim piCopy As New mpz_t()
        gmp_lib.mpz_init(piCopy)
        Dim _piSrcSize As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(pi.Pointer, 4)))
        PreAllocMpzToLimbs(piCopy, _piSrcSize + 2L)
        gmp_lib.mpz_set(piCopy, pi)
        gmp_lib.mpz_clear(pi)
        gmp_lib.mpz_init(pi)
        AppendLog($"[§226] piCopy made ({(_piSrcSize * 8L / 1048576L):N0} MB); caller pi freed{vbCrLf}")

        ' Recursive halving — writes exactly actualDigits chars at outBuf[0..actualDigits].
        Dim _convStart As DateTime = DateTime.Now
        HalveBase10(piCopy, actualDigits, outBuf, 0L, powTable, LEAF_THRESHOLD)
        Dim _convElapsed As Double = (DateTime.Now - _convStart).TotalSeconds
        AppendLog($"[§226] Recursive halving done in {_convElapsed:F1}s{vbCrLf}")

        ' Null terminator at end of digits.
        Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + actualDigits), CByte(0))

        ' Clean up power table + piCopy.
        For Each kv As KeyValuePair(Of Long, IntPtr) In powTable
            Dim mTmp As New mpz_t()
            mTmp.Pointer = kv.Value
            gmp_lib.mpz_clear(mTmp)
        Next
        gmp_lib.mpz_clear(piCopy)

        ' Display state for downstream code (WritePiDigitsToFile, autoVerify).
        _displayNativePtr = outBuf
        _displayNativeLen = actualDigits + 1L
        _displayNativeBufSize = bufSize

        Dim _totalElapsed As Double = (DateTime.Now - _t0).TotalSeconds
        AppendLog($"[§226] Parallel decimal conversion complete: {actualDigits:N0} digits in {_totalElapsed:F2}s (powTable={_powElapsed:F1}s + convert={_convElapsed:F1}s){vbCrLf}", 1)   ' §252 (#95): final digit-count result → level 1
    End Sub

    ' §270 (#90): 5B-safe split point for the §226 converter.  A half-split (digits/2) needs a
    ' 10^(digits/2) divisor; at >1B digits that exceeds GMP's ~33.5M-limb FFT ceiling and the divisor
    ' build / fdiv_qr crashes or falls off a cliff (#89).  So when digits is large, peel a FIXED
    ' CONV_SAFE_PEEL-digit chunk from the low end instead (divisor 10^CONV_SAFE_PEEL ≈ 26M limbs,
    ' safe) — sequential at the top, but each peeled ≤CONV_SAFE_PEEL slab still halves IN PARALLEL via
    ' HalveBase10's Parallel.Invoke (the hi-recursion peels the next chunk while the lo-slab converts),
    ' so parallelism is preserved while every divisor stays FFT-safe.  The split point does not change
    ' the result, so the output is byte-identical to a pure half-split (and to §216).
    Private Const CONV_SAFE_PEEL As Long = 500_000_000L   ' 10^500M ≈ 26M limbs < 33.5M FFT cap
    Private Shared Function ConvSplitLowDigits(digits As Long) As Long
        If digits > 2L * CONV_SAFE_PEEL Then Return CONV_SAFE_PEEL   ' peel a safe chunk; hi peels again
        Return digits \ 2                                           ' small enough ⇒ halve in parallel
    End Function

    ' §226 helper: recursive halving.  Writes exactly `digits` chars to outBuf[offset..],
    ' zero-padding if n's actual decimal length is < digits (low-half slots of parents).
    ' Thread-safe re-entrant: each call has its own hi/lo; powTable is read-only.
    Private Shared Sub HalveBase10(n As mpz_t, digits As Long, outBuf As IntPtr, offset As Long, powTable As System.Collections.Generic.Dictionary(Of Long, IntPtr), leafThreshold As Long)
        If digits <= leafThreshold Then
            ' Leaf: serial mpz_get_str + write with leading-zero padding.
            Dim charPtr As char_ptr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, n)
            Dim chunkLen As Long = CLng(strlen(charPtr.Pointer).ToUInt64())
            Dim zeroPad As Long = digits - chunkLen
            If zeroPad < 0L Then
                Throw New InvalidOperationException($"§226 leaf: chunkLen={chunkLen} > digits={digits} at offset={offset:N0} — power-of-10 split inconsistency")
            End If
            If zeroPad > 0L Then
                Dim _padDest As Long = outBuf.ToInt64() + offset
                For i As Long = 0L To zeroPad - 1L
                    Runtime.InteropServices.Marshal.WriteByte(New IntPtr(_padDest + i), CByte(48))   ' '0' = 0x30
                Next
            End If
            CopyMemory(New IntPtr(outBuf.ToInt64() + offset + zeroPad), charPtr.Pointer, New UIntPtr(CULng(chunkLen)))
            GmpNativeAlloc_FreeRaw(charPtr.Pointer, chunkLen + 1L)
            Return
        End If

        ' Non-leaf: split via D = 10^halfDigits.  hi gets the top `hiDigits` chars (offset),
        ' lo gets the bottom `halfDigits` chars (offset + hiDigits).
        Dim halfDigits As Long = ConvSplitLowDigits(digits)
        Dim hiDigits As Long = digits - halfDigits
        Dim dPtr As IntPtr = powTable(halfDigits)

        Dim hi As New mpz_t()
        Dim lo As New mpz_t()
        gmp_lib.mpz_init(hi)
        gmp_lib.mpz_init(lo)

        ' PreAlloc to bypass GMP's 33.5M-limb realloc abort (§216a/§218 hazard).
        Dim _nSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(n.Pointer, 4)))
        PreAllocMpzToLimbs(hi, _nSz + 2L)
        Dim _dSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(dPtr, 4)))
        PreAllocMpzToLimbs(lo, _dSz + 2L)

        Dim _dWrap As New mpz_t()
        _dWrap.Pointer = dPtr
        gmp_lib.mpz_fdiv_qr(hi, lo, n, _dWrap)

        ' Parallel.Invoke is synchronous — both sub-calls finish before we hit mpz_clear.
        System.Threading.Tasks.Parallel.Invoke(
            Sub() HalveBase10(hi, hiDigits, outBuf, offset, powTable, leafThreshold),
            Sub() HalveBase10(lo, halfDigits, outBuf, offset + hiDigits, powTable, leafThreshold))

        gmp_lib.mpz_clear(hi)
        gmp_lib.mpz_clear(lo)
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Main computation entry point
    ' ════════════════════════════════════════════════════════════════════════

    Private Function ComputePiGMP(digits As Long, token As CancellationToken) As String

        ' §224 (issue #41): trigger CpuTopology detection + logging on first run.
        ' Idempotent: subsequent ComputePiGMP calls reuse cached topology.
        EnsureCpuTopologyInitialized()

        Dim gmpSqrtInput As New mpz_t()
        Dim gmpSqrt As New mpz_t()
        Dim gmpNumer As New mpz_t()
        Dim gmpPi As New mpz_t()
        Dim gmpOne As New mpz_t()
        Dim gmpVariablesInitialized As Boolean = False

        Try
            Dim numTerms As Long = CLng(System.Math.Ceiling(digits / 14.18)) + 10

            phaseStopWatch.Restart()
            LogPhase($"Starting: {digits:N0} digits, {numTerms:N0} terms")

            Dim memBefore As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            LogPhase($"Memory before computation: {memBefore:N0}MB")

            If token.IsCancellationRequested Then Return ""

            ' §103: Declare finalP/Q/T here so both the snap_Phase3-load path and the
            ' Phase 1/2 path can populate them before Phase 3 begins.
            Dim finalP As mpz_t = Nothing
            Dim finalQ As mpz_t = Nothing
            Dim finalT As mpz_t = Nothing
            Dim p3SnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")

            ' §103: If --auto-checkpoint is active and snap_Phase3 exists, load P/Q/T
            ' and jump straight to Phase 3, skipping the 10+ hour Phase 1/2 run.
            ' §214 (2026-05-15, issue #67): probe gmpNumer.bin first.  If it exists and
            ' snap_Phase3 meta.txt's digits matches the current run, the gmpNumer-resume
            ' path at line ~6250 will fire — which jumps to NumeratorDone without ever
            ' touching finalP or finalQ.  Loading them is dead weight (P ~3.6 GB + Q ~5.6 GB
            ' at 5B).  In that case, load T only via the new T-only helper.  Saves ~9.3 GB
            ' of working set during startup, easing depth-0 §gen RAM pressure later.
            If _autoCheckpoint Then
                Dim _p3P As New mpz_t(), _p3Q As New mpz_t(), _p3T As New mpz_t()
                gmp_lib.mpz_inits(_p3P, _p3Q, _p3T, Nothing)

                Dim _gmpNumerBin As String = System.IO.Path.Combine(p3SnapDir, "gmpNumer.bin")
                Dim _metaPath As String = System.IO.Path.Combine(p3SnapDir, "meta.txt")
                Dim _gmpNumerExists As Boolean = System.IO.File.Exists(_gmpNumerBin)
                Dim _metaDigitsMatch As Boolean = False
                If _gmpNumerExists AndAlso System.IO.File.Exists(_metaPath) Then
                    Try
                        For Each _ml As String In System.IO.File.ReadAllLines(_metaPath)
                            Dim _eq As Integer = _ml.IndexOf("="c)
                            If _eq > 0 AndAlso _ml.Substring(0, _eq) = "digits" Then
                                Dim _md As Long = 0L
                                If Long.TryParse(_ml.Substring(_eq + 1), _md) AndAlso _md = digits Then
                                    _metaDigitsMatch = True
                                End If
                                Exit For
                            End If
                        Next
                    Catch
                        _metaDigitsMatch = False
                    End Try
                End If

                Dim _loadOK As Boolean
                If _gmpNumerExists AndAlso _metaDigitsMatch Then
                    _loadOK = TryLoadPhase3SnapshotTOnly(p3SnapDir, digits, _p3T)
                    If _loadOK Then
                        _p3TOnlyLoadActive = True
                    Else
                        ' T-only load failed unexpectedly after probe passed — try full load
                        ' as a safety net so we don't quietly drop into the no-P-no-Q path.
                        WriteToLog("[ComputePi§214] T-only load failed after probe passed — retrying full P/Q/T load")
                        _loadOK = TryLoadPhase3Snapshot(p3SnapDir, digits, _p3P, _p3Q, _p3T)
                    End If
                Else
                    _loadOK = TryLoadPhase3Snapshot(p3SnapDir, digits, _p3P, _p3Q, _p3T)
                End If

                If _loadOK Then
                    finalP = _p3P : finalQ = _p3Q : finalT = _p3T
                    GoTo Phase3Start
                End If
                gmp_lib.mpz_clears(_p3P, _p3Q, _p3T, Nothing)
            End If

            ' Issue #6: BinarySplitGMP now returns List(Of Result) — no Tuple allocations.
            Dim nodes As List(Of Result) = Nothing
            BinarySplitGMP(numTerms, nodes)

            LogPhase($"Binary Splitting complete ({nodes.Count} nodes)")

            Dim memAfterSplit As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            LogPhase($"Memory after split: {memAfterSplit:N0}MB")

            If _logLevel >= 2 Then
                ' Log sizes of the top-level node(s) to detect unexpectedly large intermediates
                Try
                    For nodeIdx As Integer = 0 To nodes.Count - 1
                        Dim nd As Result = nodes(nodeIdx)
                        Dim pDigits As Long = CLng(gmp_lib.mpz_sizeinbase(nd.P, 10))
                        Dim qDigits As Long = CLng(gmp_lib.mpz_sizeinbase(nd.Q, 10))
                        Dim tDigits As Long = CLng(gmp_lib.mpz_sizeinbase(nd.T, 10))
                        WriteToLog($"[Node {nodeIdx}] P~{pDigits:N0} digits  Q~{qDigits:N0} digits  T~{tDigits:N0} digits")
                    Next
                Catch
                End Try
            End If

            If token.IsCancellationRequested Then Return ""

            ' ── Final in-memory combine (usually already 1 node from BinarySplitGMP) ─
            ' Issue #6: uses Result struct instead of Tuple(Of mpz_t,mpz_t,mpz_t).
            LogPhase($"Starting final combine of {nodes.Count} nodes...")
            Dim combineIteration As Integer = 0

            While nodes.Count > 1
                combineIteration += 1
                Dim memDuringCombine As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
                LogPhase($"Final combine iteration {combineIteration}: {nodes.Count} nodes → {(nodes.Count + 1) \ 2} nodes (RAM: {memDuringCombine:N0}MB)")

                Dim nextNodes As New List(Of Result)()
                Dim i As Integer = 0
                While i < nodes.Count - 1
                    If combineIteration <= 2 Then
                        LogPhase($"  Combining nodes {i} and {i + 1}...")
                    End If

                    Dim left As Result = nodes(i)
                    Dim right As Result = nodes(i + 1)

                    Dim newP As New mpz_t()
                    Dim newQ As New mpz_t()
                    Dim tA As New mpz_t()
                    Dim tB As New mpz_t()
                    gmp_lib.mpz_inits(newP, newQ, tA, tB, Nothing)

                    Try
                        If combineIteration <= 2 Then
                            Dim leftPSize As Long = CLng(gmp_lib.mpz_sizeinbase(left.P, 10))
                            Dim rightPSize As Long = CLng(gmp_lib.mpz_sizeinbase(right.P, 10))
                            LogPhase($"  P sizes: {leftPSize:N0} × {rightPSize:N0} digits")
                        End If
                    Catch
                    End Try

                    ' Same early-free + in-place-add pattern as the BinarySplitGMP combine loop.
                    ' §108: GmpRaw_* bypasses wrapper dispatch
                    GmpRaw_mul(newP.Pointer, left.P.Pointer, right.P.Pointer)
                    gmp_lib.mpz_clears(right.P, Nothing)

                    GmpRaw_mul(newQ.Pointer, left.Q.Pointer, right.Q.Pointer)
                    gmp_lib.mpz_clears(left.Q, Nothing)

                    GmpRaw_mul(tA.Pointer, left.T.Pointer, right.Q.Pointer)
                    gmp_lib.mpz_clears(left.T, right.Q, Nothing)

                    GmpRaw_mul(tB.Pointer, left.P.Pointer, right.T.Pointer)
                    gmp_lib.mpz_clears(left.P, right.T, Nothing)

                    GmpRaw_add(tA.Pointer, tA.Pointer, tB.Pointer)  ' in-place: T result in tA's buffer
                    gmp_lib.mpz_clears(tB, Nothing) ' tA IS newT

                    nextNodes.Add(New Result With {.P = newP, .Q = newQ, .T = tA})
                    i += 2
                End While

                If nodes.Count Mod 2 = 1 Then
                    nextNodes.Add(nodes(nodes.Count - 1))
                End If

                nodes = nextNodes
            End While

            LogPhase("Final combine complete - 1 node remaining")

            finalP = nodes(0).P
            finalQ = nodes(0).Q
            finalT = nodes(0).T

            ' §103: Save snap_Phase3 now — before Phase 3 begins — so a Phase 3 crash
            ' can resume from here without re-running the 10+ hour Phase 1/2.
            SavePhase3Snapshot(p3SnapDir, digits, numTerms, finalP, finalQ, finalT)

Phase3Start:
            System.Threading.Volatile.Write(_safeMulDop, -1)   ' §107 Gap 7: reset DOP so Phase 3 uses all cores (may be 3 if Phase 2 serial path ran)
            gmp_lib.mpz_init(gmpSqrtInput)
            Dim _gmpSqrtInputRaw As IntPtr = gmpSqrtInput.Pointer  ' §78: capture before mpz_inits(gmpSqrt,gmpOne) fires §78 and corrupts it
            gmp_lib.mpz_inits(gmpSqrt, gmpOne, Nothing)
            gmpVariablesInitialized = True

            ' §NumeratorDiv-v4: Init gmpNumer and gmpPi as the LAST two mpz_init calls, in order,
            ' and capture each Pointer IMMEDIATELY after its own mpz_init — before the next call
            ' fires §78 and overwrites it.
            '
            ' §78 side-effect: every managed GMP call overwrites ALL registered mpz_t.Pointer
            ' fields with stale addresses.  mpz_inits(A,B,C) fires §78 during mpz_init(B), which
            ' corrupts A.Pointer; then fires §78 during mpz_init(C), which corrupts A and B.Pointer.
            ' Capturing after mpz_inits returns gives wrong values for all but the last arg.
            '
            ' Fix: init gmpNumer first (capture before gmpPi's mpz_init corrupts it), then init
            ' gmpPi (capture before any subsequent managed call corrupts it).  Restore gmpNumer.Pointer
            ' immediately so TryLoadPhase3Value's DeserializeOneMpz writes to the correct struct.
            ' Native struct addresses never change (only _mp_d changes on realloc), so these
            ' captures remain valid as restore values through all subsequent managed GMP calls.
            gmp_lib.mpz_init(gmpNumer)
            Dim _gmpNumerRaw As IntPtr = gmpNumer.Pointer  ' correct: captured before mpz_init(gmpPi) fires §78
            gmp_lib.mpz_init(gmpPi)
            Dim _gmpPiRaw As IntPtr = gmpPi.Pointer        ' correct: no managed GMP call between here and mpz_init(gmpPi)
            gmpNumer.Pointer = _gmpNumerRaw                 ' restore: mpz_init(gmpPi) just fired §78 and corrupted gmpNumer.Pointer
            gmpSqrtInput.Pointer = _gmpSqrtInputRaw         ' restore: mpz_inits(gmpSqrt,gmpOne)+mpz_init(gmpNumer/gmpPi) all fired §78
            Dim _finalTRaw As IntPtr = IntPtr.Zero          ' set below after mpz_init(finalT) in checkpoint path

            ' §106 checkpoint: if gmpNumer was already computed and saved, skip Steps 1–5
            ' (SafeMpzPow10, SafeMpzMul squaring, sqrt, and all three R*Q multiplies).
            Dim _gmpNumerResumeOK As Boolean = TryLoadPhase3Value("gmpNumer", gmpNumer, p3SnapDir)
            ' §214-assert (2026-05-15, issue #67): if the T-only Phase3 snapshot load fired,
            ' finalP and finalQ are mpz_init'd to 0 — only the gmpNumer-resume path is safe
            ' from here.  If TryLoadPhase3Value unexpectedly fails (e.g., corrupt gmpNumer.bin),
            ' abort with a clear message rather than fall through to Step 1+ which needs P+Q.
            If _p3TOnlyLoadActive AndAlso Not _gmpNumerResumeOK Then
                Throw New Exception("[ComputePi§214-assert] gmpNumer.bin failed to load after T-only " &
                                    "Phase3 snapshot was used (P+Q skipped).  Recovery: delete gmpNumer.bin " &
                                    "to force a fresh full P/Q/T load on next launch.")
            End If
            If _gmpNumerResumeOK Then
                LogPhase("[ComputePi] gmpNumer loaded from checkpoint — skipping Steps 1–5")
                ' finalT is still needed for the divide — reload from spill or checkpoint.
                gmp_lib.mpz_clear(finalT)   ' finalT was mpz_inits'd above as 0
                gmp_lib.mpz_init(finalT)
                _finalTRaw = finalT.Pointer  ' §NumeratorDiv: capture after fresh init, before TryLoadPhase3Value fires §78
                If Not TryLoadPhase3Value("finalT", finalT, p3SnapDir) Then
                    ' finalT not checkpointed yet — must reload from snap_Phase3 P/Q/T files.
                    ' (This path only occurs if gmpNumer was saved but finalT spill was lost.)
                    LogPhase("[ComputePi] finalT not in checkpoint — reloading from snap_Phase3")
                    Dim _ftStaging(4194303) As Byte
                    Using fs As New FileStream(System.IO.Path.Combine(p3SnapDir, "T.bin"),
                                               FileMode.Open, FileAccess.Read)
                        Using br As New BinaryReader(fs)
                            DeserializeOneMpz(finalT, br, _ftStaging)
                        End Using
                    End Using
                End If
                GoTo NumeratorDone
            End If

            ' §SqrtInput-ckpt: if gmpSqrtInput was saved after Step 3, skip Steps 1–3
            gmpSqrtInput.Pointer = _gmpSqrtInputRaw  ' §78: restore before TryLoadPhase3Value uses val.Pointer
            If TryLoadPhase3Value("gmpSqrtInput", gmpSqrtInput, p3SnapDir) Then
                LogPhase("[ComputePi] gmpSqrtInput loaded from checkpoint — skipping Steps 1–3")
                GoTo BeforeStep4
            End If

            ' §99: Use SafeMpzPow10 (repeated squaring via SafeMpzMul) for all digit counts.
            ' mpz_ui_pow_ui uses GMP's internal repeated squaring which hits the 32-bit
            ' mpn_mul_fft overflow once intermediates exceed ~33M limbs (~2GB).  At 5B digits
            ' 10^2,500,000,000 ≈ 130M limbs — well above that threshold.  SafeMpzPow10 routes
            ' every squaring through SafeMpzMul which splits around the 33M-limb boundary.
            ' §244 (issue #85): parallelize Step 1/2.  The §Step1OOM/§Phase3OOM force-serial
            ' (_safeMulDop=1) is replaced by the §243 MemoryBudget floor inside SafeMpzMul,
            ' which picks a RAM-safe DOP per squaring — turning the ~3 h single-core Step 1
            ' into multi-core.  Enabler: free the DEAD finalP (~3.6 GB at 5B; never used in the
            ' numerator — it was already freed later at the §"mpz_clears gmpSqrt, finalP" site)
            ' up front, so the floor has headroom for full DOP.  5B projection: persist ~14 GB
            ' + result/shifted/subs ~21 GB ≈ 35 GB < ~45 GB physical budget → floor admits DOP=9.
            ' finalQ/finalT stay resident (spilling them adds numerator-corruption risk for no
            ' extra DOP — the floor already allows 9).
            If finalP.Pointer <> IntPtr.Zero Then
                Dim _fpSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(finalP.Pointer, 4)))
                If _fpSz > 1 Then
                    gmp_lib.mpz_clear(finalP)
                    gmp_lib.mpz_init(finalP)   ' 0-stub: later mpz_clears(gmpSqrt, finalP) stays safe
                    LogPhase($"[ComputePi§244] freed finalP early (~{_fpSz * 8L \ 1048576L:N0} MB) — dead from Step 1 on; headroom for parallel Step 1/2")
                End If
            End If
            TrimPoolAtBoundary("pre-step1", 1048576UL)   ' §69/§243: max headroom before the floor decides DOP

            ' §83 Option A: 10^digits is a deterministic constant for this digit count.
            ' Checkpoint it after Step 1 and skip Step 1 on resume — closes the snap_Phase3 →
            ' gmpSqrtInput replay gap (a real ~3 h replay on the 2026-05-27 outage).
            Dim _pow10Meta As String = System.IO.Path.Combine(p3SnapDir, "pow10_meta.txt")
            Dim _pow10Resumed As Boolean = False
            If _autoCheckpoint AndAlso System.IO.File.Exists(System.IO.Path.Combine(p3SnapDir, "pow10.bin")) AndAlso System.IO.File.Exists(_pow10Meta) Then
                Dim _pm As Long = 0L
                If Long.TryParse(System.IO.File.ReadAllText(_pow10Meta).Trim(), _pm) AndAlso _pm = digits AndAlso TryLoadPhase3Value("pow10", gmpOne, p3SnapDir) Then
                    _pow10Resumed = True
                    LogPhase($"[ComputePi§83] pow10.bin resumed (digits={digits:N0}) — skipping Step 1")
                End If
            End If

            If Not _pow10Resumed Then
                LogPhase($"[ComputePi] Step 1: SafeMpzPow10(10^{digits:N0}) [§244 adaptive DOP]")
                SafeMpzPow10(gmpOne, digits)   ' §244: §243 floor manages DOP per squaring (was forced serial)
                LogPhase($"[ComputePi] Step 1 done: gmpOne={CLng(gmp_lib.mpz_sizeinbase(gmpOne, 10)):N0} digits")
                If _autoCheckpoint Then
                    SavePhase3Value("pow10", gmpOne, p3SnapDir)   ' §83 Option A
                    Try
                        System.IO.File.WriteAllText(_pow10Meta, digits.ToString())
                    Catch
                    End Try
                End If
            End If
            LogPhase($"[ComputePi] Step 2: SafeMpzMul gmpSqrtInput = gmpOne^2 [§244 adaptive DOP]")
            SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)   ' §244: §243 floor manages DOP (was forced serial)
            LogPhase($"[ComputePi] Step 2 done: gmpSqrtInput={CLng(gmp_lib.mpz_sizeinbase(gmpSqrtInput, 10)):N0} digits")
            ' gmpOne is no longer needed — free its ~208 MB buffer now so it is
            ' not held alive through the sqrt, numerator multiply, and division.
            ' Re-init to 0 so the Finally block can safely call mpz_clear on it.
            gmp_lib.mpz_clear(gmpOne)
            gmp_lib.mpz_init(gmpOne)
            LogPhase($"[ComputePi] Step 3: mpz_mul_ui gmpSqrtInput *= 10005")
            gmp_lib.mpz_mul_ui(gmpSqrtInput, gmpSqrtInput, 10005UI)
            gmpSqrtInput.Pointer = _gmpSqrtInputRaw  ' §78: restore before SavePhase3Value uses val.Pointer
            SavePhase3Value("gmpSqrtInput", gmpSqrtInput, p3SnapDir)
BeforeStep4:
            ' §208a: gmpSqrt checkpoint — if a prior run completed SafeMpzSqrt but failed
            ' before saving gmpNumer, gmpSqrt.bin lets us skip the entire 8+ hour sqrt
            ' on this relaunch.  See companion save below right after SafeMpzSqrt returns.
            If TryLoadPhase3Value("gmpSqrt", gmpSqrt, p3SnapDir) Then
                LogPhase("[ComputePi§208a] gmpSqrt loaded from checkpoint — skipping SafeMpzSqrt")
                gmp_lib.mpz_clear(gmpSqrtInput)
            Else
                LogPhase($"[ComputePi] Step 4: SafeMpzSqrt of {CLng(gmp_lib.mpz_sizeinbase(gmpSqrtInput, 10)):N0}-digit number")
                SafeMpzSqrt(gmpSqrt, gmpSqrtInput)
                gmp_lib.mpz_clear(gmpSqrtInput)
                ' §208a: save gmpSqrt immediately after sqrt completes so any subsequent
                ' crash before the gmpNumer save can resume from here without redoing sqrt.
                SavePhase3Value("gmpSqrt", gmpSqrt, p3SnapDir)
                LogPhase("[ComputePi§208a] gmpSqrt saved")
            End If
            LogPhase("Square root complete")

            If token.IsCancellationRequested Then Return ""

            If _logLevel >= 2 Then WriteToLog($"[ComputePi] mpz_mul_ui: gmpNumer = gmpSqrt * 426880")
            ' §208: pre-alloc gmpNumer to avoid silent ~2 GB realloc inside __gmpz_mul_ui.
            ' Same root cause as §205/§206: gmpNumer was mpz_init'd to alloc=1, mpz_mul_ui
            ' needs to grow it to gmpSqrt.size+1 (~259M+1 limbs / 2 GB) and the silent
            ' realloc kills the process at 5B-scale.  7th relaunch (2026-05-05 06:51 PT)
            ' died here right after logging "mpz_mul_ui: gmpNumer = gmpSqrt * 426880".
            Dim _szGmpSqrt208 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpSqrt.Pointer, 4))
            WriteToLog($"[ComputePi§208] pre-alloc gmpNumer to {_szGmpSqrt208 + 2:N0} limbs (gmpSqrt.size={_szGmpSqrt208:N0})")
            PreAllocMpzToLimbs(gmpNumer, CLng(_szGmpSqrt208) + 2L)
            WriteToLog($"[ComputePi§208] gmpNumer pre-alloc done; calling mpz_mul_ui")
            gmp_lib.mpz_mul_ui(gmpNumer, gmpSqrt, 426880UI)
            WriteToLog($"[ComputePi§208] mpz_mul_ui done; gmpNumer.size={Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4):N0}")
            ' Restore gmpNumer.Pointer first — §78 corrupted it during mpz_mul_ui above.
            gmpNumer.Pointer = _gmpNumerRaw
            ' §209a: REMOVED the SavePhase3Value("gmpNumer") here — the gmpNumer at this
            ' point is the INTERMEDIATE value (= 426880*sqrt), not the FINAL post-multiply
            ' numerator.  The resume logic at line ~6205 loads "gmpNumer.bin" and goes
            ' straight to the final divide via NumeratorDone, bypassing the 3-piece split
            ' and r0/r1/r2 multiplies.  If gmpNumer.bin contained the intermediate, the
            ' divide would compute pi = 426880*sqrt / T (wrong).  The §208a gmpSqrt.bin
            ' checkpoint protects the same code path — on crash before the post-combine
            ' SavePhase3Value("gmpNumer") at line ~6821, resume falls through to §208a
            ' gmpSqrt-ckpt and re-runs only the mul_ui + split + multiplies + combine.
            ' gmpSqrt value is now encoded in gmpNumer — free its ~198 MB before
            ' the large multiply.  finalP is also not used in the final formula
            ' (pi = 426880·sqrt(10005)·Q / T), so free its ~340 MB too.
            ' Combined saving: ~538 MB off the baseline before gmpNumer *= finalQ.
            gmp_lib.mpz_clears(gmpSqrt, finalP, Nothing)

            ' Spill finalT (~548 MB) to disk before the large gmpNumer *= finalQ
            ' multiply.  Without this, the baseline is ~1,318 MB and GMP's FFT
            ' multiply pushes the peak to ~2,310 MB (crashes).  By spilling we
            ' drop the baseline to ~770 MB so the multiply peaks at ~1,762 MB.
            ' finalT is reloaded immediately after finalQ is freed.
            LogPhase($"[ComputePi] Step 5: spilling finalT to disk")
            Dim finalT_spillPath As String = $"{DISK_CACHE_DIR}finalT_spill.bin"
            Dim stagingT(65535) As Byte
            Using fs As New FileStream(finalT_spillPath, FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(finalT, bw, stagingT)
                End Using
            End Using
            ' §106 checkpoint: also save finalT to snap_Phase3 so the gmpNumer shortcut can reload it.
            SavePhase3Value("finalT", finalT, p3SnapDir)
            gmp_lib.mpz_clear(finalT)   ' free ~548 MB; will be reloaded below
            LogPhase($"[ComputePi] Step 5 done: finalT spilled, finalQ={CLng(gmp_lib.mpz_sizeinbase(finalQ, 10)):N0} digits")
            ' gmpNumer *= finalQ in a single call peaks at ~2.3 GB — too large.
            ' Split finalQ into three equal thirds (by bit position) and do three
            ' smaller multiplies (~1.24 GB peak each), spilling between passes.
            '
            ' finalQ = Q2*2^(2k) + Q1*2^k + Q0   where k = bitlen(Q)/3
            ' result  = r2*2^(2k) + r1*2^k + r0   where r_i = gmpNumer * Q_i
            '
            ' Passes 0–2 compute r0, r1 then r2 (in-place).
            ' Combine:  gmpNumer = ((r2 << k) + r1) << k + r0

            Dim totalBits As Long = CLng(gmp_lib.mpz_sizeinbase(finalQ, 2))
            WriteToLog($"[3PM-DBG] totalBits={totalBits:N0} finalQ._mp_size={Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 4):N0} _mp_alloc={Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 0):N0}")
            Dim thirdBits As Long = totalBits \ 3L
            ' §209: at 5B digits totalBits ≈ 47.3B and thirdBits ≈ 15.77B — far above
            ' UInt32.MaxValue (4.29B).  The original code's `CUInt(thirdBits)` truncated
            ' silently to 2.88B, producing wrong shifts AND making mpQ2 pre-alloc
            ' (sized for k1Limbs) far smaller than the actual result Q2 (~649M limbs vs
            ' 45M alloc'd), which triggered a silent ~5 GB realloc inside mpz_tdiv_q_2exp
            ' on the 8th relaunch (2026-05-05 17:08 PT).
            '
            ' Fix: use BigShiftRight/BigShiftLeft (which chunk Long bit counts via
            ' multiple ≤2.1B-bit GmpRaw_tdiv_q_2exp/mul_2exp calls) for the split AND
            ' for the Combine A/C shifts.  k1 is kept as an mp_bitcnt_t (still used
            ' for log formatting and for older code paths that operate at smaller
            ' digit counts where thirdBits fits in UInt32) but tracked alongside
            ' thirdBits (Long) which is the authoritative shift amount.
            Dim k1 As New mp_bitcnt_t(CUInt(System.Math.Min(thirdBits, CLng(UInt32.MaxValue))))
            WriteToLog($"[3PM-DBG] thirdBits={thirdBits:N0} k1.Value={k1.Value} (mp_bitcnt_t clamped; actual shifts use BigShiftRight/Left)")

            ' Shared staging buffer for all spill I/O (sequential, never concurrent).
            Dim spillStaging(4194303) As Byte  ' 4 MB staging buffer (§56) — finalT spill only

            ' finalQ._mp_size / k1-limb counts used for pre-alloc sizing below.
            Dim _finalQSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 4)))
            ' §209: use Long thirdBits (not UInt32 k1) for limb count.  At 5B scale
            ' k1.Value is clamped to UInt32.MaxValue, so CLng(k1.Value)\64 would be
            ' ~67M limbs instead of the actual ~246M limbs each Q-piece needs.
            Dim _k1Limbs As Long = thirdBits \ 64L

            ' Extract Q0 = finalQ mod 2^k (low third) and shift finalQ to hold Q2*2^k + Q1.
            ' Using only k1-sized shifts avoids the 2k overflow in mp_bitcnt_t.
            '
            ' Pre-alloc tmpHigh before the tdiv call — same pattern as Combine section.
            ' Without it, GmpReallocFunc is invoked for the S→L transition (~747 MB), and
            ' GMP crashes silently (AV inside native code) when it immediately writes to the
            ' freshly VirtualAlloc'd pages before the OS has a chance to back them.
            ' With pre-alloc, MPZ_REALLOC short-circuits and GmpReallocFunc is never called.
            Dim tmpHigh As New mpz_t()
            gmp_lib.mpz_init2(tmpHigh, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L))) ' seed with VirtualAlloc'd buffer
            Dim _tHNeeded As Long = _finalQSz - _k1Limbs + 2L   ' result ≤ finalQSz - k1Limbs limbs; +2 margin
            ' §96 fix: pre-grow via PreAllocMpzToLimbs (NATIVE pool).  §79 originally called the
            ' MANAGED PoolGet, but §30 later made the native allocator the default — leaving
            ' _gmpPool(b) Nothing, so PoolGet threw NRE here in normal (native) mode and crashed
            ' small from-scratch runs in Phase 3.  PreAllocMpzToLimbs uses GmpNativeAlloc_PoolGet +
            ' frees the init2 seed buffer via GmpNativeAlloc_FreeRaw (the matching native free), and
            ' no-ops below GMP_LARGE_THRESHOLD (init2 buffer kept) — same behaviour, native-correct.
            PreAllocMpzToLimbs(tmpHigh, _tHNeeded)
            ' §227 (issue #61, 2026-05-22): pre-allocate mpQ1 + mpQ2 BEFORE the
            ' BigShiftRight + parallel-extraction block so both threads can use them.
            ' These k1-sized init+pre-alloc steps were previously between the Q0 and
            ' Q1/Q2 extractions; the move is mechanical (no semantic change to the
            ' pre-alloc sizing).
            Dim mpQ1 As New mpz_t()
            gmp_lib.mpz_init2(mpQ1, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _q1Needed As Long = _k1Limbs + 2L
            ' §96 cleanup: native-pool pre-grow (replaces the §79 managed PoolGet + VirtualFree of
            ' the init2 seed buffer, which leaked it as a native-pool block). Same gate/behaviour.
            PreAllocMpzToLimbs(mpQ1, _q1Needed)
            Dim mpQ2 As New mpz_t()
            gmp_lib.mpz_init2(mpQ2, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _q2Needed As Long = _k1Limbs + 2L  ' same upper bound as Q1
            ' §96 cleanup: native-pool pre-grow (replaces §79 managed PoolGet + init2-buffer leak).
            PreAllocMpzToLimbs(mpQ2, _q2Needed)

            WriteToLog($"[3PM-DBG] tmpHigh _mp_alloc={Runtime.InteropServices.Marshal.ReadInt32(tmpHigh.Pointer, 0):N0}  about to BigShiftRight(tmpHigh, finalQ, thirdBits={thirdBits:N0})")
            BigShiftRight(tmpHigh, finalQ, thirdBits)  ' §209: tmpHigh = finalQ >> thirdBits = Q2*2^k + Q1
            WriteToLog($"[3PM-DBG§227] BigShiftRight done: tmpHigh._mp_size={Runtime.InteropServices.Marshal.ReadInt32(tmpHigh.Pointer, 4):N0}; entering parallel Q0 || Q1+Q2 extraction")

            ' §227 (issue #61, 2026-05-22): parallel Q0 extraction || Q1+Q2 extraction.
            ' Two independent paths after tmpHigh is computed:
            '   Q0 path: reads tmpHigh, mutates finalQ in-place via _scratchRaw.
            '   Q1Q2 path: reads tmpHigh, writes mpQ2 + mpQ1 via _scratchRaw2.
            ' Shared input tmpHigh is treated as read-only; outputs are disjoint
            ' (finalQ vs mpQ1+mpQ2).  Each path uses its own Marshal.AllocHGlobal
            ' scratch struct so the §78 raw-pointer pattern stays per-thread.
            ' BigShiftLeft / BigShiftRight / GmpRaw_sub on disjoint mpz_t's are
            ' safe under GMP's "no shared mpz_t across threads" rule.
            Dim _t227 As DateTime = DateTime.Now
            System.Threading.Tasks.Parallel.Invoke(
                Sub()
                    ' Q0 = finalQ - (tmpHigh << thirdBits).
                    ' Uses the §209b raw-IntPtr scratch dance to bypass the managed
                    ' wrapper's §78 side-effect that corrupts registered mpz_t.Pointer.
                    Dim _scratchRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_scratchRaw)
                    Dim _scratchMpz As New mpz_t()
                    _scratchMpz.Pointer = _scratchRaw
                    PreAllocMpzToLimbs(_scratchMpz, _finalQSz + 4L)
                    BigShiftLeft(_scratchMpz, tmpHigh, thirdBits)
                    WriteToLog($"[3PM-DBG§227-Q0] _scratchMpz (tmpHigh<<thirdBits) size={Runtime.InteropServices.Marshal.ReadInt32(_scratchRaw, 4):N0}; pre-allocing finalQ for safe in-place sub")
                    PreAllocMpzToLimbs(finalQ, _finalQSz + 2L)
                    Dim _finalQPtr209b As IntPtr = finalQ.Pointer
                    GmpRaw_sub(_finalQPtr209b, _finalQPtr209b, _scratchRaw)
                    WriteToLog($"[3PM-DBG§227-Q0] GmpRaw_sub done: finalQ._mp_size={Runtime.InteropServices.Marshal.ReadInt32(_finalQPtr209b, 4):N0} (= Q0)")
                    GmpRaw_clear(_scratchRaw)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_scratchRaw)
                    _scratchMpz.Pointer = IntPtr.Zero
                    finalQ.Pointer = _finalQPtr209b
                End Sub,
                Sub()
                    ' (mpQ2, mpQ1) from tmpHigh:
                    '   mpQ2 = tmpHigh >> thirdBits     (Q2 = high third)
                    '   mpQ1 = tmpHigh - (mpQ2 << thirdBits)   (Q1 = middle third)
                    BigShiftRight(mpQ2, tmpHigh, thirdBits)
                    WriteToLog($"[3PM-DBG§227-Q1Q2] BigShiftRight(mpQ2) done: mpQ2._mp_size={Runtime.InteropServices.Marshal.ReadInt32(mpQ2.Pointer, 4):N0}")
                    Dim _scratchRaw2 As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_scratchRaw2)
                    Dim _scratchMpz2 As New mpz_t()
                    _scratchMpz2.Pointer = _scratchRaw2
                    Dim _tmpHighSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(tmpHigh.Pointer, 4)))
                    PreAllocMpzToLimbs(_scratchMpz2, _tmpHighSz + 4L)
                    BigShiftLeft(_scratchMpz2, mpQ2, thirdBits)
                    PreAllocMpzToLimbs(mpQ1, _tmpHighSz + 2L)
                    Dim _mpQ1Ptr209b As IntPtr = mpQ1.Pointer
                    Dim _tmpHighPtr209b As IntPtr = tmpHigh.Pointer
                    GmpRaw_sub(_mpQ1Ptr209b, _tmpHighPtr209b, _scratchRaw2)
                    WriteToLog($"[3PM-DBG§227-Q1Q2] GmpRaw_sub done: mpQ1._mp_size={Runtime.InteropServices.Marshal.ReadInt32(_mpQ1Ptr209b, 4):N0}")
                    GmpRaw_clear(_scratchRaw2)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_scratchRaw2)
                    _scratchMpz2.Pointer = IntPtr.Zero
                    mpQ1.Pointer = _mpQ1Ptr209b
                    tmpHigh.Pointer = _tmpHighPtr209b
                End Sub)
            WriteToLog($"[3PM-DBG§227] parallel Q extraction complete in {(DateTime.Now - _t227).TotalSeconds:F2}s; clearing tmpHigh")
            gmp_lib.mpz_clear(tmpHigh)
            WriteToLog($"[3PM-DBG] Q split complete")

            ' §61: Compute the three products r0=N*Q0, r1=N*Q1, r2=N*Q2.
            ' These were previously run via Parallel.Invoke, but GMP's mpz_mul may
            ' temporarily modify its input operands internally (normalisation, sign
            ' handling).  Concurrent access to the same mpz_t from multiple threads —
            ' even read-only — is unsafe per the GMP documentation.  All three calls
            ' share gmpNumer, so they must run sequentially.
            ' Q0 (finalQ), Q1 (mpQ1), Q2 (mpQ2) stay in RAM — no disk spilling needed.
            ' r0, r1, r2 also stay in RAM; Combine B and D use them directly without reloading.
            Dim mpR0 As New mpz_t()
            Dim mpR1 As New mpz_t()
            Dim mpR2 As New mpz_t()
            ' Pre-alloc mpR0, mpR1, mpR2 only when the result exceeds GMP_LARGE_THRESHOLD.
            ' For large runs: SafeMpzMul fast path calls mpz_mul directly on the init2 buffer;
            ' MPZ_REALLOC would trigger S→L via GmpReallocFunc which crashes on some builds.
            ' Pre-alloc replaces the init2 buffer with an exactly-sized one so MPZ_REALLOC
            ' short-circuits (alloc already sufficient).
            ' For small runs (result < GMP_LARGE_THRESHOLD): the init2 buffer (65536 limbs,
            ' VirtualAlloc'd) is already large enough — skipping keeps GmpFreeFunc routing
            ' the eventual free through VirtualFree rather than CRT free.
            Dim _numerSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
            Dim _q0Sz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 4)))
            Dim _q1SzR As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpQ1.Pointer, 4)))
            Dim _q2SzR As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpQ2.Pointer, 4)))

            gmp_lib.mpz_init2(mpR0, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _r0Needed As Long = _numerSz + _q0Sz + 2L
            PreAllocMpzToLimbs(mpR0, _r0Needed)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)

            gmp_lib.mpz_init2(mpR1, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _r1Needed As Long = _numerSz + _q1SzR + 2L
            PreAllocMpzToLimbs(mpR1, _r1Needed)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)

            gmp_lib.mpz_init2(mpR2, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _r2Needed As Long = _numerSz + _q2SzR + 2L
            PreAllocMpzToLimbs(mpR2, _r2Needed)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)

            If _logLevel >= 3 Then
                Dim _procP_pre = Process.GetCurrentProcess()
                Dim _ramP_pre As Long = _procP_pre.WorkingSet64 \ 1048576
                Dim _vmP_pre As Long = _procP_pre.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] §61 serial multiply start: r0=N*Q0, r1=N*Q1, r2=N*Q2  RAM:{_ramP_pre:N0}MB  Committed:{_vmP_pre:N0}MB")
            End If
            WriteToLog($"[ComputePi] §61 r0 DIAG: rop.Ptr={mpR0.Pointer:X} rop_alloc={Runtime.InteropServices.Marshal.ReadInt32(mpR0.Pointer,0):N0} rop_sz={Runtime.InteropServices.Marshal.ReadInt32(mpR0.Pointer,4):N0} rop_d={Runtime.InteropServices.Marshal.ReadInt64(mpR0.Pointer,8):X}")
            WriteToLog($"[ComputePi] §61 r0 DIAG: opA.Ptr={gmpNumer.Pointer:X} opA_alloc={Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer,0):N0} opA_sz={Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer,4):N0} opA_d={Runtime.InteropServices.Marshal.ReadInt64(gmpNumer.Pointer,8):X}")
            WriteToLog($"[ComputePi] §61 r0 DIAG: opB.Ptr={finalQ.Pointer:X} opB_alloc={Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer,0):N0} opB_sz={Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer,4):N0} opB_d={Runtime.InteropServices.Marshal.ReadInt64(finalQ.Pointer,8):X}")
            ' §106 Gap 1: R0/R1/R2 run in parallel — each uses a disjoint (result, Q_i) pair.
            ' gmpNumer is read-only in SafeMpzMul (only opA/opB struct fields are read to set
            ' up zero-copy piece windows; neither is ever written).  The pool allocator and
            ' VirtualAlloc are both thread-safe.  Running all three concurrently saves ~2/3
            ' of their combined wall-clock time on a 24-core machine.
            '
            ' §106 checkpoint: try to reload previously computed R0/R1/R2 before multiplying.
            Dim _r0Done As Boolean = TryLoadPhase3Value("mpR0", mpR0, p3SnapDir)
            Dim _r1Done As Boolean = TryLoadPhase3Value("mpR1", mpR1, p3SnapDir)
            Dim _r2Done As Boolean = TryLoadPhase3Value("mpR2", mpR2, p3SnapDir)
            If _r0Done AndAlso _r1Done AndAlso _r2Done Then
                LogPhase("[ComputePi] §61 all R0/R1/R2 loaded from checkpoint — skipping multiply")
                gmp_lib.mpz_clears(finalQ, mpQ1, mpQ2, Nothing)
            Else
                ' §210: serialize the three multiplies (was parallel.invoke).  At 5B digits
                ' each Q_i is ~246M limbs, gmpNumer is ~259M limbs, so each SafeMpzMul peaks
                ' at ~10-12 GB during recursion.  Three in parallel exceeded the 64 GB
                ' budget on the 10th relaunch (2026-05-05 17:33 PT) — OOM at a 47 MB
                ' inner accum buffer.  Save each immediately on completion so a crash
                ' during r1 or r2 keeps the previously-computed r_i values.
                '
                ' §233 (issue #53, 2026-05-23): lift §210's force-to-1.  Pre-§233 §210
                ' kept _safeMulDop=1 for safety at 5B, but at smaller scales DOP=1 wastes
                ' ~24× of available parallelism.  §232 made the SavePhase3Value backup
                ' async, so the only remaining serialization concern is COMPUTE memory.
                '
                ' Solution: keep the sequential structure (one compute in flight at a time
                ' to bound RAM) but lift the inner DOP via the same scale-aware policy as
                ' §231.  Per-multiply peak ≈ DOP^3 × per-task-buffer + result + intermediate.
                ' Memory budget (40 GB target on 64 GB box) and per-task-buffer scale
                ' linearly with gmpNumer.size, so the same thresholds work:
                '   numTerms <  100 M (~ < 1.4 B digits): DOP=6 → ~15 GB peak / multiply
                '   numTerms <  250 M (1.4-3.5 B)       : DOP=4 → ~20 GB peak
                '   numTerms >= 250 M (>= 3.5 B)        : DOP=3 → ~25 GB peak  (= §210 safety target)
                '
                ' Per-multiply wall: ~6 min serial at 1B → ~2 min at DOP=6 (3-4× speedup).
                ' Three multiplies: ~18 min → ~6 min at 1B (~12 min saved).  At 5B with
                ' DOP=3 the savings are smaller (~30% per multiply) but on a larger absolute
                ' base (~50 min/multiply → ~35 min/multiply = 45 min saved across the 3).
                Dim _saved210Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
                Dim _chosenDop233 As Integer
                If numTerms < 100_000_000L Then
                    _chosenDop233 = 6
                ElseIf numTerms < 250_000_000L Then
                    _chosenDop233 = 4
                Else
                    _chosenDop233 = 3
                End If
                System.Threading.Volatile.Write(_safeMulDop, _chosenDop233)
                WriteToLog($"[ComputePi§233] R0/R1/R2 pipeline: numTerms={numTerms:N0}, chosen DOP={_chosenDop233} (was hardcoded 1 in §210; SavePhase3Value backup is async via §232)")
                ' §274 (#121): route the three numerator R-multiplies through the chunked grid
                ' (parallel cells at PI_CG_DOP, low per-cell RAM) instead of §gen at the §233 DOP
                ' cap above — the same lever as §273 for the combine, for the runs §233 pins to
                ' DOP=3.  SafeMpzMulCG is bit-identical to SafeMpzMul (chunked-grid full product
                ' proven by --test-chunkedgrid; sign-aware though r_i are positive here).
                _numerChunkedGrid = (Environment.GetEnvironmentVariable("PI_NUMER_CG") <> "0")
                Dim _ncgEnv As String = Environment.GetEnvironmentVariable("PI_NUMER_CG_MINTERMS")
                Dim _ncgParsed As Long
                If _ncgEnv IsNot Nothing AndAlso Long.TryParse(_ncgEnv, _ncgParsed) AndAlso _ncgParsed >= 0L Then _numerCgMinTerms = _ncgParsed
                Dim _useCgNumer As Boolean = _numerChunkedGrid AndAlso numTerms >= _numerCgMinTerms
                If _useCgNumer Then WriteToLog($"[ComputePi§274] numerator R-multiplies via chunked-grid (numTerms={numTerms:N0} >= {_numerCgMinTerms:N0}; §233 DOP={_chosenDop233} bypassed)")
                If Not _r0Done Then
                    WriteToLog("[ComputePi§233] computing r0 = gmpNumer * Q0 (finalQ) at DOP=" & _chosenDop233 & "...")
                    Dim _t233R0 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                    If _useCgNumer Then SafeMpzMulCG(mpR0, gmpNumer, finalQ) Else SafeMpzMul(mpR0, gmpNumer, finalQ)
                    Dim _t233R0Sec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t233R0) / System.Diagnostics.Stopwatch.Frequency
                    WriteToLog($"[ComputePi§233] r0 done in {_t233R0Sec:F1}s; saving mpR0 (size={Runtime.InteropServices.Marshal.ReadInt32(mpR0.Pointer, 4):N0})")
                    SavePhase3Value("mpR0", mpR0, p3SnapDir)
                Else
                    WriteToLog("[ComputePi§233] r0 already loaded; skipping")
                End If
                gmp_lib.mpz_clear(finalQ)
                If Not _r1Done Then
                    WriteToLog("[ComputePi§233] computing r1 = gmpNumer * Q1 (mpQ1) at DOP=" & _chosenDop233 & "...")
                    Dim _t233R1 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                    If _useCgNumer Then SafeMpzMulCG(mpR1, gmpNumer, mpQ1) Else SafeMpzMul(mpR1, gmpNumer, mpQ1)
                    Dim _t233R1Sec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t233R1) / System.Diagnostics.Stopwatch.Frequency
                    WriteToLog($"[ComputePi§233] r1 done in {_t233R1Sec:F1}s; saving mpR1 (size={Runtime.InteropServices.Marshal.ReadInt32(mpR1.Pointer, 4):N0})")
                    SavePhase3Value("mpR1", mpR1, p3SnapDir)
                Else
                    WriteToLog("[ComputePi§233] r1 already loaded; skipping")
                End If
                gmp_lib.mpz_clear(mpQ1)
                If Not _r2Done Then
                    WriteToLog("[ComputePi§233] computing r2 = gmpNumer * Q2 (mpQ2) at DOP=" & _chosenDop233 & "...")
                    Dim _t233R2 As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                    If _useCgNumer Then SafeMpzMulCG(mpR2, gmpNumer, mpQ2) Else SafeMpzMul(mpR2, gmpNumer, mpQ2)
                    Dim _t233R2Sec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t233R2) / System.Diagnostics.Stopwatch.Frequency
                    WriteToLog($"[ComputePi§233] r2 done in {_t233R2Sec:F1}s; saving mpR2 (size={Runtime.InteropServices.Marshal.ReadInt32(mpR2.Pointer, 4):N0})")
                    SavePhase3Value("mpR2", mpR2, p3SnapDir)
                Else
                    WriteToLog("[ComputePi§233] r2 already loaded; skipping")
                End If
                gmp_lib.mpz_clear(mpQ2)
                System.Threading.Volatile.Write(_safeMulDop, _saved210Dop)
                WriteToLog($"[ComputePi§233] DOP restored to {_saved210Dop}; all r0/r1/r2 done")
            End If
            ' Swap r2 into gmpNumer (same pattern as the old serial Pass 2 end).
            ' The old gmpNumer buffer (~208 MB, the 426880*sqrt value) is freed by the swap.
            WriteToLog("[ComputePi] §61 calling mpz_swap(gmpNumer, mpR2)...")
            gmp_lib.mpz_swap(gmpNumer, mpR2)
            WriteToLog("[ComputePi] §61 swap done")
            WriteToLog("[ComputePi] §61 calling mpz_clear(mpR2)...")
            gmp_lib.mpz_clear(mpR2)
            WriteToLog("[ComputePi] §61 clear r2 done")
            If _logLevel >= 3 Then
                Dim _procP_post = Process.GetCurrentProcess()
                Dim _ramP_post As Long = _procP_post.WorkingSet64 \ 1048576
                Dim _vmP_post As Long = _procP_post.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] §61 parallel multiply done; entering Combine  RAM:{_ramP_post:N0}MB  Committed:{_vmP_post:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] r2 (= gmpNumer after swap) = {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' ── Combine: gmpNumer = ((r2 << k) + r1) << k + r0 ──
            ' NOTE: mpz_t is a value type (struct) in GMP.NET.  Passing the same
            ' variable as BOTH destination and source gives GMP two struct copies
            ' that share the same _mp_d pointer.  GMP's aliasing guard compares
            ' struct addresses (not _mp_d), sees no match, takes the non-aliased
            ' path, reallocates rop's limb buffer via MPZ_REALLOC, then reads from
            ' op's now-freed _mp_d → crash.  Every step below uses a fresh output
            ' variable + mpz_swap to sidestep this.

            ' Step A: gmpNumer = r2 << k  (~390 MB → ~572 MB)
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine A: shift r2 ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) left {CLng(k1):N0} bits → result≈{(CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) + CLng(k1)) \ 8388608:N0} MB")
            Dim mpShiftA As New mpz_t()
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine A: mpz_init2(mpShiftA)")
            ' Use mpz_init2 with a seed allocation >= GMP_LARGE_THRESHOLD so the
            ' limb buffer comes from VirtualAlloc (not the CRT heap).  When
            ' mpz_mul_2exp later grows the buffer it takes the large→large realloc
            ' path (VirtualAlloc + VirtualFree), bypassing _savedGmpFree entirely.
            gmp_lib.mpz_init2(mpShiftA, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct so
            ' MPZ_REALLOC short-circuits and GmpReallocFunc is never called.
            ' Root cause: GmpReallocFunc crashes silently when a managed exception escapes
            ' a native callback — .NET 10 terminates immediately, no handlers run.
            If mpShiftA.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzA As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _kBitsA As Long = CLng(k1)
                Dim _shiftLimbs As Long = _numerAbsSzA + (_kBitsA \ 64L) + 2L
                Dim _shiftBytesA As Long = _shiftLimbs * 8L
                ' Only pre-alloc if result will be large (>= GMP_LARGE_THRESHOLD).
                ' For small numbers the mpz_init2 buffer (512 KB) is sufficient and
                ' normal GmpReallocFunc L→L handles growth correctly.  A small
                ' VirtualAlloc buffer would be freed by GmpFreeFunc via _savedGmpFree
                ' (size < threshold) which is wrong for VirtualAlloc memory → crash.
                PreAllocMpzToLimbs(mpShiftA, _shiftLimbs)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)
            End If
            If _logLevel >= 3 Then
                ' Dump the native __mpz_struct that GMP will use as the destination of
                ' mpz_mul_2exp.  Layout on Windows x64:
                '   [0] int  _mp_alloc  (number of limbs allocated)
                '   [4] int  _mp_size   (actual used limbs, signed)
                '   [8] ptr  _mp_d      (pointer to limb array)
                ' _mp_alloc should be 65537 (= 1 + GMP_LARGE_THRESHOLD*8 / 64 bits).
                ' If it is a very large value GMP's MPZ_REALLOC macro will short-circuit
                ' (skip our GmpReallocFunc) and write 546 MB into the 512 KB buffer.
                If mpShiftA.Pointer <> IntPtr.Zero Then
                    Dim _mpA_alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(mpShiftA.Pointer, 0)
                    Dim _mpA_size  As Integer = Runtime.InteropServices.Marshal.ReadInt32(mpShiftA.Pointer, 4)
                    Dim _mpA_mpd   As Long    = Runtime.InteropServices.Marshal.ReadInt64(mpShiftA.Pointer, 8)
                    WriteToLog($"[ComputePi] Combine A mpShiftA native struct: ptr={mpShiftA.Pointer:X} _mp_alloc={_mpA_alloc} _mp_size={_mpA_size} _mp_d={_mpA_mpd:X}")
                Else
                    WriteToLog("[ComputePi] Combine A mpShiftA.Pointer is NULL — init2 failed silently")
                End If
                WriteToLog($"[ComputePi] Combine A: BigShiftLeft  thirdBits={thirdBits:N0}  gmpNumer={CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits")
            End If
            ' §209: BigShiftLeft chunks Long bit count safely; mpz_mul_2exp(rop, op, k1)
            ' would shift by clamped k1 = min(thirdBits, UInt32.MaxValue) which is wrong
            ' at 5B scale where thirdBits > UInt32.
            BigShiftLeft(mpShiftA, gmpNumer, thirdBits)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine A: BigShiftLeft returned OK")
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine A: mpz_swap")
            gmp_lib.mpz_swap(gmpNumer, mpShiftA)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine A: mpz_clear(mpShiftA)")
            gmp_lib.mpz_clear(mpShiftA)     ' frees the old ~390 MB limb buffer
            If _logLevel >= 3 Then
                Dim _procCA = Process.GetCurrentProcess()
                Dim _ramCombA As Long = _procCA.WorkingSet64 \ 1048576
                Dim _vmCombA As Long = _procCA.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine A done (r2<<k)  RAM:{_ramCombA:N0}MB  Committed:{_vmCombA:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine A result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' Step B: reload r1; gmpNumer += r1  (~572 MB + ~390 MB → ~572 MB)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_init(mpR1) + deserialize")
            ' §61: mpR1 already in RAM from the parallel multiply — no disk reload.
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine B: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r1 ({CLng(gmp_lib.mpz_sizeinbase(mpR1, 2)):N0} bits)")
            If _logLevel >= 3 Then
                Dim _procCBpre = Process.GetCurrentProcess()
                Dim _ramCombBpre As Long = _procCBpre.WorkingSet64 \ 1048576
                Dim _vmCombBpre As Long = _procCBpre.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine B r1 in RAM  RAM:{_ramCombBpre:N0}MB  Committed:{_vmCombBpre:N0}MB")
            End If
            Dim mpAddB As New mpz_t()
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_init2(mpAddB)")
            gmp_lib.mpz_init2(mpAddB, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpAddB.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero AndAlso mpR1.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzB As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _r1AbsSzB As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpR1.Pointer, 4)))
                Dim _addLimbs As Long = System.Math.Max(_numerAbsSzB, _r1AbsSzB) + 2L
                PreAllocMpzToLimbs(mpAddB, _addLimbs)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)
            End If
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_add")
            gmp_lib.mpz_add(mpAddB, gmpNumer, mpR1)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_swap")
            gmp_lib.mpz_swap(gmpNumer, mpAddB)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_clear(mpAddB) + mpz_clear(mpR1)")
            gmp_lib.mpz_clear(mpAddB)
            gmp_lib.mpz_clear(mpR1)
            If _logLevel >= 3 Then
                Dim _procCB = Process.GetCurrentProcess()
                Dim _ramCombB As Long = _procCB.WorkingSet64 \ 1048576
                Dim _vmCombB As Long = _procCB.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine B done (+r1)  RAM:{_ramCombB:N0}MB  Committed:{_vmCombB:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine B result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' Step C: gmpNumer = (r2<<k + r1) << k  (~572 MB → ~755 MB)
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine C: shift ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) left {CLng(k1):N0} bits → result≈{(CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) + CLng(k1)) \ 8388608:N0} MB")
            Dim mpShiftC As New mpz_t()
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine C: mpz_init2(mpShiftC)")
            gmp_lib.mpz_init2(mpShiftC, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpShiftC.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzC As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _kBitsC As Long = CLng(k1)
                Dim _shiftLimbs As Long = _numerAbsSzC + (_kBitsC \ 64L) + 2L
                PreAllocMpzToLimbs(mpShiftC, _shiftLimbs)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)
            End If
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine C: BigShiftLeft  thirdBits={thirdBits:N0} bits")
            ' §209: see Combine A above — use BigShiftLeft for Long bit counts safely.
            BigShiftLeft(mpShiftC, gmpNumer, thirdBits)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine C: mpz_swap")
            gmp_lib.mpz_swap(gmpNumer, mpShiftC)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine C: mpz_clear(mpShiftC)")
            gmp_lib.mpz_clear(mpShiftC)
            If _logLevel >= 3 Then
                Dim _procCC = Process.GetCurrentProcess()
                Dim _ramCombC As Long = _procCC.WorkingSet64 \ 1048576
                Dim _vmCombC As Long = _procCC.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine C done ((r2<<k+r1)<<k)  RAM:{_ramCombC:N0}MB  Committed:{_vmCombC:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine C result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' Step D: reload r0; gmpNumer += r0  (~755 MB + ~390 MB → ~755 MB)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_init(mpR0) + deserialize")
            ' §61: mpR0 already in RAM from the parallel multiply — no disk reload.
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine D: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r0 ({CLng(gmp_lib.mpz_sizeinbase(mpR0, 2)):N0} bits)")
            If _logLevel >= 3 Then
                Dim _procCDpre = Process.GetCurrentProcess()
                Dim _ramCombDpre As Long = _procCDpre.WorkingSet64 \ 1048576
                Dim _vmCombDpre As Long = _procCDpre.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine D r0 in RAM  RAM:{_ramCombDpre:N0}MB  Committed:{_vmCombDpre:N0}MB")
            End If
            Dim mpAddD As New mpz_t()
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_init2(mpAddD)")
            gmp_lib.mpz_init2(mpAddD, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpAddD.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero AndAlso mpR0.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzD As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _r0AbsSzD As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpR0.Pointer, 4)))
                Dim _addLimbs As Long = System.Math.Max(_numerAbsSzD, _r0AbsSzD) + 2L
                PreAllocMpzToLimbs(mpAddD, _addLimbs)   ' §96 cleanup: native-pool pre-grow (was §79 managed PoolGet + init2-buffer leak)
            End If
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_add")
            gmp_lib.mpz_add(mpAddD, gmpNumer, mpR0)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_swap")
            gmp_lib.mpz_swap(gmpNumer, mpAddD)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_clear(mpAddD) + mpz_clear(mpR0)")
            gmp_lib.mpz_clear(mpAddD)
            gmp_lib.mpz_clear(mpR0)
            If _logLevel >= 3 Then
                Dim _procCD = Process.GetCurrentProcess()
                Dim _ramCombD As Long = _procCD.WorkingSet64 \ 1048576
                Dim _vmCombD As Long = _procCD.PrivateMemorySize64 \ 1048576
                WriteToLog($"[ComputePi] Combine D done (+r0)  RAM:{_ramCombD:N0}MB  Committed:{_vmCombD:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine D result (final gmpNumer): {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            LogPhase("Numerator complete")

            ' §106 checkpoint: save gmpNumer before the final divide — it's the most
            ' expensive intermediate and reloading it avoids re-running the R0/R1/R2
            ' multiplies and Combine A-D steps if the divide crashes.
            SavePhase3Value("gmpNumer", gmpNumer, p3SnapDir)

            ' Reload finalT now that the large multiply is done
            gmp_lib.mpz_init(finalT)    ' re-init the cleared mpz_t before import
            Using fs As New FileStream(finalT_spillPath, FileMode.Open, FileAccess.Read)
                Using br As New BinaryReader(fs)
                    DeserializeOneMpz(finalT, br, stagingT)
                End Using
            End Using
            Try
                System.IO.File.Delete(finalT_spillPath)
            Catch
            End Try

NumeratorDone:
            If _logLevel >= 2 Then
                WriteToLog($"[ComputePi] finalT reloaded from spill file")
                WriteToLog($"[ComputePi] mpz_tdiv_q: pi = numer / T  (numer~{CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 10)):N0} digits  T~{CLng(gmp_lib.mpz_sizeinbase(finalT, 10)):N0} digits)")
            End If
            ' §NumeratorDiv: Restore Pointer fields AFTER the WriteToLog managed calls above.
            ' gmp_lib.mpz_sizeinbase fires §78 (side-effect of any managed GMP call), overwriting
            ' ALL registered mpz_t.Pointer fields with stale/wrong native addresses.
            ' _gmpPiRaw/_gmpNumerRaw/_finalTRaw were captured with the §NumeratorDiv-v4 fix
            ' (correct values from the respective mpz_init calls, before the next managed call
            ' could corrupt them).  Native struct addresses never change (only _mp_d inside changes
            ' on realloc), so these captures remain valid as restore values here.
            ' SafeMpzDiv reads q.Pointer (gmpPi.Pointer) and a.Pointer (gmpNumer.Pointer) at entry
            ' to capture raw addresses — correct addresses are essential for the division result.
            If _gmpPiRaw <> IntPtr.Zero Then gmpPi.Pointer = _gmpPiRaw
            If _gmpNumerRaw <> IntPtr.Zero Then gmpNumer.Pointer = _gmpNumerRaw
            If _finalTRaw <> IntPtr.Zero Then finalT.Pointer = _finalTRaw
            ' §NumeratorDiv-v3: Pre-alloc block REMOVED.
            ' gmp_lib.mpz_inits fires §78 during each mpz_init call internally, which overwrites
            ' ALL registered mpz_t.Pointer fields including gmpPi.Pointer.  By the time
            ' mpz_inits returns, gmpPi.Pointer is corrupted to another mpz_t's native struct address.
            ' Any capture of _gmpPiRaw after mpz_inits is therefore wrong.  Writing the new large
            ' buffer pointer to the wrong struct would silently corrupt memory; reading the wrong
            ' struct's _mp_d and passing it to _savedGmpFree would crash with STATUS_HEAP_CORRUPTION.
            ' Fix: skip pre-alloc entirely and let GmpReallocFunc handle it.
            ' When SafeMpzDiv fires MPZ_REALLOC(gmpPi, ~93M limbs):
            '   old_ptr = 1-limb CRT buffer (8 bytes), old_size < GMP_LARGE_THRESHOLD
            '     → freed correctly via _savedGmpFree (CRT free of CRT pointer — no crash)
            '   new_size ≈ 744 MB ≥ GMP_LARGE_THRESHOLD → VirtualAlloc for new buffer — correct
            ' Net effect: one single realloc inside SafeMpzDiv, correctly handled, no pre-alloc needed.
            ' §piCkpt: Resume from a saved gmpPi.bin if one exists for this digit count.
            ' Closes the only unprotected post-Phase-3 window (mpz_get_str), which has
            ' no internal checkpoint — without §piCkpt, a crash there forces re-running
            ' the final divide.  With §piCkpt, gmpPi survives across that crash.
            Dim _piCkptDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
            Dim _piCkptBin As String = System.IO.Path.Combine(_piCkptDir, "gmpPi.bin")
            Dim _piCkptMeta As String = System.IO.Path.Combine(_piCkptDir, "gmpPi_meta.txt")
            Dim _piCkptResumed As Boolean = False
            If _autoCheckpoint AndAlso System.IO.File.Exists(_piCkptBin) AndAlso System.IO.File.Exists(_piCkptMeta) Then
                Try
                    Dim _piMetaLines As String() = System.IO.File.ReadAllLines(_piCkptMeta)
                    Dim _piMeta As New Dictionary(Of String, String)()
                    For Each _ml As String In _piMetaLines
                        Dim _eq As Integer = _ml.IndexOf("="c)
                        If _eq > 0 Then _piMeta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                    Next
                    Dim _snapDigits As Long = 0L
                    If _piMeta.ContainsKey("digits") AndAlso Long.TryParse(_piMeta("digits"), _snapDigits) AndAlso _snapDigits = digits Then
                        Dim _piStaging(4194303) As Byte
                        Using _fs As New FileStream(_piCkptBin, FileMode.Open, FileAccess.Read)
                            Using _br As New BinaryReader(_fs)
                                DeserializeOneMpz(gmpPi, _br, _piStaging)
                            End Using
                        End Using
                        _piCkptResumed = True
                        WriteToLog($"[ComputePi§piCkpt] resumed: gmpPi.bin loaded (digits={digits:N0}) — skipping final SafeMpzDiv")
                    End If
                Catch _ex As Exception
                    WriteToLog($"[ComputePi§piCkpt] load failed ({_ex.Message}) — running full divide")
                End Try
            End If

            If Not _piCkptResumed Then
                _divCkptScope = "phase4"
                SafeMpzDiv(gmpPi, gmpNumer, finalT)   ' §107 Gap 6: operands ~5B+ limbs — mpz_tdiv_q hits mpn_mul_fft overflow

                ' §piCkpt: save gmpPi after final divide so a crash during mpz_get_str
                ' (the only unprotected segment of meaningful duration) does not force
                ' re-running the divide.
                If _autoCheckpoint Then
                    Try
                        If Not System.IO.Directory.Exists(_piCkptDir) Then System.IO.Directory.CreateDirectory(_piCkptDir)
                        Dim _piSaveStaging(4194303) As Byte
                        Using _fs As New FileStream(_piCkptBin, FileMode.Create, FileAccess.Write)
                            Using _bw As New BinaryWriter(_fs)
                                SerializeOneMpz(gmpPi, _bw, _piSaveStaging)
                            End Using
                        End Using
                        System.IO.File.WriteAllText(_piCkptMeta, $"digits={digits}{vbLf}")
                        BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                        WriteToLog($"[ComputePi§piCkpt] saved gmpPi.bin (digits={digits:N0})")
                    Catch _ex As Exception
                        WriteToLog($"[ComputePi§piCkpt] save failed: {_ex.Message}")
                    End Try
                End If
            End If
            gmp_lib.mpz_clears(gmpNumer, finalT, Nothing)
            LogPhase("Division complete")

            ' §241 (issue #69): trim pooled temporaries accumulated across the whole
            ' final divide before the decimal conversion allocates its big output buffer.
            ' Census-before here is the post-divide pool watermark.
            TrimPoolAtBoundary("pre-conversion", 1048576UL)

            If token.IsCancellationRequested Then Return ""

            If _logLevel >= 2 Then WriteToLog($"[ComputePi] mpz_get_str: converting result to string")
            Dim _strConvStart As DateTime = DateTime.Now
            ' §127 (#127): output digit count — known up front, so the parallel-converter status (which
            ' publishes no per-chunk progress) can show a rough size-based ETA.  Also used for routing below.
            Dim _piDigitsEstimate As Long = CLng(gmp_lib.mpz_sizeinbase(gmpPi, 10))
            Dim _strConvTimer As New System.Threading.Timer(
                Sub(state As Object)
                    Dim elapsed As TimeSpan = DateTime.Now - _strConvStart
                    ' §74 (issue #74): when ChunkedMpzGetStr is running it publishes (current,total)
                    ' so a 2-hour conversion shows visible chunk progress every ~minute instead of
                    ' a single mm:ss timer that's indistinguishable from a hang.  Snapshot both
                    ' fields locally to avoid a torn read across the conditional and the format.
                    Dim total As Long = _chunkConvTotal
                    Dim current As Long = _chunkConvCurrent
                    Dim statusText As String
                    If total > 0 Then
                        Dim etaText As String = ""
                        If current > 0 AndAlso current < total Then
                            Dim etaMinutes As Double = elapsed.TotalMinutes * CDbl(total - current) / CDbl(current)
                            If etaMinutes >= 60.0 Then
                                etaText = $", ETA ~{etaMinutes / 60.0:F1}h"
                            Else
                                etaText = $", ETA ~{etaMinutes:F0}m"
                            End If
                        End If
                        statusText = $"String conversion: chunk {current:N0} of {total:N0}, {elapsed:hh\:mm\:ss} elapsed{etaText}"
                    Else
                        ' §127 (#127): the parallel §226/§270 converter publishes no per-chunk progress,
                        ' so derive a rough ETA from the known output size and the observed §270 rate
                        ' (~5M digits/s at 5B — the conservative large-scale figure; smaller runs finish
                        ' sooner than predicted).  Shows the digit count too (not redundant with the
                        ' running-time label).  Labelled "~est" to flag it as a size-based estimate.
                        Dim etaText As String = ""
                        If _piDigitsEstimate > 0 Then
                            Dim remSec As Double = System.Math.Max(0.0, _piDigitsEstimate / 5_000_000.0 - elapsed.TotalSeconds)
                            etaText = If(remSec >= 3600.0, $", ~est {remSec / 3600.0:F1}h left", $", ~est {remSec / 60.0:F0}m left")
                        End If
                        statusText = $"String conversion ({_piDigitsEstimate:N0} digits)... {elapsed:hh\:mm\:ss} elapsed{etaText}"
                    End If
                    Me.BeginInvoke(Sub()
                                       LblStatus.Text = statusText
                                   End Sub)
                End Sub, Nothing, 1000, 1000)
            ' §216: GMP's mpz_get_str crashes with AccessViolation when the output exceeds
            ' ~2 GB (5B digits = 5 GB output).  Root cause appears to be Int32 overflow in
            ' mpz_get_str's internal recursive divide-and-conquer.  Route large outputs to
            ' ChunkedMpzGetStr which extracts 300M-digit slabs via mpz_fdiv_qr / 10^300M and
            ' calls mpz_get_str on each (each chunk ≤ 300 MB output, well within safe range).
            ' (_piDigitsEstimate computed above, before the status timer — §127.)
            Dim _usedChunkedPath As Boolean = False
            Dim piCharPtr As char_ptr = char_ptr.Zero
            Dim _strConvSw As New Diagnostics.Stopwatch()
            _strConvSw.Start()
            Try
                ' §226 (issue #37, 2026-05-22): route to parallel recursive-halving
                ' converter for digits >= 100M (≈ minimum size where the recursion
                ' tree has enough depth to amortize the power-of-10 precompute
                ' against parallel fan-out gains).  Above 1.5B, §216 chunked path
                ' remains as conservative fallback until §226 is 5B-validated.
                If _piDigitsEstimate >= 1_500_000_000L Then
                    ' §270 (#90): §226's safe-peel split rule makes the PARALLEL converter 5B-safe.
                    ' VALIDATED at 5B (π SHA 2218ee06… bit-identical, ~15.6 min vs §216's ~47 min, RAM
                    ' ~19 GB) ⇒ now the default; opt out with PI_CONV_PARALLEL=0 to use the §216 serial path.
                    If Environment.GetEnvironmentVariable("PI_CONV_PARALLEL") <> "0" Then
                        WriteToLog($"[ComputePi§270] Routing 5B to PARALLEL converter (§226 safe-peel, digits~={_piDigitsEstimate:N0}){vbCrLf}")
                        ParallelMpzGetStr(gmpPi, _piDigitsEstimate)   ' sets _displayNative*
                    Else
                        WriteToLog($"[ComputePi§216] Routing to chunked decimal converter (digits~={_piDigitsEstimate:N0} >= 1.5B threshold){vbCrLf}")
                        ChunkedMpzGetStr(gmpPi, _piDigitsEstimate)   ' sets _displayNativePtr, _displayNativeLen, _displayNativeBufSize
                    End If
                    _usedChunkedPath = True
                ElseIf _piDigitsEstimate >= 100_000_000L Then
                    WriteToLog($"[ComputePi§226] Routing to parallel decimal converter (digits~={_piDigitsEstimate:N0} >= 100M threshold){vbCrLf}")
                    ParallelMpzGetStr(gmpPi, _piDigitsEstimate)   ' sets _displayNativePtr, _displayNativeLen, _displayNativeBufSize
                    _usedChunkedPath = True   ' reuse same downstream flag (display state already set)
                Else
                    piCharPtr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, gmpPi)
                End If
            Finally
                _strConvTimer.Dispose()
            End Try
            _strConvSw.Stop()
            WriteToLog($"[ComputePi] mpz_get_str completed in {_strConvSw.Elapsed:mm\:ss\.fff}")
            ' §217 (2026-05-19, user directive after the 2026-05-19 5B run lost gmpPi.bin):
            ' NO CHECKPOINT IS DELETED MID-RUN.  This site previously fired right after
            ' mpz_get_str (or ChunkedMpzGetStr) succeeded, but the run is not done at
            ' that point — we still need to write pi_digits.txt, run autoverify, and
            ' cleanly exit.  If any of those fail, the gmpPi.bin checkpoint is the only
            ' thing standing between us and a 30+ hour SafeMpzDiv re-run from snap_Phase3.
            ' The load-side validator at line ~7341 silently rejects stale gmpPi.bin
            ' with a digit-count mismatch, so leaving the file on disk is safe.
            ' Cleanup happens externally between runs, never here.
            ' Capture the actual digit count BEFORE clearing gmpPi.
            ' mpz_sizeinbase returns an estimate within +1; add 2 to match GMP's internal
            ' alloc of (sizeinbase + 2) bytes.  Used to set _displayNativeLen correctly and
            ' to decide whether to free the buffer via VirtualFree or _savedGmpFree.
            Dim _piDigits As Long = CLng(gmp_lib.mpz_sizeinbase(gmpPi, 10))
            ' §216: when chunked path was used, _displayNativePtr/Len/BufSize are already set.
            If Not _usedChunkedPath Then
                _displayNativeBufSize = _piDigits + 2L   ' mirrors GmpAllocFunc's received size
            End If
            ' Free gmpPi now (~744 MB native); reinit 1-limb stub so Finally mpz_clears is safe.
            gmp_lib.mpz_clear(gmpPi)
            gmp_lib.mpz_init(gmpPi)
            ' Keep the native char buffer alive — the display timer will stream bytes
            ' directly from it, avoiding any large managed string allocation.
            If Not _usedChunkedPath Then
                _displayNativePtr = piCharPtr.Pointer
                _displayNativeLen = _piDigits + 1L   ' digits + null terminator position
            End If
            LogPhase("String conversion complete")

            LogPhase($"Done! {digits:N0} digits computed")
            Return ""

        Catch ex As Exception
            WriteExceptionToLog("ComputePiGMP", ex)
            Throw
        Finally
            Try
                If gmpVariablesInitialized Then
                    gmp_lib.mpz_clears(gmpPi, gmpOne, Nothing)
                End If
            Catch
            End Try
            ' Return all pooled limb blocks to the OS now that computation is done.
            ' _displayNativePtr is NOT in the pool (GMP never calls GmpFreeFunc on
            ' the mpz_get_str result buffer) so it remains valid for display/verify.
            GmpNativeAlloc_Flush()  ' §30: native pool flush (replaced managed FlushGmpPool)
        End Try
    End Function

    ' ════════════════════════════════════════════════════════════════════════
    '  Display helpers
    ' ════════════════════════════════════════════════════════════════════════

    Private Sub StreamPiToScreen(piString As String)
        Dim digitCount As Long = If(_displayNativePtr <> IntPtr.Zero, _displayNativeLen, CLng(piString.Length))
        LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Streaming started")
        WriteToLog($"Streaming started ({digitCount:N0} digits)")

        ' Fast path: display is off — skip the timer loop entirely and go straight
        ' to file write (if requested), then re-enable the Compute button.
        If Not ChkboxDisplay.Checked Then
            BtnCompute.Enabled = True
            BtnPause.Enabled = False
            Timer1.Stop()
            LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Display skipped")
            WriteResultToFile(digitCount)
            If _displayNativePtr = IntPtr.Zero Then displayStr = Nothing
            ' §82 auto-verify: run when display is off and checkbox is checked.
            If ChkAutoVerify.Checked Then RunVerification() Else Eta_Finalize($"Done {stopWatch.Elapsed:hh\:mm\:ss}")   ' §116/§126
            Return
        End If

        ' §271 (#98): large native result → MOVABLE WINDOW instead of streaming the whole thing into
        ' the RichTextBox (~O(n²) + GB of text).  Write the file + verify immediately (the result is
        ' ready), then show a bounded, slider-navigable window over the full digit range.
        If _displayNativePtr <> IntPtr.Zero AndAlso (digitCount - 1L) > CLng(NAV_WINDOW_DIGITS) Then
            displayTimer.Enabled = False
            BtnCompute.Enabled = True
            BtnPause.Enabled = False
            Timer1.Stop()
            WriteResultToFile(digitCount)
            If ChkAutoVerify.Checked Then RunVerification() Else Eta_Finalize($"Done {stopWatch.Elapsed:hh\:mm\:ss}")   ' §116/§126
            SetupNavWindow(digitCount - 1L)   ' totalDigits = digitCount − 1 (exclude the null terminator)
            LblStatus.Text = $"Done! {digitCount - 1L:N0} digits — drag the slider to navigate."
            LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Window ready ({digitCount - 1L:N0} digits)")
            Return
        End If

        displayTimer.Enabled = False
        RtbPiDigits.Clear()
        LblDigitsDisplayed.Text = "0"
        LblStatus.Text = $"Streaming {digitCount:N0} digits..."
        displayStr = piString   ' empty string in native mode — display reads from _displayNativePtr
        displayIdx = 0
        displayTotal = 0
        _displayChunkSize = 4096       ' §81: reset adaptive chunk size for new stream
        _displayScrollAccum = 0        ' §81: reset scroll throttle counter
        displayTimer.Enabled = True
    End Sub

    ' §271 (#98): build (once) + show the movable digit-window UI for a large native result.
    ' A TrackBar docked under RtbPiDigits scrubs a NAV_WINDOW_DIGITS-wide window over [0, totalDigits);
    ' each move re-reads that slice from the native buffer (bounded memory, O(window) per move).  The
    ' RichTextBox's own scrollbar scrolls within the current window.  Full output stays in pi_digits.txt.
    Private Sub SetupNavWindow(totalDigits As Long)
        _navTotalDigits = totalDigits
        If _navTrackBar Is Nothing Then
            _navLabel = New System.Windows.Forms.Label() With {
                .Dock = DockStyle.Bottom, .Height = 22, .TextAlign = ContentAlignment.MiddleLeft,
                .BackColor = Color.Black, .ForeColor = Color.Lime, .Font = New Font("Consolas", 9)}
            _navTrackBar = New System.Windows.Forms.TrackBar() With {
                .Dock = DockStyle.Bottom, .Minimum = 0, .Maximum = NAV_TRACKBAR_STEPS,
                .TickStyle = System.Windows.Forms.TickStyle.None, .SmallChange = 1, .LargeChange = 100}
            AddHandler _navTrackBar.Scroll, AddressOf NavWindowChanged
            Dim _parent As Control = RtbPiDigits.Parent
            _parent.Controls.Add(_navLabel)
            _parent.Controls.Add(_navTrackBar)
            _navLabel.BringToFront()
            _navTrackBar.BringToFront()
            RtbPiDigits.WordWrap = True
        End If
        _navTrackBar.Visible = True : _navLabel.Visible = True
        _navTrackBar.Value = 0
        _navTrackBar.Enabled = (totalDigits > NAV_WINDOW_DIGITS)
        ShowNavWindow(0L)
    End Sub

    Private Sub NavWindowChanged(sender As Object, e As EventArgs)
        Dim maxOffset As Long = System.Math.Max(0L, _navTotalDigits - NAV_WINDOW_DIGITS)
        Dim off As Long = CLng(_navTrackBar.Value) * maxOffset \ CLng(NAV_TRACKBAR_STEPS)
        ShowNavWindow(off)
    End Sub

    ' Re-fill RtbPiDigits with the NAV_WINDOW_DIGITS-wide slice of the native buffer at `offset`.
    Private Sub ShowNavWindow(offset As Long)
        If _displayNativePtr = IntPtr.Zero OrElse _navTotalDigits <= 0L Then Return
        _navOffset = System.Math.Max(0L, System.Math.Min(offset, System.Math.Max(0L, _navTotalDigits - 1L)))
        Dim count As Integer = CInt(System.Math.Min(CLng(NAV_WINDOW_DIGITS), _navTotalDigits - _navOffset))
        If count <= 0 Then Return
        Dim buf(count - 1) As Byte
        Runtime.InteropServices.Marshal.Copy(New IntPtr(_displayNativePtr.ToInt64() + _navOffset), buf, 0, count)
        RtbPiDigits.Text = System.Text.Encoding.ASCII.GetString(buf, 0, count)
        _navLabel.Text = $"Digits {_navOffset + 1:N0}–{_navOffset + count:N0} of {_navTotalDigits:N0}   —   drag slider to move the window; full result in pi_digits.txt"
        LblDigitsDisplayed.Text = $"{_navTotalDigits:N0}"
    End Sub

    ''' <summary>
    ''' Writes the computed Pi digits to outputFile (if ChkboxWriteToFile is checked),
    ''' or just updates the status label.  Called from both the display-timer completion
    ''' path and the fast path when display is turned off.
    ''' </summary>
    Private Sub WriteResultToFile(digitCount As Long)
        If ChkboxWriteToFile.Checked Then
            LblStatus.Text = "Writing to file..."
            Try
                If Not System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(outputFile)) Then
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputFile))
                End If
                Dim _expectedLen As Long
                If _displayNativePtr <> IntPtr.Zero Then
                    ' Stream the native char buffer to file in 1 MB chunks.
                    ' Insert decimal point after the first digit ("3" → "3.").
                    Using fs As New System.IO.FileStream(outputFile, System.IO.FileMode.Create, System.IO.FileAccess.Write)
                        fs.WriteByte(Runtime.InteropServices.Marshal.ReadByte(_displayNativePtr, 0))
                        fs.WriteByte(Asc("."c))
                        Const FILE_CHUNK As Integer = 1024 * 1024
                        Dim buf(FILE_CHUNK - 1) As Byte
                        Dim written As Long = 1
                        While written < _displayNativeLen
                            Dim toWrite As Integer = CInt(System.Math.Min(FILE_CHUNK, _displayNativeLen - written))
                            Runtime.InteropServices.Marshal.Copy(
                                New IntPtr(_displayNativePtr.ToInt64() + written), buf, 0, toWrite)
                            fs.Write(buf, 0, toWrite)
                            written += toWrite
                        End While
                    End Using
                    _expectedLen = _displayNativeLen + 1L   ' leading digit + "." + (len−1) digits
                Else
                    System.IO.File.WriteAllText(outputFile, displayStr)
                    _expectedLen = CLng(displayStr.Length)
                End If
                ' §117 (#117): confirm the file actually persisted at the expected size.  The verify
                ' step scans the in-memory buffer, so a truncated write (e.g. disk full mid-stream)
                ' would otherwise pass verify while the on-disk pi_digits.txt is partial.
                Dim _actualLen As Long = New System.IO.FileInfo(outputFile).Length
                If _actualLen <> _expectedLen Then
                    Throw New System.IO.IOException($"file size {_actualLen:N0} ≠ expected {_expectedLen:N0} bytes (truncated/partial write)")
                End If
                _saveFailed = False : _saveErrorMsg = ""
                WriteToLog($"[WriteResultToFile] saved {_actualLen:N0} bytes to {outputFile}" & vbCrLf, 1)   ' §117
                LblStatus.Text = $"Done! Saved to {outputFile}"
            Catch ex As Exception
                _saveFailed = True : _saveErrorMsg = ex.Message                                              ' §117
                WriteToLog($"[WriteResultToFile] SAVE FAILED: {ex.Message}" & vbCrLf, 1)                      ' §117
                LblStatus.Text = "File save error: " & ex.Message
            End Try
        Else
            _saveFailed = False : _saveErrorMsg = ""                                                         ' §117: no save attempted
            LblStatus.Text = $"Done! {digitCount:N0} digits computed."
        End If
    End Sub

    ' §117 (#117): prepend any file-save failure to the verify status so the in-memory verify result
    ' cannot clobber it.  Makes explicit that the verify ran against the in-memory buffer, not the file.
    Private Function ComposeVerifyStatus(verifySummary As String) As String
        If _saveFailed Then Return $"File save FAILED: {_saveErrorMsg} | (in-memory) {verifySummary}"
        Return verifySummary
    End Function

    Private Sub DisplayTimer_Tick(sender As Object, e As EventArgs) Handles displayTimer.Tick
        Dim useNative As Boolean = (_displayNativePtr <> IntPtr.Zero)
        Dim totalLen As Integer = CInt(If(useNative, _displayNativeLen, CLng(displayStr.Length)))

        If displayIdx >= totalLen Then
            displayTimer.Enabled = False
            LblStatus.Text = $"Done! {displayTotal:N0} digits displayed."
            BtnCompute.Enabled = True
            BtnPause.Enabled = False
            Timer1.Stop()

            LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Streaming complete")
            WriteToLog("Streaming complete")

            WriteResultToFile(CLng(totalLen))

            If useNative Then
                ' Leave _displayNativePtr alive so BtnTest_Click can search it directly.
                ' The buffer is freed when a new computation starts (BtnCompute_Click).
                WriteToLog("[DisplayTimer] streaming complete — native pi buffer retained for Verify")
            Else
                displayStr = Nothing
                WriteToLog("[DisplayTimer] displayStr released (LOH block freed)")
            End If

            ' §82 auto-verify: run immediately after streaming completes if checkbox is checked.
            If ChkAutoVerify.Checked Then RunVerification()
            Return
        End If

        ' §81 Display streaming: adaptive chunk size targets ~80 ms of UI work per tick.
        ' Native path uses Marshal.Copy bulk copy + Encoding.ASCII.GetString (one P/Invoke
        ' per tick instead of one per byte).  Managed path unchanged (rare fallback).
        Dim tickSw As New Diagnostics.Stopwatch()
        tickSw.Start()

        Dim chunkEnd As Integer = System.Math.Min(displayIdx + _displayChunkSize, totalLen)
        Dim appendText As String = ""

        If useNative Then
            Dim sb As System.Text.StringBuilder = Nothing
            ' First tick: prepend "3." then bulk-copy the rest of the chunk.
            If displayIdx = 0 Then
                sb = New System.Text.StringBuilder(_displayChunkSize + 2)
                sb.Append(ChrW(Runtime.InteropServices.Marshal.ReadByte(_displayNativePtr, 0)))
                sb.Append("."c)
                displayIdx = 1
                chunkEnd = System.Math.Min(displayIdx + _displayChunkSize, totalLen)
            End If

            Dim count As Integer = chunkEnd - displayIdx
            If count > 0 Then
                ' Grow the reusable byte buffer if the adaptive chunk size has outgrown it.
                If count > _displayBuf.Length Then
                    ReDim _displayBuf(count - 1)
                End If
                Runtime.InteropServices.Marshal.Copy(
                    New IntPtr(_displayNativePtr.ToInt64() + displayIdx), _displayBuf, 0, count)
                ' Detect early null terminator (wrong-result / short buffer guard).
                Dim nullPos As Integer = Array.IndexOf(_displayBuf, CByte(0), 0, count)
                If nullPos >= 0 Then
                    count = nullPos
                    displayIdx = totalLen   ' signal completion on next tick
                Else
                    displayIdx += count
                End If
                Dim decoded As String = System.Text.Encoding.ASCII.GetString(_displayBuf, 0, count)
                If sb IsNot Nothing Then
                    sb.Append(decoded)
                    appendText = sb.ToString()
                Else
                    appendText = decoded
                End If
            ElseIf sb IsNot Nothing Then
                appendText = sb.ToString()
            End If
        Else
            ' Managed fallback: character-by-character (piString is only used for small/test runs).
            Dim chunk As New System.Text.StringBuilder(_displayChunkSize)
            While displayIdx < chunkEnd
                Dim ch As Char = displayStr(displayIdx)
                If Char.IsDigit(ch) OrElse ch = "."c Then chunk.Append(ch)
                displayIdx += 1
            End While
            appendText = chunk.ToString()
        End If

        If appendText.Length > 0 Then
            displayTotal += appendText.Length
            RtbPiDigits.AppendText(appendText)
            ' §81 scroll throttle: only call ScrollToCaret every 10,000 chars to avoid
            ' a layout pass on every tick.
            _displayScrollAccum += appendText.Length
            If _displayScrollAccum >= 10000 Then
                RtbPiDigits.SelectionStart = RtbPiDigits.TextLength
                RtbPiDigits.ScrollToCaret()
                _displayScrollAccum = 0
            End If
            LblDigitsDisplayed.Text = $"{displayTotal:N0}"
        End If

        ' §81 adaptive chunk size: grow if tick finished under 60 ms, shrink if over 90 ms.
        ' Capped at 1,000,000 to prevent a single tick freezing the UI thread.
        tickSw.Stop()
        Dim tickMs As Long = tickSw.ElapsedMilliseconds
        If tickMs < 60 AndAlso _displayChunkSize < 1000000 Then
            _displayChunkSize = CInt(System.Math.Min(CLng(_displayChunkSize) * 2L, 1000000L))
        ElseIf tickMs > 90 AndAlso _displayChunkSize > 256 Then
            _displayChunkSize = System.Math.Max(_displayChunkSize \ 2, 256)
        End If
    End Sub

    Private Sub Timer1_Tick(sender As Object, e As EventArgs) Handles Timer1.Tick
        Dim span As TimeSpan = stopWatch.Elapsed
        LblRunningTime.Text = Format(span.Days, "000") & "." &
                              Format(span.Hours, "00") & ":" &
                              Format(span.Minutes, "00") & ":" &
                              Format(span.Seconds, "00") & "." &
                              Format(span.Milliseconds, "000")
    End Sub

    ''' <summary>
    ''' Runs all built-in and custom verifications against the computed Pi digits.
    ''' Results are written to LblStatus and the phase log — no modal dialogs.
    ''' Called by BtnTest_Click (on demand) and automatically after streaming
    ''' completes when ChkAutoVerify is checked.
    ''' </summary>
    Private Sub RunVerification()
        Dim parts As New System.Collections.Generic.List(Of String)()
        Dim allOk As Boolean = True

        If _displayNativePtr <> IntPtr.Zero Then
            ' §75 (issue #75): at 5B+ scale we cannot route through Marshal.PtrToStringAnsi
            ' because the resulting managed String would exceed the CLR's 2^31-1 char limit
            ' and throw ArgumentException("string must be null-terminated").  Scan the native
            ' byte buffer directly at the known-good positions.  O(needle) per check instead
            ' of O(n) full-buffer materialisation, and no managed allocation.
            Dim totalLen As Long = _displayNativeLen
            WriteToLog($"[Verify] native pi buffer scanned at known positions (len={totalLen:N0}, buffer retained for display)")

            Dim _999999 As Byte() = New Byte() {&H39, &H39, &H39, &H39, &H39, &H39}
            If NativeMatchAt(_999999, _displayNativePtr, totalLen, 762L) Then
                parts.Add("999999@762 OK")
            Else
                parts.Add("999999@762 FAIL")
                allOk = False
            End If

            Dim _777777777 As Byte() = New Byte() {&H37, &H37, &H37, &H37, &H37, &H37, &H37, &H37, &H37}
            If totalLen > 24658610L Then
                If NativeMatchAt(_777777777, _displayNativePtr, totalLen, 24658601L) Then
                    parts.Add("777777777@24,658,601 OK")
                Else
                    parts.Add("777777777@24,658,601 FAIL")
                    allOk = False
                End If
            Else
                parts.Add("777777777 not checked (need 24.66M+ digits)")
            End If

            ' §29: nine-9s replaces e-digits check (e-digits don't appear until 45B+ digits).
            ' Native buffer has no '.' prefix, so digit-stream position == buffer offset:
            ' buffer[0]='3' and buffer[n] = n-th decimal digit (1-indexed).
            Dim _nine9s As Byte() = New Byte() {&H39, &H39, &H39, &H39, &H39, &H39, &H39, &H39, &H39}
            If totalLen > 564665215L Then
                If NativeMatchAt(_nine9s, _displayNativePtr, totalLen, 564665206L) Then
                    parts.Add("nine-9s@564,665,206 OK")
                Else
                    parts.Add("nine-9s@564,665,206 FAIL")
                    allOk = False
                End If
            Else
                parts.Add("nine-9s not checked (need 564M+ digits)")
            End If

            Dim summary As String = ComposeVerifyStatus(If(allOk, "Verify OK: ", "Verify: ") & String.Join(" | ", parts))   ' §117
            LblStatus.Text = summary
            WriteToLog("[Verify] " & summary, 1)   ' §252 (#95): verify outcome = level 1 (result)
            Eta_Finalize($"Done {stopWatch.Elapsed:hh\:mm\:ss} — " & If(_saveFailed, "SAVE FAILED ✗", If(allOk, "Verified ✓", "Verify FAIL ✗")))   ' §116/§126

            If _verifyAt.Count > 0 OrElse _verifyContains.Count > 0 Then
                RunCustomVerificationsNative(_displayNativePtr, totalLen)
            End If
            Return
        End If

        ' Small-scale interactive path: no native buffer (display-disabled fast path was not
        ' used or buffer already released), fall back to the managed RtbPiDigits text.
        Dim piText As String = RtbPiDigits.Text.Replace(".", "").Replace(vbCrLf, "")

        Dim pos1 As Integer = piText.IndexOf("999999")
        If pos1 = 762 Then
            parts.Add("999999@762 OK")
        ElseIf pos1 >= 0 Then
            parts.Add($"999999 at {pos1} (expected 762) FAIL")
            allOk = False
        Else
            parts.Add("999999 not found")
            allOk = False
        End If

        Dim pos2 As Integer = piText.IndexOf("777777777")
        If pos2 = 24658601 Then
            parts.Add("777777777@24,658,601 OK")
        ElseIf pos2 >= 0 Then
            parts.Add($"777777777 at {pos2} (expected 24658601) FAIL")
            allOk = False
        Else
            parts.Add("777777777 not found")
        End If

        Dim pos3 As Integer = piText.IndexOf("999999999")
        If pos3 = 564665206 Then
            parts.Add("nine-9s@564,665,206 OK")
        ElseIf pos3 >= 0 Then
            parts.Add($"nine-9s at {pos3} (expected 564665206) FAIL")
            allOk = False
        Else
            parts.Add("nine-9s not found (need 564M+ digits)")
        End If

        Dim summary2 As String = ComposeVerifyStatus(If(allOk, "Verify OK: ", "Verify: ") & String.Join(" | ", parts))   ' §117
        LblStatus.Text = summary2
        WriteToLog("[Verify] " & summary2, 1)   ' §252 (#95): verify outcome = level 1 (result)
        Eta_Finalize($"Done {stopWatch.Elapsed:hh\:mm\:ss} — " & If(_saveFailed, "SAVE FAILED ✗", If(allOk, "Verified ✓", "Verify FAIL ✗")))   ' §116/§126

        If _verifyAt.Count > 0 OrElse _verifyContains.Count > 0 Then
            RunCustomVerifications(piText)
        End If
    End Sub

    ' §75 (issue #75): native-buffer scan helpers.  Used by RunVerification and the custom-
    ' verify path when _displayNativePtr is alive (any run that streams from the GMP/§216
    ' char buffer — i.e., any run large enough to matter).  Avoid Marshal.PtrToStringAnsi
    ' which throws at >2 GB.

    Private Function NativeMatchAt(needle As Byte(), nativePtr As IntPtr, totalLen As Long, position As Long) As Boolean
        If position < 0 OrElse position + CLng(needle.Length) > totalLen Then Return False
        Dim base64 As Long = nativePtr.ToInt64() + position
        For i As Integer = 0 To needle.Length - 1
            If Runtime.InteropServices.Marshal.ReadByte(New IntPtr(base64 + CLng(i)), 0) <> needle(i) Then Return False
        Next
        Return True
    End Function

    ' Chunked byte-buffer search.  1 MB scan window with (needle-1) overlap so a match
    ' straddling a chunk boundary still hits.  Returns first match offset, or -1.
    Private Function NativeIndexOf(needle As Byte(), nativePtr As IntPtr, totalLen As Long) As Long
        If needle.Length = 0 Then Return 0
        If CLng(needle.Length) > totalLen Then Return -1
        Const SCAN_CHUNK As Integer = 1024 * 1024
        Dim overlap As Integer = needle.Length - 1
        Dim buf(SCAN_CHUNK + overlap - 1) As Byte
        Dim firstByte As Byte = needle(0)
        Dim pos As Long = 0
        While pos < totalLen
            Dim toRead As Integer = CInt(System.Math.Min(CLng(SCAN_CHUNK + overlap), totalLen - pos))
            Runtime.InteropServices.Marshal.Copy(New IntPtr(nativePtr.ToInt64() + pos), buf, 0, toRead)
            Dim scanEnd As Integer
            If pos + CLng(toRead) >= totalLen Then
                scanEnd = toRead - needle.Length + 1
            Else
                scanEnd = toRead - overlap
            End If
            For i As Integer = 0 To scanEnd - 1
                If buf(i) = firstByte Then
                    Dim match As Boolean = True
                    For j As Integer = 1 To needle.Length - 1
                        If buf(i + j) <> needle(j) Then
                            match = False
                            Exit For
                        End If
                    Next
                    If match Then Return pos + CLng(i)
                End If
            Next
            pos += CLng(SCAN_CHUNK)
        End While
        Return -1
    End Function

    ' §75 (issue #75): native equivalent of RunCustomVerifications.  Same semantics, byte-
    ' range based, no managed String materialisation.
    Private Sub RunCustomVerificationsNative(nativePtr As IntPtr, totalLen As Long)
        For Each chk In _verifyAt
            Dim digits As String = chk.Item1
            Dim expectedPos As Long = chk.Item2
            Dim needle As Byte() = System.Text.Encoding.ASCII.GetBytes(digits)
            Dim ok As Boolean = NativeMatchAt(needle, nativePtr, totalLen, expectedPos)
            Dim msg As String = $"[verify-at] '{digits}' at {expectedPos:N0}: {If(ok, "OK", "FAIL")}"
            WriteToLog(msg)
            LblStatus.Text = msg
        Next

        For Each needleText In _verifyContains
            Dim needle As Byte() = System.Text.Encoding.ASCII.GetBytes(needleText)
            Dim pos As Long = NativeIndexOf(needle, nativePtr, totalLen)
            Dim msg As String = If(pos >= 0, $"[verify-contains] '{needleText}' at {pos:N0} OK", $"[verify-contains] '{needleText}' NOT FOUND")
            WriteToLog(msg)
            LblStatus.Text = msg
        Next
    End Sub

    ''' <summary>
    ''' Runs --verify-at and --verify-contains checks supplied on the command line.
    ''' Results are written to LblStatus and the phase log — no modal dialogs.
    ''' </summary>
    Private Sub RunCustomVerifications(piText As String)
        For Each chk In _verifyAt
            Dim digits As String = chk.Item1
            Dim expectedPos As Long = chk.Item2
            Dim actualPos As Long = CLng(piText.IndexOf(digits))
            Dim msg As String
            If actualPos >= 0 Then
                msg = $"[verify-at] '{digits}' at {actualPos} (expected {expectedPos}): {If(actualPos = expectedPos, "OK", "FAIL")}"
            Else
                msg = $"[verify-at] '{digits}' NOT FOUND (expected at {expectedPos})"
            End If
            WriteToLog(msg)
            LblStatus.Text = msg
        Next

        For Each needle In _verifyContains
            Dim pos As Long = CLng(piText.IndexOf(needle))
            Dim msg As String = If(pos >= 0, $"[verify-contains] '{needle}' at {pos} OK", $"[verify-contains] '{needle}' NOT FOUND")
            WriteToLog(msg)
            LblStatus.Text = msg
        Next
    End Sub

    Private Sub BtnTest_Click(sender As Object, e As EventArgs) Handles BtnTest.Click
        RunVerification()
    End Sub

    Private Sub TxtDigitsofPI_TextChanged(sender As Object, e As EventArgs) Handles TxtDigitsofPI.TextChanged
        Dim cursorPos As Integer = TxtDigitsofPI.SelectionStart
        Dim rawText As String = TxtDigitsofPI.Text.Replace(",", "")
        Dim digits As Long
        If Long.TryParse(rawText, digits) Then
            Dim formatted As String = digits.ToString("N0")
            If TxtDigitsofPI.Text <> formatted Then
                TxtDigitsofPI.Text = formatted
                Dim newPos As Integer = cursorPos + (formatted.Length - rawText.Length)
                If newPos < 0 Then newPos = 0
                If newPos > formatted.Length Then newPos = formatted.Length
                TxtDigitsofPI.SelectionStart = newPos
            End If
        End If
    End Sub

End Class
