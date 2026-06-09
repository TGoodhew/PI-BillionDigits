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

    ' Compute result = floor(sqrt(n)).  Safe for any size n.
    ' §100: GMP's mpz_sqrt crashes at 5B digits (519M-limb input triggers
    ' mpn_mul_fft table overflow).  This Newton implementation routes every
    ' large multiplication through SafeMpzMul and every large division through
    ' SafeMpzDiv — neither calls mpn_mul_fft directly.
    '
    ' Algorithm: progressive-precision Newton, working in sqrt(n)'s domain.
    ' At each step with current precision kBitsX:
    '   target   = min(2·kBitsX + 4, bitsS + 2)
    '   nShift   = max(0, bitsN - 2·target)  [keep even]
    '   xHalf    = nShift / 2
    '   nTrunc   = n >> nShift           (~2·target bits)
    '   xTrunc   = x >> xHalf            (~target bits, sqrt(nTrunc) domain)
    '   q        = floor(nTrunc / xTrunc) (~target bits)
    '   xNew     = ((xTrunc + q) >> 1) << xHalf  [scaled back to sqrt(n) domain]
    ' Convergence: Newton quadratic convergence, ~6 large iterations for 5B digits.
    ''' <summary>
    ''' Computes result = floor(sqrt(n)), safe for any size n (§100). A progressive-precision Newton
    ''' iteration in sqrt(n)'s domain that routes every large multiply through SafeMpzMul and every
    ''' large division through <see cref="SafeMpzDiv"/> — neither calls mpn_mul_fft directly, which
    ''' GMP's mpz_sqrt would and which overflows at 5 B digits. ~6 large iterations at 5 B.
    ''' </summary>
    ''' <param name="result">Receives floor(sqrt(n)).</param>
    ''' <param name="n">Radicand.</param>
    Private Shared Sub SafeMpzSqrt(result As mpz_t, n As mpz_t)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        Dim szN As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(n.Pointer, 4))
        If CLng(szN) <= SAFE Then
            gmp_lib.mpz_sqrt(result, n)
            Return
        End If

        ' §174: gmp_lib.mpz_sizeinbase uses GMP's MSVC mp_bitcnt_t (32-bit unsigned long on Windows).
        ' For szN > 67.1M limbs (bitsN > 4.29B) the return value truncates.  szN=103.8M → 6.64B
        ' truncates to 2.35B, giving bitsS=1.17B instead of 3.32B, which causes the Newton loop
        ' to be skipped entirely (kBitsX=2.8B > bitsS+2=1.17B).  Use szN*64 as a safe upper bound.
        ' bitsS upper bound is ceil(szN*64 / 2) = (szN*64+1) / 2 = szN*32 + (if odd, 1 else 0).
        Dim bitsN As Long = CLng(szN) * 64L   ' upper bound; actual bitsN ≤ szN*64 and ≥ szN*64-63
        Dim bitsS As Long = (bitsN + 1L) >> 1   ' bits in floor(sqrt(n)) — upper bound is fine for loop termination

        ' Seed: mpz_sqrt at safe scale — result ≤ SEED_BITS bits = ~5.5M limbs
        Const SEED_BITS As Long = 350_000_000L
        Dim seedShift As Long = System.Math.Max(0L, bitsN - 2L * SEED_BITS)
        If (seedShift And 1L) <> 0L Then seedShift += 1L  ' must be even

        Dim x As New mpz_t()
        gmp_lib.mpz_init(x)
        If seedShift = 0L Then
            gmp_lib.mpz_sqrt(x, n)
        Else
            Dim nSeed As New mpz_t()
            gmp_lib.mpz_init(nSeed)
            BigShiftRight(nSeed, n, seedShift)   ' PreAllocMpzToLimbs inside BigShiftRight
            gmp_lib.mpz_sqrt(x, nSeed)           ' safe: nSeed has 2·SEED_BITS bits
            gmp_lib.mpz_clear(nSeed)
            BigShiftLeft(x, x, seedShift >> 1)   ' x ≈ sqrt(n), correct to SEED_BITS bits
        End If

        ' §106: Newton step checkpoint — resume from the last completed step if available.
        ' Checkpoint lives in snap_Phase3/sqrt_newton.bin + sqrt_newton_meta.txt.
        ' Written immediately after each step completes and backed up to SnapshotStore.
        Dim sqrtSnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim sqrtCheckBin As String = System.IO.Path.Combine(sqrtSnapDir, "sqrt_newton.bin")
        Dim sqrtCheckMeta As String = System.IO.Path.Combine(sqrtSnapDir, "sqrt_newton_meta.txt")
        Dim kBitsX As Long = SEED_BITS
        ' §203: resumed step counter — must match the OLD run's labeling so that
        ' the next iter's _divCkptScope ("sqrt_step_N") agrees with any §171-ckpt
        ' div_meta.txt left on disk by the prior run.  Without this, _newtonStep
        ' restarted from 0 and §171-ckpt scope check would always fail on resume.
        Dim _resumedNewtonStep As Integer = 0

        ' Try to load an existing Newton checkpoint.
        If _autoCheckpoint AndAlso System.IO.File.Exists(sqrtCheckBin) AndAlso System.IO.File.Exists(sqrtCheckMeta) Then
            Try
                Dim metaLines As String() = System.IO.File.ReadAllLines(sqrtCheckMeta)
                Dim meta As New Dictionary(Of String, String)()
                For Each ml As String In metaLines
                    Dim eq As Integer = ml.IndexOf("="c)
                    If eq > 0 Then meta(ml.Substring(0, eq)) = ml.Substring(eq + 1)
                Next
                Dim snapBitsN As Long = 0L, snapKBitsX As Long = 0L
                If meta.ContainsKey("bitsN") AndAlso Long.TryParse(meta("bitsN"), snapBitsN) AndAlso
                   meta.ContainsKey("kBitsX") AndAlso Long.TryParse(meta("kBitsX"), snapKBitsX) AndAlso
                   snapBitsN = bitsN AndAlso snapKBitsX > SEED_BITS Then
                    Dim staging(4194303) As Byte
                    ' x was mpz_init'd above — DeserializeOneMpz handles large limb counts.
                    Using fs As New FileStream(sqrtCheckBin, FileMode.Open, FileAccess.Read)
                        Using br As New BinaryReader(fs)
                            DeserializeOneMpz(x, br, staging)
                        End Using
                    End Using
                    kBitsX = snapKBitsX
                    ' §203: read the prior run's step counter.  Optional for backwards
                    ' compat — older meta files may lack it; default 0 is harmless.
                    Dim _snapStep As Integer = 0
                    If meta.ContainsKey("step") AndAlso Integer.TryParse(meta("step"), _snapStep) Then
                        _resumedNewtonStep = _snapStep
                    End If
                    AppendLog($"[SafeMpzSqrt] Resumed from Newton checkpoint: kBitsX={kBitsX:N0} bits, prior step={_resumedNewtonStep}{vbCrLf}")
                End If
            Catch ex As Exception
                AppendLog($"[SafeMpzSqrt] Newton checkpoint load failed ({ex.Message}) — starting from seed{vbCrLf}")
            End Try
        End If

        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] seed ready ({CLng(gmp_lib.mpz_sizeinbase(x, 10)):N0} digits); beginning Newton refinement{vbCrLf}")

        ' §SqNewton: Capture raw x and n native struct pointers before the Newton loop.
        ' SafeMpzDiv (called inside the loop) makes managed GMP calls (mpz_init/clear for r, ar,
        ' bTrunc, rSq, p inside SafeMpzReciprocal) that trigger the §78 side-effect, corrupting
        ' ALL registered mpz_t.Pointer fields — including x.Pointer and n.Pointer in this scope.
        ' By capturing raw pointers here (before any managed calls in the loop), and restoring
        ' them at the end of each iteration, BigShiftRight/GmpRaw_set use valid native structs.
        Dim _xNativePtr As IntPtr = x.Pointer
        Dim _nNativePtr As IntPtr = n.Pointer

        ' Newton refinement — doubles precision each step
        ' §203: seed _newtonStep from the resumed step counter so that on resume,
        ' the first iter's _divCkptScope continues the OLD run's numbering.
        Dim _newtonStep As Integer = _resumedNewtonStep
        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§175] Newton loop entry: kBitsX={kBitsX:N0} bitsS={bitsS:N0} bitsN={bitsN:N0} szN={szN:N0} _newtonStep={_newtonStep} loop_cond={kBitsX < bitsS + 2L}{vbCrLf}")
        Do While kBitsX < bitsS + 2L
            _newtonStep += 1
            Dim target As Long = System.Math.Min(kBitsX * 2L + 4L, bitsS + 2L)
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§175] Newton step {_newtonStep}: target={target:N0}{vbCrLf}")
            Dim nShift As Long = System.Math.Max(0L, bitsN - 2L * target)
            If (nShift And 1L) <> 0L Then nShift += 1L
            Dim xHalf As Long = nShift >> 1

            ' §SqNewton: Use raw AllocHGlobal structs for nTrunc/xTrunc/q instead of
            ' gmp_lib.mpz_init — bypasses the §78 managed-wrapper corruption.
            ' gmp_lib.mpz_init registers objects with Math.Gmp.Native, which then corrupts
            ' ALL registered Pointer fields on subsequent managed GMP calls inside SafeMpzDiv
            ' (mpz_init(r), SafeMpzReciprocal's mpz_init(bTrunc/rSq/p), mpz_clear(ar), etc.).
            ' Raw structs are invisible to the managed framework — their .Pointer fields
            ' cannot be corrupted — so SafeMpzDiv receives valid native struct addresses.
            Dim _nTruncRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_nTruncRaw)
            Dim nTrunc As New mpz_t()
            nTrunc.Pointer = _nTruncRaw
            If nShift > 0L Then
                BigShiftRight(nTrunc, n, nShift)
            Else
                PreAllocMpzToLimbs(nTrunc, CLng(szN))  ' pre-alloc to szN limbs; avoids small→large inside __gmpz_set
                GmpRaw_set(_nTruncRaw, _nNativePtr)    ' copy n using captured raw pointer — immune to §78
            End If

            Dim _xTruncRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_xTruncRaw)
            Dim xTrunc As New mpz_t()
            xTrunc.Pointer = _xTruncRaw
            If xHalf > 0L Then
                BigShiftRight(xTrunc, x, xHalf)
                ' §205: ensure xTrunc has +2 limbs of headroom for the in-place
                ' xTrunc += q add later — without it GMP must realloc 2 GB inside
                ' __gmpz_add at sqrt_step_2 which crashed reproducibly (3× on 5B run).
                Dim _szXT205a As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4))
                PreAllocMpzToLimbs(xTrunc, CLng(_szXT205a) + 2L)
            Else
                Dim _szX As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xNativePtr, 4))
                ' §205: +2 limb headroom (see comment above) — eliminates the in-place add realloc.
                PreAllocMpzToLimbs(xTrunc, CLng(_szX) + 2L)
                GmpRaw_set(_xTruncRaw, _xNativePtr)     ' copy x using captured raw pointer
            End If

            Dim _qRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_qRaw)
            Dim q As New mpz_t()
            q.Pointer = _qRaw
            Dim szNT As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_nTruncRaw, 4))
            Dim szXT As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4))

            ' §140: verify xTrunc at B2-range positions by reading x's raw limbs and comparing.
            ' xHalf=1921928090: xHalf_limb=30030126, xHalf_rem=26.
            ' xTrunc[j] = (x[30030126+j] >> 26) | (x[30030126+j+1] << 38)
            ' Check 7 evenly-spaced B2-range positions + 2 midpoint/top.
            If _logLevel >= 2 AndAlso szXT = 21875001 AndAlso xHalf = 1921928090L Then
                Dim _xHLimb140 As Long = 30030126L
                Dim _xSz140 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xNativePtr, 4))
                Dim _xD140 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_xNativePtr, 8))
                Dim _xtD140 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(xTrunc.Pointer, 8))
                ' §153: Extended check — add 8 positions in [14904664..21554666] (the B2 range contributing to A2×B2[13612996]).
                ' These positions have never been verified. A mismatch here proves BigShiftRight corrupts b at that location.
                Dim _j140_check() As Long = {14583334L, 14904664L, 15583334L, 15904664L, 16583334L, 17583334L, 17904664L, 18583334L, 19583334L, 19904664L, 20583334L, 20904664L, 21389832L, 21554666L, 21875000L}
                Dim _sb140 As New System.Text.StringBuilder()
                _sb140.Append($"[SafeMpzSqrt§140] szX={_xSz140:N0} xHalf=1921928090 xHalf_limb=30030126{vbCrLf}")
                For Each _j140 As Long In _j140_check
                    Dim _x0 As Long = If(_xHLimb140 + _j140 < CLng(_xSz140), Runtime.InteropServices.Marshal.ReadInt64(_xD140, CInt((_xHLimb140 + _j140) * 8L)), 0L)
                    Dim _x1 As Long = If(_xHLimb140 + _j140 + 1L < CLng(_xSz140), Runtime.InteropServices.Marshal.ReadInt64(_xD140, CInt((_xHLimb140 + _j140 + 1L) * 8L)), 0L)
                    Dim _xt_exp As Long = CLng(CULng(_x0) >> 26) Or CLng(CULng(_x1) << 38)
                    Dim _xt_act As Long = If(_j140 < CLng(szXT), Runtime.InteropServices.Marshal.ReadInt64(_xtD140, CInt(_j140 * 8L)), 0L)
                    _sb140.Append($"  j={_j140:N0}: x[{_xHLimb140+_j140}]={_x0:X16} x[{_xHLimb140+_j140+1}]={_x1:X16} exp={_xt_exp:X16} act={_xt_act:X16} match={_xt_exp = _xt_act}{vbCrLf}")
                Next
                AppendLog(_sb140.ToString())
            End If
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] Newton step {_newtonStep}: target={target:N0} bits, div {szNT:N0}/{szXT:N0} limbs{vbCrLf}")
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§133-probe] szNT={szNT} szXT={szXT} logLevel={_logLevel} cond={szNT = 43750001 AndAlso szXT = 21875001}{vbCrLf}")
            ' §133: verify nTrunc[42779664] by reading n directly, bypassing BigShiftRight.
            ' nTrunc = n >> nShift: limb_off = nShift\64, bit_shift = nShift Mod 64.
            ' nTrunc[42779664] = (n[limb_off+42779664] >> bit_shift) | (n[limb_off+42779665] << (64-bit_shift))
            ' If this matches nTrunc[42779664] AND §118b's a[42779664], BigShiftRight is correct.
            ' If they differ, BigShiftRight has a bug OR n is corrupted.
            If _logLevel >= 2 AndAlso szNT = 43750001 AndAlso szXT = 21875001 Then
                Const _TGT133 As Long = 42779664L
                Dim _limb133 As Long = nShift \ 64L
                Dim _bit133 As Integer = CInt(nShift Mod 64L)
                Dim _nSz133 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_nNativePtr, 4))
                Dim _nD133 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_nNativePtr, 8))
                Dim _idxLo133 As Long = _limb133 + _TGT133
                Dim _idxHi133 As Long = _limb133 + _TGT133 + 1L
                Dim _vLo133 As Long = If(_idxLo133 < CLng(_nSz133), Runtime.InteropServices.Marshal.ReadInt64(_nD133, CInt(_idxLo133 * 8L)), 0L)
                Dim _vHi133 As Long = If(_idxHi133 < CLng(_nSz133), Runtime.InteropServices.Marshal.ReadInt64(_nD133, CInt(_idxHi133 * 8L)), 0L)
                Dim _expected133 As Long = CLng(CULng(_vLo133) >> _bit133) Or CLng(CULng(_vHi133) << (64 - _bit133))
                Dim _nTruncSz133 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(nTrunc.Pointer, 4))
                Dim _nTruncD133 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(nTrunc.Pointer, 8))
                Dim _actual133 As Long = If(_TGT133 < CLng(_nTruncSz133), Runtime.InteropServices.Marshal.ReadInt64(_nTruncD133, CInt(_TGT133 * 8L)), 0L)
                AppendLog($"[SafeMpzSqrt§133] nShift={nShift:N0} limb_off={_limb133:N0} bit_shift={_bit133}" &
                          $" n[{_idxLo133:N0}]={_vLo133:X16} n[{_idxHi133:N0}]={_vHi133:X16}" &
                          $" expected nTrunc[{_TGT133:N0}]={_expected133:X16} actual nTrunc[{_TGT133:N0}]={_actual133:X16}" &
                          $" MATCH={_expected133 = _actual133}{vbCrLf}")
            End If
            If CLng(szNT) + CLng(szXT) <= SAFE Then
                GmpRaw_tdiv_q(q.Pointer, nTrunc.Pointer, xTrunc.Pointer)  ' §35
            Else
                _divCkptScope = $"sqrt_step_{_newtonStep}"
                SafeMpzDiv(q, nTrunc, xTrunc)
            End If
            AppendLog($"[SafeMpzSqrt§202-postdiv] step {_newtonStep}: SafeMpzDiv returned; entering post-divide cleanup (xHalf={xHalf:N0} target={target:N0}){vbCrLf}")
            ' §SqNewton: Use raw GmpRaw ops for cleanup — no managed mpz_clear/mpz_add.
            ' After SafeMpzDiv, all managed mpz_t.Pointer fields in this scope are potentially
            ' corrupted by §78. We use only the captured raw IntPtrs (_nTruncRaw, _xTruncRaw,
            ' _qRaw, _xNativePtr) for all post-SafeMpzDiv operations.
            GmpRaw_clear(_nTruncRaw)                                              ' free nTrunc limb buffer
            nTrunc.Pointer = IntPtr.Zero                                          ' prevent finalizer mpz_clear
            Runtime.InteropServices.Marshal.FreeHGlobal(_nTruncRaw)
            AppendLog($"[SafeMpzSqrt§202-postdiv] nTrunc cleared and freed{vbCrLf}")

            ' §204-trace: dump _xTruncRaw and _qRaw struct fields (alloc, size, _mp_d) immediately
            ' before GmpRaw_add — second 5B-run-1 death (08:49 PT, log frozen at "nTrunc cleared
            ' and freed" both times) is reproducibly inside or after this line.  If either struct
            ' is corrupted, this trace catches it; if both look healthy, the crash is inside
            ' __gmpz_add itself (allocator failure, GMP abort, etc.).
            Dim _xtAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 0)
            Dim _xtSize As Integer = Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4)
            Dim _xtD As Long = Runtime.InteropServices.Marshal.ReadInt64(_xTruncRaw, 8)
            Dim _qAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(_qRaw, 0)
            Dim _qSize As Integer = Runtime.InteropServices.Marshal.ReadInt32(_qRaw, 4)
            Dim _qD As Long = Runtime.InteropServices.Marshal.ReadInt64(_qRaw, 8)
            AppendLog($"[SafeMpzSqrt§204-pre-add] xTrunc struct: alloc={_xtAlloc:N0} size={_xtSize:N0} _mp_d={_xtD:X16} (raw={_xTruncRaw.ToInt64():X16}){vbCrLf}")
            AppendLog($"[SafeMpzSqrt§204-pre-add] q      struct: alloc={_qAlloc:N0} size={_qSize:N0} _mp_d={_qD:X16} (raw={_qRaw.ToInt64():X16}){vbCrLf}")
            ' Also probe first/last limb of each so we know the limb buffers are mapped (an AV
            ' inside __gmpz_add reading the limb buffer would happen here too, but at least the
            ' last log line written would point us at the operand whose buffer is bad).
            If _xtD <> 0 AndAlso _xtSize > 0 Then
                Dim _xtBot As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_xtD), 0)
                Dim _xtTop As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_xtD), CInt((CLng(_xtSize) - 1L) * 8L))
                AppendLog($"[SafeMpzSqrt§204-pre-add] xTrunc limbs: [0]={_xtBot:X16} [{_xtSize - 1}]={_xtTop:X16}{vbCrLf}")
            End If
            If _qD <> 0 AndAlso _qSize > 0 Then
                Dim _qBot As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qD), 0)
                Dim _qTop As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qD), CInt((CLng(_qSize) - 1L) * 8L))
                AppendLog($"[SafeMpzSqrt§204-pre-add] q      limbs: [0]={_qBot:X16} [{_qSize - 1}]={_qTop:X16}{vbCrLf}")
            End If
            AppendLog($"[SafeMpzSqrt§204-pre-add] calling GmpRaw_add (in-place xTrunc += q)…{vbCrLf}")

            GmpRaw_add(_xTruncRaw, _xTruncRaw, _qRaw)                           ' xTrunc += q
            AppendLog($"[SafeMpzSqrt§202-postdiv] xTrunc += q complete (szXT={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_xTruncRaw, 4)):N0}){vbCrLf}")
            GmpRaw_clear(_qRaw)                                                   ' free q limb buffer
            q.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_qRaw)
            GmpRaw_tdiv_q_2exp(_xTruncRaw, _xTruncRaw, 1UI)                     ' xTrunc >>= 1
            AppendLog($"[SafeMpzSqrt§202-postdiv] q freed; xTrunc >>= 1 done{vbCrLf}")

            If xHalf > 0L Then
                AppendLog($"[SafeMpzSqrt§202-postdiv] BigShiftLeft xHalf={xHalf:N0} starting{vbCrLf}")
                BigShiftLeft(xTrunc, xTrunc, xHalf)
                AppendLog($"[SafeMpzSqrt§202-postdiv] BigShiftLeft done{vbCrLf}")
            End If
            GmpRaw_swap(_xNativePtr, _xTruncRaw)  ' swap: x's native struct gets new Newton value
            x.Pointer = _xNativePtr               ' restore managed x.Pointer (corrupted by SafeMpzDiv §78)
            n.Pointer = _nNativePtr               ' restore managed n.Pointer for next iteration
            GmpRaw_clear(_xTruncRaw)                                              ' free old x limb buffer
            xTrunc.Pointer = IntPtr.Zero
            Runtime.InteropServices.Marshal.FreeHGlobal(_xTruncRaw)
            kBitsX = target
            AppendLog($"[SafeMpzSqrt§202-postdiv] swap+free complete; kBitsX advanced to {kBitsX:N0}{vbCrLf}")

            ' §106: Save Newton step checkpoint immediately after completion.
            If _autoCheckpoint Then
                Try
                    AppendLog($"[SafeMpzSqrt§202-ckpt] starting sqrt_newton.bin save (step={_newtonStep} kBitsX={kBitsX:N0}){vbCrLf}")
                    If Not System.IO.Directory.Exists(sqrtSnapDir) Then
                        System.IO.Directory.CreateDirectory(sqrtSnapDir)
                    End If
                    Dim staging(4194303) As Byte
                    Using fs As New FileStream(sqrtCheckBin, FileMode.Create, FileAccess.Write)
                        Using bw As New BinaryWriter(fs)
                            SerializeOneMpz(x, bw, staging)
                        End Using
                    End Using
                    AppendLog($"[SafeMpzSqrt§202-ckpt] sqrt_newton.bin written; writing meta{vbCrLf}")
                    System.IO.File.WriteAllText(sqrtCheckMeta,
                        $"bitsN={bitsN}{vbLf}kBitsX={kBitsX}{vbLf}step={_newtonStep}{vbLf}")
                    AppendLog($"[SafeMpzSqrt§202-ckpt] meta written; calling BackupSnapshotToStore{vbCrLf}")
                    BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                    AppendLog($"[SafeMpzSqrt] Newton step {_newtonStep} checkpoint saved (kBitsX={kBitsX:N0}){vbCrLf}")
                Catch ex As Exception
                    AppendLog($"[SafeMpzSqrt] Newton checkpoint save failed: {ex.Message}{vbCrLf}")
                End Try
            End If
            AppendLog($"[SafeMpzSqrt§202-postdiv] step {_newtonStep} fully complete; looping (kBitsX={kBitsX:N0} bitsS+2={bitsS + 2L:N0} cont={kBitsX < bitsS + 2L}){vbCrLf}")

            ' §219 (issue #79): drain finalizer queue at SafeMpzSqrt Newton step boundary.
            ' Each sqrt-Newton step runs a full SafeMpzDiv internally (which itself runs
            ' SafeMpzReciprocal Newton loop), creating thousands of mpz_t wrappers.
            ' Sqrt-Newton at 1B+ scale has 4 steps each ~2.5 hours single-threaded; the
            ' finalizer backlog accumulated during a step is best drained at the step
            ' boundary while we're momentarily in cleanup before the next step's SafeMpzMul.
            DrainFinalizers()
        Loop

        If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt] Newton done; final adjustment{vbCrLf}")

        ' Final adjustment: ensure result = floor(sqrt(n)) exactly (off by at most 1).
        '
        ' §228 (issue #54, 2026-05-23): parallelize the two initial squarings (xSq = x²,
        ' x1Sq = (x+1)²) via Parallel.Invoke.  Both have disjoint inputs and result buffers;
        ' the original §207 force-serial-DOP guard was for a 5B-run-6 crash inside SafeMpzMul's
        ' own recursive Parallel.For — that crash mode was removed by §220 (#55, force-serial
        ' caps lifted) and §221 (#44, size-gate lifted) so the recursion can now safely run at
        ' the caller's _safeMulDop.  Adj-down and adj-up loops remain serial (one squaring at
        ' a time, 0-1 iter typical) and use the inherited DOP — no extra outer parallelism.
        '
        ' Expected impact: ~halves final-adj wall time (~10-20 h saved at 5B).  §206 pre-alloc
        ' guards retained: x1 and x1Sq must be pre-sized before mpz_add_ui / SafeMpzMul to
        ' avoid silent realloc inside Parallel.Invoke tasks.
        Dim _szX228 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(x.Pointer, 4))
        AppendLog($"[SafeMpzSqrt§228] entering parallel final-adj: szX={_szX228:N0} limbs (currentDop={System.Threading.Volatile.Read(_safeMulDop)}){vbCrLf}")
        ' §275 (#121): decide whether to route the final-adjustment squarings through the chunked
        ' grid (parallel cells at PI_CG_DOP, low per-cell RAM) instead of §gen at the inherited DOP.
        _sqrtChunkedGrid = (Environment.GetEnvironmentVariable("PI_SQRT_CG") <> "0")
        Dim _scgEnv As String = Environment.GetEnvironmentVariable("PI_SQRT_CG_MINLIMBS")
        Dim _scgParsed As Long
        If _scgEnv IsNot Nothing AndAlso Long.TryParse(_scgEnv, _scgParsed) AndAlso _scgParsed >= 0L Then _sqrtCgMinLimbs = _scgParsed
        Dim _useCgSqrt As Boolean = _sqrtChunkedGrid AndAlso CLng(_szX228) >= _sqrtCgMinLimbs
        If _useCgSqrt Then AppendLog($"[SafeMpzSqrt§275] final-adj squarings via chunked-grid (szX={_szX228:N0} >= {_sqrtCgMinLimbs:N0}){vbCrLf}")

        ' Pre-compute x1 = x+1 before launching the parallel pair (cannot do this inside the
        ' Parallel.Invoke task without races on x).
        Dim x1 As New mpz_t()
        gmp_lib.mpz_init(x1)
        PreAllocMpzToLimbs(x1, CLng(_szX228) + 2L)  ' §206: avoid silent realloc inside __gmpz_add_ui
        gmp_lib.mpz_add_ui(x1, x, 1UI)
        AppendLog($"[SafeMpzSqrt§228] x1 = x+1 computed (size={Runtime.InteropServices.Marshal.ReadInt32(x1.Pointer, 4):N0}){vbCrLf}")

        ' Pre-alloc both result buffers up front so Parallel.Invoke tasks don't race on realloc.
        Dim xSq As New mpz_t()
        gmp_lib.mpz_init(xSq)
        PreAllocMpzToLimbs(xSq, 2L * CLng(_szX228) + 4L)  ' §206/§207: pre-size to 2·szX+4
        Dim x1Sq As New mpz_t()
        gmp_lib.mpz_init(x1Sq)
        PreAllocMpzToLimbs(x1Sq, 2L * CLng(_szX228) + 4L)
        AppendLog($"[SafeMpzSqrt§228] xSq and x1Sq pre-alloc'd ({(2L * CLng(_szX228) + 4L):N0} limbs each, ~{((2L * CLng(_szX228) + 4L) * 8L) \ (1024L * 1024L):N0} MB){vbCrLf}")

        ' §228: launch the two squarings concurrently.  Inner SafeMpzMul fans out per §220/§221.
        AppendLog($"[SafeMpzSqrt§228] Parallel.Invoke(xSq=x*x, x1Sq=x1*x1) starting{vbCrLf}")
        Dim _t228Ticks As Long = System.Diagnostics.Stopwatch.GetTimestamp()
        If _useCgSqrt Then
            ' §275: each chunked-grid squaring already saturates up to PI_CG_DOP cores, so run the
            ' two sequentially (no Parallel.Invoke ⇒ no core oversubscription).  CG squaring is
            ' bit-exact (--test-chunkedgrid sq=True) and far faster than §gen at these sizes.
            SafeMpzMulCG(xSq, x, x)
            SafeMpzMulCG(x1Sq, x1, x1)
        Else
            System.Threading.Tasks.Parallel.Invoke(
                Sub() SafeMpzMul(xSq, x, x),
                Sub() SafeMpzMul(x1Sq, x1, x1))
        End If
        Dim _t228Elapsed As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t228Ticks) / System.Diagnostics.Stopwatch.Frequency
        AppendLog($"[SafeMpzSqrt§228] Parallel.Invoke done; elapsed={_t228Elapsed:F2}s (szXSq={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(xSq.Pointer, 4)):N0} szX1Sq={System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(x1Sq.Pointer, 4)):N0}){vbCrLf}")

        ' adj-down: x²>n means Newton overshot; rare (0-1 iter typical from Newton convergence).
        ' Keep x1 in sync so the post-adj x1Sq matches the new (x+1).
        Dim _adjDownSqrt As Integer = 0
        Do While GmpRaw_cmp(xSq.Pointer, n.Pointer) > 0   ' §35: x² > n → x too large
            _adjDownSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-down iter={_adjDownSqrt} (x²>n){vbCrLf}")
            gmp_lib.mpz_sub_ui(x, x, 1UI)
            gmp_lib.mpz_sub_ui(x1, x1, 1UI)
            If _useCgSqrt Then SafeMpzMulCG(xSq, x, x) Else SafeMpzMul(xSq, x, x)   ' §275
        Loop
        If _adjDownSqrt > 0 Then
            AppendLog($"[SafeMpzSqrt§228] adj-down ran {_adjDownSqrt} iter(s); recomputing x1Sq for new x1{vbCrLf}")
            If _useCgSqrt Then SafeMpzMulCG(x1Sq, x1, x1) Else SafeMpzMul(x1Sq, x1, x1)   ' §275
        Else
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-down done: 0 iter(s); x1Sq from parallel pair still valid{vbCrLf}")
        End If
        gmp_lib.mpz_clear(xSq)

        ' adj-up: (x+1)² ≤ n means Newton undershot; rare.
        Dim _adjUpSqrt As Integer = 0
        Do While GmpRaw_cmp(x1Sq.Pointer, n.Pointer) <= 0   ' §35: (x+1)² ≤ n → x too small
            _adjUpSqrt += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzSqrt§228] adj-up iter={_adjUpSqrt} ((x+1)²≤n){vbCrLf}")
            GmpRaw_swap(x.Pointer, x1.Pointer)  ' §35
            gmp_lib.mpz_add_ui(x1, x, 1UI)
            If _useCgSqrt Then SafeMpzMulCG(x1Sq, x1, x1) Else SafeMpzMul(x1Sq, x1, x1)   ' §275
        Loop
        AppendLog($"[SafeMpzSqrt§228] adj-up done: {_adjUpSqrt} iter(s); SafeMpzSqrt complete{vbCrLf}")
        gmp_lib.mpz_clear(x1)
        gmp_lib.mpz_clear(x1Sq)

        GmpRaw_swap(result.Pointer, x.Pointer)  ' §35
        gmp_lib.mpz_clear(x)
    End Sub
End Class
