using System.Runtime.CompilerServices;
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
    /// <summary>Whether <see cref="TransformFullAndAdd"/> is usable — the 4x4 transpose is built from <see cref="Sse2.UnpackLow(Vector128{int}, Vector128{int})"/>/<see cref="Sse2.UnpackHigh(Vector128{int}, Vector128{int})"/>, which have no portable equivalent (see the type's remarks).</summary>
    public static bool CanTransform => Sse2.IsSupported;

    private static readonly Vector128<int> C1 = Vector128.Create(Vp8InverseTransformConstants.C1);
    private static readonly Vector128<int> C2 = Vector128.Create(Vp8InverseTransformConstants.C2);
    private static readonly Vector128<int> Four = Vector128.Create(4);
    private static readonly Vector128<int> Zero = Vector128<int>.Zero;
    private static readonly Vector128<int> TwoFiftyFive = Vector128.Create(255);

    /// <inheritdoc cref="Vp8ScalarInverseDct.TransformFullAndAdd"/>
    public static void TransformFullAndAdd(ReadOnlySpan<short> coefficients, Span<byte> dst, int offset, int stride)
    {
        // Lane j of each vector = original column j's coefficient at that row position.
        var in0 = LoadRowOfColumns(coefficients, 0);
        var in4 = LoadRowOfColumns(coefficients, 4);
        var in8 = LoadRowOfColumns(coefficients, 8);
        var in12 = LoadRowOfColumns(coefficients, 12);

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

        AddClipStoreRow(dst, offset, row0);
        AddClipStoreRow(dst, offset + stride, row1);
        AddClipStoreRow(dst, offset + (2 * stride), row2);
        AddClipStoreRow(dst, offset + (3 * stride), row3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadRowOfColumns(ReadOnlySpan<short> coefficients, int baseIndex) =>
        Vector128.Create((int)coefficients[baseIndex], coefficients[baseIndex + 1], coefficients[baseIndex + 2], coefficients[baseIndex + 3]);

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
    private static void Transpose4x4(
        Vector128<int> row0, Vector128<int> row1, Vector128<int> row2, Vector128<int> row3,
        out Vector128<int> col0, out Vector128<int> col1, out Vector128<int> col2, out Vector128<int> col3)
    {
        var lo01 = Sse2.UnpackLow(row0, row1);
        var hi01 = Sse2.UnpackHigh(row0, row1);
        var lo23 = Sse2.UnpackLow(row2, row3);
        var hi23 = Sse2.UnpackHigh(row2, row3);

        col0 = Sse2.UnpackLow(lo01.AsInt64(), lo23.AsInt64()).AsInt32();
        col1 = Sse2.UnpackHigh(lo01.AsInt64(), lo23.AsInt64()).AsInt32();
        col2 = Sse2.UnpackLow(hi01.AsInt64(), hi23.AsInt64()).AsInt32();
        col3 = Sse2.UnpackHigh(hi01.AsInt64(), hi23.AsInt64()).AsInt32();
    }

    /// <summary>Shifts a row's 4 pixel deltas by the final descale, adds them to the existing prediction, clamps to [0,255], and stores.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddClipStoreRow(Span<byte> dst, int rowOffset, Vector128<int> deltas)
    {
        var shifted = Vector128.ShiftRightArithmetic(deltas, 3);
        var basePixels = Vector128.Create((int)dst[rowOffset], dst[rowOffset + 1], dst[rowOffset + 2], dst[rowOffset + 3]);
        var clamped = Vector128.Min(Vector128.Max(basePixels + shifted, Zero), TwoFiftyFive);

        dst[rowOffset + 0] = (byte)clamped.GetElement(0);
        dst[rowOffset + 1] = (byte)clamped.GetElement(1);
        dst[rowOffset + 2] = (byte)clamped.GetElement(2);
        dst[rowOffset + 3] = (byte)clamped.GetElement(3);
    }
}
