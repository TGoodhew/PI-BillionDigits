Imports System.Numerics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Collections.Concurrent
Imports System.Runtime.InteropServices
Imports Math.Gmp.Native
Imports System.Diagnostics
Imports System.Security.Cryptography

' §277 (#114): split out of Form1.vb (pure file-move, no logic change).
Partial Class Form1

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
                Dim _etaTick As Integer = 0
                While Interlocked.Read(completedChunks) < numChunks
                    Dim snap As Long = Interlocked.Read(completedChunks)
                    Me.BeginInvoke(Sub()
                                       LblStatus.Text = $"Phase 1: {snap:N0} / {numChunks:N0} chunks ({snap * 100L \ numChunks:N0}%)"
                                   End Sub)
                    ' §126 (#126): Phase 1 has a TRUE progress ratio (chunks done / total) — feed it as a
                    ' sound live ETA fraction (throttled ~20 s to keep the [ETA§259] log readable).
                    If _etaTick Mod 40 = 0 AndAlso numChunks > 0 Then Try : Eta_Refresh(RunStageId.Phase1, System.Math.Min(1.0, CDbl(snap) / CDbl(numChunks))) : Catch : End Try
                    _etaTick += 1
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
                        Dim _etaTick As Integer = 0
                        While Interlocked.Read(completedPairs) < pairCount
                            Dim snap As Long = Interlocked.Read(completedPairs)
                            Me.BeginInvoke(Sub()
                                LblStatus.Text = $"Phase 2 Level {level}: {snap:N0} / {pairCount:N0} pairs"
                            End Sub)
                            ' §126 (#126): give the combine a declining window-title ETA (was a multi-hour
                            ' no-ETA gap).  Throttled to ~20 s so the [ETA§259] log isn't spammed; the
                            ' projector uses the Phase-2 stage cost (history, else scaled default) − elapsed.
                            If _etaTick Mod 40 = 0 Then Try : Eta_Refresh(Eta_NextStage(), 0.0) : Catch : End Try
                            _etaTick += 1
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

            Dim memNow As Long = Process.GetCurrentProcess().WorkingSet64 \ BYTES_PER_MB
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
End Class
