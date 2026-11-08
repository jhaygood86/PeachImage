using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Webp.Decoding.Vp8.Dct;

/// <summary>
/// Vectorized form of <see cref="Vp8ScalarInverseDct.TransformFullAndAdd"/>: the general (has-AC-coefficients)
/// inverse DCT, for a block known to carry at least one AC coefficient.
/// </summary>
/// <remarks>
/// <para>
/// Unlike JPEG's AAN kernel, every operation here is exact integer arithmetic (add/subtract/multiply/shift) —
/// there is no floating-point rounding to approximate, so this is provably bit-identical to the scalar form
/// rather than merely close to it, and the tests hold it to exactly that standard.
/// </para>
/// <para>
/// The scalar butterfly runs twice: once per column (4 independent 4-input butterflies, one per original
/// column), producing an intermediate; then once per row (4 more independent butterflies, reading that
/// intermediate transposed). Both passes are 4 independent butterflies with no cross-lane interaction within
/// a pass — exactly what SIMD lanes are for — but each pass's <em>output</em> needs to become the next pass's
/// <em>lanes</em>, which needs a real transpose in between and cannot be done lane-wise. Reusing the same
/// <see cref="Transpose4x4"/> both before and after the second butterfly (once to feed it, once to turn its
/// row-indexed output back into contiguous per-row pixel groups) is what makes this a column-pass /
/// transpose / row-pass / transpose shape, matching how libwebp's own <c>TransformOne_SSE2</c> and
/// <c>Jpeg.Dct.AanVectorButterfly.Transpose8x8</c> in this codebase both structure a block transform.
/// </para>
/// </remarks>
internal static class Vp8VectorInverseDct
{
    /// <summary>Whether <see cref="TransformFullAndAdd(ReadOnlySpan{short}, Span{byte}, int, int)"/> is usable — the 4x4 transpose is built from <see cref="Vp8Interleave.UnpackLow(Vector128{int}, Vector128{int})"/>/<see cref="Vp8Interleave.UnpackHigh(Vector128{int}, Vector128{int})"/>, which map to <c>punpck*</c> on x86 and <c>zip1/zip2</c> on Arm64 (the portable vector API has no two-vector interleave).</summary>
    public static bool CanTransform => Vp8Interleave.IsSupported;

    private static readonly Vector128<int> C1 = Vector128.Create(Vp8InverseTransformConstants.C1);
    private static readonly Vector128<int> C2 = Vector128.Create(Vp8InverseTransformConstants.C2);
    private static readonly Vector128<int> Four = Vector128.Create(4);
    private static readonly Vector128<int> Zero = Vector128<int>.Zero;
    private static readonly Vector128<int> TwoFiftyFive = Vector128.Create(255);

    /// <inheritdoc cref="Vp8ScalarInverseDct.TransformFullAndAdd"/>
    public static void TransformFullAndAdd(ReadOnlySpan<short> coefficients, Span<byte> dst, int offset, int stride)
    {
        // One range check for the whole block (16 coefficients; four rows of four pixels), in place of a check per load and store.
        if (coefficients.Length < 16 || offset < 0 || stride < 0 || (long)offset + (3L * stride) + 4 > dst.Length)
        {
            throw new ArgumentException("The block lies outside its buffers.");
        }

        TransformFullAndAdd(ref MemoryMarshal.GetReference(coefficients), ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), offset), stride);
    }

    /// <summary>
    /// Adds a block whose only coefficient is DC: the inverse transform of such a block is the constant <c>(dc + 4) &gt;&gt; 3</c>,
    /// added to all sixteen pixels (the scalar <see cref="Vp8ScalarInverseDct.TransformDcOnlyAndAdd"/> on four-pixel vectors).
    /// The caller guarantees the 4x4 pixels at <paramref name="dst"/> and 16 coefficients at <paramref name="coefficients"/> exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddDcOnly(ref short coefficients, ref byte dst, int stride)
    {
        // (dc + 4) >> 3 of an int16 fits in 13 bits, so the short lanes below cannot overflow.
        var dc = Vector128.Create((short)((coefficients + 4) >> 3));
        var zero = Vector128<short>.Zero;
        var max = Vector128.Create((short)255);

        for (int row = 0; row < 4; row++)
        {
            ref byte line = ref Unsafe.Add(ref dst, row * stride);
            var pixels = Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref line)).AsByte()).AsInt16();
            var sum = Vector128.Min(Vector128.Max(pixels + dc, zero), max).AsUInt16();
            Unsafe.WriteUnaligned(ref line, Vector128.Narrow(sum, sum).AsUInt32().ToScalar());
        }
    }

    /// <summary>
    /// The inverse transform of a block whose only nonzero coefficients are at raster positions 0, 1 and 4 (the first three in scan
    /// order), added to the prediction. With every other input zero the two butterfly passes collapse: each output row is a row constant
    /// (from positions 0 and 4) plus a fixed four-lane pattern (from position 1), so a few scalar multiplies and four vector rows replace
    /// two transposes and eight vector multiplies. The arithmetic is the full transform's, in the same integer widths, so the result is
    /// identical to <see cref="TransformFullAndAdd(ref short, ref byte, int)"/> for such a block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSparseAc(ref short coefficients, ref byte dst, int stride)
    {
        int in1 = Unsafe.Add(ref coefficients, 1);
        int in4 = Unsafe.Add(ref coefficients, 4);
        int a = coefficients + 4;

        int c4 = (in4 * Vp8InverseTransformConstants.C2) >> 16;
        int d4 = in4 + ((in4 * Vp8InverseTransformConstants.C1) >> 16);
        int c1 = (in1 * Vp8InverseTransformConstants.C2) >> 16;
        int d1 = in1 + ((in1 * Vp8InverseTransformConstants.C1) >> 16);

        var pattern = Vector128.Create(d1, c1, -c1, -d1);
        AddClipStoreRow(ref dst, Vector128.Create(a + d4) + pattern);
        AddClipStoreRow(ref Unsafe.Add(ref dst, stride), Vector128.Create(a + c4) + pattern);
        AddClipStoreRow(ref Unsafe.Add(ref dst, 2 * stride), Vector128.Create(a - c4) + pattern);
        AddClipStoreRow(ref Unsafe.Add(ref dst, 3 * stride), Vector128.Create(a - d4) + pattern);
    }

    /// <summary>The general inverse transform of a block known to carry AC, over raw references; see the span overload.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformFullAndAdd(ref short coefficients, ref byte dst, int stride)
    {
        if (Sse2.IsSupported && FitsSixteenBitMath(ref coefficients))
        {
            TransformFullAndAdd16(ref coefficients, ref dst, stride);
            return;
        }

        // Lane j of each vector = original column j's coefficient at that row position.
        var in0 = LoadRowOfColumns(ref coefficients, 0);
        var in4 = LoadRowOfColumns(ref coefficients, 4);
        var in8 = LoadRowOfColumns(ref coefficients, 8);
        var in12 = LoadRowOfColumns(ref coefficients, 12);

        // Column pass: 4 independent butterflies, one per lane (= per original column). Lane-parallel, no
        // transpose needed yet -- this mirrors the scalar loop's `for (int i = 0; i < 4; i++)` exactly, just
        // computing all 4 iterations of it at once instead of one at a time.
        var a = in0 + in8;
        var b = in0 - in8;
        var c = Mul2(in4) - Mul1(in12);
        var d = Mul1(in4) + Mul2(in12);

        // slotN[j] = column j's N-th butterfly output. This is a genuinely different axis from the columns
        // pass 1 was lane-parallel over: slot0..slot3 need to become pass 2's *inputs*, one full vector per
        // column rather than one lane per column, which is exactly what a transpose does.
        var slot0 = a + d;
        var slot1 = b + c;
        var slot2 = b - c;
        var slot3 = a - d;

        Transpose4x4(slot0, slot1, slot2, slot3, out var col0, out var col1, out var col2, out var col3);

        // col0..col3 are now column 0..3's own 4 butterfly outputs, one full vector per column. That is
        // exactly (t0, t4, t8, t12) for a row pass made lane-parallel by output row instead of by column:
        // col0's lane r is output row r's t0, col1's lane r is row r's t4, and so on.
        var dc = col0 + Four;
        var a2 = dc + col2;
        var b2 = dc - col2;
        var c2 = Mul2(col1) - Mul1(col3);
        var d2 = Mul1(col1) + Mul2(col3);

        // outN[r] = output row r's N-th pixel delta (before the final >>3). Transposing once more turns this
        // back into one vector per row, each holding that row's 4 contiguous pixel deltas -- ready to shift,
        // add to the existing prediction, clamp, and store.
        var out0 = a2 + d2;
        var out1 = b2 + c2;
        var out2 = b2 - c2;
        var out3 = a2 - d2;

        Transpose4x4(out0, out1, out2, out3, out var row0, out var row1, out var row2, out var row3);

        AddClipStoreRow(ref dst, row0);
        AddClipStoreRow(ref Unsafe.Add(ref dst, stride), row1);
        AddClipStoreRow(ref Unsafe.Add(ref dst, 2 * stride), row2);
        AddClipStoreRow(ref Unsafe.Add(ref dst, 3 * stride), row3);
    }

    /// <summary>
    /// The largest coefficient magnitude for which every intermediate of the transform fits in a signed 16-bit lane: the first pass
    /// grows values by at most 3.85x, the second by at most 3.85x again plus the rounding bias, so 2047 * 14.8 + 4 stays below 32767.
    /// Dequantized coefficients of real images are far inside it; anything outside takes the 32-bit kernel, which is exact for every input.
    /// </summary>
    private const int SixteenBitLimit = 2047;

    /// <summary>Whether all sixteen coefficients are within +-<see cref="SixteenBitLimit"/>, which makes <see cref="TransformFullAndAdd16"/> exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FitsSixteenBitMath(ref short coefficients)
    {
        // (v + limit) as unsigned is within [0, 2 * limit] exactly when v is within +-limit (values below -limit wrap high).
        var offset = Vector128.Create((short)SixteenBitLimit);
        var low = (Vector128.LoadUnsafe(ref coefficients) + offset).AsUInt16();
        var high = (Vector128.LoadUnsafe(ref coefficients, 8) + offset).AsUInt16();
        var bound = Vector128.Create((ushort)(2 * SixteenBitLimit));
        return !Vector128.GreaterThanAny(low, bound) && !Vector128.GreaterThanAny(high, bound);
    }

    /// <summary>
    /// <see cref="TransformFullAndAdd(ref short, ref byte, int)"/> in 16-bit lanes, for blocks within <see cref="SixteenBitLimit"/>. A
    /// multiply by 20091 or 35468 shifted right by 16 is the high half of a signed 16-bit multiply (35468 as -30068 plus the value
    /// itself), so the four multiplies of a butterfly are cheap high multiplies instead of 32-bit low multiplies, and with every
    /// intermediate inside 16 bits the results are the same as the 32-bit kernel's. The structure is the same: butterflies down the
    /// columns, a transpose, butterflies along the rows, a transpose, then descale, add, clamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformFullAndAdd16(ref short coefficients, ref byte dst, int stride)
    {
        var in0 = LoadRow16(ref coefficients, 0);
        var in4 = LoadRow16(ref coefficients, 4);
        var in8 = LoadRow16(ref coefficients, 8);
        var in12 = LoadRow16(ref coefficients, 12);

        var a = in0 + in8;
        var b = in0 - in8;
        var c = Mul2(in4) - Mul1(in12);
        var d = Mul1(in4) + Mul2(in12);

        // Transpose the four (4-lane) vectors a+d, b+c, b-c, a-d: afterwards `columns01` holds column 0 then column 1 of the
        // result, `columns23` columns 2 and 3.
        var interleave01 = Sse2.UnpackLow(a + d, b + c);
        var interleave23 = Sse2.UnpackLow(b - c, a - d);
        var columns01 = Sse2.UnpackLow(interleave01.AsInt32(), interleave23.AsInt32()).AsInt16();
        var columns23 = Sse2.UnpackHigh(interleave01.AsInt32(), interleave23.AsInt32()).AsInt16();

        var col0 = columns01;
        var col1 = Sse2.ShiftRightLogical128BitLane(columns01, 8);
        var col2 = columns23;
        var col3 = Sse2.ShiftRightLogical128BitLane(columns23, 8);

        var dc = col0 + Vector128.Create((short)4);
        var a2 = dc + col2;
        var b2 = dc - col2;
        var c2 = Mul2(col1) - Mul1(col3);
        var d2 = Mul1(col1) + Mul2(col3);

        var rowInterleave01 = Sse2.UnpackLow(a2 + d2, b2 + c2);
        var rowInterleave23 = Sse2.UnpackLow(b2 - c2, a2 - d2);
        var rows01 = Sse2.ShiftRightArithmetic(Sse2.UnpackLow(rowInterleave01.AsInt32(), rowInterleave23.AsInt32()).AsInt16(), 3);
        var rows23 = Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(rowInterleave01.AsInt32(), rowInterleave23.AsInt32()).AsInt16(), 3);

        ref byte row1 = ref Unsafe.Add(ref dst, stride);
        ref byte row2 = ref Unsafe.Add(ref dst, 2 * stride);
        ref byte row3 = ref Unsafe.Add(ref dst, 3 * stride);

        var zero = Vector128<byte>.Zero;
        ulong pixels01 = Unsafe.ReadUnaligned<uint>(ref dst) | ((ulong)Unsafe.ReadUnaligned<uint>(ref row1) << 32);
        ulong pixels23 = Unsafe.ReadUnaligned<uint>(ref row2) | ((ulong)Unsafe.ReadUnaligned<uint>(ref row3) << 32);
        var sum01 = rows01 + Sse2.UnpackLow(Vector128.CreateScalarUnsafe(pixels01).AsByte(), zero).AsInt16();
        var sum23 = rows23 + Sse2.UnpackLow(Vector128.CreateScalarUnsafe(pixels23).AsByte(), zero).AsInt16();

        // Saturating pack clamps to [0,255]: negative sums go to 0 and anything above 255 to 255.
        var packed = Sse2.PackUnsignedSaturate(sum01, sum23).AsUInt32();
        Unsafe.WriteUnaligned(ref dst, packed.GetElement(0));
        Unsafe.WriteUnaligned(ref row1, packed.GetElement(1));
        Unsafe.WriteUnaligned(ref row2, packed.GetElement(2));
        Unsafe.WriteUnaligned(ref row3, packed.GetElement(3));
    }

    /// <summary>Four consecutive coefficients in the low four 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRow16(ref short coefficients, int baseIndex) =>
        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref coefficients, baseIndex)))).AsInt16();

    /// <summary><c>v + ((v * 20091) &gt;&gt; 16)</c> on 16-bit lanes: the high half of the product is exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Mul1(Vector128<short> v) => v + Sse2.MultiplyHigh(v, Vector128.Create((short)Vp8InverseTransformConstants.C1));

    /// <summary><c>(v * 35468) &gt;&gt; 16</c> on 16-bit lanes, as <c>v + ((v * -30068) &gt;&gt; 16)</c> because 35468 does not fit a signed lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Mul2(Vector128<short> v) => v + Sse2.MultiplyHigh(v, Vector128.Create((short)(Vp8InverseTransformConstants.C2 - 65536)));

    /// <summary>Four consecutive coefficients, sign-extended to 32-bit lanes (one 8-byte load plus a widen, not four scalar loads).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadRowOfColumns(ref short coefficients, int baseIndex) =>
        Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref coefficients, baseIndex)))).AsInt16());

    /// <summary>The scalar kernel's <c>Mul1(v) = v + ((v*C1) &gt;&gt; 16)</c>, lane-wise. <c>Vector128.Multiply</c> on <see cref="int"/> truncates to the low 32 bits exactly as C#'s <c>int * int</c> does, so this matches the scalar form bit for bit, wraparound included.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> v) => v + Vector128.ShiftRightArithmetic(v * C1, 16);

    /// <summary>The scalar kernel's <c>Mul2(v) = (v*C2) &gt;&gt; 16</c>, lane-wise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> v) => Vector128.ShiftRightArithmetic(v * C2, 16);

    /// <summary>
    /// A true 4x4 transpose: <c>colK[i] == rowI[k]</c> for every <c>i, k</c>. The standard two-round SSE2
    /// shape — interleave adjacent rows as 32-bit lanes, then combine those as 64-bit lanes — needs no
    /// portable equivalent search, since .NET's cross-platform <c>Vector128</c> API has no two-vector
    /// interleave at all (only single-vector <c>Shuffle</c>); this is the same gap
    /// <c>Vp8VectorLoopFilter</c>'s 16x8 transpose already documents and works around the same way.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose4x4(
        Vector128<int> row0, Vector128<int> row1, Vector128<int> row2, Vector128<int> row3,
        out Vector128<int> col0, out Vector128<int> col1, out Vector128<int> col2, out Vector128<int> col3)
    {
        var lo01 = Vp8Interleave.UnpackLow(row0, row1);
        var hi01 = Vp8Interleave.UnpackHigh(row0, row1);
        var lo23 = Vp8Interleave.UnpackLow(row2, row3);
        var hi23 = Vp8Interleave.UnpackHigh(row2, row3);

        col0 = Vp8Interleave.UnpackLow(lo01.AsInt64(), lo23.AsInt64()).AsInt32();
        col1 = Vp8Interleave.UnpackHigh(lo01.AsInt64(), lo23.AsInt64()).AsInt32();
        col2 = Vp8Interleave.UnpackLow(hi01.AsInt64(), hi23.AsInt64()).AsInt32();
        col3 = Vp8Interleave.UnpackHigh(hi01.AsInt64(), hi23.AsInt64()).AsInt32();
    }

    /// <summary>Shifts a row's 4 pixel deltas by the final descale, adds them to the existing prediction, clamps to [0,255], and stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddClipStoreRow(ref byte row, Vector128<int> deltas)
    {
        var shifted = Vector128.ShiftRightArithmetic(deltas, 3);
        var basePixels = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref row)).AsByte())).AsInt32();
        var clamped = Vector128.Min(Vector128.Max(basePixels + shifted, Zero), TwoFiftyFive).AsUInt32();

        // Every lane is in [0,255], so the truncating narrows keep the values and put the four bytes in lane 0.
        var bytes = Vector128.Narrow(Vector128.Narrow(clamped, clamped), Vector128.Narrow(clamped, clamped));
        Unsafe.WriteUnaligned(ref row, bytes.AsUInt32().ToScalar());
    }
}
