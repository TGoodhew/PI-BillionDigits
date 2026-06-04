' ════════════════════════════════════════════════════════════════════════════
'  §260 (issue #63): Performance advisor — a conservative rules engine over the
'  observed run metrics + hardware fingerprint (§258).
'
'  Philosophy (from the issue): a WRONG recommendation is worse than none.  Every
'  rule fires only when its inputs are KNOWN; ambiguous/absent data yields an
'  informational "more data needed" line, never a fabricated number.  Savings are
'  shown as ranges, scaled from the 2026-05-10 audit's 1 B/5 B figures by run
'  length — never averaged into false precision.
'
'  Recommendations target what is still actionable: the evergreen HARDWARE levers
'  (XMP/EXPO state, channel population, DRAM speed) plus the open compute issues
'  (#88 memory-bandwidth ceiling, #42 a×r‖q×b).  The original #41–#61 micro-issues
'  are mostly landed (affinity §247, force-serial lifts §210/§233, DOP §231), so
'  the advisor no longer points at them.
'
'  Advisor_Evaluate is a PURE Shared function ⇒ unit-testable headlessly
'  (--test-advisor).  Rendering is minimal: a level-2 log block + best-effort
'  status surface.
' ════════════════════════════════════════════════════════════════════════════
Partial Class Form1

    Friend Structure AdvisorMetrics
        Public CoresActive As Double            ' avg logical cores busy in the sampled window
        Public CurrentStage As RunStageId
        Public MemSpeedMts As Integer           ' rated max (0 = unknown)
        Public MemSpeedConfigured As Integer    ' effective (0 = unknown); < rated ⇒ XMP/EXPO off
        Public MemModules As Integer            ' populated DIMMs (0 = unknown)
        Public LogicalCores As Integer
        Public Hybrid As Boolean
    End Structure

    Friend Structure AdvisorRecommendation
        Public Title As String
        Public IssueRef As String               ' "" when it is a hardware/BIOS action, not a code issue
        Public SaveLowHours As Double
        Public SaveHighHours As Double
        Public Confidence As String             ' high | medium | low | info
        Public Why As String
    End Structure

    ' A compute stage is memory-bandwidth-bound (multi-limb GMP multiplies) when
    ' it is Divide / Numerator / Sqrt — exactly where DDR5 saturation bites (#88).
    Private Shared Function Advisor_IsBandwidthStage(sid As RunStageId) As Boolean
        Return sid = RunStageId.Divide OrElse sid = RunStageId.Numerator OrElse sid = RunStageId.Sqrt
    End Function

    ' Linear scale of an audit saving (given at 1 B) by run length.
    Private Shared Function Advisor_Scale(save1B As Double, digits As Long) As Double
        Return save1B * System.Math.Max(0.2, CDbl(digits) / 1.0E+9)
    End Function

    ''' <summary>
    ''' PURE evaluation: observed metrics + digit count → ranked recommendations.
    ''' Deterministic and side-effect-free.
    ''' </summary>
    Friend Shared Function Advisor_Evaluate(m As AdvisorMetrics, digits As Long) As List(Of AdvisorRecommendation)
        Dim recs As New List(Of AdvisorRecommendation)()

        ' ── Hardware: XMP/EXPO not enabled (effective speed below rated). ──
        If m.MemSpeedMts > 0 AndAlso m.MemSpeedConfigured > 0 AndAlso m.MemSpeedConfigured < m.MemSpeedMts Then
            Dim gainPct As Double = (CDbl(m.MemSpeedMts) / m.MemSpeedConfigured - 1.0) * 100.0
            recs.Add(New AdvisorRecommendation With {
                .Title = $"Enable XMP/EXPO — running {m.MemSpeedConfigured} MT/s vs rated {m.MemSpeedMts} MT/s",
                .IssueRef = "", .Confidence = "high",
                .SaveLowHours = Advisor_Scale(1.5, digits), .SaveHighHours = Advisor_Scale(2.5, digits),
                .Why = $"Memory-bound stages scale with bandwidth; enabling the rated profile adds ~{gainPct:F0}% bandwidth at zero cost."})
        End If

        ' ── Hardware: single channel (one DIMM) starves the bandwidth stages. ──
        If m.MemModules = 1 Then
            recs.Add(New AdvisorRecommendation With {
                .Title = "Populate a 2nd matched DIMM — currently single-channel",
                .IssueRef = "", .Confidence = "high",
                .SaveLowHours = Advisor_Scale(3.0, digits), .SaveHighHours = Advisor_Scale(6.0, digits),
                .Why = "A single DIMM runs single-channel; a matched pair ~doubles DRAM bandwidth, the binding limit for the big multiplies."})
        End If

        ' ── Saturation: many cores busy in a bandwidth stage ⇒ at the DDR ceiling. ──
        If m.CoresActive >= 8.0 AndAlso Advisor_IsBandwidthStage(m.CurrentStage) Then
            Dim atRated As Boolean = (m.MemSpeedMts = 0) OrElse (m.MemSpeedConfigured >= m.MemSpeedMts)
            If atRated Then
                recs.Add(New AdvisorRecommendation With {
                    .Title = "Memory-bandwidth limited — faster DRAM (e.g. DDR5-7200 CL34) would help most",
                    .IssueRef = "#88", .Confidence = "medium",
                    .SaveLowHours = Advisor_Scale(1.5, digits), .SaveHighHours = Advisor_Scale(2.5, digits),
                    .Why = $"{m.CoresActive:F0} cores busy during {m.CurrentStage} at rated speed: more threads won't help (bandwidth-bound). Upgrading DRAM speed is the lever (#88)."})
            End If
        End If

        ' ── Under-utilisation: few cores busy in a compute stage that can parallelise. ──
        If m.CoresActive > 0.0 AndAlso m.CoresActive < 4.0 AndAlso Advisor_IsBandwidthStage(m.CurrentStage) Then
            recs.Add(New AdvisorRecommendation With {
                .Title = "Low core utilisation in a compute stage — review DOP / pipelining",
                .IssueRef = "#42", .Confidence = "low",
                .SaveLowHours = Advisor_Scale(1.0, digits), .SaveHighHours = Advisor_Scale(3.0, digits),
                .Why = $"Only {m.CoresActive:F1} cores busy during {m.CurrentStage}. If not RAM-capped, pipelining a×r‖q×b (#42) or a higher DOP could fill idle cores."})
        End If

        ' ── Info: memory topology unknown ⇒ withhold the hardware rules. ──
        If m.MemSpeedMts = 0 AndAlso m.MemModules = 0 Then
            recs.Add(New AdvisorRecommendation With {
                .Title = "Memory topology unknown — XMP/channel advice withheld",
                .IssueRef = "", .Confidence = "info",
                .SaveLowHours = 0.0, .SaveHighHours = 0.0,
                .Why = "Win32_PhysicalMemory query returned no data (CIM blocked/slow). Hardware recommendations need DRAM speed + DIMM count."})
        End If

        ' Rank: high → medium → low → info, then by mid-saving descending.
        Dim rank As Func(Of String, Integer) = Function(c) If(c = "high", 0, If(c = "medium", 1, If(c = "low", 2, 3)))
        recs.Sort(Function(a, b)
                      Dim byConf As Integer = rank(a.Confidence).CompareTo(rank(b.Confidence))
                      If byConf <> 0 Then Return byConf
                      Return (b.SaveLowHours + b.SaveHighHours).CompareTo(a.SaveLowHours + a.SaveHighHours)
                  End Function)
        Return recs
    End Function

    ''' <summary>Render recommendations to the log (minimal UI surface).</summary>
    Friend Sub Advisor_Render(m As AdvisorMetrics)
        Try
            Dim recs As List(Of AdvisorRecommendation) = Advisor_Evaluate(m, DIGITS)
            AppendLog($"[Advisor§260] {recs.Count} recommendation(s) for {DIGITS:N0} digits (cores~{m.CoresActive:F1}, stage {m.CurrentStage}):{vbCrLf}", 2)
            For Each r As AdvisorRecommendation In recs
                Dim save As String = If(r.SaveHighHours > 0.0, $"  est ~{r.SaveLowHours:F1}–{r.SaveHighHours:F1}h", "")
                Dim iss As String = If(r.IssueRef <> "", $" [{r.IssueRef}]", "")
                AppendLog($"[Advisor§260]  • ({r.Confidence}){iss} {r.Title}{save}{vbCrLf}    {r.Why}{vbCrLf}", 2)
            Next
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Build live metrics from the current process + hardware fingerprint.
    ''' CoresActive is a coarse proxy (Δ CPU-seconds over a short window would
    ''' refine it; the caller passes the sampled value).
    ''' </summary>
    Friend Function Advisor_CurrentMetrics(coresActive As Double, stage As RunStageId) As AdvisorMetrics
        Dim hw As Dictionary(Of String, Object) = Telemetry_HardwareFingerprint()
        Dim geti As Func(Of String, Integer) =
            Function(k)
                Dim v As Object = Nothing
                If hw.TryGetValue(k, v) AndAlso v IsNot Nothing Then
                    Dim n As Integer
                    If Integer.TryParse(v.ToString(), n) Then Return n
                End If
                Return 0
            End Function
        Return New AdvisorMetrics With {
            .CoresActive = coresActive,
            .CurrentStage = stage,
            .MemSpeedMts = geti("memory_speed_mts"),
            .MemSpeedConfigured = geti("memory_speed_configured"),
            .MemModules = geti("memory_modules"),
            .LogicalCores = geti("logical_cores"),
            .Hybrid = (hw.ContainsKey("hybrid") AndAlso TypeOf hw("hybrid") Is Boolean AndAlso CBool(hw("hybrid")))}
    End Function

End Class
