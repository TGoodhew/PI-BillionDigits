Imports System.IO
Imports System.Diagnostics
Imports Math.Gmp.Native

' ════════════════════════════════════════════════════════════════════════════
'  §263 (issue #88): Memory-bandwidth / DOP-saturation microbenchmark.
'
'  The #88 premise is that at 5B the §gen multiply is memory-bandwidth-bound — the
'  9 sub-products fan out across cores but contend on a single DDR5 pool, so cores
'  sit stalled on loads (live 5B trace: ~37% cores active).  The issue's headline
'  lanes (NUMA / per-channel pinning) are NOT achievable on a single-socket,
'  dual-channel desktop: one NUMA node ⇒ VirtualAllocExNuma has nothing to target,
'  and the integrated controller interleaves the two channels at cache-line
'  granularity with no software API to pin an allocation to a channel.
'
'  What IS actionable here: measure the bandwidth-saturation DOP knee.  If §gen
'  throughput plateaus before the current DOP=9, the extra threads burn cores +
'  contend on the memory controller for ~zero throughput — capping DOP at the knee
'  is a real win (cores/power freed, possibly less contention) even without
'  breaking the ceiling.  --test-dopscan times one large L3-overflowing §gen mul
'  across DOP values and prints ms / speedup / parallel-efficiency, flagging the
'  knee (last DOP whose added threads still buy ≥10% more throughput).
'
'  No math-path change; pure measurement.  Size via PI_DOPSCAN_LIMBS (default 24M
'  limbs/operand = 192 MB, ~6× the 30 MB L3 ⇒ firmly bandwidth-bound).
' ════════════════════════════════════════════════════════════════════════════
Partial Class Form1

    Friend Shared Function TestDopScan() As Boolean
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dopscan_test.txt")
        Dim log As Action(Of String) =
            Sub(s)
                Try : System.IO.File.AppendAllText(outPath, s & vbCrLf) : Catch : End Try
                AppendLog($"[DopScan§263] {s}{vbCrLf}", 1)
            End Sub
        Try : System.IO.File.WriteAllText(outPath, $"[TestDopScan] start {DateTime.Now}{vbCrLf}") : Catch : End Try

        Dim n As Integer = 24_000_000
        Dim envN As String = Environment.GetEnvironmentVariable("PI_DOPSCAN_LIMBS")
        Dim parsedN As Integer
        If envN IsNot Nothing AndAlso Integer.TryParse(envN, parsedN) AndAlso parsedN >= 6_000_000 Then n = parsedN

        log($"operands: {n:N0} × {n:N0} limbs ({CLng(n) * 8L \ 1048576L:N0} MB each); L3≈30 MB ⇒ ~{CLng(n) * 8L \ 1048576L \ 30L}× overflow")
        Dim rng As New Random(20260604)
        Dim a As New mpz_t() : a.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(a, n, rng)
        Dim b As New mpz_t() : b.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(b, n, rng)
        Dim r As New mpz_t() : r.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(r.Pointer)
        Dim savedDop As Integer = System.Threading.Volatile.Read(_safeMulDop)

        ' Warm up (page-in, pool buffers, JIT) at full DOP — not timed.
        System.Threading.Volatile.Write(_safeMulDop, 9)
        SafeMpzMul(r, a, b)

        Dim dops() As Integer = {1, 2, 3, 4, 6, 8, 9}
        Dim ms(dops.Length - 1) As Double
        Dim baseMs As Double = 0.0
        log("  DOP |    ms   | speedup | par-eff | Δ-thru/thread")
        log("  ----+---------+---------+---------+--------------")
        Dim prevSpeedup As Double = 0.0, prevDop As Integer = 0
        For i As Integer = 0 To dops.Length - 1
            Dim dop As Integer = dops(i)
            If _statusHook IsNot Nothing Then _statusHook($"DopScan: DOP {dop}/9 on {n:N0}×{n:N0} §gen… ({i + 1}/{dops.Length})")
            System.Threading.Volatile.Write(_safeMulDop, dop)
            Dim best As Double = Double.MaxValue
            For rep As Integer = 0 To 1
                GC.Collect() : GC.WaitForPendingFinalizers()
                Dim sw As Stopwatch = Stopwatch.StartNew()
                SafeMpzMul(r, a, b)
                sw.Stop()
                best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds)
            Next
            ms(i) = best
            If dop = 1 Then baseMs = best
            Dim speedup As Double = If(best > 0.0, baseMs / best, 0.0)
            Dim eff As Double = speedup / dop
            Dim margin As Double = If(dop > prevDop, (speedup - prevSpeedup) / CDbl(dop - prevDop), 0.0)
            log($"  {dop,3} | {best,7:F0} | {speedup,6:F2}× | {eff,6:F2} | {If(dop = 1, "  (base)", margin.ToString("F2") & "×/thr")}")
            prevSpeedup = speedup : prevDop = dop
        Next

        ' Knee = the highest DOP whose marginal throughput per added thread is still ≥ 0.10×
        ' (i.e. each extra thread buys ≥10% of one core's ideal work). Beyond it = saturated.
        Dim knee As Integer = 1, prevS As Double = 1.0, prevD As Integer = 1
        For i As Integer = 1 To dops.Length - 1
            Dim s As Double = If(ms(i) > 0.0, baseMs / ms(i), 0.0)
            Dim m As Double = (s - prevS) / CDbl(dops(i) - prevD)
            If m >= 0.10 Then knee = dops(i)
            prevS = s : prevD = dops(i)
        Next
        Dim full As Double = If(ms(dops.Length - 1) > 0.0, baseMs / ms(dops.Length - 1), 0.0)
        log($"SATURATION KNEE ≈ DOP {knee} (best speedup {full:F2}× at DOP 9; ideal would be 9×).")
        If knee < 9 Then
            log($"⇒ DOP 9 is PAST the knee: threads {knee + 1}–9 add ~0 throughput but burn cores/power +")
            log($"  contend on the memory controller. Capping §gen DOP near {knee} is a free efficiency win.")
        Else
            log("⇒ throughput still scaling at DOP 9 on this size — not yet bandwidth-saturated here.")
        End If

        System.Threading.Volatile.Write(_safeMulDop, savedDop)
        Try
            gmp_lib.mpz_clear(a) : gmp_lib.mpz_clear(b) : gmp_lib.mpz_clear(r)
            Runtime.InteropServices.Marshal.FreeHGlobal(a.Pointer)
            Runtime.InteropServices.Marshal.FreeHGlobal(b.Pointer)
            Runtime.InteropServices.Marshal.FreeHGlobal(r.Pointer)
        Catch
        End Try
        log($"[TestDopScan] done {DateTime.Now}")
        Return True   ' measurement harness — always 'passes'; the curve is the output
    End Function

    ' ════════════════════════════════════════════════════════════════════════
    '  §265 (#88): split-factor experiment.  §gen uses a 3×3 split = 9 sub-products
    '  ⇒ only 9 cores; 15 of 24 idle by design.  This compares the production §gen
    '  3×3 against the chunked-grid full product driven at coarser k×k grids (cell ≈
    '  N/k via _cgCellOverride): k=3 (9 cells), 4 (16), 5 (25), 6 (36) — i.e. using
    '  up to PI_CG_DOP cores.  Question: does a finer top-level split (more cells ⇒
    '  more cores) beat the 9-way split, or does the extra DDR5 contention + per-cell
    '  overhead eat the gain?  Each grid is checked bit-identical to §gen.
    '  (Coarse cells are FFT-safe only at benchmark sizes; at 5B N/k overflows the
    '  33M-limb FFT limit — so this answers the structural question, it is not a 5B
    '  drop-in.  PI_DOPSCAN_LIMBS sets the size; default 24M keeps cells FFT-safe.)
    ' ════════════════════════════════════════════════════════════════════════
    Friend Shared Function TestGridScan() As Boolean
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gridscan_test.txt")
        Dim log As Action(Of String) =
            Sub(s)
                Try : System.IO.File.AppendAllText(outPath, s & vbCrLf) : Catch : End Try
                AppendLog($"[GridScan§265] {s}{vbCrLf}", 1)
            End Sub
        Try : System.IO.File.WriteAllText(outPath, $"[TestGridScan] start {DateTime.Now}{vbCrLf}") : Catch : End Try

        Dim n As Integer = 24_000_000
        Dim envN As String = Environment.GetEnvironmentVariable("PI_DOPSCAN_LIMBS")
        Dim parsedN As Integer
        If envN IsNot Nothing AndAlso Integer.TryParse(envN, parsedN) AndAlso parsedN >= 6_000_000 Then n = parsedN

        log($"operands: {n:N0} × {n:N0} limbs ({CLng(n) * 8L \ 1048576L:N0} MB each); cores={Environment.ProcessorCount}")
        Dim rng As New Random(20260604)
        Dim a As New mpz_t() : a.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(a, n, rng)
        Dim b As New mpz_t() : b.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(b, n, rng)
        Dim rRef As New mpz_t() : rRef.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(rRef.Pointer)
        Dim rTest As New mpz_t() : rTest.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(rTest.Pointer)
        Dim savedDop As Integer = System.Threading.Volatile.Read(_safeMulDop)

        Dim timeIt As Func(Of Action, Double) =
            Function(act)
                Dim best As Double = Double.MaxValue
                For rep As Integer = 0 To 1
                    GC.Collect() : GC.WaitForPendingFinalizers()
                    Dim sw As Stopwatch = Stopwatch.StartNew()
                    act() : sw.Stop()
                    best = System.Math.Min(best, sw.Elapsed.TotalMilliseconds)
                Next
                Return best
            End Function

        ' Baseline: production §gen 3×3 (recursive), at DOP=9.  rRef = reference product.
        System.Threading.Volatile.Write(_safeMulDop, 9)
        If _statusHook IsNot Nothing Then _statusHook("GridScan: §gen 3×3 baseline (warmup)…")
        SafeMpzMul(rRef, a, b)   ' warmup + reference
        If _statusHook IsNot Nothing Then _statusHook("GridScan: §gen 3×3 baseline (timing)…")
        Dim genMs As Double = timeIt(Sub() SafeMpzMul(rRef, a, b))
        log("  method        | cells |    ms   | vs §gen | bit-exact")
        log("  --------------+-------+---------+---------+----------")
        log($"  §gen 3×3 rec  |     9 | {genMs,7:F0} |  1.00×  |  (ref)")

        Dim allMatch As Boolean = True
        Dim ks() As Integer = {3, 4, 5, 6}
        For Each k As Integer In ks
            Dim cell As Integer = CInt((CLng(n) + k - 1L) \ CLng(k))   ' ≈ N/k ⇒ k×k cells
            If _statusHook IsNot Nothing Then _statusHook($"GridScan: chunked {k}×{k} grid (cell {cell:N0})…")
            _cgCellOverride = cell
            Dim gms As Double = timeIt(Sub() SafeMpzMul_ChunkedGrid(rTest, a, b, 0L))
            Dim match As Boolean = (GmpRaw_cmp(rTest.Pointer, rRef.Pointer) = 0)
            If Not match Then allMatch = False
            Dim cells As Integer = k * k
            Dim su As Double = If(gms > 0.0, genMs / gms, 0.0)
            log($"  chunked {k}×{k}   | {cells,5} | {gms,7:F0} | {su,5:F2}×  | {If(match, "yes", "NO ***")}")
        Next
        _cgCellOverride = 0
        System.Threading.Volatile.Write(_safeMulDop, savedDop)

        ' Verdict: did any coarser grid beat §gen 3×3?
        log($"NOTE: §gen 3×3 is recursive (sub-products re-split); chunked k×k cells are FLAT GMP muls.")
        log($"Bit-exact vs §gen: {If(allMatch, "ALL grids match", "MISMATCH — see *** above")}.")

        ' No explicit mpz_clear: SafeMpzMul_ChunkedGrid swaps a raw GmpNativeAlloc block into rTest,
        ' which GMP's free-function doesn't match (native AV).  The harness exits immediately ⇒ OS
        ' reclaims.  (§266 — fixed the teardown segfault here too.)
        log($"[TestGridScan] done {DateTime.Now}")
        Return allMatch   ' fail the harness if a grid is not bit-identical to §gen
    End Function

    ' ════════════════════════════════════════════════════════════════════════
    '  §266 (#88): CELL-SIZE sweep at 5B operand sizes.  §gridscan showed coarse
    '  flat cells beat §gen at 24M, but 8M cells only stay FFT-safe up to ~48M
    '  operands.  At 5B (operands ≈260M limbs) cells must be ≤~16M (cell·cell ≤
    '  33M-limb GMP-FFT limit).  This sweeps the chunked grid over cell sizes
    '  {1.5M (production), 4M, 8M, 16M} at a large N and compares vs §gen — to see
    '  whether ≤16M cells still beat §gen at 5B, i.e. whether the production grid's
    '  1.5M cell leaves a large win on the table.  Each result is bit-checked vs
    '  §gen.  Size via PI_DOPSCAN_LIMBS (default 260M = the 5B q×b shape).
    ' ════════════════════════════════════════════════════════════════════════
    Friend Shared Function TestCellSweep() As Boolean
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cellsweep_test.txt")
        Dim log As Action(Of String) =
            Sub(s)
                Try : System.IO.File.AppendAllText(outPath, s & vbCrLf) : Catch : End Try
                AppendLog($"[CellSweep§266] {s}{vbCrLf}", 1)
            End Sub
        Try : System.IO.File.WriteAllText(outPath, $"[TestCellSweep] start {DateTime.Now}{vbCrLf}") : Catch : End Try

        Dim n As Integer = 260_000_000   ' 5B q×b ≈ 260M × 260M limbs
        Dim envN As String = Environment.GetEnvironmentVariable("PI_DOPSCAN_LIMBS")
        Dim parsedN As Integer
        If envN IsNot Nothing AndAlso Integer.TryParse(envN, parsedN) AndAlso parsedN >= 6_000_000 Then n = parsedN

        log($"operands: {n:N0} × {n:N0} limbs ({CLng(n) * 8L \ 1048576L:N0} MB each); cores={Environment.ProcessorCount}")
        log($"  availPhys={MemBudget_AvailablePhysicalGB():F1}GB availCommit={MemBudget_AvailableCommitGB():F1}GB")
        If _statusHook IsNot Nothing Then _statusHook($"CellSweep: filling 2×{n:N0}-limb operands…")
        Dim rng As New Random(20260604)
        Dim a As New mpz_t() : a.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(a, n, rng)
        Dim b As New mpz_t() : b.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : FillRandomMpz(b, n, rng)
        Dim rRef As New mpz_t() : rRef.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(rRef.Pointer)
        Dim rTest As New mpz_t() : rTest.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16) : GmpRaw_init(rTest.Pointer)
        Dim savedDop As Integer = System.Threading.Volatile.Read(_safeMulDop)

        Dim oneShot As Func(Of Action, Double) =
            Function(act)
                GC.Collect() : GC.WaitForPendingFinalizers()
                Dim sw As Stopwatch = Stopwatch.StartNew()
                act() : sw.Stop()
                Return sw.Elapsed.TotalMilliseconds   ' 1 rep at 5B sizes (each mul is minutes)
            End Function

        ' REFERENCE = chunked grid at 1.5M (the PRODUCTION cell size), RAM-safe.  Bigger cells are
        ' bit-checked against it and reported as "vs 1.5M" — directly answering "does raising the cell
        ' help at 5B?".  §gen is RAM-bound at the 5B q×b size (260M § peak ≈36 GB ⇒ pages on a 64 GB
        ' box, observed thrashing >30 min), so it is OPT-IN (PI_CELLSWEEP_GEN=1) and runs LAST — the
        ' chunked cell-size answer lands first regardless.
        Dim allMatch As Boolean = True
        Dim refMs As Double = 0.0
        Dim cells() As Integer = {1_500_000, 4_000_000, 8_000_000, 16_000_000}
        log("  cell  | cells |    ms    | vs 1.5M | bit-exact")
        log("  ------+-------+----------+---------+----------")
        For Each cell As Integer In cells
            If CLng(cell) > CLng(n) Then Continue For                       ' cell can't exceed operand
            If CLng(cell) * 2L > 33_000_000L Then Continue For              ' keep cell·cell product FFT-safe
            Dim ncell As Long = (CLng(n) + cell - 1L) \ CLng(cell)
            Dim isRef As Boolean = (refMs = 0.0)                            ' first (1.5M) is the reference
            If _statusHook IsNot Nothing Then _statusHook($"CellSweep: cell {cell:N0} ({ncell}×{ncell}) on {n:N0}²…")
            log($"[{DateTime.Now:HH:mm:ss}] cell={cell:N0} ({ncell}×{ncell}={ncell * ncell} cells) starting…")
            _cgCellOverride = cell
            Dim dst As mpz_t = If(isRef, rRef, rTest)
            Dim cms As Double = oneShot(Sub() SafeMpzMul_ChunkedGrid(dst, a, b, 0L))
            Dim match As Boolean = isRef OrElse (GmpRaw_cmp(rTest.Pointer, rRef.Pointer) = 0)
            If Not match Then allMatch = False
            If isRef Then refMs = cms
            Dim su As Double = If(cms > 0.0, refMs / cms, 0.0)
            log($"  {cell \ 1_000_000,4}M  | {ncell * ncell,5} | {cms,8:F0} | {su,5:F2}×  | {If(isRef, "(ref)", If(match, "yes", "NO ***"))}")
        Next
        _cgCellOverride = 0

        ' Optional §gen baseline (recursive) — RAM-bound at 5B sizes; opt-in so it never blocks the run.
        If Environment.GetEnvironmentVariable("PI_CELLSWEEP_GEN") = "1" Then
            System.Threading.Volatile.Write(_safeMulDop, 9)
            If _statusHook IsNot Nothing Then _statusHook($"CellSweep: §gen baseline on {n:N0}² (RAM-bound, may page)…")
            log($"[{DateTime.Now:HH:mm:ss}] §gen baseline starting (opt-in; may thrash/page)…")
            Dim genMs As Double = oneShot(Sub() SafeMpzMul(rTest, a, b))
            Dim gmatch As Boolean = (GmpRaw_cmp(rTest.Pointer, rRef.Pointer) = 0)
            If Not gmatch Then allMatch = False
            log($"  §gen  |    -- | {genMs,8:F0} | {If(refMs > 0.0, refMs / genMs, 0.0),5:F2}×  | {If(gmatch, "yes", "NO ***")}  (recursive)")
        End If
        System.Threading.Volatile.Write(_safeMulDop, savedDop)
        log($"Bit-exact vs chunked-1.5M ref: {If(allMatch, "ALL match", "MISMATCH — see *** above")}.")
        log("Reading: vs-1.5M >1× AND bit-exact ⇒ the production 1.5M cell is leaving that speedup on")
        log("the table at 5B — raise the chunked cell toward the ~16M FFT-safe maximum.")

        ' NOTE: no explicit mpz_clear here — SafeMpzMul_ChunkedGrid swaps a raw GmpNativeAlloc block
        ' into rTest's _mp_d, which GMP's mpz_clear free-function does not match (native AV, bypasses
        ' Try/Catch).  The harness Environment.Exit's immediately, so the OS reclaims everything; an
        ' explicit clear would only risk a teardown segfault after the data is already written.
        log($"[TestCellSweep] done {DateTime.Now}")
        Return allMatch
    End Function

End Class
