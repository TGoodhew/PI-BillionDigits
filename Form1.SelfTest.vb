Imports System.IO
Imports System.Linq

' ════════════════════════════════════════════════════════════════════════════
'  §259/§260 self-tests for the ETA estimator (#62) and performance advisor (#63).
'  Both validate PURE logic — no GMP, no files mutated (TestEta uses a digit count
'  that cannot collide with real run_history, forcing the deterministic cold-start
'  outPath).  Run via --test-eta / --test-advisor; exit code 0 = all PASS.
' ════════════════════════════════════════════════════════════════════════════
Partial Class Form1

    Private Shared Sub SelfTest_Log(outPath As String, line As String, ByRef allPass As Boolean, pass As Boolean)
        If Not pass Then allPass = False
        Dim s As String = $"  [{If(pass, "PASS", "FAIL")}] {line}{vbCrLf}"
        Try : File.AppendAllText(outPath, s) : Catch : End Try
        AppendLog($"[SelfTest]{s}", 1)
    End Sub

    ''' <summary>--test-eta: validate the pure ETA projection + formatter.</summary>
    Friend Shared Function TestEta() As Boolean
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eta_test.txt")
        Try : File.WriteAllText(outPath, $"[TestEta] start {DateTime.Now}{vbCrLf}") : Catch : End Try
        Dim allPass As Boolean = True
        Dim digits As Long = 123456789L      ' unlikely in history ⇒ cold-start (conf low)
        Dim conf As String = ""

        ' (1) Nothing done — full projection, low confidence, positive.
        Dim full As Double = Eta_ProjectRemainingSeconds(digits, New HashSet(Of RunStageId)(),
            RunStageId.Phase1, 0.0, 0.0, conf)
        SelfTest_Log(outPath, $"cold-start full={full:F0}s conf={conf} (>0, low)", allPass, full > 0.0 AndAlso conf = "low")

        ' (2) Phase1 done — strictly less remaining.
        Dim doneP1 As New HashSet(Of RunStageId) From {RunStageId.Phase1}
        Dim afterP1 As Double = Eta_ProjectRemainingSeconds(digits, doneP1, RunStageId.Phase2, 0.0, 0.0, conf)
        SelfTest_Log(outPath, $"afterP1={afterP1:F0}s < full={full:F0}s", allPass, afterP1 < full AndAlso afterP1 > 0.0)

        ' (3) Through Numerator — even less; Divide+Output remain.
        Dim done4 As New HashSet(Of RunStageId) From {RunStageId.Phase1, RunStageId.Phase2, RunStageId.Sqrt, RunStageId.Numerator}
        Dim after4 As Double = Eta_ProjectRemainingSeconds(digits, done4, RunStageId.Divide, 0.0, 0.0, conf)
        SelfTest_Log(outPath, $"after4={after4:F0}s < afterP1={afterP1:F0}s", allPass, after4 < afterP1 AndAlso after4 > 0.0)

        ' (4) Live fraction projection: Divide 25% done, 1000s elapsed ⇒ ~3000s left in Divide.
        Dim outCost As Double = Eta_ScaledDefault(RunStageId.Output, digits)
        Dim live As Double = Eta_ProjectRemainingSeconds(digits, done4, RunStageId.Divide, 1000.0, 0.25, conf)
        Dim expectLive As Double = 3000.0 + outCost
        SelfTest_Log(outPath, $"live(frac .25,1000s)={live:F0}s ≈ {expectLive:F0}s", allPass, System.Math.Abs(live - expectLive) < 1.0)

        ' (5) All done — ~0 remaining.
        Dim doneAll As New HashSet(Of RunStageId) From {RunStageId.Phase1, RunStageId.Phase2, RunStageId.Sqrt, RunStageId.Numerator, RunStageId.Divide, RunStageId.Output}
        Dim none As Double = Eta_ProjectRemainingSeconds(digits, doneAll, RunStageId.Output, 0.0, 1.0, conf)
        SelfTest_Log(outPath, $"all-done={none:F0}s (≈0)", allPass, none < 1.0)

        ' (6) Formatter: hours and days render with units + confidence.
        Dim f1h As String = Eta_Format(3600.0, "low")
        Dim f2d As String = Eta_Format(180000.0, "high")
        SelfTest_Log(outPath, $"fmt 1h='{f1h}'", allPass, f1h.Contains("1h") AndAlso f1h.Contains("conf low"))
        SelfTest_Log(outPath, $"fmt 2d='{f2d}'", allPass, f2d.Contains("d ") AndAlso f2d.Contains("conf high"))

        Try : File.AppendAllText(outPath, $"[TestEta] OVERALL {If(allPass, "PASS", "FAIL")}{vbCrLf}") : Catch : End Try
        Return allPass
    End Function

    ''' <summary>--test-advisor: validate the pure rules engine across profiles.</summary>
    Friend Shared Function TestAdvisor() As Boolean
        Dim outPath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "advisor_test.txt")
        Try : File.WriteAllText(outPath, $"[TestAdvisor] start {DateTime.Now}{vbCrLf}") : Catch : End Try
        Dim allPass As Boolean = True
        Dim digits As Long = 1000000000L
        Dim has As Func(Of List(Of AdvisorRecommendation), String, Boolean) =
            Function(rs, needle) rs.Any(Function(r) r.Title.Contains(needle) OrElse r.IssueRef = needle)

        ' XMP off: configured below rated.
        Dim xmp As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 5600, .MemSpeedConfigured = 4800, .MemModules = 2, .CoresActive = 8, .CurrentStage = RunStageId.Divide, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, $"XMP-off ⇒ 'Enable XMP' ({xmp.Count} recs)", allPass, has(xmp, "Enable XMP"))

        ' Single channel.
        Dim sc As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 5600, .MemSpeedConfigured = 5600, .MemModules = 1, .CoresActive = 8, .CurrentStage = RunStageId.Divide, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, "1 DIMM ⇒ 'Populate a 2nd matched DIMM'", allPass, has(sc, "Populate a 2nd matched DIMM"))

        ' Saturation at rated speed during a bandwidth stage ⇒ #88.
        Dim sat As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 5600, .MemSpeedConfigured = 5600, .MemModules = 2, .CoresActive = 12, .CurrentStage = RunStageId.Divide, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, "12 cores @rated in Divide ⇒ #88", allPass, has(sat, "#88"))

        ' Under-utilisation ⇒ #42, and NO false hardware recs.
        Dim under As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 5600, .MemSpeedConfigured = 5600, .MemModules = 2, .CoresActive = 2, .CurrentStage = RunStageId.Divide, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, "2 cores in Divide ⇒ #42, no XMP/channel rec", allPass,
            has(under, "#42") AndAlso Not has(under, "Enable XMP") AndAlso Not has(under, "Populate"))

        ' Unknown memory ⇒ single 'info' line, no hardware claim.
        Dim unk As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 0, .MemSpeedConfigured = 0, .MemModules = 0, .CoresActive = 8, .CurrentStage = RunStageId.Phase1, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, "unknown mem ⇒ 'topology unknown' info", allPass, has(unk, "topology unknown"))

        ' Well-tuned (XMP on, dual channel, healthy cores, non-bandwidth stage) ⇒ zero recs.
        Dim good As List(Of AdvisorRecommendation) = Advisor_Evaluate(New AdvisorMetrics With {
            .MemSpeedMts = 5600, .MemSpeedConfigured = 5600, .MemModules = 2, .CoresActive = 6, .CurrentStage = RunStageId.Phase1, .LogicalCores = 24}, digits)
        SelfTest_Log(outPath, $"well-tuned ⇒ 0 recs (got {good.Count})", allPass, good.Count = 0)

        Try : File.AppendAllText(outPath, $"[TestAdvisor] OVERALL {If(allPass, "PASS", "FAIL")}{vbCrLf}") : Catch : End Try
        Return allPass
    End Function

End Class
