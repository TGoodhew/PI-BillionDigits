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

    ' ════════════════════════════════════════════════════════════════════════
    '  §99: Safe 10^n via repeated squaring using SafeMpzMul
    ' ════════════════════════════════════════════════════════════════════════
    ''' <summary>
    ''' Computes result = 10^exponent using binary (repeated squaring) with
    ''' SafeMpzMul for every multiplication, avoiding the GMP 32-bit mpn_mul_fft
    ''' overflow that crashes mpz_ui_pow_ui when the intermediate values exceed
    ''' ~33M limbs (~2GB).  At 5B digits, 10^2,500,000,000 ≈ 130M limbs — well
    ''' above the threshold.
    ''' </summary>
    Private Sub SafeMpzPow10(result As mpz_t, exponent As Long)
        ' Seed: result = 1
        gmp_lib.mpz_set_ui(result, 1UI)

        If exponent = 0L Then Return

        ' base = 10
        Dim base As New mpz_t()
        gmp_lib.mpz_init_set_ui(base, 10UI)

        Dim exp As Long = exponent
        Do
            If (exp And 1L) = 1L Then
                ' result *= base
                Dim tmp As New mpz_t()
                gmp_lib.mpz_init(tmp)
                SafeMpzMul(tmp, result, base)
                gmp_lib.mpz_swap(result, tmp)
                gmp_lib.mpz_clear(tmp)
            End If
            exp >>= 1
            If exp = 0L Then Exit Do
            ' base = base^2
            Dim tmp2 As New mpz_t()
            gmp_lib.mpz_init(tmp2)
            SafeMpzMul(tmp2, base, base)
            gmp_lib.mpz_swap(base, tmp2)
            gmp_lib.mpz_clear(tmp2)
        Loop

        gmp_lib.mpz_clear(base)
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §216: Chunked decimal conversion (replaces mpz_get_str for huge outputs)
    ' ════════════════════════════════════════════════════════════════════════
    ' GMP's mpz_get_str crashes with 0xC0000005 AccessViolation when the output
    ' exceeds approximately 2 GB.  Observed on the 2026-05-19 5B run: §piCkpt
    ' fired (gmpPi safely saved), then mpz_get_str was called on the 5B-digit
    ' result (output ≈ 5 GB) and crashed inside __gmpz_get_str.  The whole
    ' multi-hour SafeMpzDiv pipeline ran to completion successfully — only
    ' final decimal conversion failed.
    '
    ' Root cause is likely Int32 overflow in mpz_get_str's recursive
    ' divide-and-conquer (mpn_dc_get_str / mpn_dc_get_str_powtab in GMP source).
    ' Each recursion level computes positions in the output buffer using
    ' mp_size_t (int = 32 bits on Windows x64).  Once output_size > 2^31 bytes,
    ' those positions can wrap and dereference outside the buffer.
    '
    ' Fix: iteratively extract 300M-digit slabs from gmpPi via
    '   rem = pi mod 10^300M ; pi = pi // 10^300M
    ' and call mpz_get_str on each rem (output ≤ 300 MB, well within safe range).
    ' Write each slab right-to-left into a pre-allocated VirtualAlloc buffer,
    ' padding non-MSB slabs with leading zeros to 300M chars.  After all slabs
    ' extracted, memmove the contents to offset 0.
    '
    ' For 5B digits: 17 slabs.  Cost dominated by 17 × mpz_fdiv_qr at
    ' shrinking dividend sizes (5B → 4.7B → ... → 300M digits) divided by a
    ' fixed 300M-digit divisor.  Expected wall time at 5B: ~4-8 h.
    '
    ' NOTE: §270 (#90) shipped the parallel recursive-halving converter that issue #37 had only
    ' planned — see ParallelMpzGetStr.  As of §270 that parallel path is the DEFAULT at ≥ 1.5 B digits;
    ' this §216 serial slab converter is now the fallback (selected when PI_CONV_PARALLEL=0).
    ''' <summary>
    ''' Serial chunked decimal conversion (§216) — replaces GMP's mpz_get_str, which crashes with an
    ''' AccessViolation when the output exceeds ~2 GB (Int32 overflow in its divide-and-conquer). Extracts
    ''' fixed-size decimal slabs via repeated (rem = pi mod 10^k ; pi = pi \ 10^k), converts each slab
    ''' (safely small) and writes them right-to-left into a pre-allocated native buffer.
    ''' Superseded as the default by <see cref="ParallelMpzGetStr"/> (§270); used as the fallback.
    ''' </summary>
    ''' <param name="pi">The computed value to render (consumed/shrunk during extraction).</param>
    ''' <param name="totalDigitsEstimate">Estimated decimal digit count, used to size the output buffer.</param>
    Private Sub ChunkedMpzGetStr(pi As mpz_t, totalDigitsEstimate As Long)
        ' §216d: reduced chunk from 300M → 50M.  At 300M chunk, mpz_get_str crashed
        ' inside __gmpn_dc_get_str on a 15.5M-limb chunkRem producing 300M-char output.
        ' At 50M chunk: chunkRem is ~2.6M limbs, mpz_get_str internal temps drop from
        ' ~3 GB to ~200 MB.  Trade-off: 100 fdiv_qr iterations instead of 17, but each
        ' is faster (smaller 10^50M divisor).  Net throughput is similar.
        Const CHUNK_DIGITS As Long = 50_000_000L
        AppendLog($"[§216d] Chunked decimal conversion start: totalDigitsEstimate={totalDigitsEstimate:N0} chunk={CHUNK_DIGITS:N0}{vbCrLf}")

        ' Allocate output buffer: totalDigits + 16 bytes slack (room for null terminator + a safety margin)
        Dim bufSize As Long = totalDigitsEstimate + 16L
        Dim outBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(bufSize)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If outBuf = IntPtr.Zero Then
            Throw New OutOfMemoryException($"§216: VirtualAlloc {bufSize:N0} bytes for output buffer failed")
        End If
        AppendLog($"[§216] Output buffer: {bufSize:N0} bytes at 0x{outBuf.ToInt64():X16}{vbCrLf}")

        ' Compute 10^CHUNK_DIGITS — one-time cost.  Result is a CHUNK_DIGITS-digit number,
        ' ≈ 15.5M limbs ≈ 124 MB in mpz_t native form.  Cost: log2(CHUNK_DIGITS) ≈ 28 squarings,
        ' last of which is (CHUNK_DIGITS/2)-digit × (CHUNK_DIGITS/2)-digit.  Takes ~minutes.
        Dim _powStart As DateTime = DateTime.Now
        Dim D As New mpz_t()
        gmp_lib.mpz_init(D)
        gmp_lib.mpz_ui_pow_ui(D, 10UI, CULng(CHUNK_DIGITS))
        AppendLog($"[§216] 10^{CHUNK_DIGITS:N0} computed in {(DateTime.Now - _powStart).TotalMinutes:F2} min{vbCrLf}")

        ' piMutable: working copy of pi, divided down each iteration.
        ' §216a: PreAlloc to pi's size BEFORE mpz_set, otherwise GMP's MPZ_REALLOC inside
        ' mpz_set aborts with "overflow in mpz type" when needed > INT_MAX/64 = 33,554,431
        ' limbs.  At 5B digits pi is ~260M limbs.  PreAllocMpzToLimbs writes _mp_alloc
        ' directly via Marshal.WriteInt32, bypassing GMP's check entirely.
        Dim piMutable As New mpz_t()
        gmp_lib.mpz_init(piMutable)
        Dim _piSrcSize As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(pi.Pointer, 4)))
        PreAllocMpzToLimbs(piMutable, _piSrcSize + 2L)
        AppendLog($"[§216] piMutable PreAlloc'd to {(_piSrcSize + 2L):N0} limbs before mpz_set{vbCrLf}")
        gmp_lib.mpz_set(piMutable, pi)

        ' §216d: free the caller's pi (= gmpPi) buffer NOW — we've copied to piMutable
        ' and never reference pi again.  Saves ~2 GB during the chunked conversion,
        ' lowering peak commit pressure.  Reinit to a 1-limb stub so the caller's
        ' mpz_clear(gmpPi) at line 7192 is still safe.
        gmp_lib.mpz_clear(pi)
        gmp_lib.mpz_init(pi)
        AppendLog($"[§216d] caller's pi buffer freed (~2 GB) after copy to piMutable{vbCrLf}")

        ' chunkRem: pre-allocate to divisor size + a few limbs (max possible remainder size).
        ' 15.5M < 33.5M so GMP's auto-realloc would also work, but PreAlloc avoids the
        ' first-iteration small→large realloc round-trip.
        Dim chunkRem As New mpz_t()
        gmp_lib.mpz_init(chunkRem)
        Dim _dAlloc As Long = CLng(Runtime.InteropServices.Marshal.ReadInt32(D.Pointer, 0))
        PreAllocMpzToLimbs(chunkRem, _dAlloc + 2L)

        ' §216b: GMP's mpz_fdiv_qr crashes silently (stack overflow in TMP_ALLOC) when
        ' the quotient destination aliases the dividend (quot == num == piMutable).
        ' At 244M-limb quotient, GMP's internal aliasing-handler allocates ~2 GB of
        ' scratch via TMP_ALLOC_LIMBS, which on Windows's 1 MB default stack overflows
        ' before our NativeAllocFunc even gets called.  Use a separate quotient mpz_t
        ' to avoid aliasing entirely, then mpz_set the result back into piMutable for
        ' the next iteration (mpz_set's MPZ_REALLOC sees alloc >= needed, skips abort).
        Dim quotTmp As New mpz_t()
        gmp_lib.mpz_init(quotTmp)
        PreAllocMpzToLimbs(quotTmp, _piSrcSize + 2L)
        AppendLog($"[§216b] quotTmp PreAlloc'd to {(_piSrcSize + 2L):N0} limbs (de-aliased fdiv_qr){vbCrLf}")

        ' chunkEndPos: exclusive upper bound in outBuf where the next chunk will end.
        ' Starts at bufSize-1 (reserve last byte for null terminator).
        Dim chunkEndPos As Long = bufSize - 1L
        Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + chunkEndPos), CByte(0))   ' null terminator

        Dim chunkIdx As Long = 0

        ' §74 (issue #74): publish total-chunk count so the _strConvTimer UI callback can
        ' show "chunk N of M".  totalDigitsEstimate is mpz_sizeinbase(gmpPi, 10) — within
        ' ±1 of the true digit count.  Ceiling division gives the exact chunk count the
        ' loop below will produce.
        Dim totalChunks As Long = (totalDigitsEstimate + CHUNK_DIGITS - 1L) \ CHUNK_DIGITS
        _chunkConvTotal = totalChunks
        _chunkConvCurrent = 0

        Try

        While gmp_lib.mpz_sgn(piMutable) > 0
            Dim _chunkStart As DateTime = DateTime.Now
            Dim _piSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(piMutable.Pointer, 4)))
            AppendLog($"[§216c] iter {chunkIdx + 1L} start: piMutable_sz={_piSz:N0} → mpz_fdiv_qr...{vbCrLf}")
            _chunkConvCurrent = chunkIdx + 1L   ' §74: 1-based "current chunk" for the UI

            ' §216b: de-aliased call — quot=quotTmp, num=piMutable, rem=chunkRem, den=D.
            ' Then mpz_set(piMutable, quotTmp) for next iteration (no realloc, alloc already 260M).
            gmp_lib.mpz_fdiv_qr(quotTmp, chunkRem, piMutable, D)
            Dim _qSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(quotTmp.Pointer, 4)))
            Dim _rSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 4)))
            AppendLog($"[§216c] iter {chunkIdx + 1L}: fdiv_qr done, qSz={_qSz:N0} rSz={_rSz:N0} → mpz_set...{vbCrLf}")
            gmp_lib.mpz_set(piMutable, quotTmp)
            AppendLog($"[§216c] iter {chunkIdx + 1L}: mpz_set done → mpz_get_str on rem (chunkRem alloc={Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 0):N0} size={Runtime.InteropServices.Marshal.ReadInt32(chunkRem.Pointer, 4):N0})...{vbCrLf}")
            Dim _divElapsed As TimeSpan = DateTime.Now - _chunkStart

            ' §216e: log mpz_get_str's return value and strlen separately to pinpoint crash.
            Dim chunkCharPtr As char_ptr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, chunkRem)
            AppendLog($"[§216e] iter {chunkIdx + 1L}: mpz_get_str returned ptr=0x{chunkCharPtr.Pointer.ToInt64():X16}{vbCrLf}")

            Dim chunkLen As Long = CLng(strlen(chunkCharPtr.Pointer).ToUInt64())
            AppendLog($"[§216e] iter {chunkIdx + 1L}: strlen returned chunkLen={chunkLen:N0}{vbCrLf}")

            Dim isTop As Boolean = (gmp_lib.mpz_sgn(piMutable) = 0)
            Dim writeAt As Long
            Dim zeroPadCount As Long = 0

            If isTop Then
                ' MSB chunk: write actual chunkLen bytes; no leading-zero padding.
                writeAt = chunkEndPos - chunkLen
                AppendLog($"[§216e] iter {chunkIdx + 1L}: isTop=True writeAt={writeAt:N0} → CopyMemory({chunkLen:N0} bytes)...{vbCrLf}")
                CopyMemory(New IntPtr(outBuf.ToInt64() + writeAt), chunkCharPtr.Pointer, New UIntPtr(CULng(chunkLen)))
                AppendLog($"[§216e] iter {chunkIdx + 1L}: CopyMemory done{vbCrLf}")
            Else
                ' Non-top chunk: must fill exactly CHUNK_DIGITS columns, padded with leading zeros.
                writeAt = chunkEndPos - CHUNK_DIGITS
                zeroPadCount = CHUNK_DIGITS - chunkLen
                AppendLog($"[§216e] iter {chunkIdx + 1L}: isTop=False writeAt={writeAt:N0} zeroPad={zeroPadCount:N0} → write zeros...{vbCrLf}")
                Dim _padDest As Long = outBuf.ToInt64() + writeAt
                For i As Long = 0 To zeroPadCount - 1
                    Runtime.InteropServices.Marshal.WriteByte(New IntPtr(_padDest + i), CByte(48))   ' '0' = 0x30
                Next
                AppendLog($"[§216e] iter {chunkIdx + 1L}: zeros written → CopyMemory({chunkLen:N0} bytes)...{vbCrLf}")
                CopyMemory(New IntPtr(outBuf.ToInt64() + writeAt + zeroPadCount), chunkCharPtr.Pointer, New UIntPtr(CULng(chunkLen)))
                AppendLog($"[§216e] iter {chunkIdx + 1L}: CopyMemory done{vbCrLf}")
            End If

            chunkEndPos = writeAt

            ' §216f: use GmpNativeAlloc_FreeRaw, NOT _savedGmpFree.  _savedGmpFree is the
            ' ORIGINAL GMP allocator's free (CRT free) saved before GmpNativeAlloc_Install
            ' replaced the callbacks.  The chunk buffer was allocated by our REPLACEMENT
            ' NativeAllocFunc via VirtualAlloc — calling CRT free() on a VirtualAlloc'd
            ' pointer crashes the process.  GmpNativeAlloc_FreeRaw routes correctly to
            ' our NativeFreeFunc (VirtualFree for oversized blocks, pool return for ≤16 MB).
            AppendLog($"[§216f] iter {chunkIdx + 1L}: about to GmpNativeAlloc_FreeRaw chunkCharPtr...{vbCrLf}")
            GmpNativeAlloc_FreeRaw(chunkCharPtr.Pointer, chunkLen + 1L)
            AppendLog($"[§216f] iter {chunkIdx + 1L}: chunkCharPtr freed{vbCrLf}")

            chunkIdx += 1
            Dim _chunkTotal As TimeSpan = DateTime.Now - _chunkStart
            AppendLog($"[§216] chunk {chunkIdx} done: chunkLen={chunkLen:N0} isTop={isTop} pad={zeroPadCount:N0} writeAt={writeAt:N0} (div={_divElapsed.TotalMinutes:F1}m, total={_chunkTotal.TotalMinutes:F1}m){vbCrLf}")
        End While

        Finally
            ' §74 (issue #74): clear progress fields so the next mpz_get_str call (small-scale
            ' path that doesn't enter this function) shows the original "String conversion..."
            ' status instead of stale "chunk 100 of 100" text.
            _chunkConvCurrent = 0
            _chunkConvTotal = 0
        End Try

        ' Clean up GMP scratch.
        gmp_lib.mpz_clear(piMutable)
        gmp_lib.mpz_clear(chunkRem)
        gmp_lib.mpz_clear(D)
        gmp_lib.mpz_clear(quotTmp)

        ' The actual content lives in outBuf[chunkEndPos .. bufSize-1] with a null terminator at bufSize-1.
        ' Shift it back to offset 0 so downstream code sees the digits starting at outBuf[0].
        Dim actualStart As Long = chunkEndPos
        Dim actualLen As Long = (bufSize - 1L) - actualStart   ' excludes null terminator
        AppendLog($"[§216] actualStart={actualStart:N0} actualLen={actualLen:N0}{vbCrLf}")

        If actualStart > 0 Then
            Dim _moveStart As DateTime = DateTime.Now
            ' RtlMoveMemory handles overlap correctly.
            CopyMemory(outBuf, New IntPtr(outBuf.ToInt64() + actualStart), New UIntPtr(CULng(actualLen)))
            ' Re-place null terminator at the new end of content.
            Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + actualLen), CByte(0))
            AppendLog($"[§216] memmove of {actualLen:N0} bytes done in {(DateTime.Now - _moveStart).TotalSeconds:F2}s{vbCrLf}")
        End If

        ' Populate display state so downstream code (WritePiDigitsToFile, autoVerify) works unchanged.
        _displayNativePtr = outBuf
        _displayNativeLen = actualLen + 1L   ' includes null terminator (mirrors mpz_get_str path)
        _displayNativeBufSize = bufSize

        AppendLog($"[§216] Chunked decimal conversion complete: {actualLen:N0} digits in {chunkIdx} chunks{vbCrLf}", 1)   ' §252 (#95): final digit-count result → level 1
    End Sub

    ' ════════════════════════════════════════════════════════════════════════
    '  §226: Parallel recursive-halving decimal conversion (issue #37)
    ' ════════════════════════════════════════════════════════════════════════
    ' Replaces the strictly-sequential §216 chunked converter (and GMP's serial
    ' internal mpz_get_str) with a parallel binary-tree halving:
    '
    '   ParallelBase10(n, digits, outBuf, offset):
    '     if digits <= LEAF: outBuf[offset..] = mpz_get_str(n), left-padded with '0'
    '     else:
    '       D = 10^(digits/2)         ' from pre-built power table
    '       (hi, lo) = mpz_fdiv_qr(n, D)
    '       Parallel.Invoke(
    '         HalveBase10(hi, hiDigits, outBuf, offset),
    '         HalveBase10(lo, halfDigits, outBuf, offset + hiDigits))
    '
    ' Key design points:
    '   * Power-of-10 table pre-built sequentially before any recursion fires.
    '     Sizes determined by walking the recursion tree (≤ log2(digits/LEAF)
    '     distinct powers).
    '   * Critical path = one fdiv_qr per level, sizes halving each level.
    '     Wall ≈ 2 × top-level-fdiv_qr (geometric series).
    '   * Each non-leaf node allocates its own hi/lo mpz_t — no shared mutable
    '     state during parallel recursion.  Power table is read-only.
    '   * PreAllocMpzToLimbs on hi/lo bypasses GMP's 33.5M-limb realloc abort
    '     for large operands (same hazard as §216a / §218 / §225).
    '   * outBuf is written left-to-right; each leaf knows its absolute offset.
    '     No final memmove needed (unlike §216's right-to-left strategy).
    '   * Verification: byte-identical output to GMP's mpz_get_str / §216
    '     chunked path.  Validated at 1B against the 2026-05-22 post-§225
    '     verified pi_digits.txt.
    ''' <summary>
    ''' Parallel recursive-halving decimal converter (§226/§270, #90) — the DEFAULT decimal conversion
    ''' at ≥ 1.5 B digits (opt out with PI_CONV_PARALLEL=0 to use the §216 serial path). Splits the value
    ''' against a pre-built power-of-10 table and recurses in parallel, writing each leaf's digits at its
    ''' absolute output offset. 5 B-safe via the §270 safe-peel split rule; byte-identical to mpz_get_str.
    ''' </summary>
    ''' <param name="pi">The computed value to render.</param>
    ''' <param name="totalDigitsEstimate">Estimated decimal digit count, used to size the output buffer and power table.</param>
    Private Sub ParallelMpzGetStr(pi As mpz_t, totalDigitsEstimate As Long)
        Const LEAF_THRESHOLD As Long = 50_000_000L

        Dim _t0 As DateTime = DateTime.Now
        AppendLog($"[§226] Parallel decimal conversion start: totalDigitsEstimate={totalDigitsEstimate:N0} leaf={LEAF_THRESHOLD:N0}{vbCrLf}")

        ' Allocate output buffer (totalDigits + 16 slack; null terminator at end).
        Dim bufSize As Long = totalDigitsEstimate + 16L
        Dim outBuf As IntPtr = VirtualAlloc(IntPtr.Zero, New UIntPtr(CULng(bufSize)), MEM_COMMIT_RESERVE, VA_PAGE_READWRITE)
        If outBuf = IntPtr.Zero Then
            Throw New OutOfMemoryException($"§226: VirtualAlloc {bufSize:N0} bytes failed")
        End If
        AppendLog($"[§226] Output buffer: {bufSize:N0} bytes at 0x{outBuf.ToInt64():X16}{vbCrLf}")

        ' Actual digit count (mpz_sizeinbase over-estimates by 1 at most).
        Dim actualDigits As Long = CLng(gmp_lib.mpz_sizeinbase(pi, 10UI))

        ' Walk the recursion tree to collect the unique D-sizes (10^halfDigits at each
        ' non-leaf node).  Worklist holds digit counts still to explore.
        Dim neededD As New System.Collections.Generic.HashSet(Of Long)()
        Dim seen As New System.Collections.Generic.HashSet(Of Long)()
        Dim work As New System.Collections.Generic.Queue(Of Long)()
        seen.Add(actualDigits)
        work.Enqueue(actualDigits)
        While work.Count > 0
            Dim d As Long = work.Dequeue()
            If d <= LEAF_THRESHOLD Then Continue While
            Dim halfD As Long = ConvSplitLowDigits(d)
            Dim hiD As Long = d - halfD
            neededD.Add(halfD)
            If seen.Add(halfD) Then work.Enqueue(halfD)
            If seen.Add(hiD) Then work.Enqueue(hiD)
        End While
        Dim _ordered As List(Of Long) = neededD.OrderBy(Function(x) x).ToList()
        AppendLog($"[§226] Powers-of-10 needed: {_ordered.Count} sizes ({String.Join(", ", _ordered)}){vbCrLf}")

        ' Compute each 10^k sequentially (mpz_ui_pow_ui's internal squaring already
        ' competes with itself for FFT scratch).  Cost dominated by the largest power.
        Dim powTable As New System.Collections.Generic.Dictionary(Of Long, IntPtr)()
        For Each k As Long In _ordered
            Dim _kt As DateTime = DateTime.Now
            Dim m As New mpz_t()
            gmp_lib.mpz_init(m)
            gmp_lib.mpz_ui_pow_ui(m, 10UI, CUInt(k))
            powTable(k) = m.Pointer
            AppendLog($"[§226] 10^{k:N0} computed in {(DateTime.Now - _kt).TotalSeconds:F1}s{vbCrLf}")
        Next
        Dim _powElapsed As Double = (DateTime.Now - _t0).TotalSeconds
        AppendLog($"[§226] Power table done in {_powElapsed:F1}s total{vbCrLf}")

        ' Working copy of pi so we can free the caller's buffer pre-conversion.
        ' §216a/d pattern: PreAlloc + mpz_set, then free pi + reinit to stub.
        Dim piCopy As New mpz_t()
        gmp_lib.mpz_init(piCopy)
        Dim _piSrcSize As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(pi.Pointer, 4)))
        PreAllocMpzToLimbs(piCopy, _piSrcSize + 2L)
        gmp_lib.mpz_set(piCopy, pi)
        gmp_lib.mpz_clear(pi)
        gmp_lib.mpz_init(pi)
        AppendLog($"[§226] piCopy made ({(_piSrcSize * 8L / BYTES_PER_MB):N0} MB); caller pi freed{vbCrLf}")

        ' Recursive halving — writes exactly actualDigits chars at outBuf[0..actualDigits].
        Dim _convStart As DateTime = DateTime.Now
        HalveBase10(piCopy, actualDigits, outBuf, 0L, powTable, LEAF_THRESHOLD)
        Dim _convElapsed As Double = (DateTime.Now - _convStart).TotalSeconds
        AppendLog($"[§226] Recursive halving done in {_convElapsed:F1}s{vbCrLf}")

        ' Null terminator at end of digits.
        Runtime.InteropServices.Marshal.WriteByte(New IntPtr(outBuf.ToInt64() + actualDigits), CByte(0))

        ' Clean up power table + piCopy.
        For Each kv As KeyValuePair(Of Long, IntPtr) In powTable
            Dim mTmp As New mpz_t()
            mTmp.Pointer = kv.Value
            gmp_lib.mpz_clear(mTmp)
        Next
        gmp_lib.mpz_clear(piCopy)

        ' Display state for downstream code (WritePiDigitsToFile, autoVerify).
        _displayNativePtr = outBuf
        _displayNativeLen = actualDigits + 1L
        _displayNativeBufSize = bufSize

        Dim _totalElapsed As Double = (DateTime.Now - _t0).TotalSeconds
        AppendLog($"[§226] Parallel decimal conversion complete: {actualDigits:N0} digits in {_totalElapsed:F2}s (powTable={_powElapsed:F1}s + convert={_convElapsed:F1}s){vbCrLf}", 1)   ' §252 (#95): final digit-count result → level 1
    End Sub

    ' §270 (#90): 5B-safe split point for the §226 converter.  A half-split (digits/2) needs a
    ' 10^(digits/2) divisor; at >1B digits that exceeds GMP's ~33.5M-limb FFT ceiling and the divisor
    ' build / fdiv_qr crashes or falls off a cliff (#89).  So when digits is large, peel a FIXED
    ' CONV_SAFE_PEEL-digit chunk from the low end instead (divisor 10^CONV_SAFE_PEEL ≈ 26M limbs,
    ' safe) — sequential at the top, but each peeled ≤CONV_SAFE_PEEL slab still halves IN PARALLEL via
    ' HalveBase10's Parallel.Invoke (the hi-recursion peels the next chunk while the lo-slab converts),
    ' so parallelism is preserved while every divisor stays FFT-safe.  The split point does not change
    ' the result, so the output is byte-identical to a pure half-split (and to §216).
    Private Const CONV_SAFE_PEEL As Long = 500_000_000L   ' 10^500M ≈ 26M limbs < 33.5M FFT cap
    Private Shared Function ConvSplitLowDigits(digits As Long) As Long
        If digits > 2L * CONV_SAFE_PEEL Then Return CONV_SAFE_PEEL   ' peel a safe chunk; hi peels again
        Return digits \ 2                                           ' small enough ⇒ halve in parallel
    End Function

    ' §226 helper: recursive halving.  Writes exactly `digits` chars to outBuf[offset..],
    ' zero-padding if n's actual decimal length is < digits (low-half slots of parents).
    ' Thread-safe re-entrant: each call has its own hi/lo; powTable is read-only.
    Private Shared Sub HalveBase10(n As mpz_t, digits As Long, outBuf As IntPtr, offset As Long, powTable As System.Collections.Generic.Dictionary(Of Long, IntPtr), leafThreshold As Long)
        If digits <= leafThreshold Then
            ' Leaf: serial mpz_get_str + write with leading-zero padding.
            Dim charPtr As char_ptr = gmp_lib.mpz_get_str(char_ptr.Zero, 10, n)
            Dim chunkLen As Long = CLng(strlen(charPtr.Pointer).ToUInt64())
            Dim zeroPad As Long = digits - chunkLen
            If zeroPad < 0L Then
                Throw New InvalidOperationException($"§226 leaf: chunkLen={chunkLen} > digits={digits} at offset={offset:N0} — power-of-10 split inconsistency")
            End If
            If zeroPad > 0L Then
                Dim _padDest As Long = outBuf.ToInt64() + offset
                For i As Long = 0L To zeroPad - 1L
                    Runtime.InteropServices.Marshal.WriteByte(New IntPtr(_padDest + i), CByte(48))   ' '0' = 0x30
                Next
            End If
            CopyMemory(New IntPtr(outBuf.ToInt64() + offset + zeroPad), charPtr.Pointer, New UIntPtr(CULng(chunkLen)))
            GmpNativeAlloc_FreeRaw(charPtr.Pointer, chunkLen + 1L)
            Return
        End If

        ' Non-leaf: split via D = 10^halfDigits.  hi gets the top `hiDigits` chars (offset),
        ' lo gets the bottom `halfDigits` chars (offset + hiDigits).
        Dim halfDigits As Long = ConvSplitLowDigits(digits)
        Dim hiDigits As Long = digits - halfDigits
        Dim dPtr As IntPtr = powTable(halfDigits)

        Dim hi As New mpz_t()
        Dim lo As New mpz_t()
        gmp_lib.mpz_init(hi)
        gmp_lib.mpz_init(lo)

        ' PreAlloc to bypass GMP's 33.5M-limb realloc abort (§216a/§218 hazard).
        Dim _nSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(n.Pointer, 4)))
        PreAllocMpzToLimbs(hi, _nSz + 2L)
        Dim _dSz As Long = CLng(System.Math.Abs(Runtime.InteropServices.Marshal.ReadInt32(dPtr, 4)))
        PreAllocMpzToLimbs(lo, _dSz + 2L)

        Dim _dWrap As New mpz_t()
        _dWrap.Pointer = dPtr
        gmp_lib.mpz_fdiv_qr(hi, lo, n, _dWrap)

        ' Parallel.Invoke is synchronous — both sub-calls finish before we hit mpz_clear.
        System.Threading.Tasks.Parallel.Invoke(
            Sub() HalveBase10(hi, hiDigits, outBuf, offset, powTable, leafThreshold),
            Sub() HalveBase10(lo, halfDigits, outBuf, offset + hiDigits, powTable, leafThreshold))

        gmp_lib.mpz_clear(hi)
        gmp_lib.mpz_clear(lo)
    End Sub
End Class
