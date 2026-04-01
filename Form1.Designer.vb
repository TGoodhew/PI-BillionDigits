<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New ComponentModel.Container()
        Timer1 = New Timer(components)
        Panel1 = New Panel()
        LstBoxPhases = New ListBox()
        BtnTest = New Button()
        LblDigitsDisplayed = New Label()
        LblRunningTime = New Label()
        Label4 = New Label()
        ChkboxWriteToFile = New CheckBox()
        ChkboxDisplay = New CheckBox()
        Label3 = New Label()
        TxtDigitsofPI = New TextBox()
        Label2 = New Label()
        Label1 = New Label()
        BtnPause = New Button()
        LblStatus = New TextBox()
        BtnCompute = New Button()
        RtbPiDigits = New RichTextBox()
        Panel1.SuspendLayout()
        SuspendLayout()
        ' 
        ' Timer1
        ' 
        ' 
        ' Panel1
        ' 
        Panel1.Controls.Add(LstBoxPhases)
        Panel1.Controls.Add(BtnTest)
        Panel1.Controls.Add(LblDigitsDisplayed)
        Panel1.Controls.Add(LblRunningTime)
        Panel1.Controls.Add(Label4)
        Panel1.Controls.Add(ChkboxWriteToFile)
        Panel1.Controls.Add(ChkboxDisplay)
        Panel1.Controls.Add(Label3)
        Panel1.Controls.Add(TxtDigitsofPI)
        Panel1.Controls.Add(Label2)
        Panel1.Controls.Add(Label1)
        Panel1.Controls.Add(BtnPause)
        Panel1.Controls.Add(LblStatus)
        Panel1.Controls.Add(BtnCompute)
        Panel1.Dock = DockStyle.Top
        Panel1.Location = New Point(0, 0)
        Panel1.Name = "Panel1"
        Panel1.Size = New Size(3121, 319)
        Panel1.TabIndex = 25
        ' 
        ' LstBoxPhases
        ' 
        LstBoxPhases.FormattingEnabled = True
        LstBoxPhases.Location = New Point(1745, 12)
        LstBoxPhases.Name = "LstBoxPhases"
        LstBoxPhases.Size = New Size(1106, 279)
        LstBoxPhases.TabIndex = 39
        ' 
        ' BtnTest
        ' 
        BtnTest.Location = New Point(403, 91)
        BtnTest.Name = "BtnTest"
        BtnTest.Size = New Size(112, 34)
        BtnTest.TabIndex = 28
        BtnTest.Text = "Test"
        BtnTest.UseVisualStyleBackColor = True
        ' 
        ' LblDigitsDisplayed
        ' 
        LblDigitsDisplayed.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        LblDigitsDisplayed.Location = New Point(971, 35)
        LblDigitsDisplayed.Name = "LblDigitsDisplayed"
        LblDigitsDisplayed.Size = New Size(259, 38)
        LblDigitsDisplayed.TabIndex = 36
        ' 
        ' LblRunningTime
        ' 
        LblRunningTime.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        LblRunningTime.Location = New Point(1438, 35)
        LblRunningTime.Name = "LblRunningTime"
        LblRunningTime.Size = New Size(313, 38)
        LblRunningTime.TabIndex = 35
        ' 
        ' Label4
        ' 
        Label4.AutoSize = True
        Label4.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label4.Location = New Point(1236, 31)
        Label4.Name = "Label4"
        Label4.Size = New Size(196, 38)
        Label4.TabIndex = 34
        Label4.Text = "Running Time:"
        ' 
        ' ChkboxWriteToFile
        ' 
        ChkboxWriteToFile.AutoSize = True
        ChkboxWriteToFile.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        ChkboxWriteToFile.Location = New Point(180, 89)
        ChkboxWriteToFile.Name = "ChkboxWriteToFile"
        ChkboxWriteToFile.Size = New Size(170, 36)
        ChkboxWriteToFile.TabIndex = 33
        ChkboxWriteToFile.Text = "Write to File"
        ChkboxWriteToFile.UseVisualStyleBackColor = True
        ' 
        ' ChkboxDisplay
        ' 
        ChkboxDisplay.AutoSize = True
        ChkboxDisplay.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        ChkboxDisplay.Location = New Point(25, 89)
        ChkboxDisplay.Name = "ChkboxDisplay"
        ChkboxDisplay.Size = New Size(117, 36)
        ChkboxDisplay.TabIndex = 32
        ChkboxDisplay.Text = "Display"
        ChkboxDisplay.UseVisualStyleBackColor = True
        ' 
        ' Label3
        ' 
        Label3.AutoSize = True
        Label3.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label3.Location = New Point(754, 31)
        Label3.Name = "Label3"
        Label3.Size = New Size(223, 38)
        Label3.TabIndex = 31
        Label3.Text = "Digits Displayed:"
        ' 
        ' TxtDigitsofPI
        ' 
        TxtDigitsofPI.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        TxtDigitsofPI.Location = New Point(508, 26)
        TxtDigitsofPI.Name = "TxtDigitsofPI"
        TxtDigitsofPI.Size = New Size(205, 39)
        TxtDigitsofPI.TabIndex = 30
        ' 
        ' Label2
        ' 
        Label2.AutoSize = True
        Label2.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label2.Location = New Point(344, 28)
        Label2.Name = "Label2"
        Label2.Size = New Size(158, 38)
        Label2.TabIndex = 29
        Label2.Text = "Digits of PI:"
        ' 
        ' Label1
        ' 
        Label1.AutoSize = True
        Label1.Font = New Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        Label1.Location = New Point(45, 162)
        Label1.Name = "Label1"
        Label1.Size = New Size(97, 38)
        Label1.TabIndex = 28
        Label1.Text = "Status:"
        ' 
        ' BtnPause
        ' 
        BtnPause.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        BtnPause.Location = New Point(180, 26)
        BtnPause.Name = "BtnPause"
        BtnPause.Size = New Size(134, 47)
        BtnPause.TabIndex = 27
        BtnPause.Text = "Cancel"
        BtnPause.UseVisualStyleBackColor = True
        ' 
        ' LblStatus
        ' 
        LblStatus.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        LblStatus.Location = New Point(148, 161)
        LblStatus.Name = "LblStatus"
        LblStatus.Size = New Size(1125, 39)
        LblStatus.TabIndex = 26
        ' 
        ' BtnCompute
        ' 
        BtnCompute.Font = New Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        BtnCompute.Location = New Point(25, 26)
        BtnCompute.Name = "BtnCompute"
        BtnCompute.Size = New Size(134, 47)
        BtnCompute.TabIndex = 25
        BtnCompute.Text = "Start"
        BtnCompute.UseVisualStyleBackColor = True
        ' 
        ' RtbPiDigits
        ' 
        RtbPiDigits.Dock = DockStyle.Fill
        RtbPiDigits.Location = New Point(0, 319)
        RtbPiDigits.Name = "RtbPiDigits"
        RtbPiDigits.Size = New Size(3121, 1028)
        RtbPiDigits.TabIndex = 27
        RtbPiDigits.Text = ""
        ' 
        ' Form1
        ' 
        AutoScaleDimensions = New SizeF(10F, 25F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Size(3121, 1347)
        Controls.Add(RtbPiDigits)
        Controls.Add(Panel1)
        Name = "Form1"
        Text = "Calculate PI to 1 Billion"
        WindowState = FormWindowState.Maximized
        Panel1.ResumeLayout(False)
        Panel1.PerformLayout()
        ResumeLayout(False)
    End Sub
    Friend WithEvents Timer1 As Timer
    Friend WithEvents Panel1 As Panel
    Friend WithEvents LblDigitsDisplayed As Label
    Friend WithEvents LblRunningTime As Label
    Friend WithEvents Label4 As Label
    Friend WithEvents ChkboxWriteToFile As CheckBox
    Friend WithEvents ChkboxDisplay As CheckBox
    Friend WithEvents Label3 As Label
    Friend WithEvents TxtDigitsofPI As TextBox
    Friend WithEvents Label2 As Label
    Friend WithEvents Label1 As Label
    Friend WithEvents BtnPause As Button
    Friend WithEvents LblStatus As TextBox
    Friend WithEvents BtnCompute As Button
    Friend WithEvents RtbPiDigits As RichTextBox
    Friend WithEvents BtnTest As Button
    Friend WithEvents LstBoxPhases As ListBox

End Class
