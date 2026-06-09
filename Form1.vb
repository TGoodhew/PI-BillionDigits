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
    ' §281 (#123): chunked-grid cell DOP override for the --test-cgdopscan core-headroom probe.
    ' 0 = production default (env PI_CG_DOP, §282 capped at ProcessorCount + wave-balanced).  >0 sets
    ' an EXACT raw DOP and bypasses §282 wave-balancing, so the probe can measure each DOP cleanly.
    ' Only the benchmark ever writes it.
    Private Shared _cgDopOverride As Integer = 0

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
    Private Shared _testCgDopScan As Boolean = False ' §281 (#123): chunked-grid DOP-headroom sweep
    Private Shared _testGridScan As Boolean = False  ' §265 (#88): split-factor (k×k) experiment
    Private Shared _testCellSweep As Boolean = False ' §266 (#88): cell-size sweep at 5B operand sizes
    Private Shared _testRecipConv As Boolean = False ' §272 (#88): reciprocal-Newton convergence probe
    ' §276 (#125): how often the reciprocal-Newton loop writes its mid-iteration resume checkpoint
    ' (nr_r.bin).  It was written EVERY iteration — a full-width ~2 GB serialize on the compute thread
    ' (~33×2 GB ≈ 66 GB at 5B) that saturated the disk and stalled compute under low availPhys (#125).
    ' nr_r.bin is purely a resume point (the result + snap_Phase3 are independent), so saving every Nth
    ' iteration cuts the I/O ~N× and cannot change the computed π; a crash loses ≤ N−1 (~1–2 min each)
    ' iterations of recompute.  Default 4; PI_NR_CKPT_EVERY overrides (1 = old every-iteration behavior).
    Private Shared _nrCkptEvery As Integer = 4
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
    ' it parallelises at PI_CG_DOP (§282: default ProcessorCount, wave-balanced) at low RAM — same reason
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
    ' §111 (#111): bytes per MB, for readability where a size is divided to report MB.  All ~57 former
    ' `\ 1048576[L]` / `/ 1048576[.0|L]` MB-conversion sites and the 1 MB pool-trim threshold args now
    ' use this constant.  (The few `N * 1024 * 1024` buffer-SIZE constants are intentionally left as the
    ' explicit multiplication — they are `As Integer` and reading "16 * 1024 * 1024" as 16 MB is clear.)
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
            AppendLog($"[Trim§241 ctx={ctx}] pooled-before={pooledBefore / BYTES_PER_MB:N0} MB ({blocksBefore:N0} blks) | freed {freedBuffers:N0} blks {freedBytes / 1073741824.0:N3} GB (minBucket={minBytes / BYTES_PER_MB:N0} MB) | WS {wsBefore \ BYTES_PER_MB:N0} -> {wsAfter \ BYTES_PER_MB:N0} MB{vbCrLf}")
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
            GmpNativeAlloc_Trim(CULng(BYTES_PER_MB), _freed, _bytes)
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
                Case "--test-cgdopscan"
                    _testCgDopScan = True     ' §281 (#123): chunked-grid DOP-headroom sweep, then exit
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

        ' §118/§119 (#118/#119): add the large-run UI controls (log-level dropdown, AutoCheckpoint +
        ' Auto-OK checkboxes, output-dir field).  Interactive only; no-op headless.
        BuildLargeRunControls()

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
        If _testMulHigh OrElse _testChunkedGrid OrElse _testEta OrElse _testAdvisor OrElse _testDopScan OrElse _testCgDopScan OrElse _testGridScan OrElse _testCellSweep OrElse _testRecipConv Then
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
                        ElseIf _testCgDopScan Then
                            tag = "TestCgDopScan" : If _statusHook IsNot Nothing Then _statusHook("Running --test-cgdopscan…")
                            ok = TestCgDopScan()                        ' §281 (#123)
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
            If Not _headless AndAlso Not _suppressDialogs Then   ' §119
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
            "Available RAM: " & (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes \ BYTES_PER_MB).ToString() & "MB" & vbCrLf &
            "GMP DLL: " & gmpDllPath & vbCrLf &
            "GMP Memory: System allocator (default)"
        If Not _headless AndAlso Not _suppressDialogs Then   ' §119
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
            Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
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
        Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
        Dim virtMem As Long = Process.GetCurrentProcess().VirtualMemorySize64 \ BYTES_PER_MB
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
            "    --test-advisor --test-dopscan --test-cgdopscan --test-gridscan --test-cellsweep --test-recipconv" & vbCrLf &
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

    ' §118/§119 (#118/#119): build the large-run UI controls programmatically (kept out of the fragile
    ' auto-generated Designer).  Opens a 4th panel row with: a DESCRIBED log-level dropdown (replacing
    ' the opaque 0–5 spinner, and reconciling the interactive default to 2), an AutoCheckpoint checkbox
    ' (was CLI-only — a UI user could not enable crash-resume), an "Auto-OK dialogs" checkbox (#119),
    ' and an output-directory field + Browse.  Interactive only — headless runs drive these via flags,
    ' and creating/driving the controls headlessly could clobber CLI-set values.
    Private Sub BuildLargeRunControls()
        If _headless Then Return
        Try
            Dim f As New Font("Segoe UI", 14.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
            Const rowY As Integer = 212
            Panel1.Height = System.Math.Max(Panel1.Height, 275)   ' RtbPiDigits (Dock=Fill) reflows automatically

            ' Log-level dropdown — replaces the opaque NudLogLevel spinner.  It writes back into
            ' NudLogLevel.Value, which BtnCompute_Click still reads, so no other code changes.  Default 2
            ' (fixes the latent Designer=1 vs runtime=2 mismatch).
            LblLogLevel.Visible = False : NudLogLevel.Visible = False
            Dim lblLL As New Label() With {.AutoSize = True, .Font = f, .Location = New Point(25, rowY + 4), .Text = "Log level:"}
            Dim cmbLL As New ComboBox() With {.Font = f, .Location = New Point(190, rowY), .Size = New Size(560, 40),
                                              .DropDownStyle = ComboBoxStyle.DropDownList, .Name = "CmbLogLevel"}
            cmbLL.Items.AddRange(New Object() {
                "0 — Silent (errors + crashes only)", "1 — Errors + result", "2 — Phase milestones (default)",
                "3 — Sub-phase trace", "4 — Detailed (mul diagnostics)", "5 — Allocator/affinity (very large)"})
            cmbLL.SelectedIndex = 2 : NudLogLevel.Value = 2D
            AddHandler cmbLL.SelectedIndexChanged, Sub(s, ev) NudLogLevel.Value = CDec(cmbLL.SelectedIndex)
            Panel1.Controls.Add(lblLL) : Panel1.Controls.Add(cmbLL)

            ' AutoCheckpoint checkbox — was CLI-only (no UI control existed).
            Dim chkAC As New CheckBox() With {.AutoSize = True, .Font = f, .Location = New Point(800, rowY + 2),
                                              .Text = "Auto-checkpoint (resume)", .Checked = _autoCheckpoint, .UseVisualStyleBackColor = True}
            AddHandler chkAC.CheckedChanged, Sub(s, ev) _autoCheckpoint = chkAC.Checked
            TipMain.SetToolTip(chkAC, "Snapshot each combine level; an interrupted run auto-resumes from the last level next time. (Auto-enabled for runs ≥ 100M digits.)")
            Panel1.Controls.Add(chkAC)

            ' §119: Auto-OK dialogs — log + auto-dismiss spontaneous info/error modals so an unattended
            ' interactive run never blocks (sets the same _suppressDialogs the global handler honours).
            Dim chkOK As New CheckBox() With {.AutoSize = True, .Font = f, .Location = New Point(1230, rowY + 2),
                                              .Text = "Auto-OK dialogs (unattended)", .Checked = _suppressDialogs, .UseVisualStyleBackColor = True}
            AddHandler chkOK.CheckedChanged, Sub(s, ev) _suppressDialogs = chkOK.Checked
            TipMain.SetToolTip(chkOK, "Log + auto-dismiss spontaneous info/error dialogs (incl. mid-run OOM and the global unhandled-exception modal) so an unattended run never blocks. Close/Cancel confirmations still prompt — never auto-confirmed.")
            Panel1.Controls.Add(chkOK)

            ' Output directory field + Browse (was CLI-only).
            Dim lblOut As New Label() With {.AutoSize = True, .Font = f, .Location = New Point(1700, rowY + 4), .Text = "Output:"}
            Dim txtOut As New TextBox() With {.Font = f, .Location = New Point(1820, rowY), .Size = New Size(820, 39), .Text = _outputDir, .Name = "TxtOutputDir"}
            AddHandler txtOut.TextChanged, Sub(s, ev) If txtOut.Text.Trim().Length > 0 Then _outputDir = txtOut.Text.Trim()
            Dim btnBrowse As New Button() With {.Font = f, .Location = New Point(2655, rowY - 2), .Size = New Size(130, 44), .Text = "Browse", .UseVisualStyleBackColor = True}
            AddHandler btnBrowse.Click, Sub(s, ev)
                                            Try
                                                Using fb As New FolderBrowserDialog()
                                                    fb.SelectedPath = _outputDir
                                                    If fb.ShowDialog() = DialogResult.OK AndAlso fb.SelectedPath.Length > 0 Then txtOut.Text = fb.SelectedPath
                                                End Using
                                            Catch
                                            End Try
                                        End Sub
            Panel1.Controls.Add(lblOut) : Panel1.Controls.Add(txtOut) : Panel1.Controls.Add(btnBrowse)
        Catch
            ' UI is best-effort — never block a run on control setup.
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
                                  If Not _headless AndAlso Not _suppressDialogs Then   ' §119: Auto-OK ⇒ log + exit (Else)
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
                                  If Not _headless AndAlso Not _suppressDialogs Then   ' §119: Auto-OK ⇒ log + exit (Else)
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
                                  If Not _headless AndAlso Not _suppressDialogs Then   ' §119: Auto-OK ⇒ log + exit (Else)
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
        If accBuf = IntPtr.Zero Then Throw New OutOfMemoryException($"SafeMpzMul_ChunkedGrid: PoolGet {accBytes \ BYTES_PER_MB} MB failed")
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
        ' chunked beats §gen at 5B.  DOP from env PI_CG_DOP, default ProcessorCount (§282: capped at
        ' ProcessorCount, then wave-balanced below — was a flat 16).
        Dim _cgDop As Integer = Environment.ProcessorCount
        Dim _cgEnv As String = Environment.GetEnvironmentVariable("PI_CG_DOP")
        Dim _cgParsed As Integer
        If _cgEnv IsNot Nothing AndAlso Integer.TryParse(_cgEnv, _cgParsed) AndAlso _cgParsed >= 1 Then _cgDop = _cgParsed
        ' §282 (#123 follow-up): cap at the host's core count (was a flat 16 — arbitrary, and on a
        ' 24-core box it left cells under-parallelised).  The §281 probe showed real throughput past
        ' DOP 16; wave-balancing just below trims a high cap to the fewest EVEN waves so it never
        ' creates a wasteful short tail wave.  Adapts to the host (scales with ProcessorCount).
        _cgDop = System.Math.Max(1, System.Math.Min(_cgDop, Environment.ProcessorCount))
        ' §281 (#123): the cgdopscan probe overrides DOP (and bypasses §282 balancing) to measure
        ' each RAW DOP cleanly.  Never set on the production path (override = 0).
        If _cgDopOverride > 0 Then _cgDop = _cgDopOverride
        Dim nCells As Integer = cOff.Count
        ' §282: balance waves.  The grid runs cells in waves of _cgDop with a SERIAL accumulate
        ' barrier between waves, so wall-time tracks the WAVE COUNT, not just core count.  Greedy
        ' cap-sized waves leave a ragged tail (36 cells @ 16 ⇒ waves 16,16,4: 20 idle cores in the
        ' tail AND the same 3 barriers as DOP 12 — measured SLOWER than 12).  Packing into
        ' ceil(nCells/cap) EVEN waves removes the idle tail and minimises barriers (36 cells @ cap 24
        ' ⇒ 2 waves of 18, ~14% faster than ragged DOP-16 at the §281 benchmark).  Purely a
        ' scheduling change — the product is independent of _cgDop, so it stays bit-identical.
        ' Opt out via PI_CG_BALANCE=0.
        If nCells > 0 AndAlso _cgDopOverride = 0 AndAlso Environment.GetEnvironmentVariable("PI_CG_BALANCE") <> "0" Then
            Dim _nWaves As Integer = System.Math.Max(1, CInt(System.Math.Ceiling(nCells / CDbl(_cgDop))))
            _cgDop = System.Math.Max(1, CInt(System.Math.Ceiling(nCells / CDbl(_nWaves))))
        End If
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
        AppendLog($"[PreAlloc] {neededLimbs:N0} limbs ({neededBytes \ BYTES_PER_MB:N0} MB) OK{vbCrLf}")
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
    ''' <summary>
    ''' §280 (#113): the §233 numerator R-multiply pipeline, lifted VERBATIM out of ComputePiGMP
    ''' (orchestration extraction — no logic change).  Loads r0/r1/r2 from the Phase-3 checkpoint if
    ''' present, else computes r_i = gmpNumer*Q_i at the §233 scale-aware DOP (or via the §274
    ''' chunked grid), saving each on completion; clears finalQ/mpQ1/mpQ2 when done, as before.
    ''' </summary>
    Private Sub ComputeNumeratorRMultiplies(mpR0 As mpz_t, mpR1 As mpz_t, mpR2 As mpz_t, gmpNumer As mpz_t, finalQ As mpz_t, mpQ1 As mpz_t, mpQ2 As mpz_t, p3SnapDir As String, numTerms As Long)
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
    End Sub


    ' ════════════════════════════════════════════════════════════════════════
    '  Main computation entry point
    ' ════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' §280 (#113): the final decimal-conversion stage, lifted VERBATIM out of ComputePiGMP
    ''' (orchestration extraction — no logic change).  Size-routes gmpPi through the §270 parallel /
    ''' §216 chunked / mpz_get_str converter, drives the status timer + §127 size-ETA, sets the
    ''' _displayNative* fields, and frees gmpPi (reinit'd to a 1-limb stub for the caller's Finally).
    ''' </summary>
    Private Sub ConvertResultToDecimal(gmpPi As mpz_t)
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
    End Sub

    ''' <summary>
    ''' §280 (#113): the in-memory final combine, lifted VERBATIM out of ComputePiGMP (orchestration
    ''' extraction — no logic change).  Reduces the node list to a single root (P,Q,T) by pairwise
    ''' P=lP·rP, Q=lQ·rQ, T=lT·rQ+lP·rT with early frees; usually a no-op (BinarySplitGMP returns 1).
    ''' </summary>
    Private Function FinalCombineNodes(nodes As List(Of Result)) As List(Of Result)
            ' ── Final in-memory combine (usually already 1 node from BinarySplitGMP) ─
            ' Issue #6: uses Result struct instead of Tuple(Of mpz_t,mpz_t,mpz_t).
            LogPhase($"Starting final combine of {nodes.Count} nodes...")
            Dim combineIteration As Integer = 0

            While nodes.Count > 1
                combineIteration += 1
                Dim memDuringCombine As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
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
        Return nodes
    End Function

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

            Dim memBefore As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
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

            Dim memAfterSplit As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
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

            nodes = FinalCombineNodes(nodes)

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
                    LogPhase($"[ComputePi§244] freed finalP early (~{_fpSz * 8L \ BYTES_PER_MB:N0} MB) — dead from Step 1 on; headroom for parallel Step 1/2")
                End If
            End If
            TrimPoolAtBoundary("pre-step1", CULng(BYTES_PER_MB))   ' §69/§243: max headroom before the floor decides DOP

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
                Dim _ramP_pre As Long = _procP_pre.WorkingSet64 \ BYTES_PER_MB
                Dim _vmP_pre As Long = _procP_pre.PrivateMemorySize64 \ BYTES_PER_MB
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
            ComputeNumeratorRMultiplies(mpR0, mpR1, mpR2, gmpNumer, finalQ, mpQ1, mpQ2, p3SnapDir, numTerms)
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
                Dim _ramP_post As Long = _procP_post.WorkingSet64 \ BYTES_PER_MB
                Dim _vmP_post As Long = _procP_post.PrivateMemorySize64 \ BYTES_PER_MB
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
                Dim _ramCombA As Long = _procCA.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombA As Long = _procCA.PrivateMemorySize64 \ BYTES_PER_MB
                WriteToLog($"[ComputePi] Combine A done (r2<<k)  RAM:{_ramCombA:N0}MB  Committed:{_vmCombA:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine A result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' Step B: reload r1; gmpNumer += r1  (~572 MB + ~390 MB → ~572 MB)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine B: mpz_init(mpR1) + deserialize")
            ' §61: mpR1 already in RAM from the parallel multiply — no disk reload.
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine B: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r1 ({CLng(gmp_lib.mpz_sizeinbase(mpR1, 2)):N0} bits)")
            If _logLevel >= 3 Then
                Dim _procCBpre = Process.GetCurrentProcess()
                Dim _ramCombBpre As Long = _procCBpre.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombBpre As Long = _procCBpre.PrivateMemorySize64 \ BYTES_PER_MB
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
                Dim _ramCombB As Long = _procCB.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombB As Long = _procCB.PrivateMemorySize64 \ BYTES_PER_MB
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
                Dim _ramCombC As Long = _procCC.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombC As Long = _procCC.PrivateMemorySize64 \ BYTES_PER_MB
                WriteToLog($"[ComputePi] Combine C done ((r2<<k+r1)<<k)  RAM:{_ramCombC:N0}MB  Committed:{_vmCombC:N0}MB")
            End If
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine C result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")

            ' Step D: reload r0; gmpNumer += r0  (~755 MB + ~390 MB → ~755 MB)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine D: mpz_init(mpR0) + deserialize")
            ' §61: mpR0 already in RAM from the parallel multiply — no disk reload.
            If _logLevel >= 4 Then WriteToLog($"[ComputePi] Combine D: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r0 ({CLng(gmp_lib.mpz_sizeinbase(mpR0, 2)):N0} bits)")
            If _logLevel >= 3 Then
                Dim _procCDpre = Process.GetCurrentProcess()
                Dim _ramCombDpre As Long = _procCDpre.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombDpre As Long = _procCDpre.PrivateMemorySize64 \ BYTES_PER_MB
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
                Dim _ramCombD As Long = _procCD.WorkingSet64 \ BYTES_PER_MB
                Dim _vmCombD As Long = _procCD.PrivateMemorySize64 \ BYTES_PER_MB
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
            TrimPoolAtBoundary("pre-conversion", CULng(BYTES_PER_MB))

            If token.IsCancellationRequested Then Return ""

            ConvertResultToDecimal(gmpPi)

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
