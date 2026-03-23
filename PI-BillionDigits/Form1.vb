Option Strict On
Option Explicit On

Imports System.Numerics
Imports System.IO
Imports System.Threading
Imports System.Runtime.InteropServices
Imports Math.Gmp.Native
Imports System.Diagnostics

Public Class Form1

    Private stopWatch As New Stopwatch()
    Private phaseStopWatch As New Stopwatch()
    Private cts As CancellationTokenSource
    Private DIGITS As Long
    Private Const outputFile As String = "c:\PiOutput\pi_digits.txt"
    Private displayStr As String = ""
    Private displayIdx As Integer = 0
    Private displayTotal As Long = 0
    Private WithEvents displayTimer As New System.Windows.Forms.Timer()
    Private gmpC3Const As mpz_t = Nothing

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
    '    MessageBox.Show("Step 1: About to VirtualAlloc 20GB...")

    '    _poolBase = VirtualAlloc(IntPtr.Zero,
    '                          New UIntPtr(_poolSize),
    '                          MEM_RESERVE Or MEM_COMMIT,
    '                          PAGE_READWRITE)

    '    If _poolBase = IntPtr.Zero Then
    '        Dim err As Integer = Marshal.GetLastWin32Error()
    '        MessageBox.Show($"VirtualAlloc FAILED! Win32 error: {err}" & vbCrLf &
    '                    "Pool size requested: 20GB" & vbCrLf &
    '                    "Try reducing to 10GB")
    '        Throw New OutOfMemoryException("Failed to allocate GMP pool!")
    '    End If

    '    MessageBox.Show($"Step 2: VirtualAlloc succeeded at 0x{_poolBase.ToString("X")}")
    '    _poolOffset = 0UL

    '    _allocDel = New allocate_function(AddressOf GmpAlloc)
    '    _reallocDel = New reallocate_function(AddressOf GmpRealloc)
    '    _freeDel = New free_function(AddressOf GmpFree)

    '    MessageBox.Show("Step 3: About to call mp_set_memory_functions...")
    '    gmp_lib.mp_set_memory_functions(_allocDel, _reallocDel, _freeDel)
    '    MessageBox.Show("Step 4: Pool fully initialized!")
    'End Sub
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        LblStatus.Text = "Ready"
        TxtDigitsofPI.Text = "1,000,000"
        ChkboxDisplay.Checked = True
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
        TxtChunkSize.Text = "500"
        LstBoxPhases.Items.Clear()

        ' Initialize GMP with system allocator (default)
        ' Custom pool allocator commented out - was causing memory corruption
        Dim dummy As New mpz_t()
        gmp_lib.mpz_init(dummy)
        gmp_lib.mpz_clear(dummy)

        ' Initialize constant for Chudnovsky algorithm
        gmpC3Const = New mpz_t()
        gmp_lib.mpz_init(gmpC3Const)
        gmp_lib.mpz_set_str(gmpC3Const, New char_ptr("10939058860032000"), 10)

        MessageBox.Show(
        "64-bit process: " & Environment.Is64BitProcess.ToString() & vbCrLf &
        "IntPtr.Size: " & IntPtr.Size.ToString() & " (must be 8)" & vbCrLf &
        "Available RAM: " & (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes \ 1048576).ToString() & "MB" & vbCrLf &
        "GMP Memory: System allocator (default)",
        "Process Info")
    End Sub

    Private Sub LogPhase(phaseName As String)
        Dim elapsed As TimeSpan = stopWatch.Elapsed
        Dim phaseTime As TimeSpan = phaseStopWatch.Elapsed
        phaseStopWatch.Restart()
        Dim procMem As Long = Process.GetCurrentProcess().WorkingSet64 \ 1048576
        Dim virtMem As Long = Process.GetCurrentProcess().VirtualMemorySize64 \ 1048576
        Dim entry As String = $"{elapsed:hh\:mm\:ss\.ff} | +{phaseTime:mm\:ss\.ff} | RAM:{procMem:N0}MB | VIRT:{virtMem:N0}MB | {phaseName}"
        Try
            System.IO.File.AppendAllText("c:\PiOutput\pi_phase_log.txt",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & " | " & entry & vbCrLf)
        Catch
        End Try
        Me.BeginInvoke(Sub()
                           LstBoxPhases.Items.Add(entry)
                           LstBoxPhases.SelectedIndex = LstBoxPhases.Items.Count - 1
                           LblStatus.Text = phaseName
                       End Sub)
    End Sub

    Private Sub BtnCompute_Click(sender As Object, e As EventArgs) Handles BtnCompute.Click
        BtnCompute.Enabled = False
        BtnPause.Enabled = True
        DIGITS = CLng(TxtDigitsofPI.Text.Replace(",", ""))
        stopWatch.Restart()
        phaseStopWatch.Restart()
        cts = New CancellationTokenSource()
        Timer1.Start()
        LstBoxPhases.Items.Clear()
        LstBoxPhases.Items.Add($"Starting {DIGITS:N0} digits at {DateTime.Now:HH:mm:ss}")
        Try
            System.IO.File.WriteAllText("c:\PiOutput\pi_phase_log.txt",
                $"=== PI Computation Started {DateTime.Now} ===" & vbCrLf &
                $"=== Digits: {DIGITS:N0} ===" & vbCrLf)
        Catch
        End Try
        RtbPiDigits.AppendText("Starting computation..." & vbCrLf)
        Dim computeThread As New System.Threading.Thread(
            Sub()
                Try
                    Dim result As String = ComputePiGMP(DIGITS, cts.Token)
                    If result <> "" Then
                        Me.Invoke(Sub() StreamPiToScreen(result))
                    End If
                Catch ex As Exception
                    Me.Invoke(Sub()
                                  LblStatus.Text = "Error: " & ex.Message
                                  BtnCompute.Enabled = True
                                  BtnPause.Enabled = False
                                  Timer1.Stop()
                              End Sub)
                End Try
            End Sub, 256 * 1024 * 1024)  ' Back to 256MB
        computeThread.IsBackground = True
        computeThread.Start()
    End Sub

    Private Sub BtnPause_Click_1(sender As Object, e As EventArgs) Handles BtnPause.Click
        cts.Cancel()
        displayTimer.Enabled = False
        BtnPause.Enabled = False
        BtnCompute.Enabled = True
        Timer1.Stop()
        LblStatus.Text = "Paused."
        If ChkboxWriteToFile.Checked Then
            Try
                If Not System.IO.Directory.Exists(
                    System.IO.Path.GetDirectoryName(outputFile)) Then
                    System.IO.Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(outputFile))
                End If
                System.IO.File.WriteAllText(outputFile, RtbPiDigits.Text)
                LblStatus.Text = "Paused. Saved to file."
            Catch ex As Exception
                LblStatus.Text = "Paused. File save error: " & ex.Message
            End Try
        End If
    End Sub

    Private Sub BinarySplitChunk(a As Long, b As Long,
                          ByRef Pab As mpz_t,
                          ByRef Qab As mpz_t,
                          ByRef Tab As mpz_t)
        ' Stack-based iterative binary splitting
        Dim workStack As New Stack(Of WorkItem)()
        Dim results As New Dictionary(Of Integer, Result)
        Dim nextIndex As Integer = 0

        ' Push initial work
        workStack.Push(New WorkItem With {.a = a, .b = b, .resultIndex = 0, .isComplete = False})

        While workStack.Count > 0
            Dim current As WorkItem = workStack.Pop()

            ' Base case: single term
            If current.b - current.a = 1 Then
                Dim res As New Result With {
        .P = New mpz_t(),
        .Q = New mpz_t(),
        .T = New mpz_t()
    }
                gmp_lib.mpz_inits(res.P, res.Q, res.T, Nothing)

                If current.a = 0 Then
                    gmp_lib.mpz_set_ui(res.P, 1UI)
                    gmp_lib.mpz_set_ui(res.Q, 1UI)
                    gmp_lib.mpz_set_ui(res.T, 13591409UI)
                Else
                    Dim aBig As New mpz_t()
                    Dim t1 As New mpz_t()
                    Dim t2 As New mpz_t()
                    gmp_lib.mpz_inits(aBig, t1, t2, Nothing)
                    gmp_lib.mpz_set_si(aBig, CInt(current.a))

                    gmp_lib.mpz_mul_ui(t1, aBig, 6UI)
                    gmp_lib.mpz_sub_ui(t1, t1, 5UI)
                    gmp_lib.mpz_mul_ui(t2, aBig, 2UI)
                    gmp_lib.mpz_sub_ui(t2, t2, 1UI)
                    gmp_lib.mpz_mul(res.P, t1, t2)
                    gmp_lib.mpz_mul_ui(t1, aBig, 6UI)
                    gmp_lib.mpz_sub_ui(t1, t1, 1UI)
                    gmp_lib.mpz_mul(res.P, res.P, t1)

                    gmp_lib.mpz_pow_ui(res.Q, aBig, 3UI)
                    gmp_lib.mpz_mul(res.Q, res.Q, gmpC3Const)

                    gmp_lib.mpz_mul_ui(t1, aBig, 545140134UI)
                    gmp_lib.mpz_add_ui(t1, t1, 13591409UI)
                    gmp_lib.mpz_mul(res.T, res.P, t1)
                    If (current.a And 1L) = 1L Then gmp_lib.mpz_neg(res.T, res.T)

                    gmp_lib.mpz_clears(aBig, t1, t2, Nothing)
                End If

                results(current.resultIndex) = res
            ElseIf current.isComplete Then
                ' Combine results from left and right children
                Dim leftRes As Result = results(current.leftChildIndex)
                Dim rightRes As Result = results(current.rightChildIndex)

                Dim res As New Result With {
                    .P = New mpz_t(),
                    .Q = New mpz_t(),
                    .T = New mpz_t()
                }
                Dim tempA As New mpz_t()
                Dim tempB As New mpz_t()
                gmp_lib.mpz_inits(res.P, res.Q, res.T, tempA, tempB, Nothing)

                gmp_lib.mpz_mul(res.P, leftRes.P, rightRes.P)
                gmp_lib.mpz_mul(res.Q, leftRes.Q, rightRes.Q)
                gmp_lib.mpz_mul(tempA, leftRes.T, rightRes.Q)
                gmp_lib.mpz_mul(tempB, leftRes.P, rightRes.T)
                gmp_lib.mpz_add(res.T, tempA, tempB)

                gmp_lib.mpz_clears(leftRes.P, leftRes.Q, leftRes.T, Nothing)
                gmp_lib.mpz_clears(rightRes.P, rightRes.Q, rightRes.T, Nothing)
                gmp_lib.mpz_clears(tempA, tempB, Nothing)

                results.Remove(current.leftChildIndex)
                results.Remove(current.rightChildIndex)
                results(current.resultIndex) = res
            Else
                ' Split into two sub-problems
                Dim mid As Long = (current.a + current.b) \ 2
                nextIndex += 1
                Dim leftIdx As Integer = nextIndex
                nextIndex += 1
                Dim rightIdx As Integer = nextIndex

                ' Push marker to combine results later
                workStack.Push(New WorkItem With {
                .a = current.a,
                .b = current.b,
                .resultIndex = current.resultIndex,
                .isComplete = True,
                .leftChildIndex = leftIdx,
                .rightChildIndex = rightIdx
            })

                ' Push right child first (processed second)
                workStack.Push(New WorkItem With {
                .a = mid,
                .b = current.b,
                .resultIndex = rightIdx,
                .isComplete = False
            })

                ' Push left child (processed first)
                workStack.Push(New WorkItem With {
                .a = current.a,
                .b = mid,
                .resultIndex = leftIdx,
                .isComplete = False
            })
            End If
        End While

        ' Return the final result
        Dim finalResult As Result = results(0)
        Pab = finalResult.P
        Qab = finalResult.Q
        Tab = finalResult.T
    End Sub

    Private Sub BinarySplitGMP(numTerms As Long,
                                ByRef nodes As List(Of Tuple(Of mpz_t, mpz_t, mpz_t)))

        Const CHUNK_SIZE As Long = 1024L
        Const STOP_AT As Long = 4L

        Dim numChunks As Long = (numTerms + CHUNK_SIZE - 1) \ CHUNK_SIZE
        LogPhase($"Processing {numChunks:N0} chunks of {CHUNK_SIZE} terms each...")

        Dim chunkP(CInt(numChunks) - 1) As mpz_t
        Dim chunkQ(CInt(numChunks) - 1) As mpz_t
        Dim chunkT(CInt(numChunks) - 1) As mpz_t

        For i As Long = 0 To numChunks - 1
            Dim chunkStart As Long = i * CHUNK_SIZE
            Dim chunkEnd As Long = System.Math.Min(chunkStart + CHUNK_SIZE, numTerms)
            BinarySplitChunk(chunkStart, chunkEnd,
                             chunkP(CInt(i)), chunkQ(CInt(i)), chunkT(CInt(i)))
            If i Mod 1000 = 0 AndAlso i > 0 Then
                Dim pct As Double = (i / numChunks) * 100
                LogPhase($"Chunks: {i:N0}/{numChunks:N0} ({pct:F1}%)")
            End If
        Next

        LogPhase($"All {numChunks:N0} chunks computed, combining to {STOP_AT} nodes...")

        Dim currentP() As mpz_t = chunkP
        Dim currentQ() As mpz_t = chunkQ
        Dim currentT() As mpz_t = chunkT
        Dim currentSize As Long = numChunks
        Dim level As Integer = 0

        While currentSize > STOP_AT
            level += 1
            Dim nextSize As Long = (currentSize + 1) \ 2
            Dim nextP(CInt(nextSize) - 1) As mpz_t
            Dim nextQ(CInt(nextSize) - 1) As mpz_t
            Dim nextT(CInt(nextSize) - 1) As mpz_t

            Dim j As Long = 0
            Dim k As Long = 0
            While j < currentSize - 1
                nextP(CInt(k)) = New mpz_t()
                nextQ(CInt(k)) = New mpz_t()
                nextT(CInt(k)) = New mpz_t()
                gmp_lib.mpz_inits(nextP(CInt(k)), nextQ(CInt(k)), nextT(CInt(k)), Nothing)

                Dim tempA As New mpz_t()
                Dim tempB As New mpz_t()
                gmp_lib.mpz_inits(tempA, tempB, Nothing)

                gmp_lib.mpz_mul(nextP(CInt(k)), currentP(CInt(j)), currentP(CInt(j + 1)))
                gmp_lib.mpz_mul(nextQ(CInt(k)), currentQ(CInt(j)), currentQ(CInt(j + 1)))
                gmp_lib.mpz_mul(tempA, currentT(CInt(j)), currentQ(CInt(j + 1)))
                gmp_lib.mpz_mul(tempB, currentP(CInt(j)), currentT(CInt(j + 1)))
                gmp_lib.mpz_add(nextT(CInt(k)), tempA, tempB)

                gmp_lib.mpz_clears(tempA, tempB, Nothing)
                gmp_lib.mpz_clears(currentP(CInt(j)), currentQ(CInt(j)),
                                   currentT(CInt(j)), Nothing)
                gmp_lib.mpz_clears(currentP(CInt(j + 1)), currentQ(CInt(j + 1)),
                                   currentT(CInt(j + 1)), Nothing)

                j += 2
                k += 1
            End While

            If currentSize Mod 2 = 1 Then
                nextP(CInt(k)) = currentP(CInt(currentSize - 1))
                nextQ(CInt(k)) = currentQ(CInt(currentSize - 1))
                nextT(CInt(k)) = currentT(CInt(currentSize - 1))
            End If

            currentP = nextP
            currentQ = nextQ
            currentT = nextT
            currentSize = nextSize

            LogPhase($"Combine level {level}: {currentSize:N0} nodes remaining")
        End While

        nodes = New List(Of Tuple(Of mpz_t, mpz_t, mpz_t))
        For i As Integer = 0 To CInt(currentSize) - 1
            nodes.Add(Tuple.Create(currentP(i), currentQ(i), currentT(i)))
        Next
    End Sub

    Private Function ComputePiGMP(digits As Long, token As CancellationToken) As String

        Dim gmpSqrtInput As New mpz_t()
        Dim gmpSqrt As New mpz_t()
        Dim gmpNumer As New mpz_t()
        Dim gmpPi As New mpz_t()
        Dim gmpOne As New mpz_t()
        Dim gmpVariablesInitialized As Boolean = False

        Try
            Dim numTerms As Long = CLng(System.Math.Ceiling(digits / 14.18)) + 10

            MessageBox.Show($"Step B: numTerms={numTerms:N0} - about to LogPhase and BinarySplitGMP")

            phaseStopWatch.Restart()
            LogPhase($"Starting: {digits:N0} digits, {numTerms:N0} terms")

            MessageBox.Show("Step C: LogPhase OK - about to call BinarySplitGMP")

            If token.IsCancellationRequested Then Return ""

            Dim nodes As List(Of Tuple(Of mpz_t, mpz_t, mpz_t)) = Nothing
            BinarySplitGMP(numTerms, nodes)

            MessageBox.Show($"Step D: BinarySplitGMP complete - {nodes.Count} nodes")

            LogPhase($"Binary Splitting complete ({nodes.Count} nodes)")

            If token.IsCancellationRequested Then Return ""

            While nodes.Count > 1
                Dim nextNodes As New List(Of Tuple(Of mpz_t, mpz_t, mpz_t))
                Dim i As Integer = 0
                While i < nodes.Count - 1
                    Dim left As Tuple(Of mpz_t, mpz_t, mpz_t) = nodes(i)
                    Dim right As Tuple(Of mpz_t, mpz_t, mpz_t) = nodes(i + 1)

                    Dim newP As New mpz_t()
                    Dim newQ As New mpz_t()
                    Dim newT As New mpz_t()
                    Dim tA As New mpz_t()
                    Dim tB As New mpz_t()
                    gmp_lib.mpz_inits(newP, newQ, newT, tA, tB, Nothing)

                    gmp_lib.mpz_mul(newP, left.Item1, right.Item1)
                    gmp_lib.mpz_mul(newQ, left.Item2, right.Item2)
                    gmp_lib.mpz_mul(tA, left.Item3, right.Item2)
                    gmp_lib.mpz_mul(tB, left.Item1, right.Item3)
                    gmp_lib.mpz_add(newT, tA, tB)

                    gmp_lib.mpz_clears(left.Item1, left.Item2, left.Item3, Nothing)
                    gmp_lib.mpz_clears(right.Item1, right.Item2, right.Item3, Nothing)
                    gmp_lib.mpz_clears(tA, tB, Nothing)

                    nextNodes.Add(Tuple.Create(newP, newQ, newT))
                    i += 2
                End While

                If nodes.Count Mod 2 = 1 Then
                    nextNodes.Add(nodes(nodes.Count - 1))
                End If

                nodes = nextNodes
                LogPhase($"Final combine: {nodes.Count} nodes remaining")
            End While

            MessageBox.Show("Step E: Final combine complete - about to sqrt")

            Dim finalP As mpz_t = nodes(0).Item1
            Dim finalQ As mpz_t = nodes(0).Item2
            Dim finalT As mpz_t = nodes(0).Item3

            gmp_lib.mpz_inits(gmpSqrtInput, gmpSqrt, gmpNumer, gmpPi, gmpOne, Nothing)
            gmpVariablesInitialized = True

            gmp_lib.mpz_ui_pow_ui(gmpOne, 10UI, CUInt(digits))
            gmp_lib.mpz_mul(gmpSqrtInput, gmpOne, gmpOne)
            gmp_lib.mpz_mul_ui(gmpSqrtInput, gmpSqrtInput, 10005UI)
            gmp_lib.mpz_sqrt(gmpSqrt, gmpSqrtInput)
            gmp_lib.mpz_clear(gmpSqrtInput)
            LogPhase("Square root complete")

            If token.IsCancellationRequested Then Return ""

            gmp_lib.mpz_mul_ui(gmpNumer, gmpSqrt, 426880UI)
            gmp_lib.mpz_mul(gmpNumer, gmpNumer, finalQ)
            gmp_lib.mpz_clears(gmpSqrt, finalQ, Nothing)
            LogPhase("Numerator complete")

            gmp_lib.mpz_tdiv_q(gmpPi, gmpNumer, finalT)
            gmp_lib.mpz_clears(gmpNumer, finalT, finalP, Nothing)
            LogPhase("Division complete")

            If token.IsCancellationRequested Then Return ""

            MessageBox.Show("Step F: About to convert to string")

            Dim piCharPtr As char_ptr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, gmpPi)
            Dim piStr As String = piCharPtr.ToString()
            gmp_lib.free(piCharPtr)
            LogPhase("String conversion complete")

            If piStr.Length > CInt(digits) + 1 Then
                piStr = piStr.Substring(0, CInt(digits) + 1)
            End If

            LogPhase($"Done! {digits:N0} digits computed")
            Return piStr(0) & "." & piStr.Substring(1)

        Catch ex As Exception
            MessageBox.Show("EXCEPTION: " & ex.Message & vbCrLf & ex.StackTrace)
            Me.BeginInvoke(Sub()
                               LblStatus.Text = "Error: " & ex.Message
                               BtnCompute.Enabled = True
                               BtnPause.Enabled = False
                               Timer1.Stop()
                           End Sub)
            Return ""
        Finally
            Try
                If gmpVariablesInitialized Then
                    ' Only clear variables that were successfully initialized
                    gmp_lib.mpz_clears(gmpPi, gmpOne, Nothing)
                End If
            Catch
            End Try
        End Try
    End Function

    Private Sub StreamPiToScreen(piString As String)
        LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Streaming started")
        Try
            System.IO.File.AppendAllText("c:\PiOutput\pi_phase_log.txt",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") &
                $" | Streaming started ({piString.Length:N0} digits)" & vbCrLf)
        Catch
        End Try
        displayTimer.Enabled = False
        RtbPiDigits.Clear()
        LblDigitsDisplayed.Text = "0"
        LblStatus.Text = $"Streaming {piString.Length:N0} digits..."
        displayStr = piString
        displayIdx = 0
        displayTotal = 0
        displayTimer.Enabled = True
    End Sub

    Private Sub DisplayTimer_Tick(sender As Object, e As EventArgs) Handles displayTimer.Tick
        If displayIdx >= displayStr.Length Then
            displayTimer.Enabled = False
            LblStatus.Text = $"Done! {displayTotal:N0} digits displayed."
            BtnCompute.Enabled = True
            BtnPause.Enabled = False
            Timer1.Stop()

            LstBoxPhases.Items.Add($"{stopWatch.Elapsed:hh\:mm\:ss\.ff} | Streaming complete")
            Try
                System.IO.File.AppendAllText("c:\PiOutput\pi_phase_log.txt",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") &
                    " | Streaming complete" & vbCrLf)
            Catch
            End Try

            If ChkboxWriteToFile.Checked Then
                LblStatus.Text = "Writing to file..."
                Try
                    If Not System.IO.Directory.Exists(
                        System.IO.Path.GetDirectoryName(outputFile)) Then
                        System.IO.Directory.CreateDirectory(
                            System.IO.Path.GetDirectoryName(outputFile))
                    End If
                    System.IO.File.WriteAllText(outputFile, displayStr)
                    LblStatus.Text = $"Done! Saved to {outputFile}"
                Catch ex As Exception
                    LblStatus.Text = "File save error: " & ex.Message
                End Try
            End If
            Return
        End If

        Dim chunkSize As Integer = 500
        If Integer.TryParse(TxtChunkSize.Text, chunkSize) = False Then chunkSize = 500
        If chunkSize < 1 Then chunkSize = 1
        If chunkSize > 1000000 Then chunkSize = 1000000
        Dim chunkEnd As Integer = System.Math.Min(displayIdx + chunkSize, displayStr.Length)

        Dim chunk As New System.Text.StringBuilder()
        While displayIdx < chunkEnd
            Dim ch As Char = displayStr(displayIdx)
            If Char.IsDigit(ch) OrElse ch = "."c Then
                chunk.Append(ch)
            End If
            displayIdx += 1
        End While

        If chunk.Length > 0 Then
            displayTotal += chunk.Length
            RtbPiDigits.AppendText(chunk.ToString())
            RtbPiDigits.SelectionStart = RtbPiDigits.TextLength
            RtbPiDigits.ScrollToCaret()
            LblDigitsDisplayed.Text = $"{displayTotal:N0}"
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

    Private Sub BtnTest_Click(sender As Object, e As EventArgs) Handles BtnTest.Click
        Dim piText As String = RtbPiDigits.Text.Replace(".", "").Replace(vbCrLf, "")

        Dim pos1 As Integer = piText.IndexOf("999999")
        If pos1 >= 0 Then
            MessageBox.Show($"Found '999999' at position {pos1}!" & vbCrLf &
                           $"Expected position: 762" & vbCrLf &
                           $"Correct: {pos1 = 762}")
        Else
            MessageBox.Show("999999 not found!")
        End If

        Dim pos2 As Integer = piText.IndexOf("777777777")
        If pos2 >= 0 Then
            MessageBox.Show($"Found '777777777' at position {pos2}!" & vbCrLf &
                           $"Expected position: 24,658,601" & vbCrLf &
                           $"Correct: {pos2 = 24658601}")
        Else
            MessageBox.Show("777777777 not found - may need more digits!")
        End If
        Dim pos3 As Integer = piText.IndexOf("27182818284")
        If pos3 >= 0 Then
            MessageBox.Show($"Found first digits of e '27182818284' at position {pos3}!")
        Else
            MessageBox.Show("First digits of e not found in first 250M digits!")
        End If
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