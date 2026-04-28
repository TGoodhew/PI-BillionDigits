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
    ' §81 display perf: pre-allocated byte buffer reused across ticks (avoids per-tick allocation).
    Private _displayBuf() As Byte = New Byte(65535) {}   ' initial 64 KB; grown as adaptive chunk size increases
    ' §81 adaptive chunk size: starts at 4096, adjusted each tick to target ~80 ms of UI work.
    Private _displayChunkSize As Integer = 4096
    ' §81 scroll throttle: accumulates chars since last ScrollToCaret; scroll only every 10,000 chars.
    Private _displayScrollAccum As Integer = 0
    Private WithEvents displayTimer As New System.Windows.Forms.Timer()
    Private gmpC3Const As mpz_t = Nothing

    ' ── Headless / command-line mode ─────────────────────────────────────────
    ' Set by --autostart (suppress all dialogs) and --autoverify (run verify +
    ' Application.Exit after computation completes).
    Private _headless As Boolean = False
    Private Shared _logLevel As Integer = 2   ' §107-diag: raised to 2 for Newton-loop trace
    Private _autoVerify As Boolean = False
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
    Private Shared Function OpenThread(
        dwDesiredAccess As UInteger,
        bInheritHandle As Boolean,
        dwThreadId As UInteger) As IntPtr
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
                    ' Hybrid CPU — restrict process to P-cores
                    If SetProcessAffinityMask(GetCurrentProcess(), New IntPtr(pCoreMask)) Then
                        AppendLog($"[Affinity] Hybrid CPU detected. P-core mask=0x{pCoreMask:X}  E-core mask=0x{eCoreMask:X}. Process restricted to P-cores.{vbCrLf}")
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
    End Function

    ' §21: Try/Catch removed — same rationale as GmpAllocFunc.
    Private Shared Function GmpReallocFunc(old_ptr As void_ptr,
                                            old_size As size_t,
                                            new_size As size_t) As void_ptr
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
                vbCrLf)
        Catch
        End Try
        Return 0   ' EXCEPTION_CONTINUE_SEARCH — let Windows handle it (WER, minidump, etc.)
    End Function

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' ── Parse command-line arguments ─────────────────────────────────────
        ' Supported flags:
        '   --digits N                  Set the digit count (no commas required)
        '   --autostart                 Suppress all dialogs and auto-begin computation
        '   --autoverify                After computation, auto-run verify + exit
        '   --threshold N               Override the RAM/disk threshold (nodes)
        '   --log-level N               Set runtime logging level 0–5 (default 1)
        '   --output-dir D              Override output directory for digits, log, and node cache
        '   --checkpoint-from-level N   Serialize nodes at level >= N to disk (for resume)
        '   --resume-from-level N       Skip Phase 1 + levels 1..N-1; load checkpoint files for level N
        '   --auto-checkpoint           Write RAM snapshot at end of each level; auto-resume on next run
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

        ' Restrict process affinity to P-cores on hybrid CPUs (Intel 12th gen+,
        ' AMD Zen 4c).  E-cores run GMP arithmetic ~30-50% slower and cause
        ' cache-topology mismatches in parallel workloads.
        SetPCoreAffinity()
        StartAffinityWatchdog()   ' §106: keep all threads on P-cores throughout the run

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
            Dim _levelNames() As String = {"None", "Performance", "Stages", "Last stage", "Full trace", "Allocator"}
            Dim loggingMode As String = $"{_logLevel} ({If(_logLevel >= 0 AndAlso _logLevel < _levelNames.Length, _levelNames(_logLevel), "Custom")})"
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
            BackupSnapshotToStore("snap_Phase3")
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
            BackupSnapshotToStore("snap_Phase3")
        Catch ex As Exception
            LogPhase($"[ComputePi] snap_Phase3 save FAILED: {ex.Message} — continuing without checkpoint")
        End Try
    End Sub

    ' §103: Load finalP/finalQ/finalT from snap_Phase3/ if it exists and matches digits.
    ' Returns True and populates outP/outQ/outT on success; returns False on any mismatch or error.
    ' outP/outQ/outT must already be mpz_init'd by the caller.
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
        ' §5B-Option-B (2026-04-27 — TEMPORARY DIAGNOSTIC): lowered 5M → 1M to force one
        ' more recursion level past the current 2.2M × 1.1M leaves. Binary outcome:
        '   Bug disappears  ⇒ leaf mpz_mul/mpn_mul_fft is wrong at sizes ≥ 1M (FFT
        '                     precision issue at the previous threshold).
        '   Bug persists    ⇒ middle-limb error is in our SafeMpzMul 3×3 split logic
        '                     (accumulator add, mul_2exp shift, or recursion housekeeping).
        ' Revert to 5M once the source of the 5B SafeMpzMul middle-limb bug is identified.
        Const SAFE_LIMB_THRESHOLD As Integer = 1_000_000

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
                    AppendLog($"[SafeMpzMul§183] ENTRY zero-data squaring: szA={szA:N0} opA_d={_183_opAd:X16} r[0]={_183_r0:X16} r[1]={_183_r1:X16} opAptr={opA.Pointer.ToInt64():X16} resPtr={result.Pointer.ToInt64():X16}{vbCrLf}")
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
                    AppendLog($"[SafeMpzMul§178] zero-result squaring: szA={szA:N0} opA.Ptr={opA.Pointer.ToInt64():X16} opA._mp_d={_178aD:X16} raw[0]={_178r0:X16} raw[1]={_178r1:X16}{vbCrLf}")
                End If
            End If
            Return
        End If

        ' §143: Log when recursion is triggered for large multiplications (szA+szB > threshold).
        If _logLevel >= 2 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
            AppendLog($"[SafeMpzMul§143] RECURSE szA={szA:N0} szB={szB:N0} total={CLng(szA)+CLng(szB):N0} (threshold={SAFE_LIMB_THRESHOLD:N0}) — using 3×3 split to avoid GMP FFT precision error{vbCrLf}")
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
                $"[SafeMpzMul] accum pre-alloc FAILED for {_resultBytes \ 1048576L:N0} MB — throwing OOM{vbCrLf}")
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
        Dim _smmDop As Integer = System.Threading.Volatile.Read(_safeMulDop)  ' §27: cross-thread read
        If _smmDop <= 0 Then _smmDop = Environment.ProcessorCount
        ' §138: Force serial sub-product computation for q×b (szA=szB=21875001, opA_d≠opB_d).
        ' Parallel.For with GmpNativeAlloc.dll gave catastrophically wrong prods(8) for q×b —
        ' suspected GMP internal allocator thread-safety issue under concurrent mpz_mul reallocs.
        ' r×r has opA_d=opB_d (same=True) and continues to use the parallel path safely.
        ' §165: Extended to also cover a×r (szA=43750001, szB=21875001, opA_d≠opB_d).
        ' Newton's non-squaring mults peak at ~21875000×10937500 (half the size of a×r) so
        ' they were never affected; a×r is the first 43750001×21875001 non-squaring call.
        Dim _forceSerialQxB As Boolean = ((szA = 21875001 OrElse szA = 43750001) AndAlso szB = 21875001 AndAlso _pre_opA_d <> _opB_d)
        If _smmDop <= 1 OrElse _forceSerialQxB Then
            If _logLevel >= 2 AndAlso _forceSerialQxB Then AppendLog($"[SafeMpzMul§138] forcing serial sub-products for {If(szA = 43750001, "a×r", "q×b")} (opA_d={_pre_opA_d:X16} opB_d={_opB_d:X16}){vbCrLf}")
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
                SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
            End Sub)
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
            AppendLog($"[SafeMpzMul§136] serial A2×B2[{_IDX136:N0}]={_fr136v:X16} prods(8)[{_IDX136:N0}]={_p8v136:X16} match={_fr136v = _p8v136}{vbCrLf}")
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
            Dim _p8Top134 As Long = If(_p8Sz134 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p8D134, (_p8Sz134 - 1) * 8), 0L)
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
            AppendLog($"[SafeMpzMul] shared shifted pre-alloc FAILED for {_maxShiftedLimbs * 8L \ 1048576L:N0} MB — throwing OOM{vbCrLf}")
            Throw New OutOfMemoryException($"SafeMpzMul: VirtualAlloc failed for shared shifted ({_maxShiftedLimbs * 8L \ 1048576L} MB)")
        End If
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] shifted buffer OK ({_maxShiftedLimbs * 8L \ 1048576L:N0} MB); starting accumulation (§39={mA = mB AndAlso CLng(mA) + CLng(mB) <= 100_000_000L}){vbCrLf}")
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
          If mA = mB AndAlso
              CLng(mA) + CLng(mB) <= 50_000_000L AndAlso
              _A0_szT > 0 AndAlso _A1_szT > 0 AndAlso _A2_szT > 0 AndAlso
              _B0_szT > 0 AndAlso _B1_szT > 0 AndAlso _B2_szT > 0 Then
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
            For _col As Integer = 0 To 4
                Dim _bk As Integer = _col_base(_col)
                ' Add extra sub-products into the base slot.
                ' §Phase3ColAdd fix: pre-grow prods(_bk) before each add so GMP never needs
                ' to call its internal realloc callback. When prods(_bk) is exactly szA+szB+2
                ' limbs and the result needs one more limb (carry), GMP calls NativeReallocFunc
                ' which does VirtualAlloc+memcpy+VirtualFree for a potentially large buffer.
                ' At peak memory pressure that VirtualAlloc can fail → GMP calls abort() →
                ' silent process termination. Pre-growing here prevents the realloc entirely.
                For Each _ak As Integer In _col_extra(_col)
                    Dim _bk_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_bk).Pointer, 4))
                    Dim _ak_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_ak).Pointer, 4))
                    Dim _needed As Integer = System.Math.Max(_bk_sz, _ak_sz) + 2  ' +2 for carry safety
                    Dim _bk_alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(prods(_bk).Pointer, 0)
                    If _bk_alloc < _needed Then
                        Dim _oldBuf As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(_bk).Pointer, 8))
                        Dim _oldBytes As Long = CLng(_bk_alloc) * 8L
                        Dim _newBytes As Long = CLng(_needed) * 8L
                        Dim _newBuf As IntPtr = GmpNativeAlloc_PoolGet(_newBytes)
                        If _newBuf = IntPtr.Zero Then
                            AppendLog($"[SafeMpzMul§39] pre-grow FAILED for {_newBytes \ 1048576L:N0} MB — throwing OOM{vbCrLf}")
                            Throw New OutOfMemoryException($"SafeMpzMul §39 pre-grow: GmpNativeAlloc_PoolGet failed ({_newBytes \ 1048576L} MB)")
                        End If
                        CopyMemory(_newBuf, _oldBuf, New UIntPtr(CULng(_bk_sz) * 8UL))
                        GmpNativeAlloc_FreeRaw(_oldBuf, _oldBytes)
                        Runtime.InteropServices.Marshal.WriteInt32(prods(_bk).Pointer, 0, _needed)
                        Runtime.InteropServices.Marshal.WriteInt64(prods(_bk).Pointer, 8, _newBuf.ToInt64())
                    End If
                    GmpRaw_add(prods(_bk).Pointer, prods(_bk).Pointer, prods(_ak).Pointer)
                    GmpRaw_clear(prods(_ak).Pointer)
                    Dim _tmp_ak = prods(_ak).Pointer : prods(_ak).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_tmp_ak)
                Next
                ' Shift column sum and add to accumulator
                Dim _colShift As ULong = CULng(_col) * bitsA
                Dim _sv_bk As IntPtr = prods(_bk).Pointer
                If _colShift = 0UL Then
                    GmpRaw_add(accumPtr, accumPtr, _sv_bk)
                Else
                    Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
                    Dim _shiftSrc As IntPtr = _sv_bk
                    Dim _shiftRem As ULong = _colShift
                    While _shiftRem > 0UL
                        Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                        GmpRaw_mul_2exp(_sv_shifted_hdr, _shiftSrc, _chunk)
                        _shiftSrc = _sv_shifted_hdr
                        _shiftRem -= CULng(_chunk)
                    End While
                    GmpRaw_add(accumPtr, accumPtr, _sv_shifted_hdr)
                End If
                ' §114: per-column diagnostic for q*b (mA=mB=7,291,667 symmetric call).
                If _logLevel >= 2 AndAlso mA = 7291667UL Then
                    Dim _114bkSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_bk, 4))
                    Dim _114shSz As Integer = If(_colShift = 0UL, _114bkSz, System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_shifted_hdr, 4)))
                    Dim _114aSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                    Dim _114bkDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_bk, 8))
                    Dim _114bkTop As Long = If(_114bkSz >= 1, Runtime.InteropServices.Marshal.ReadInt64(_114bkDPtr, (_114bkSz - 1) * 8), 0L)
                    Dim _114bkBot As Long = If(_114bkSz >= 1, Runtime.InteropServices.Marshal.ReadInt64(_114bkDPtr, 0), 0L)
                    AppendLog($"[SafeMpzMul§114] §39 col={_col} shift={_colShift:N0} szBk={_114bkSz:N0} bkTop={_114bkTop:X16} bkBot={_114bkBot:X16} szShifted={_114shSz:N0} szAccum={_114aSz:N0}{vbCrLf}")
                    ' For col=4 (A2*B2): log accum at the position driving the rem error.
                    If _col = 4 Then
                        Const _114EL As Long = 42779664L
                        Dim _114ADPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                        Dim _114AV As Long = If(_114EL < CLng(_114aSz), Runtime.InteropServices.Marshal.ReadInt64(_114ADPtr, CInt(_114EL * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§114] accum[{_114EL:N0}]={_114AV:X16}{vbCrLf}")
                    End If
                End If
                GmpRaw_clear(_sv_bk)
                prods(_bk).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_sv_bk)
            Next _col
        Else
            ' §23/§90: Original per-product accumulation for asymmetric case (mA ≠ mB).
            For k As Integer = 0 To 8
                Dim ki As Integer = k \ 3
                Dim kj As Integer = k Mod 3
                Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
                Dim _sv_prod As IntPtr = prods(k).Pointer
                Dim _logPre As Integer = If(_logLevel >= 2, System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_prod, 4)), 0)
                If _logLevel >= 2 Then
                    Dim _prodDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                    Dim _prodTop As Long = If(_logPre >= 1, Runtime.InteropServices.Marshal.ReadInt64(_prodDPtr, (_logPre - 1) * 8), 0L)
                    Dim _prodTop2 As Long = If(_logPre >= 2, Runtime.InteropServices.Marshal.ReadInt64(_prodDPtr, (_logPre - 2) * 8), 0L)
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
                        AppendLog($"[SafeMpzMul§5B-sub k={k} ki={ki} kj={kj}] szProd={_logPre:N0} bot=[{_sp5Bot:X16} {_sp5Bot2:X16}] mid[{_logPre\2:N0}]={_sp5Mid:X16} top=[{_sp5Top2:X16} {_sp5Top:X16}]{vbCrLf}")
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verify] A_{ki}[0]={_ai5_bot:X16} B_{kj}[0]={_bj5_bot:X16} (A_{ki}*B_{kj})_lo={_exp5SpBot:X16} actual prod[0]={_sp5Bot:X16} match={(_exp5SpBot = _sp5Bot)}{vbCrLf}")
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
                            AppendLog($"[SafeMpzMul§5B-sub k={k} verify1] A_{ki}[1]={_ai51_1:X16} B_{kj}[1]={_bj51_1:X16} hi00={_hi00:X16} lo01={_lo01:X16} lo10={_lo10:X16}  expected prod[1]={_expProd1:X16}  actual prod[1]={_actProd1:X16}  match={(_actProd1 = _expProd1)}{vbCrLf}")
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
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verifyT] A_{ki}[{_szAi - 1UL:N0}]={_ai5_top:X16} B_{kj}[{_szBj - 1UL:N0}]={_bj5_top2:X16} expHi={_expTopHi:X16} expLo={_expTopLo:X16} expSzProd={_expSzProd:N0} actSzProd={_logPre:N0} actTop={_sp5Top:X16} actTop-1={_sp5Top2:X16} cmpExp={_expTopForCmp:X16} diff(act-exp)={_topDiff:X16}{vbCrLf}")
                    End If
                End If
                If shiftBits = 0UL Then
                    GmpRaw_add(accumPtr, accumPtr, _sv_prod)
                    If _logLevel >= 2 Then
                        Dim _accumSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        AppendLog($"[SafeMpzMul§gen] k={k} shift=0 szProd={_logPre:N0} accumSz={_accumSz:N0}{vbCrLf}")
                    End If
                Else
                    Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
                    Dim _shiftSrc As IntPtr = _sv_prod
                    Dim _shiftRem As ULong = shiftBits
                    While _shiftRem > 0UL
                        Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                        GmpRaw_mul_2exp(_sv_shifted_hdr, _shiftSrc, _chunk)
                        _shiftSrc = _sv_shifted_hdr
                        _shiftRem -= CULng(_chunk)
                    End While
                    ' §150: q*b pre-add check — verify accum[42779664]=0 before adding k=8.
                    ' Only k=8 (shift=29166668 limbs) can reach position 42779664; no k<8 does.
                    ' A nonzero pre-k8 value would reveal an unexpected earlier sub-product bug.
                    If _logLevel >= 2 AndAlso k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                        Dim _pre150sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        Dim _pre150DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                        Dim _pre150v As Long = If(42779664L < CLng(_pre150sz), Runtime.InteropServices.Marshal.ReadInt64(_pre150DPtr, CInt(42779664L * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§150] pre-k8-add accum[42779664]={_pre150v:X16} (expect 0000000000000000){vbCrLf}")
                    End If
                    GmpRaw_add(accumPtr, accumPtr, _sv_shifted_hdr)
                    If _logLevel >= 2 Then
                        Dim _shiftedSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_shifted_hdr, 4))
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
                GmpRaw_clear(prods(k).Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(prods(k).Pointer)
            Next k
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
            If _logLevel >= 2 Then AppendLog($"[BSR§129] dst={dst.ToInt64():X16} src={src.ToInt64():X16} chunk={chunk:N0} bitsLeft={bitsLeft:N0} rop_alloc={Runtime.InteropServices.Marshal.ReadInt32(dst, 0):N0} rop_sz={Runtime.InteropServices.Marshal.ReadInt32(dst, 4):N0} src_sz={Runtime.InteropServices.Marshal.ReadInt32(src, 4):N0}{vbCrLf}")
            GmpRaw_tdiv_q_2exp(dst, src, chunk)
            If _logLevel >= 2 Then AppendLog($"[BSR§129] done chunk={chunk:N0} rop_sz={Runtime.InteropServices.Marshal.ReadInt32(dst, 4):N0}{vbCrLf}")
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

    ' Compute r = floor(2^kBits / b) for b > 0, kBits > sizeinbase(b,2).
    ' Newton iteration with progressive precision; r is always an underestimate.
    ' All large multiplications use SafeMpzMul — no direct mpn_mul_fft calls.
    Private Shared Sub SafeMpzReciprocal(r As mpz_t, b As mpz_t, kBits As Long)
        Const SAFE As Integer = 33_554_431
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

        ' ── Seed: ~64-bit approximation from top 64 bits of b ──────────────
        Dim bHiShift As Long = System.Math.Max(0L, bBits - 64L)
        Dim bHi As New mpz_t()
        gmp_lib.mpz_init(bHi)
        If bHiShift > 0L Then
            BigShiftRight(bHi, b, bHiShift)
            gmp_lib.mpz_add_ui(bHi, bHi, 1UI)   ' ceiling → underestimate of reciprocal guaranteed
        Else
            GmpRaw_set(bHi.Pointer, b.Pointer)  ' §35
        End If
        ' rSeed = floor(2^64 / bHi)  [safe: both operands tiny]
        Dim rSeed As New mpz_t()
        gmp_lib.mpz_init(rSeed)
        gmp_lib.mpz_set_ui(rSeed, 1UI)
        gmp_lib.mpz_mul_2exp(rSeed, rSeed, New mp_bitcnt_t(64UI))
        GmpRaw_tdiv_q(rSeed.Pointer, rSeed.Pointer, bHi.Pointer)  ' §35
        gmp_lib.mpz_clear(bHi)
        ' Scale to r's domain: rSeed * 2^(kBits-64-bHiShift) ≈ 2^kBits / b (underestimate)
        Dim seedScale As Long = kBits - 64L - bHiShift
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

        ' §NR-ckpt: Resume from a mid-NR checkpoint if one exists for this exact kBits/bBits.
        ' Saves r (the reciprocal estimate) and prec so a crash during a later NR iteration
        ' does not require restarting from the seed.  bTrunc is re-derived each iteration
        ' from b, so only r + prec need to be saved.
        Dim _nrSnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _nrBin As String = System.IO.Path.Combine(_nrSnapDir, "nr_r.bin")
        Dim _nrMeta As String = System.IO.Path.Combine(_nrSnapDir, "nr_meta.txt")
        Dim prec As Long = 62L
        If _autoCheckpoint AndAlso System.IO.File.Exists(_nrBin) AndAlso System.IO.File.Exists(_nrMeta) Then
            Try
                Dim _metaLines As String() = System.IO.File.ReadAllLines(_nrMeta)
                Dim _meta As New Dictionary(Of String, String)()
                For Each _ml As String In _metaLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _meta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _snapKBits As Long = 0L, _snapBBits As Long = 0L, _snapPrec As Long = 0L
                If _meta.ContainsKey("kBits") AndAlso Long.TryParse(_meta("kBits"), _snapKBits) AndAlso
                   _meta.ContainsKey("bBits") AndAlso Long.TryParse(_meta("bBits"), _snapBBits) AndAlso
                   _meta.ContainsKey("prec")  AndAlso Long.TryParse(_meta("prec"),  _snapPrec)  AndAlso
                   _snapKBits = kBits AndAlso _snapBBits = bBits AndAlso _snapPrec > 62L Then
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
        Dim _nrIter As Integer = 0
        Do While prec < rBits + 2L
            _nrIter += 1
            prec = System.Math.Min(prec * 2L + 4L, rBits + 2L)

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
            If bShift > 0L Then
                BigShiftRight(bTrunc, b, bShift)
                ' No ceiling +1: floor truncation avoids catastrophic overshoot in final step.
            Else
                ' §PreAlloc-bTrunc: bTrunc._mp_alloc from prior BigShiftRight may be < _szB.
                ' GMP's __gmpz_realloc aborts when new_alloc > 33,554,431 limbs (INT_MAX/64,
                ' 32-bit mp_size_t overflow check fires BEFORE our GmpReallocFunc callback).
                ' Pre-allocate via our pool to bypass it, same pattern as BigShiftRight/BigShiftLeft.
                PreAllocMpzToLimbs(bTrunc, CLng(_szB))
                GmpRaw_set(bTrunc.Pointer, b.Pointer)  ' §35
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
            Else
                SafeMpzMul(rSq, r, r)
            End If
            ' §121: log rSq top+bot at final iteration to verify r×r correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz121 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
                Dim _rSq121DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rSq.Pointer, 8))
                Dim _rSq121B0 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 0), 0L)
                Dim _rSq121B1 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 8), 0L)
                Dim _rSq121T1 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, (_sz121 - 1) * 8), 0L)
                Dim _rSq121T0 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, (_sz121 - 2) * 8), 0L)
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
            Else
                SafeMpzMul(p, bTrunc, rSq)
            End If
            ' §122: log p top+bot before BigShiftRight at final iteration to verify b×rSq correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz122 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Dim _p122DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p122B0 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 0), 0L)
                Dim _p122B1 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 8), 0L)
                Dim _p122T1 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, (_sz122 - 1) * 8), 0L)
                Dim _p122T0 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, (_sz122 - 2) * 8), 0L)
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
                Dim _p120T1 As Long = If(_sz120 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, (_sz120 - 1) * 8), 0L)
                Dim _p120T0 As Long = If(_sz120 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, (_sz120 - 2) * 8), 0L)
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
            ' §PreAlloc-r-add: After checkpoint restore r._mp_alloc equals _mp_size exactly.
            ' GmpRaw_add(r,r,r) → 2r may need one extra limb → __gmpz_realloc > 33.5M limit → GMP abort.
            ' Pre-allocate 2 extra limbs via our pool to bypass it.
            PreAllocMpzToLimbs(r, CLng(szR) + 2L)
            GmpRaw_add(r.Pointer, r.Pointer, r.Pointer)    ' §NR-raw: r = 2r — bypass managed wrapper pointer corruption
            GmpRaw_sub(r.Pointer, r.Pointer, p.Pointer)    ' §NR-raw: r = 2r - p — bypass managed wrapper pointer corruption
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
            If _logLevel >= 2 Then
                Dim _szR_after As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)
                Dim _szP As Integer = Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4)
                ' §119: log r bottom+top 2 limbs at every NR iteration to track lower-bit convergence
                Dim _sz119 As Integer = System.Math.Abs(_szR_after)
                Dim _r119DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r119B0 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 0), 0L)
                Dim _r119B1 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 8), 0L)
                Dim _r119T1 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, (_sz119 - 1) * 8), 0L)
                Dim _r119T0 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, (_sz119 - 2) * 8), 0L)
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
                    BackupSnapshotToStore("snap_Phase3")
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
        Loop
        ' §108-diag: log top 4 limbs of r to verify value (not just size)
        If _logLevel >= 2 Then
            Dim _szRFinal As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            Dim _rDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _rLimb2 As Long = If(_szRFinal >= 1, Runtime.InteropServices.Marshal.ReadInt64(_rDPtr, (_szRFinal - 1) * 8), 0L)
            Dim _rLimb1 As Long = If(_szRFinal >= 2, Runtime.InteropServices.Marshal.ReadInt64(_rDPtr, (_szRFinal - 2) * 8), 0L)
            AppendLog($"[SafeMpzReciprocal] done: szR={_szRFinal:N0} top2limbs=[{_rLimb2:X16} {_rLimb1:X16}] kBits={kBits:N0} rBits={rBits:N0}{vbCrLf}")
        End If
        ' §36: Clear loop-external temporaries once after loop completes.
        gmp_lib.mpz_clears(bTrunc, rSq, p, Nothing)
    End Sub

    ' Compute q = floor(a / b).  Safe for any operand size.
    ' Uses Barrett-style Newton reciprocal + SafeMpzMul — no direct GMP division
    ' for large inputs (which would crash via mpn_mul_fft overflow at 5B digits).
    Private Shared Sub SafeMpzDiv(q As mpz_t, a As mpz_t, b As mpz_t)
        Const SAFE As Integer = 33_554_431
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

        Dim r As New mpz_t()
        gmp_lib.mpz_init(r)
        ' §168: Force all-serial for SafeMpzReciprocal — bTrunc×rSq inside iter=25
        ' (szA=21875001, szB≈34603008) bypasses §138 (szB≠21875001) and runs in parallel,
        ' corrupting r. §166/§167 proved a×r and q×b are computed correctly FROM wrong r.
        ' Making the entire Newton reciprocal serial ensures bTrunc×rSq produces correct p.
        Dim _saved168Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
        System.Threading.Volatile.Write(_safeMulDop, 1)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§168] forcing all-serial for SafeMpzReciprocal (savedDop={_saved168Dop}){vbCrLf}")
        SafeMpzReciprocal(r, b, kBits)
        System.Threading.Volatile.Write(_safeMulDop, _saved168Dop)
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
            Dim _aDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _aTop As Long = If(szA >= 1, Runtime.InteropServices.Marshal.ReadInt64(_aDPtr, (szA - 1) * 8), 0L)
            Dim _aTop2 As Long = If(szA >= 2, Runtime.InteropServices.Marshal.ReadInt64(_aDPtr, (szA - 2) * 8), 0L)
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
            AppendLog($"[SafeMpzDiv§5B-a] a[0]={_5b_aBot:X16} a[1]={_5b_aBot2:X16} a[mid={szA \ 2:N0}]={_5b_aMid:X16} a[szA-2]={_5b_aTop2:X16} a[szA-1]={_5b_aTop:X16}{vbCrLf}")
            AppendLog($"[SafeMpzDiv§5B-r] r[0]={_5b_rBot:X16} r[1]={_5b_rBot2:X16} r[mid={szR \ 2:N0}]={_5b_rMid:X16} r[szR-2]={_5b_rTop2:X16} r[szR-1]={_5b_rTop:X16}{vbCrLf}")
        End If

        ' §166: Force ALL recursive levels of a×r fully serial — GMP allocator is not
        ' thread-safe under concurrent mpz_mul reallocs with distinct opA_d/opB_d buffers.
        ' §138/§165 only forced the outer Parallel.For; inner recursive SafeMpzMul calls
        ' still used Parallel.For (szA=14583333 ≠ 21875001 bypassed §138 gate).
        ' Setting _safeMulDop=1 propagates into every recursive level of SafeMpzMul.
        Dim _saved166Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
        System.Threading.Volatile.Write(_safeMulDop, 1)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§166] forcing all-serial for a×r (savedDop={_saved166Dop}){vbCrLf}")
        SafeMpzMul(ar, a, r)
        System.Threading.Volatile.Write(_safeMulDop, _saved166Dop)
        gmp_lib.mpz_clear(r)
        Dim szAR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))

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
            AppendLog($"[SafeMpzDiv§5B-ar] ar[0]={_arBot5:X16} ar[1]={_arBot5_2:X16} ar[mid={szAR \ 2:N0}]={_arMid5:X16} ar[szAR-2]={_arTop5_2:X16} ar[szAR-1]={_arTop5:X16}{vbCrLf}")
            AppendLog($"[SafeMpzDiv§5B-arBot] actual ar[0]={_arBot5:X16}  expected (a[0]*r[0])_lo={_expArBot:X16}  match={(_arBot5 = _expArBot)}{vbCrLf}")
            AppendLog($"[SafeMpzDiv§5B-arTop] actual ar[szAR-1..szAR-2]=[{_arTop5:X16} {_arTop5_2:X16}]  a[top]*r[top]=[hi={_expArTopHigh:X16} lo={_expArTopLow:X16}]  (top should be ≈ hi+carry; lo+carry should be near {_arTop5_2:X16}){vbCrLf}")
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
            AppendLog($"[SafeMpzDiv§5B-arQ-src] ar[{_5bQuartArIdx:N0}]={_5b_arQuart0:X16} ar[{_5bQuartArIdx+1:N0}]={_5b_arQuart1:X16} ar[{_5bMidArIdx:N0}]={_5b_arMid0:X16} ar[{_5bMidArIdx+1:N0}]={_5b_arMid1:X16}{vbCrLf}")
        End If
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
            Dim _arDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _arTop As Long = If(szAR >= 1, Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, (szAR - 1) * 8), 0L)
            Dim _arTop2 As Long = If(szAR >= 2, Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, (szAR - 2) * 8), 0L)
            ' Boundary limbs: at index kBits\64 and kBits\64+1. q_bot = (bnd0 >> kBits%64) | (bnd1 << (64 - kBits%64))
            Dim _kLimb As Long = kBits \ 64L
            Dim _kRem As Integer = CInt(kBits Mod 64L)
            Dim _arBnd0 As Long = If(_kLimb >= 0L AndAlso _kLimb < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_kLimb * 8L)), 0L)
            Dim _arBnd1 As Long = If(_kLimb + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_kLimb + 1L) * 8L)), 0L)
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
        Dim szQ As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))
        If _logLevel >= 2 Then
            Dim _qDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _qTop As Long = If(szQ >= 1, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, (szQ - 1) * 8), 0L)
            Dim _qTop2 As Long = If(szQ >= 2, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, (szQ - 2) * 8), 0L)
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
                AppendLog($"[SafeMpzDiv§5B-q] q[0]={_qBot:X16} q[1]={_qBotPos5:X16} q[quart=21,875,000]={_qQuart5:X16} q[mid={szQ \ 2:N0}]={_qMid5:X16} q[szQ-2]={_qTop2:X16} q[szQ-1]={_qTop:X16}{vbCrLf}")
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
                AppendLog($"[SafeMpzDiv§5B-q-quart] actual q[21,875,000]={_actQQuartU:X16}  expected (ar[{21875000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQQuart:X16}  match={(_actQQuartU = _expQQuart)}{vbCrLf}")
                AppendLog($"[SafeMpzDiv§5B-q-mid]   actual q[43,750,000]={_actQMidU:X16}  expected (ar[{43750000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQMid:X16}  match={(_actQMidU = _expQMid)}{vbCrLf}")
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
        Dim _qPtr As IntPtr = q.Pointer     ' §§78-qptr: capture before mpz_clear(ar) fires §78 and corrupts q.Pointer
        gmp_lib.mpz_clear(ar)

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
        ' §167: Same all-levels serial fix for q×b.
        Dim _saved167Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
        System.Threading.Volatile.Write(_safeMulDop, 1)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§167] forcing all-serial for q×b (savedDop={_saved167Dop}){vbCrLf}")
        SafeMpzMul(qb, q, b)
        System.Threading.Volatile.Write(_safeMulDop, _saved167Dop)
        ' Capture qb's raw pointer immediately — before any native call that could corrupt qb.Pointer.
        Dim _qbPtr As IntPtr = qb.Pointer   ' = savedResultPtr set by SafeMpzMul
        Dim szQB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4))
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§184] qb raw: alloc={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 0):N0} size={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4):N0} _mp_d={Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8):X16}{vbCrLf}")
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
            Dim _remDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
            Dim _remTop As Long = If(szRem >= 1, Runtime.InteropServices.Marshal.ReadInt64(_remDPtr, (szRem - 1) * 8), 0L)
            Dim _bDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
            Dim _bTop As Long = If(szB >= 1, Runtime.InteropServices.Marshal.ReadInt64(_bDPtr, (szB - 1) * 8), 0L)
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
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-down complete: {_adjDown} iter(s){vbCrLf}")

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
                Dim _bTop171e As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_bData171e, CInt(CLng(szB - 1) * 8L)))
                Dim _bTopBits171 As Integer = 0
                Dim _bTopScan As ULong = _bTop171e
                Do While _bTopScan <> 0UL
                    _bTopBits171 += 1
                    _bTopScan >>= 1
                Loop
                AppendLog($"[SafeMpzDiv§171-entry] szA={szA:N0} szB={szB:N0} szRem={_szRem171:N0} ratio={(CDbl(_szRem171)/szB):F3} bTop=0x{_bTop171e:X16} bTopBits={_bTopBits171} (if <48, single-limb correction will NOT converge — upstream Barrett bug suspected){vbCrLf}")

                Dim _171Pass As Integer = 0
                Do While _szRem171 > szB
                    _171Pass += 1
                    If _171Pass > 64 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 failed to converge in 64 passes (szRem={_szRem171}, szB={szB}, szA={szA})")
                    End If
                    Dim _szRemBefore171 As Integer = _szRem171
                    Dim _remData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
                    Dim _bData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
                    Dim _bTop171 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_bData171, CInt(CLng(szB - 1) * 8L)))
                    Dim _topSliceLen171 As Integer = _szRem171 - szB + 1
                    Dim _deltaBytes171 As Long = CLng(_topSliceLen171) * 8L
                    Dim _deltaBuf171 As IntPtr = GmpNativeAlloc_PoolGet(_deltaBytes171)
                    If _deltaBuf171 = IntPtr.Zero Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pool alloc failed on pass {_171Pass}: requested {_deltaBytes171:N0} bytes")
                    End If
                    Dim _remTopPtr171 As IntPtr = New IntPtr(_remData171.ToInt64() + CLng(szB - 1) * 8L)
                    GmpRaw_mpn_divrem_1(_deltaBuf171, 0, _remTopPtr171, _topSliceLen171, _bTop171 + 1UL)
                    Dim _deltaSz171 As Integer = _topSliceLen171
                    Do While _deltaSz171 > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(_deltaBuf171, CInt(CLng(_deltaSz171 - 1) * 8L)) = 0L
                        _deltaSz171 -= 1
                    Loop
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] bTop=0x{_bTop171:X16} szDelta={_deltaSz171:N0} szRemBefore={_szRemBefore171:N0}{vbCrLf}")
                    If _deltaSz171 = 0 Then
                        GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                        Throw New InvalidOperationException($"SafeMpzDiv §171 delta=0 on pass {_171Pass}: top-limb ratio too small. szRem={_szRem171}, szB={szB}, bTop=0x{_bTop171:X16}")
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
                    _bWrap171.Pointer = _bPtr
                    Dim _deltaWrap171 As New mpz_t()
                    _deltaWrap171.Pointer = _deltaHdr171
                    SafeMpzMul(_prod171, _deltaWrap171, _bWrap171)
                    ' §171-fix: read prod size from _prodHdr171 (captured raw) — _prod171.Pointer may be stale per §175.
                    Dim _szProd171 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_prodHdr171, 4))
                    Dim _ptrMatch171 As Boolean = (_prodHdr171 = _prod171.Pointer)
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] szProd={_szProd171:N0} prodHdr=0x{_prodHdr171.ToInt64():X} prod.Ptr=0x{_prod171.Pointer.ToInt64():X} match={_ptrMatch171}{vbCrLf}")
                    GmpRaw_sub(_remRaw, _remRaw, _prodHdr171)
                    GmpRaw_clear(_prodHdr171)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_prodHdr171)
                    _prod171.Pointer = IntPtr.Zero
                    Runtime.InteropServices.Marshal.FreeHGlobal(_deltaHdr171)
                    GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] done: szRemAfter={_szRem171:N0} Δ={_szRemBefore171 - _szRem171:N0}{vbCrLf}")
                    If _szRem171 >= _szRemBefore171 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pass {_171Pass} did not reduce rem SIZE: before={_szRemBefore171}, after={_szRem171}, szB={szB}, szProd={_szProd171}, ptrMatch={_ptrMatch171}, bTopBits={_bTopBits171}. ROOT CAUSE: Barrett estimate was off by ~2^{(CLng(_szRemBefore171 - szB) * 64L):N0} (far more than the usual ±1-2). Single-limb top-limb correction cannot converge when bTopBits<{48} and rem/b value-ratio is ~2^({(CLng(_szRemBefore171 - szB) * 64L):N0}). Investigate upstream: SafeMpzMul(ar,a,r), BigShiftRight(ar,kBits), SafeMpzReciprocal precision at 5B scale.")
                    End If
                Loop
                AppendLog($"[SafeMpzDiv§171-done] {_171Pass} pass(es); szRem={_szRem171:N0} ≤ szB={szB:N0}{vbCrLf}")
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

        q.Pointer = _qPtr  ' §§78-qptr: restore after adj loops used _qPtr directly
        GmpRaw_clear(_remRaw) : Runtime.InteropServices.Marshal.FreeHGlobal(_remRaw)
        remainder.Pointer = IntPtr.Zero
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
    Private Shared Sub SafeMpzSqrt(result As mpz_t, n As mpz_t)
        Const SAFE As Integer = 33_554_431
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
                    AppendLog($"[SafeMpzSqrt] Resumed from Newton checkpoint: kBitsX={kBitsX:N0} bits{vbCrLf}")
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
        Dim _newtonStep As Integer = 0
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§175] Newton loop entry: kBitsX={kBitsX:N0} bitsS={bitsS:N0} bitsN={bitsN:N0} szN={szN:N0} loop_cond={kBitsX < bitsS + 2L}{vbCrLf}")
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
            Else
                Dim _szX As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xNativePtr, 4))
                PreAllocMpzToLimbs(xTrunc, CLng(_szX))  ' pre-alloc
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
                SafeMpzDiv(q, nTrunc, xTrunc)
            End If
            ' §SqNewton: Use raw GmpRaw ops for cleanup — no managed mpz_clear/mpz_add.
            ' After SafeMpzDiv, all managed mpz_t.Pointer fields in this scope are potentially
            ' corrupted by §78. We use only the captured raw IntPtrs (_nTruncRaw, _xTruncRaw,
            ' _qRaw, _xNativePtr) for all post-SafeMpzDiv operations.
            GmpRaw_clear(_nTruncRaw)                                              ' free nTrunc limb buffer
            nTrunc.Pointer = IntPtr.Zero                                          ' prevent finalizer mpz_clear
            Runtime.InteropServices.Marshal.FreeHGlobal(_nTruncRaw)

            GmpRaw_add(_xTruncRaw, _xTruncRaw, _qRaw)                           ' xTrunc += q
            GmpRaw_clear(_qRaw)                                                   ' free q limb buffer
            q.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_qRaw)
            GmpRaw_tdiv_q_2exp(_xTruncRaw, _xTruncRaw, 1UI)                     ' xTrunc >>= 1

            If xHalf > 0L Then BigShiftLeft(xTrunc, xTrunc, xHalf)
            GmpRaw_swap(_xNativePtr, _xTruncRaw)  ' swap: x's native struct gets new Newton value
            x.Pointer = _xNativePtr               ' restore managed x.Pointer (corrupted by SafeMpzDiv §78)
            n.Pointer = _nNativePtr               ' restore managed n.Pointer for next iteration
            GmpRaw_clear(_xTruncRaw)                                              ' free old x limb buffer
            xTrunc.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_xTruncRaw)
            kBitsX = target

            ' §106: Save Newton step checkpoint immediately after completion.
            If _autoCheckpoint Then
                Try
                    If Not System.IO.Directory.Exists(sqrtSnapDir) Then
                        System.IO.Directory.CreateDirectory(sqrtSnapDir)
                    End If
                    Dim staging(4194303) As Byte
                    Using fs As New FileStream(sqrtCheckBin, FileMode.Create, FileAccess.Write)
                        Using bw As New BinaryWriter(fs)
                            SerializeOneMpz(x, bw, staging)
                        End Using
                    End Using
                    System.IO.File.WriteAllText(sqrtCheckMeta,
                        $"bitsN={bitsN}{vbLf}kBitsX={kBitsX}{vbLf}step={_newtonStep}{vbLf}")
                    BackupSnapshotToStore("snap_Phase3")
                    AppendLog($"[SafeMpzSqrt] Newton step {_newtonStep} checkpoint saved (kBitsX={kBitsX:N0}){vbCrLf}")
                Catch ex As Exception
                    AppendLog($"[SafeMpzSqrt] Newton checkpoint save failed: {ex.Message}{vbCrLf}")
                End Try
            End If
        Loop

        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] Newton done; final adjustment{vbCrLf}")

        ' Final adjustment: ensure result = floor(sqrt(n)) exactly (off by at most 1)
        Dim xSq As New mpz_t()
        gmp_lib.mpz_init(xSq)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] final adj: computing x² to check x²≤n{vbCrLf}")
        SafeMpzMul(xSq, x, x)
        Dim _adjDownSqrt As Integer = 0
        Do While GmpRaw_cmp(xSq.Pointer, n.Pointer) > 0   ' §35: x² > n → x too large
            _adjDownSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] adj-down iter={_adjDownSqrt} (x²>n){vbCrLf}")
            gmp_lib.mpz_sub_ui(x, x, 1UI)
            SafeMpzMul(xSq, x, x)
        Loop
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] adj-down done: {_adjDownSqrt} iter(s){vbCrLf}")
        gmp_lib.mpz_clear(xSq)

        Dim x1 As New mpz_t()
        gmp_lib.mpz_init(x1)
        gmp_lib.mpz_add_ui(x1, x, 1UI)
        Dim x1Sq As New mpz_t()
        gmp_lib.mpz_init(x1Sq)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] final adj: computing (x+1)² to check (x+1)²>n{vbCrLf}")
        SafeMpzMul(x1Sq, x1, x1)
        Dim _adjUpSqrt As Integer = 0
        Do While GmpRaw_cmp(x1Sq.Pointer, n.Pointer) <= 0   ' §35: (x+1)² ≤ n → x too small
            _adjUpSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] adj-up iter={_adjUpSqrt} ((x+1)²≤n){vbCrLf}")
            GmpRaw_swap(x.Pointer, x1.Pointer)  ' §35
            gmp_lib.mpz_add_ui(x1, x, 1UI)
            SafeMpzMul(x1Sq, x1, x1)
        Loop
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] adj-up done: {_adjUpSqrt} iter(s); SafeMpzSqrt done{vbCrLf}")
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
            End Sub)

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
                System.Threading.Volatile.Write(_safeMulDop, 3)  ' §95
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
                    If pairCount >= 2L Then
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
                    If pairCount >= 2L Then
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
                    BackupSnapshotToStore($"snap_L{level}")   ' §104: immediate SnapshotStore backup
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

            ' §103: Declare finalP/Q/T here so both the snap_Phase3-load path and the
            ' Phase 1/2 path can populate them before Phase 3 begins.
            Dim finalP As mpz_t = Nothing
            Dim finalQ As mpz_t = Nothing
            Dim finalT As mpz_t = Nothing
            Dim p3SnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")

            ' §103: If --auto-checkpoint is active and snap_Phase3 exists, load P/Q/T
            ' and jump straight to Phase 3, skipping the 10+ hour Phase 1/2 run.
            If _autoCheckpoint Then
                Dim _p3P As New mpz_t(), _p3Q As New mpz_t(), _p3T As New mpz_t()
                gmp_lib.mpz_inits(_p3P, _p3Q, _p3T, Nothing)
                If TryLoadPhase3Snapshot(p3SnapDir, digits, _p3P, _p3Q, _p3T) Then
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
            If TryLoadPhase3Value("gmpNumer", gmpNumer, p3SnapDir) Then
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
            LogPhase($"[ComputePi] Step 1: SafeMpzPow10(10^{digits:N0})")
            ' §Step1OOM: At 5B digits gmpOne ≈ 259M limbs.  P/Q/T are still resident (~15 GB),
            ' leaving little headroom for 9 parallel shifted buffers (~2 GB each at the last
            ' squaring).  Force serial sub-products for the entire Step 1, same rationale as
            ' §Phase3OOM for Step 2.  Restore _safeMulDop afterwards.
            Dim _savedDopStep1 As Integer = System.Threading.Volatile.Read(_safeMulDop)
            System.Threading.Volatile.Write(_safeMulDop, 1)
            SafeMpzPow10(gmpOne, digits)
            System.Threading.Volatile.Write(_safeMulDop, _savedDopStep1)
            LogPhase($"[ComputePi] Step 1 done: gmpOne={CLng(gmp_lib.mpz_sizeinbase(gmpOne, 10)):N0} digits")
            LogPhase($"[ComputePi] Step 2: SafeMpzMul gmpSqrtInput = gmpOne^2")
            ' §Phase3OOM: Force serial sub-products for the Step 2 squaring.
            ' At 5B digits gmpOne ≈ 130M limbs (1 GB).  With _safeMulDop=24 the 9 sub-products
            ' run concurrently; each is ~43M×43M limbs → ~700 MB, so 9 in parallel = ~6 GB
            ' of simultaneous allocation on top of ~22 GB already in use → silent OOM crash
            ' (Windows terminates the process when VirtualAlloc fails with no managed exception).
            ' Forcing serial sub-products reduces peak concurrent memory to 1 sub-product at a
            ' time (~700 MB extra), allowing the squaring to complete safely.
            ' Restore _safeMulDop after Step 2 so subsequent SafeMpzSqrt/SafeMpzMul calls
            ' continue to use all cores where memory pressure is lower.
            Dim _savedDopStep2 As Integer = System.Threading.Volatile.Read(_safeMulDop)
            System.Threading.Volatile.Write(_safeMulDop, 1)
            SafeMpzMul(gmpSqrtInput, gmpOne, gmpOne)
            System.Threading.Volatile.Write(_safeMulDop, _savedDopStep2)
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
            LogPhase($"[ComputePi] Step 4: SafeMpzSqrt of {CLng(gmp_lib.mpz_sizeinbase(gmpSqrtInput, 10)):N0}-digit number")
            SafeMpzSqrt(gmpSqrt, gmpSqrtInput)
            gmp_lib.mpz_clear(gmpSqrtInput)
            LogPhase("Square root complete")

            If token.IsCancellationRequested Then Return ""

            If _logLevel >= 2 Then WriteToLog($"[ComputePi] mpz_mul_ui: gmpNumer = gmpSqrt * 426880")
            gmp_lib.mpz_mul_ui(gmpNumer, gmpSqrt, 426880UI)
            ' §SqrtDone-ckpt: Save gmpNumer immediately after the sqrt so a crash during
            ' Steps 5+ can restart from here (skipping all 10+ hours of Steps 1–4).
            ' Restore gmpNumer.Pointer first — §78 corrupted it during mpz_mul_ui above.
            gmpNumer.Pointer = _gmpNumerRaw
            SavePhase3Value("gmpNumer", gmpNumer, p3SnapDir)
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
            ' Only pre-alloc when the result buffer exceeds GMP_LARGE_THRESHOLD.
            ' Below the threshold the init2 buffer (524 KB, VirtualAlloc'd) is already
            ' large enough; replacing it with a smaller VirtualAlloc'd buffer would cause
            ' GmpFreeFunc to route the later free through _savedGmpFree (CRT free) on a
            ' VirtualAlloc'd pointer — crashing on small digit counts (< ~200K digits).
            If _tHBytes >= GMP_LARGE_THRESHOLD Then
                Dim _tHBuf As IntPtr = PoolGet(_tHBytes)  ' §79: PoolGet so pool bucket capacity matches actual allocation
                If _tHBuf <> IntPtr.Zero Then
                    Dim _tHOld As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(tmpHigh.Pointer, 8))
                    VirtualFree(_tHOld, UIntPtr.Zero, MEM_RELEASE)  ' free the init2 seed buffer
                    Runtime.InteropServices.Marshal.WriteInt32(tmpHigh.Pointer, 0, CInt(_tHNeeded))
                    Runtime.InteropServices.Marshal.WriteInt64(tmpHigh.Pointer, 8, _tHBuf.ToInt64())
                    WriteToLog($"[3PM-DBG] tmpHigh pre-alloc {_tHNeeded:N0} limbs ({_tHBytes \ 1048576L:N0} MB) ptr={_tHBuf:X}")
                Else
                    WriteToLog($"[3PM-DBG] tmpHigh pre-alloc FAILED for {_tHBytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[3PM-DBG] tmpHigh pre-alloc skipped ({_tHNeeded:N0} limbs, {_tHBytes:N0} B < GMP threshold; init2 buffer sufficient)")
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
            If _q1Bytes >= GMP_LARGE_THRESHOLD Then
                Dim _q1Buf As IntPtr = PoolGet(_q1Bytes)  ' §79
                If _q1Buf <> IntPtr.Zero Then
                    Dim _q1Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpQ1.Pointer, 8))
                    VirtualFree(_q1Old, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.WriteInt32(mpQ1.Pointer, 0, CInt(_q1Needed))
                    Runtime.InteropServices.Marshal.WriteInt64(mpQ1.Pointer, 8, _q1Buf.ToInt64())
                    WriteToLog($"[3PM-DBG] mpQ1 pre-alloc {_q1Needed:N0} limbs ({_q1Bytes \ 1048576L:N0} MB)")
                Else
                    WriteToLog($"[3PM-DBG] mpQ1 pre-alloc FAILED for {_q1Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[3PM-DBG] mpQ1 pre-alloc skipped ({_q1Needed:N0} limbs, {_q1Bytes:N0} B < GMP threshold; init2 buffer sufficient)")
            End If
            Dim mpQ2 As New mpz_t()
            gmp_lib.mpz_init2(mpQ2, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _q2Needed As Long = _k1Limbs + 2L  ' same upper bound as Q1
            Dim _q2Bytes As Long = _q2Needed * 8L
            If _q2Bytes >= GMP_LARGE_THRESHOLD Then
                Dim _q2Buf As IntPtr = PoolGet(_q2Bytes)  ' §79
                If _q2Buf <> IntPtr.Zero Then
                    Dim _q2Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpQ2.Pointer, 8))
                    VirtualFree(_q2Old, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.WriteInt32(mpQ2.Pointer, 0, CInt(_q2Needed))
                    Runtime.InteropServices.Marshal.WriteInt64(mpQ2.Pointer, 8, _q2Buf.ToInt64())
                    WriteToLog($"[3PM-DBG] mpQ2 pre-alloc {_q2Needed:N0} limbs ({_q2Bytes \ 1048576L:N0} MB)")
                Else
                    WriteToLog($"[3PM-DBG] mpQ2 pre-alloc FAILED for {_q2Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[3PM-DBG] mpQ2 pre-alloc skipped ({_q2Needed:N0} limbs, {_q2Bytes:N0} B < GMP threshold; init2 buffer sufficient)")
            End If
            WriteToLog($"[3PM-DBG] about to extract Q1/Q2 from tmpHigh")
            gmp_lib.mpz_tdiv_r_2exp(mpQ1, tmpHigh, k1)   ' mpQ1 = Q1
            WriteToLog($"[3PM-DBG] mpQ1._mp_size={Runtime.InteropServices.Marshal.ReadInt32(mpQ1.Pointer, 4):N0}  about to tdiv_q_2exp(mpQ2, tmpHigh, k1)")
            gmp_lib.mpz_tdiv_q_2exp(mpQ2, tmpHigh, k1)   ' mpQ2 = Q2
            WriteToLog($"[3PM-DBG] mpQ2._mp_size={Runtime.InteropServices.Marshal.ReadInt32(mpQ2.Pointer, 4):N0}  clearing tmpHigh")
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
            Dim _r0Bytes As Long = _r0Needed * 8L
            If _r0Bytes >= GMP_LARGE_THRESHOLD Then
                Dim _r0Buf As IntPtr = PoolGet(_r0Bytes)  ' §79
                If _r0Buf <> IntPtr.Zero Then
                    Dim _r0Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpR0.Pointer, 8))
                    VirtualFree(_r0Old, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.WriteInt32(mpR0.Pointer, 0, CInt(_r0Needed))
                    Runtime.InteropServices.Marshal.WriteInt64(mpR0.Pointer, 8, _r0Buf.ToInt64())
                    WriteToLog($"[ComputePi] mpR0 pre-alloc {_r0Needed:N0} limbs ({_r0Bytes \ 1048576L:N0} MB)")
                Else
                    WriteToLog($"[ComputePi] mpR0 pre-alloc FAILED for {_r0Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[ComputePi] mpR0 pre-alloc skipped ({_r0Needed:N0} limbs, {_r0Bytes:N0} B < GMP threshold; init2 buffer sufficient)")
            End If

            gmp_lib.mpz_init2(mpR1, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _r1Needed As Long = _numerSz + _q1SzR + 2L
            Dim _r1Bytes As Long = _r1Needed * 8L
            If _r1Bytes >= GMP_LARGE_THRESHOLD Then
                Dim _r1Buf As IntPtr = PoolGet(_r1Bytes)  ' §79
                If _r1Buf <> IntPtr.Zero Then
                    Dim _r1Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpR1.Pointer, 8))
                    VirtualFree(_r1Old, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.WriteInt32(mpR1.Pointer, 0, CInt(_r1Needed))
                    Runtime.InteropServices.Marshal.WriteInt64(mpR1.Pointer, 8, _r1Buf.ToInt64())
                    WriteToLog($"[ComputePi] mpR1 pre-alloc {_r1Needed:N0} limbs ({_r1Bytes \ 1048576L:N0} MB)")
                Else
                    WriteToLog($"[ComputePi] mpR1 pre-alloc FAILED for {_r1Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[ComputePi] mpR1 pre-alloc skipped ({_r1Needed:N0} limbs, {_r1Bytes:N0} B < GMP threshold; init2 buffer sufficient)")
            End If

            gmp_lib.mpz_init2(mpR2, New mp_bitcnt_t(CUInt(GMP_LARGE_THRESHOLD * 8L)))
            Dim _r2Needed As Long = _numerSz + _q2SzR + 2L
            Dim _r2Bytes As Long = _r2Needed * 8L
            If _r2Bytes >= GMP_LARGE_THRESHOLD Then
                Dim _r2Buf As IntPtr = PoolGet(_r2Bytes)  ' §79
                If _r2Buf <> IntPtr.Zero Then
                    Dim _r2Old As New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(mpR2.Pointer, 8))
                    VirtualFree(_r2Old, UIntPtr.Zero, MEM_RELEASE)
                    Runtime.InteropServices.Marshal.WriteInt32(mpR2.Pointer, 0, CInt(_r2Needed))
                    Runtime.InteropServices.Marshal.WriteInt64(mpR2.Pointer, 8, _r2Buf.ToInt64())
                    WriteToLog($"[ComputePi] mpR2 pre-alloc {_r2Needed:N0} limbs ({_r2Bytes \ 1048576L:N0} MB)")
                Else
                    WriteToLog($"[ComputePi] mpR2 pre-alloc FAILED for {_r2Bytes \ 1048576L:N0} MB — will rely on GmpReallocFunc")
                End If
            Else
                WriteToLog($"[ComputePi] mpR2 pre-alloc skipped ({_r2Needed:N0} limbs, {_r2Bytes:N0} B < GMP threshold; init2 buffer sufficient)")
            End If

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
                WriteToLog("[ComputePi] §61 calling r0=N*Q0, r1=N*Q1, r2=N*Q2 in parallel...")
                System.Threading.Tasks.Parallel.Invoke(
                    Sub() SafeMpzMul(mpR0, gmpNumer, finalQ),
                    Sub() SafeMpzMul(mpR1, gmpNumer, mpQ1),
                    Sub() SafeMpzMul(mpR2, gmpNumer, mpQ2))
                WriteToLog("[ComputePi] §61 parallel r0/r1/r2 done")
                gmp_lib.mpz_clears(finalQ, mpQ1, mpQ2, Nothing)
                ' §106 checkpoint: save all three immediately so a later crash can reload.
                SavePhase3Value("mpR0", mpR0, p3SnapDir)
                SavePhase3Value("mpR1", mpR1, p3SnapDir)
                SavePhase3Value("mpR2", mpR2, p3SnapDir)
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
                If _shiftBytesA >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufA As IntPtr = PoolGet(_shiftBytesA)  ' §79
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
                WriteToLog($"[ComputePi] Combine A: mpz_mul_2exp  k={CLng(k1):N0} bits  gmpNumer={CLng(gmp_lib.mpz_sizeinbase(gmpNumer, 2)):N0} bits")
            End If
            gmp_lib.mpz_mul_2exp(mpShiftA, gmpNumer, k1)
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine A: mpz_mul_2exp returned OK")
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
                Dim _addBytesB As Long = _addLimbs * 8L
                If _addBytesB >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufB As IntPtr = PoolGet(_addBytesB)  ' §79
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
                Dim _shiftBytesC As Long = _shiftLimbs * 8L
                If _shiftBytesC >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufC As IntPtr = PoolGet(_shiftBytesC)  ' §79
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
            If _logLevel >= 3 Then WriteToLog($"[ComputePi] Combine C: mpz_mul_2exp  k={CLng(k1):N0} bits")
            gmp_lib.mpz_mul_2exp(mpShiftC, gmpNumer, k1)
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
                Dim _addBytesD As Long = _addLimbs * 8L
                If _addBytesD >= GMP_LARGE_THRESHOLD Then
                    Dim _bigBufD As IntPtr = PoolGet(_addBytesD)  ' §79
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
            SafeMpzDiv(gmpPi, gmpNumer, finalT)   ' §107 Gap 6: operands ~5B+ limbs — mpz_tdiv_q hits mpn_mul_fft overflow
            gmp_lib.mpz_clears(gmpNumer, finalT, Nothing)
            LogPhase("Division complete")

            If token.IsCancellationRequested Then Return ""

            If _logLevel >= 2 Then WriteToLog($"[ComputePi] mpz_get_str: converting result to string")
            Dim _strConvStart As DateTime = DateTime.Now
            Dim _strConvTimer As New System.Threading.Timer(
                Sub(state As Object)
                    Dim elapsed As TimeSpan = DateTime.Now - _strConvStart
                    Me.BeginInvoke(Sub()
                                       LblStatus.Text = $"String conversion... {elapsed:mm\:ss} elapsed"
                                   End Sub)
                End Sub, Nothing, 1000, 1000)
            Dim piCharPtr As char_ptr
            Dim _strConvSw As New Diagnostics.Stopwatch()
            _strConvSw.Start()
            Try
                piCharPtr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, gmpPi)
            Finally
                _strConvTimer.Dispose()
            End Try
            _strConvSw.Stop()
            WriteToLog($"[ComputePi] mpz_get_str completed in {_strConvSw.Elapsed:mm\:ss\.fff}")
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
            If ChkAutoVerify.Checked Then RunVerification()
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
        Dim piText As String
        If _displayNativePtr <> IntPtr.Zero Then
            piText = Runtime.InteropServices.Marshal.PtrToStringAnsi(_displayNativePtr)
            WriteToLog("[Verify] native pi buffer searched (buffer retained for display)")
        Else
            piText = RtbPiDigits.Text.Replace(".", "").Replace(vbCrLf, "")
        End If

        ' ── Built-in checks ──────────────────────────────────────────────────
        Dim parts As New System.Collections.Generic.List(Of String)()
        Dim allOk As Boolean = True

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

        ' §29: nine-9s replaces e-digits check (e-digits don't appear until 45B+ digits)
        ' piText has the '.' stripped, so piText[0]='3' and piText[n] = n-th decimal digit (1-indexed).
        ' Nine consecutive 9s first appear at 1-indexed decimal position 564,665,206 → piText[564665206].
        ' (Previous hardcoded value 564665205 was off by 1 — it was the file offset minus 2 rather
        ' than the correct piText index.)
        Dim pos3 As Integer = piText.IndexOf("999999999")
        If pos3 = 564665206 Then
            parts.Add("nine-9s@564,665,206 OK")
        ElseIf pos3 >= 0 Then
            parts.Add($"nine-9s at {pos3} (expected 564665206) FAIL")
            allOk = False
        Else
            parts.Add("nine-9s not found (need 564M+ digits)")
        End If

        Dim summary As String = If(allOk, "Verify OK: ", "Verify: ") & String.Join(" | ", parts)
        LblStatus.Text = summary
        WriteToLog("[Verify] " & summary)

        ' ── Custom --verify-at / --verify-contains checks ────────────────────
        If _verifyAt.Count > 0 OrElse _verifyContains.Count > 0 Then
            RunCustomVerifications(piText)
        End If
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
