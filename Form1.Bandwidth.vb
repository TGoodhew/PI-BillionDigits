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

End Class
