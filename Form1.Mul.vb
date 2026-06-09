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

    ''' <summary>
    ''' GMP's MSVC build uses signed 32-bit mp_size_t.  mpn_mul_fft internally
    ''' computes  pl * GMP_NUMB_BITS  (pl = nl + ml, GMP_NUMB_BITS = 64) as an
    ''' int32 expression.  When pl >= 33,554,432 limbs (= 2^31/64) this product
    ''' overflows int32, corrupting the FFT scratch-size calculation and causing
    ''' GmpAllocFunc to receive an invalid (huge/negative) size.
    '''
    ''' This wrapper detects when szA + szB >= the overflow threshold and falls
    ''' back to a 3-way schoolbook split: each operand is decomposed into three
    ''' pieces of ceil(sz/3) limbs, giving 9 safe sub-products.  Each
    ''' sub-product has at most ceil(szA/3)+ceil(szB/3) limbs = ~(szA+szB)/3*2
    ''' limbs, which stays well below the 33,554,431-limb threshold.
    ''' </summary>
    Private Shared Sub SafeMpzMul(result As mpz_t, opA As mpz_t, opB As mpz_t)
        ' §143: Threshold lowered from 33,554,431 to 10,000,000 to prevent GMP FFT precision errors.
        ' At ~7.3M × 7.3M limbs (total 14.6M), GMP's FFT transform size M≈2^24 causes rounding
        ' errors because 64-bit limb data makes intermediate FFT coefficients exceed double precision
        ' (53-bit mantissa), producing silently wrong products.  Lowering the threshold forces the
        ' 3×3 recursive split, breaking the sub-products down to ≈2.4M × 2.4M limbs each (≈4.9M
        ' total) which are well within safe FFT accuracy range.
        ' Upper bound constraint: pl * 64 must fit in int32 → pl_max = floor((2^31-1)/64) = 33,554,431.
        '
        ' §160: Threshold further lowered from 10,000,000 to 5,000,000.
        ' The a×r multiplication (szA=43750001, szB=21875001) uses two levels of 3×3 §gen:
        '   Outer §gen → inner SafeMpzMul(14583333, 7291667) [21.9M total, uses §gen again]
        '   Inner §gen → inner-inner SafeMpzMul(4861111, 2430555) [7.29M total]
        ' At 10M threshold, the inner-inner call (7.29M total) fell below the threshold and used
        ' GmpRaw_mul directly. But 7.29M total requires GMP FFT transform size M≈2^23, which
        ' still triggers the same double-precision rounding errors — silently producing wrong limbs
        ' at position ar[64654664] and causing the Barrett rem to exceed b (szRem=42779665 crash).
        ' q×b and b×r inner-inner products are only ≈4.86M total (M≈2^22) and are unaffected.
        ' Lowering to 5M forces SafeMpzMul(4861111, 2430555) into §gen, breaking it into
        ' sub-products of ≈2.43M total (M≈2^22), which are within safe FFT accuracy range.
        '
        ' §5B-Option-B (2026-04-27/28): briefly tried 1M to force one more recursion level
        ' past the 2.2M × 1.1M leaves. Run produced bit-identical wrong middle limbs
        ' for ALL 9 outer sub-products and threw the same Barrett error at §171 — proving
        ' the leaf mpz_mul / mpn_mul_fft is NOT the source of the 5B middle-limb bug.
        ' Reverted to 5M; investigation continues into the §gen accumulation step itself.
        Const SAFE_LIMB_THRESHOLD As Integer = 5_000_000

        Dim szA_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opA.Pointer, 4)
        Dim szB_signed As Integer = Runtime.InteropServices.Marshal.ReadInt32(opB.Pointer, 4)
        Dim szA As Integer = System.Math.Abs(szA_signed)
        Dim szB As Integer = System.Math.Abs(szB_signed)

        ' §183: At SafeMpzMul entry, detect if opA._mp_d already points to zero data (pre-corruption).
        ' If opA._mp_d is zero-filled here, the bug happened in the CALLER before this call.
        If _logLevel >= 2 AndAlso opA.Pointer = opB.Pointer AndAlso szA > 0 Then
            Dim _183_opAd As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
            If _183_opAd <> 0L Then
                Dim _183_r0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_183_opAd), 0)
                Dim _183_r1 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_183_opAd + 8L), 0)
                If _183_r0 = 0L AndAlso _183_r1 = 0L Then
                    AppendLog($"[SafeMpzMul§183] ENTRY zero-data squaring: szA={szA:N0} opA_d={_183_opAd:X16} r[0]={_183_r0:X16} r[1]={_183_r1:X16} opAptr={opA.Pointer.ToInt64():X16} resPtr={result.Pointer.ToInt64():X16}{vbCrLf}", 5)   ' §252 (#95)
                End If
            End If
        End If

        If szA + szB <= SAFE_LIMB_THRESHOLD Then
            If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-PRE  szA={szA:N0} szB={szB:N0} | " &
                    $"opA.Ptr={opA.Pointer.ToInt64():X} opA_sz={szA_signed:N0} opA_d={Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8):X} " &
                    $"opB.Ptr={opB.Pointer.ToInt64():X} opB_sz={szB_signed:N0}{vbCrLf}")
            End If
            ' §78: Use raw P/Invoke to bypass Math.Gmp.Native's managed wrapper, which
            ' corrupts mpz_t.Pointer fields during native calls (same root cause as §42).
            GmpRaw_mul(result.Pointer, opA.Pointer, opB.Pointer)
            If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
                AppendLog(
                    $"[SafeMpzMul] FAST-POST result.Ptr={result.Pointer.ToInt64():X} result_sz={Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4):N0} " &
                    $"result_d={Runtime.InteropServices.Marshal.ReadInt64(result.Pointer, 8):X}{vbCrLf}")
            End If
            ' §178: Diagnose zero-result squarings in the fast path (szA+szB ≤ threshold).
            ' Fires when opA=opB (squaring) AND the result is unexpectedly zero.
            ' Case A: szA=0 means depth-1 trim incorrectly found all-zeros in r[0..mA-1].
            ' Case B: szA>0 but result=0 means GmpRaw_mul itself produced a wrong answer.
            ' In both cases, log szA and the raw limb at opA._mp_d to identify root cause.
            If _logLevel >= 2 AndAlso opA.Pointer = opB.Pointer Then
                Dim _178rSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(result.Pointer, 4))
                If _178rSz = 0 Then
                    Dim _178aD As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
                    Dim _178r0 As Long = If(_178aD <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_178aD), 0), 0L)
                    Dim _178r1 As Long = If(_178aD <> 0L AndAlso szA >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_178aD + 8L), 0), 0L)
                    AppendLog($"[SafeMpzMul§178] zero-result squaring: szA={szA:N0} opA.Ptr={opA.Pointer.ToInt64():X16} opA._mp_d={_178aD:X16} raw[0]={_178r0:X16} raw[1]={_178r1:X16}{vbCrLf}", 5)   ' §252 (#95)
                End If
            End If
            Return
        End If

        ' §70 (#70 RAM-cap dispatch): at depth-0 (Not _smm_innerForceSerial), if even §gen at DOP=1
        ' would exceed the memory budget, route to the chunked grid (full mode) instead — it has a
        ' ~half-size peak (no shifted buffer, tiny per-cell temps) so it completes where §gen OOMs,
        ' capping the ~40-70 GB depth-0 peak (§68 Phase C).  No-op on a roomy box (§gen-DOP1 fits);
        ' chunked-full is bit-identical to §gen (validated --test-chunkedgrid) but ~1.4× slower.
        If (Not _smm_innerForceSerial) AndAlso MemBudget_ShouldFallbackToChunkedGrid(CLng(szA), CLng(szB)) Then
            If _logLevel >= 2 Then AppendLog($"[MemoryBudget§70] §gen DOP=1 peak {MemBudget_ProjectMulPeakGB(CLng(szA), CLng(szB), 1):F1}GB > budget — routing {szA:N0}×{szB:N0} to chunked-grid full product (RAM cap){vbCrLf}")
            SafeMpzMul_ChunkedGrid(result, opA, opB, 0L)
            Return
        End If

        ' §143: Log when recursion is triggered for large multiplications (szA+szB > threshold).
        If _logLevel >= 5 AndAlso CLng(szA) + CLng(szB) > 5_000_000L Then
            AppendLog($"[SafeMpzMul§143] RECURSE szA={szA:N0} szB={szB:N0} total={CLng(szA)+CLng(szB):N0} (threshold={SAFE_LIMB_THRESHOLD:N0}) — using 3×3 split to avoid GMP FFT precision error{vbCrLf}", 5)
        End If

        Dim resultSign As Integer = System.Math.Sign(szA_signed) * System.Math.Sign(szB_signed)

        ' Piece widths in limbs (ceiling division by 3) and bits.
        Dim mA As ULong = CULng((szA + 2) \ 3)
        Dim mB As ULong = CULng((szB + 2) \ 3)
        Dim bitsA As ULong = mA * 64UL
        Dim bitsB As ULong = mB * 64UL

        ' §40 — Accumulate into a separate `accum` object instead of `result`.
        ' §39 (restoring result.Pointer after each inner call) was insufficient: inner
        ' SafeMpzMul calls corrupt not only result.Pointer but also the contents of the
        ' native __mpz_struct at savedResultPtr itself (_mp_alloc is overwritten with the
        ' inner's pre-alloc size, making all accumulated data invisible to the caller).
        ' Since `accum` is never passed to any inner SafeMpzMul call, it is immune to
        ' this corruption.  At the end, accum's struct fields are written directly to
        ' savedResultPtr and result.Pointer is restored, giving the caller a valid result.
        Dim _resultLimbs As Long = CLng(szA) + CLng(szB) + 2L
        Dim _resultBytes As Long = _resultLimbs * 8L

        ' Save result's struct address, free its old limb buffer, then blank the struct.
        Dim savedResultPtr As IntPtr = result.Pointer
        Dim _oldResultAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(savedResultPtr, 0))
        Dim _oldResultPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))
        Dim _oldResultSz As Long = CLng(_oldResultAlloc) * 8L
        ' §30: All GMP limb buffers now come from native pool (VirtualAlloc-backed).
        ' GmpNativeAlloc_FreeRaw returns them to the SLIST or VirtualFrees directly.
        ' Replaces the old large/small branching (_savedGmpFree vs VirtualFree).
        GmpNativeAlloc_FreeRaw(_oldResultPtr, _oldResultSz)
        ' Blank result's struct _mp_alloc and _mp_size; _mp_d will hold the accumPtr stash (§44).
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, 0)
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, 0)

        ' §42: Allocate the large accumulation buffer and wire it into a raw accumPtr struct.
        ' Using Marshal.AllocHGlobal(16) for the struct header bypasses mpz_t wrapper
        ' corruption: Math.Gmp.Native cannot modify a plain IntPtr.
        ' §30 fix: use GmpNativeAlloc_PoolGet so NativeFreeFunc can correctly compute raw=ptr-16.
        ' Raw VirtualAlloc here would crash: NativeFreeFunc subtracts GMP_BLOCK_PREFIX (16) expecting
        ' the SLIST_ENTRY header, but a direct VirtualAlloc'd pointer has no such prefix.
        Dim accumBuf As IntPtr = GmpNativeAlloc_PoolGet(_resultBytes)
        If accumBuf = IntPtr.Zero Then
            AppendLog(
                $"[SafeMpzMul] accum pre-alloc FAILED for {_resultBytes \ BYTES_PER_MB:N0} MB — throwing OOM{vbCrLf}", 1)   ' §252 (#95): OOM → level 1
            Throw New OutOfMemoryException($"SafeMpzMul: GmpNativeAlloc_PoolGet failed for accum buffer ({_resultBytes \ BYTES_PER_MB} MB)")
        End If
        Dim accumPtr As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 0, CInt(_resultLimbs)) ' _mp_alloc
        Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 4, 0)                  ' _mp_size = 0
        Runtime.InteropServices.Marshal.WriteInt64(accumPtr, 8, accumBuf.ToInt64()) ' _mp_d
        ' §44: stash accumPtr in result's own native CRT struct (_mp_d slot, offset +8).
        ' Inner calls use `prod` as their result — they never touch outer result's struct.
        ' This slot survives all native GMP calls and managed-stack corruption.
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, accumPtr.ToInt64())
        If _logLevel >= 4 Then AppendLog(
            $"[SafeMpzMul] accum pre-alloc OK: {_resultLimbs:N0} limbs ({_resultBytes \ BYTES_PER_MB:N0} MB){vbCrLf}")

        ' Split opB into three pieces upfront: opB is small so all three pieces coexist cheaply.
        ' opA and opB are Q/P values from Chudnovsky binary split, always non-negative.
        ' §90: Zero-copy limb-window pieces — struct headers point directly into opB's limb array.
        ' No init/tdiv needed; safe for bitsB > UInt32.Max. GMP reads pieces but never rewrites them.
        ' Cleanup: FreeHGlobal(header) only — GmpRaw_clear would free opB's buffer (catastrophic).
        Dim B0 As New mpz_t(), B1 As New mpz_t(), B2 As New mpz_t()
        B0.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        B1.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        B2.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Dim _opB_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)
        ' B0 = limbs [0, mB) of opB
        Dim _B0_szT As Integer = CInt(System.Math.Min(CLng(szB), CLng(mB)))
        While _B0_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_opB_d + CLng(_B0_szT - 1) * 8L)) = 0L
            _B0_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B0.Pointer, 0, CInt(mB))
        Runtime.InteropServices.Marshal.WriteInt32(B0.Pointer, 4, _B0_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B0.Pointer, 8, _opB_d)
        ' B1 = limbs [mB, 2*mB) of opB
        Dim _B1_d As Long = _opB_d + CLng(mB) * 8L
        Dim _B1_szT As Integer = CInt(System.Math.Min(CLng(szB) - CLng(mB), CLng(mB)))
        If _B1_szT < 0 Then _B1_szT = 0
        While _B1_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_B1_d + CLng(_B1_szT - 1) * 8L)) = 0L
            _B1_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B1.Pointer, 0, CInt(mB))
        Runtime.InteropServices.Marshal.WriteInt32(B1.Pointer, 4, _B1_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B1.Pointer, 8, _B1_d)
        ' B2 = limbs [2*mB, szB) of opB
        Dim _B2_d As Long = _opB_d + 2L * CLng(mB) * 8L
        Dim _B2_limbs As Long = System.Math.Max(0L, CLng(szB) - 2L * CLng(mB))
        Dim _B2_szT As Integer = CInt(_B2_limbs)
        While _B2_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_B2_d + CLng(_B2_szT - 1) * 8L)) = 0L
            _B2_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(B2.Pointer, 0, CInt(_B2_limbs))
        Runtime.InteropServices.Marshal.WriteInt32(B2.Pointer, 4, _B2_szT)
        Runtime.InteropServices.Marshal.WriteInt64(B2.Pointer, 8, _B2_d)
        If _logLevel >= 4 AndAlso CLng(szA) + CLng(szB) > 10_000_000L Then
            AppendLog(
                $"[SafeMpzMul] B-pieces | " &
                $"B0.Ptr={B0.Pointer.ToInt64():X} B0_sz={Runtime.InteropServices.Marshal.ReadInt32(B0.Pointer, 4):N0} " &
                $"B1.Ptr={B1.Pointer.ToInt64():X} B1_sz={Runtime.InteropServices.Marshal.ReadInt32(B1.Pointer, 4):N0} " &
                $"B2.Ptr={B2.Pointer.ToInt64():X} B2_sz={Runtime.InteropServices.Marshal.ReadInt32(B2.Pointer, 4):N0}{vbCrLf}")
        End If

        ' §90: Zero-copy limb-window pieces for A — headers point directly into opA's limb array.
        ' No init2/tdiv/CopyMemory needed; safe for bitsA > UInt32.Max.
        ' Cleanup: FreeHGlobal(header) only — GmpRaw_clear would free opA's buffer (catastrophic).
        Dim A0 As New mpz_t(), A1 As New mpz_t(), A2 As New mpz_t()
        A0.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        A1.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        A2.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        Dim _pre_opA_d As Long = Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8)
        ' A0 = limbs [0, mA) of opA
        Dim _A0_szT As Integer = CInt(System.Math.Min(CLng(szA), CLng(mA)))
        While _A0_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + CLng(_A0_szT - 1) * 8L)) = 0L
            _A0_szT -= 1
        End While
        ' §179: Catch A0-trim-to-zero in squarings — diagnoses wrong _pre_opA_d vs genuine all-zero data.
        If _logLevel >= 2 AndAlso _A0_szT = 0 AndAlso CInt(System.Math.Min(CLng(szA), CLng(mA))) > 0 AndAlso _pre_opA_d = _opB_d Then
            Dim _179_raw0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0)
            Dim _179_raw1 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + 8L), 0)
            Dim _179_opAptr As Long = opA.Pointer.ToInt64()
            Dim _179_freed As Long = _oldResultPtr.ToInt64()
            Dim _179_accBuf As Long = accumBuf.ToInt64()
            Dim _179_same As Boolean = (_179_freed = _pre_opA_d)
            Dim _179_accumSame As Boolean = (_179_accBuf = _pre_opA_d)
            AppendLog($"[SafeMpzMul§179] A0-trim-ZERO squaring: szA={szA:N0} mA={mA:N0} opA_d={_pre_opA_d:X16} raw[0]={_179_raw0:X16} raw[1]={_179_raw1:X16}" &
                      $" opAptr={_179_opAptr:X16} savedResPtr={savedResultPtr.ToInt64():X16} freedBuf={_179_freed:X16} freed==opA_d={_179_same}" &
                      $" accumBuf={_179_accBuf:X16} accumBuf==opA_d={_179_accumSame}{vbCrLf}")
        End If
        Runtime.InteropServices.Marshal.WriteInt32(A0.Pointer, 0, CInt(mA))
        Runtime.InteropServices.Marshal.WriteInt32(A0.Pointer, 4, _A0_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A0.Pointer, 8, _pre_opA_d)
        ' A1 = limbs [mA, 2*mA) of opA
        Dim _A1_d As Long = _pre_opA_d + CLng(mA) * 8L
        Dim _A1_szT As Integer = CInt(System.Math.Min(CLng(szA) - CLng(mA), CLng(mA)))
        If _A1_szT < 0 Then _A1_szT = 0
        While _A1_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A1_d + CLng(_A1_szT - 1) * 8L)) = 0L
            _A1_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A1.Pointer, 0, CInt(mA))
        Runtime.InteropServices.Marshal.WriteInt32(A1.Pointer, 4, _A1_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A1.Pointer, 8, _A1_d)
        ' A2 = limbs [2*mA, szA) of opA
        Dim _A2_d As Long = _pre_opA_d + 2L * CLng(mA) * 8L
        Dim _A2_limbs As Long = System.Math.Max(0L, CLng(szA) - 2L * CLng(mA))
        Dim _A2_szT As Integer = CInt(_A2_limbs)
        While _A2_szT > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_A2_d + CLng(_A2_szT - 1) * 8L)) = 0L
            _A2_szT -= 1
        End While
        Runtime.InteropServices.Marshal.WriteInt32(A2.Pointer, 0, CInt(_A2_limbs))
        Runtime.InteropServices.Marshal.WriteInt32(A2.Pointer, 4, _A2_szT)
        Runtime.InteropServices.Marshal.WriteInt64(A2.Pointer, 8, _A2_d)

        Dim A_parts() As mpz_t = {A0, A1, A2}
        Dim B_parts() As mpz_t = {B0, B1, B2}

        ' §115: log piece trim-sizes and buffer identity to distinguish r*r vs q*b calls.
        ' If opA._mp_d == opB._mp_d → r*r (same buffer). Else → q*b or other.
        If _logLevel >= 2 AndAlso mA = 7291667UL Then
            Dim _115same As Boolean = (_pre_opA_d = _opB_d)
            AppendLog($"[SafeMpzMul§115] mA=mB=7291667 call: opA_d={_pre_opA_d:X16} opB_d={_opB_d:X16} same={_115same}" &
                      $" A0sz={_A0_szT:N0} A1sz={_A1_szT:N0} A2sz={_A2_szT:N0}" &
                      $" B0sz={_B0_szT:N0} B1sz={_B1_szT:N0} B2sz={_B2_szT:N0}{vbCrLf}")
        End If

        ' §177: For depth-2 r×r squaring calls (mA=2430556, same buffer), log opA_d, szA,
        ' piece trim sizes, and the actual limb values at key positions in the sub-piece.
        ' This diagnoses why A_sub0_2 appears as size=0 even though r[0]≠0.
        If _logLevel >= 2 AndAlso mA = 2430556UL AndAlso _pre_opA_d = _opB_d Then
            Dim _177b0 As Long = If(_A0_szT >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0), 0L)
            Dim _177b1 As Long = If(_A0_szT >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 8), 0L)
            Dim _177top As Long = If(_A0_szT >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d + CLng(_A0_szT - 1) * 8L), 0), 0L)
            Dim _177rawAt0 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_pre_opA_d), 0)
            AppendLog($"[SafeMpzMul§177] depth2-sq mA=2430556 szA={szA:N0} opA_d={_pre_opA_d:X16}" &
                      $" A0sz={_A0_szT:N0} A1sz={_A1_szT:N0} A2sz={_A2_szT:N0}" &
                      $" sub0[0]={_177b0:X16} sub0[1]={_177b1:X16} sub0[top]={_177top:X16} raw[0]@opA_d={_177rawAt0:X16}{vbCrLf}")
        End If

        ' Allocate one result buffer per sub-product (k = i*3 + j, k ∈ 0..8).
        ' §25: raw init — Marshal.AllocHGlobal(16) struct header + GmpRaw_init limb buffer.
        Dim prods(8) As mpz_t
        For k As Integer = 0 To 8
            prods(k) = New mpz_t()
            prods(k).Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(prods(k).Pointer)
        Next k

        ' §59: Run all 9 sub-products A_i × B_j simultaneously on the thread pool.
        ' Each call uses a distinct prods(k) as result; A_parts and B_parts are read-only shared.
        ' GMP arithmetic on non-aliased objects is thread-safe. The §44 accumPtr stash lives in
        ' result's native struct (_mp_d slot); inner calls stash into prods(k) structs instead
        ' and never touch result's struct, so the outer stash is preserved across the Parallel.For.
        ' §69: Use _safeMulDop to control inner parallelism. When called from Phase 2's
        ' Parallel.For (outer DOP=24), _safeMulDop=1 so sub-products run serially — eliminates
        ' the thread-pool park/unpark overhead. When called from the serial Phase 2 top levels
        ' or ComputePiGMP, _safeMulDop=ProcessorCount to use all cores.
        ' §238 (issue #87): if this thread is already running inside a parent SafeMpzMul's
        ' Parallel.For sub-product lambda, force serial sub-products here — caps the nested
        ' parallel memory blow-up that crashed 5B sqrt_step_6.  See _smm_innerForceSerial decl.
        Dim _smmDop As Integer
        If _smm_innerForceSerial Then
            _smmDop = 1
        Else
            _smmDop = System.Threading.Volatile.Read(_safeMulDop)  ' §27: cross-thread read
        End If
        If _smmDop <= 0 Then _smmDop = Environment.ProcessorCount
        ' §243 (issue #68): MemoryBudget DOP floor.  Only at top-level large muls (_smmDop>1,
        ' i.e. depth-0 — inner recursion is forced to 1 by §238).  First trim the pool if
        ' commit headroom is low (frees GB-scale ≤16MB granules per #69), then reduce DOP if
        ' the projected 9-sub-product §gen peak would exceed available commit.  FLOOR ONLY
        ' (Min): never raises DOP, so on a healthy box with ample RAM this is a no-op.  Reading
        ' is cached (~2 s) so the cost is negligible even though SafeMpzMul is called often.
        If _smmDop > 1 Then
            MemBudget_MaybeTrimUnderPressure(10.0)
            Dim _budgetDop As Integer = MemBudget_SuggestSafeMulDop(CLng(szA), CLng(szB))
            If _budgetDop < _smmDop Then
                If _logLevel >= 2 Then AppendLog($"[MemoryBudget§243] §gen DOP floored {_smmDop}→{_budgetDop} (szA={szA:N0} szB={szB:N0} availPhys={MemBudget_AvailablePhysicalGB():F1}GB availCommit={MemBudget_AvailableCommitGB():F1}GB projIncr@{_smmDop}={MemBudget_ProjectMulPeakGB(CLng(szA), CLng(szB), _smmDop):F1}GB){vbCrLf}")
                _smmDop = _budgetDop
            End If
        End If
        ' §138/§165 LIFTED by §221 (issue #44, 2026-05-22): the size-gate that forced
        ' serial 9-sub-product computation for q×b (szA=szB=21875001) and a×r
        ' (szA=43750001, szB=21875001) when opA_d≠opB_d. The original "wrong prods(8)"
        ' symptom was the SAME upstream Newton-convergence bug §200/§201 fixed and the
        ' SAME issue §220 (issue #55) just lifted at SafeMpzDiv. With §200/§201 in place
        ' the parallel SafeMpzMul path produces correct prods regardless of opA_d/opB_d
        ' identity. The 9 sub-products inside this Parallel.For are independent results
        ' written to distinct prods(k) slots; concurrent GMP mpz_mul on distinct mpz_t
        ' destinations has no shared mutable state outside the GmpNativeAlloc pool
        ' (which is per-thread-safe by design).
        ' Original gate preserved as comment for easy revert (grep §221):
        '   Dim _forceSerialQxB As Boolean = ((szA = 21875001 OrElse szA = 43750001) AndAlso szB = 21875001 AndAlso _pre_opA_d <> _opB_d)
        '   If _smmDop <= 1 OrElse _forceSerialQxB Then
        '       If _logLevel >= 2 AndAlso _forceSerialQxB Then AppendLog($"[SafeMpzMul§138] forcing serial sub-products...")
        ' The §144/§170/§169 in-loop verifiers stay enabled and would catch any regression.
        If _smmDop <= 1 Then
            ' Serial path: no thread pool involvement, no park/unpark overhead.
            For k As Integer = 0 To 8
                ' §182: Before each inner call involving A2 (k=6,7,8), log A2._mp_d and its raw[0].
                ' Detects when A2._mp_d gets corrupted between piece setup and the k=8 call.
                If _logLevel >= 2 AndAlso k >= 6 AndAlso _pre_opA_d = _opB_d Then
                    Dim _182_A2d As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
                    Dim _182_A2r0 As Long = If(_182_A2d <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_182_A2d), 0), 0L)
                    AppendLog($"[SafeMpzMul§182] pre-k={k} squaring szA={szA:N0} mA={mA:N0} A2_ptr={A_parts(2).Pointer.ToInt64():X16} A2_d={_182_A2d:X16} A2_d[0]={_182_A2r0:X16}{vbCrLf}")
                End If
                SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
            Next k
        Else
            Dim _smm_opts As New System.Threading.Tasks.ParallelOptions() With {
                .MaxDegreeOfParallelism = _smmDop
            }
            Parallel.For(0, 9, _smm_opts, Sub(k As Integer)
                ' §238 (issue #87): mark this worker thread as "inside parallel sub-product"
                ' so recursive SafeMpzMul below sees the flag and forces _smmDop=1.  Save/restore
                ' for ThreadPool thread reuse — the worker may host unrelated work later.
                Dim _was238 As Boolean = _smm_innerForceSerial
                _smm_innerForceSerial = True
                Try
                    SafeMpzMul(prods(k), A_parts(k \ 3), B_parts(k Mod 3))
                Finally
                    _smm_innerForceSerial = _was238
                End Try
            End Sub)
        End If

        ' §5B-c2 (DISABLED 2026-04-28): originally compared prods(8) against a direct
        ' GmpRaw_mul on A_2 × B_2 (58.3M × 29.2M).  At 87.5M total limbs, GMP's mpz_mul
        ' AVs (0xC0000005 inside libgmp-10.dll) — the FFT workspace either OOMs or fails
        ' an internal bound check.  This is the very failure §143 exists to avoid via the
        ' recursive 3×3 split.  Existing §136 block uses the same pattern at 21.9M × 21.9M
        ' (43.75M total) where GMP merely produces wrong limbs instead of crashing.
        ' Replaced by §5B-e below: chunked-grid reference using sub-threshold direct
        ' GmpRaw_mul calls then independent accumulation.

        ' §5B-e: Chunked-grid independent reference for prods(7) = A_2 × B_1 and
        ' prods(8) = A_2 × B_2 at the outer 175M × 87.5M call.  Compute each via
        ' a 39×20 grid (≤ 1.5M × 1.5M = ≤ 3M total per sub-product, well under
        ' SAFE_LIMB_THRESHOLD = 5M where direct GmpRaw_mul is reliable per §160's
        ' analysis), then accumulate via mul_2exp + add into a reference mpz_t
        ' whose limb buffer is PRE-ALLOCATED via VirtualAlloc to the max final size
        ' (avoiding GMP-internal realloc paths through NativeReallocFunc that aborted
        ' run 11 with "gmp: overflow in mpz type").  Same swap-in pattern as §gen's
        ' _sharedSjBuf / _sv_shifted_hdr.
        '
        ' Compare the suspect index to our SafeMpzMul prods(k):
        '   Match  ⇒ our prods(k) is correct (bug elsewhere — likely the other
        '             prods(k) or in the level-1 GmpRaw_add carry chain).
        '   Differ ⇒ our prods(k) is wrong; the chunked reference IS the truth, and
        '             we have an exact delta + a known-good limb to investigate
        '             our SafeMpzMul split with.
        If _logLevel >= 2 AndAlso szA = 175000001 AndAlso szB = 87500001 Then
            Const _CHUNK_E As Integer = 1500000
            ' Max final/intermediate buffer size: prods(7|8) = ≤ 87.5M limbs; pad to 90M for safety.
            Const _E_MAX_LIMBS As Integer = 90_000_000
            Dim _E_MAX_BYTES As Long = CLng(_E_MAX_LIMBS) * 8L
            AppendLog($"[SafeMpzMul§5B-e] starting chunked-grid reference (chunk={_CHUNK_E:N0}, prealloc={_E_MAX_LIMBS:N0} limbs/buf, {_E_MAX_BYTES \ BYTES_PER_MB:N0} MB){vbCrLf}", 5)   ' §252 (#95)
            For Each _refIdx As Integer In New Integer() {7, 8}
                Dim _refTargetIdx As Long = If(_refIdx = 7, 72916666L, 43749999L)
                Dim _ref_A_d As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
                Dim _ref_A_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(A_parts(2).Pointer, 4))
                Dim _ref_B_partIdx As Integer = If(_refIdx = 7, 1, 2)
                Dim _ref_B_d As Long = Runtime.InteropServices.Marshal.ReadInt64(B_parts(_ref_B_partIdx).Pointer, 8)
                Dim _ref_B_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(B_parts(_ref_B_partIdx).Pointer, 4))
                AppendLog($"[SafeMpzMul§5B-e prods({_refIdx})] A_2 sz={_ref_A_sz:N0} B_{_ref_B_partIdx} sz={_ref_B_sz:N0} target idx={_refTargetIdx:N0}{vbCrLf}", 5)   ' §252 (#95)
                ' Per-refIdx VirtualAlloc'd buffers (zeroed by VirtualAlloc, freed at end)
                Dim _eAccBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_E_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                Dim _eShiftBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(_E_MAX_BYTES)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
                If _eAccBuf = IntPtr.Zero OrElse _eShiftBuf = IntPtr.Zero Then
                    AppendLog($"[SafeMpzMul§5B-e prods({_refIdx})] VirtualAlloc FAILED — skipping{vbCrLf}", 5)   ' §252 (#95)
                    If _eAccBuf <> IntPtr.Zero Then VirtualFree(_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                    If _eShiftBuf <> IntPtr.Zero Then VirtualFree(_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
                    Continue For
                End If
                ' Setup _refAcc with swapped-in pre-allocated buffer (mirror §gen's _sv_shifted_hdr setup).
                Dim _refAcc As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_refAcc)
                Dim _ra_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_refAcc, 0))
                Dim _ra_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_refAcc, 8))
                GmpNativeAlloc_FreeRaw(_ra_initPtr, _ra_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_refAcc, 0, _E_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_refAcc, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_refAcc, 8, _eAccBuf.ToInt64())
                ' Same swap-in for _ckShifted
                Dim _ckShifted As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_ckShifted)
                Dim _cs_initAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_ckShifted, 0))
                Dim _cs_initPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_ckShifted, 8))
                GmpNativeAlloc_FreeRaw(_cs_initPtr, _cs_initAlloc * 8L)
                Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 0, _E_MAX_LIMBS)
                Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 4, 0)
                Runtime.InteropServices.Marshal.WriteInt64(_ckShifted, 8, _eShiftBuf.ToInt64())
                ' _ckPartial uses GMP-managed buffer (small, ~3M limbs max — realloc is safe at this size).
                Dim _ckPartial As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                GmpRaw_init(_ckPartial)
                ' Zero-copy chunk headers (do NOT GmpRaw_clear — they alias opA/opB data).
                Dim _ckA As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _ckB As IntPtr = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                Dim _numCkA As Integer = (_ref_A_sz + _CHUNK_E - 1) \ _CHUNK_E
                Dim _numCkB As Integer = (_ref_B_sz + _CHUNK_E - 1) \ _CHUNK_E
                Dim _ckCount As Integer = 0
                For i As Integer = 0 To _numCkA - 1
                    Dim _ckA_off As Long = CLng(i) * CLng(_CHUNK_E)
                    Dim _ckA_sz As Integer = CInt(System.Math.Min(CLng(_CHUNK_E), CLng(_ref_A_sz) - _ckA_off))
                    If _ckA_sz <= 0 Then Continue For
                    While _ckA_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_ref_A_d + (_ckA_off + CLng(_ckA_sz - 1)) * 8L)) = 0L
                        _ckA_sz -= 1
                    End While
                    If _ckA_sz <= 0 Then Continue For
                    Runtime.InteropServices.Marshal.WriteInt32(_ckA, 0, _CHUNK_E)
                    Runtime.InteropServices.Marshal.WriteInt32(_ckA, 4, _ckA_sz)
                    Runtime.InteropServices.Marshal.WriteInt64(_ckA, 8, _ref_A_d + _ckA_off * 8L)
                    For j As Integer = 0 To _numCkB - 1
                        Dim _ckB_off As Long = CLng(j) * CLng(_CHUNK_E)
                        Dim _ckB_sz As Integer = CInt(System.Math.Min(CLng(_CHUNK_E), CLng(_ref_B_sz) - _ckB_off))
                        If _ckB_sz <= 0 Then Continue For
                        While _ckB_sz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_ref_B_d + (_ckB_off + CLng(_ckB_sz - 1)) * 8L)) = 0L
                            _ckB_sz -= 1
                        End While
                        If _ckB_sz <= 0 Then Continue For
                        Runtime.InteropServices.Marshal.WriteInt32(_ckB, 0, _CHUNK_E)
                        Runtime.InteropServices.Marshal.WriteInt32(_ckB, 4, _ckB_sz)
                        Runtime.InteropServices.Marshal.WriteInt64(_ckB, 8, _ref_B_d + _ckB_off * 8L)
                        GmpRaw_mul(_ckPartial, _ckA, _ckB)
                        Dim _ckShiftBits As ULong = CULng(_ckA_off + _ckB_off) * 64UL
                        If _ckShiftBits = 0UL Then
                            GmpRaw_add(_refAcc, _refAcc, _ckPartial)
                        Else
                            Runtime.InteropServices.Marshal.WriteInt32(_ckShifted, 4, 0)
                            Dim _shiftSrc As IntPtr = _ckPartial
                            Dim _shiftRem As ULong = _ckShiftBits
                            While _shiftRem > 0UL
                                Dim _chunkBits As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                                GmpRaw_mul_2exp(_ckShifted, _shiftSrc, _chunkBits)
                                _shiftSrc = _ckShifted
                                _shiftRem -= CULng(_chunkBits)
                            End While
                            GmpRaw_add(_refAcc, _refAcc, _ckShifted)
                        End If
                        _ckCount += 1
                    Next j
                Next i
                Dim _refAccSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_refAcc, 4))
                Dim _refAccD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_refAcc, 8))
                Dim _refV As ULong = If(_refTargetIdx < CLng(_refAccSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt(_refTargetIdx * 8L))), 0UL)
                Dim _refV_lo As ULong = If(_refTargetIdx - 1L < CLng(_refAccSz) AndAlso _refTargetIdx - 1L >= 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt((_refTargetIdx - 1L) * 8L))), 0UL)
                Dim _refV_hi As ULong = If(_refTargetIdx + 1L < CLng(_refAccSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_refAccD, CInt((_refTargetIdx + 1L) * 8L))), 0UL)
                Dim _ourSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_refIdx).Pointer, 4))
                Dim _ourD As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(_refIdx).Pointer, 8))
                Dim _ourV As ULong = If(_refTargetIdx < CLng(_ourSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt(_refTargetIdx * 8L))), 0UL)
                Dim _ourV_lo As ULong = If(_refTargetIdx - 1L < CLng(_ourSz) AndAlso _refTargetIdx - 1L >= 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt((_refTargetIdx - 1L) * 8L))), 0UL)
                Dim _ourV_hi As ULong = If(_refTargetIdx + 1L < CLng(_ourSz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_ourD, CInt((_refTargetIdx + 1L) * 8L))), 0UL)
                AppendLog($"[SafeMpzMul§5B-e prods({_refIdx}) idx={_refTargetIdx:N0}] subProducts={_ckCount:N0} refSz={_refAccSz:N0} ourSz={_ourSz:N0} reference[idx-1,idx,idx+1]=[{_refV_lo:X16} {_refV:X16} {_refV_hi:X16}] ourSafeMpzMul[idx-1,idx,idx+1]=[{_ourV_lo:X16} {_ourV:X16} {_ourV_hi:X16}] match@idx={(_refV = _ourV)}{vbCrLf}", 5)   ' §252 (#95)
                ' Cleanup — _refAcc and _ckShifted have swapped-in VirtualAlloc'd buffers.
                ' Do NOT GmpRaw_clear (would call NativeFreeFunc on a pointer that has no
                ' SLIST_ENTRY prefix → catastrophic).  Free the 16-byte struct headers and
                ' VirtualFree the limb buffers separately.
                Runtime.InteropServices.Marshal.FreeHGlobal(_refAcc)
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckShifted)
                ' _ckPartial uses GMP-managed buffer — full GmpRaw_clear is correct.
                GmpRaw_clear(_ckPartial) : Runtime.InteropServices.Marshal.FreeHGlobal(_ckPartial)
                ' Chunk headers point into A_2/B_j data — header-only free.
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckA)
                Runtime.InteropServices.Marshal.FreeHGlobal(_ckB)
                ' Release pre-allocated VirtualAlloc'd buffers.
                VirtualFree(_eAccBuf, UIntPtr.Zero, MEM_RELEASE)
                VirtualFree(_eShiftBuf, UIntPtr.Zero, MEM_RELEASE)
            Next
            AppendLog($"[SafeMpzMul§5B-e] chunked-grid reference complete{vbCrLf}", 5)   ' §252 (#95)
        End If

        ' §136: directly call GmpRaw_mul for A2×B2 and compare to prods(8)[13612996] for q×b.
        ' After §143 threshold fix: prods(8) is computed via recursive SafeMpzMul (correct),
        ' but §136's direct GmpRaw_mul call bypasses the threshold and hits the GMP FFT precision bug.
        ' Expected post-fix: match=False (§136 direct=wrong, prods(8) recursive=correct).
        ' This mismatch CONFIRMS the GMP FFT bug: same inputs, different code paths, different results.
        If _logLevel >= 2 AndAlso szA = 21875001 AndAlso szB = 21875001 AndAlso _pre_opA_d <> _opB_d Then
            Dim _fr136 As New mpz_t()
            _fr136.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
            GmpRaw_init(_fr136.Pointer)
            GmpRaw_mul(_fr136.Pointer, A_parts(2).Pointer, B_parts(2).Pointer)
            Dim _fr136sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_fr136.Pointer, 4))
            Dim _fr136D As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_fr136.Pointer, 8))
            Const _IDX136 As Long = 13612996L
            Dim _fr136v As Long = If(_IDX136 < CLng(_fr136sz), Runtime.InteropServices.Marshal.ReadInt64(_fr136D, CInt(_IDX136 * 8L)), 0L)
            Dim _p8sz136 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(8).Pointer, 4))
            Dim _p8D136 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(8).Pointer, 8))
            Dim _p8v136 As Long = If(_IDX136 < CLng(_p8sz136), Runtime.InteropServices.Marshal.ReadInt64(_p8D136, CInt(_IDX136 * 8L)), 0L)
            AppendLog($"[SafeMpzMul§136] serial A2×B2[{_IDX136:N0}]={_fr136v:X16} prods(8)[{_IDX136:N0}]={_p8v136:X16} match={_fr136v = _p8v136}{vbCrLf}", 5)   ' §252 (#95)
            GmpRaw_clear(_fr136.Pointer)
            Runtime.InteropServices.Marshal.FreeHGlobal(_fr136.Pointer)
        End If

        ' §134: after all sub-products are computed, log A2×B2 (prods(8)) bot/top/[13612996]
        ' for the q×b call (szA=szB=21875001). Verifies GmpRaw_mul output before accumulation.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§134-probe] szA={szA} szB={szB} cond={szA = 21875001 AndAlso szB = 21875001}{vbCrLf}")
        If _logLevel >= 2 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
            Dim _p8Ptr134 As IntPtr = prods(8).Pointer
            Dim _p8Sz134 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_p8Ptr134, 4))
            Dim _p8D134 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_p8Ptr134, 8))
            Dim _p8Bot134 As Long = If(_p8Sz134 >= 1, Runtime.InteropServices.Marshal.ReadInt64(_p8D134, 0), 0L)
            Dim _p8Bot1134 As Long = If(_p8Sz134 >= 2, Runtime.InteropServices.Marshal.ReadInt64(_p8D134, 8), 0L)
            ' §237 (issue #86): 64-bit safe pointer arithmetic — latent at 5 B since this block is gated by szA=21875001 (1 B-tuned).
            Dim _p8Top134 As Long = If(_p8Sz134 >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_p8D134.ToInt64() + (CLng(_p8Sz134) - 1L) * 8L), 0), 0L)
            Const _IDX134 As Long = 13612996L
            Dim _p8Mid134 As Long = If(_IDX134 < CLng(_p8Sz134), Runtime.InteropServices.Marshal.ReadInt64(_p8D134, CInt(_IDX134 * 8L)), 0L)
            ' Also log A2[0] and B2[0] to verify piece contents
            Dim _a2D134 As Long = Runtime.InteropServices.Marshal.ReadInt64(A_parts(2).Pointer, 8)
            Dim _b2D134 As Long = Runtime.InteropServices.Marshal.ReadInt64(B_parts(2).Pointer, 8)
            Dim _a2Bot134 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_a2D134), 0)
            Dim _b2Bot134 As Long = Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_b2D134), 0)
            AppendLog($"[SafeMpzMul§134] A2×B2: A2[0]={_a2Bot134:X16} B2[0]={_b2Bot134:X16}" &
                      $" szProd={_p8Sz134:N0} [0]={_p8Bot134:X16} [1]={_p8Bot1134:X16}" &
                      $" [top]={_p8Top134:X16} [{_IDX134:N0}]={_p8Mid134:X16}{vbCrLf}")
        End If
        ' §176: For the r×r squaring call (same buffer, mA=mB=7291667), read prods(0..2)[0]
        ' immediately after inner SafeMpzMul completes — before §44 recovery or any shift.
        ' If prods(0)[0]=0 here, the bug is inside depth-2 computation of A_piece0^2.
        ' If prods(0)[0]≠0 here but =0 at §114, something between here and §114 corrupts it.
        If _logLevel >= 2 AndAlso mA = 7291667UL AndAlso _pre_opA_d = _opB_d Then
            For _p176 As Integer = 0 To 2
                Dim _ptr176 As IntPtr = prods(_p176).Pointer
                If _ptr176 <> IntPtr.Zero Then
                    Dim _sz176 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_ptr176, 4))
                    Dim _d176 As Long = Runtime.InteropServices.Marshal.ReadInt64(_ptr176, 8)
                    Dim _b0_176 As Long = If(_sz176 >= 1 AndAlso _d176 <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_d176), 0), 0L)
                    Dim _b1_176 As Long = If(_sz176 >= 2 AndAlso _d176 <> 0L, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_d176), 8), 0L)
                    AppendLog($"[SafeMpzMul§176] post-inner k={_p176} prods[{_p176}].Ptr={_ptr176.ToInt64():X16} sz={_sz176:N0} [0]={_b0_176:X16} [1]={_b1_176:X16}{vbCrLf}")
                End If
            Next _p176
        End If

        ' §44: recover accumPtr from result's stash after Parallel.For.
        ' §181-fix: Do NOT re-read result.Pointer here — Math.Gmp.Native may have corrupted it
        ' during inner SafeMpzMul calls (§175/§78 corruption). savedResultPtr was captured at
        ' line 2274 as a plain IntPtr, immune to managed-wrapper corruption, and remains correct.
        ' The old `savedResultPtr = result.Pointer` overwrite was the root cause of opA._mp_d
        ' being corrupted across recursive depths (§179 diagnosis).
        accumPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(savedResultPtr, 8))
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] szA={szA:N0} accumPtr recovered; allocating shifted buffer ({CLng(mA) + CLng(mB) + CLng((2UL * bitsA + 2UL * bitsB) \ 64UL) + 4L:N0} limbs){vbCrLf}")

        ' Serial accumulation: shift each prod_k into its positional slot and add to accum.
        ' No inner SafeMpzMul calls — no §42/§44 managed-stack corruption in this loop.
        '
        ' §23: Pre-allocate ONE shared shifted buffer sized for the largest iteration,
        ' replacing the original 8 per-k VirtualAlloc/VirtualFree pairs with a single pair.
        ' Upper bound for any k=8 (max shift = 2*bitsA + 2*bitsB):
        '   sub-product limbs ≤ mA + mB,  shift limbs = (2*bitsA+2*bitsB)/64 + 1
        '   which simplifies to ≤ 3*mA + 3*mB + 4 total limbs.
        Dim _maxShiftBitsShared As ULong = 2UL * bitsA + 2UL * bitsB
        Dim _maxShiftedLimbs As Long = CLng(mA) + CLng(mB) + CLng(_maxShiftBitsShared \ 64UL) + 4L
        Dim _sharedSjBuf As IntPtr = VirtualAlloc(IntPtr.Zero,
                                                   New UIntPtr(CULng(_maxShiftedLimbs * 8L)),
                                                   MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If _sharedSjBuf = IntPtr.Zero Then
            GmpNativeAlloc_FreeRaw(accumBuf, _resultBytes)   ' §30 fix: match PoolGet allocation above
            Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
            AppendLog($"[SafeMpzMul] shared shifted pre-alloc FAILED for {_maxShiftedLimbs * 8L \ BYTES_PER_MB:N0} MB — throwing OOM{vbCrLf}", 1)   ' §252 (#95): OOM → level 1
            Throw New OutOfMemoryException($"SafeMpzMul: VirtualAlloc failed for shared shifted ({_maxShiftedLimbs * 8L \ BYTES_PER_MB} MB)")
        End If
        ' §122 (#122): the real §39 decision (symmetric + ≤50M total + all 6 pieces dense) is made
        ' below and logged there — this line previously printed a premature "§39=" using the wrong
        ' 100M threshold and ignoring the dense check, which did not reflect what the code did.
        If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] shifted buffer OK ({_maxShiftedLimbs * 8L \ BYTES_PER_MB:N0} MB); starting accumulation{vbCrLf}")
        ' §25: raw init for shifted — struct header via Marshal.AllocHGlobal, limb buffer via GmpRaw_init.
        Dim shifted As New mpz_t()
        shifted.Pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        GmpRaw_init(shifted.Pointer)
        ' Replace shifted's initial 1-limb CRT buffer with the shared VirtualAlloc'd buffer.
        Dim _sv_shifted_hdr As IntPtr = shifted.Pointer
        Dim _shiftedInitAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(_sv_shifted_hdr, 0))
        Dim _shiftedInitPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_shifted_hdr, 8))
        ' §30: Free the initial 1-limb buffer allocated by GmpRaw_init via native pool.
        GmpNativeAlloc_FreeRaw(_shiftedInitPtr, CLng(_shiftedInitAlloc) * 8L)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 0, CInt(_maxShiftedLimbs))
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
        Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted_hdr, 8, _sharedSjBuf.ToInt64())

        ' §39: When mA=mB=m sub-products sharing the same (i+j) have identical shift
        ' = (i+j)*m*64.  Group into 5 columns and shift once per column instead of 9
        ' times individually.  Saves 4 large shift operations at the cost of 4 adds.
        ' Column 0: prod(0)              shift 0
        ' Column 1: prod(1)+prod(3)      shift 1*bitsA
        ' Column 2: prod(2)+prod(4)+prod(6) shift 2*bitsA
        ' Column 3: prod(5)+prod(7)      shift 3*bitsA
        ' Column 4: prod(8)              shift 4*bitsA
          ' §128: The §39 column-group path assumes all split windows behave like
          ' dense m-limb blocks. In the failing SafeMpzDiv q*b case, B0 is exactly
          ' zero (B0sz=0), and the grouped accumulation produced a catastrophic
          ' quotient under-estimate. Fall back to the general 9-product path when
          ' any split piece is zero-sized; keep §39 only for fully dense windows.
          ' §Phase3ColAdd: The §39 column adds (e.g. prods(2)+=prods(4)) trigger a GMP
          ' internal realloc of the destination sub-product buffer: our NativeReallocFunc
          ' does VirtualAlloc(new) + memcpy + VirtualFree(old). When each sub-product
          ' exceeds ~800 MB (mA+mB > 100 M limbs) this VirtualAlloc can fail at peak
          ' memory pressure — GMP receives NULL from its realloc callback and calls
          ' abort(), killing the process silently. The §gen path (below) accumulates
          ' each sub-product one at a time into the pre-sized accumulator and shifted
          ' buffer, so no GMP realloc is triggered. Skip §39 for large sub-products.
          ' Option G (2026-04-29): disable §39 column-group fast path to test the
          ' hypothesis that §39 produces a wrong q×b at 5B scale.  Set _OPT_G_DISABLE_S39
          ' = True to force every symmetric SafeMpzMul to go through §gen.  If §171 stops
          ' throwing with §39 disabled, the bug is in §39's accumulation logic.
          Const _OPT_G_DISABLE_S39 As Boolean = False  ' Reverted after run 16 ruled out §39
          ' §122 (#122): authoritative §39 gate — symmetric (mA=mB), ≤50M total limbs, and all 6
          ' split pieces dense (non-zero).  Captured into a boolean so the log reflects the real
          ' decision (the earlier shifted-buffer log used a wrong 100M threshold + no dense check).
          Dim _s39Dense As Boolean = (_A0_szT > 0 AndAlso _A1_szT > 0 AndAlso _A2_szT > 0 AndAlso
                                      _B0_szT > 0 AndAlso _B1_szT > 0 AndAlso _B2_szT > 0)
          Dim _s39Engaged As Boolean = (Not _OPT_G_DISABLE_S39) AndAlso
              mA = mB AndAlso
              CLng(mA) + CLng(mB) <= 50_000_000L AndAlso
              _s39Dense
          If _logLevel >= 2 Then AppendLog($"[SafeMpzMul§accum] §39={_s39Engaged} (mA={mA:N0} mB={mB:N0} sum={CLng(mA) + CLng(mB):N0} cap=50,000,000 dense={_s39Dense}){vbCrLf}")
          If _s39Engaged Then
            If _logLevel >= 4 Then AppendLog($"[SafeMpzMul] §39 column-group fast path (mA=mB={mA:N0}){vbCrLf}")
            ' Per-column: base product index and list of additional product indices to add first
            Dim _col_base As Integer() = {0, 1, 2, 5, 8}
            Dim _col_extra As Integer()() = New Integer()() {
                New Integer() {},
                New Integer() {3},
                New Integer() {4, 6},
                New Integer() {7},
                New Integer() {}
            }
            ' §246 (issue #45 Layer 1): parallel per-column add-chains.  Each column's adds
            ' target a DISTINCT prods slot (col0:{0} col1:{1,3} col2:{2,4,6} col3:{5,7}
            ' col4:{8} — disjoint), the GmpNativeAlloc pool is per-CPU-safe, and the pre-grow
            ' below prevents any GMP realloc — so the 5 columns' add-chains are independent and
            ' run concurrently.  Gated on _smmDop>1: when this §39 call is itself a nested
            ' sub-product, §238 has set _smmDop=1 and we keep the plain serial path (no nested
            ' parallel blow-up, no Parallel.For overhead on the hot 1B path).  The shift +
            ' accumulate that follows stays SERIAL (shared _sv_shifted_hdr + ordered accumPtr).
            Dim _doColAdd As Action(Of Integer) =
                Sub(_colA As Integer)
                    Dim _bkA As Integer = _col_base(_colA)
                    ' §Phase3ColAdd: pre-grow prods(_bkA) before each add so GMP never calls its
                    ' realloc callback (a VirtualAlloc+memcpy+VirtualFree that can fail → abort()).
                    For Each _ak As Integer In _col_extra(_colA)
                        Dim _bk_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_bkA).Pointer, 4))
                        Dim _ak_sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(_ak).Pointer, 4))
                        Dim _needed As Integer = System.Math.Max(_bk_sz, _ak_sz) + 2  ' +2 for carry safety
                        Dim _bk_alloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(prods(_bkA).Pointer, 0)
                        If _bk_alloc < _needed Then
                            Dim _oldBuf As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(prods(_bkA).Pointer, 8))
                            Dim _oldBytes As Long = CLng(_bk_alloc) * 8L
                            Dim _newBytes As Long = CLng(_needed) * 8L
                            Dim _newBuf As IntPtr = GmpNativeAlloc_PoolGet(_newBytes)
                            If _newBuf = IntPtr.Zero Then
                                Throw New OutOfMemoryException($"SafeMpzMul §39 pre-grow: GmpNativeAlloc_PoolGet failed ({_newBytes \ BYTES_PER_MB} MB)")
                            End If
                            CopyMemory(_newBuf, _oldBuf, New UIntPtr(CULng(_bk_sz) * 8UL))
                            GmpNativeAlloc_FreeRaw(_oldBuf, _oldBytes)
                            Runtime.InteropServices.Marshal.WriteInt32(prods(_bkA).Pointer, 0, _needed)
                            Runtime.InteropServices.Marshal.WriteInt64(prods(_bkA).Pointer, 8, _newBuf.ToInt64())
                        End If
                        GmpRaw_add(prods(_bkA).Pointer, prods(_bkA).Pointer, prods(_ak).Pointer)
                        GmpRaw_clear(prods(_ak).Pointer)
                        Dim _tmp_ak = prods(_ak).Pointer : prods(_ak).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_tmp_ak)
                    Next
                End Sub
            If _smmDop > 1 Then
                Dim _colOpts As New System.Threading.Tasks.ParallelOptions() With {.MaxDegreeOfParallelism = System.Math.Min(5, _smmDop)}
                System.Threading.Tasks.Parallel.For(0, 5, _colOpts, _doColAdd)
            Else
                For _colS As Integer = 0 To 4 : _doColAdd(_colS) : Next
            End If

            ' §256 (#45 Layer 2/3): mpn OFFSET-accumulation — replaces the per-column mul_2exp shift
            ' (+ shared _sv_shifted_hdr buffer + ordered GmpRaw_add) with a direct mpn_add at limb
            ' offset col·mA.  bitsA = mA·64, so a column's shift of col·bitsA bits is exactly col·mA
            ' LIMBS (no sub-limb shift) — the column-sum adds straight into accumBuf at that offset
            ' (the chunked-grid pattern).  Eliminates the O(result) shift copy + the ~1.2 GB shifted
            ' buffer; accumulate is now O(Σ col-sum) not O(5×result).  accumBuf is zeroed first (the
            ' offset adds read the gaps).  Shifts are memory-bandwidth-bound, so this beats the
            ' originally-planned "parallelise the shifts" (which would just contend on RAM bandwidth).
            ZeroMemory(accumBuf, New UIntPtr(CULng(CLng(_resultLimbs) * 8L)))
            For _col As Integer = 0 To 4
                Dim _bk As Integer = _col_base(_col)
                Dim _sv_bk As IntPtr = prods(_bk).Pointer
                Dim _bkSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_bk, 4))
                If _bkSz > 0 Then
                    Dim _bkD As Long = Runtime.InteropServices.Marshal.ReadInt64(_sv_bk, 8)
                    Dim _off As Long = CLng(_col) * CLng(mA)   ' col·bitsA bits = col·mA limbs
                    GmpRaw_mpn_add(New IntPtr(accumBuf.ToInt64() + _off * 8L), New IntPtr(accumBuf.ToInt64() + _off * 8L),
                                   CInt(CLng(_resultLimbs) - _off), New IntPtr(_bkD), _bkSz)
                End If
                GmpRaw_clear(_sv_bk)
                prods(_bk).Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_sv_bk)
            Next _col
            ' Normalize accumPtr _mp_size (highest nonzero limb) — mpn_add wrote raw limbs into accumBuf.
            Dim _accSz As Integer = CInt(_resultLimbs)
            While _accSz > 0 AndAlso Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(accumBuf.ToInt64() + CLng(_accSz - 1) * 8L)) = 0L
                _accSz -= 1
            End While
            Runtime.InteropServices.Marshal.WriteInt32(accumPtr, 4, _accSz)
        Else
            ' §222 (issue #60, 2026-05-22): parallel shift pre-pass for asymmetric §gen path.
            '
            ' The original loop did serial: for each k, shift prods(k) by ki·bitsA+kj·bitsB
            ' into a single shared _sv_shifted_hdr buffer, then add to accumPtr. The shifts
            ' (mpz_mul_2exp) are O(N) memmoves and don't overlap across k's because they
            ' all write into the same shared buffer.
            '
            ' Phase 2 win: allocate 9 separate shifted buffers and compute all shifts in
            ' parallel. The serial reduction below (k=0..8 add into accumPtr) is preserved
            ' EXACTLY — same diagnostics, same accumPtr update order — only the shift work
            ' is parallelized. Memory cost: 9 buffers × max-shifted-size ≈ 5.4 GB at 5B,
            ' ~1.1 GB at 1B (well within 64 GB budget).
            '
            ' Gated on _smmDop > 1 AND szA+szB > 500M limbs: at 1B operand scale
            ' (szA+szB ~192M for q×b) the per-call overhead of pre-allocating 9 buffers +
            ' Parallel.For startup × 800+ §gen invocations OUTWEIGHS the parallel-shift gain.
            ' Measured 2026-05-22: §222 ON at 1M-limb threshold made 1B q×b ~5% slower
            ' (8m 57s vs 8m 32s baseline). At 5B-scale operands (szA+szB ~500M+) the shift
            ' cost grows as O(N) while the overhead stays fixed, so the gain dominates per
            ' the #60 issue body's 5-10% projection. The 500M threshold ensures §222 fires
            ' only at the top-level a×r / q×b calls in a 5B run (where it pays off) and
            ' stays inactive at 1B (where it doesn't).
            Dim _useS222 As Boolean = (_smmDop > 1) AndAlso (CLng(szA) + CLng(szB) > 500_000_000L)
            Dim _shifted222 As IntPtr() = Nothing
            If _useS222 Then
                _shifted222 = New IntPtr(8) {}
                ' Worst-case shifted size for each k: prods(k) is at most szA+szB+1 limbs;
                ' shift = (ki·bitsA + kj·bitsB) bits = (ki·mA + kj·mB) limbs. So shifted_k
                ' has at most szA+szB+1 + ki·mA + kj·mB limbs. PreAlloc each one to bypass
                ' GMP's 33.5M-limb realloc abort (same §216a-class issue as §218).
                For _k222 As Integer = 0 To 8
                    _shifted222(_k222) = Runtime.InteropServices.Marshal.AllocHGlobal(16)
                    GmpRaw_init(_shifted222(_k222))
                Next
                Dim _gen222_opts As New System.Threading.Tasks.ParallelOptions() With {.MaxDegreeOfParallelism = System.Math.Min(9, _smmDop)}
                System.Threading.Tasks.Parallel.For(0, 9, _gen222_opts, Sub(k As Integer)
                    Dim ki As Integer = k \ 3
                    Dim kj As Integer = k Mod 3
                    Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
                    If shiftBits = 0UL Then
                        ' k=0: no shift; copy prods(0) value into shifted222(0) so the
                        ' serial reduction below has a uniform API (read from shifted222(k)).
                        Dim _szP0 As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(0).Pointer, 4))
                        Dim _p0Wrap As New mpz_t() With {.Pointer = _shifted222(0)}
                        PreAllocMpzToLimbs(_p0Wrap, CLng(_szP0) + 2L)
                        GmpRaw_set(_shifted222(0), prods(0).Pointer)
                    Else
                        ' Pre-alloc to (prods(k) size + shift_limbs + 2) limbs.
                        Dim _szPk As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(prods(k).Pointer, 4))
                        Dim _shiftLimbs As Long = CLng(shiftBits \ 64UL) + 1L
                        Dim _shWrap As New mpz_t() With {.Pointer = _shifted222(k)}
                        PreAllocMpzToLimbs(_shWrap, CLng(_szPk) + _shiftLimbs + 2L)
                        Dim _shiftSrc As IntPtr = prods(k).Pointer
                        Dim _shiftRem As ULong = shiftBits
                        While _shiftRem > 0UL
                            Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                            GmpRaw_mul_2exp(_shifted222(k), _shiftSrc, _chunk)
                            _shiftSrc = _shifted222(k)
                            _shiftRem -= CULng(_chunk)
                        End While
                    End If
                End Sub)
                If _logLevel >= 3 Then AppendLog($"[SafeMpzMul§222] parallel shift pre-pass complete (DOP={_gen222_opts.MaxDegreeOfParallelism}){vbCrLf}", 3)
            End If

            ' §23/§90: Original per-product accumulation for asymmetric case (mA ≠ mB).
            ' When §222 is active, the inline shift is REPLACED by a read from _shifted222(k);
            ' the serial add + all diagnostics are preserved unchanged.
            For k As Integer = 0 To 8
                Dim ki As Integer = k \ 3
                Dim kj As Integer = k Mod 3
                Dim shiftBits As ULong = CULng(ki) * bitsA + CULng(kj) * bitsB
                Dim _sv_prod As IntPtr = prods(k).Pointer
                Dim _logPre As Integer = If(_logLevel >= 5, System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_sv_prod, 4)), 0)   ' §252 (#95): per-sub-product spam → level 5
                If _logLevel >= 5 Then
                    ' §215: Int32 overflow in offset arithmetic.  _logPre is Integer; (_logPre-1)*8
                    ' overflows Int32 when _logPre > 2^28 = 268,435,456 limbs.  At the topmost a×r
                    ' recursion level for 5B (szA=998M × szB=259M), prods(k) reaches 419M limbs;
                    ' (419,352,782)*8 = 3,354,822,256 wraps to -940,145,040 → AccessViolation in
                    ' Marshal.ReadInt64.  Fix: compute the absolute limb address in 64-bit, then
                    ' read at offset 0.  Same fix applied at §SafeMpzDiv a/ar/b log sites.
                    Dim _prodDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                    Dim _prodTop As Long = If(_logPre >= 1, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_prodDPtr.ToInt64() + (CLng(_logPre) - 1L) * 8L), 0), 0L)
                    Dim _prodTop2 As Long = If(_logPre >= 2, Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_prodDPtr.ToInt64() + (CLng(_logPre) - 2L) * 8L), 0), 0L)
                    AppendLog($"[SafeMpzMul§gen] k={k} ki={ki} kj={kj} shift={shiftBits:N0} szProd={_logPre:N0} top2=[{_prodTop:X16} {_prodTop2:X16}]{vbCrLf}")
                    ' §111: For the final a*r call only, log product limb at the known error position.
                    If k = 8 AndAlso szA = 43750001 AndAlso szB = 21875001 Then
                        Const _EL As Long = 20904662L
                        Dim _pDPtrErr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _pErrL As Long = If(_logPre > CInt(_EL), Runtime.InteropServices.Marshal.ReadInt64(_pDPtrErr, CInt(_EL * 8L)), 0L)
                        Dim _pErrL1 As Long = If(_logPre > CInt(_EL + 1L), Runtime.InteropServices.Marshal.ReadInt64(_pDPtrErr, CInt((_EL + 1L) * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§111] k=8 prod[{_EL:N0}]={_pErrL:X16} prod[{_EL+1:N0}]={_pErrL1:X16}{vbCrLf}")
                    End If
                    ' §130: q*b A2*B2 — log product limb that maps to accum[42,779,664].
                    ' Only k=8 (A2×B2, shift=1,866,666,752 bits=29,166,668 limbs) reaches that position:
                    '   accum[42,779,664] = prods(8)[42,779,664 - 29,166,668] = prods(8)[13,612,996]
                    If k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                        Const _PL130 As Long = 13612996L
                        Dim _p130dPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _p130v As Long = If(_PL130 < CLng(_logPre), Runtime.InteropServices.Marshal.ReadInt64(_p130dPtr, CInt(_PL130 * 8L)), 0L)
                        Dim _p130v1 As Long = If(_PL130 + 1L < CLng(_logPre), Runtime.InteropServices.Marshal.ReadInt64(_p130dPtr, CInt((_PL130 + 1L) * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§130] k=8 A2*B2 prod[{_PL130:N0}]={_p130v:X16} prod[{_PL130+1:N0}]={_p130v1:X16} szProd={_logPre:N0}{vbCrLf}")
                    End If
                    ' §5B-sub: at the outer 175M × 87.5M call only, log each of the 9 sub-products'
                    ' bot/mid/top limbs AND verify prods(k)[0] = A_i[0] * B_j[0] mod 2^64 (exact).
                    ' Mismatch ⇒ that sub-product (the recursive SafeMpzMul call for A_i × B_j)
                    ' produced a wrong bottom limb — narrows the 5B bug to a specific k.
                    If szA = 175000001 AndAlso szB = 87500001 AndAlso _logPre > 0 Then
                        Dim _sp5DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(_sv_prod, 8))
                        Dim _sp5Bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, 0))
                        Dim _sp5Bot2 As ULong = If(_logPre >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, 8)), 0UL)
                        Dim _sp5Mid As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre \ 2) * 8L)))
                        Dim _sp5Top2 As ULong = If(_logPre >= 2, CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre - 2) * 8L))), 0UL)
                        Dim _sp5Top As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_sp5DPtr, CInt(CLng(_logPre - 1) * 8L)))
                        ' Read A_i[0] and B_j[0] via opA/opB data + ki*mA, kj*mB offsets.
                        Dim _opA_d5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(opA.Pointer, 8))
                        Dim _opB_d5 As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8))
                        Dim _ai5_bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt(CLng(ki) * CLng(mA) * 8L)))
                        Dim _bj5_bot As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt(CLng(kj) * CLng(mB) * 8L)))
                        Dim _exp5SpBot As ULong = _ai5_bot * _bj5_bot
                        AppendLog($"[SafeMpzMul§5B-sub k={k} ki={ki} kj={kj}] szProd={_logPre:N0} bot=[{_sp5Bot:X16} {_sp5Bot2:X16}] mid[{_logPre\2:N0}]={_sp5Mid:X16} top=[{_sp5Top2:X16} {_sp5Top:X16}]{vbCrLf}", 5)   ' §252 (#95)
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verify] A_{ki}[0]={_ai5_bot:X16} B_{kj}[0]={_bj5_bot:X16} (A_{ki}*B_{kj})_lo={_exp5SpBot:X16} actual prod[0]={_sp5Bot:X16} match={(_exp5SpBot = _sp5Bot)}{vbCrLf}", 5)   ' §252 (#95)
                        ' §5B-sub-1: verify prods(k)[1].  prods[0] receives only one term
                        ' (A_i[0]*B_j[0].lo) so its carry to limb 1 is 0.  prods[1] receives:
                        '   hi(A_i[0]*B_j[0]) + lo(A_i[0]*B_j[1]) + lo(A_i[1]*B_j[0])
                        ' all reduced mod 2^64.  Mismatch ⇒ that prods(k) is wrong starting
                        ' from limb 1, narrowing the bug to a specific recursive sub-product.
                        If _logPre >= 2 Then
                            Dim _ai51_1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt((CLng(ki) * CLng(mA) + 1L) * 8L)))
                            Dim _bj51_1 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt((CLng(kj) * CLng(mB) + 1L) * 8L)))
                            Dim _hi00_lo As ULong = 0UL
                            Dim _hi00 As ULong = System.Math.BigMul(_ai5_bot, _bj5_bot, _hi00_lo)
                            Dim _lo01 As ULong = _ai5_bot * _bj51_1
                            Dim _lo10 As ULong = _ai51_1 * _bj5_bot
                            Dim _expProd1 As ULong = _hi00 + _lo01 + _lo10
                            Dim _actProd1 As ULong = _sp5Bot2
                            AppendLog($"[SafeMpzMul§5B-sub k={k} verify1] A_{ki}[1]={_ai51_1:X16} B_{kj}[1]={_bj51_1:X16} hi00={_hi00:X16} lo01={_lo01:X16} lo10={_lo10:X16}  expected prod[1]={_expProd1:X16}  actual prod[1]={_actProd1:X16}  match={(_actProd1 = _expProd1)}{vbCrLf}", 5)   ' §252 (#95)
                        End If
                        ' §5B-sub-T: verify prods(k) TOP limb ≈ hi(A_i[topA] * B_j[topB]).
                        ' The top limb of A_i × B_j is dominated by hi(A_i[topA]*B_j[topB])
                        ' plus a tiny carry from below (typically 0..2 for random data).
                        ' If mpz_mul stripped a leading zero the actual size will be one less
                        ' than expected and actTop ≈ lo(A_i[topA]*B_j[topB]).  A "wildly off"
                        ' sub-product (the working hypothesis) will diverge by a huge amount,
                        ' pinpointing which k's recursive SafeMpzMul produced a wrong top.
                        Dim _szAi As ULong = If(ki = 2, CULng(szA) - 2UL * mA, mA)
                        Dim _szBj As ULong = If(kj = 2, CULng(szB) - 2UL * mB, mB)
                        Dim _topAOff As Long = (CLng(ki) * CLng(mA) + CLng(_szAi) - 1L) * 8L
                        Dim _topBOff As Long = (CLng(kj) * CLng(mB) + CLng(_szBj) - 1L) * 8L
                        Dim _ai5_top As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opA_d5, CInt(_topAOff)))
                        Dim _bj5_top2 As ULong = CULng(Runtime.InteropServices.Marshal.ReadInt64(_opB_d5, CInt(_topBOff)))
                        Dim _expTopLo As ULong = 0UL
                        Dim _expTopHi As ULong = System.Math.BigMul(_ai5_top, _bj5_top2, _expTopLo)
                        Dim _expSzProd As ULong = _szAi + _szBj
                        Dim _expTopForCmp As ULong = If(_expSzProd = CULng(_logPre), _expTopHi, _expTopLo)
                        Dim _topDiff As ULong = _sp5Top - _expTopForCmp
                        AppendLog($"[SafeMpzMul§5B-sub k={k} verifyT] A_{ki}[{_szAi - 1UL:N0}]={_ai5_top:X16} B_{kj}[{_szBj - 1UL:N0}]={_bj5_top2:X16} expHi={_expTopHi:X16} expLo={_expTopLo:X16} expSzProd={_expSzProd:N0} actSzProd={_logPre:N0} actTop={_sp5Top:X16} actTop-1={_sp5Top2:X16} cmpExp={_expTopForCmp:X16} diff(act-exp)={_topDiff:X16}{vbCrLf}", 5)   ' §252 (#95)
                    End If
                End If
                ' §222 (issue #60): use the parallel-pre-shifted buffer when available.
                Dim _shiftedForK222 As IntPtr = If(_useS222, _shifted222(k), _sv_shifted_hdr)
                If shiftBits = 0UL Then
                    ' k=0 has shift=0. When §222 is active, _shifted222(0) holds a COPY of
                    ' prods(0) (we used GmpRaw_set in the pre-pass); adding it is equivalent
                    ' to adding _sv_prod directly. When §222 is OFF, fall through to the
                    ' original direct-add of _sv_prod.
                    If _useS222 Then
                        GmpRaw_add(accumPtr, accumPtr, _shifted222(0))
                    Else
                        GmpRaw_add(accumPtr, accumPtr, _sv_prod)
                    End If
                    If _logLevel >= 5 Then   ' §252 (#95): per-sub-product accumulation spam → level 5
                        Dim _accumSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        AppendLog($"[SafeMpzMul§gen] k={k} shift=0 szProd={_logPre:N0} accumSz={_accumSz:N0}{vbCrLf}")
                    End If
                Else
                    If Not _useS222 Then
                        ' Original inline shift path — runs when §222 pre-pass was skipped
                        ' (caller DOP=1 or operand too small to amortise the parallel cost).
                        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
                        Dim _shiftSrc As IntPtr = _sv_prod
                        Dim _shiftRem As ULong = shiftBits
                        While _shiftRem > 0UL
                            Dim _chunk As UInteger = CUInt(System.Math.Min(_shiftRem, CULng(UInt32.MaxValue)))
                            GmpRaw_mul_2exp(_sv_shifted_hdr, _shiftSrc, _chunk)
                            _shiftSrc = _sv_shifted_hdr
                            _shiftRem -= CULng(_chunk)
                        End While
                    End If
                    ' §150: q*b pre-add check — verify accum[42779664]=0 before adding k=8.
                    ' Only k=8 (shift=29166668 limbs) can reach position 42779664; no k<8 does.
                    ' A nonzero pre-k8 value would reveal an unexpected earlier sub-product bug.
                    If _logLevel >= 2 AndAlso k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                        Dim _pre150sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        Dim _pre150DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                        Dim _pre150v As Long = If(42779664L < CLng(_pre150sz), Runtime.InteropServices.Marshal.ReadInt64(_pre150DPtr, CInt(42779664L * 8L)), 0L)
                        AppendLog($"[SafeMpzMul§150] pre-k8-add accum[42779664]={_pre150v:X16} (expect 0000000000000000){vbCrLf}")
                    End If
                    GmpRaw_add(accumPtr, accumPtr, _shiftedForK222)
                    If _logLevel >= 5 Then   ' §252 (#95): per-sub-product accumulation spam → level 5
                        Dim _shiftedSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(_shiftedForK222, 4))
                        Dim _accumSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        AppendLog($"[SafeMpzMul§gen] k={k} shift={shiftBits:N0} szProd={_logPre:N0} szShifted={_shiftedSz:N0} accumSz={_accumSz:N0}{vbCrLf}")
                        ' §111: For the final a*r call only, log accum at the known error position.
                        If k = 8 AndAlso szA = 43750001 AndAlso szB = 21875001 Then
                            Const _AL As Long = 64654664L
                            Dim _accDiagSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                            Dim _accDiagDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                            Dim _aErrL As Long = If(_AL < CLng(_accDiagSz), Runtime.InteropServices.Marshal.ReadInt64(_accDiagDPtr, CInt(_AL * 8L)), 0L)
                            Dim _aErrL1 As Long = If(_AL + 1L < CLng(_accDiagSz), Runtime.InteropServices.Marshal.ReadInt64(_accDiagDPtr, CInt((_AL + 1L) * 8L)), 0L)
                            AppendLog($"[SafeMpzMul§111] k=8 accum[{_AL:N0}]={_aErrL:X16} accum[{_AL+1:N0}]={_aErrL1:X16}{vbCrLf}")
                        End If
                        ' §130: log accum[42,779,664] after k=8 for q*b call (only contributor to that limb).
                        If k = 8 AndAlso szA = 21875001 AndAlso szB = 21875001 Then
                            Const _AL130 As Long = 42779664L
                            Dim _acc130sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                            Dim _acc130dPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                            Dim _acc130v As Long = If(_AL130 < CLng(_acc130sz), Runtime.InteropServices.Marshal.ReadInt64(_acc130dPtr, CInt(_AL130 * 8L)), 0L)
                            Dim _acc130v1 As Long = If(_AL130 + 1L < CLng(_acc130sz), Runtime.InteropServices.Marshal.ReadInt64(_acc130dPtr, CInt((_AL130 + 1L) * 8L)), 0L)
                            AppendLog($"[SafeMpzMul§130] k=8 accum[{_AL130:N0}]={_acc130v:X16} accum[{_AL130+1:N0}]={_acc130v1:X16} accumSz={_acc130sz:N0}{vbCrLf}")
                        End If
                    End If
                End If
                ' §5B-c3: at the outer 175M × 87.5M call, log accum[218,750,001] (and neighbours)
                ' after each k's accumulation.  Only k=7 (shift=145.83M limbs) and k=8 (shift=175.0M
                ' limbs) reach that index; for k<7 the value should remain zero.  The k that first
                ' introduces the wrong value pinpoints whether the bug is in prods(7)'s middle,
                ' prods(8)'s middle, or the shift+add at one of those k's.
                If _logLevel >= 2 AndAlso szA = 175000001 AndAlso szB = 87500001 Then
                    Const _IDX_C3 As Long = 218750001L
                    Dim _accC3sz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                    Dim _accC3DPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                    Dim _accC3v0 As ULong = If(_IDX_C3 - 1L < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt((_IDX_C3 - 1L) * 8L))), 0UL)
                    Dim _accC3v1 As ULong = If(_IDX_C3 < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt(_IDX_C3 * 8L))), 0UL)
                    Dim _accC3v2 As ULong = If(_IDX_C3 + 1L < CLng(_accC3sz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accC3DPtr, CInt((_IDX_C3 + 1L) * 8L))), 0UL)
                    AppendLog($"[SafeMpzMul§5B-c3 k={k}] post-add accum[{_IDX_C3 - 1L:N0}]={_accC3v0:X16} accum[{_IDX_C3:N0}]={_accC3v1:X16} accum[{_IDX_C3 + 1L:N0}]={_accC3v2:X16} accumSz={_accC3sz:N0}{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' §5B-d-L2: Level-2 recursive C-3 — at the inner SafeMpzMul calls that produce
                ' prods(6/7/8) of the outer 175M × 87.5M call (gated by szA=58,333,333 ∧
                ' szB=29,166,667 — the size of A_2 × any B_j), log accum at the two suspect
                ' indices after each k=0..8 sub-product accumulation.
                '   prods(7) suspect: accum[72,916,666] (the limb that became the wrong outer
                '     prods(7)[72,916,666] = 3E924C7A243168E4 in run 9).
                '   prods(8) suspect: accum[43,749,999] (the limb that contributes to outer
                '     ar[218,750,001] from prods(8) at level 1).
                ' Fingerprint via opB[0] (cached pre-loop):
                '   B_0[0]=88638C785832DAFF (prods(6)), B_1[0]=4B08FAE8DCA50441 (prods(7)),
                '   B_2[0]=0706751D8688C2D3 (prods(8)).
                ' The k' that first introduces a wrong value at the matching index pinpoints
                ' which level-3 sub-product (or which level-2 shift+add step) is the culprit.
                If _logLevel >= 2 AndAlso szA = 58333333 AndAlso szB = 29166667 Then
                    Const _IDX_D_P7 As Long = 72916666L
                    Const _IDX_D_P8 As Long = 43749999L
                    Dim _accDsz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                    Dim _accDDPtr As IntPtr = New IntPtr(Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
                    Dim _accD7 As ULong = If(_IDX_D_P7 < CLng(_accDsz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accDDPtr, CInt(_IDX_D_P7 * 8L))), 0UL)
                    Dim _accD8 As ULong = If(_IDX_D_P8 < CLng(_accDsz), CULng(Runtime.InteropServices.Marshal.ReadInt64(_accDDPtr, CInt(_IDX_D_P8 * 8L))), 0UL)
                    Dim _opBd_d2 As Long = Runtime.InteropServices.Marshal.ReadInt64(opB.Pointer, 8)
                    Dim _opB0_d2 As ULong = If(_opBd_d2 <> 0L, CULng(Runtime.InteropServices.Marshal.ReadInt64(New IntPtr(_opBd_d2), 0)), 0UL)
                    AppendLog($"[SafeMpzMul§5B-d-L2 k={k} opB[0]={_opB0_d2:X16}] post-add accum[{_IDX_D_P7:N0}]={_accD7:X16} accum[{_IDX_D_P8:N0}]={_accD8:X16} accumSz={_accDsz:N0}{vbCrLf}", 5)   ' §252 (#95)
                End If
                ' §212 (2026-05-15): depth-0 RAM diagnostics for top-level §gen calls.
                ' Gated on szA + szB > 800M limbs — at 5B scale this fires ONLY at the
                ' outermost a×r (998M × 259M = 1.26B total).  The 5/15 crash was an
                ' AccessViolation in KernelBase during k=1's shift+add at exactly this depth;
                ' working-set + private-memory + accumPtr alloc-headroom logged here lets us
                ' confirm whether the next crash (if any) is heap pressure vs mpz_t size limit
                ' vs allocator corruption.
                If CLng(szA) + CLng(szB) > 800_000_000L Then
                    Try
                        Dim _212proc As System.Diagnostics.Process = System.Diagnostics.Process.GetCurrentProcess()
                        Dim _212ws As Long = _212proc.WorkingSet64
                        Dim _212priv As Long = _212proc.PrivateMemorySize64
                        Dim _212accSz As Integer = System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
                        Dim _212accAlloc As Integer = Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 0)
                        AppendLog($"[SafeMpzMul§212] depth-0 k={k} END  szA={szA:N0} szB={szB:N0} WS={_212ws \ BYTES_PER_MB:N0}MB Priv={_212priv \ BYTES_PER_MB:N0}MB accumSz={_212accSz:N0} accumAlloc={_212accAlloc:N0}{vbCrLf}", 5)   ' §252 (#95)
                    Catch _212ex As Exception
                        AppendLog($"[SafeMpzMul§212] depth-0 k={k} diag failed: {_212ex.Message}{vbCrLf}", 5)   ' §252 (#95)
                    End Try
                End If
                GmpRaw_clear(prods(k).Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(prods(k).Pointer)
            Next k

            ' §222 (issue #60): free the 9 parallel-shifted buffers, if used.
            If _useS222 Then
                For _k222free As Integer = 0 To 8
                    GmpRaw_clear(_shifted222(_k222free))
                    Runtime.InteropServices.Marshal.FreeHGlobal(_shifted222(_k222free))
                Next
            End If
        End If
        ' §23: Free the shared buffer once, then re-init shifted with a fresh 1-limb stub
        ' so the final GmpRaw_clear below frees it cleanly without double-freeing _sharedSjBuf.
        ' §25: raw re-init — zero the struct fields so GmpRaw_init allocates a fresh limb buffer.
        VirtualFree(_sharedSjBuf, UIntPtr.Zero, MEM_RELEASE)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 0, 0)
        Runtime.InteropServices.Marshal.WriteInt32(_sv_shifted_hdr, 4, 0)
        Runtime.InteropServices.Marshal.WriteInt64(_sv_shifted_hdr, 8, 0L)
        GmpRaw_init(_sv_shifted_hdr)   ' allocates fresh 1-limb buffer; freed by GmpRaw_clear below
        ' §175: Do NOT re-read result.Pointer here.  Math.Gmp.Native corrupts mpz_t.Pointer
        ' for locally-scoped objects during recursive SafeMpzMul calls (§78), so result.Pointer
        ' may point to a wrong struct after sub-product computation.  The original savedResultPtr
        ' (line 2260) and local accumPtr (line 2284) are plain IntPtr locals — immune to
        ' managed-wrapper corruption — and are still correct here (accumulation loop has no
        ' inner SafeMpzMul calls).  Re-reading was unnecessary and caused result.Pointer to be
        ' restored to a corrupted address, making rSq.Pointer point at the wrong struct and
        ' rSq bot/lower-limbs to appear as zero (§121 symptom), giving wrong Newton r.

        ' Copy accumPtr struct to savedResultPtr, then free the 16-byte accumPtr header.
        ' accumBuf ownership transfers to result (via savedResultPtr._mp_d); do NOT free it here.
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 0, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 0))
        Runtime.InteropServices.Marshal.WriteInt32(savedResultPtr, 4, Runtime.InteropServices.Marshal.ReadInt32(accumPtr, 4))
        Runtime.InteropServices.Marshal.WriteInt64(savedResultPtr, 8, Runtime.InteropServices.Marshal.ReadInt64(accumPtr, 8))
        result.Pointer = savedResultPtr
        Runtime.InteropServices.Marshal.FreeHGlobal(accumPtr)
        accumPtr = IntPtr.Zero
        If _logLevel >= 4 Then AppendLog(
            $"[SafeMpzMul] done: szA={szA:N0} szB={szB:N0} → {GmpRaw_sizeinbase(savedResultPtr, 10):N0} digits{vbCrLf}")

        ' §42: negate via raw P/Invoke so result.Pointer corruption cannot affect the call.
        If resultSign < 0 Then GmpRaw_neg(savedResultPtr, savedResultPtr)

        ' §59: prods(0..8) are already cleared inside the accumulation loop.
        GmpRaw_clear(shifted.Pointer) : Runtime.InteropServices.Marshal.FreeHGlobal(shifted.Pointer)
        ' §90: zero-copy pieces — only free the 16-byte struct headers; _mp_d aliases opA/opB so
        ' GmpRaw_clear must NOT be called (it would free opA/opB's limb buffer — catastrophic).
        ' Null out Pointer before FreeHGlobal: mpz_t's GC finalizer calls mpz_clear if Pointer≠null,
        ' which would pass opA/opB's _mp_d to GmpFreeFunc — a concurrent double-free.
        Dim _A0p = A0.Pointer : A0.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A0p)
        Dim _A1p = A1.Pointer : A1.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A1p)
        Dim _A2p = A2.Pointer : A2.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_A2p)
        Dim _B0p = B0.Pointer : B0.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B0p)
        Dim _B1p = B1.Pointer : B1.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B1p)
        Dim _B2p = B2.Pointer : B2.Pointer = IntPtr.Zero : Runtime.InteropServices.Marshal.FreeHGlobal(_B2p)
        AppendLog(
            $"[SafeMpzMul] cleared: szA={szA:N0} szB={szB:N0}{vbCrLf}")
    End Sub
End Class
