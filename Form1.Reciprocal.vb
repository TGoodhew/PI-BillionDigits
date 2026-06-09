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

    ' Compute r = floor(2^kBits / b) for b > 0, kBits > sizeinbase(b,2).
    ' Newton iteration with progressive precision; r is always an underestimate.
    ' All large multiplications use SafeMpzMul — no direct mpn_mul_fft calls.
    ''' <summary>
    ''' Barrett-style reciprocal: computes r ≈ floor(2^kBits / b) by progressive-precision Newton
    ''' iteration (r ← 2r − r²·b≫…), routing every large multiply through the chunked grid / SafeMpzMul
    ''' so GMP's FFT is never handed an over-large operand. Seeded with a ~126-bit estimate and exited
    ''' by a sound r-stability detector (§272).
    ''' </summary>
    ''' <param name="r">Receives the reciprocal.</param>
    ''' <param name="b">Divisor.</param>
    ''' <param name="kBits">Fixed-point scale — the reciprocal is of 2^kBits.</param>
    ''' <remarks>
    ''' CONTRACT: r is always a strict UNDERESTIMATE of floor(2^kBits/b) (the §107 invariant — the
    ''' high-product short-muls round up, so r never overshoots). <see cref="SafeMpzDiv"/> depends on
    ''' this and corrects the resulting quotient to the exact value with its §171/§218 adjust loop.
    ''' Convergence must be gated only on real r-stability, NEVER on the precision schedule (#93).
    ''' </remarks>
    Private Shared Sub SafeMpzReciprocal(r As mpz_t, b As mpz_t, kBits As Long)
        Const SAFE As Integer = GMP_FFT_LIMB_CAP   ' §111 (#111): named GMP-FFT 32-bit limb cap
        ' §250 (#94): read the high-half short-product flags once per reciprocal call.
        ' §254 (#70): chunked-grid reciprocal is now ON BY DEFAULT (opt-out with PI_RECIP_SHORTMUL=0).
        ' Validated: 1B π bit-identical to oracle, 500M VERIFY all-OK incl bShift>0, 5B engages cleanly
        ' (259.5M² ~33min/iter), harness 2.81×(rSq)/6.97×(p) vs §gen-DOP9.  Only routes the reciprocal
        ' capped-iter muls (RecipMul); the gate (size>SAFE + DOP≤MAXDOP) fires it only where it wins.
        _recipShortMul = (Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL") <> "0")
        _recipShortMulVerify = (Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL_VERIFY") = "1")   ' =1 cross-checks each chunked RecipMul vs §gen (default off)
        Dim _rsmDopEnv As String = Environment.GetEnvironmentVariable("PI_RECIP_SHORTMUL_MAXDOP")  ' §251 (#70) DOP gate
        Dim _rsmDopParsed As Integer
        If _rsmDopEnv IsNot Nothing AndAlso Integer.TryParse(_rsmDopEnv, _rsmDopParsed) AndAlso _rsmDopParsed >= 1 Then _recipShortMulMaxDop = _rsmDopParsed Else _recipShortMulMaxDop = 9
        _divArShortMul = (Environment.GetEnvironmentVariable("PI_DIV_AR_SHORTMUL") <> "0")   ' §262 (#42): chunked-HIGH a×r, opt-out
        _divQbChunked = (Environment.GetEnvironmentVariable("PI_DIV_QB_CHUNKED") <> "0")      ' §269 (#88): chunked-full q×b, opt-out
        ' §276 (#125): reciprocal-checkpoint cadence (default every 4 iters; 1 = every iteration).
        Dim _nrceEnv As String = Environment.GetEnvironmentVariable("PI_NR_CKPT_EVERY")
        Dim _nrceParsed As Integer
        If _nrceEnv IsNot Nothing AndAlso Integer.TryParse(_nrceEnv, _nrceParsed) AndAlso _nrceParsed >= 1 Then _nrCkptEvery = _nrceParsed Else _nrCkptEvery = 4
        ' §174-fix: mpz_sizeinbase returns UInt32 — overflows when bBits > 2^31 (szB > 33M limbs).
        ' Compute exact bBits from top limb via CLZ; avoids any overflow and is always precise.
        Dim _szB As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(b.Pointer, 4))
        Dim _bDataPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(b.Pointer, 8))
        Dim _topLimbPtr As IntPtr = New IntPtr(_bDataPtr.ToInt64() + CLng(_szB - 1) * 8L)
        Dim _topLimb As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_topLimbPtr))
        Dim _topLimbBits As Integer = 64 - System.Numerics.BitOperations.LeadingZeroCount(_topLimb)
        Dim bBits As Long = CLng(_szB - 1) * 64L + CLng(_topLimbBits)
        Dim rBits As Long = kBits - bBits + 1L   ' r has at most rBits significant bits
        If rBits <= 0L Then
            gmp_lib.mpz_set_ui(r, 0UI)
            Return
        End If

        ' §201-raise: check for a prior converged Newton r at smaller scale.
        ' If found at kBits ≈ new_kBits / 2, load and left-shift to use as a high-precision
        ' seed for the new Newton. With prior_rBits worth of correct precision, only ~3-5
        ' iters are needed (vs ~37 from a 1-bit seed via prec doubling).
        Dim _raiseUsed As Boolean = False
        Dim _exactReuse As Boolean = False   ' §230 (issue #81): set when saved r is bit-identical to the new call's r
        Dim _raisePriorRBits As Long = 0L
        Dim _nrSnapDirRaise As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _nrRaiseBin As String = System.IO.Path.Combine(_nrSnapDirRaise, "nr_raise.bin")
        Dim _nrRaiseMeta As String = System.IO.Path.Combine(_nrSnapDirRaise, "nr_raise_meta.txt")
        If _autoCheckpoint AndAlso System.IO.File.Exists(_nrRaiseBin) AndAlso System.IO.File.Exists(_nrRaiseMeta) Then
            Try
                Dim _rmLines As String() = System.IO.File.ReadAllLines(_nrRaiseMeta)
                Dim _rmDict As New Dictionary(Of String, String)()
                For Each _ml As String In _rmLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _rmDict(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _priorKBits As Long = 0L, _priorBBits As Long = 0L, _priorRBits As Long = 0L
                If _rmDict.ContainsKey("kBits") AndAlso Long.TryParse(_rmDict("kBits"), _priorKBits) AndAlso
                   _rmDict.ContainsKey("bBits") AndAlso Long.TryParse(_rmDict("bBits"), _priorBBits) AndAlso
                   _rmDict.ContainsKey("rBits") AndAlso Long.TryParse(_rmDict("rBits"), _priorRBits) Then
                    ' §230 (issue #81, 2026-05-23): exact-scale fast-path.  When the saved r is at
                    ' the IDENTICAL (kBits, bBits, rBits) AND the divisor b is bit-identical
                    ' (verified via SHA-256 bSig stored in meta), the saved r is already the
                    ' correct reciprocal — load it and skip Newton entirely.  Without this
                    ' fast-path, the pre-§230 ratio check (0.4..0.7) rejects ratio=1.000 and
                    ' Newton runs from scratch (~30 min at 1B for the post-§225 deterministic
                    ' Phase 4 reciprocal).  bSig guards against the unlikely case where a
                    ' different divisor shares the same bit-length: without it, blindly using
                    ' the saved r would silently produce a wrong reciprocal.
                    Dim _priorScopeForExact As String = If(_rmDict.ContainsKey("scope"), _rmDict("scope"), "")
                    Dim _curScopeForExact As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                    Dim _scopeOkForExact As Boolean = (_priorScopeForExact.StartsWith("sqrt_step_") AndAlso _curScopeForExact.StartsWith("sqrt_step_")) OrElse
                                                       (_priorScopeForExact.Length > 0 AndAlso _priorScopeForExact = _curScopeForExact)
                    If _scopeOkForExact AndAlso _priorKBits = kBits AndAlso _priorBBits = bBits AndAlso _priorRBits = rBits AndAlso _rmDict.ContainsKey("bSig") Then
                        Dim _priorBSig As String = _rmDict("bSig").Trim().ToLowerInvariant()
                        AppendLog($"[SafeMpzReciprocal§230] exact-scale match candidate (scope={_priorScopeForExact} kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0}); verifying bSig{vbCrLf}")
                        Dim _t230Start As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                        Dim _curBSig As String = ComputeMpzSig(b)
                        Dim _t230SigSec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t230Start) / System.Diagnostics.Stopwatch.Frequency
                        If _curBSig = _priorBSig Then
                            ' bSig confirms saved r is for THIS exact b — load r and signal the
                            ' downstream §NR-ckpt + Newton gates to short-circuit (no Newton work).
                            Dim _staging230(4194303) As Byte
                            Using _fs230 As New FileStream(_nrRaiseBin, FileMode.Open, FileAccess.Read)
                                Using _br230 As New BinaryReader(_fs230)
                                    DeserializeOneMpz(r, _br230, _staging230)
                                End Using
                            End Using
                            _raiseUsed = True
                            _exactReuse = True
                            _raisePriorRBits = _priorRBits
                            AppendLog($"[SafeMpzReciprocal§230] EXACT-REUSE: bSig verified in {_t230SigSec:F2}s; loaded saved r directly — Newton skipped{vbCrLf}")
                        Else
                            AppendLog($"[SafeMpzReciprocal§230] bSig mismatch after {_t230SigSec:F2}s (cur={_curBSig.Substring(0, 16)}... prior={_priorBSig.Substring(0, System.Math.Min(16, _priorBSig.Length))}...) — falling through to existing §201-raise logic{vbCrLf}")
                        End If
                    End If

                    ' §225 (issue #80, 2026-05-22): scope-compatibility gate.
                    ' Pre-§225 the kBits ratio check alone was used, on the assumption that
                    ' the saved r came from a structurally similar divisor. That assumption
                    ' holds between consecutive sqrt-Newton steps (xTrunc only changes in
                    ' low-precision bits across step transitions), but FAILS across the
                    ' sqrt-Newton → phase4 transition: phase4's finalT = T·sqrt(N) is
                    ' structurally unrelated to xTrunc, so the loaded r is garbage as a
                    ' seed and the §201-raise _minNrIters=5 override (vs §200's ~35) leaves
                    ' Newton far short of converged. Empirical: 1B run 2026-05-22 produced
                    ' a wrong r at phase4 → adj-up hit MAX_ADJ_ITERS → §218+§171 entered →
                    ' §171 grind at Δ=1 limb/pass would have throw at 64-pass hard cap.
                    Dim _priorScope As String = If(_rmDict.ContainsKey("scope"), _rmDict("scope"), "")
                    Dim _curScope As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                    Dim _scopeOk As Boolean = (_priorScope.StartsWith("sqrt_step_") AndAlso _curScope.StartsWith("sqrt_step_")) OrElse
                                              (_priorScope.Length > 0 AndAlso _priorScope = _curScope)
                    Dim _ratio As Double = If(kBits > 0L, CDbl(_priorKBits) / CDbl(kBits), 0.0)
                    If _scopeOk AndAlso _ratio > 0.4 AndAlso _ratio < 0.7 AndAlso _priorRBits > 0L AndAlso _priorRBits < rBits Then
                        Dim _staging(4194303) As Byte
                        Using _fs As New FileStream(_nrRaiseBin, FileMode.Open, FileAccess.Read)
                            Using _br As New BinaryReader(_fs)
                                DeserializeOneMpz(r, _br, _staging)
                            End Using
                        End Using
                        Dim _scaleShift As Long = rBits - _priorRBits
                        If _scaleShift > 0L Then
                            BigShiftLeft(r, r, _scaleShift)
                        End If
                        AppendLog($"[SafeMpzReciprocal] §201-raise: loaded prior r (priorScope={_priorScope} priorKBits={_priorKBits:N0} priorRBits={_priorRBits:N0}), scaled by 2^{_scaleShift:N0} → seed for Newton (curScope={_curScope} kBits={kBits:N0} rBits={rBits:N0}){vbCrLf}")
                        _raiseUsed = True
                        _raisePriorRBits = _priorRBits
                    ElseIf Not _scopeOk Then
                        ' §225: scope mismatch — saved r is for a different divisor family.
                        ' Discard the stale file so future calls don't trip the same check.
                        AppendLog($"[SafeMpzReciprocal§225] scope mismatch — skipping §201-raise and deleting stale nr_raise.bin (priorScope='{_priorScope}' curScope='{_curScope}' priorKBits={_priorKBits:N0} newKBits={kBits:N0}){vbCrLf}")
                        Try
                            System.IO.File.Delete(_nrRaiseBin)
                            System.IO.File.Delete(_nrRaiseMeta)
                        Catch _exDel As Exception
                            AppendLog($"[SafeMpzReciprocal§225] stale-file delete failed ({_exDel.Message}) — will retry on next call{vbCrLf}")
                        End Try
                    Else
                        AppendLog($"[SafeMpzReciprocal] §201-raise: prior found but ratio={_ratio:F3} or rBits mismatch — skipping raise (priorScope={_priorScope} priorKBits={_priorKBits:N0} priorRBits={_priorRBits:N0} newKBits={kBits:N0} newRBits={rBits:N0}){vbCrLf}")
                    End If
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §201-raise load failed ({_ex.Message}) — falling back to fresh seed{vbCrLf}")
                _raiseUsed = False
            End Try
        End If

        ' ── Seed: ~126-bit approximation from the top 128 bits of b ────────
        ' Skipped if §201-raise loaded a prior r as the seed.
        ' §272 (#88): the old seed used numerator 2^64 against the top 64 bits of b (bHi ≈ 2^63),
        ' so floor(2^64 / bHi) ≈ 2 — a 1-2 BIT quotient, NOT the ~62-bit reciprocal the prec
        ' schedule (which starts at 62) was designed for.  --test-recipconv measured SEED
        ' correctBits = 2 and correct-bits doubling from 2 (≈2^iter), which is exactly why §200
        ' had to force min_nrIters = ceil(log2(rBits))+3 extra full-width iters to converge.
        ' A P-bit quotient needs numerator 2^(bitlen(bHi)+P): keep the top SEED_BBITS=128 bits of
        ' b and divide 2^(SEED_BBITS+SEED_PREC) by bHi to get a genuine ~SEED_PREC=126-bit seed.
        ' The underestimate invariant is preserved for ANY numerator: bHi = ceil(b/2^bHiShift) ≥
        ' b/2^bHiShift ⟹ floor(2^N/bHi) ≤ 2^N·2^bHiShift/b ⟹ r ≤ 2^kBits/b after scaling.  With
        ' accuracy now ~126 ≥ the prec-schedule's 62, accuracy tracks prec and the Newton loop
        ' converges ~9 iters sooner (the §272 detector below exits on real r-stability).
        If Not _raiseUsed Then
            Const SEED_BBITS As Long = 128L   ' bits of b retained in bHi
            Const SEED_PREC As Long = 126L    ' target correct bits in the seed (≤ SEED_BBITS−2)
            Dim bHiShift As Long = System.Math.Max(0L, bBits - SEED_BBITS)
            Dim bHi As New mpz_t()
            gmp_lib.mpz_init(bHi)
            If bHiShift > 0L Then
                BigShiftRight(bHi, b, bHiShift)
                gmp_lib.mpz_add_ui(bHi, bHi, 1UI)   ' ceiling → underestimate of reciprocal guaranteed
            Else
                GmpRaw_set(bHi.Pointer, b.Pointer)  ' §35
            End If
            ' rSeed = floor(2^(SEED_BBITS+SEED_PREC) / bHi)  [both operands ≤ ~256 bits ⇒ tiny/fast]
            Dim rSeed As New mpz_t()
            gmp_lib.mpz_init(rSeed)
            gmp_lib.mpz_set_ui(rSeed, 1UI)
            gmp_lib.mpz_mul_2exp(rSeed, rSeed, New mp_bitcnt_t(CUInt(SEED_BBITS + SEED_PREC)))
            GmpRaw_tdiv_q(rSeed.Pointer, rSeed.Pointer, bHi.Pointer)  ' §35
            gmp_lib.mpz_clear(bHi)
            ' Scale to r's domain: rSeed * 2^(kBits-(SEED_BBITS+SEED_PREC)-bHiShift) ≈ 2^kBits / b
            Dim seedScale As Long = kBits - (SEED_BBITS + SEED_PREC) - bHiShift
            If seedScale > 0L Then
                BigShiftLeft(rSeed, rSeed, seedScale)
            ElseIf seedScale < 0L Then
                BigShiftRight(rSeed, rSeed, -seedScale)
            End If
            ' §35: mpz_sgn is a GMP macro — read _mp_size field (offset +4) directly.
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(rSeed.Pointer, 4)) > 0 Then gmp_lib.mpz_sub_ui(rSeed, rSeed, 2UI)
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(rSeed.Pointer, 4)) <= 0 Then gmp_lib.mpz_set_ui(rSeed, 1UI)
            GmpRaw_swap(r.Pointer, rSeed.Pointer)  ' §35
            gmp_lib.mpz_clear(rSeed)
        End If

        ' §NR-ckpt: Resume from a mid-NR checkpoint if one exists for this exact kBits/bBits.
        ' Saves r (the reciprocal estimate) and prec so a crash during a later NR iteration
        ' does not require restarting from the seed.  bTrunc is re-derived each iteration
        ' from b, so only r + prec need to be saved.
        Dim _nrSnapDir As String = System.IO.Path.Combine(DISK_CACHE_DIR, "snap_Phase3")
        Dim _nrBin As String = System.IO.Path.Combine(_nrSnapDir, "nr_r.bin")
        Dim _nrMeta As String = System.IO.Path.Combine(_nrSnapDir, "nr_meta.txt")
        ' §201-raise: when raised, prec starts already at priorRBits+2 (Newton's seed has
        ' priorRBits worth of correct bits).  Otherwise default to 62 (1-bit ε from rSeed).
        Dim prec As Long = If(_raiseUsed, _raisePriorRBits + 2L, 62L)
        Dim _resumedIter As Long = 0L  ' §200: iter count from a resumed §NR-ckpt; 0 if no resume
        ' §NR-ckpt match check (_snapKBits=kBits) takes precedence over §201-raise when both
        ' apply: a matching mid-Newton snapshot is more recent than any prior-scale raise.
        ' §230: an exact-reuse load above already gives r at full precision — skip §NR-ckpt
        ' resume entirely (it would overwrite the loaded r with a mid-Newton snapshot).
        If Not _exactReuse AndAlso _autoCheckpoint AndAlso System.IO.File.Exists(_nrBin) AndAlso System.IO.File.Exists(_nrMeta) Then
            Try
                Dim _metaLines As String() = System.IO.File.ReadAllLines(_nrMeta)
                Dim _meta As New Dictionary(Of String, String)()
                For Each _ml As String In _metaLines
                    Dim _eq As Integer = _ml.IndexOf("="c)
                    If _eq > 0 Then _meta(_ml.Substring(0, _eq)) = _ml.Substring(_eq + 1)
                Next
                Dim _snapKBits As Long = 0L, _snapBBits As Long = 0L, _snapPrec As Long = 0L, _snapIter As Long = 0L
                If _meta.ContainsKey("kBits") AndAlso Long.TryParse(_meta("kBits"), _snapKBits) AndAlso
                   _meta.ContainsKey("bBits") AndAlso Long.TryParse(_meta("bBits"), _snapBBits) AndAlso
                   _meta.ContainsKey("prec")  AndAlso Long.TryParse(_meta("prec"),  _snapPrec)  AndAlso
                   _snapKBits = kBits AndAlso _snapBBits = bBits AndAlso _snapPrec > 62L Then
                    ' §200: also parse iter so resumed Newton can continue from the right iter count.
                    If _meta.ContainsKey("iter") Then Long.TryParse(_meta("iter"), _snapIter)
                    _resumedIter = _snapIter
                    Dim _nrStaging(4194303) As Byte
                    Using _fs As New FileStream(_nrBin, FileMode.Open, FileAccess.Read)
                        Using _br As New BinaryReader(_fs)
                            DeserializeOneMpz(r, _br, _nrStaging)
                        End Using
                    End Using
                    prec = _snapPrec
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt resumed: prec={prec:N0} bBits={bBits:N0} kBits={kBits:N0}{vbCrLf}")
                End If
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §NR-ckpt load failed ({_ex.Message}) — starting from seed{vbCrLf}")
                prec = 62L
            End Try
        End If

        ' §272 (#88): record the seed's correct-bit count for the --test-recipconv probe.
        ' Null-check no-op in production (only the harness sets _recipConvRef).  If the seed shows
        ' ~62 correct bits the §200 forced-tail iters are largely wasted (early-exit recoverable);
        ' if it shows ~1 bit the seed scaling is lossy and a better seed is the real lever.
        If _recipConvRef IsNot Nothing Then
            AppendLog($"[RecipConv§272] SEED correctBits={RecipConv_CorrectBits(r, rBits):N0}/{rBits:N0} (kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0}){vbCrLf}", 1)
        End If

        ' ── Newton: r ← 2r - ceil(b/2^bShift) · r² / 2^(kBits-bShift) ────
        ' Progressive precision: prec doubles each step from ~62 → rBits+2.
        ' Ceiling truncation of b maintains r as a strict underestimate throughout.
        ' §36: Allocate bTrunc/rSq/p once outside the loop — eliminates ~18 large
        '      VirtualAlloc/Free per sqrt call (each Newton step would otherwise init
        '      and clear these, each touching the allocator twice for large operands).
        Dim bTrunc As New mpz_t()
        gmp_lib.mpz_init(bTrunc)
        Dim rSq As New mpz_t()
        gmp_lib.mpz_init(rSq)
        Dim p As New mpz_t()
        gmp_lib.mpz_init(p)
        ' prec is declared and initialised in the §NR-ckpt block above (default 62L, or restored value).
        Dim _nrIter As Integer = CInt(_resumedIter)  ' §200: continue from the resumed iter count
        ' §200 (2026-04-29): Newton must iterate until full precision is reached.  The seed has
        ' relative error ε_0 < 1/2 (since rSeed ≈ r_true / 2).  With Newton's quadratic convergence
        ' (ε_n = ε_0^(2^n)), reaching ε ≤ 2^-rBits requires n ≥ log2(rBits) iterations — about 33
        ' for rBits ≈ 5.6B.  The original loop terminated at prec >= rBits+2 (which happens after
        ' 27 iters when prec doubles from 62), but that is FEWER iterations than Newton's true
        ' convergence needs.  Result: r is short by ~2^(rBits - 2^27) ≈ 2^5.45B (verified
        ' empirically via Option H r×b chunked-grid logging).  Fix: require min_nrIters =
        ' ceil(log2(rBits)) + 3 slack iterations.  Subsequent iters at capped prec use bShift=0
        ' (full b), which keeps doubling Newton's precision until ε is below 2^-rBits.
        ' §201-raise: when seed already has priorRBits ≈ rBits/2 worth of correct bits,
        ' Newton's quadratic convergence reaches full precision in 1-2 iters.  Use 5 for
        ' headroom (covers seed scaling rounding + convergence slack).  Without raising,
        ' the seed has only 1 bit of precision, so log2(rBits)+3 iters are required.
        Dim _minNrIters As Integer = If(_raiseUsed, 5, CInt(System.Math.Ceiling(System.Math.Log(System.Math.Max(2L, rBits), 2))) + 3)
        ' §242 (issue #93 cand 1): bTrunc cache.  bShift = max(0, bBits-prec-2) DECREASES while
        ' prec doubles (iters 1..~27), then is CONSTANT once prec caps at rBits+2 (iters 28-37;
        ' at the 5B final divide bShift ≈ 30.7 Gbit).  Since b is constant too, floor(b/2^bShift)
        ' is bit-identical across the capped iters, yet BigShiftRight (a chunked, ~0.35-core,
        ' bandwidth-bound truncation of the ~47 Gbit b — minutes per iter) recomputes it each
        ' time.  Track the last bShift and skip the recompute when unchanged.  Safe: bTrunc is
        ' read-only after it is set (only consumed by p = bTrunc·rSq).
        Dim _prevBShift As Long = -1L
        ' §230: exact-reuse loaded r at full precision — skip the Newton loop body entirely.
        Do While Not _exactReuse AndAlso (prec < rBits + 2L OrElse _nrIter < _minNrIters)
            _nrIter += 1
            prec = System.Math.Min(prec * 2L + 4L, rBits + 2L)
            ' §253 (#52): surface reciprocal-Newton progress in the UI status box (not gated by
            ' _logLevel — it's the UI).  Shows iter / target-iters and precision reached.
            If _statusHook IsNot Nothing Then _statusHook($"Reciprocal Newton: iter {_nrIter}/{_minNrIters}  prec {prec:N0}/{rBits + 2L:N0} bits")
            ' §259 (#62): refine the Divide-stage ETA from the §200 fixed iteration schedule.
            If _etaReciprocalHook IsNot Nothing Then _etaReciprocalHook(_nrIter, _minNrIters)

            ' §107-fix: do NOT truncate r.  Keep r in full domain (magnitude ~2^rBits).
            ' The shift formula kBits-bShift is calibrated for r at full domain.
            ' Truncating r to prec bits (as the old code did) reduces r from ~2^rBits
            ' to ~2^prec, causing p→0 (early iters) or p>>2r (overshoot), both wrong.
            ' With r kept full-size, bTrunc's increasing width drives progressive precision.

            ' bTrunc = floor(b / 2^bShift), bShift = max(0, bBits - prec - 2)
            ' §107: Use floor (not ceiling) truncation.  The +1 ceiling added to bTrunc
            ' introduces an extra error of r²/2^(kBits-bShift) ≈ 2^(bShift+1)*r/R into p.
            ' In the final Newton iteration bShift is small (~56 bits for sqrt inputs), so
            ' this extra term ≈ 2^57*r/R dwarfs the true Newton correction e²/R (which is
            ' near zero at convergence), pushing p > 2r and making r = 2r-p go deeply
            ' negative.  The guard then resets r=1 and the loop exits with r ≈ 2^29 (3
            ' limbs) instead of the correct ~21.875M-limb reciprocal.
            ' Floor is safe: any slight overestimate of r is corrected by SafeMpzDiv's
            ' adjustment loop (which already handles q too-large by decrementing).
            Dim bShift As Long = System.Math.Max(0L, bBits - prec - 2L)
            If bShift = _prevBShift Then
                ' §242 (#93): bShift unchanged since last iter and b is constant, so bTrunc
                ' already holds the correct floor(b/2^bShift) — skip the recompute.  This is
                ' the win: on capped-prec iters (28-37 at 5B) it elides a minutes-long,
                ' bandwidth-bound BigShiftRight.  bTrunc is read-only after this block.
                If _logLevel >= 2 Then AppendLog($"[SafeMpzReciprocal§242] iter={_nrIter} bShift={bShift:N0} unchanged — reusing cached bTrunc (BigShiftRight skipped){vbCrLf}")
            ElseIf bShift > 0L Then
                BigShiftRight(bTrunc, b, bShift)
                ' No ceiling +1: floor truncation avoids catastrophic overshoot in final step.
                _prevBShift = bShift
            Else
                ' §PreAlloc-bTrunc: bTrunc._mp_alloc from prior BigShiftRight may be < _szB.
                ' GMP's __gmpz_realloc aborts when new_alloc > 33,554,431 limbs (INT_MAX/64,
                ' 32-bit mp_size_t overflow check fires BEFORE our GmpReallocFunc callback).
                ' Pre-allocate via our pool to bypass it, same pattern as BigShiftRight/BigShiftLeft.
                PreAllocMpzToLimbs(bTrunc, CLng(_szB))
                GmpRaw_set(bTrunc.Pointer, b.Pointer)  ' §35
                _prevBShift = 0L
            End If

            ' §127: log r[20,904,662..665] BEFORE rSq — this is r_24 (input to final Newton step)
            ' §147: extend to 20,904,662..663 — unverified range, hypothesized error site
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz127 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
                Const _idx127 As Integer = 20_904_664
                Dim _r127DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r127Vm2 As Long = If(_sz127 > _idx127 - 2, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 - 2) * 8L), 0L)
                Dim _r127Vm1 As Long = If(_sz127 > _idx127 - 1, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 - 1) * 8L), 0L)
                Dim _r127Val As Long = If(_sz127 > _idx127, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127) * 8L), 0L)
                Dim _r127Val1 As Long = If(_sz127 > _idx127 + 1, Runtime.InteropServices.Marshal.ReadInt64(_r127DPtr, CLng(_idx127 + 1) * 8L), 0L)
                AppendLog($"[NR127] iter={_nrIter} r_before_rSq[{_idx127-2:N0}]={_r127Vm2:X16} [{_idx127-1:N0}]={_r127Vm1:X16} [{_idx127:N0}]={_r127Val:X16} [{_idx127+1:N0}]={_r127Val1:X16} sz={_sz127:N0}{vbCrLf}")
            End If
            ' rSq = r²
            Dim szR As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            ' §129: verify r and rSq pointers are valid before rSq=r² call
            If _logLevel >= 2 Then
                Dim _r129Alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 0)
                Dim _rSq129Alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 0)
                Dim _rSq129Sz As Integer = Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4)
                Dim _bTrunc129Sz As Integer = Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4)
                AppendLog($"[NR§129] iter={_nrIter} szR={szR:N0} r_alloc={_r129Alloc:N0} rSq_alloc={_rSq129Alloc:N0} rSq_sz={_rSq129Sz:N0} bTrunc_sz={_bTrunc129Sz:N0}{vbCrLf}")
            End If
            If CLng(szR) * 2L <= SAFE Then
                GmpRaw_mul(rSq.Pointer, r.Pointer, r.Pointer)
            ElseIf _recipShortMul AndAlso MemBudget_SuggestSafeMulDop(CLng(szR), CLng(szR)) <= _recipShortMulMaxDop Then
                ' §250/§251 (#94/#70): chunked-grid high product for the large reciprocal squaring.
                ' Engages on SIZE (already past szR·2 ≤ SAFE) + low §gen DOP — NOT bShift=0.  §251-fix:
                ' at the 5B DIVIDE the denominator bBits (≈47e9) >> rBits (≈16.6e9), so bShift stays
                ' ≈30e9 and NEVER reaches 0 — the old bShift=0 gate wrongly excluded the 5B reciprocal.
                ' r² is 2·szR limbs; keep top szR + margin (overestimate-rounded), skipping the low half.
                If _logLevel >= 2 Then AppendLog($"[§251-gate] rSq iter={_nrIter} ENGAGE szR={szR:N0} prec={prec:N0} rBits={rBits:N0} bShift={bShift:N0} dop={MemBudget_SuggestSafeMulDop(CLng(szR), CLng(szR))}{vbCrLf}")
                RecipMul(rSq, r, r, CLng(szR) + 4096L, "rSq", _nrIter)
            Else
                SafeMpzMul(rSq, r, r)
            End If
            ' §121: log rSq top+bot at final iteration to verify r×r correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz121 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
                Dim _rSq121DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rSq.Pointer, 8))
                Dim _rSq121B0 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 0), 0L)
                Dim _rSq121B1 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_rSq121DPtr, 8), 0L)
                ' §237 (issue #86): compute the absolute limb address in Int64.  Old `(sz - 1) * 8`
                ' overflowed Int32 at 5 B when sz > 2^28 (~268 M limbs) → AV in Marshal.ReadInt64.
                Dim _rSq121T1 As Long = If(_sz121 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rSq121DPtr.ToInt64() + (CLng(_sz121) - 1L) * 8L), 0), 0L)
                Dim _rSq121T0 As Long = If(_sz121 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rSq121DPtr.ToInt64() + (CLng(_sz121) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR121] iter={_nrIter} rSq sz={_sz121:N0} bot=[{_rSq121B0:X16} {_rSq121B1:X16}] top=[{_rSq121T0:X16} {_rSq121T1:X16}]{vbCrLf}")
            End If
            ' §126: log rSq[20,904,662..663] at final iter — B2[6,321,328] of rSq feeds into p[64,654,664]
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz126 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
                Const _idx126 As Integer = 20_904_662
                Dim _rSq126DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(rSq.Pointer, 8))
                Dim _rSq126Val As Long = If(_sz126 > _idx126, Runtime.InteropServices.Marshal.ReadInt64(_rSq126DPtr, CLng(_idx126) * 8L), 0L)
                Dim _rSq126Val1 As Long = If(_sz126 > _idx126 + 1, Runtime.InteropServices.Marshal.ReadInt64(_rSq126DPtr, CLng(_idx126 + 1) * 8L), 0L)
                AppendLog($"[NR126] iter={_nrIter} rSq[{_idx126:N0}]={_rSq126Val:X16} rSq[{_idx126 + 1:N0}]={_rSq126Val1:X16} sz={_sz126:N0}{vbCrLf}")
            End If

            ' p = bTrunc · rSq
            Dim szBt As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4))
            Dim szRsq As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(rSq.Pointer, 4))
            If CLng(szBt) + CLng(szRsq) <= SAFE Then
                GmpRaw_mul(p.Pointer, bTrunc.Pointer, rSq.Pointer)
            ElseIf _recipShortMul AndAlso MemBudget_SuggestSafeMulDop(CLng(szBt), CLng(szRsq)) <= _recipShortMulMaxDop Then
                ' §250/§251 (#94/#70): chunked-grid high product for p = bTrunc·rSq.  Engages on SIZE
                ' + low §gen DOP — NOT bShift=0 (§251-fix; see rSq site).  p is shifted right by
                ' (kBits−bShift); keep the surviving top limbs + margin (overestimate ⟹ r underestimate).
                ' keepP uses bShift, so it's correct for the 5B bShift≈30e9 regime too.
                Dim _keepP As Long = CLng(szBt) + CLng(szRsq) - (kBits - bShift) \ 64L + 4096L
                If _logLevel >= 2 Then AppendLog($"[§251-gate] p iter={_nrIter} ENGAGE szBt={szBt:N0} szRsq={szRsq:N0} keepP={_keepP:N0} bShift={bShift:N0} dop={MemBudget_SuggestSafeMulDop(CLng(szBt), CLng(szRsq))}{vbCrLf}")
                RecipMul(p, bTrunc, rSq, _keepP, "p", _nrIter)
            Else
                SafeMpzMul(p, bTrunc, rSq)
            End If
            ' §122: log p top+bot before BigShiftRight at final iteration to verify b×rSq correctness
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz122 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Dim _p122DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p122B0 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 0), 0L)
                Dim _p122B1 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p122DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _p122T1 As Long = If(_sz122 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p122DPtr.ToInt64() + (CLng(_sz122) - 1L) * 8L), 0), 0L)
                Dim _p122T0 As Long = If(_sz122 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p122DPtr.ToInt64() + (CLng(_sz122) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR122] iter={_nrIter} p_before_shift sz={_sz122:N0} bot=[{_p122B0:X16} {_p122B1:X16}] top=[{_p122T0:X16} {_p122T1:X16}]{vbCrLf}")
            End If
            ' §125: log p[64,654,663..665] before BigShiftRight
            ' p[64654663] maps to p_shifted[20904663]; p[64654664] maps to p_shifted[20904664]
            ' §147: add 64654663 to verify p_shifted[20904663] = (p[64654663]>>27)|(p[64654664]<<37)
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz125 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Const _idx125 As Integer = 64_654_664
                Dim _p125DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p125Vm1 As Long = If(_sz125 > _idx125 - 1, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125 - 1) * 8L), 0L)
                Dim _p125Val As Long = If(_sz125 > _idx125, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125) * 8L), 0L)
                Dim _p125Val1 As Long = If(_sz125 > _idx125 + 1, Runtime.InteropServices.Marshal.ReadInt64(_p125DPtr, CLng(_idx125 + 1) * 8L), 0L)
                Dim _p125Exp As Long = CLng(CULng(_p125Vm1) >> 27) Or (_p125Val << 37)  ' expected p_shifted[20904663] — unsigned shift to match GMP
                AppendLog($"[NR125] iter={_nrIter} p_before_shift[{_idx125-1:N0}]={_p125Vm1:X16} [{_idx125:N0}]={_p125Val:X16} [{_idx125+1:N0}]={_p125Val1:X16} sz={_sz125:N0} expected_psh[{_idx125-43750001:N0}]={_p125Exp:X16}{vbCrLf}")
            End If

            ' p >>= (kBits - bShift);  r = 2r - p
            ' §107-fix: revert to kBits-bShift.  This formula is correct when r is in
            ' "full domain" (magnitude ~2^rBits).  The bug was the truncation above
            ' which reduced r to ~2^prec, invalidating the shift.  With r kept at
            ' full domain the formula converges correctly.
            BigShiftRight(p, p, kBits - bShift)
            ' §120: log p top+bot after BigShiftRight (only at bShift=0, i.e. final iter)
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz120 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Dim _p120DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p120B0 As Long = If(_sz120 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, 0), 0L)
                Dim _p120B1 As Long = If(_sz120 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p120DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _p120T1 As Long = If(_sz120 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p120DPtr.ToInt64() + (CLng(_sz120) - 1L) * 8L), 0), 0L)
                Dim _p120T0 As Long = If(_sz120 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p120DPtr.ToInt64() + (CLng(_sz120) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR120] iter={_nrIter} p_after_shift: sz={_sz120:N0} bot=[{_p120B0:X16} {_p120B1:X16}] top=[{_p120T0:X16} {_p120T1:X16}]{vbCrLf}")
            End If
            ' §123: log p[20,904,662..665] after BigShiftRight — §147: add 20904662..663 for cross-check with §125
            ' Verify: p_shifted[20904663] should == (p_before[64654663]>>27)|(p_before[64654664]<<37) from §125
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz123 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4))
                Const _idx123 As Integer = 20_904_664
                Dim _p123DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(p.Pointer, 8))
                Dim _p123Vm2 As Long = If(_sz123 > _idx123 - 2, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 - 2) * 8L), 0L)
                Dim _p123Vm1 As Long = If(_sz123 > _idx123 - 1, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 - 1) * 8L), 0L)
                Dim _p123Val As Long = If(_sz123 > _idx123, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123) * 8L), 0L)
                Dim _p123Val1 As Long = If(_sz123 > _idx123 + 1, Runtime.InteropServices.Marshal.ReadInt64(_p123DPtr, CLng(_idx123 + 1) * 8L), 0L)
                AppendLog($"[NR123] iter={_nrIter} p_after_shift[{_idx123-2:N0}]={_p123Vm2:X16} [{_idx123-1:N0}]={_p123Vm1:X16} [{_idx123:N0}]={_p123Val:X16} [{_idx123+1:N0}]={_p123Val1:X16} sz={_sz123:N0}{vbCrLf}")
            End If
            ' §272 (#88): sound convergence detector.  At the fixed point r = 2r − p ⟹ p == r, so
            ' r == p means this iteration is a no-op and r is frozen at the iteration's fixed point
            ' (within the §107 ±1-2 ulp that SafeMpzDiv's §171 adjust already corrects).  Compare BEFORE
            ' the 2r−p update (r is still the previous estimate, p is this iter's product).  Gated on
            ' prec having reached its cap (prec >= rBits+2) so it can only fire at FULL target precision
            ' — NOT on bShift, which at the real divide stays ≫0 because bBits ≫ rBits (the §251-fix
            ' regime: bTrunc keeps only the top ~rBits bits of b, the rest don't affect r's rBits sig
            ' bits).  It detects ACTUAL r-stability, never a prec proxy, so it cannot undershoot (while r
            ' still gains low bits, p ≠ r and we iterate).  Self-validating: it exits only when r is
            ' exactly stable, so the result is bit-identical to running the remaining §200 min_nrIters
            ' tail.  §272 also fixed the 1-bit seed ⇒ accuracy now tracks prec and reaches rBits as prec
            ' caps (~9-13 iters before the old min_nrIters floor at 1B/5B).
            Dim _converged272 As Boolean = (prec >= rBits + 2L) AndAlso (GmpRaw_cmp(r.Pointer, p.Pointer) = 0)
            ' §PreAlloc-r-add: After checkpoint restore r._mp_alloc equals _mp_size exactly.
            ' GmpRaw_add(r,r,r) → 2r may need one extra limb → __gmpz_realloc > 33.5M limit → GMP abort.
            ' Pre-allocate 2 extra limbs via our pool to bypass it.
            PreAllocMpzToLimbs(r, CLng(szR) + 2L)
            GmpRaw_add(r.Pointer, r.Pointer, r.Pointer)    ' §NR-raw: r = 2r — bypass managed wrapper pointer corruption
            GmpRaw_sub(r.Pointer, r.Pointer, p.Pointer)    ' §NR-raw: r = 2r - p — bypass managed wrapper pointer corruption
            ' §272 (#88): per-iter convergence probe for --test-recipconv (null-check no-op in production).
            ' Shows whether correct-bits doubles from ~1 (lossy seed) or jumps to ~62 then doubles (good
            ' seed), and whether it plateaus at rBits before _minNrIters (⇒ forced tail iters are wasted).
            If _recipConvRef IsNot Nothing Then
                AppendLog($"[RecipConv§272] iter={_nrIter}/{_minNrIters} prec={prec:N0} bShift={bShift:N0} correctBits={RecipConv_CorrectBits(r, rBits):N0}/{rBits:N0}{vbCrLf}", 1)
            End If
            ' §272 (#88): exit as soon as the full-precision iteration is a proven no-op (r frozen).
            If _converged272 Then
                AppendLog($"[SafeMpzReciprocal§272] converged at iter={_nrIter} (r==p fixed point, prec={prec:N0} rBits={rBits:N0}) — exiting Newton ({_minNrIters - _nrIter} min-iter tail skipped){vbCrLf}", 2)
                Exit Do
            End If
            ' §124: log r[20,904,662..665] immediately after r = 2r - p (final iter)
            ' §147: extend to 20904662..663 — check if r[20904663] has a gross error vs §127/§123
            If _logLevel >= 2 AndAlso bShift = 0 Then
                Dim _sz124 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
                Const _idx124 As Integer = 20_904_664
                Dim _r124DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r124Vm2 As Long = If(_sz124 > _idx124 - 2, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 - 2) * 8L), 0L)
                Dim _r124Vm1 As Long = If(_sz124 > _idx124 - 1, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 - 1) * 8L), 0L)
                Dim _r124Val As Long = If(_sz124 > _idx124, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124) * 8L), 0L)
                Dim _r124Val1 As Long = If(_sz124 > _idx124 + 1, Runtime.InteropServices.Marshal.ReadInt64(_r124DPtr, CLng(_idx124 + 1) * 8L), 0L)
                AppendLog($"[NR124] iter={_nrIter} r_after_sub[{_idx124-2:N0}]={_r124Vm2:X16} [{_idx124-1:N0}]={_r124Vm1:X16} [{_idx124:N0}]={_r124Val:X16} [{_idx124+1:N0}]={_r124Val1:X16} sz={_sz124:N0}{vbCrLf}")
            End If
            If _logLevel >= 3 Then   ' §252 (#95): per-Newton-iter detail → level 3 (sub-phase progress)
                Dim _szR_after As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)
                Dim _szP As Integer = Runtime.InteropServices.Marshal.ReadInt32(p.Pointer, 4)
                ' §119: log r bottom+top 2 limbs at every NR iteration to track lower-bit convergence
                Dim _sz119 As Integer = System.Math.Abs(_szR_after)
                Dim _r119DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
                Dim _r119B0 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 0), 0L)
                Dim _r119B1 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_r119DPtr, 8), 0L)
                ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
                Dim _r119T1 As Long = If(_sz119 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_r119DPtr.ToInt64() + (CLng(_sz119) - 1L) * 8L), 0), 0L)
                Dim _r119T0 As Long = If(_sz119 >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_r119DPtr.ToInt64() + (CLng(_sz119) - 2L) * 8L), 0), 0L)
                AppendLog($"[NR§119] iter={_nrIter} szR={_sz119:N0} bot=[{_r119B0:X16} {_r119B1:X16}] top=[{_r119T0:X16} {_r119T1:X16}]{vbCrLf}")
                AppendLog($"[NR] iter={_nrIter} prec={prec:N0} bShift={bShift:N0} kBitsMinusBShift={kBits - bShift:N0} szP={_szP:N0} szR_after={_szR_after:N0}{vbCrLf}")
            End If

            ' §NR-ckpt: Save r and prec after a Newton iteration so a crash during the NEXT iteration's
            ' SafeMpzMul can resume from here rather than the seed.  r.Pointer is valid here — no managed
            ' GMP call since the GmpRaw_sub above.
            ' §276 (#125): save only every _nrCkptEvery-th iteration (default 4) instead of every one —
            ' this ~2 GB full-width serialize on the compute thread was saturating the disk (~66 GB at
            ' 5B) and stalling compute under low availPhys.  Purely a resume point, so this cannot change
            ' the computed π; a crash loses ≤ _nrCkptEvery−1 iterations of recompute.  iter 0 always saves.
            If _autoCheckpoint AndAlso (_nrIter Mod _nrCkptEvery = 0) Then
                Try
                    If Not System.IO.Directory.Exists(_nrSnapDir) Then
                        System.IO.Directory.CreateDirectory(_nrSnapDir)
                    End If
                    Dim _nrSaveStaging(4194303) As Byte
                    Using _fs As New FileStream(_nrBin, FileMode.Create, FileAccess.Write)
                        Using _bw As New BinaryWriter(_fs)
                            SerializeOneMpz(r, _bw, _nrSaveStaging)
                        End Using
                    End Using
                    System.IO.File.WriteAllText(_nrMeta,
                        $"kBits={kBits}{vbLf}bBits={bBits}{vbLf}prec={prec}{vbLf}iter={_nrIter}{vbLf}")
                    BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt saved: iter={_nrIter} prec={prec:N0}{vbCrLf}")
                Catch _ex As Exception
                    AppendLog($"[SafeMpzReciprocal] §NR-ckpt save failed: {_ex.Message}{vbCrLf}")
                End Try
            End If

            ' Guard: reset if r went non-positive.  With floor truncation (§107) this
            ' should not happen in normal operation.  Retained as a safety net for
            ' pathological seeds only.
            ' §35: mpz_sgn is a GMP macro — read _mp_size field directly.
            If System.Math.Sign(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)) <= 0 Then
                Dim _guardSzR As Integer = Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4)
                Dim _guardSzBTrunc As Integer = Runtime.InteropServices.Marshal.ReadInt32(bTrunc.Pointer, 4)
                Dim _guardMsg As String = $"[SafeMpzReciprocal] GUARD fired: prec={prec:N0} bShift={bShift:N0} szR={_guardSzR:N0} szBTrunc={_guardSzBTrunc:N0} kBits={kBits:N0}" & vbCrLf
                Try : System.IO.File.AppendAllText("C:\PiOutput\guard_debug.txt", _guardMsg) : Catch : End Try
                AppendLog(_guardMsg)
                GmpRaw_set_ui(r.Pointer, 1UI)    ' §NR-raw: bypass managed wrapper
                prec = 1L
            End If

            ' §173-removed: Do NOT zero lower bits of r.  §173 was introduced to fix a
            ' hypothesised "garbage lower bits" problem, but it actually CAUSES the fixed-point
            ' convergence failure: zeroing r's lower bits each step resets them to 0, so the
            ' final iteration starts with lower bits=0 instead of the partially-converged value
            ' from prior steps, causing p_after_shift top = r top (fixed point, szDelta≈42.8M).
            ' Correct behaviour without §173: lower bits of r are "garbage" in early iterations
            ' but they do NOT pollute the upper bits (the garbage contribution to p is confined
            ' to low-order positions after the shift).  Newton converges normally.

            ' §219 (issue #79): drain finalizer queue at the Newton iteration boundary.
            ' Each iteration creates and drops many mpz_t wrappers (rSq, p, sub-products,
            ' etc.); over a multi-hour SafeMpzReciprocal run the finalizer backlog grows
            ' and starts stealing CPU from the single compute thread. Bounded cleanup here.
            DrainFinalizers()
        Loop
        ' §108-diag: log top 4 limbs of r to verify value (not just size)
        If _logLevel >= 2 Then
            Dim _szRFinal As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(r.Pointer, 4))
            Dim _rDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(r.Pointer, 8))
            ' §237 (issue #86): 64-bit safe pointer arithmetic — see comment at §121 site above.
            Dim _rLimb2 As Long = If(_szRFinal >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rDPtr.ToInt64() + (CLng(_szRFinal) - 1L) * 8L), 0), 0L)
            Dim _rLimb1 As Long = If(_szRFinal >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_rDPtr.ToInt64() + (CLng(_szRFinal) - 2L) * 8L), 0), 0L)
            AppendLog($"[SafeMpzReciprocal] done: szR={_szRFinal:N0} top2limbs=[{_rLimb2:X16} {_rLimb1:X16}] kBits={kBits:N0} rBits={rBits:N0}{vbCrLf}")
        End If

        ' §201-raise: save converged r as nr_raise.bin so the NEXT (larger-scale)
        ' SafeMpzReciprocal call can load it as a high-precision seed.  Overwrites any
        ' prior nr_raise.bin — only the most recently converged r is kept.  The next call
        ' decides whether to use it based on the kBits ratio (0.4..0.7).
        If _autoCheckpoint Then
            Try
                If Not System.IO.Directory.Exists(_nrSnapDirRaise) Then
                    System.IO.Directory.CreateDirectory(_nrSnapDirRaise)
                End If
                Dim _raiseSaveStaging(4194303) As Byte
                Using _fs As New FileStream(_nrRaiseBin, FileMode.Create, FileAccess.Write)
                    Using _bw As New BinaryWriter(_fs)
                        SerializeOneMpz(r, _bw, _raiseSaveStaging)
                    End Using
                End Using
                ' §225 (issue #80): write _divCkptScope so the next call can verify the
                ' saved r is for a compatible divisor family (sqrt_step_* across consecutive
                ' steps; same scope otherwise).
                ' §230 (issue #81): also write SHA-256 bSig of b's limbs so the next call's
                ' exact-scale fast-path can verify the saved r is for the IDENTICAL divisor
                ' before bypassing Newton.  Old meta files without bSig: §230 fast-path
                ' won't fire (safe — falls through to existing §201-raise logic).
                Dim _saveScope As String = If(_divCkptScope IsNot Nothing, _divCkptScope, "")
                Dim _t230SigStart As Long = System.Diagnostics.Stopwatch.GetTimestamp()
                Dim _saveBSig As String = ComputeMpzSig(b)
                Dim _t230SigSaveSec As Double = (System.Diagnostics.Stopwatch.GetTimestamp() - _t230SigStart) / System.Diagnostics.Stopwatch.Frequency
                System.IO.File.WriteAllText(_nrRaiseMeta,
                    $"kBits={kBits}{vbLf}bBits={bBits}{vbLf}rBits={rBits}{vbLf}scope={_saveScope}{vbLf}bSig={_saveBSig}{vbLf}")
                BackupSnapshotToStoreAsync("snap_Phase3")  ' §232: async backup off compute critical path
                AppendLog($"[SafeMpzReciprocal] §201-raise: saved converged r (scope={_saveScope} kBits={kBits:N0} bBits={bBits:N0} rBits={rBits:N0} bSig={_saveBSig.Substring(0, 16)}... computed in {_t230SigSaveSec:F2}s) for future raise{vbCrLf}")
            Catch _ex As Exception
                AppendLog($"[SafeMpzReciprocal] §201-raise save failed: {_ex.Message}{vbCrLf}")
            End Try
        End If

        ' §211 (2026-05-15): DEFER §NR-ckpt cleanup until SafeMpzDiv §202-exit fires.
        ' Originally deleted here at end of SafeMpzReciprocal — but that left the entire
        ' post-recip stretch (a×r → BigShiftRight → §171-ckpt save) UNPROTECTED.  Empirical
        ' impact: 5/15 09:55 crash in top-level depth-0 §gen of a×r at 5B scale (kBits=63.9B)
        ' lost iter=37 §NR-ckpt because this delete had already fired, forcing recovery from
        ' the iter=36 backup and re-running the entire 13h Newton loop.  Stale-data concern
        ' from the original cleanup (cross-call kBits mismatch) is already handled by the
        ' §NR-ckpt resume check (_snapKBits = kBits AndAlso _snapBBits = bBits at line ~3352),
        ' so leftover files are safely ignored by future calls.  Actual cleanup moved to
        ' SafeMpzDiv §202-exit, which only fires when the entire divide succeeds — i.e., the
        ' point at which the §NR-ckpt is guaranteed no longer needed.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzReciprocal] §211: deferring §NR-ckpt cleanup to SafeMpzDiv §202-exit (kBits={kBits:N0}){vbCrLf}")

        ' §36: Clear loop-external temporaries once after loop completes.
        gmp_lib.mpz_clears(bTrunc, rSq, p, Nothing)
    End Sub
End Class
