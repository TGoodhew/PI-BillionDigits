Imports System.IO
Imports System.Text.Json
Imports System.Diagnostics

' ════════════════════════════════════════════════════════════════════════════
'  §258 (issues #62 / #63): Run-telemetry foundation.
'
'  Collects per-stage wall time + a hardware fingerprint during a run and appends
'  an immutable record to run_history.json at exit.  This data layer is shared by
'  the ETA estimator (#62, Form1.Eta.vb) and the performance advisor (#63,
'  Form1.Advisor.vb).
'
'  Design rules (all enforced here):
'   • Purely additive — never touches the π math, the checkpoint format, or the
'     SnapshotStore.  Every I/O path is best-effort: a failure logs and continues.
'   • Stage identity is a stable enum (RunStageId), NOT the freeform LogPhase
'     text — so re-wording a log line can never silently break ETA/advisor.
'   • The hardware fingerprint is cached (CIM queries are slow); memory speed is a
'     best-effort PowerShell CIM probe — absent data degrades the advisor to
'     "more data needed" rather than a fabricated recommendation.
' ════════════════════════════════════════════════════════════════════════════
Partial Class Form1

    ' Canonical compute stages, in execution order.  ETA + advisor key off these.
    Friend Enum RunStageId
        Phase1      ' binary splitting → L0.bin
        Phase2      ' tree combine
        Sqrt        ' SafeMpzSqrt (Newton)
        Numerator   ' R0/R1/R2 multiplies
        Divide      ' SafeMpzDiv (incl. SafeMpzReciprocal Newton)
        Output      ' decimal conversion (§216/§226)
    End Enum

    Friend Structure RunStageRecord
        Public Id As RunStageId
        Public WallSeconds As Double
        Public PeakWsMb As Long
    End Structure

    ' In-memory accumulation for the current run (instance — one run per process).
    Private _telStages As New List(Of RunStageRecord)()
    Private _telCurStageStartSec As Double = 0.0
    Private _telStarted As Boolean = False

    ' Hardware fingerprint cache (shared — immutable for the process lifetime).
    Private Shared _telHwCache As Dictionary(Of String, Object) = Nothing

    ''' <summary>
    ''' Maps a freeform LogPhase name onto a canonical stage completion, if it
    ''' marks one.  Completion markers are emitted in stage order, so the interval
    ''' between two consecutive markers is exactly the intervening stage's wall
    ''' time (e.g. between "Binary Splitting complete" and "Final combine complete"
    ''' lies all of Phase 2).
    ''' </summary>
    Private Shared Function Telemetry_ClassifyComplete(phaseName As String, ByRef stageId As RunStageId) As Boolean
        If String.IsNullOrEmpty(phaseName) Then Return False
        If phaseName.Contains("Binary Splitting complete") Then stageId = RunStageId.Phase1 : Return True
        If phaseName.Contains("Final combine complete") Then stageId = RunStageId.Phase2 : Return True
        If phaseName.Contains("Square root complete") Then stageId = RunStageId.Sqrt : Return True
        If phaseName.Contains("Numerator complete") Then stageId = RunStageId.Numerator : Return True
        If phaseName.Contains("Division complete") Then stageId = RunStageId.Divide : Return True
        If phaseName.Contains("String conversion complete") Then stageId = RunStageId.Output : Return True
        Return False
    End Function

    ''' <summary>
    ''' Called from LogPhase for every phase entry.  Records a canonical stage's
    ''' wall time the moment its completion marker is seen, then advances the
    ''' start clock for the next stage and notifies the live ETA estimator.
    ''' </summary>
    Private Sub Telemetry_OnPhase(phaseName As String)
        Try
            If Not _telStarted Then
                _telStarted = True
                _telCurStageStartSec = stopWatch.Elapsed.TotalSeconds
            End If
            Dim sid As RunStageId
            If Telemetry_ClassifyComplete(phaseName, sid) Then
                Dim nowSec As Double = stopWatch.Elapsed.TotalSeconds
                _telStages.Add(New RunStageRecord With {
                    .Id = sid,
                    .WallSeconds = System.Math.Max(0.0, nowSec - _telCurStageStartSec),
                    .PeakWsMb = Process.GetCurrentProcess().PeakWorkingSet64 \ 1048576L})
                _telCurStageStartSec = nowSec
                Eta_OnStageComplete(sid)   ' refine the live ETA (Form1.Eta.vb)
            End If
        Catch
            ' Telemetry must never perturb a run.
        End Try
    End Sub

    ''' <summary>Stages recorded so far this run (snapshot copy).</summary>
    Friend Function Telemetry_CompletedStages() As List(Of RunStageRecord)
        Return New List(Of RunStageRecord)(_telStages)
    End Function

    ''' <summary>
    ''' Hardware fingerprint, cached.  Reuses the already-detected CpuTopology
    ''' (§224) and MemoryBudget (§243) data; memory speed/channels are a slow,
    ''' best-effort CIM probe done once.
    ''' </summary>
    Friend Shared Function Telemetry_HardwareFingerprint() As Dictionary(Of String, Object)
        If _telHwCache IsNot Nothing Then Return _telHwCache
        Dim hw As New Dictionary(Of String, Object)()
        Try
            hw("logical_cores") = Environment.ProcessorCount
            hw("hybrid") = _topoIsHybrid
            hw("p_cores") = If(_topoPCoreIds IsNot Nothing, _topoPCoreIds.Length, 0)
            hw("e_cores") = If(_topoECoreIds IsNot Nothing, _topoECoreIds.Length, 0)
            hw("total_logicals") = _topoTotalLogicals
            hw("memory_capacity_gb") = System.Math.Round(MemBudget_TotalPhysicalGB(), 1)
            hw("os_version") = Environment.OSVersion.VersionString
            hw("machine_hash") = Environment.MachineName.GetHashCode()
        Catch
        End Try
        Telemetry_TryAddMemoryInfo(hw)
        _telHwCache = hw
        Return hw
    End Function

    ''' <summary>
    ''' Best-effort DRAM speed/channel probe via a one-shot PowerShell CIM query.
    ''' Adds memory_speed_mts (rated max), memory_speed_configured (effective —
    ''' lower ⇒ XMP/EXPO off) and memory_modules.  Any failure leaves the keys
    ''' absent, which the advisor treats as "unknown" (no recommendation).
    ''' </summary>
    Private Shared Sub Telemetry_TryAddMemoryInfo(hw As Dictionary(Of String, Object))
        Try
            Dim psi As New ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command ""Get-CimInstance Win32_PhysicalMemory | " &
                "Select-Object Speed,ConfiguredClockSpeed | ConvertTo-Json -Compress""") With {
                .RedirectStandardOutput = True, .UseShellExecute = False, .CreateNoWindow = True}
            Dim p As Process = Process.Start(psi)
            If p Is Nothing Then Return
            Dim outp As String = p.StandardOutput.ReadToEnd()
            If Not p.WaitForExit(8000) Then Return
            If String.IsNullOrWhiteSpace(outp) Then Return
            ' ConvertTo-Json yields a single object for one DIMM, an array for many.
            Dim maxSpeed As Integer = 0, maxConfig As Integer = 0, modules As Integer = 0
            Using doc As JsonDocument = JsonDocument.Parse(outp)
                Dim root As JsonElement = doc.RootElement
                Dim items As New List(Of JsonElement)()
                If root.ValueKind = JsonValueKind.Array Then
                    For Each e As JsonElement In root.EnumerateArray() : items.Add(e) : Next
                Else
                    items.Add(root)
                End If
                modules = items.Count
                For Each e As JsonElement In items
                    Dim sp As JsonElement, cfg As JsonElement
                    If e.TryGetProperty("Speed", sp) AndAlso sp.ValueKind = JsonValueKind.Number Then maxSpeed = System.Math.Max(maxSpeed, sp.GetInt32())
                    If e.TryGetProperty("ConfiguredClockSpeed", cfg) AndAlso cfg.ValueKind = JsonValueKind.Number Then maxConfig = System.Math.Max(maxConfig, cfg.GetInt32())
                Next
            End Using
            If maxSpeed > 0 Then hw("memory_speed_mts") = maxSpeed
            If maxConfig > 0 Then hw("memory_speed_configured") = maxConfig
            If modules > 0 Then hw("memory_modules") = modules
        Catch
            ' CIM unavailable / blocked / slow — leave memory keys absent.
        End Try
    End Sub

    ''' <summary>Path to the append-only run history.</summary>
    Friend Shared ReadOnly Property Telemetry_HistoryPath As String
        Get
            Return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PI-BillionDigits", "run_history.json")
        End Get
    End Property

    ''' <summary>
    ''' Append one JSON-lines record for the just-finished run.  Called from the
    ''' compute thread's outer try/finally so a crashed/aborted run still records
    ''' what it managed to do.  Best-effort: a write failure logs at level 1.
    ''' </summary>
    Friend Sub Telemetry_WriteRunHistory(outcome As String, digits As Long)
        Try
            Dim histPath As String = Telemetry_HistoryPath
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(histPath))
            Dim stages As New List(Of Dictionary(Of String, Object))()
            For Each s As RunStageRecord In _telStages
                stages.Add(New Dictionary(Of String, Object) From {
                    {"name", s.Id.ToString()},
                    {"wall_seconds", System.Math.Round(s.WallSeconds, 1)},
                    {"peak_ws_mb", s.PeakWsMb}})
            Next
            Dim rec As New Dictionary(Of String, Object) From {
                {"schema_version", 1},
                {"finished_local", DateTime.Now.ToString("o")},
                {"outcome", outcome},
                {"digits", digits},
                {"total_wall_seconds", System.Math.Round(stopWatch.Elapsed.TotalSeconds, 1)},
                {"hardware", Telemetry_HardwareFingerprint()},
                {"stages", stages}}
            File.AppendAllText(histPath, JsonSerializer.Serialize(rec) & vbLf)
            AppendLog($"[Telemetry§258] run_history appended: outcome={outcome} digits={digits:N0} stages={_telStages.Count} → {histPath}{vbCrLf}", 2)
        Catch ex As Exception
            AppendLog($"[Telemetry§258] run_history write failed: {ex.Message}{vbCrLf}", 1)
        End Try
    End Sub

    ''' <summary>
    ''' Read prior run records (most-recent-last).  Returns parsed dictionaries;
    ''' the estimator consumes them.  Returns an empty list on any failure.
    ''' </summary>
    Friend Shared Function Telemetry_ReadRunHistory() As List(Of JsonElement)
        Dim result As New List(Of JsonElement)()
        Try
            Dim histPath As String = Telemetry_HistoryPath
            If Not File.Exists(histPath) Then Return result
            For Each line As String In File.ReadAllLines(histPath)
                If String.IsNullOrWhiteSpace(line) Then Continue For
                Try
                    Dim doc As JsonDocument = JsonDocument.Parse(line)
                    result.Add(doc.RootElement.Clone())
                    doc.Dispose()
                Catch
                End Try
            Next
        Catch
        End Try
        Return result
    End Function

End Class
