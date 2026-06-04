Imports System.Text.Json

' ════════════════════════════════════════════════════════════════════════════
'  §259 (issue #62): Predicted-completion-time estimator.
'
'  ETA = Σ(remaining-stage cost).  A stage that is done (completed OR skipped via
'  checkpoint) contributes 0; the in-flight stage contributes a live projection
'  from its own elapsed/fraction; not-yet-started stages contribute a cost drawn
'  from history (exact digit match ⇒ high confidence) or, failing that, a
'  hardcoded 1 B default scaled by digits^exponent (low confidence).
'
'  The projection (Eta_ProjectRemainingSeconds) is a PURE Shared function so it is
'  unit-testable headlessly (--test-eta).  Live wiring (Eta_OnStageComplete,
'  Eta_OnReciprocalProgress) is thin: it maintains the done-set + current-stage
'  clock and refreshes a minimal UI surface (window title + level-2 log line).
'
'  Caveat baked in from the issue: the dominant Divide stage's reciprocal Newton
'  runs a FIXED _minNrIters iterations (§200) — cost = minIters × per-iter, NOT
'  "until prec ≥ rBits".  That is exactly what Eta_OnReciprocalProgress encodes.
' ════════════════════════════════════════════════════════════════════════════
Partial Class Form1

    Private Shared ReadOnly _etaOrder As RunStageId() = {
        RunStageId.Phase1, RunStageId.Phase2, RunStageId.Sqrt,
        RunStageId.Numerator, RunStageId.Divide, RunStageId.Output}

    ' Cold-start per-stage cost at 1 B digits (seconds) + super-linear scaling
    ' exponents.  Seeded from the 5 B run-#3 breakdown (Divide dominates: a×r +
    ' q×b + reciprocal).  These are deliberately rough — history overrides them
    ' the moment one same-digit run has completed.
    Private Shared Function Eta_DefaultCost1B(sid As RunStageId) As Double
        Select Case sid
            Case RunStageId.Phase1 : Return 600.0
            Case RunStageId.Phase2 : Return 400.0
            Case RunStageId.Sqrt : Return 300.0
            Case RunStageId.Numerator : Return 500.0
            Case RunStageId.Divide : Return 3000.0
            Case Else : Return 400.0          ' Output
        End Select
    End Function

    Private Shared Function Eta_Exponent(sid As RunStageId) As Double
        Select Case sid
            Case RunStageId.Phase1, RunStageId.Phase2, RunStageId.Numerator, RunStageId.Output : Return 1.05
            Case Else : Return 1.10          ' Sqrt, Divide
        End Select
    End Function

    Private Shared Function Eta_ScaledDefault(sid As RunStageId, digits As Long) As Double
        Dim n As Double = System.Math.Max(1.0, CDbl(digits)) / 1.0E+9
        Return Eta_DefaultCost1B(sid) * System.Math.Pow(n, Eta_Exponent(sid))
    End Function

    ''' <summary>
    ''' Per-stage cost map from history: median wall_seconds across successful
    ''' runs at the same digit count.  Empty if no such record exists.
    ''' </summary>
    Friend Shared Function Eta_HistoricalStageCosts(digits As Long) As Dictionary(Of RunStageId, Double)
        Dim byStage As New Dictionary(Of RunStageId, List(Of Double))()
        Try
            For Each rec As JsonElement In Telemetry_ReadRunHistory()
                Dim dEl As JsonElement, oEl As JsonElement, sEl As JsonElement
                If Not rec.TryGetProperty("digits", dEl) OrElse dEl.ValueKind <> JsonValueKind.Number Then Continue For
                If dEl.GetInt64() <> digits Then Continue For
                If rec.TryGetProperty("outcome", oEl) AndAlso oEl.GetString() <> "success" Then Continue For
                If Not rec.TryGetProperty("stages", sEl) OrElse sEl.ValueKind <> JsonValueKind.Array Then Continue For
                For Each st As JsonElement In sEl.EnumerateArray()
                    Dim nm As JsonElement, ws As JsonElement
                    If st.TryGetProperty("name", nm) AndAlso st.TryGetProperty("wall_seconds", ws) Then
                        Dim sid As RunStageId
                        If [Enum].TryParse(Of RunStageId)(nm.GetString(), sid) Then
                            If Not byStage.ContainsKey(sid) Then byStage(sid) = New List(Of Double)()
                            byStage(sid).Add(ws.GetDouble())
                        End If
                    End If
                Next
            Next
        Catch
        End Try
        Dim result As New Dictionary(Of RunStageId, Double)()
        For Each kv As KeyValuePair(Of RunStageId, List(Of Double)) In byStage
            Dim vals = kv.Value : vals.Sort()
            result(kv.Key) = vals(vals.Count \ 2)   ' median
        Next
        Return result
    End Function

    ''' <summary>
    ''' PURE projection: seconds of work remaining.  done = stages already
    ''' finished or skipped (contribute 0); curStage is in flight with
    ''' curStageElapsedSec already spent and curStageFraction∈[0,1] complete
    ''' (0 ⇒ unknown, fall back to the stage's default cost); confidence is set
    ''' "high" when same-digit history seeded the not-started stages, else "low".
    ''' </summary>
    Friend Shared Function Eta_ProjectRemainingSeconds(
            digits As Long,
            done As HashSet(Of RunStageId),
            curStage As RunStageId, curStageElapsedSec As Double, curStageFraction As Double,
            ByRef confidence As String) As Double

        Dim hist As Dictionary(Of RunStageId, Double) = Eta_HistoricalStageCosts(digits)
        confidence = If(hist.Count > 0, "high", "low")

        Dim costOf As Func(Of RunStageId, Double) =
            Function(sid)
                Dim c As Double
                If hist.TryGetValue(sid, c) Then Return c
                Return Eta_ScaledDefault(sid, digits)
            End Function

        Dim remaining As Double = 0.0
        For Each sid As RunStageId In _etaOrder
            If done IsNot Nothing AndAlso done.Contains(sid) Then Continue For   ' finished/skipped → 0
            If sid = curStage Then
                If curStageFraction > 0.02 Then
                    ' Live projection from observed rate (handles per-iter reciprocal refinement).
                    Dim projected As Double = curStageElapsedSec / curStageFraction
                    remaining += System.Math.Max(0.0, projected - curStageElapsedSec)
                Else
                    remaining += System.Math.Max(0.0, costOf(sid) - curStageElapsedSec)
                End If
            Else
                remaining += costOf(sid)   ' not started
            End If
        Next
        Return remaining
    End Function

    ''' <summary>Human ETA string: "ETA: 2026-06-05 04:12  (~7h 30m, conf low)".</summary>
    Friend Shared Function Eta_Format(remainingSeconds As Double, confidence As String) As String
        Dim rel As String
        Dim ts As TimeSpan = TimeSpan.FromSeconds(System.Math.Max(0.0, remainingSeconds))
        If ts.TotalDays >= 1 Then
            rel = $"~{CInt(System.Math.Floor(ts.TotalDays))}d {ts.Hours}h"
        ElseIf ts.TotalHours >= 1 Then
            rel = $"~{ts.Hours}h {ts.Minutes}m"
        ElseIf ts.TotalMinutes >= 1 Then
            rel = $"~{ts.Minutes}m"
        Else
            rel = "<1m"
        End If
        Return $"ETA: {DateTime.Now.Add(ts):yyyy-MM-dd HH:mm}  ({rel}, conf {confidence})"
    End Function

    ' ── live state ──────────────────────────────────────────────────────────
    Private _etaDone As New HashSet(Of RunStageId)()

    ''' <summary>Mark a stage done (completed or skipped) and refresh the ETA.</summary>
    Private Sub Eta_OnStageComplete(sid As RunStageId)
        _etaDone.Add(sid)
        Dim nextStage As RunStageId = Eta_NextStage()
        Eta_Refresh(nextStage, 0.0)
    End Sub

    Private Function Eta_NextStage() As RunStageId
        For Each sid As RunStageId In _etaOrder
            If Not _etaDone.Contains(sid) Then Return sid
        Next
        Return RunStageId.Output
    End Function

    ''' <summary>
    ''' Per-iteration reciprocal refinement (the dominant Divide stage).  Called
    ''' from the SafeMpzReciprocal Newton loop with the §200 fixed iteration
    ''' schedule, so fraction = iterDone / minIters is a true progress ratio.
    ''' </summary>
    Friend Sub Eta_OnReciprocalProgress(iterDone As Integer, minIters As Integer)
        If minIters <= 0 Then Return
        Eta_Refresh(RunStageId.Divide, System.Math.Min(1.0, CDbl(iterDone) / CDbl(minIters)))
    End Sub

    Private Sub Eta_Refresh(curStage As RunStageId, curStageFraction As Double)
        Try
            Dim curElapsed As Double = System.Math.Max(0.0, stopWatch.Elapsed.TotalSeconds - _telCurStageStartSec)
            Dim conf As String = ""
            Dim remaining As Double = Eta_ProjectRemainingSeconds(
                DIGITS, _etaDone, curStage, curElapsed, curStageFraction, conf)
            Dim msg As String = Eta_Format(remaining, conf)
            AppendLog($"[ETA§259] {msg}  (stage {curStage}, {curStageFraction:P0}){vbCrLf}", 2)
            ' Minimal UI: window title is always rendered and layout-risk-free.
            If Not _headless Then
                Try
                    If Not Me.IsDisposed Then Me.BeginInvoke(Sub() Me.Text = $"π {DIGITS:N0} — {msg}")
                Catch
                End Try
            End If
        Catch
        End Try
    End Sub

End Class
