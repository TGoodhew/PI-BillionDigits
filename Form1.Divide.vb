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

    ' Compute q = floor(a / b).  Safe for any operand size.
    ' Uses Barrett-style Newton reciprocal + SafeMpzMul — no direct GMP division
    ' for large inputs (which would crash via mpn_mul_fft overflow at 5B digits).
    ''' <summary>
    ''' Computes q = floor(a / b), safe for any operand size. Forms the Barrett quotient from
    ''' <see cref="SafeMpzReciprocal"/> (q ≈ (a · r) ≫ kBits, the a×r §262 and q×b §269 multiplies
    ''' running on the chunked grid), then corrects it to the exact floor. No direct GMP division —
    ''' which would crash via mpn_mul_fft overflow at 5 B digits.
    ''' </summary>
    ''' <param name="q">Receives the exact integer quotient floor(a / b).</param>
    ''' <param name="a">Dividend.</param>
    ''' <param name="b">Divisor.</param>
    ''' <remarks>
    ''' CONTRACT: the reciprocal r is a strict underestimate (§107), so the raw Barrett quotient is at
    ''' or just below the true value; the §171/§218 adjust loop then nudges q by ±1 (bounded by
    ''' MAX_ADJ_ITERS) until a − q·b ∈ [0, b), yielding the exact floor. This is why over-estimating the
    ''' a×r / q×b high-products (§262/§269) is safe — the adjustment converges either way and π is
    ''' bit-identical.
    ''' </remarks>
    Private Shared Sub SafeMpzDiv(q As mpz_t, a As mpz_t, b As mpz_t)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        Const MAX_ADJ_ITERS As Integer = 10   ' Barrett should need ≤ 2; >10 means reciprocal is wrong
        ' §184c: Capture a.Pointer and b.Pointer as plain IntPtrs immediately on entry.
        ' Every SafeMpzMul call in this function (a×r and q×b) triggers the §78 side-effect,
        ' corrupting ALL registered mpz_t Pointer fields — including a.Pointer and b.Pointer.
        ' These pre-captured values remain valid for the lifetime of SafeMpzDiv.
        Dim _aPtr As IntPtr = a.Pointer
        Dim _bPtr As IntPtr = b.Pointer
        Dim szA As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(a.Pointer, 4))
        Dim szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
        If CLng(szA) + CLng(szB) <= SAFE Then
            GmpRaw_tdiv_q(q.Pointer, a.Pointer, b.Pointer)  ' §35
            Return
        End If

        ' §172: Removed — §173 (zero lower bits after each Newton iter) fixes SafeMpzReciprocal
        ' convergence for all szB, so the mpz_tdiv_q bypass is no longer needed.

        ' §174: Compute aBits/bBits directly from limb counts to avoid GMP's mpz_sizeinbase
        ' truncation bug: on GMP's MSVC build mp_bitcnt_t is 32-bit unsigned long, so
        ' mpz_sizeinbase overflows for numbers > ~67M limbs (2^32 / 64 = 67.1M), returning
        ' a truncated value.  For a=103.8M limbs (6.64B bits) the truncated result is 2.35B
        ' which makes kBits < bBits → rBits negative → Newton loop skipped → Barrett fails.
        ' Using szA*64 and szB*64 gives correct upper bounds (within 63 bits of actual).
        Dim aBits As Long = CLng(szA) * 64L
        Dim bBits As Long = CLng(szB) * 64L
        ' r = floor(2^kBits / b), kBits = aBits+3 (Barrett: quotient_bits + divisor_bits + margin)
        Dim kBits As Long = aBits + 3L
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] entry: szA={szA:N0} szB={szB:N0} aBits={aBits:N0} bBits={bBits:N0} kBits={kBits:N0}{vbCrLf}")

        ' §171-ckpt: lifted declarations so the resume path can populate them.
        ' Original assignments stay where they were (now without "Dim").
        Dim _qPtr As IntPtr = IntPtr.Zero
        Dim szQ As Integer = 0
        Dim _ckpQResumed As Boolean = False

        ' §171-ckpt: Resume from a saved div_q checkpoint if one exists for this exact call.
        ' div_q.bin holds the post-shift Barrett quotient; on resume we skip the Newton
        ' reciprocal, a×r, BigShiftRight, and the swap, and jump straight to q×b.
        Dim _divCkptDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _divCkptBin As String = System.IO.Path.Combine(_divCkptDir, "div_q.bin")
        Dim _divCkptMeta As String = System.IO.Path.Combine(_divCkptDir, "div_meta.txt")
        If _autoCheckpoint AndAlso System.IO.File.Exists(_divCkptBin) AndAlso System.IO.File.Exists(_divCkptMeta) Then
            Try
                Dim _metaLines As String() = System.IO.File.ReadAllLines(_divCkptMeta)
                Dim _meta As New Dictionary(Of String, String)()
                For Each _ml As String In _metaLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _meta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _snapSzA As Integer = 0, _snapSzB As Integer = 0
                Dim _snapABits As Long = 0L, _snapKBits As Long = 0L
                Dim _snapScope As String = ""
                If _meta.ContainsKey("szA") AndAlso Integer.TryParse(_meta("szA"), _snapSzA) AndAlso
                   _meta.ContainsKey("szB") AndAlso Integer.TryParse(_meta("szB"), _snapSzB) AndAlso
                   _meta.ContainsKey("aBits") AndAlso Long.TryParse(_meta("aBits"), _snapABits) AndAlso
                   _meta.ContainsKey("kBits") AndAlso Long.TryParse(_meta("kBits"), _snapKBits) AndAlso
                   _meta.ContainsKey("scope") Then
                    _snapScope = _meta("scope")
                    If _snapSzA = szA AndAlso _snapSzB = szB AndAlso _snapABits = aBits AndAlso
                       _snapKBits = kBits AndAlso _snapScope = _divCkptScope Then
                        Dim _qStaging(4194303) As Byte
                        Using _fs As New FileStream(_divCkptBin, FileMode.Open, FileAccess.Read)
                            Using _br As New BinaryReader(_fs)
                                DeserializeOneMpz(q, _br, _qStaging)
                            End Using
                        End Using
                        _qPtr = q.Pointer
                        szQ = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qPtr, 4))
                        _ckpQResumed = True
                        AppendLog($"[SafeMpzDiv§171-ckpt] resumed: skipping Newton+a×r+shift (szQ={szQ:N0} scope={_divCkptScope}){vbCrLf}")
                    End If
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzDiv§171-ckpt] load failed ({_ex.Message}) — running full path{vbCrLf}")
            End Try
        End If
        If _ckpQResumed Then GoTo PostShiftCheckpoint

        Dim r As New mpz_t()
        gmp_lib.mpz_init(r)
        ' §220 (issue #55, 2026-05-22): §168 force-serial LIFTED.
        ' Original §168 forced _safeMulDop=1 for SafeMpzReciprocal because bTrunc×rSq
        ' inside iter=25 produced wrong r under parallel execution. Root cause turned
        ' out to be the Newton premature-convergence bug fixed by §200 (_minNrIters =
        ' log2(rBits)+3) + §201-raise. With those fixes in place, a wrong reciprocal
        ' can no longer be produced regardless of DOP. The §144/§170/§169 in-loop
        ' verifiers stay enabled and would catch any regression early.
        ' Original lines preserved as comments for easy revert (grep §220):
        '   Dim _saved168Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
        '   System.Threading.Volatile.Write(_safeMulDop, 1)
        '   ...AppendLog($"[SafeMpzDiv§168] forcing all-serial...
        '   System.Threading.Volatile.Write(_safeMulDop, _saved168Dop)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §168 lifted — SafeMpzReciprocal runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        SafeMpzReciprocal(r, b, kBits)
        Dim szR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] reciprocal done: szR={szR:N0}{vbCrLf}")

        ' §116: verify r interior limbs — are lower limbs within each piece zero?
        ' §147: extend to include r[20904662] and r[20904663] — hypothesized error site
        If _logLevel >= 2 Then
            Dim _r116DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r116Bot0 As Long = If(szR >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 0), 0L)
            Dim _r116Bot1 As Long = If(szR >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 8), 0L)
            Dim _r116Mid As Long = If(szR >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 10937500 * 8), 0L)
            Dim _r116Near2 As Long = If(szR >= 20904663, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904662 * 8), 0L)
            Dim _r116Near1 As Long = If(szR >= 20904664, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904663 * 8), 0L)
            Dim _r116Near As Long = If(szR >= 20904665, Runtime.InteropServices.Marshal.ReadInt64(_r116DPtr, 20904664 * 8), 0L)
            AppendLog($"[SafeMpzDiv§116] r limbs: bot=[{_r116Bot0:X16} {_r116Bot1:X16}] mid[10937500]={_r116Mid:X16} [20904662]={_r116Near2:X16} [20904663]={_r116Near1:X16} [20904664]={_r116Near:X16}{vbCrLf}")
            Dim _a116DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _a116Bot As Long = If(szA >= 1, Runtime.InteropServices.Marshal.ReadInt64(_a116DPtr, 0), 0L)
            Dim _a116Mid As Long = If(szA >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_a116DPtr, 10937500 * 8), 0L)
            AppendLog($"[SafeMpzDiv§116b] a limbs: bot={_a116Bot:X16} mid[10937500]={_a116Mid:X16}{vbCrLf}")
        End If

        ' §117: verify r limbs in the unsampled near-top region (20,904,665..21,874,998)
        If _logLevel >= 2 Then
            Dim _r117DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r117_a As Long = If(szR >= 20904666, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 20904665 * 8), 0L)
            Dim _r117_b As Long = If(szR >= 21000001, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21000000 * 8), 0L)
            Dim _r117_c As Long = If(szR >= 21500001, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21500000 * 8), 0L)
            Dim _r117_d As Long = If(szR >= 21874999, Runtime.InteropServices.Marshal.ReadInt64(_r117DPtr, 21874998 * 8), 0L)
            AppendLog($"[SafeMpzDiv§117] r near-top: [20904665]={_r117_a:X16} [21000000]={_r117_b:X16} [21500000]={_r117_c:X16} [21874998]={_r117_d:X16}{vbCrLf}")
        End If

        ' §144: verify b×r ≈ 2^kBits — correct r means b×r < 2^kBits exactly.
        ' Checks limb kBits\64 (should have bits kBits%64..63 = 0) and limb kBits\64+1 (should be 0).
        ' A nonzero result means SafeMpzReciprocal overestimated r, which would cause Barrett to fail.
        If _logLevel >= 2 AndAlso szR = 21875001 Then
            Dim _br144 As New mpz_t()
            gmp_lib.mpz_init(_br144)
            AppendLog($"[SafeMpzDiv§144] computing b*r to verify reciprocal (szB={szB:N0} szR={szR:N0})...{vbCrLf}")
            ' §144-serial: force serial to avoid parallel SafeMpzMul race condition corrupting diagnostic
            Dim _saved144Dop As Integer = System.Threading.Volatile.Read(_safeMulDop)
            System.Threading.Volatile.Write(_safeMulDop, 1)
            SafeMpzMul(_br144, b, r)
            System.Threading.Volatile.Write(_safeMulDop, _saved144Dop)
            Dim _szBR144 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_br144.Pointer, 4))
            Dim _kLimb144 As Long = kBits \ 64L
            Dim _kRem144 As Integer = CInt(kBits Mod 64L)
            Dim _br144DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_br144.Pointer, 8))
            Dim _br144_km1 As Long = If(_szBR144 > _kLimb144 - 1, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144 - 1) * 8), 0L)
            Dim _br144_k As Long = If(_szBR144 > _kLimb144, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144) * 8), 0L)
            Dim _br144_kp1 As Long = If(_szBR144 > _kLimb144 + 1, Runtime.InteropServices.Marshal.ReadInt64(_br144DPtr, CInt(_kLimb144 + 1) * 8), 0L)
            Dim _rOk144 As Boolean = (_br144_kp1 = 0L) AndAlso (CULng(_br144_k) < CULng(1L << _kRem144))
            AppendLog($"[SafeMpzDiv§144] b*r sz={_szBR144:N0} kLimb={_kLimb144:N0} kRem={_kRem144} b*r[kLimb-1]={_br144_km1:X16} b*r[kLimb]={_br144_k:X16} b*r[kLimb+1]={_br144_kp1:X16} maxOK={(1L << _kRem144) - 1L:X16} r_valid={_rOk144}{vbCrLf}")
            ' §169: Check LOWER bound — b*(r+1) > 2^kBits. If false, r < floor(2^kBits/b), i.e., r is too small.
            ' b*(r+1) = b*r + b. Check if adding b to b*r causes bit kBits to be set.
            ' This is cheap (O(n) add) since b*r is already computed. No extra SafeMpzMul needed.
            gmp_lib.mpz_add(_br144, _br144, b)
            Dim _szBRp1 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_br144.Pointer, 4))
            Dim _brp1DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_br144.Pointer, 8))
            Dim _brp1_k As Long = If(_szBRp1 > _kLimb144, Runtime.InteropServices.Marshal.ReadInt64(_brp1DPtr, CInt(_kLimb144) * 8), 0L)
            Dim _brp1_kp1 As Long = If(_szBRp1 > _kLimb144 + 1, Runtime.InteropServices.Marshal.ReadInt64(_brp1DPtr, CInt(_kLimb144 + 1) * 8), 0L)
            ' b*(r+1) > 2^kBits iff bit kBits is set, i.e., b*(r+1)[kLimb] has bit kRem set OR [kLimb+1]≠0
            Dim _rTight169 As Boolean = (CULng(_brp1_k) >= CULng(1L << _kRem144)) OrElse (_brp1_kp1 <> 0L)
            AppendLog($"[SafeMpzDiv§169] b*(r+1) sz={_szBRp1:N0} [kLimb]={_brp1_k:X16} [kLimb+1]={_brp1_kp1:X16} r_tight(lower_bound_ok)={_rTight169}{vbCrLf}")
            ' §170: Measure exact error magnitude of r. delta = 2^kBits - b*r.
            ' At this point _br144 = b*(r+1) = b*r + b, so:
            '   delta = 2^kBits - b*r = 2^kBits + b - b*(r+1) = 2^kBits + b - _br144
            ' szDelta > szB means r is wrong by more than 1 full unit, i.e. massively off.
            ' r_error ~ delta/b in limbs: szDelta - szB = approximate limb-count of r's error.
            Dim _pow170 As New mpz_t()
            gmp_lib.mpz_init(_pow170)
            gmp_lib.mpz_set_ui(_pow170, 1UI)
            gmp_lib.mpz_mul_2exp(_pow170, _pow170, New mp_bitcnt_t(CUInt(kBits)))
            gmp_lib.mpz_add(_pow170, _pow170, b)        ' 2^kBits + b
            gmp_lib.mpz_sub(_pow170, _pow170, _br144)   ' 2^kBits + b - b*(r+1) = delta
            Dim _szDelta170 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_pow170.Pointer, 4))
            Dim _delta170DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_pow170.Pointer, 8))
            Dim _delta170Top As Long = If(_szDelta170 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_delta170DPtr, CInt((_szDelta170 - 1) * 8L)), 0L)
            Dim _delta170Top2 As Long = If(_szDelta170 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_delta170DPtr, CInt((_szDelta170 - 2) * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§170] delta=2^kBits-b*r szDelta={_szDelta170:N0} szB={szB:N0} r_error_limbs~={_szDelta170 - szB:N0} top2=[{_delta170Top:X16} {_delta170Top2:X16}]{vbCrLf}")
            gmp_lib.mpz_clear(_pow170)
            gmp_lib.mpz_clear(_br144)
        End If

        ' q_approx = floor(a · r / 2^kBits)
        Dim ar As New mpz_t()
        gmp_lib.mpz_init(ar)
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] computing a*r (szA={szA:N0} szR={szR:N0})...{vbCrLf}")
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szA can be 998M at 5B).
            Dim _aDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _aTop As Long = If(szA >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_aDPtr.ToInt64() + (CLng(szA) - 1L) * 8L), 0), 0L)
            Dim _aTop2 As Long = If(szA >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_aDPtr.ToInt64() + (CLng(szA) - 2L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzDiv] a top2=[{_aTop:X16} {_aTop2:X16}]{vbCrLf}")
        End If
        ' §154: log r at key positions for independent ar verification.
        If _logLevel >= 2 AndAlso szR = 21875001 Then
            Dim _r154DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            Dim _r154_pos() As Long = {20904664L, 20904665L, 21000000L, 21500000L, 21874999L, 21875000L}
            Dim _sb154 As New System.Text.StringBuilder()
            _sb154.Append($"[SafeMpzDiv§154] r at key positions (szR={szR:N0}):{vbCrLf}")
            For Each _p154 As Long In _r154_pos
                Dim _v154 As Long = If(_p154 < CLng(szR), Runtime.InteropServices.Marshal.ReadInt64(_r154DPtr, CInt(_p154 * 8L)), 0L)
                _sb154.Append($"  r[{_p154:N0}]={_v154:X16}{vbCrLf}")
            Next
            AppendLog(_sb154.ToString())
        End If
        ' §5B-investigate: capture a, r boundary limbs BEFORE SafeMpzMul (r is cleared after).
        ' These are used post-mul to verify ar[0] = a[0]*r[0] mod 2^64 (exact) and that
        ' ar's top limbs are consistent with a[szA-1]*r[szR-1] (approximate, plus carries).
        ' If pre-mul a/r values look plausible but post-mul ar values are wildly off, the
        ' bug is in SafeMpzMul itself.  If pre-mul values are wrong, the bug is upstream
        ' (SafeMpzReciprocal for r; whoever produced a for a).
        Dim _5b_verify As Boolean = (szA = 175000001 AndAlso szR = 87500001)
        Dim _5b_aBot As ULong = 0UL, _5b_aTop As ULong = 0UL, _5b_aTop2 As ULong = 0UL
        Dim _5b_aMid As ULong = 0UL, _5b_aBot2 As ULong = 0UL
        Dim _5b_rBot As ULong = 0UL, _5b_rTop As ULong = 0UL, _5b_rTop2 As ULong = 0UL
        Dim _5b_rMid As ULong = 0UL, _5b_rBot2 As ULong = 0UL
        ' §5B-q-mid: capture ar at the kLimb+midIdx and kLimb+quartIdx positions BEFORE
        ' BigShiftRight, so post-shift we can verify q[mid] and q[quart] derive correctly
        ' from those ar limbs via q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem)).
        ' Mismatch ⇒ BigShiftRight middle bug.  Agreement ⇒ narrows bug to SafeMpzMul middle.
        Dim _5b_arMid0 As ULong = 0UL, _5b_arMid1 As ULong = 0UL
        Dim _5b_arQuart0 As ULong = 0UL, _5b_arQuart1 As ULong = 0UL
        ' §5B-f3: capture 100 evenly-spaced ar samples (and their +1 neighbours) pre-shift
        ' for post-shift verification of q[i] = (ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61).
        ' Any mismatch across the 100 samples ⇒ BigShiftRight has a bug at that index;
        ' all 100 matching ⇒ shift is faithful, and the bug must be in ar itself or upstream.
        Dim _f3_qIdx(99) As Long
        Dim _f3_arLo(99) As ULong
        Dim _f3_arHi(99) As ULong
        If _logLevel >= 2 AndAlso _5b_verify Then
            Dim _aD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _rD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            _5b_aBot = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, 0))
            _5b_aBot2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, 8))
            _5b_aMid = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA \ 2) * 8L)))
            _5b_aTop2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA - 2) * 8L)))
            _5b_aTop = CULng(Runtime.InteropServices.Marshal.ReadInt64(_aD5, CInt(CLng(szA - 1) * 8L)))
            _5b_rBot = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, 0))
            _5b_rBot2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, 8))
            _5b_rMid = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR \ 2) * 8L)))
            _5b_rTop2 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR - 2) * 8L)))
            _5b_rTop = CULng(Runtime.InteropServices.Marshal.ReadInt64(_rD5, CInt(CLng(szR - 1) * 8L)))
            AppendLog($"[SafeMpzDiv§5B-a] a[0]={_5b_aBot:X16} a[1]={_5b_aBot2:X16} a[mid={szA \ 2:N0}]={_5b_aMid:X16} a[szA-2]={_5b_aTop2:X16} a[szA-1]={_5b_aTop:X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-r] r[0]={_5b_rBot:X16} r[1]={_5b_rBot2:X16} r[mid={szR \ 2:N0}]={_5b_rMid:X16} r[szR-2]={_5b_rTop2:X16} r[szR-1]={_5b_rTop:X16}{vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §220 (issue #55, 2026-05-22): §166 force-serial LIFTED.
        ' Original §166 forced _safeMulDop=1 for a×r because §138/§165 only forced the
        ' outer Parallel.For while inner recursive SafeMpzMul calls bypassed the gate
        ' and ran parallel, producing wrong a×r values. Like §168, the root cause was
        ' Newton premature-convergence (now fixed by §200/§201) producing a wrong r;
        ' a×r was correctly computing (wrong_r × a). With §200/§201 in place, r is
        ' always correct so a×r is always correct regardless of DOP.
        ' Original lines preserved as comments for easy revert (grep §220).
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §166 lifted — a×r runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        ' §262 (#42): a×r is computed in full then `BigShiftRight(ar, ar, kBits)` throws away the low
        ' kLimb=kBits\64 limbs — so only the HIGH part is ever used (q = ar >> kBits).  Route it
        ' through the #70 chunked-grid HIGH product: keep only the top (fullLimbs − kLimb) result
        ' limbs (+GUARD), skipping the cells whose entire output lies below the cut.  The round-up
        ' overestimate ⇒ q overestimate ⇒ §171 adj-DOWN corrects (the §107 contract), so π stays
        ' bit-identical.  This attacks the dominant divide cost (a×r ≈ 5h40m vs q×b ≈ 1h34m at 5B)
        ' AND ~halves a×r's peak RAM (chunked accumulator vs the full §gen result+shifted+sub).
        ' Gated: flag + size (>1 cell) + DOP, and OFF under _5b_verify (those diagnostics read the
        ' low limbs, which a high product does not compute).
        Dim _arFull As Long = CLng(szA) + CLng(szR)
        Dim _arKLimb As Long = kBits \ 64L
        Dim _arKeep As Long = _arFull - _arKLimb
        If _divArShortMul AndAlso (Not _5b_verify) AndAlso _arKeep > 0L _
           AndAlso (CLng(szA) > 1500000L OrElse CLng(szR) > 1500000L) _
           AndAlso MemBudget_SuggestSafeMulDop(CLng(szA), CLng(szR)) <= _recipShortMulMaxDop Then
            AppendLog($"[SafeMpzDiv§262-gate] a×r HIGH ENGAGE szA={szA:N0} szR={szR:N0} fullLimbs={_arFull:N0} kLimb={_arKLimb:N0} keep={_arKeep:N0}{vbCrLf}", 2)
            SafeMpzMul_ChunkedGrid(ar, a, r, _arKeep)
        Else
            SafeMpzMul(ar, a, r)
        End If
        ' §5B-f1: r's data buffer is needed by the §5B-f1 chunked-grid reference (below).
        ' Defer mpz_clear(r) until AFTER §5B-f1 completes to keep r alive.  Before §5B-f1
        ' was added the clear lived here directly; now it's at the end of the §5B-f1 block.
        Dim szAR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))

        ' §213 (2026-05-15, issue #66): when _5b_verify is False (which is ALL 5B-scale calls
        ' — _5b_verify is gated to szA=175,000,001 AndAlso szR=87,500,001, the 1B sqrt-step-4
        ' shape), §5B-f1 and §5B-f2 below are skipped entirely, so r is dead from this point
        ' on.  Free it now to drop ~2 GB of working set during the dangerous depth-0 §gen
        ' window that follows.  The deferred clear at the end of the §5B-f1/§5B-f2 block
        ' becomes conditional on _5b_verify so 1B-scale runs still defer.
        If Not _5b_verify Then
            gmp_lib.mpz_clear(r)
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§213] r cleared eagerly (_5b_verify=False, ~{CLng(szR) * 8L \ BYTES_PER_MB:N0} MB freed){vbCrLf}")
        End If

        ' §241 (issue #69): trim pooled FFT temporaries left over from a×r before q×b
        ' allocates fresh. Measures pool retention at this boundary (census-before).
        TrimPoolAtBoundary("post-axr", CULng(BYTES_PER_MB))

        ' §5B-investigate (post-mul): verify ar boundary limbs against pre-mul a, r values.
        ' Bottom: ar[0] = (a[0]*r[0]) mod 2^64 — EXACT relation, mismatch ⇒ SafeMpzMul bug.
        ' Top: ar[szAR-1] should be ≈ high(a[szA-1]*r[szR-1]) plus accumulated carry from cross
        ' products; off-by-many-orders-of-magnitude indicates SafeMpzMul produced wrong top.
        If _logLevel >= 2 AndAlso _5b_verify Then
            Dim _arD5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _arBot5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, 0))
            Dim _arBot5_2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, 8))
            Dim _arMid5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR \ 2) * 8L)))
            Dim _arTop5_2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR - 2) * 8L)))
            Dim _arTop5 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(CLng(szAR - 1) * 8L)))
            Dim _expArBot As ULong = _5b_aBot * _5b_rBot
            Dim _expArTopLow As ULong = 0UL
            Dim _expArTopHigh As ULong = System.Math.BigMul(_5b_aTop, _5b_rTop, _expArTopLow)
            AppendLog($"[SafeMpzDiv§5B-ar] ar[0]={_arBot5:X16} ar[1]={_arBot5_2:X16} ar[mid={szAR \ 2:N0}]={_arMid5:X16} ar[szAR-2]={_arTop5_2:X16} ar[szAR-1]={_arTop5:X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-arBot] actual ar[0]={_arBot5:X16}  expected (a[0]*r[0])_lo={_expArBot:X16}  match={(_arBot5 = _expArBot)}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-arTop] actual ar[szAR-1..szAR-2]=[{_arTop5:X16} {_arTop5_2:X16}]  a[top]*r[top]=[hi={_expArTopHigh:X16} lo={_expArTopLow:X16}]  (top should be ≈ hi+carry; lo+carry should be near {_arTop5_2:X16}){vbCrLf}", 5)   ' §252 (#95)
            ' §5B-q-mid: capture ar at the q-mid and q-quart shift positions for post-shift verification.
            ' kBits=11,200,000,067 ⇒ kLimb=175,000,001, kRem=3.  q has szQ=87,500,001 limbs.
            ' q[mid=43,750,000] derives from ar[218,750,001] and ar[218,750,002].
            ' q[quart=21,875,000] derives from ar[196,875,001] and ar[196,875,002].
            Dim _5bKLimb As Long = kBits \ 64L
            Dim _5bMidArIdx As Long = _5bKLimb + 43750000L
            Dim _5bQuartArIdx As Long = _5bKLimb + 21875000L
            _5b_arMid0 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_5bMidArIdx * 8L)))
            _5b_arMid1 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt((_5bMidArIdx + 1L) * 8L)))
            _5b_arQuart0 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_5bQuartArIdx * 8L)))
            _5b_arQuart1 = CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt((_5bQuartArIdx + 1L) * 8L)))
            AppendLog($"[SafeMpzDiv§5B-arQ-src] ar[{_5bQuartArIdx:N0}]={_5b_arQuart0:X16} ar[{_5bQuartArIdx+1:N0}]={_5b_arQuart1:X16} ar[{_5bMidArIdx:N0}]={_5b_arMid0:X16} ar[{_5bMidArIdx+1:N0}]={_5b_arMid1:X16}{vbCrLf}", 5)   ' §252 (#95)
            ' §5B-f3 capture: 100 evenly-spaced ar samples covering q's full range [0..szQ-1].
            ' szQ = 87,500,001, kLimb = 175,000,001.  Sample positions evenly across q indices.
            For _f3s As Integer = 0 To 99
                Dim _f3_qi As Long = CLng(_f3s) * 87499999L \ 99L  ' 0, 884K, 1.77M, ..., 87.5M-1
                _f3_qIdx(_f3s) = _f3_qi
                Dim _f3_arIdxLo As Long = _5bKLimb + _f3_qi
                Dim _f3_arIdxHi As Long = _5bKLimb + _f3_qi + 1L
                _f3_arLo(_f3s) = If(_f3_arIdxLo >= 0L AndAlso _f3_arIdxLo < CLng(szAR), CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_f3_arIdxLo * 8L))), 0UL)
                _f3_arHi(_f3s) = If(_f3_arIdxHi >= 0L AndAlso _f3_arIdxHi < CLng(szAR), CULng(Runtime.InteropServices.Marshal.ReadInt64(_arD5, CInt(_f3_arIdxHi * 8L))), 0UL)
            Next
            AppendLog($"[SafeMpzDiv§5B-f3] captured 100 ar samples for post-shift verification (kLimb={_5bKLimb:N0} kRem=3){vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §5B-f1 (DONE — run 14 confirmed ar = a × r is fully correct via 1000
        ' evenly-spaced position scans, mismatches=0).  Disabled to save ~80 min.
        ' Re-enable by flipping _F1_ENABLED to True if the diagnostic needs to re-run.
        Const _F1_ENABLED As Boolean = False
        ' §5B-f1: Chunked-grid independent reference for the FULL a × r product.
        ' Computes the 262.5M-limb result via a 117 × 59 = 6,903-cell grid of
        ' ≤ 1.5M × 1.5M (≤ 3M total per cell — reliable mpz_mul per §160), then
        ' scans our SafeMpzMul ar against the reference at 1,000 evenly-spaced
        ' positions.  Mirrors the §5B-e pattern but for the full a × r product.
        '
        ' Outcome:
        '   Mismatches > 0 ⇒ ar is wrong at those positions; bug is in §gen
        '                    accumulation (mul_2exp shift / GmpRaw_add carry).
        '   Mismatches = 0 ⇒ ar is fully correct; the §171 trigger must be
        '                    driven by something else (kBits computation off,
        '                    BigShiftRight wrong at a position F-3 missed, or
        '                    the §171 trigger logic itself misjudges Barrett).
        '
        ' Pre-allocates _refAcc and _ckShifted buffers via VirtualAlloc to
        ' 270M limbs (~2.16 GB each) — _mp_alloc set to full pre-allocated
        ' size so mul_2exp and add never trigger realloc (same pattern as §gen).
        If _logLevel >= 2 AndAlso _5b_verify AndAlso _F1_ENABLED Then
            Const _F1_CHUNK As Integer = 1500000
            Const _F1_MAX_LIMBS As Integer = 270_000_000
            Dim _F1_MAX_BYTES As Long = CLng(_F1_MAX_LIMBS) * 8L
            Dim _F1_aD As Long = Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8)
            Dim _F1_aSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(a.Pointer, 4))
            Dim _F1_rD As Long = Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8)
            Dim _F1_rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            AppendLog($"[SafeMpzDiv§5B-f1] starting chunked-grid a × r reference (chunk={_F1_CHUNK:N0}, prealloc={_F1_MAX_LIMBS:N0} limbs/buf, {_F1_MAX_BYTES \ BYTES_PER_MB:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f1] a sz={_F1_aSz:N0} r sz={_F1_rSz:N0}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F1_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F1_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F1_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F1_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F1_eAccBuf = IntPtr.Zero OrElse _F1_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f1] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F1_eAccBuf <> IntPtr.Zero Then VirtualFree(_F1_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F1_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F1_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F1_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_refAcc)
                Dim _F1_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F1_refAcc, 0))
                Dim _F1_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F1_ra_initPtr, _F1_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_refAcc, 0, _F1_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F1_refAcc, 8, _F1_eAccBuf.ToInt64())
                Dim _F1_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_ckShifted)
                Dim _F1_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F1_ckShifted, 0))
                Dim _F1_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F1_cs_initPtr, _F1_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 0, _F1_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F1_ckShifted, 8, _F1_eShiftBuf.ToInt64())
                Dim _F1_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F1_ckPartial)
                Dim _F1_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F1_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F1_numCkA As Integer = (_F1_aSz + _F1_CHUNK - 1) \ _F1_CHUNK
                Dim _F1_numCkB As Integer = (_F1_rSz + _F1_CHUNK - 1) \ _F1_CHUNK
                Dim _F1_ckCount As Integer = 0
                For i As Integer = 0 To _F1_numCkA - 1
                    Dim _F1_aOff As Long = CLng(i) * CLng(_F1_CHUNK)
                    Dim _F1_aSzCk As Integer = CInt(System.Math.Min(CLng(_F1_CHUNK), CLng(_F1_aSz) - _F1_aOff))
                    If _F1_aSzCk <= 0 Then Continue For
                    While _F1_aSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F1_aD + (_F1_aOff + CLng(_F1_aSzCk - 1)) * 8L)) = 0L
                        _F1_aSzCk -= 1
                    End While
                    If _F1_aSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F1_ckA, 0, _F1_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F1_ckA, 4, _F1_aSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F1_ckA, 8, _F1_aD + _F1_aOff * 8L)
                    For j As Integer = 0 To _F1_numCkB - 1
                        Dim _F1_bOff As Long = CLng(j) * CLng(_F1_CHUNK)
                        Dim _F1_bSzCk As Integer = CInt(System.Math.Min(CLng(_F1_CHUNK), CLng(_F1_rSz) - _F1_bOff))
                        If _F1_bSzCk <= 0 Then Continue For
                        While _F1_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F1_rD + (_F1_bOff + CLng(_F1_bSzCk - 1)) * 8L)) = 0L
                            _F1_bSzCk -= 1
                        End While
                        If _F1_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F1_ckB, 0, _F1_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F1_ckB, 4, _F1_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F1_ckB, 8, _F1_rD + _F1_bOff * 8L)
                        GmpRaw_mul(_F1_ckPartial, _F1_ckA, _F1_ckB)
                        Dim _F1_shiftBits As ULong = CULng(_F1_aOff + _F1_bOff) * 64UL
                        If _F1_shiftBits = 0UL Then
                            GmpRaw_add(_F1_refAcc, _F1_refAcc, _F1_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F1_ckShifted, 4, 0)
                            Dim _F1_shiftSrc As IntPtr = _F1_ckPartial
                            Dim _F1_shiftRem As ULong = _F1_shiftBits
                            While _F1_shiftRem > 0UL
                                Dim _F1_chunkBits As UInteger = CUInt(System.Math.Min(_F1_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F1_ckShifted, _F1_shiftSrc, _F1_chunkBits)
                                _F1_shiftSrc = _F1_ckShifted
                                _F1_shiftRem -= CULng(_F1_chunkBits)
                            End While
                            GmpRaw_add(_F1_refAcc, _F1_refAcc, _F1_ckShifted)
                        End If
                        _F1_ckCount += 1
                    Next j
                Next i
                Dim _F1_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F1_refAcc, 4))
                Dim _F1_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F1_refAcc, 8))
                Dim _F1_arDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                AppendLog($"[SafeMpzDiv§5B-f1] reference complete: subProducts={_F1_ckCount:N0} refSz={_F1_refSz:N0} ourArSz={szAR:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Scan ar against reference at 1,000 evenly-spaced positions across the full range.
                Const _F1_NUM_SAMPLES As Integer = 1000
                Dim _F1_mismatchCount As Integer = 0
                Dim _F1_firstMismatchIdx As Long = -1L
                Dim _F1_logCount As Integer = 0
                Dim _F1_maxIdx As Long = CLng(System.Math.Min(_F1_refSz, szAR)) - 1L
                For _F1s As Integer = 0 To _F1_NUM_SAMPLES - 1
                    Dim _F1_idx As Long = If(_F1_NUM_SAMPLES > 1, CLng(_F1s) * _F1_maxIdx \ CLng(_F1_NUM_SAMPLES - 1), 0L)
                    Dim _F1_refV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F1_refDPtr, CInt(_F1_idx * 8L)))
                    Dim _F1_arV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F1_arDPtr, CInt(_F1_idx * 8L)))
                    If _F1_refV <> _F1_arV Then
                        _F1_mismatchCount += 1
                        If _F1_firstMismatchIdx = -1L Then _F1_firstMismatchIdx = _F1_idx
                        If _F1_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f1 MISMATCH] sample={_F1s} ar[{_F1_idx:N0}] reference={_F1_refV:X16} ourSafeMpzMul={_F1_arV:X16}{vbCrLf}", 5)   ' §252 (#95)
                            _F1_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f1 SUMMARY] scanned {_F1_NUM_SAMPLES} ar positions across [0..{_F1_maxIdx:N0}], mismatches={_F1_mismatchCount}, firstMismatchArIdx={_F1_firstMismatchIdx}{vbCrLf}", 5)   ' §252 (#95)
                ' Cleanup — _refAcc and _ckShifted have swapped-in VirtualAlloc'd buffers.
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckShifted)
                GmpRaw_clear(_F1_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F1_ckB)
                VirtualFree(_F1_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F1_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If
        ' §5B-f2: Verify r is a true reciprocal by computing r × b via chunked-grid
        ' and checking the result lies in [2^kBits - b, 2^kBits).  If r is correct,
        ' the top limb (r*b)[kLimb] should be in [0, 2^kRem-1] and limbs above kLimb
        ' should be zero.  If r is short (Newton converged early), the result will
        ' be MUCH smaller — fewer total limbs, with high-zone limbs missing entirely.
        ' Grid: 59 × 59 = 3,481 sub-products at ≤ 1.5M × 1.5M (≤ 3M total).
        ' Result: 175M limbs ≈ 1.4 GB.  Pre-alloc 180M-limb buffers ≈ 1.44 GB each.
        ' Estimated time: ~40 min (fewer sub-products than F-1, smaller buffers).
        ' Re-enabled for Option H: F-2's original check was TOO WEAK (only verified top
        ' ~130 bits of r×b).  Extended logging at multiple intermediate positions to
        ' discriminate "r correct" from "r short by 2^5.45B".  See Option H discussion
        ' in README §5B-investigate.
        Const _F2_ENABLED As Boolean = True
        If _logLevel >= 2 AndAlso _5b_verify AndAlso _F2_ENABLED Then
            Const _F2_CHUNK As Integer = 1500000
            Const _F2_MAX_LIMBS As Integer = 180_000_000
            Dim _F2_MAX_BYTES As Long = CLng(_F2_MAX_LIMBS) * 8L
            Dim _F2_rD As Long = Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8)
            Dim _F2_rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            Dim _F2_bD As Long = Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8)
            Dim _F2_bSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
            Dim _F2_kLimb As Long = kBits \ 64L
            Dim _F2_kRem As Integer = CInt(kBits Mod 64L)
            AppendLog($"[SafeMpzDiv§5B-f2] starting r×b chunked-grid reference (chunk={_F2_CHUNK:N0}, prealloc={_F2_MAX_LIMBS:N0} limbs/buf, {_F2_MAX_BYTES \ BYTES_PER_MB:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f2] r sz={_F2_rSz:N0} b sz={_F2_bSz:N0} kBits={kBits:N0} kLimb={_F2_kLimb:N0} kRem={_F2_kRem}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F2_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F2_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F2_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F2_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F2_eAccBuf = IntPtr.Zero OrElse _F2_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f2] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F2_eAccBuf <> IntPtr.Zero Then VirtualFree(_F2_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F2_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F2_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F2_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_refAcc)
                Dim _F2_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F2_refAcc, 0))
                Dim _F2_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F2_ra_initPtr, _F2_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_refAcc, 0, _F2_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F2_refAcc, 8, _F2_eAccBuf.ToInt64())
                Dim _F2_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_ckShifted)
                Dim _F2_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F2_ckShifted, 0))
                Dim _F2_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F2_cs_initPtr, _F2_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 0, _F2_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F2_ckShifted, 8, _F2_eShiftBuf.ToInt64())
                Dim _F2_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F2_ckPartial)
                Dim _F2_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F2_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F2_numCkR As Integer = (_F2_rSz + _F2_CHUNK - 1) \ _F2_CHUNK
                Dim _F2_numCkB As Integer = (_F2_bSz + _F2_CHUNK - 1) \ _F2_CHUNK
                Dim _F2_ckCount As Integer = 0
                For i As Integer = 0 To _F2_numCkR - 1
                    Dim _F2_rOff As Long = CLng(i) * CLng(_F2_CHUNK)
                    Dim _F2_rSzCk As Integer = CInt(System.Math.Min(CLng(_F2_CHUNK), CLng(_F2_rSz) - _F2_rOff))
                    If _F2_rSzCk <= 0 Then Continue For
                    While _F2_rSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F2_rD + (_F2_rOff + CLng(_F2_rSzCk - 1)) * 8L)) = 0L
                        _F2_rSzCk -= 1
                    End While
                    If _F2_rSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F2_ckA, 0, _F2_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F2_ckA, 4, _F2_rSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F2_ckA, 8, _F2_rD + _F2_rOff * 8L)
                    For j As Integer = 0 To _F2_numCkB - 1
                        Dim _F2_bOff As Long = CLng(j) * CLng(_F2_CHUNK)
                        Dim _F2_bSzCk As Integer = CInt(System.Math.Min(CLng(_F2_CHUNK), CLng(_F2_bSz) - _F2_bOff))
                        If _F2_bSzCk <= 0 Then Continue For
                        While _F2_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F2_bD + (_F2_bOff + CLng(_F2_bSzCk - 1)) * 8L)) = 0L
                            _F2_bSzCk -= 1
                        End While
                        If _F2_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F2_ckB, 0, _F2_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F2_ckB, 4, _F2_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F2_ckB, 8, _F2_bD + _F2_bOff * 8L)
                        GmpRaw_mul(_F2_ckPartial, _F2_ckA, _F2_ckB)
                        Dim _F2_shiftBits As ULong = CULng(_F2_rOff + _F2_bOff) * 64UL
                        If _F2_shiftBits = 0UL Then
                            GmpRaw_add(_F2_refAcc, _F2_refAcc, _F2_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F2_ckShifted, 4, 0)
                            Dim _F2_shiftSrc As IntPtr = _F2_ckPartial
                            Dim _F2_shiftRem As ULong = _F2_shiftBits
                            While _F2_shiftRem > 0UL
                                Dim _F2_chunkBits As UInteger = CUInt(System.Math.Min(_F2_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F2_ckShifted, _F2_shiftSrc, _F2_chunkBits)
                                _F2_shiftSrc = _F2_ckShifted
                                _F2_shiftRem -= CULng(_F2_chunkBits)
                            End While
                            GmpRaw_add(_F2_refAcc, _F2_refAcc, _F2_ckShifted)
                        End If
                        _F2_ckCount += 1
                    Next j
                Next i
                Dim _F2_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F2_refAcc, 4))
                Dim _F2_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F2_refAcc, 8))
                AppendLog($"[SafeMpzDiv§5B-f2] r×b reference complete: subProducts={_F2_ckCount:N0} refSz={_F2_refSz:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Inspect (r×b) at and around kLimb to assess r's correctness.
                ' If r is exact: refSz should be kLimb or kLimb+1 (with limb kLimb in [0, 2^kRem)).
                ' If r is short: refSz < kLimb (high zone is empty), or limb kLimb-1 is much smaller than 2^64.
                ' If r is large: refSz > kLimb+1, or limb kLimb has bits set above bit kRem.
                Dim _f2GetLimb = Function(_idx As Long) As ULong
                                     If _idx >= 0L AndAlso _idx < CLng(_F2_refSz) Then
                                         Return CULng(Runtime.InteropServices.Marshal.ReadInt64(_F2_refDPtr, CInt(_idx * 8L)))
                                     End If
                                     Return 0UL
                                 End Function
                Dim _F2_v_kL As ULong = _f2GetLimb(_F2_kLimb)
                Dim _F2_v_kLm1 As ULong = _f2GetLimb(_F2_kLimb - 1L)
                Dim _F2_v_kLm2 As ULong = _f2GetLimb(_F2_kLimb - 2L)
                Dim _F2_v_kLp1 As ULong = _f2GetLimb(_F2_kLimb + 1L)
                Dim _F2_v_kLp2 As ULong = _f2GetLimb(_F2_kLimb + 2L)
                Dim _F2_v_top As ULong = _f2GetLimb(CLng(_F2_refSz) - 1L)
                Dim _F2_v_top2 As ULong = _f2GetLimb(CLng(_F2_refSz) - 2L)
                Dim _F2_v_bot As ULong = _f2GetLimb(0L)
                Dim _F2_v_bot1 As ULong = _f2GetLimb(1L)
                Dim _F2_kBitsBoundary As ULong = If(_F2_kRem >= 64, 0UL, CULng(1L << _F2_kRem) - 1UL)
                Dim _F2_v_kL_aboveKRem As ULong = _F2_v_kL >> _F2_kRem
                Dim _F2_v_kL_inRange As Boolean = (_F2_v_kL <= _F2_kBitsBoundary)
                Dim _F2_aboveKLimbAllZero As Boolean = (_F2_v_kLp1 = 0UL AndAlso _F2_v_kLp2 = 0UL)
                AppendLog($"[SafeMpzDiv§5B-f2 inspect] r×b[0]={_F2_v_bot:X16} r×b[1]={_F2_v_bot1:X16} r×b[kLimb-2]={_F2_v_kLm2:X16} r×b[kLimb-1]={_F2_v_kLm1:X16} r×b[kLimb]={_F2_v_kL:X16} r×b[kLimb+1]={_F2_v_kLp1:X16} r×b[kLimb+2]={_F2_v_kLp2:X16} r×b[top-1]={_F2_v_top2:X16} r×b[top]={_F2_v_top:X16}{vbCrLf}", 5)   ' §252 (#95)
                AppendLog($"[SafeMpzDiv§5B-f2 verdict] refSz={_F2_refSz:N0} kLimb={_F2_kLimb:N0} kRem={_F2_kRem} kRemMask={_F2_kBitsBoundary:X16} r×b[kLimb]>>kRem={_F2_v_kL_aboveKRem:X16} (should be 0 if r×b<2^kBits) inKBitsRange={_F2_v_kL_inRange} aboveKLimbAllZero={_F2_aboveKLimbAllZero}{vbCrLf}", 5)   ' §252 (#95)
                ' Option H: extended r×b logging at intermediate positions to discriminate
                ' "r correct" (FF block extends ~87.5M limbs from kLimb-1 down) from
                ' "r short by 2^5.45B" (FF block extends only ~3M limbs).
                ' Distances from kLimb (descending): 3M, 5M, 10M, 50M, 87M, 90M, 130M.
                ' For correct r: all of 3M..87M should be 0xFFFFFFFFFFFFFFFF; 90M, 130M
                ' may differ (within δ region).
                ' For r short by 2^5.45B: only 3M is in FF block; 5M..130M all NOT FF.
                Dim _F2_offsets As Long() = New Long() {3000000L, 5000000L, 10000000L, 50000000L, 87000000L, 90000000L, 130000000L}
                Dim _F2_ffBoundary As Long = -1L  ' first non-FF position from top going down
                For Each _F2_off As Long In _F2_offsets
                    Dim _F2_idx As Long = _F2_kLimb - _F2_off
                    Dim _F2_v As ULong = _f2GetLimb(_F2_idx)
                    Dim _F2_isFF As Boolean = (_F2_v = &HFFFFFFFFFFFFFFFFUL)
                    AppendLog($"[SafeMpzDiv§5B-f2 H] r×b[kLimb-{_F2_off:N0}={_F2_idx:N0}]={_F2_v:X16} isFF={_F2_isFF}{vbCrLf}", 5)   ' §252 (#95)
                    If Not _F2_isFF AndAlso _F2_ffBoundary = -1L Then _F2_ffBoundary = _F2_off
                Next
                If _F2_ffBoundary = -1L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] All checked positions are 0xFF...FF: FF block extends >130M limbs → r is essentially correct.{vbCrLf}", 5)   ' §252 (#95)
                ElseIf _F2_ffBoundary < 5000000L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends within {_F2_ffBoundary:N0} limbs of top → r SHORT BY ≥ ~2^5.45B (Newton precision failure).{vbCrLf}", 5)   ' §252 (#95)
                ElseIf _F2_ffBoundary < 87000000L Then
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends within {_F2_ffBoundary:N0} limbs of top → r SHORT BY some amount; investigate Newton precision.{vbCrLf}", 5)   ' §252 (#95)
                Else
                    AppendLog($"[SafeMpzDiv§5B-f2 H verdict] FF block ends at {_F2_ffBoundary:N0} limbs (at/past expected ≈87.5M boundary) → r appears correct; bug is elsewhere.{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' Cleanup
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckShifted)
                GmpRaw_clear(_F2_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F2_ckB)
                VirtualFree(_F2_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F2_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If

        ' §5B-f1/f2 (deferred from §166 site): now that the chunked-grid references are done,
        ' release r.  When both diagnostics are gated off, this clears r at exactly the same
        ' logical point as the original code.
        ' §213 (2026-05-15, issue #66): now conditional — when _5b_verify=False the eager
        ' clear above already fired.  Only the 1B sqrt-step-4 path (_5b_verify=True) reaches
        ' the deferred clear here.
        If _5b_verify Then gmp_lib.mpz_clear(r)
        ' §135 save slots: ar[64654664/65] captured inside §111 block, used after BigShiftRight.
        Dim _ar135_v0 As Long = 0L
        Dim _ar135_v1 As Long = 0L
        ' §141 save slots: ar[65139832/33] → expected q[21389832] (A2/B2 midpoint).
        Dim _ar141_v0 As Long = 0L
        Dim _ar141_v1 As Long = 0L
        ' §142: A2-range q verification — save ar pairs for q[14583334+j] at j=0,1M..6M.
        ' ar index = kLimb + q_index = 43750000 + (14583334 + j) = 58333334 + j.
        ' Evenly-spaced j: 0, 1000000, 2000000, 3000000, 4000000, 5000000, 6000000.
        Dim _ar142_pairs(6, 1) As Long  ' (7 points) × (v0, v1)
        Dim _ar142_qIdx() As Long = {14583334L, 15583334L, 16583334L, 17583334L, 18583334L, 19583334L, 20583334L}
        ' §149: Top q-range — save ar pairs for q[k] at k in [20950000..21875000].
        ' These q limbs (q[20904664..21875000]) are the inputs to A2×B2[13612996] in q×b,
        ' and were NOT verified by §135/§141/§142.  A mismatch here pinpoints the bug.
        ' ar index = 43750000 + k.  Last valid ar index = 65625000 (szAR=65625001).
        Dim _ar149_pairs(10, 1) As Long  ' (11 points) × (v0, v1)
        Dim _ar149_qIdx() As Long = {20950000L, 21000000L, 21100000L, 21200000L, 21300000L, 21500000L, 21600000L, 21700000L, 21800000L, 21874999L, 21875000L}
        ' §151: Gap q-range [20583334..20950000] — unverified by §142/§149, all contribute to A2×B2[13612996].
        Dim _ar151_pairs(3, 1) As Long  ' (4 points) × (v0, v1)
        Dim _ar151_qIdx() As Long = {20600000L, 20700000L, 20800000L, 20900000L}
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szAR can be 1.26B at 5B).
            Dim _arDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _arTop As Long = If(szAR >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (CLng(szAR) - 1L) * 8L), 0), 0L)
            Dim _arTop2 As Long = If(szAR >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (CLng(szAR) - 2L) * 8L), 0), 0L)
            ' Boundary limbs: at index kBits\64 and kBits\64+1. q_bot = (bnd0 >> kBits%64) | (bnd1 << (64 - kBits%64))
            Dim _kLimb As Long = kBits \ 64L
            Dim _kRem As Integer = CInt(kBits Mod 64L)
            ' §239 (2026-05-31, issue #71 residual): 64-bit-safe absolute-address read.
            ' szAR ≈ 1.26B at 5B, so _kLimb (= kBits\64 ≈ 998M) × 8 = 7.99 GB overflows
            ' Int32; with overflow checks off CInt wraps to a NEGATIVE offset → Marshal
            ' reads ~601 MB before the buffer → AccessViolation. The §215 fix at line 5066
            ' fixed _arTop but missed these two boundary reads. Same pattern as §237/c7a0c76.
            Dim _arBnd0 As Long = If(_kLimb >= 0L AndAlso _kLimb < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + _kLimb * 8L), 0), 0L)
            Dim _arBnd1 As Long = If(_kLimb + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_arDPtr.ToInt64() + (_kLimb + 1L) * 8L), 0), 0L)
            Dim _arBot As Long = If(szAR >= 1, Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, 0), 0L)
            Dim _qBotExpected As Long = CLng(CULng(_arBnd0) >> _kRem) Or CLng(CULng(_arBnd1) << (64 - _kRem))
            AppendLog($"[SafeMpzDiv] ar pre-shift: szAR={szAR:N0} top2=[{_arTop:X16} {_arTop2:X16}] bot=[{_arBot:X16}] bnd=[{_arBnd0:X16} {_arBnd1:X16}] q_bot_expected={_qBotExpected:X16}{vbCrLf}")
            ' §111/§112: log error-limb and sparse sweep of ar[43750002..64654663].
            If szAR = 65625001 Then
                Const _ARD As Long = 64654664L
                Dim _arErrL As Long = If(_ARD < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ARD * 8L)), 0L)
                Dim _arErrL1 As Long = If(_ARD + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ARD + 1L) * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§111] ar[{_ARD:N0}]={_arErrL:X16} ar[{_ARD+1:N0}]={_arErrL1:X16}{vbCrLf}")
                _ar135_v0 = _arErrL   ' §135: save for post-BigShiftRight verification
                _ar135_v1 = _arErrL1
                ' §112: sparse sweep across the middle zone to find where ar goes wrong.
                ' Also includes 65139832/33 which correspond to q[21389832] (A2/B2 midpoint check).
                Dim _sweepPositions() As Long = {43750002L, 45000000L, 47000000L, 50000000L, 52000000L, 55000000L, 57000000L, 58333334L, 58333335L, 60000000L, 62000000L, 64000000L, 64654663L, 65139832L, 65139833L}
                Dim _sweepSb As New System.Text.StringBuilder()
                _sweepSb.Append($"[SafeMpzDiv§112] ar sparse sweep (szAR={szAR:N0}):{vbCrLf}")
                For Each _sp As Long In _sweepPositions
                    Dim _sv As Long = If(_sp < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_sp * 8L)), 0L)
                    _sweepSb.Append($"  ar[{_sp:N0}]={_sv:X16}{vbCrLf}")
                Next
                AppendLog(_sweepSb.ToString())
                ' Save ar[65139832/33] for §141 verification of q[21389832] after BigShiftRight.
                _ar141_v0 = If(65139832L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(65139832L * 8L)), 0L)
                _ar141_v1 = If(65139833L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(65139833L * 8L)), 0L)
                ' §149: save ar pairs for top q-range verification.
                For _i149 As Integer = 0 To 10
                    Dim _ar149_base As Long = 43750000L + _ar149_qIdx(_i149)
                    _ar149_pairs(_i149, 0) = If(_ar149_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar149_base * 8L)), 0L)
                    _ar149_pairs(_i149, 1) = If(_ar149_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar149_base + 1L) * 8L)), 0L)
                Next
                ' §151: save ar pairs for gap q-range [20583334..20950000] verification.
                For _i151 As Integer = 0 To 3
                    Dim _ar151_base As Long = 43750000L + _ar151_qIdx(_i151)
                    _ar151_pairs(_i151, 0) = If(_ar151_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar151_base * 8L)), 0L)
                    _ar151_pairs(_i151, 1) = If(_ar151_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar151_base + 1L) * 8L)), 0L)
                Next
                ' §142: save ar pairs for A2-range q verification.
                For _i142 As Integer = 0 To 6
                    Dim _ar142_base As Long = 43750000L + _ar142_qIdx(_i142)  ' = 58333334 + j
                    _ar142_pairs(_i142, 0) = If(_ar142_base < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_ar142_base * 8L)), 0L)
                    _ar142_pairs(_i142, 1) = If(_ar142_base + 1L < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt((_ar142_base + 1L) * 8L)), 0L)
                Next
                ' §132: verify q[14583334] from ar — q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem))
                ' kLimb=43750000, kRem=27 → q[14583334] = (ar[58333334] >> 27) | (ar[58333335] << 37)
                Const _Q132_IDX As Long = 14583334L
                Const _AR132_0 As Long = 58333334L   ' kLimb + Q132_IDX
                Const _AR132_1 As Long = 58333335L
                Dim _ar132_v0 As Long = If(_AR132_0 < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_AR132_0 * 8L)), 0L)
                Dim _ar132_v1 As Long = If(_AR132_1 < CLng(szAR), Runtime.InteropServices.Marshal.ReadInt64(_arDPtr, CInt(_AR132_1 * 8L)), 0L)
                Dim _q132_expected As Long = CLng(CULng(_ar132_v0) >> 27) Or CLng(CULng(_ar132_v1) << 37)
                AppendLog($"[SafeMpzDiv§132] ar[{_AR132_0:N0}]={_ar132_v0:X16} ar[{_AR132_1:N0}]={_ar132_v1:X16} → expected q[{_Q132_IDX:N0}]={_q132_expected:X16}{vbCrLf}")
            End If
        End If
        AppendLog($"[SafeMpzDiv] a*r done: szAR={szAR:N0}; shifting right by kBits={kBits:N0}...{vbCrLf}")
        BigShiftRight(ar, ar, kBits)
        ' §171-ckpt: szQ assignment lifted (Dim moved to top of SafeMpzDiv).
        szQ = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(ar.Pointer, 4))
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szQ ≈ 259M at 5B — just under Int32 threshold).
            Dim _qDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
            Dim _qTop As Long = If(szQ >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qDPtr.ToInt64() + (CLng(szQ) - 1L) * 8L), 0), 0L)
            Dim _qTop2 As Long = If(szQ >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_qDPtr.ToInt64() + (CLng(szQ) - 2L) * 8L), 0), 0L)
            Dim _qBot As Long = If(szQ >= 1, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 0), 0L)
            Dim _qBot2 As Long = If(szQ >= 2, Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 8), 0L)
            AppendLog($"[SafeMpzDiv] q_approx ready: szQ={szQ:N0} top2limbs=[{_qTop:X16} {_qTop2:X16}] bot2limbs=[{_qBot:X16} {_qBot2:X16}]{vbCrLf}")
            ' §5B-investigate (post-shift): log q at multiple positions and verify BigShiftRight
            ' at the boundary.  q[i] = (ar[kLimb+i] >> kRem) | (ar[kLimb+i+1] << (64-kRem)).
            ' Mismatch between this expected value (computed from saved ar limbs) and actual q[i]
            ' would indicate BigShiftRight produced wrong output; agreement narrows the bug to
            ' SafeMpzMul.  At kBits=11,200,000,067: kLimb=175,000,001, kRem=3.  Top valid q
            ' index is szQ-1 = 87,500,000, which corresponds to ar[262,500,001] = ar[szAR-1].
            If szQ = 87500001 Then
                Dim _qMid5 As Long = If(CLng(szQ \ 2) < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(CLng(szQ \ 2) * 8L)), 0L)
                Dim _qBotPos5 As Long = If(1L < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, 8), 0L)
                Dim _qQuart5 As Long = Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(21875000L * 8L))
                AppendLog($"[SafeMpzDiv§5B-q] q[0]={_qBot:X16} q[1]={_qBotPos5:X16} q[quart=21,875,000]={_qQuart5:X16} q[mid={szQ \ 2:N0}]={_qMid5:X16} q[szQ-2]={_qTop2:X16} q[szQ-1]={_qTop:X16}{vbCrLf}", 5)   ' §252 (#95)
                ' §5B-q-mid: verify q[mid] and q[quart] derive correctly from saved ar limbs.
                ' kRem=3, so q[i] = (ar[kLimb+i] >> 3) | (ar[kLimb+i+1] << 61).
                ' If actual ≠ expected ⇒ BigShiftRight has a 5B middle-limb bug.
                ' If actual = expected ⇒ BigShiftRight is faithful at these positions, narrowing
                ' the bug to SafeMpzMul middle limbs.
                Dim _kLimbQ5 As Long = kBits \ 64L
                Dim _expQMid As ULong = (_5b_arMid0 >> 3) Or (_5b_arMid1 << 61)
                Dim _expQQuart As ULong = (_5b_arQuart0 >> 3) Or (_5b_arQuart1 << 61)
                Dim _actQMidU As ULong = CULng(_qMid5)
                Dim _actQQuartU As ULong = CULng(_qQuart5)
                AppendLog($"[SafeMpzDiv§5B-q-quart] actual q[21,875,000]={_actQQuartU:X16}  expected (ar[{21875000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQQuart:X16}  match={(_actQQuartU = _expQQuart)}{vbCrLf}", 5)   ' §252 (#95)
                AppendLog($"[SafeMpzDiv§5B-q-mid]   actual q[43,750,000]={_actQMidU:X16}  expected (ar[{43750000L+_kLimbQ5:N0}]>>3)|(ar[+1]<<61)={_expQMid:X16}  match={(_actQMidU = _expQMid)}{vbCrLf}", 5)   ' §252 (#95)
                ' §5B-f3 verify: scan all 100 captured pre-shift samples against actual q.
                ' Mismatch ⇒ BigShiftRight is wrong at that index.  All match ⇒ shift is faithful
                ' (so the bug must be in ar itself, or in the kBits computation, or upstream).
                Dim _f3_mismatchCount As Integer = 0
                Dim _f3_firstMismatch As Integer = -1
                Dim _f3_logCount As Integer = 0
                For _f3s As Integer = 0 To 99
                    Dim _f3_expQ As ULong = (_f3_arLo(_f3s) >> 3) Or (_f3_arHi(_f3s) << 61)
                    Dim _f3_qi As Long = _f3_qIdx(_f3s)
                    Dim _f3_actQ As ULong = If(_f3_qi >= 0L AndAlso _f3_qi < CLng(szQ), CULng(Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(_f3_qi * 8L))), 0UL)
                    If _f3_expQ <> _f3_actQ Then
                        _f3_mismatchCount += 1
                        If _f3_firstMismatch = -1 Then _f3_firstMismatch = _f3s
                        If _f3_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f3 MISMATCH] sample={_f3s} q[{_f3_qi:N0}] expected={_f3_expQ:X16} actual={_f3_actQ:X16} (ar_pre[{_kLimbQ5 + _f3_qi:N0}]={_f3_arLo(_f3s):X16} ar_pre[+1]={_f3_arHi(_f3s):X16}){vbCrLf}", 5)   ' §252 (#95)
                            _f3_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f3 SUMMARY] scanned 100 q positions, mismatches={_f3_mismatchCount}, firstMismatchSampleIdx={_f3_firstMismatch}{vbCrLf}", 5)   ' §252 (#95)
            End If
            ' §113: log q middle limbs to verify BigShiftRight correctness.
            If szQ = 21875001 Then
                Dim _q113Positions() As Long = {10937500L, 20904664L}
                Dim _q113Sb As New System.Text.StringBuilder()
                _q113Sb.Append($"[SafeMpzDiv§113] q middle limbs (szQ={szQ:N0}):{vbCrLf}")
                For Each _qp As Long In _q113Positions
                    Dim _qv As Long = If(_qp < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr, CInt(_qp * 8L)), 0L)
                    _q113Sb.Append($"  q[{_qp:N0}]={_qv:X16}{vbCrLf}")
                Next
                AppendLog(_q113Sb.ToString())
            End If
            ' §135: verify q[20904664] is consistent with ar[64654664/65] saved in §111.
            ' kBits=2800000027 → kLimb=43750000, kRem=27.
            ' q[20904664] = (ar[64654664] >> 27) | (ar[64654665] << 37)
            If szQ = 21875001 AndAlso _ar135_v0 <> 0L Then
                Const _Q135_IDX As Long = 20904664L
                Dim _q135_expected As Long = CLng(CULng(_ar135_v0) >> 27) Or CLng(CULng(_ar135_v1) << 37)
                Dim _qDPtr135 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _q135_actual As Long = If(_Q135_IDX < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr135, CInt(_Q135_IDX * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§135] ar[64654664]={_ar135_v0:X16} ar[64654665]={_ar135_v1:X16} → q[{_Q135_IDX:N0}] expected={_q135_expected:X16} actual={_q135_actual:X16} match={_q135_expected = _q135_actual}{vbCrLf}")
            End If
            ' §142: A2-range q sweep — verify q at 7 evenly-spaced positions from saved ar pairs.
            If szQ = 21875001 AndAlso _ar142_pairs(0, 0) <> 0L Then
                Dim _qDPtr142 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _bDPtr142 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
                Dim _sb142 As New System.Text.StringBuilder()
                _sb142.Append($"[SafeMpzDiv§142] A2-range q vs expected-from-ar vs b (kRem=27):{vbCrLf}")
                For _i142 As Integer = 0 To 6
                    Dim _qj As Long = _ar142_qIdx(_i142)
                    Dim _exp142 As Long = CLng(CULng(_ar142_pairs(_i142, 0)) >> 27) Or CLng(CULng(_ar142_pairs(_i142, 1)) << 37)
                    Dim _act142 As Long = If(_qj < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr142, CInt(_qj * 8L)), 0L)
                    Dim _b142 As Long = If(_qj < CLng(szB), Runtime.InteropServices.Marshal.ReadInt64(_bDPtr142, CInt(_qj * 8L)), 0L)
                    _sb142.Append($"  q[{_qj:N0}] exp={_exp142:X16} act={_act142:X16} match={_exp142 = _act142} b={_b142:X16}{vbCrLf}")
                Next
                AppendLog(_sb142.ToString())
            End If
            ' §141: verify q[21389832] (A2/B2 midpoint) from ar[65139832/33] saved in §112 sweep.
            ' q[21389832] = (ar[65139832] >> 27) | (ar[65139833] << 37)
            ' Also log q[21389832] vs b[21389832] to check their relative magnitude.
            If szQ = 21875001 AndAlso _ar141_v0 <> 0L Then
                Const _Q141_IDX As Long = 21389832L
                Dim _q141_expected As Long = CLng(CULng(_ar141_v0) >> 27) Or CLng(CULng(_ar141_v1) << 37)
                Dim _qDPtr141 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _q141_actual As Long = If(_Q141_IDX < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr141, CInt(_Q141_IDX * 8L)), 0L)
                Dim _bDPtr141 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
                Dim _b141_val As Long = If(_Q141_IDX < CLng(szB), Runtime.InteropServices.Marshal.ReadInt64(_bDPtr141, CInt(_Q141_IDX * 8L)), 0L)
                AppendLog($"[SafeMpzDiv§141] ar[65139832]={_ar141_v0:X16} ar[65139833]={_ar141_v1:X16} → q[{_Q141_IDX:N0}] expected={_q141_expected:X16} actual={_q141_actual:X16} match={_q141_expected = _q141_actual} b[{_Q141_IDX:N0}]={_b141_val:X16}{vbCrLf}")
            End If
            ' §149: verify q at top range [20950000..21875000] against pre-BigShiftRight ar values.
            ' These q limbs (q[20904664..21875000]) are the inputs to A2×B2[13612996] in q×b.
            ' §135 covered q[20904664], §141 covered q[21389832]; this fills the unchecked gap.
            ' A mismatch (exp≠act) means BigShiftRight is wrong at that q position.
            ' A wrong saved ar pair (verified only by §112 sweep) would point to wrong a×r instead.
            If szQ = 21875001 Then
                Dim _qDPtr149 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _sb149 As New System.Text.StringBuilder()
                _sb149.Append($"[SafeMpzDiv§149] top q-range verify — limbs contributing to A2×B2[13612996]:{vbCrLf}")
                Dim _149anyBad As Boolean = False
                For _i149 As Integer = 0 To 10
                    Dim _qk149 As Long = _ar149_qIdx(_i149)
                    Dim _e149 As Long = CLng(CULng(_ar149_pairs(_i149, 0)) >> 27) Or CLng(CULng(_ar149_pairs(_i149, 1)) << 37)
                    Dim _a149 As Long = If(_qk149 < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr149, CInt(_qk149 * 8L)), 0L)
                    If _e149 <> _a149 Then _149anyBad = True
                    _sb149.Append($"  q[{_qk149:N0}] exp={_e149:X16} act={_a149:X16} match={_e149 = _a149}{vbCrLf}")
                Next
                _sb149.Append($"  any_mismatch={_149anyBad}{vbCrLf}")
                AppendLog(_sb149.ToString())
            End If
            ' §151: verify q at gap range [20583334..20950000] against pre-BigShiftRight ar values.
            ' This is the last unverified range contributing to A2×B2[13612996] in q×b.
            If szQ = 21875001 Then
                Dim _qDPtr151 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(ar.Pointer, 8))
                Dim _sb151 As New System.Text.StringBuilder()
                _sb151.Append($"[SafeMpzDiv§151] gap q-range verify [20583334..20950000]:{vbCrLf}")
                Dim _151anyBad As Boolean = False
                For _i151 As Integer = 0 To 3
                    Dim _qk151 As Long = _ar151_qIdx(_i151)
                    Dim _e151 As Long = CLng(CULng(_ar151_pairs(_i151, 0)) >> 27) Or CLng(CULng(_ar151_pairs(_i151, 1)) << 37)
                    Dim _a151 As Long = If(_qk151 < CLng(szQ), Runtime.InteropServices.Marshal.ReadInt64(_qDPtr151, CInt(_qk151 * 8L)), 0L)
                    If _e151 <> _a151 Then _151anyBad = True
                    _sb151.Append($"  q[{_qk151:N0}] exp={_e151:X16} act={_a151:X16} match={_e151 = _a151}{vbCrLf}")
                Next
                _sb151.Append($"  any_mismatch={_151anyBad}{vbCrLf}")
                AppendLog(_sb151.ToString())
            End If
        End If
        GmpRaw_swap(q.Pointer, ar.Pointer)  ' §35
        _qPtr = q.Pointer     ' §§78-qptr: capture before mpz_clear(ar) fires §78 and corrupts q.Pointer
        gmp_lib.mpz_clear(ar)

PostShiftCheckpoint:
        ' §171-ckpt resume target.  On the normal path: _qPtr was captured above and
        ' szQ was set after BigShiftRight.  On the resumed path: both were populated
        ' from the loaded checkpoint at SafeMpzDiv entry.  Either way, q.Pointer holds
        ' the post-shift Barrett quotient and we now save (if not resumed) and proceed.

        ' §171-ckpt: save q so a crash during q×b or §171 adj loops can resume from here.
        ' We skip the save when we just resumed from this same checkpoint — the file
        ' on disk already matches our state.
        If _autoCheckpoint AndAlso (Not _ckpQResumed) Then
            Try
                If Not System.IO.Directory.Exists(_divCkptDir) Then
                    System.IO.Directory.CreateDirectory(_divCkptDir)
                End If
                Dim _saveStaging(4194303) As Byte
                Dim _qMpz As New mpz_t()
                _qMpz.Pointer = _qPtr
                Using _fs As New FileStream(_divCkptBin, FileMode.Create, FileAccess.Write)
                    Using _bw As New BinaryWriter(_fs)
                        SerializeOneMpz(_qMpz, _bw, _saveStaging)
                    End Using
                End Using
                System.IO.File.WriteAllText(_divCkptMeta,
                    $"szA={szA}{vbLf}szB={szB}{vbLf}aBits={aBits}{vbLf}kBits={kBits}{vbLf}scope={_divCkptScope}{vbLf}")
                BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                AppendLog($"[SafeMpzDiv§171-ckpt] saved div_q.bin (szQ={szQ:N0} scope={_divCkptScope}){vbCrLf}")
            Catch _ex As Exception
                AppendLog($"[SafeMpzDiv§171-ckpt] save failed: {_ex.Message}{vbCrLf}")
            End Try
        End If

        ' §118: log b limbs at key positions to compare vs q, and verify a at critical position
        If _logLevel >= 2 AndAlso szB = 21875001 Then
            Dim _b118DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
            Dim _b118_0 As Long = If(szB >= 1, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, 0), 0L)
            Dim _b118_1 As Long = If(szB >= 2, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, 8), 0L)
            Dim _b118_mid As Long = If(szB >= 10937501, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(10937500L * 8L)), 0L)
            Dim _b118_b2start As Long = If(szB >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(14583334L * 8L)), 0L)
            Dim _b118_near As Long = If(szB >= 20904665, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(20904664L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§118] b limbs: bot=[{_b118_0:X16} {_b118_1:X16}] mid[10937500]={_b118_mid:X16} [14583334]={_b118_b2start:X16} [20904664]={_b118_near:X16}{vbCrLf}")
            Dim _a118DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(a.Pointer, 8))
            Dim _a118_4277 As Long = If(szA >= 42779665, Runtime.InteropServices.Marshal.ReadInt64(_a118DPtr, CInt(42779664L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§118b] a[42779664]={_a118_4277:X16}{vbCrLf}")
            ' §131: log q and b limbs in A2/B2 range (A2,B2 start at limb 14,583,334).
            ' A2[13,612,996]=q[28,196,330] is the key limb in A2×B2 that maps to q*b[42,779,664].
            Dim _q131DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(q.Pointer, 8))
            Dim _q131sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(q.Pointer, 4))
            Dim _q131_14 As Long = If(_q131sz >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(14583334L * 8L)), 0L)
            Dim _q131_28 As Long = If(_q131sz >= 28196331, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(28196330L * 8L)), 0L)
            Dim _q131_top As Long = If(_q131sz >= 21875001, Runtime.InteropServices.Marshal.ReadInt64(_q131DPtr, CInt(21875000L * 8L)), 0L)
            Dim _b131_14 As Long = If(szB >= 14583335, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(14583334L * 8L)), 0L)
            Dim _b131_28 As Long = If(szB >= 28196331, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(28196330L * 8L)), 0L)
            Dim _b131_top As Long = If(szB >= 21875001, Runtime.InteropServices.Marshal.ReadInt64(_b118DPtr, CInt(21875000L * 8L)), 0L)
            AppendLog($"[SafeMpzDiv§131] q/b A2 limbs: q[14583334]={_q131_14:X16} b[14583334]={_b131_14:X16} q[28196330]={_q131_28:X16} b[28196330]={_b131_28:X16} q[21875000]={_q131_top:X16} b[21875000]={_b131_top:X16}{vbCrLf}")
        End If

        ' Adjustment: remainder = a - q·b; fix until 0 ≤ remainder < b  (at most 2 corrections)
        ' §184: Use raw struct header for qb — bypasses Math.Gmp.Native managed wrapper entirely.
        ' gmp_lib.mpz_init(qb) + gmp_lib.mpz_sub(remainder, a, qb) goes through the managed
        ' wrapper, which exhibits the §78 side-effect: after gmp_lib.mpz_init(remainder),
        ' Math.Gmp.Native corrupts qb.Pointer (it scans registered mpz_t objects and updates
        ' their Pointer fields).  When gmp_lib.mpz_sub then reads qb.Pointer, it gets a garbage
        ' address and passes it to GMP's __gmpz_sub, which hits a STATUS_ASSERTION_FAILURE
        ' (exception 0x40000015, offset 0x14ef6 in libgmp-10.dll) every time.
        ' Fix: allocate qb as a plain raw IntPtr struct (Marshal.AllocHGlobal(16)), use
        ' GmpRaw_init to fill it, pass SafeMpzMul the managed wrapper for result capture,
        ' then do all subsequent operations (sub, clear) via raw P/Invoke using the captured
        ' raw pointer — immune to §78 corruption.
        ' _aPtr and _bPtr were captured at SafeMpzDiv entry (§184c) — already correct here.
        Dim _qbRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        GmpRaw_init(_qbRaw)   ' sets _mp_alloc=1, _mp_size=0, allocates 1-limb buffer via GmpAllocFunc
        Dim qb As New mpz_t()
        qb.Pointer = _qbRaw
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] computing q*b (szQ={szQ:N0} szB={szB:N0})...{vbCrLf}")
        ' §220 (issue #55, 2026-05-22): §167 force-serial LIFTED.
        ' Same rationale as §166 (above): the original wrong-q×b was caused by upstream
        ' wrong-r from premature Newton convergence, fixed by §200/§201. q×b parallel
        ' execution is safe with current SafeMpzReciprocal.
        ' Original lines preserved as comments for easy revert (grep §220).
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv§220] §167 lifted — q×b runs at caller DOP={System.Threading.Volatile.Read(_safeMulDop)}{vbCrLf}")
        ' §269 (#88): route the FULL q×b through the chunked grid (bit-exact full mode) instead of the
        ' slow §gen recursion — the §268 adaptive 16M cell makes it far faster (§266: 260M² full ~8.6×
        ' vs the old 1.5M chunked, and far beyond §gen).  Gated: flag + size(>1 cell) + DOP, and OFF
        ' under _5b_verify (the §5B diagnostics read §gen internals of qb).  qb's buffer lifecycle is
        ' unchanged (both §gen and chunked swap a GmpNativeAlloc accumulator into qb).
        Dim _szQ269 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(q.Pointer, 4))
        If _divQbChunked AndAlso (Not _5b_verify) _
           AndAlso (CLng(_szQ269) > 1500000L OrElse CLng(szB) > 1500000L) _
           AndAlso MemBudget_SuggestSafeMulDop(CLng(_szQ269), CLng(szB)) <= _recipShortMulMaxDop Then
            AppendLog($"[SafeMpzDiv§269] q×b via chunked-full: szQ={_szQ269:N0} szB={szB:N0}{vbCrLf}", 2)
            SafeMpzMul_ChunkedGrid(qb, q, b, 0L)
        Else
            SafeMpzMul(qb, q, b)
        End If
        ' Capture qb's raw pointer immediately — before any native call that could corrupt qb.Pointer.
        Dim _qbPtr As IntPtr = qb.Pointer   ' = savedResultPtr set by SafeMpzMul
        Dim szQB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4))
        If _logLevel >= 4 Then AppendLog($"[SafeMpzDiv§184] qb raw: alloc={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 0):N0} size={Runtime.InteropServices.Marshal.ReadInt32(_qbPtr, 4):N0} _mp_d={Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8):X16}{vbCrLf}")

        ' §241 (issue #69): trim pooled temporaries left over from q×b. Census-before
        ' here captures the post-q×b retention (the highest-RAM point in the divide).
        TrimPoolAtBoundary("post-qxb", CULng(BYTES_PER_MB))

        ' §5B-f4: Cheap qb sanity checks (post-SafeMpzMul(qb, q, b), pre-subtract).
        ' Mathematically q ≈ q_true (within ±1 by Barrett), so q × b ≈ a (within b).
        ' qb's TOP limbs should match a's TOP limbs almost exactly; qb[0] should equal
        ' (q[0] × b[0]) mod 2^64 by integer-multiplication identity.
        ' If qb[top] differs significantly from a[top] ⇒ SafeMpzMul has a bug specific
        ' to symmetric q × b at this scale (the OPPOSITE of F-1's a × r verification).
        ' If qb[0] ≠ (q[0] × b[0]) mod 2^64 ⇒ SafeMpzMul has a bottom-limb bug for qb.
        If _logLevel >= 2 AndAlso szQ = 87500001 AndAlso szB = 87500001 Then
            Dim _f4_aD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_aPtr, 8))
            Dim _f4_qD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qPtr, 8))
            Dim _f4_bD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
            Dim _f4_qbD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8))
            Dim _f4_q0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qD, 0))
            Dim _f4_b0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_bD, 0))
            Dim _f4_qb0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, 0))
            Dim _f4_qb1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, 8))
            Dim _f4_a0 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, 0))
            Dim _f4_a1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, 8))
            Dim _f4_expQb0 As ULong = _f4_q0 * _f4_b0
            Dim _f4_qbTop As ULong = If(szQB >= 1, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 1) * 8L))), 0UL)
            Dim _f4_qbTop1 As ULong = If(szQB >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 2) * 8L))), 0UL)
            Dim _f4_qbTop2 As ULong = If(szQB >= 3, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qbD, CInt(CLng(szQB - 3) * 8L))), 0UL)
            Dim _f4_aTop As ULong = If(szA >= 1, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 1) * 8L))), 0UL)
            Dim _f4_aTop1 As ULong = If(szA >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 2) * 8L))), 0UL)
            Dim _f4_aTop2 As ULong = If(szA >= 3, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(CLng(szA - 3) * 8L))), 0UL)
            AppendLog($"[SafeMpzDiv§5B-f4 inputs] q[0]={_f4_q0:X16} q[1]={CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_qD, 8)):X16} b[0]={_f4_b0:X16} b[1]={CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_bD, 8)):X16}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f4 qbBot] qb[0]={_f4_qb0:X16} qb[1]={_f4_qb1:X16} (q[0]*b[0])_lo={_f4_expQb0:X16} match={(_f4_qb0 = _f4_expQb0)}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f4 qbTop] qb[szQB-1..-3]=[{_f4_qbTop:X16} {_f4_qbTop1:X16} {_f4_qbTop2:X16}] vs a[szA-1..-3]=[{_f4_aTop:X16} {_f4_aTop1:X16} {_f4_aTop2:X16}] szQB={szQB:N0} szA={szA:N0}{vbCrLf}", 5)   ' §252 (#95)
            ' Sanity: q × b should be ≤ a (since q = floor(a/b) ≈ q_true, q × b ≤ q_true × b ≤ a).
            ' qb's top limb should equal or be just below a's top.
            If _f4_qbTop > _f4_aTop Then
                AppendLog($"[SafeMpzDiv§5B-f4 ALARM] qb[top]={_f4_qbTop:X16} > a[top]={_f4_aTop:X16} — qb is bigger than a in top limb (extreme overshoot){vbCrLf}", 5)   ' §252 (#95)
            End If

            ' §5B-f6: re-read a at the same positions §5B-a captured, verify integrity.
            ' If a's data was modified (e.g., by SafeMpzMul writing through input pieces),
            ' we'd see a mismatch here.  Captures: a[0]=A514E7911325F190, a[1]=96FCE3B61243D2E0,
            ' a[mid=87,500,000]=9A776346843EEB7A, a[szA-2]=A4CCDE102251DA76, a[szA-1]=0479BC06C17340EB.
            Dim _f6_a0 As ULong = _f4_a0
            Dim _f6_a1 As ULong = _f4_a1
            Dim _f6_aMid As ULong = If(szA > 87500000, CULng(Runtime.InteropServices.Marshal.ReadInt64(_f4_aD, CInt(87500000L * 8L))), 0UL)
            Dim _f6_aTop2 As ULong = _f4_aTop1
            Dim _f6_aTop As ULong = _f4_aTop
            Const _F6_EXP_A0 As ULong = &HA514E7911325F190UL
            Const _F6_EXP_A1 As ULong = &H96FCE3B61243D2E0UL
            Const _F6_EXP_AMID As ULong = &H9A776346843EEB7AUL
            Const _F6_EXP_ATOP2 As ULong = &HA4CCDE102251DA76UL
            Const _F6_EXP_ATOP As ULong = &H479BC06C17340EBUL
            Dim _f6_okBot As Boolean = (_f6_a0 = _F6_EXP_A0 AndAlso _f6_a1 = _F6_EXP_A1)
            Dim _f6_okMid As Boolean = (_f6_aMid = _F6_EXP_AMID)
            Dim _f6_okTop As Boolean = (_f6_aTop2 = _F6_EXP_ATOP2 AndAlso _f6_aTop = _F6_EXP_ATOP)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[0]={_f6_a0:X16} (exp {_F6_EXP_A0:X16}) a[1]={_f6_a1:X16} (exp {_F6_EXP_A1:X16}) okBot={_f6_okBot}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[mid=87,500,000]={_f6_aMid:X16} (exp {_F6_EXP_AMID:X16}) okMid={_f6_okMid}{vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f6 a-integrity] a[szA-2]={_f6_aTop2:X16} (exp {_F6_EXP_ATOP2:X16}) a[szA-1]={_f6_aTop:X16} (exp {_F6_EXP_ATOP:X16}) okTop={_f6_okTop}{vbCrLf}", 5)   ' §252 (#95)
            If Not (_f6_okBot AndAlso _f6_okMid AndAlso _f6_okTop) Then
                AppendLog($"[SafeMpzDiv§5B-f6 ALARM] a was corrupted between SafeMpzDiv entry and qb completion!{vbCrLf}", 5)   ' §252 (#95)
            End If
        End If

        ' §5B-f5: Chunked-grid independent reference for q × b at the 5B SafeMpzDiv call.
        ' All other components verified: a intact (F-6), r correct (F-2), ar=a×r correct
        ' (F-1), q derived correctly via BigShiftRight (F-3), §39 not the bug (Option G).
        ' Yet rem = a - qb has size 172.7M limbs (> 2× szB).  The only remaining unverified
        ' component is qb = SafeMpzMul(q, b) middle limbs.
        '
        ' Compute reference qb via chunked-grid (59 × 59 = 3,481 sub-products at ≤ 3M total
        ' each — reliable mpz_mul per §160).  Scan our SafeMpzMul qb against the reference
        ' at 1,000 evenly-spaced positions across [0..szQB-1].
        '
        ' Outcome:
        '   Mismatches > 0 ⇒ SafeMpzMul has a bug specific to q × b at this scale; bug
        '                    is in §gen for symmetric q × b (87.5M × 87.5M).
        '   Mismatches = 0 ⇒ qb is correct; bug is in the §171 algorithm itself or in
        '                    rem subtraction.
        Const _F5_ENABLED As Boolean = False  ' DONE — run 19 confirmed qb matches reference at 1000 positions
        If _logLevel >= 2 AndAlso szQ = 87500001 AndAlso szB = 87500001 AndAlso _F5_ENABLED Then
            Const _F5_CHUNK As Integer = 1500000
            Const _F5_MAX_LIMBS As Integer = 180_000_000
            Dim _F5_MAX_BYTES As Long = CLng(_F5_MAX_LIMBS) * 8L
            Dim _F5_qD As Long = Runtime.InteropServices.Marshal.ReadInt64(_qPtr, 8)
            Dim _F5_qSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_qPtr, 4))
            Dim _F5_bD As Long = Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8)
            Dim _F5_bSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_bPtr, 4))
            AppendLog($"[SafeMpzDiv§5B-f5] starting q×b chunked-grid reference (chunk={_F5_CHUNK:N0}, prealloc={_F5_MAX_LIMBS:N0} limbs/buf, {_F5_MAX_BYTES \ BYTES_PER_MB:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            AppendLog($"[SafeMpzDiv§5B-f5] q sz={_F5_qSz:N0} b sz={_F5_bSz:N0}{vbCrLf}", 5)   ' §252 (#95)
            Dim _F5_eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F5_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            Dim _F5_eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_F5_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
            If _F5_eAccBuf = IntPtr.Zero OrElse _F5_eShiftBuf = IntPtr.Zero Then
                AppendLog($"[SafeMpzDiv§5B-f5] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                If _F5_eAccBuf <> IntPtr.Zero Then VirtualFree(_F5_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                If _F5_eShiftBuf <> IntPtr.Zero Then VirtualFree(_F5_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Else
                Dim _F5_refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_refAcc)
                Dim _F5_ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F5_refAcc, 0))
                Dim _F5_ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_F5_ra_initPtr, _F5_ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_refAcc, 0, _F5_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F5_refAcc, 8, _F5_eAccBuf.ToInt64())
                Dim _F5_ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_ckShifted)
                Dim _F5_cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_F5_ckShifted, 0))
                Dim _F5_cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_F5_cs_initPtr, _F5_cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 0, _F5_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_F5_ckShifted, 8, _F5_eShiftBuf.ToInt64())
                Dim _F5_ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_F5_ckPartial)
                Dim _F5_ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F5_ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _F5_numCkQ As Integer = (_F5_qSz + _F5_CHUNK - 1) \ _F5_CHUNK
                Dim _F5_numCkB As Integer = (_F5_bSz + _F5_CHUNK - 1) \ _F5_CHUNK
                Dim _F5_ckCount As Integer = 0
                For i As Integer = 0 To _F5_numCkQ - 1
                    Dim _F5_qOff As Long = CLng(i) * CLng(_F5_CHUNK)
                    Dim _F5_qSzCk As Integer = CInt(System.Math.Min(CLng(_F5_CHUNK), CLng(_F5_qSz) - _F5_qOff))
                    If _F5_qSzCk <= 0 Then Continue For
                    While _F5_qSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F5_qD + (_F5_qOff + CLng(_F5_qSzCk - 1)) * 8L)) = 0L
                        _F5_qSzCk -= 1
                    End While
                    If _F5_qSzCk <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_F5_ckA, 0, _F5_CHUNK)
                    Runtime.InteropServices.Marshal.WriteInt32(_F5_ckA, 4, _F5_qSzCk)
                    Runtime.InteropServices.Marshal.WriteInt64(_F5_ckA, 8, _F5_qD + _F5_qOff * 8L)
                    For j As Integer = 0 To _F5_numCkB - 1
                        Dim _F5_bOff As Long = CLng(j) * CLng(_F5_CHUNK)
                        Dim _F5_bSzCk As Integer = CInt(System.Math.Min(CLng(_F5_CHUNK), CLng(_F5_bSz) - _F5_bOff))
                        If _F5_bSzCk <= 0 Then Continue For
                        While _F5_bSzCk > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_F5_bD + (_F5_bOff + CLng(_F5_bSzCk - 1)) * 8L)) = 0L
                            _F5_bSzCk -= 1
                        End While
                        If _F5_bSzCk <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_F5_ckB, 0, _F5_CHUNK)
                        Runtime.InteropServices.Marshal.WriteInt32(_F5_ckB, 4, _F5_bSzCk)
                        Runtime.InteropServices.Marshal.WriteInt64(_F5_ckB, 8, _F5_bD + _F5_bOff * 8L)
                        GmpRaw_mul(_F5_ckPartial, _F5_ckA, _F5_ckB)
                        Dim _F5_shiftBits As ULong = CULng(_F5_qOff + _F5_bOff) * 64UL
                        If _F5_shiftBits = 0UL Then
                            GmpRaw_add(_F5_refAcc, _F5_refAcc, _F5_ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_F5_ckShifted, 4, 0)
                            Dim _F5_shiftSrc As IntPtr = _F5_ckPartial
                            Dim _F5_shiftRem As ULong = _F5_shiftBits
                            While _F5_shiftRem > 0UL
                                Dim _F5_chunkBits As UInteger = CUInt(System.Math.Min(_F5_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_F5_ckShifted, _F5_shiftSrc, _F5_chunkBits)
                                _F5_shiftSrc = _F5_ckShifted
                                _F5_shiftRem -= CULng(_F5_chunkBits)
                            End While
                            GmpRaw_add(_F5_refAcc, _F5_refAcc, _F5_ckShifted)
                        End If
                        _F5_ckCount += 1
                    Next j
                Next i
                Dim _F5_refSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_F5_refAcc, 4))
                Dim _F5_refDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_F5_refAcc, 8))
                Dim _F5_qbDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_qbPtr, 8))
                AppendLog($"[SafeMpzDiv§5B-f5] reference complete: subProducts={_F5_ckCount:N0} refSz={_F5_refSz:N0} ourQbSz={szQB:N0}{vbCrLf}", 5)   ' §252 (#95)
                Const _F5_NUM_SAMPLES As Integer = 1000
                Dim _F5_mismatchCount As Integer = 0
                Dim _F5_firstMismatchIdx As Long = -1L
                Dim _F5_logCount As Integer = 0
                Dim _F5_maxIdx As Long = CLng(System.Math.Min(_F5_refSz, szQB)) - 1L
                For _F5s As Integer = 0 To _F5_NUM_SAMPLES - 1
                    Dim _F5_idx As Long = If(_F5_NUM_SAMPLES > 1, CLng(_F5s) * _F5_maxIdx \ CLng(_F5_NUM_SAMPLES - 1), 0L)
                    Dim _F5_refV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F5_refDPtr, CInt(_F5_idx * 8L)))
                    Dim _F5_qbV As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_F5_qbDPtr, CInt(_F5_idx * 8L)))
                    If _F5_refV <> _F5_qbV Then
                        _F5_mismatchCount += 1
                        If _F5_firstMismatchIdx = -1L Then _F5_firstMismatchIdx = _F5_idx
                        If _F5_logCount < 10 Then
                            AppendLog($"[SafeMpzDiv§5B-f5 MISMATCH] sample={_F5s} qb[{_F5_idx:N0}] reference={_F5_refV:X16} ourSafeMpzMul={_F5_qbV:X16}{vbCrLf}", 5)   ' §252 (#95)
                            _F5_logCount += 1
                        End If
                    End If
                Next
                AppendLog($"[SafeMpzDiv§5B-f5 SUMMARY] scanned {_F5_NUM_SAMPLES} qb positions across [0..{_F5_maxIdx:N0}], mismatches={_F5_mismatchCount}, firstMismatchQbIdx={_F5_firstMismatchIdx}{vbCrLf}", 5)   ' §252 (#95)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckShifted)
                GmpRaw_clear(_F5_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckPartial)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_F5_ckB)
                VirtualFree(_F5_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_F5_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            End If
        End If
        ' §184: Allocate remainder as raw struct with pre-allocated limb buffer large enough
        ' to hold the result of a - qb (max szA limbs) — avoids GmpReallocFunc being called
        ' inside __gmpz_sub, which could trigger the §78 side-effect or pool interaction.
        Dim _remLimbs As Long = CLng(szA) + 2L
        Dim _remBuf As IntPtr = GmpNativeAlloc_PoolGet(_remLimbs * 8L)
        Dim _remRaw As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Runtime.InteropServices.Marshal.WriteInt32(_remRaw, 0, CInt(_remLimbs))  ' _mp_alloc
        Runtime.InteropServices.Marshal.WriteInt32(_remRaw, 4, 0)                ' _mp_size = 0
        Runtime.InteropServices.Marshal.WriteInt64(_remRaw, 8, _remBuf.ToInt64()) ' _mp_d
        Dim remainder As New mpz_t()
        remainder.Pointer = _remRaw
        ' §184: Use captured _aPtr and _qbPtr — both corrupted by §78 after SafeMpzMul.
        GmpRaw_sub(_remRaw, _aPtr, _qbPtr)
        GmpRaw_clear(_qbPtr) : Runtime.InteropServices.Marshal.FreeHGlobal(_qbRaw)
        qb.Pointer = IntPtr.Zero   ' prevent GC finalizer from double-freeing
        Dim remSign As Integer = System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
        Dim szRem As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
        If _logLevel >= 2 Then
            ' §215: 64-bit safe offset (szB can be 739M at 5B).
            Dim _remDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
            Dim _remTop As Long = If(szRem >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_remDPtr.ToInt64() + (CLng(szRem) - 1L) * 8L), 0), 0L)
            Dim _bDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
            Dim _bTop As Long = If(szB >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bDPtr.ToInt64() + (CLng(szB) - 1L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzDiv] q*b done: szQB={szQB:N0}; remainder sign={remSign} szRem={szRem:N0} remTop={_remTop:X16} bTop={_bTop:X16}{vbCrLf}")
        End If

        ' §184: All adj-down/adj-up operations use _remRaw directly (raw IntPtr) — immune to §78.
        ' §35: mpz_sgn is a GMP macro — read _mp_size field directly.
        Dim _adjDown As Integer = 0
        Do While System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4)) < 0  ' q too large
            _adjDown += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-down iter={_adjDown}{vbCrLf}")
            If _adjDown > MAX_ADJ_ITERS Then
                Dim _szRem2 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                GmpRaw_clear(_remRaw) : Runtime.InteropServices.Marshal.FreeHGlobal(_remRaw)
                remainder.Pointer = IntPtr.Zero
                Throw New InvalidOperationException($"SafeMpzDiv adj-down exceeded {MAX_ADJ_ITERS} iters — reciprocal likely wrong. szA={szA} szB={szB} aBits={aBits} kBits={kBits} szR={szR} szQ={szQ} szQB={szQB} szRem={_szRem2}")
            End If
            GmpRaw_sub_ui(_qPtr, _qPtr, 1UI)
            GmpRaw_add(_remRaw, _remRaw, _bPtr)
        Loop
        If _logLevel >= 3 Then AppendLog($"[SafeMpzDiv] adj-down complete: {_adjDown} iter(s){vbCrLf}", 3)

        Dim _adjUp As Integer = 0
        Dim _171Done As Boolean = False
        Do While GmpRaw_cmp(_remRaw, _bPtr) >= 0   ' §35: q too small
            _adjUp += 1
            If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-up iter={_adjUp}{vbCrLf}")
            If _adjUp > MAX_ADJ_ITERS AndAlso Not _171Done Then
                ' §171: Barrett estimate is wildly off — rem >> b after adj loop.
                ' Iterative top-limb correction: each pass computes delta = floor(rem_top /
                ' (bTop+1)) via mpn_divrem_1 (no TMP_ALLOC, safe for any szB), then subtracts
                ' delta×b from rem and adds delta to q.  Loops until szRem ≤ szB, then normal
                ' adj-up finishes the last few iters.
                '
                ' §171-fix (5B crash, commit after 066f613): use captured _prodHdr171 (not
                ' _prod171.Pointer) for the subtract.  SafeMpzMul recursion can corrupt
                ' result.Pointer per §175 — at 5B the stale .Pointer pointed to a struct with
                ' _mp_size=0 so GmpRaw_sub effectively subtracted zero → szRem unchanged →
                ' §171b fallback → GmpRaw_tdiv_q AV on 172M-limb input.  The raw IntPtr
                ' _prodHdr171 is set by SafeMpzMul's savedResultPtr path (line 2815) and is
                ' always correct.
                _171Done = True
                Dim _szRem171 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                ' §171-entry: log bTop's bit width to quickly spot unnormalized divisors —
                ' these can't converge via single-limb top correction at 5B+ scale.
                Dim _bData171e As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bPtr, 8))
                Dim _bTop171e As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bData171e.ToInt64() + CLng(szB - 1) * 8L), 0))  ' §239: 64-bit-safe (szB ≈ 739M at 5B → CInt overflow)
                Dim _bTopBits171 As Integer = 0
                Dim _bTopScan As ULong = _bTop171e
                Do While _bTopScan <> 0UL
                    _bTopBits171 += 1
                    _bTopScan >>= 1
                Loop
                AppendLog($"[SafeMpzDiv§171-entry] szA={szA:N0} szB={szB:N0} szRem={_szRem171:N0} ratio={(CDbl(_szRem171)/szB):F3} bTop=0x{_bTop171e:X16} bTopBits={_bTopBits171} (if <48, normalizing per §218 below){vbCrLf}")

                ' §218 (issue #78, 2026-05-21): Knuth Algorithm D-style divisor normalization.
                ' When _bTopBits171 < 48, the previous single-limb correction
                ' delta = floor(remTop / (bTop+1)) over-estimates by up to 2^(64-bTopBits)×
                ' because the lower (szB-1) limbs of b contribute meaningfully to the actual
                ' divisor value that bTop+1 ignores. The over-estimate makes delta×b > rem;
                ' subtraction wraps to negative-magnitude same-size remainder; convergence
                ' check fires.
                '
                ' Fix: shift both rem and b LEFT by shift = 64 - bTopBits so the normalized
                ' bTop has its top bit set (bTopBits_norm = 64). The single-limb estimate
                ' is then tight (off by at most ±1-2 limbs, matching the pre-normalization
                ' design intent). The quotient delta is scale-invariant: (rem×2^s)/(b×2^s)
                ' = rem/b, so q is unaffected. At the end of correction we shift rem RIGHT
                ' by shift to restore original scale before returning to the outer adj-up loop.
                Dim _shift218 As Integer = 0
                Dim _bForCorr218 As IntPtr = _bPtr
                Dim _szBForCorr218 As Integer = szB
                Dim _bNormHdr218 As IntPtr = IntPtr.Zero
                If _bTopBits171 < 48 Then
                    _shift218 = 64 - _bTopBits171
                    AppendLog($"[SafeMpzDiv§218-norm] bTopBits={_bTopBits171} < 48; shift-normalizing b and rem by {_shift218} bits{vbCrLf}")
                    _bNormHdr218 = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_bNormHdr218)
                    ' §218 (issue #78 fix-up): mul_2exp internally calls __gmpz_realloc which
                    ' aborts when new_alloc > INT_MAX/64 = 33.5M limbs (same class of bug as
                    ' §216a). At 1B scale b is ~140M limbs; without PreAlloc, mul_2exp aborts
                    ' the process with "gmp: overflow in mpz type". Wrap each raw IntPtr in
                    ' an mpz_t struct and PreAllocMpzToLimbs to bypass GMP's check before
                    ' calling mul_2exp.
                    Dim _bNormWrap218 As New mpz_t()
                    _bNormWrap218.Pointer = _bNormHdr218
                    PreAllocMpzToLimbs(_bNormWrap218, CLng(szB) + 2L)
                    AppendLog($"[SafeMpzDiv§218-norm] _bNorm PreAlloc'd to {(CLng(szB) + 2L):N0} limbs{vbCrLf}")
                    GmpRaw_mul_2exp(_bNormHdr218, _bPtr, CUInt(_shift218))

                    Dim _remWrap218 As New mpz_t()
                    _remWrap218.Pointer = _remRaw
                    PreAllocMpzToLimbs(_remWrap218, CLng(_szRem171) + 2L)
                    AppendLog($"[SafeMpzDiv§218-norm] _rem PreAlloc'd to {(CLng(_szRem171) + 2L):N0} limbs{vbCrLf}")
                    GmpRaw_mul_2exp(_remRaw, _remRaw, CUInt(_shift218))

                    _bForCorr218 = _bNormHdr218
                    _szBForCorr218 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_bNormHdr218, 4))
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§218-norm] after normalization: szB_norm={_szBForCorr218:N0} szRem_norm={_szRem171:N0}{vbCrLf}")
                End If

                Dim _171Pass As Integer = 0
                Do While _szRem171 > _szBForCorr218
                    _171Pass += 1
                    If _171Pass > 64 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 failed to converge in 64 passes (szRem={_szRem171}, szB={_szBForCorr218}, szA={szA}, shift218={_shift218})")
                    End If
                    Dim _szRemBefore171 As Integer = _szRem171
                    Dim _remData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_remRaw, 8))
                    Dim _bData171 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_bForCorr218, 8))
                    Dim _bTop171 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_bData171.ToInt64() + CLng(_szBForCorr218 - 1) * 8L), 0))  ' §239: 64-bit-safe (szB ≈ 739M at 5B → CInt overflow)
                    Dim _topSliceLen171 As Integer = _szRem171 - _szBForCorr218 + 1
                    Dim _deltaBytes171 As Long = CLng(_topSliceLen171) * 8L
                    Dim _deltaBuf171 As IntPtr = GmpNativeAlloc_PoolGet(_deltaBytes171)
                    If _deltaBuf171 = IntPtr.Zero Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pool alloc failed on pass {_171Pass}: requested {_deltaBytes171:N0} bytes")
                    End If
                    Dim _remTopPtr171 As IntPtr = New IntPtr(_remData171.ToInt64() + CLng(_szBForCorr218 - 1) * 8L)
                    GmpRaw_mpn_divrem_1(_deltaBuf171, 0, _remTopPtr171, _topSliceLen171, _bTop171 + 1UL)
                    Dim _deltaSz171 As Integer = _topSliceLen171
                    Do While _deltaSz171 > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_deltaBuf171.ToInt64() + CLng(_deltaSz171 - 1) * 8L), 0) = 0L  ' §239: 64-bit-safe
                        _deltaSz171 -= 1
                    Loop
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] bTop=0x{_bTop171:X16} szDelta={_deltaSz171:N0} szRemBefore={_szRemBefore171:N0}{vbCrLf}", 3)   ' §252 (#95)
                    If _deltaSz171 = 0 Then
                        GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                        Throw New InvalidOperationException($"SafeMpzDiv §171 delta=0 on pass {_171Pass}: top-limb ratio too small. szRem={_szRem171}, szB={_szBForCorr218}, bTop=0x{_bTop171:X16}, shift218={_shift218}")
                    End If
                    Dim _deltaHdr171 As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    Runtime.InteropServices.Marshal.WriteInt32(_deltaHdr171, 0, _topSliceLen171)
                    Runtime.InteropServices.Marshal.WriteInt32(_deltaHdr171, 4, _deltaSz171)
                    Runtime.InteropServices.Marshal.WriteInt64(_deltaHdr171, 8, _deltaBuf171.ToInt64())
                    GmpRaw_add(_qPtr, _qPtr, _deltaHdr171)
                    Dim _prodHdr171 As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_prodHdr171)
                    Dim _prod171 As New mpz_t()
                    _prod171.Pointer = _prodHdr171
                    Dim _bWrap171 As New mpz_t()
                    _bWrap171.Pointer = _bForCorr218
                    Dim _deltaWrap171 As New mpz_t()
                    _deltaWrap171.Pointer = _deltaHdr171
                    SafeMpzMul(_prod171, _deltaWrap171, _bWrap171)
                    ' §171-fix: read prod size from _prodHdr171 (captured raw) — _prod171.Pointer may be stale per §175.
                    Dim _szProd171 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_prodHdr171, 4))
                    Dim _ptrMatch171 As Boolean = (_prodHdr171 = _prod171.Pointer)
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] szProd={_szProd171:N0} prodHdr=0x{_prodHdr171.ToInt64():X} prod.Ptr=0x{_prod171.Pointer.ToInt64():X} match={_ptrMatch171}{vbCrLf}", 3)   ' §252 (#95)
                    GmpRaw_sub(_remRaw, _remRaw, _prodHdr171)
                    GmpRaw_clear(_prodHdr171)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_prodHdr171)
                    _prod171.Pointer = IntPtr.Zero
                    Runtime.InteropServices.Marshal.FreeHGlobal(_deltaHdr171)
                    GmpNativeAlloc_FreeRaw(_deltaBuf171, _deltaBytes171)
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§171 pass={_171Pass}] done: szRemAfter={_szRem171:N0} Δ={_szRemBefore171 - _szRem171:N0}{vbCrLf}", 3)   ' §252 (#95)
                    If _szRem171 >= _szRemBefore171 Then
                        Throw New InvalidOperationException($"SafeMpzDiv §171 pass {_171Pass} did not reduce rem SIZE: before={_szRemBefore171}, after={_szRem171}, szB={_szBForCorr218}, szProd={_szProd171}, ptrMatch={_ptrMatch171}, bTopBits_orig={_bTopBits171}, shift218={_shift218}. After §218 normalization this should not occur — investigate further.")
                    End If
                Loop
                AppendLog($"[SafeMpzDiv§171-done] {_171Pass} pass(es); szRem={_szRem171:N0} ≤ szB={_szBForCorr218:N0}{vbCrLf}", 3)   ' §252 (#95)

                ' §218 (issue #78): denormalize rem back to original scale, then free the
                ' normalized b copy. The outer adj-up loop and any downstream code references
                ' _remRaw at the original (unshifted) scale.
                If _shift218 > 0 Then
                    GmpRaw_tdiv_q_2exp(_remRaw, _remRaw, CUInt(_shift218))
                    _szRem171 = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                    AppendLog($"[SafeMpzDiv§218-norm] denormalized rem; szRem after rshift={_szRem171:N0}{vbCrLf}")
                    GmpRaw_clear(_bNormHdr218)
                    Runtime.InteropServices.Marshal.FreeHGlobal(_bNormHdr218)
                End If

                _adjUp = 0
                Continue Do
            End If
            If _adjUp > MAX_ADJ_ITERS Then
                ' §171b: after iterative correction, adj-up must converge in ≤ MAX_ADJ_ITERS.
                ' If not, something is fundamentally wrong — throw with full diagnostics.
                ' (Do NOT fall back to GmpRaw_tdiv_q — it AVs for 170M+ limb inputs in the
                ' SafeMpzSqrt/Newton call stack; see investigation 2026-04-23.)
                Dim _szRem171b As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_remRaw, 4))
                Throw New InvalidOperationException($"SafeMpzDiv §171b: adj-up still exceeded {MAX_ADJ_ITERS} iters after §171 correction. szRem={_szRem171b}, szB={szB}, szA={szA}")
            End If
            GmpRaw_add_ui(_qPtr, _qPtr, 1UI)
            GmpRaw_sub(_remRaw, _remRaw, _bPtr)
        Loop
        If _logLevel >= 2 Then AppendLog($"[SafeMpzDiv] adj-up complete: {_adjUp} iter(s); SafeMpzDiv done{vbCrLf}")

        ' §202-trace: dense logging through SafeMpzDiv exit cleanup.  The 5B-run-1 process
        ' died silently between "SafeMpzDiv done" and the next outer Newton checkpoint —
        ' this trace pinpoints which exit step (rem free, ckpt cleanup, return) was last.
        AppendLog($"[SafeMpzDiv§202-exit] start cleanup; scope={_divCkptScope} szQ={szQ:N0}{vbCrLf}", 3)   ' §252 (#95)
        q.Pointer = _qPtr  ' §§78-qptr: restore after adj loops used _qPtr directly
        GmpRaw_clear(_remRaw) : Runtime.InteropServices.Marshal.FreeHGlobal(_remRaw)
        remainder.Pointer = IntPtr.Zero
        AppendLog($"[SafeMpzDiv§202-exit] remainder cleared and freed{vbCrLf}", 3)   ' §252 (#95)

        ' §217 (2026-05-19, user directive after gmpPi.bin loss on 5B run):
        ' NO CHECKPOINT IS DELETED MID-RUN.  The previous §171-ckpt and §211 §NR-ckpt
        ' cleanup blocks fired at SafeMpzDiv exit — that is "this divide converged" but
        ' NOT "the whole run succeeded".  Multiple SafeMpzDiv calls happen per run
        ' (a×r, q×b, plus several in sqrt-Newton); deleting after the first one defeats
        ' the point of having checkpoints to recover from a later failure.
        '
        ' Stale-file safety is handled at the LOAD side, not the WRITE side:
        '   - §171-ckpt load at line ~3813 validates scope/szA/szB/aBits/kBits
        '   - §NR-ckpt load at line ~3440 validates kBits/bBits/prec
        '   - §piCkpt  load at line ~7341 validates digits
        ' A stale file with mismatched metadata is silently rejected ("load failed —
        ' running full path"), so leaving stale files on disk does not poison anything.
        '
        ' Cleanup happens externally between runs (Run-PiCompute.ps1's
        ' Invoke-CheckpointBackup + the §94 stale-snapshot purge at the start of a
        ' fresh non-resume run), never inside ComputePiGMP or SafeMpzDiv.
        AppendLog($"[SafeMpzDiv§202-exit] returning to caller (§217: ckpt files preserved){vbCrLf}", 3)   ' §252 (#95)
    End Sub
End Class
