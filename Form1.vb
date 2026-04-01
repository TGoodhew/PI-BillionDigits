Option Strict On
Option Explicit On

' ── Logging detail level ─────────────────────────────────────────────────────
' LOGGING_DETAIL = 0  Major [PHASE] markers and exceptions only.
'                     Lowest I/O overhead; use for normal production runs.
'
' LOGGING_DETAIL = 1  Detail on the last combine level and all ComputePiGMP
'                     steps only.  (RECOMMENDED for crash diagnosis — captures
'                     the high-value final operations without logging every
'                     intermediate level, which would slow the run noticeably.)
'
' LOGGING_DETAIL = 2  Per-operation trace on every BinarySplitChunk call and
'                     every combine level.  Use only when debugging an early-
'                     level crash; generates very large log files.
' ────────────────────────────────────────────────────────────────────────────
#Const LOGGING_DETAIL = 1

Imports System.Numerics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Collections.Concurrent
Imports System.Runtime.InteropServices
Imports Math.Gmp.Native
Imports System.Diagnostics

Public Class Form1

    Private stopWatch As New Stopwatch()
    Private phaseStopWatch As New Stopwatch()
    Private cts As CancellationTokenSource
    Private DIGITS As Long
    ' Output directory: defaults to %LOCALAPPDATA%\PI-BillionDigits — always writable,
    ' no admin rights required, works on any machine regardless of drive letter.
    ' All output paths (digits file, log file, node cache) derive from this one field.
    Private Shared _outputDir As String = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PI-BillionDigits")
    Private ReadOnly Property outputFile As String
        Get
            Return System.IO.Path.Combine(_outputDir, "pi_digits.txt")
        End Get
    End Property
    Private displayStr As String = ""
    Private displayIdx As Integer = 0
    Private displayTotal As Long = 0
    ' Native streaming: pointer into the GMP-allocated char buffer + length.
    ' When non-zero, DisplayTimer_Tick reads bytes directly via Marshal.ReadByte
    ' instead of indexing displayStr, avoiding the ~1 GB managed string entirely.
    Private _displayNativePtr As IntPtr = IntPtr.Zero
    Private _displayNativeLen As Long = 0
    Private _displayNativeBufSize As Long = 0   ' GmpAllocFunc alloc size; >= GMP_LARGE_THRESHOLD → VirtualAlloc'd
    Private WithEvents displayTimer As New System.Windows.Forms.Timer()
    Private gmpC3Const As mpz_t = Nothing

    ' ── Headless / command-line mode ─────────────────────────────────────────
    ' Set by --autostart (suppress all dialogs) and --autoverify (run verify +
    ' Application.Exit after computation completes).
    Private _headless As Boolean = False
    Private _autoVerify As Boolean = False
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

    ' ── Thread-safe logging for GMP allocator callbacks ──────────────────────
    ' VirtualAlloc / VirtualFree / CRT malloc / CRT free are all intrinsically
    ' thread-safe.  Only the File.AppendAllText log writes need serialisation so
    ' that concurrent allocator callbacks from parallel worker threads don't race
    ' on the log file and lose entries (or silently throw IOException).
    Private Shared ReadOnly _logLock As New Object()

    Private Shared Sub AppendLog(message As String)
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
    ' GMP Custom Memory Pool - COMMENTED OUT - CAUSED MEMORY CORRUPTION
    ' The bump allocator violated GMP's memory contract causing crashes after
    ' ~300 iterations due to incompatible free/realloc semantics.
    ' Using system allocator instead (reliable and well-tested with GMP).
    ' ═══════════════════════════════════════════════════════════════════════

    '<DllImport("kernel32.dll", SetLastError:=True)>
    'Private Shared Function VirtualAlloc(lpAddress As IntPtr,
    '                                  dwSize As UIntPtr,
    '                                  flAllocationType As UInteger,
    '                                  flProtect As UInteger) As IntPtr
    'End Function

    '<DllImport("kernel32.dll", SetLastError:=True)>
    'Private Shared Function VirtualFree(lpAddress As IntPtr,
    '                                 dwSize As UIntPtr,
    '                                 dwFreeType As UInteger) As Boolean
    'End Function

    '<DllImport("kernel32.dll", EntryPoint:="RtlMoveMemory")>
    'Private Shared Sub CopyMemory(dest As IntPtr, src As IntPtr,
    '                           length As UIntPtr)
    'End Sub



    'Private Const MEM_RESERVE As UInteger = &H2000UI
    'Private Const MEM_COMMIT As UInteger = &H1000UI
    'Private Const MEM_RELEASE As UInteger = &H8000UI
    'Private Const PAGE_READWRITE As UInteger = &H4UI



    '' Pool state
    'Private _poolBase As IntPtr
    'Private _poolSize As ULong = 20UL * 1024UL * 1024UL * 1024UL  ' 20GB
    'Private _poolOffset As ULong

    '' Keep delegates alive - GC must NOT collect these!
    'Private _allocDel As allocate_function
    'Private _reallocDel As reallocate_function
    'Private _freeDel As free_function

    'Private Function AlignUp(value As ULong, alignment As ULong) As ULong
    '    Return (value + alignment - 1UL) And Not (alignment - 1UL)
    'End Function

    'Private Function GmpAlloc(size As size_t) As void_ptr
    '    Dim needed As ULong = AlignUp(CULng(size), 16UL)
    '    If _poolOffset + needed > _poolSize Then
    '        Throw New OutOfMemoryException(
    '        $"GMP pool exhausted! Used {_poolOffset \ (1024UL * 1024UL * 1024UL)}GB")
    '    End If
    '    Dim result As IntPtr = IntPtr.Add(_poolBase, CInt(_poolOffset))  ' BUG: CInt overflows at 2GB!
    '    _poolOffset += needed
    '    Return New void_ptr(result)
    'End Function

    'Private Function GmpRealloc(ptr As void_ptr,
    '                          old_size As size_t,
    '                          new_size As size_t) As void_ptr
    '    Dim newPtr As void_ptr = GmpAlloc(new_size)
    '    If ptr.ToIntPtr() <> IntPtr.Zero Then
    '        Dim copyBytes As UIntPtr = New UIntPtr(
    '        System.Math.Min(CULng(old_size), CULng(new_size)))
    '        CopyMemory(newPtr.ToIntPtr(), ptr.ToIntPtr(), copyBytes)
    '    End If
    '    Return newPtr  ' BUG: Old pointer becomes dangling - GMP expects free to mark it reusable
    'End Function

    'Private Sub GmpFree(ptr As void_ptr, size As size_t)
    '    ' Bump allocator - no-op
    '    ' BUG: GMP expects freed memory to be tracked/reusable, causing metadata corruption
    'End Sub

    'Private Sub InitGmpPool()
    '    ...commented out...
    'End Sub

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
                    ' Hybrid CPU — restrict process to P-cores
                    If SetProcessAffinityMask(GetCurrentProcess(), New IntPtr(pCoreMask)) Then
                        AppendLog($"[Affinity] Hybrid CPU detected. P-core mask=0x{pCoreMask:X}  E-core mask=0x{eCoreMask:X}. Process restricted to P-cores.{vbCrLf}")
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

    ''' <summary>Returns floor(log2(sz)) clamped to [0, POOL_BUCKETS-1].</summary>
    Private Shared Function PoolBucket(sz As Long) As Integer
        If sz <= 1L Then Return 0
        Dim b As Integer = 0
        Dim v As Long = sz - 1L   ' -1 so exact powers of 2 map to their own bucket
        While v > 0L
            v = v >> 1
            b += 1
        End While
        Return System.Math.Min(b, POOL_BUCKETS - 1)
    End Function

    Private Shared Function PoolGet(sz As Long) As IntPtr
        If sz > 0L AndAlso sz <= POOL_MAX_BLOCK Then
            Dim b As Integer = PoolBucket(sz)
            Dim ptr As IntPtr
            If _gmpPool(b).TryPop(ptr) Then Return ptr
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
            If _gmpPool(b).Count < POOL_CAP Then
                _gmpPool(b).Push(ptr)
                Return
            End If
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

    Private Shared Function GmpAllocFunc(alloc_size As size_t) As void_ptr
        Try
        Dim rawSz As ULong = CULng(alloc_size)
        If rawSz > CULng(Long.MaxValue) Then
            ' Size > 9.2 EB — clearly corrupted GMP internal state.
            ' Return null so GMP will abort cleanly; native crash handler logs it.
            AppendLog($"[GmpAllocFunc] CORRUPT SIZE ({rawSz}) — returning null{vbCrLf}")
            Return New void_ptr(IntPtr.Zero)
        End If
        Dim sz As Long = CLng(rawSz)
        If sz >= GMP_LARGE_THRESHOLD Then
            Dim ptr As IntPtr = PoolGet(sz)
            If ptr = IntPtr.Zero Then
                AppendLog($"[GmpAlloc] PoolGet({sz:N0} bytes) FAILED — GMP will abort{vbCrLf}")
            End If
            Return New void_ptr(ptr)
        End If
        Return _savedGmpAlloc(alloc_size)
        Catch ex As Exception
            AppendLog($"[GmpAllocFunc] EXCEPTION ({ex.GetType().Name}): {ex.Message} — returning null{vbCrLf}")
            Return New void_ptr(IntPtr.Zero)
        End Try
    End Function

    Private Shared Function GmpReallocFunc(old_ptr As void_ptr,
                                            old_size As size_t,
                                            new_size As size_t) As void_ptr
        Try
        Dim rawOld As ULong = CULng(old_size)
        Dim rawNew As ULong = CULng(new_size)
        If rawOld > CULng(Long.MaxValue) OrElse rawNew > CULng(Long.MaxValue) Then
            AppendLog($"[GmpReallocFunc] CORRUPT SIZE (old={rawOld}, new={rawNew}) — returning null{vbCrLf}")
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
                AppendLog($"[GmpRealloc] large→large PoolGet({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}")
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
                AppendLog($"[GmpRealloc] small→large PoolGet({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}")
            End If
        Else
            ' large → small: CRT-alloc new block, pool-return old block
            Dim newVoid As void_ptr = _savedGmpAlloc(new_size)
            newP = newVoid.ToIntPtr()
            If newP <> IntPtr.Zero Then
                If copyBytes.ToUInt64() > 0UL Then CopyMemory(newP, oldP, copyBytes)
                PoolReturn(oldP, oldSz)
            Else
                AppendLog($"[GmpRealloc] large→small CRT alloc({newSz:N0} bytes) FAILED (old={oldSz:N0}) — GMP will abort{vbCrLf}")
            End If
        End If

        Return New void_ptr(newP)
        Catch ex As Exception
            AppendLog($"[GmpReallocFunc] EXCEPTION ({ex.GetType().Name}): {ex.Message} — returning null{vbCrLf}")
            Return New void_ptr(IntPtr.Zero)
        End Try
    End Function

    Private Shared Sub GmpFreeFunc(ptr As void_ptr, size As size_t)
        Try
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
        Catch ex As Exception
            AppendLog($"[GmpFreeFunc] EXCEPTION ({ex.GetType().Name}): {ex.Message} — leaking ptr{vbCrLf}")
        End Try
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

    ' §42: Raw P/Invoke to libgmp-10.dll — bypass mpz_t wrapper entirely for accumulation.
    ' Math.Gmp.Native corrupts mpz_t.Pointer for locally-scoped mpz_t objects during
    ' recursive SafeMpzMul calls, even for objects not passed to inner calls.  Using plain
    ' IntPtr (Marshal.AllocHGlobal) for the accumulator and saved _sv_xxx IntPtrs for
    ' product/shifted avoids the corruption entirely.
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

    Private Sub InitGmpVirtualAllocFunctions()
        ' Initialise pool buckets (fixed array — no ConcurrentDictionary overhead).
        For b As Integer = 0 To POOL_BUCKETS - 1
            _gmpPool(b) = New ConcurrentStack(Of IntPtr)()
        Next

        ' Step 1: Force gmp_lib's static initializer to run NOW, while the native
        ' GMP table still points to the default CRT malloc/realloc/free.
        ' gmp_lib initializes lazily (first access).  If it runs AFTER our thunks
        ' are installed it would read our thunk pointers into allocate_func_ptr, and
        ' .NET 10's GetDelegateForFunctionPointer would return our allocate_function
        ' delegate instead of creating _allocate_function_x64 — crashing on the cast.
        ' Calling mp_get_memory_functions here is the cleanest trigger; it also gives
        ' us the saved CRT delegates we need for small-allocation fallback.
        gmp_lib.mp_get_memory_functions(_savedGmpAlloc, _savedGmpRealloc, _savedGmpFree)

        ' Step 2: Install our thunks ONLY in GMP's native function pointer table.
        ' Math.Gmp.Native's allocate_func_ptr lambda is already set (from step 1)
        ' and captures the original CRT malloc IntPtr — it will NOT be touched here.
        ' So gmp_lib.allocate / mpz_t.Initializing / mpz_init continue to use CRT
        ' malloc normally for managed-side __mpz_struct allocations.
        _gmpAlloc = New allocate_function(AddressOf GmpAllocFunc)
        _gmpRealloc = New reallocate_function(AddressOf GmpReallocFunc)
        _gmpFree = New free_function(AddressOf GmpFreeFunc)
        GmpSetMemoryFunctionsNative(
            Marshal.GetFunctionPointerForDelegate(_gmpAlloc),
            Marshal.GetFunctionPointerForDelegate(_gmpRealloc),
            Marshal.GetFunctionPointerForDelegate(_gmpFree))
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
                vbCrLf)
        Catch
        End Try
        Return 0   ' EXCEPTION_CONTINUE_SEARCH — let Windows handle it (WER, minidump, etc.)
    End Function

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' ── Parse command-line arguments ─────────────────────────────────────
        ' Supported flags:
        '   --digits N       Set the digit count (no commas required)
        '   --autostart      Suppress all dialogs and auto-begin computation
        '   --autoverify     After computation, auto-run verify + exit
        '   --threshold N    Override the RAM/disk threshold (nodes)
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
            End Select
            i += 1
        Loop

        LblStatus.Text = "Ready"
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

        ' Restrict process affinity to P-cores on hybrid CPUs (Intel 12th gen+,
        ' AMD Zen 4c).  E-cores run GMP arithmetic ~30-50% slower and cause
        ' cache-topology mismatches in parallel workloads.
        SetPCoreAffinity()

        ' Install VirtualAlloc/VirtualFree custom GMP allocator so large limb
        ' buffers are immediately decommitted on free, preventing commit-charge
        ' accumulation that caused abort() in multi-pass multiply.
        InitGmpVirtualAllocFunctions()

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
        TipMain.SetToolTip(BtnTest,
            "Search the computed Pi digits for known sequences to verify correctness: " &
            "999999 at position 762, 777777777 at position 24,658,601, and the first digits of e (27182818284).")
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
    Private Sub WriteToLog(message As String)
        Try
            Dim elapsed As TimeSpan = stopWatch.Elapsed
            Dim threadId As Integer = Thread.CurrentThread.ManagedThreadId
            Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            AppendLog($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | T{threadId} | {elapsed:hh\:mm\:ss\.fff} | RAM:{procMem:N0}MB | {message}" & vbCrLf)
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
            WriteToLog(sb.ToString())
        Catch
        End Try
    End Sub

    Private Sub LogPhase(phaseName As String)
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
        BtnCompute.Enabled = False
        BtnPause.Enabled = True

        ' Capture threshold from UI (or keep CLI-supplied value in headless mode).
        _diskThreshold = CInt(NudRamThreshold.Value)

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
#If LOGGING_DETAIL = 2 Then
            Dim loggingMode As String = "FULL DETAIL (every level + BinarySplitChunk)"
#ElseIf LOGGING_DETAIL = 1 Then
            Dim loggingMode As String = "FINAL LEVEL DETAIL (last combine level + ComputePiGMP)"
#Else
            Dim loggingMode As String = "MAJOR PHASES ONLY"
#End If
            System.IO.File.WriteAllText(LOG_FILE,
                $"=== PI Computation Started {DateTime.Now} ===" & vbCrLf &
                $"=== Digits: {DIGITS:N0} ===" & vbCrLf &
                $"=== Logging: {loggingMode} ===" & vbCrLf)
        Catch
        End Try
        RtbPiDigits.AppendText("Starting computation..." & vbCrLf)
        Dim computeThread As New System.Threading.Thread(
            Sub()
                Try
                    Dim result As String = ComputePiGMP(DIGITS, cts.Token)
                    If _displayNativePtr <> IntPtr.Zero OrElse result <> "" Then
                        Me.Invoke(Sub() StreamPiToScreen(result))
                    End If
                    ' --autoverify: run verify logic headlessly then exit.
                    ' Requires both _headless AND _autoVerify so interactive runs
                    ' never auto-exit even if --autoverify was somehow received.
                    If _headless AndAlso _autoVerify Then
                        Me.Invoke(Sub()
                                      BtnTest_Click(Nothing, EventArgs.Empty)
                                      Application.Exit()
                                  End Sub)
                    End If
                Catch oex As OutOfMemoryException
                    WriteExceptionToLog("ComputeThread/OutOfMemoryException", oex)
                    Me.Invoke(Sub()
                                  If Not _headless Then
                                      MessageBox.Show("OUT OF MEMORY!" & vbCrLf & oex.Message & vbCrLf & oex.StackTrace)
                                  Else
                                      WriteToLog("[DIALOG] OUT OF MEMORY: " & oex.Message)
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
                                      WriteToLog("[DIALOG] OVERFLOW: " & ovex.Message)
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
                                      WriteToLog("[DIALOG] EXCEPTION: " & ex.GetType().Name & ": " & ex.Message)
                                  End If
                                  LblStatus.Text = "Error: " & ex.Message
                                  BtnCompute.Enabled = True
                                  BtnPause.Enabled = False
                                  Timer1.Stop()
                              End Sub)
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

    Private Sub BinarySplitChunk(a As Long, b As Long,
                          ByRef Pab As mpz_t,
                          ByRef Qab As mpz_t,
                          ByRef Tab As mpz_t)
#If LOGGING_DETAIL = 2 Then
        WriteToLog($"[BinarySplitChunk] Enter  a={a:N0}  b={b:N0}  terms={b - a:N0}")
#End If

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
                        gmp_lib.mpz_set_ui(res.P, 1UI)
                        gmp_lib.mpz_set_ui(res.Q, 1UI)
                        gmp_lib.mpz_set_ui(res.T, 13591409UI)
                    Else
                        Dim aBig As New mpz_t()
                        Dim t1 As New mpz_t()
                        Dim t2 As New mpz_t()
                        gmp_lib.mpz_inits(aBig, t1, t2, Nothing)
                        gmp_lib.mpz_set_si(aBig, CInt(currentWorkItem.a))

                        gmp_lib.mpz_mul_ui(t1, aBig, 6UI)
                        gmp_lib.mpz_sub_ui(t1, t1, 5UI)
                        gmp_lib.mpz_mul_ui(t2, aBig, 2UI)
                        gmp_lib.mpz_sub_ui(t2, t2, 1UI)
                        gmp_lib.mpz_mul(res.P, t1, t2)
                        gmp_lib.mpz_mul_ui(t1, aBig, 6UI)
                        gmp_lib.mpz_sub_ui(t1, t1, 1UI)
                        gmp_lib.mpz_mul(res.P, res.P, t1)

                        gmp_lib.mpz_pow_ui(res.Q, aBig, 3UI)
                        gmp_lib.mpz_mul(res.Q, res.Q, gmpC3Const)

                        gmp_lib.mpz_mul_ui(t1, aBig, 545140134UI)
                        gmp_lib.mpz_add_ui(t1, t1, 13591409UI)
                        gmp_lib.mpz_mul(res.T, res.P, t1)
                        If (currentWorkItem.a And 1L) = 1L Then gmp_lib.mpz_neg(res.T, res.T)

                        gmp_lib.mpz_clears(aBig, t1, t2, Nothing)
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

                    gmp_lib.mpz_mul(res.P, leftRes.P, rightRes.P)
                    gmp_lib.mpz_mul(res.Q, leftRes.Q, rightRes.Q)
                    gmp_lib.mpz_mul(tempA, leftRes.T, rightRes.Q)
                    gmp_lib.mpz_mul(tempB, leftRes.P, rightRes.T)
                    gmp_lib.mpz_add(res.T, tempA, tempB)

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
#If LOGGING_DETAIL = 2 Then
        WriteToLog($"[BinarySplitChunk] Exit   a={a:N0}  b={b:N0}  stackPeak={maxDepth}")
#End If
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
    Private Sub SerializeNodeToDisk(p As mpz_t, q As mpz_t, t As mpz_t, filePath As String,
                                    Optional detailLog As Boolean = True)
#If LOGGING_DETAIL = 2 Then
        WriteToLog($"[Serialize] Writing {System.IO.Path.GetFileName(filePath)}")
#ElseIf LOGGING_DETAIL = 1 Then
        If detailLog Then WriteToLog($"[Serialize] Writing {System.IO.Path.GetFileName(filePath)}")
#End If
        Dim staging(4194303) As Byte  ' 4 MB staging buffer (§56) — reused for all three fields
        Try
            Using fs As New FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(p, bw, staging)
                    SerializeOneMpz(q, bw, staging)
                    SerializeOneMpz(t, bw, staging)
                End Using
            End Using
#If LOGGING_DETAIL = 2 Then
            Dim fileSize As Long = New FileInfo(filePath).Length
            WriteToLog($"[Serialize] Done   {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
#ElseIf LOGGING_DETAIL = 1 Then
            If detailLog Then
                Dim fileSize As Long = New FileInfo(filePath).Length
                WriteToLog($"[Serialize] Done   {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
            End If
#End If
        Catch ex As Exception
            WriteExceptionToLog($"SerializeNodeToDisk({filePath})", ex)
            LogPhase($"Error serializing node to {filePath}: {ex.Message}")
            Throw
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
    Private Sub SerializeOneMpz(val As mpz_t, bw As BinaryWriter, staging As Byte())
        ' Read _mp_size from the native __mpz_struct (Int32 at byte offset 4).
        ' Positive = positive number, negative = negative number.
        Dim mpSize As Integer = Marshal.ReadInt32(val.Pointer, 4)
        bw.Write(mpSize)
        If mpSize = 0 Then Return
        Dim limbCount As Long = CLng(System.Math.Abs(mpSize))
        Dim byteCount As Long = limbCount * 8L
        ' Read _mp_d (pointer to the limb array) at byte offset 8.
        Dim mpD As IntPtr = Marshal.ReadIntPtr(val.Pointer, 8)
#If LOGGING_DETAIL >= 1 Then
        If byteCount > 400L * 1024L * 1024L Then
            AppendLog(
                $"[SerializeOneMpz] large: _mp_size={mpSize:N0} byteCount={byteCount:N0}{vbCrLf}")
        End If
#End If
        ' Stream raw limb bytes in 64 KB chunks using the SOH staging buffer.
        ' No intermediate allocation needed — data is read straight from _mp_d.
        Dim remaining As Long = byteCount
        Dim offset As Long = 0L
        While remaining > 0
            Dim chunkSize As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
            Marshal.Copy(IntPtr.Add(mpD, CInt(offset)), staging, 0, chunkSize)
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
#If LOGGING_DETAIL = 2 Then
        Dim fileSize As Long = If(System.IO.File.Exists(filePath), New FileInfo(filePath).Length, -1)
        WriteToLog($"[Deserialize] Loading {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
#ElseIf LOGGING_DETAIL = 1 Then
        If detailLog Then
            Dim fileSize As Long = If(System.IO.File.Exists(filePath), New FileInfo(filePath).Length, -1)
            WriteToLog($"[Deserialize] Loading {System.IO.Path.GetFileName(filePath)}  size={fileSize \ 1024:N0}KB")
        End If
#End If

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
#If LOGGING_DETAIL = 2 Then
        WriteToLog($"[Deserialize] Done {System.IO.Path.GetFileName(filePath)}")
#ElseIf LOGGING_DETAIL = 1 Then
        If detailLog Then WriteToLog($"[Deserialize] Done {System.IO.Path.GetFileName(filePath)}")
#End If
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
    Private Sub DeserializeOneMpz(val As mpz_t, br As BinaryReader, staging As Byte())
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
            While remaining > 0
                Dim toRead As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
                Dim bytesRead As Integer = br.Read(staging, 0, toRead)
                If bytesRead <= 0 Then _
                    Throw New EndOfStreamException($"Unexpected end of stream in DeserializeOneMpz (small)")
                Marshal.Copy(staging, 0, IntPtr.Add(mpD, CInt(offset)), bytesRead)
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
            ' will see size >= GMP_LARGE_THRESHOLD and call VirtualFree —
            ' matching this VirtualAlloc.
            Dim limbs As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(byteCount)),
                                               MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
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
            While remaining > 0
                Dim toRead As Integer = CInt(System.Math.Min(remaining, CLng(staging.Length)))
                Dim bytesRead As Integer = br.Read(staging, 0, toRead)
                If bytesRead <= 0 Then _
                    Throw New EndOfStreamException($"Unexpected end of stream in DeserializeOneMpz (large)")
                Marshal.Copy(staging, 0, IntPtr.Add(limbs, CInt(offset)), bytesRead)
                offset += bytesRead
                remaining -= bytesRead
            End While
            Marshal.WriteInt32(val.Pointer, 4, mpSize)   ' set _mp_size now data is valid
        End If
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  Safe large-integer multiply (avoids GMP 32-bit mp_size_t overflow)
    ' ════════════════════════════════════════════════════════════════════════

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
        ' Threshold: pl * 64 must fit in int32. pl_max = floor((2^31-1)/64) = 33,554,431.
        Const SAFE_LIMB_THRESHOLD As Integer = 33_554_431

        Dim szA_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4)
        Dim szB_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4)
        Dim szA As Integer = System.Math.Abs(szA_signed)
        Dim szB As Integer = System.Math.Abs(szB_signed)

        If szA + szB <= SAFE_LIMB_THRESHOLD Then
#If LOGGING_DETAIL >= 1 Then
            If CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-PRE  szA={szA:N0} szB={szB:N0} | " &
                    $"opA.Ptr={opA.Pointer.ToInt64():X} opA_sz={szA_signed:N0} opA_d={Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8):X} " &
                    $"opB.Ptr={opB.Pointer.ToInt64():X} opB_sz={szB_signed:N0}{vbCrLf}")
            End If
#End If
            gmp_lib.mpz_mul(result, opA, opB)
#If LOGGING_DETAIL >= 1 Then
            If CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-POST result.Ptr={result.Pointer.ToInt64():X} result_sz={Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4):N0} " &
                    $"result_d={Runtime.InteropServices.Marshal.ReadInt64(result.Pointer, 8):X}{vbCrLf}")
            End If
#End If
            Return
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
        If _oldResultSz >= GMP_LARGE_THRESHOLD Then
            VirtualFree(_oldResultPtr, UIntPtr.Zero, MEM_RELEASE)
        Else
            _savedGmpFree(New void_ptr(_oldResultPtr), New size_t(CULng(_oldResultSz)))
        End If
        ' Blank result's struct _mp_alloc and _mp_size; _mp_d will hold the accumPtr stash (§44).
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, 0)
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, 0)

        ' §42: Allocate the large accumulation buffer and wire it into a raw accumPtr struct.
        ' Using Marshal.AllocHGlobal(16) for the struct header bypasses mpz_t wrapper
        ' corruption: Math.Gmp.Native cannot modify a plain IntPtr.
        Dim accumBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_resultBytes)),
                                              MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If accumBuf = IntPtr.Zero Then
            AppendLog(
                $"[SafeMpzMul] accum pre-alloc FAILED for {_resultBytes \ 1048576L:N0} MB — throwing OOM{vbCrLf}")
            Throw New OutOfMemoryException($"SafeMpzMul: VirtualAlloc failed for accum buffer ({_resultBytes \ 1048576L} MB)")
        End If
        Dim accumPtr As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 0, CInt(_resultLimbs)) ' _mp_alloc
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 4, 0)                  ' _mp_size = 0
        Runtime.InteropServices.Marshal.WriteInt64(accumPtr, 8, accumBuf.ToInt64()) ' _mp_d
        ' §44: stash accumPtr in result's own native CRT struct (_mp_d slot, offset +8).
        ' Inner calls use `prod` as their result — they never touch outer result's struct.
        ' This slot survives all native GMP calls and managed-stack corruption.
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, accumPtr.ToInt64())
#If LOGGING_DETAIL >= 2 Then
        AppendLog(
            $"[SafeMpzMul] accum pre-alloc OK: {_resultLimbs:N0} limbs ({_resultBytes \ 1048576L:N0} MB){vbCrLf}")
#End If

        ' Split opB into three pieces upfront: opB is small so all three pieces coexist cheaply.
        ' opA and opB are Q/P values from Chudnovsky binary split, always non-negative.
        Dim B0 As New mpz_t(), B1 As New mpz_t(), B2 As New mpz_t(), Btmp As New mpz_t()
        gmp_lib.mpz_inits(B0, B1, B2, Btmp, Nothing)
        gmp_lib.mpz_tdiv_r_2exp(B0, opB, New mp_bitcnt_t(CUInt(bitsB)))
        gmp_lib.mpz_tdiv_q_2exp(Btmp, opB, New mp_bitcnt_t(CUInt(bitsB)))
        gmp_lib.mpz_tdiv_r_2exp(B1, Btmp, New mp_bitcnt_t(CUInt(bitsB)))
        gmp_lib.mpz_tdiv_q_2exp(B2, Btmp, New mp_bitcnt_t(CUInt(bitsB)))
        gmp_lib.mpz_clears(Btmp, Nothing)
#If LOGGING_DETAIL >= 1 Then
        If CLng(szA) + CLng(szB) > 10_000_000L Then
            AppendLog(
                $"[SafeMpzMul] B-pieces | " &
                $"B0.Ptr={B0.Pointer.ToInt64():X} B0_sz={Runtime.InteropServices.Marshal.ReadInt32(B0.Pointer, 4):N0} " &
                $"B1.Ptr={B1.Pointer.ToInt64():X} B1_sz={Runtime.InteropServices.Marshal.ReadInt32(B1.Pointer, 4):N0} " &
                $"B2.Ptr={B2.Pointer.ToInt64():X} B2_sz={Runtime.InteropServices.Marshal.ReadInt32(B2.Pointer, 4):N0}{vbCrLf}")
        End If
#End If

        ' §59: Pre-extract all three A pieces upfront so all 9 sub-products can run in parallel.
        ' mpz_init2(bitsA) pre-allocates _mp_alloc >= mA before direct limb copies (A1, A2).
        Dim A0 As New mpz_t(), A1 As New mpz_t(), A2 As New mpz_t()
        gmp_lib.mpz_init2(A0, New mp_bitcnt_t(CUInt(bitsA)))
        gmp_lib.mpz_init2(A1, New mp_bitcnt_t(CUInt(bitsA)))
        gmp_lib.mpz_init2(A2, New mp_bitcnt_t(CUInt(bitsA)))

        ' A0 = opA mod 2^bitsA  (low mA limbs)
        gmp_lib.mpz_tdiv_r_2exp(A0, opA, New mp_bitcnt_t(CUInt(bitsA)))

        ' A1 = limbs [mA, 2*mA) — direct copy into pre-alloc'd buffer
        Dim _pre_opA_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
        Dim _A1_src As IntPtr = New IntPtr(_pre_opA_d + CLng(mA) * 8L)
        Dim _A1_dst As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(A1.Pointer, 8))
        CopyMemory(_A1_dst, _A1_src, New UIntPtr(CULng(mA) * 8UL))
        Dim _A1_sz As Integer = CInt(mA)
        Dim _A1_dstL As Long = _A1_dst.ToInt64()
        While _A1_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A1_dstL + CLng(_A1_sz - 1) * 8L)) = 0L
            _A1_sz -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A1.Pointer, 4, _A1_sz)

        ' A2 = limbs [2*mA, szA) — direct copy
        Dim _A2_limbs As Long = CLng(szA) - 2L * CLng(mA)
        Dim _A2_src As IntPtr = New IntPtr(_pre_opA_d + 2L * CLng(mA) * 8L)
        Dim _A2_dst As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(A2.Pointer, 8))
        CopyMemory(_A2_dst, _A2_src, New UIntPtr(CULng(_A2_limbs) * 8UL))
        Dim _A2_sz As Integer = CInt(_A2_limbs)
        Dim _A2_dstL As Long = _A2_dst.ToInt64()
        While _A2_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A2_dstL + CLng(_A2_sz - 1) * 8L)) = 0L
            _A2_sz -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A2.Pointer, 4, _A2_sz)

        Dim A_parts() As mpz_t = {A0, A1, A2}
        Dim B_parts() As mpz_t = {B0, B1, B2}

        ' Allocate one result buffer per sub-product (k = i*3 + j, k ∈ 0..8).
        Dim prods(8) As mpz_t
        For k As Integer = 0 To 8
            prods(k) = New mpz_t()
            gmp_lib.mpz_init(prods(k))
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
        Dim _smmDop As Integer = If(_safeMulDop > 0, _safeMulDop, Environment.ProcessorCount)
        If _smmDop <= 1 Then
            ' Serial path: no thread pool involvement, no park/unpark overhead.
            For k As Integer = 0 To 8
                SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
            Next k
        Else
            Dim _smm_opts As New System.Threading.Tasks.ParallelOptions() With {
                .MaxDegreeOfParallelism = _smmDop
            }
            Parallel.For(0, 9, _smm_opts, Sub(k As Integer)
                SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
            End Sub)
        End If

        ' §44: recover accumPtr from result's stash after Parallel.For.
        savedResultPtr = result.Pointer
        accumPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))

        ' Serial accumulation: shift each prod_k into its positional slot and add to accum.
        ' No inner SafeMpzMul calls — no §42/§44 managed-stack corruption in this loop.
        Dim shifted As New mpz_t()
        gmp_lib.mpz_init(shifted)
        For k As Integer = 0 To 8
            Dim ki As Integer = k \ 3
            Dim kj As Integer = k Mod 3
            Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
            Dim _sv_prod As IntPtr = prods(k).Pointer
            Dim _sv_shifted As IntPtr = shifted.Pointer
            If shiftBits = 0UL Then
                GmpRaw_add(accumPtr, accumPtr, _sv_prod)
            Else
                ' Pre-allocate shifted to the exact size needed; same sizing as the original
                ' per-j path. No inner SafeMpzMul calls here so no memory-pressure interaction.
                Dim _szProd As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_prod, 4)))
                Dim _shiftLimbs As Long = CLng(shiftBits) \ 64L + 1L
                Dim _neededLimbs As Long = _szProd + _shiftLimbs + 2L
                Dim _sj_buf As IntPtr = VirtualAlloc(IntPtr.Zero,
                                                     New UIntPtr(CULng(_neededLimbs * 8L)),
                                                     MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                If _sj_buf = IntPtr.Zero Then
                    VirtualFree(accumBuf, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
                    AppendLog(
                        $"[SafeMpzMul] shifted pre-alloc FAILED for {_neededLimbs * 8L \ 1048576L:N0} MB at k={k} — throwing OOM{vbCrLf}")
                    Throw New OutOfMemoryException($"SafeMpzMul: VirtualAlloc failed for shifted ({_neededLimbs * 8L \ 1048576L} MB)")
                End If
                Dim _sjOldBytes As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_sv_shifted, 0)) * 8L
                If _sjOldBytes >= GMP_LARGE_THRESHOLD Then
                    VirtualFree(New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_shifted, 8)), UIntPtr.Zero, MEM_RELEASE)
                Else
                    _savedGmpFree(New void_ptr(New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_shifted, 8))),
                                  New size_t(CULng(_sjOldBytes)))
                End If
                Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted, 0, CInt(_neededLimbs))
                Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted, 8, _sj_buf.ToInt64())
                If shiftBits <= CULng(UInt32.MaxValue) Then
                    GmpRaw_mul_2exp(_sv_shifted, _sv_prod, CUInt(shiftBits))
                Else
                    Dim _shift1 As ULong = shiftBits \ 2UL
                    Dim _shift2 As ULong = shiftBits - _shift1
                    GmpRaw_mul_2exp(_sv_shifted, _sv_prod, CUInt(_shift1))
                    GmpRaw_mul_2exp(_sv_shifted, _sv_shifted, CUInt(_shift2))
                End If
                GmpRaw_add(accumPtr, accumPtr, _sv_shifted)
                ' Free shifted's buffer; reinit for next k.
                VirtualFree(New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_shifted, 8)), UIntPtr.Zero, MEM_RELEASE)
                Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted, 8, 0L)
                gmp_lib.mpz_init(shifted)
            End If
            ' Free prod_k immediately after accumulation to limit peak RAM.
            gmp_lib.mpz_clear(prods(k))
        Next k
        ' §44: re-read accumPtr from result's stash after the serial accumulation loop.
        ' The accumulation loop contains no inner SafeMpzMul calls so locals are uncorrupted,
        ' but we re-read from the stash anyway for consistency with the §44 pattern.
        savedResultPtr = result.Pointer
        accumPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))

        ' Copy accumPtr struct to savedResultPtr, then free the 16-byte accumPtr header.
        ' accumBuf ownership transfers to result (via savedResultPtr._mp_d); do NOT free it here.
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 0))
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
        result.Pointer = savedResultPtr
        Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
        accumPtr = IntPtr.Zero
#If LOGGING_DETAIL >= 1 Then
        AppendLog(
            $"[SafeMpzMul] done: szA={szA:N0} szB={szB:N0} → {GmpRaw_sizeinbase(savedResultPtr, 10):N0} digits{vbCrLf}")
#End If

        ' §42: negate via raw P/Invoke so result.Pointer corruption cannot affect the call.
        If resultSign < 0 Then GmpRaw_neg(savedResultPtr, savedResultPtr)

        ' §59: prods(0..8) are already cleared inside the accumulation loop.
        ' shifted was mpz_init'd after its last use; A0/A1/A2 replaced A_part.
        gmp_lib.mpz_clears(shifted, A0, A1, A2, B0, B1, B2, Nothing)
        AppendLog(
            $"[SafeMpzMul] cleared: szA={szA:N0} szB={szB:N0}{vbCrLf}")
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
    Private Sub BinarySplitGMP(numTerms As Long,
                                ByRef nodes As List(Of Result))

        Const CHUNK_SIZE As Long = 512L
        Const STOP_AT As Long = 1L
        ' §73: threshold read from UI/CLI at compute start — adapts to available RAM.
        Dim DISK_THRESHOLD As Integer = _diskThreshold

        Dim numChunks As Long = (numTerms + CHUNK_SIZE - 1) \ CHUNK_SIZE

        ' Validate array size before allocation
        If numChunks > Integer.MaxValue Then
            Throw New OverflowException($"Too many chunks: {numChunks:N0} exceeds Integer.MaxValue ({Integer.MaxValue:N0})")
        End If

        LogPhase($"Processing {numChunks:N0} chunks of {CHUNK_SIZE} terms each (streaming to disk)...")

        ' Clear old cache
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
            End If
        Catch
        End Try

        ' Issue #4: List(Of DiskNode) now holds value types — no per-element heap allocation.
        Dim diskNodes As New List(Of DiskNode)()
        Dim currentSize As Long = numChunks
        Dim level As Integer = 0

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
        ' §54: single-file format — all Level-0 chunks written to one L0.bin.
        ' Eliminates 137K individual file creates/deletes (the ~2 min NVMe
        ' metadata overhead at the start of every run).  Each thread serializes
        ' to a MemoryStream outside the lock; only the file-position record and
        ' write happen under SyncLock, keeping lock hold-time minimal.
        Dim L0_BIN_PATH As String = DISK_CACHE_DIR & "L0.bin"
        Dim l0Stream As FileStream = Nothing
        Dim l0Lock As New Object()
        If numChunks > DISK_THRESHOLD Then
            l0Stream = New FileStream(L0_BIN_PATH, FileMode.Create, FileAccess.Write,
                                      FileShare.None, 4 * 1024 * 1024)
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
                BinarySplitChunk(chunkStart, chunkEnd, tempP, tempQ, tempT)

                Dim node As DiskNode
                node.FilePath = Nothing
                node.MemP = Nothing
                node.MemQ = Nothing
                node.MemT = Nothing
                node.Level = 0
                node.Index = CInt(i)
                node.IsInMemory = (numChunks <= DISK_THRESHOLD)

                If node.IsInMemory Then
                    node.MemP = tempP
                    node.MemQ = tempQ
                    node.MemT = tempT
                Else
                    ' Serialize to a MemoryStream first (no lock needed), then
                    ' write to the shared L0.bin under lock — recording the byte
                    ' offset so Phase 2 can seek directly to this chunk.
                    Dim stagingBuf(4194303) As Byte  ' 4 MB staging buffer (§56)
                    Using ms As New System.IO.MemoryStream()
                        Using bw As New System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen:=True)
                            SerializeOneMpz(tempP, bw, stagingBuf)
                            SerializeOneMpz(tempQ, bw, stagingBuf)
                            SerializeOneMpz(tempT, bw, stagingBuf)
                        End Using
                        gmp_lib.mpz_clears(tempP, tempQ, tempT, Nothing)
                        Dim chunkData As Byte() = ms.GetBuffer()
                        Dim chunkLen As Integer = CInt(ms.Length)
                        SyncLock l0Lock
                            node.FileOffset = l0Stream.Position
                            l0Stream.Write(chunkData, 0, chunkLen)
                        End SyncLock
                    End Using
                    node.FilePath = L0_BIN_PATH
                End If

                chunkResults(CInt(i)) = node

                Dim done As Long = Interlocked.Increment(completedChunks)
                If done Mod 5000L = 0L Then
                    WriteToLog($"[Phase1] {done:N0}/{numChunks:N0} chunks complete (parallel)")
                End If
            End Sub)

        phase1PollThread.Join()

        If l0Stream IsNot Nothing Then
            l0Stream.Flush()
            l0Stream.Dispose()
            l0Stream = Nothing
        End If

        diskNodes.AddRange(chunkResults)

        If currentSize > DISK_THRESHOLD Then
            LogPhase($"Parallel: {currentSize:N0} chunks streamed to L0.bin")
        Else
            LogPhase($"Parallel: {currentSize:N0} chunks computed in memory")
        End If

        ' ── Phase 2: combine levels until one node remains ──────────────────
        While currentSize > STOP_AT
            level += 1
            Dim nextSize As Long = (currentSize + 1) \ 2
            Dim nextDiskNodes As New List(Of DiskNode)()
            Dim useDisk As Boolean = nextSize > DISK_THRESHOLD
            ' True only for the final combine pass (2 nodes → 1).  Controls
            ' whether LOGGING_DETAIL=1 emits per-operation trace for this level.
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
                ' §69 revised (trace 5): Outer DOP = ProcessorCount\2. Each outer task calls
                ' Parallel.Invoke(newP, newQ) then Parallel.Invoke(tempA, tempB), requiring
                ' a free thread for the second Invoke task. With outer=ProcessorCount all
                ' threads are occupied by outer tasks — Parallel.Invoke deadlocks on itself
                ' causing Task.WaitAll to hit 19.33% in trace 5. ProcessorCount\2 leaves half
                ' the threads free so both Invoke tasks always run immediately in parallel.
                ' SafeMpzMul inner DOP remains 1 (serial) to avoid nested oversubscription.
                _safeMulDop = 1
                Dim _p2opts As New System.Threading.Tasks.ParallelOptions() With {
                    .MaxDegreeOfParallelism = System.Math.Max(1, Environment.ProcessorCount \ 2)
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

                        ' §60: newP and newQ use disjoint operands — run both simultaneously.
                        System.Threading.Tasks.Parallel.Invoke(
                            Sub() SafeMpzMul(newP, leftP, rightP),
                            Sub() SafeMpzMul(newQ, leftQ, rightQ))
                        gmp_lib.mpz_clears(rightP, Nothing)
                        gmp_lib.mpz_clears(leftQ, Nothing)

                        ' §60: tempA and tempB use disjoint operands — run both simultaneously.
                        System.Threading.Tasks.Parallel.Invoke(
                            Sub() SafeMpzMul(tempA, leftT, rightQ),
                            Sub() SafeMpzMul(tempB, leftP, rightT))
                        gmp_lib.mpz_clears(leftT, rightQ, Nothing)
                        gmp_lib.mpz_clears(leftP, rightT, Nothing)

                        gmp_lib.mpz_add(tempA, tempA, tempB)
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
                        Interlocked.Increment(completedPairs)
                    End Sub)
                phase2PollThread.Join()
                nextDiskNodes.AddRange(nextResults)
                ' §69: Restore full DOP for serial Phase 2 top levels and ComputePiGMP.
                _safeMulDop = Environment.ProcessorCount

            Else
                ' ── Serial path (top levels: few pairs, very large operands) ─
                Dim nodeIdx As Long = 0
                While nodeIdx < diskNodes.Count - 1

                    ' Load left operand
                    Dim leftP As mpz_t = Nothing
                    Dim leftQ As mpz_t = Nothing
                    Dim leftT As mpz_t = Nothing

                    If diskNodes(CInt(nodeIdx)).IsInMemory Then
                        leftP = diskNodes(CInt(nodeIdx)).MemP
                        leftQ = diskNodes(CInt(nodeIdx)).MemQ
                        leftT = diskNodes(CInt(nodeIdx)).MemT
                    Else
                        LoadNodeFromDisk(diskNodes(CInt(nodeIdx)).FilePath, diskNodes(CInt(nodeIdx)).FileOffset, leftP, leftQ, leftT, isLastLevel)
                        If diskNodes(CInt(nodeIdx)).Level > 0 Then
                            Try
                                System.IO.File.Delete(diskNodes(CInt(nodeIdx)).FilePath)
                            Catch
                            End Try
                        End If
                    End If

                    ' Load right operand
                    Dim rightP As mpz_t = Nothing
                    Dim rightQ As mpz_t = Nothing
                    Dim rightT As mpz_t = Nothing

                    If diskNodes(CInt(nodeIdx + 1)).IsInMemory Then
                        rightP = diskNodes(CInt(nodeIdx + 1)).MemP
                        rightQ = diskNodes(CInt(nodeIdx + 1)).MemQ
                        rightT = diskNodes(CInt(nodeIdx + 1)).MemT
                    Else
                        LoadNodeFromDisk(diskNodes(CInt(nodeIdx + 1)).FilePath, diskNodes(CInt(nodeIdx + 1)).FileOffset, rightP, rightQ, rightT, isLastLevel)
                        If diskNodes(CInt(nodeIdx + 1)).Level > 0 Then
                            Try
                                System.IO.File.Delete(diskNodes(CInt(nodeIdx + 1)).FilePath)
                            Catch
                            End Try
                        End If
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
                    ' Steps 1 & 2: newP = leftP*rightP and newQ = leftQ*rightQ are fully
                    ' independent (disjoint operands) — run both on the thread pool simultaneously.
                    ' WriteToLog (used in LOGGING_DETAIL) is thread-safe via SyncLock _logLock.
#If LOGGING_DETAIL >= 1 Then
                    If isTopLevel Then
                        Dim _szLP As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftP.Pointer, 4)
                        Dim _szRP As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightP.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul newP  leftP={System.Math.Abs(_szLP):N0} rightP={System.Math.Abs(_szRP):N0} limbs")
                        Dim _szLQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftQ.Pointer, 4)
                        Dim _szRQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightQ.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul newQ  leftQ={System.Math.Abs(_szLQ):N0} rightQ={System.Math.Abs(_szRQ):N0} limbs")
                    End If
#End If
                    System.Threading.Tasks.Parallel.Invoke(
                        Sub() SafeMpzMul(newP, leftP, rightP),
                        Sub() SafeMpzMul(newQ, leftQ, rightQ))
                    gmp_lib.mpz_clears(rightP, Nothing)             ' rightP done
                    gmp_lib.mpz_clears(leftQ, Nothing)              ' leftQ done

                    ' Steps 3 & 4: tempA = leftT*rightQ and tempB = leftP*rightT are fully
                    ' independent (disjoint operands) — run both on the thread pool simultaneously.
#If LOGGING_DETAIL >= 1 Then
                    If isTopLevel Then
                        Dim _szLT As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftT.Pointer, 4)
                        Dim _szRQ2 As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightQ.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul tempA  leftT={System.Math.Abs(_szLT):N0} rightQ={System.Math.Abs(_szRQ2):N0} limbs")
                        Dim _szLP2 As Integer = Runtime.InteropServices.Marshal.ReadInt32(leftP.Pointer, 4)
                        Dim _szRT As Integer = Runtime.InteropServices.Marshal.ReadInt32(rightT.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: mul tempB  leftP={System.Math.Abs(_szLP2):N0} rightT={System.Math.Abs(_szRT):N0} limbs")
                    End If
#End If
                    System.Threading.Tasks.Parallel.Invoke(
                        Sub() SafeMpzMul(tempA, leftT, rightQ),
                        Sub() SafeMpzMul(tempB, leftP, rightT))
                    gmp_lib.mpz_clears(leftT, rightQ, Nothing)      ' leftT, rightQ done
                    gmp_lib.mpz_clears(leftP, rightT, Nothing)      ' leftP, rightT done

#If LOGGING_DETAIL >= 1 Then
                    If isTopLevel Then
                        Dim _szTA As Integer = Runtime.InteropServices.Marshal.ReadInt32(tempA.Pointer, 4)
                        Dim _szTB As Integer = Runtime.InteropServices.Marshal.ReadInt32(tempB.Pointer, 4)
                        WriteToLog($"[Combine] L{level} N{nodeIdx\2}: add newT  tempA={System.Math.Abs(_szTA):N0} tempB={System.Math.Abs(_szTB):N0} limbs")
                    End If
#End If
                    gmp_lib.mpz_add(tempA, tempA, tempB)            ' T result in tempA's buffer
                    gmp_lib.mpz_clears(tempB, Nothing)              ' tempB done; tempA IS newT
#If LOGGING_DETAIL >= 1 Then
                    If isTopLevel Then WriteToLog($"[Combine] L{level} N{nodeIdx\2}: combine complete")
#End If

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
#If LOGGING_DETAIL >= 1 Then
                        If isTopLevel Then
                            Dim _preSerNewQ As Integer = Runtime.InteropServices.Marshal.ReadInt32(newQ.Pointer, 4)
                            WriteToLog($"[Combine] L{level} N{nodeIdx\2}: pre-serialize newQ._mp_size={_preSerNewQ:N0}")
                        End If
#End If
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
            FlushGmpPool()

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
    '  Main computation entry point
    ' ════════════════════════════════════════════════════════════════════════

    Private Function ComputePiGMP(digits As Long, token As CancellationToken) As String

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

            ' Issue #6: BinarySplitGMP now returns List(Of Result) — no Tuple allocations.
            Dim nodes As List(Of Result) = Nothing
            BinarySplitGMP(numTerms, nodes)

            LogPhase($"Binary Splitting complete ({nodes.Count} nodes)")

            Dim memAfterSplit As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
            LogPhase($"Memory after split: {memAfterSplit:N0}MB")

#If LOGGING_DETAIL >= 1 Then
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
#End If

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
                    gmp_lib.mpz_mul(newP, left.P, right.P)
                    gmp_lib.mpz_clears(right.P, Nothing)

                    gmp_lib.mpz_mul(newQ, left.Q, right.Q)
                    gmp_lib.mpz_clears(left.Q, Nothing)

                    gmp_lib.mpz_mul(tA, left.T, right.Q)
                    gmp_lib.mpz_clears(left.T, right.Q, Nothing)

                    gmp_lib.mpz_mul(tB, left.P, right.T)
                    gmp_lib.mpz_clears(left.P, right.T, Nothing)

                    gmp_lib.mpz_add(tA, tA, tB)    ' in-place: T result in tA's buffer
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

            Dim finalP As mpz_t = nodes(0).P
            Dim finalQ As mpz_t = nodes(0).Q
            Dim finalT As mpz_t = nodes(0).T

            gmp_lib.mpz_inits(gmpSqrtInput, gmpSqrt, gmpNumer, gmpPi, gmpOne, Nothing)
            gmpVariablesInitialized = True

#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] mpz_ui_pow_ui: 10^{digits:N0}")
#End If
            gmp_lib.mpz_ui_pow_ui(gmpOne, 10UI, CUInt(digits))
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] mpz_mul: gmpSqrtInput = gmpOne^2")
#End If
            SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)
            AppendLog(
                $"[DIAG] gmpSqrtInput after SafeMpzMul(gmpOne^2): {gmp_lib.mpz_sizeinbase(gmpSqrtInput, 10):N0} digits{vbCrLf}")
            ' gmpOne is no longer needed — free its ~208 MB buffer now so it is
            ' not held alive through the sqrt, numerator multiply, and division.
            ' Re-init to 0 so the Finally block can safely call mpz_clear on it.
            gmp_lib.mpz_clear(gmpOne)
            gmp_lib.mpz_init(gmpOne)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] gmpOne freed (early): RAM now lower before sqrt")
            WriteToLog($"[ComputePi] mpz_mul_ui: gmpSqrtInput *= 10005")
#End If
            gmp_lib.mpz_mul_ui(gmpSqrtInput, gmpSqrtInput, 10005UI)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] mpz_sqrt: sqrt({CLng(gmp_lib.mpz_sizeinbase(gmpSqrtInput, 10)):N0}-digit number)")
#End If
            gmp_lib.mpz_sqrt(gmpSqrt, gmpSqrtInput)
            gmp_lib.mpz_clear(gmpSqrtInput)
            LogPhase("Square root complete")

            If token.IsCancellationRequested Then Return ""

#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] mpz_mul_ui: gmpNumer = gmpSqrt * 426880")
#End If
            gmp_lib.mpz_mul_ui(gmpNumer, gmpSqrt, 426880UI)
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
            Dim finalT_spillPath As String = $"{DISK_CACHE_DIR}finalT_spill.bin"
            Dim stagingT(65535) As Byte
            Using fs As New FileStream(finalT_spillPath, FileMode.Create, FileAccess.Write)
                Using bw As New BinaryWriter(fs)
                    SerializeOneMpz(finalT, bw, stagingT)
                End Using
            End Using
            gmp_lib.mpz_clear(finalT)   ' free ~548 MB; will be reloaded below
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] gmpSqrt+finalP freed + finalT spilled: RAM before big multiply")
            WriteToLog($"[ComputePi] Three-pass multiply: splitting finalQ " &
                       $"(Q~{CLng(gmp_lib.mpz_sizeinbase(finalQ, 10)):N0} digits)")
#End If
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
            ' mp_bitcnt_t is 32-bit on Windows (MSVC unsigned long).  thirdBits ≈ 3 billion
            ' which fits in UInt32 (max 4.29 billion).  2*thirdBits would overflow UInt32, so
            ' all shifts use k1 only and are done in two single-k1 steps instead.
            Dim k1 As New mp_bitcnt_t(CUInt(thirdBits))
            WriteToLog($"[3PM-DBG] thirdBits={thirdBits:N0} k1.Value={k1.Value}")

            ' Shared staging buffer for all spill I/O (sequential, never concurrent).
            Dim spillStaging(4194303) As Byte  ' 4 MB staging buffer (§56) — finalT spill only

            ' finalQ._mp_size / k1-limb counts used for pre-alloc sizing below.
            Dim _finalQSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 4)))
            Dim _k1Limbs As Long = CLng(k1.Value) \ 64L   ' limb_cnt = k1 / GMP_NUMB_BITS

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
            Dim _tHBytes As Long = _tHNeeded * 8L
            Dim _tHBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_tHBytes)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _tHBuf <> IntPtr.Zero Then
                Dim _tHOld As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(tmpHigh.Pointer, 8))
                VirtualFree(_tHOld, UIntPtr.Zero, MEM_RELEASE)  ' free the init2 seed buffer
                Runtime.InteropServices.Marshal.WriteInt32(tmpHigh.Pointer, 0, CInt(_tHNeeded))
                Runtime.InteropServices.Marshal.WriteInt64(tmpHigh.Pointer, 8, _tHBuf.ToInt64())
                WriteToLog($"[3PM-DBG] tmpHigh pre-alloc {_tHNeeded:N0} limbs ({_tHBytes \ 1048576L:N0} MB) ptr={_tHBuf:X}")
            Else
                WriteToLog($"[3PM-DBG] tmpHigh pre-alloc FAILED for {_tHBytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
            End If
            WriteToLog($"[3PM-DBG] tmpHigh _mp_alloc={Runtime.InteropServices.Marshal.ReadInt32(tmpHigh.Pointer, 0):N0}  about to tdiv_q_2exp(tmpHigh, finalQ, k1)")
            gmp_lib.mpz_tdiv_q_2exp(tmpHigh, finalQ, k1)  ' tmpHigh = Q2*2^k + Q1
            WriteToLog($"[3PM-DBG] tdiv_q_2exp done: tmpHigh._mp_size={Runtime.InteropServices.Marshal.ReadInt32(tmpHigh.Pointer, 4):N0}  about to tdiv_r_2exp(finalQ, finalQ, k1)")
            gmp_lib.mpz_tdiv_r_2exp(finalQ, finalQ, k1)   ' finalQ  = Q0 (fits in existing alloc; no realloc)
            WriteToLog($"[3PM-DBG] tdiv_r_2exp done: finalQ._mp_size={Runtime.InteropServices.Marshal.ReadInt32(finalQ.Pointer, 4):N0}")

            ' Extract Q1 and Q2 from tmpHigh with another k1-sized shift.
            ' Both results are ≤ k1/64 limbs ≈ 373 MB — pre-alloc both for the same reason.
            Dim mpQ1 As New mpz_t()
            gmp_lib.mpz_init2(mpQ1, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _q1Needed As Long = _k1Limbs + 2L
            Dim _q1Bytes As Long = _q1Needed * 8L
            Dim _q1Buf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_q1Bytes)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _q1Buf <> IntPtr.Zero Then
                Dim _q1Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpQ1.Pointer, 8))
                VirtualFree(_q1Old, UIntPtr.Zero, MEM_RELEASE)
                Runtime.InteropServices.Marshal.WriteInt32(mpQ1.Pointer, 0, CInt(_q1Needed))
                Runtime.InteropServices.Marshal.WriteInt64(mpQ1.Pointer, 8, _q1Buf.ToInt64())
                WriteToLog($"[3PM-DBG] mpQ1 pre-alloc {_q1Needed:N0} limbs ({_q1Bytes \ 1048576L:N0} MB)")
            Else
                WriteToLog($"[3PM-DBG] mpQ1 pre-alloc FAILED for {_q1Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
            End If
            Dim mpQ2 As New mpz_t()
            gmp_lib.mpz_init2(mpQ2, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _q2Needed As Long = _k1Limbs + 2L  ' same upper bound as Q1
            Dim _q2Bytes As Long = _q2Needed * 8L
            Dim _q2Buf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_q2Bytes)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _q2Buf <> IntPtr.Zero Then
                Dim _q2Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpQ2.Pointer, 8))
                VirtualFree(_q2Old, UIntPtr.Zero, MEM_RELEASE)
                Runtime.InteropServices.Marshal.WriteInt32(mpQ2.Pointer, 0, CInt(_q2Needed))
                Runtime.InteropServices.Marshal.WriteInt64(mpQ2.Pointer, 8, _q2Buf.ToInt64())
                WriteToLog($"[3PM-DBG] mpQ2 pre-alloc {_q2Needed:N0} limbs ({_q2Bytes \ 1048576L:N0} MB)")
            Else
                WriteToLog($"[3PM-DBG] mpQ2 pre-alloc FAILED for {_q2Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
            End If
            WriteToLog($"[3PM-DBG] about to extract Q1/Q2 from tmpHigh")
            gmp_lib.mpz_tdiv_r_2exp(mpQ1, tmpHigh, k1)   ' mpQ1 = Q1
            WriteToLog($"[3PM-DBG] mpQ1._mp_size={Runtime.InteropServices.Marshal.ReadInt32(mpQ1.Pointer, 4):N0}  about to tdiv_q_2exp(mpQ2, tmpHigh, k1)")
            gmp_lib.mpz_tdiv_q_2exp(mpQ2, tmpHigh, k1)   ' mpQ2 = Q2
            WriteToLog($"[3PM-DBG] mpQ2._mp_size={Runtime.InteropServices.Marshal.ReadInt32(mpQ2.Pointer, 4):N0}  clearing tmpHigh")
            gmp_lib.mpz_clear(tmpHigh)
            WriteToLog($"[3PM-DBG] Q split complete")

            ' §61: Run all three products simultaneously — gmpNumer is read-only in all three
            ' calls; each result is a distinct mpz_t so there is no shared mutable state.
            ' Q0 (finalQ), Q1 (mpQ1), Q2 (mpQ2) stay in RAM — no disk spilling needed.
            ' r0, r1, r2 also stay in RAM; Combine B and D use them directly without reloading.
            Dim mpR0 As New mpz_t()
            Dim mpR1 As New mpz_t()
            Dim mpR2 As New mpz_t()
            gmp_lib.mpz_inits(mpR0, mpR1, mpR2, Nothing)
#If LOGGING_DETAIL >= 1 Then
            Dim _procP_pre = Process.GetCurrentProcess()
            Dim _ramP_pre As Long = _procP_pre.WorkingSet64 \ 1048576
            Dim _vmP_pre As Long = _procP_pre.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] §61 parallel multiply start: r0=N*Q0, r1=N*Q1, r2=N*Q2  RAM:{_ramP_pre:N0}MB  Committed:{_vmP_pre:N0}MB")
#End If
            System.Threading.Tasks.Parallel.Invoke(
                Sub() SafeMpzMul(mpR0, gmpNumer, finalQ),
                Sub() SafeMpzMul(mpR1, gmpNumer, mpQ1),
                Sub() SafeMpzMul(mpR2, gmpNumer, mpQ2))
            gmp_lib.mpz_clears(finalQ, mpQ1, mpQ2, Nothing)
            ' Swap r2 into gmpNumer (same pattern as the old serial Pass 2 end).
            ' The old gmpNumer buffer (~208 MB, the 426880*sqrt value) is freed by the swap.
            gmp_lib.mpz_swap(gmpNumer, mpR2)
            gmp_lib.mpz_clear(mpR2)
#If LOGGING_DETAIL >= 1 Then
            Dim _procP_post = Process.GetCurrentProcess()
            Dim _ramP_post As Long = _procP_post.WorkingSet64 \ 1048576
            Dim _vmP_post As Long = _procP_post.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] §61 parallel multiply done; entering Combine  RAM:{_ramP_post:N0}MB  Committed:{_vmP_post:N0}MB")
#End If
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] r2 (= gmpNumer after swap) = {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")
#End If

            ' ── Combine: gmpNumer = ((r2 << k) + r1) << k + r0 ──
            ' NOTE: mpz_t is a value type (struct) in GMP.NET.  Passing the same
            ' variable as BOTH destination and source gives GMP two struct copies
            ' that share the same _mp_d pointer.  GMP's aliasing guard compares
            ' struct addresses (not _mp_d), sees no match, takes the non-aliased
            ' path, reallocates rop's limb buffer via MPZ_REALLOC, then reads from
            ' op's now-freed _mp_d → crash.  Every step below uses a fresh output
            ' variable + mpz_swap to sidestep this.

            ' Step A: gmpNumer = r2 << k  (~390 MB → ~572 MB)
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine A: shift r2 ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) left {CLng(k1):N0} bits → result≈{(CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) + CLng(k1)) \ 8388608:N0} MB")
#End If
            Dim mpShiftA As New mpz_t()
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine A: mpz_init2(mpShiftA)")
#End If
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
                If _shiftBytesA >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufA As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_shiftBytesA)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                    If _bigBufA <> IntPtr.Zero Then
                        Dim _oldBufA As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpShiftA.Pointer, 8))
                        VirtualFree(_oldBufA, UIntPtr.Zero, MEM_RELEASE)
                        Runtime.InteropServices.Marshal.WriteInt32(mpShiftA.Pointer, 0, CInt(_shiftLimbs))
                        Runtime.InteropServices.Marshal.WriteInt64(mpShiftA.Pointer, 8, _bigBufA.ToInt64())
                        WriteToLog($"[ComputePi] Combine A: pre-alloc {_shiftLimbs:N0} limbs ({_shiftBytesA \ 1048576L:N0} MB) ptr={_bigBufA:X}")
                    Else
                        WriteToLog($"[ComputePi] Combine A: pre-alloc VirtualAlloc FAILED for {_shiftBytesA \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                    End If
                End If
            End If
#If LOGGING_DETAIL >= 1 Then
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
            WriteToLog($"[ComputePi] Combine A: mpz_mul_2exp  k={CLng(k1):N0} bits  gmpNumer={CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits")
#End If
            gmp_lib.mpz_mul_2exp(mpShiftA, gmpNumer, k1)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine A: mpz_mul_2exp returned OK")
#End If
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine A: mpz_swap")
#End If
            gmp_lib.mpz_swap(gmpNumer, mpShiftA)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine A: mpz_clear(mpShiftA)")
#End If
            gmp_lib.mpz_clear(mpShiftA)     ' frees the old ~390 MB limb buffer
#If LOGGING_DETAIL >= 1 Then
            Dim _procCA = Process.GetCurrentProcess()
            Dim _ramCombA As Long = _procCA.WorkingSet64 \ 1048576
            Dim _vmCombA As Long = _procCA.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine A done (r2<<k)  RAM:{_ramCombA:N0}MB  Committed:{_vmCombA:N0}MB")
#End If
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine A result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")
#End If

            ' Step B: reload r1; gmpNumer += r1  (~572 MB + ~390 MB → ~572 MB)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine B: mpz_init(mpR1) + deserialize")
#End If
            ' §61: mpR1 already in RAM from the parallel multiply — no disk reload.
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine B: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r1 ({CLng(gmp_lib.mpz_sizeinbase(mpR1, 2)):N0} bits)")
#End If
#If LOGGING_DETAIL >= 1 Then
            Dim _procCBpre = Process.GetCurrentProcess()
            Dim _ramCombBpre As Long = _procCBpre.WorkingSet64 \ 1048576
            Dim _vmCombBpre As Long = _procCBpre.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine B r1 in RAM  RAM:{_ramCombBpre:N0}MB  Committed:{_vmCombBpre:N0}MB")
#End If
            Dim mpAddB As New mpz_t()
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine B: mpz_init2(mpAddB)")
#End If
            gmp_lib.mpz_init2(mpAddB, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpAddB.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero AndAlso mpR1.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzB As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _r1AbsSzB As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpR1.Pointer, 4)))
                Dim _addLimbs As Long = System.Math.Max(_numerAbsSzB, _r1AbsSzB) + 2L
                Dim _addBytesB As Long = _addLimbs * 8L
                If _addBytesB >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufB As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_addBytesB)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                    If _bigBufB <> IntPtr.Zero Then
                        Dim _oldBufB As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpAddB.Pointer, 8))
                        VirtualFree(_oldBufB, UIntPtr.Zero, MEM_RELEASE)
                        Runtime.InteropServices.Marshal.WriteInt32(mpAddB.Pointer, 0, CInt(_addLimbs))
                        Runtime.InteropServices.Marshal.WriteInt64(mpAddB.Pointer, 8, _bigBufB.ToInt64())
                        WriteToLog($"[ComputePi] Combine B: pre-alloc {_addLimbs:N0} limbs ({_addBytesB \ 1048576L:N0} MB) ptr={_bigBufB:X}")
                    Else
                        WriteToLog($"[ComputePi] Combine B: pre-alloc VirtualAlloc FAILED for {_addBytesB \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                    End If
                End If
            End If
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine B: mpz_add")
#End If
            gmp_lib.mpz_add(mpAddB, gmpNumer, mpR1)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine B: mpz_swap")
#End If
            gmp_lib.mpz_swap(gmpNumer, mpAddB)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine B: mpz_clear(mpAddB) + mpz_clear(mpR1)")
#End If
            gmp_lib.mpz_clear(mpAddB)
            gmp_lib.mpz_clear(mpR1)
#If LOGGING_DETAIL >= 1 Then
            Dim _procCB = Process.GetCurrentProcess()
            Dim _ramCombB As Long = _procCB.WorkingSet64 \ 1048576
            Dim _vmCombB As Long = _procCB.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine B done (+r1)  RAM:{_ramCombB:N0}MB  Committed:{_vmCombB:N0}MB")
#End If
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine B result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")
#End If

            ' Step C: gmpNumer = (r2<<k + r1) << k  (~572 MB → ~755 MB)
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine C: shift ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) left {CLng(k1):N0} bits → result≈{(CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) + CLng(k1)) \ 8388608:N0} MB")
#End If
            Dim mpShiftC As New mpz_t()
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine C: mpz_init2(mpShiftC)")
#End If
            gmp_lib.mpz_init2(mpShiftC, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpShiftC.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzC As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _kBitsC As Long = CLng(k1)
                Dim _shiftLimbs As Long = _numerAbsSzC + (_kBitsC \ 64L) + 2L
                Dim _shiftBytesC As Long = _shiftLimbs * 8L
                If _shiftBytesC >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufC As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_shiftBytesC)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                    If _bigBufC <> IntPtr.Zero Then
                        Dim _oldBufC As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpShiftC.Pointer, 8))
                        VirtualFree(_oldBufC, UIntPtr.Zero, MEM_RELEASE)
                        Runtime.InteropServices.Marshal.WriteInt32(mpShiftC.Pointer, 0, CInt(_shiftLimbs))
                        Runtime.InteropServices.Marshal.WriteInt64(mpShiftC.Pointer, 8, _bigBufC.ToInt64())
                        WriteToLog($"[ComputePi] Combine C: pre-alloc {_shiftLimbs:N0} limbs ({_shiftBytesC \ 1048576L:N0} MB) ptr={_bigBufC:X}")
                    Else
                        WriteToLog($"[ComputePi] Combine C: pre-alloc VirtualAlloc FAILED for {_shiftBytesC \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                    End If
                End If
            End If
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine C: mpz_mul_2exp  k={CLng(k1):N0} bits")
#End If
            gmp_lib.mpz_mul_2exp(mpShiftC, gmpNumer, k1)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine C: mpz_swap")
#End If
            gmp_lib.mpz_swap(gmpNumer, mpShiftC)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine C: mpz_clear(mpShiftC)")
#End If
            gmp_lib.mpz_clear(mpShiftC)
#If LOGGING_DETAIL >= 1 Then
            Dim _procCC = Process.GetCurrentProcess()
            Dim _ramCombC As Long = _procCC.WorkingSet64 \ 1048576
            Dim _vmCombC As Long = _procCC.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine C done ((r2<<k+r1)<<k)  RAM:{_ramCombC:N0}MB  Committed:{_vmCombC:N0}MB")
#End If
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine C result: {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")
#End If

            ' Step D: reload r0; gmpNumer += r0  (~755 MB + ~390 MB → ~755 MB)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine D: mpz_init(mpR0) + deserialize")
#End If
            ' §61: mpR0 already in RAM from the parallel multiply — no disk reload.
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine D: add gmpNumer ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits) + r0 ({CLng(gmp_lib.mpz_sizeinbase(mpR0, 2)):N0} bits)")
#End If
#If LOGGING_DETAIL >= 1 Then
            Dim _procCDpre = Process.GetCurrentProcess()
            Dim _ramCombDpre As Long = _procCDpre.WorkingSet64 \ 1048576
            Dim _vmCombDpre As Long = _procCDpre.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine D r0 in RAM  RAM:{_ramCombDpre:N0}MB  Committed:{_vmCombDpre:N0}MB")
#End If
            Dim mpAddD As New mpz_t()
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine D: mpz_init2(mpAddD)")
#End If
            gmp_lib.mpz_init2(mpAddD, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            ' Pre-allocate the full result buffer directly into the native __mpz_struct.
            If mpAddD.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero AndAlso mpR0.Pointer <> IntPtr.Zero Then
                Dim _numerAbsSzD As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _r0AbsSzD As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(mpR0.Pointer, 4)))
                Dim _addLimbs As Long = System.Math.Max(_numerAbsSzD, _r0AbsSzD) + 2L
                Dim _addBytesD As Long = _addLimbs * 8L
                If _addBytesD >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufD As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_addBytesD)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                    If _bigBufD <> IntPtr.Zero Then
                        Dim _oldBufD As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpAddD.Pointer, 8))
                        VirtualFree(_oldBufD, UIntPtr.Zero, MEM_RELEASE)
                        Runtime.InteropServices.Marshal.WriteInt32(mpAddD.Pointer, 0, CInt(_addLimbs))
                        Runtime.InteropServices.Marshal.WriteInt64(mpAddD.Pointer, 8, _bigBufD.ToInt64())
                        WriteToLog($"[ComputePi] Combine D: pre-alloc {_addLimbs:N0} limbs ({_addBytesD \ 1048576L:N0} MB) ptr={_bigBufD:X}")
                    Else
                        WriteToLog($"[ComputePi] Combine D: pre-alloc VirtualAlloc FAILED for {_addBytesD \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                    End If
                End If
            End If
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine D: mpz_add")
#End If
            gmp_lib.mpz_add(mpAddD, gmpNumer, mpR0)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine D: mpz_swap")
#End If
            gmp_lib.mpz_swap(gmpNumer, mpAddD)
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] Combine D: mpz_clear(mpAddD) + mpz_clear(mpR0)")
#End If
            gmp_lib.mpz_clear(mpAddD)
            gmp_lib.mpz_clear(mpR0)
#If LOGGING_DETAIL >= 1 Then
            Dim _procCD = Process.GetCurrentProcess()
            Dim _ramCombD As Long = _procCD.WorkingSet64 \ 1048576
            Dim _vmCombD As Long = _procCD.PrivateMemorySize64 \ 1048576
            WriteToLog($"[ComputePi] Combine D done (+r0)  RAM:{_ramCombD:N0}MB  Committed:{_vmCombD:N0}MB")
#End If
#If LOGGING_DETAIL >= 2 Then
            WriteToLog($"[ComputePi] Combine D result (final gmpNumer): {CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits ({CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)) \ 8388608:N0} MB)")
#End If

            LogPhase("Numerator complete")

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
#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] finalT reloaded from spill file")
            WriteToLog($"[ComputePi] mpz_tdiv_q: pi = numer / T  (numer~{CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 10)):N0} digits  T~{CLng(gmp_lib.mpz_sizeinbase(finalT, 10)):N0} digits)")
#End If
            ' Pre-allocate gmpPi result buffer so MPZ_REALLOC short-circuits.
            ' gmpPi was initialised via mpz_inits (1-limb CRT buffer); the quotient
            ' is ~744 MB, so without pre-allocation GmpReallocFunc would be called.
            ' Guard: only VirtualAlloc when the result will be large (>= GMP_LARGE_THRESHOLD).
            ' For small/test inputs the quotient may be near-zero (numer << T), giving
            ' _quotBytes = 3*8 = 24 bytes.  A tiny VirtualAlloc buffer would be freed by
            ' GmpFreeFunc via _savedGmpFree (size < threshold) on a VirtualAlloc pointer → crash.
            If gmpPi.Pointer <> IntPtr.Zero AndAlso gmpNumer.Pointer <> IntPtr.Zero AndAlso finalT.Pointer <> IntPtr.Zero Then
                Dim _numerSzDiv As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(gmpNumer.Pointer, 4)))
                Dim _denomSzDiv As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(finalT.Pointer, 4)))
                Dim _quotLimbs As Long = System.Math.Max(_numerSzDiv - _denomSzDiv + 1L, 1L) + 2L
                Dim _quotBytes As Long = _quotLimbs * 8L
                If _quotBytes >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufPi As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_quotBytes)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                    If _bigBufPi <> IntPtr.Zero Then
                        Dim _oldAllocPi As Integer = Runtime.InteropServices.Marshal.ReadInt32(gmpPi.Pointer, 0)
                        Dim _oldBufPi As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(gmpPi.Pointer, 8))
                        ' The original buffer came from the CRT allocator (_savedGmpAlloc).
                        ' Free it via the saved free function, not VirtualFree.
                        _savedGmpFree(New void_ptr(_oldBufPi), New size_t(CULng(_oldAllocPi) * 8UL))
                        Runtime.InteropServices.Marshal.WriteInt32(gmpPi.Pointer, 0, CInt(_quotLimbs))
                        Runtime.InteropServices.Marshal.WriteInt64(gmpPi.Pointer, 8, _bigBufPi.ToInt64())
                        WriteToLog($"[ComputePi] Division: pre-alloc gmpPi {_quotLimbs:N0} limbs ({_quotBytes \ 1048576L:N0} MB) ptr={_bigBufPi:X}")
                    Else
                        WriteToLog($"[ComputePi] Division: pre-alloc VirtualAlloc FAILED for {_quotBytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                    End If
                End If
            End If
            gmp_lib.mpz_tdiv_q(gmpPi, gmpNumer, finalT)
            gmp_lib.mpz_clears(gmpNumer, finalT, Nothing)
            LogPhase("Division complete")

            If token.IsCancellationRequested Then Return ""

#If LOGGING_DETAIL >= 1 Then
            WriteToLog($"[ComputePi] mpz_get_str: converting result to string")
#End If
            Dim _strConvStart As DateTime = DateTime.Now
            Dim _strConvTimer As New System.Threading.Timer(
                Sub(state As Object)
                    Dim elapsed As TimeSpan = DateTime.Now - _strConvStart
                    Me.BeginInvoke(Sub()
                                       LblStatus.Text = $"String conversion... {elapsed:mm\:ss} elapsed"
                                   End Sub)
                End Sub, Nothing, 1000, 1000)
            Dim piCharPtr As char_ptr
            Try
                piCharPtr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, gmpPi)
            Finally
                _strConvTimer.Dispose()
            End Try
            ' Capture the actual digit count BEFORE clearing gmpPi.
            ' mpz_sizeinbase returns an estimate within +1; add 2 to match GMP's internal
            ' alloc of (sizeinbase + 2) bytes.  Used to set _displayNativeLen correctly and
            ' to decide whether to free the buffer via VirtualFree or _savedGmpFree.
            Dim _piDigits As Long = CLng(gmp_lib.mpz_sizeinbase(gmpPi, 10))
            _displayNativeBufSize = _piDigits + 2L   ' mirrors GmpAllocFunc's received size
            ' Free gmpPi now (~744 MB native); reinit 1-limb stub so Finally mpz_clears is safe.
            gmp_lib.mpz_clear(gmpPi)
            gmp_lib.mpz_init(gmpPi)
            ' Keep the native char buffer alive — the display timer will stream bytes
            ' directly from it, avoiding any large managed string allocation.
            _displayNativePtr = piCharPtr.Pointer
            _displayNativeLen = _piDigits + 1L   ' digits + null terminator position
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
            FlushGmpPool()
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
            Return
        End If

        displayTimer.Enabled = False
        RtbPiDigits.Clear()
        LblDigitsDisplayed.Text = "0"
        LblStatus.Text = $"Streaming {digitCount:N0} digits..."
        displayStr = piString   ' empty string in native mode — display reads from _displayNativePtr
        displayIdx = 0
        displayTotal = 0
        displayTimer.Enabled = True
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
                Else
                    System.IO.File.WriteAllText(outputFile, displayStr)
                End If
                LblStatus.Text = $"Done! Saved to {outputFile}"
            Catch ex As Exception
                LblStatus.Text = "File save error: " & ex.Message
            End Try
        Else
            LblStatus.Text = $"Done! {digitCount:N0} digits computed."
        End If
    End Sub

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
                ' The buffer will be freed when the user clicks Test or Compute.
                WriteToLog("[DisplayTimer] streaming complete — native pi buffer retained for Test button")
            Else
                displayStr = Nothing
                WriteToLog("[DisplayTimer] displayStr released (LOH block freed)")
            End If
            Return
        End If

        ' Display streaming chunk: 500 chars/tick at 100 ms interval = ~5,000 chars/sec.
        ' Sufficient for interactive viewing; irrelevant for headless runs (display is off).
        Const chunkSize As Integer = 500
        Dim chunkEnd As Integer = System.Math.Min(displayIdx + chunkSize, totalLen)

        Dim chunk As New System.Text.StringBuilder()
        If useNative Then
            ' First tick: prepend "3." before streaming the rest of the digits.
            If displayIdx = 0 Then
                chunk.Append(ChrW(Runtime.InteropServices.Marshal.ReadByte(_displayNativePtr, 0)))
                chunk.Append("."c)
                displayIdx = 1
            End If
            While displayIdx < chunkEnd
                Dim b As Byte = Runtime.InteropServices.Marshal.ReadByte(_displayNativePtr, displayIdx)
                If b = 0 Then
                    displayIdx = totalLen   ' null terminator reached — signal completion
                    Exit While
                End If
                chunk.Append(ChrW(b))
                displayIdx += 1
            End While
        Else
            While displayIdx < chunkEnd
                Dim ch As Char = displayStr(displayIdx)
                If Char.IsDigit(ch) OrElse ch = "."c Then
                    chunk.Append(ch)
                End If
                displayIdx += 1
            End While
        End If

        If chunk.Length > 0 Then
            displayTotal += chunk.Length
            RtbPiDigits.AppendText(chunk.ToString())
            RtbPiDigits.SelectionStart = RtbPiDigits.TextLength
            RtbPiDigits.ScrollToCaret()
            LblDigitsDisplayed.Text = $"{displayTotal:N0}"
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
    ''' Runs any --verify-at and --verify-contains checks supplied on the command line.
    ''' Results are shown in a MessageBox (interactive) or written to the log (headless).
    ''' </summary>
    Private Sub RunCustomVerifications(piText As String)
        For Each chk In _verifyAt
            Dim digits As String = chk.Item1
            Dim expectedPos As Long = chk.Item2
            Dim actualPos As Long = CLng(piText.IndexOf(digits))
            Dim msg As String
            If actualPos >= 0 Then
                msg = $"[verify-at] '{digits}' found at position {actualPos}. Expected: {expectedPos}. Correct: {actualPos = expectedPos}"
            Else
                msg = $"[verify-at] '{digits}' NOT FOUND (expected at position {expectedPos})"
            End If
            If Not _headless Then
                MessageBox.Show(msg, "Verify")
            Else
                WriteToLog("[DIALOG] " & msg)
                LblStatus.Text = msg
            End If
        Next

        For Each needle In _verifyContains
            Dim pos As Long = CLng(piText.IndexOf(needle))
            Dim msg As String
            If pos >= 0 Then
                msg = $"[verify-contains] '{needle}' found at position {pos}"
            Else
                msg = $"[verify-contains] '{needle}' NOT FOUND"
            End If
            If Not _headless Then
                MessageBox.Show(msg, "Verify")
            Else
                WriteToLog("[DIALOG] " & msg)
                LblStatus.Text = msg
            End If
        Next
    End Sub

    Private Sub BtnTest_Click(sender As Object, e As EventArgs) Handles BtnTest.Click
        ' Search the full native buffer when available (complete digits regardless of
        ' how far the display timer has streamed); fall back to the text box otherwise.
        Dim piText As String
        Dim usingNativeBuffer As Boolean = (_displayNativePtr <> IntPtr.Zero)
        If usingNativeBuffer Then
            ' Marshal the entire native string (null-terminated ASCII) into a managed string.
            ' This is ~1 GB for a billion-digit run; it will briefly double memory usage but
            ' lets IndexOf work normally.
            piText = Runtime.InteropServices.Marshal.PtrToStringAnsi(_displayNativePtr)
            ' Free via the same allocator that GmpAllocFunc used: VirtualFree for large
            ' buffers (>= 512 KB), _savedGmpFree for small ones (wrong result / test runs).
            If _displayNativeBufSize >= GMP_LARGE_THRESHOLD Then
                VirtualFree(_displayNativePtr, UIntPtr.Zero, MEM_RELEASE)
            Else
                _savedGmpFree(New void_ptr(_displayNativePtr), New size_t(CULng(_displayNativeBufSize)))
            End If
            _displayNativePtr = IntPtr.Zero
            WriteToLog("[BtnTest] native pi buffer searched and freed")
        Else
            piText = RtbPiDigits.Text.Replace(".", "").Replace(vbCrLf, "")
        End If

        Dim pos1 As Integer = piText.IndexOf("999999")
        Dim verify1 As String
        If pos1 >= 0 Then
            verify1 = $"Found '999999' at position {pos1}! Expected: 762. Correct: {pos1 = 762}"
        Else
            verify1 = "999999 not found!"
        End If
        If Not _headless Then
            MessageBox.Show(verify1)
        Else
            WriteToLog("[DIALOG] Verify 999999: " & verify1)
            LblStatus.Text = verify1
        End If

        Dim pos2 As Integer = piText.IndexOf("777777777")
        Dim verify2 As String
        If pos2 >= 0 Then
            verify2 = $"Found '777777777' at position {pos2}! Expected: 24,658,601. Correct: {pos2 = 24658601}"
        Else
            verify2 = "777777777 not found - may need more digits!"
        End If
        If Not _headless Then
            MessageBox.Show(verify2)
        Else
            WriteToLog("[DIALOG] Verify 777777777: " & verify2)
        End If

        Dim pos3 As Integer = piText.IndexOf("27182818284")
        Dim verify3 As String
        If pos3 >= 0 Then
            verify3 = $"Found first digits of e '27182818284' at position {pos3}!"
        Else
            verify3 = "First digits of e not found in current digits!"
        End If
        If Not _headless Then
            MessageBox.Show(verify3)
        Else
            WriteToLog("[DIALOG] Verify e digits: " & verify3)
        End If

        ' Run any additional checks supplied via --verify-at / --verify-contains
        If _verifyAt.Count > 0 OrElse _verifyContains.Count > 0 Then
            RunCustomVerifications(piText)
        End If
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
