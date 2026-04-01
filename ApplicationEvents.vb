Imports Microsoft.VisualBasic.ApplicationServices

Namespace My
    ' The following events are available for MyApplication:
    ' Startup: Raised when the application starts, before the startup form is created.
    ' Shutdown: Raised after all application forms are closed.  This event is not raised if the application terminates abnormally.
    ' UnhandledException: Raised if the application encounters an unhandled exception.
    ' StartupNextInstance: Raised when launching a single-instance application and the application is already active. 
    ' NetworkAvailabilityChanged: Raised when the network connection is connected or disconnected.

    ' **NEW** ApplyApplicationDefaults: Raised when the application queries default values to be set for the application.

    ' Example:
    ' Private Sub MyApplication_ApplyApplicationDefaults(sender As Object, e As ApplyApplicationDefaultsEventArgs) Handles Me.ApplyApplicationDefaults
    '
    '   ' Setting the application-wide default Font:
    '   e.Font = New Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular)
    '
    '   ' Setting the HighDpiMode for the Application:
    '   e.HighDpiMode = HighDpiMode.PerMonitorV2
    '
    '   ' If a splash dialog is used, this sets the minimum display time:
    '   e.MinimumSplashScreenDisplayTime = 4000
    ' End Sub

    Partial Friend Class MyApplication

        Private Sub MyApplication_UnhandledException(sender As Object, e As UnhandledExceptionEventArgs) Handles Me.UnhandledException
            ' Build the full exception chain
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine($"=== UNHANDLED EXCEPTION at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===")
            Dim current As Exception = e.Exception
            Dim depth As Integer = 0
            While current IsNot Nothing
                Dim prefix As String = If(depth = 0, "Exception", $"InnerException[{depth}]")
                sb.AppendLine($"{prefix}: {current.GetType().FullName}")
                sb.AppendLine($"Message: {current.Message}")
                sb.AppendLine("StackTrace:")
                sb.AppendLine(current.StackTrace)
                sb.AppendLine()
                current = current.InnerException
                depth += 1
            End While
            sb.AppendLine("=== END UNHANDLED EXCEPTION ===")

            ' Write to the log file before showing UI
            Try
                Dim logDir As String = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PI-BillionDigits")
                System.IO.Directory.CreateDirectory(logDir)
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(logDir, "pi_phase_log.txt"), sb.ToString())
            Catch
            End Try

            MessageBox.Show(sb.ToString(), "Unhandled Exception", MessageBoxButtons.OK, MessageBoxIcon.Error)
            e.ExitApplication = False   ' keep the app alive; user can review state and close manually
        End Sub

    End Class
End Namespace
