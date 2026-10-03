using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// VP8's forward 4x4 DCT, fused with computing the residual (source minus prediction) in one pass — the
/// encode-side counterpart of <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseDct"/>, though not its exact algebraic
/// inverse (see <see cref="Vp8ForwardTransformConstants"/>'s remarks). Transcribed verbatim from libwebp's
/// <c>src/dsp/enc.c</c> <c>FTransform_C</c>, cross-checked against the downloaded upstream source.
/// </summary>
internal static class Vp8ForwardDct
{
    /// <summary>
    /// Computes the 4x4 residual between <paramref name="source"/> and <paramref name="prediction"/> (both
    /// already-reconstructed/predicted pixel planes, read at their own origin/stride) and forward-DCTs it into
    /// <paramref name="output"/> (16 entries, natural raster order, not zigzag).
    /// </summary>
    public static void Transform(
        ReadOnlySpan<byte> source, int sourceOffset, int sourceStride,
        ReadOnlySpan<byte> prediction, int predictionOffset, int predictionStride,
        Span<short> output)
    {
        if (Vp8Interleave.IsSupported)
        {
            TransformVector(source, sourceOffset, sourceStride, prediction, predictionOffset, predictionStride, output);
            return;
        }

        TransformScalar(source, sourceOffset, sourceStride, prediction, predictionOffset, predictionStride, output);
    }

    /// <summary>
    /// The same transform on <see cref="Vector128{T}"/>, bit-identical to <see cref="TransformScalar"/>. The scalar
    /// form's first pass runs once per <em>row</em> (butterflies across the four columns) and its second once per
    /// <em>column</em> (across the four rows). With one row per vector that needs a lane-wise butterfly across
    /// vectors, so: load the residual rows, transpose so each vector holds one column (lane = row), run pass 1
    /// lane-parallel, transpose its four outputs so each vector is again one row of the intermediate (lane =
    /// coefficient column), and run pass 2 lane-parallel — the output rows then fall out in natural order.
    /// </summary>
    internal static void TransformVector(
        ReadOnlySpan<byte> source, int sourceOffset, int sourceStride,
        ReadOnlySpan<byte> prediction, int predictionOffset, int predictionStride,
        Span<short> output)
    {
        var row0 = ResidualRow(source, sourceOffset, prediction, predictionOffset);
        var row1 = ResidualRow(source, sourceOffset + sourceStride, prediction, predictionOffset + predictionStride);
        var row2 = ResidualRow(source, sourceOffset + (2 * sourceStride), prediction, predictionOffset + (2 * predictionStride));
        var row3 = ResidualRow(source, sourceOffset + (3 * sourceStride), prediction, predictionOffset + (3 * predictionStride));

        // d0..d3 of every row at once: vector j holds column j's residual for rows 0..3.
        Vp8VectorInverseDct.Transpose4x4(row0, row1, row2, row3, out var d0, out var d1, out var d2, out var d3);

        var sin = Vector128.Create(Vp8ForwardTransformConstants.Sin);
        var cos = Vector128.Create(Vp8ForwardTransformConstants.Cos);

        var a0 = d0 + d3;
        var a1 = d1 + d2;
        var a2 = d1 - d2;
        var a3 = d0 - d3;

        // Lane i of tK is the scalar tmp[(i * 4) + K].
        var t0 = (a0 + a1) * 8;
        var t1 = Vector128.ShiftRightArithmetic((a2 * sin) + (a3 * cos) + Vector128.Create(Vp8ForwardTransformConstants.FirstPassBiasOdd1), 9);
        var t2 = (a0 - a1) * 8;
        var t3 = Vector128.ShiftRightArithmetic((a3 * sin) - (a2 * cos) + Vector128.Create(Vp8ForwardTransformConstants.FirstPassBiasOdd3), 9);

        // Back to one vector per intermediate row: lane i of rK is tmp[(K * 4) + i].
        Vp8VectorInverseDct.Transpose4x4(t0, t1, t2, t3, out var r0, out var r1, out var r2, out var r3);

        var b0 = r0 + r3;
        var b1 = r1 + r2;
        var b2 = r1 - r2;
        var b3 = r0 - r3;

        var seven = Vector128.Create(7);
        var out0 = Vector128.ShiftRightArithmetic(b0 + b1 + seven, 4);
        var out2 = Vector128.ShiftRightArithmetic(b0 - b1 + seven, 4);
        var out3 = Vector128.ShiftRightArithmetic((b3 * sin) - (b2 * cos) + Vector128.Create(Vp8ForwardTransformConstants.SecondPassBiasOdd3), 16);

        // "+ (a3 != 0 ? 1 : 0)": the equality mask is -1 where b3 == 0, so adding (mask + 1) adds 1 exactly where b3 != 0.
        var nonZeroB3 = Vector128.Equals(b3, Vector128<int>.Zero) + Vector128.Create(1);
        var out1 = Vector128.ShiftRightArithmetic((b2 * sin) + (b3 * cos) + Vector128.Create(Vp8ForwardTransformConstants.SecondPassBiasOdd1), 16) + nonZeroB3;

        // The scalar form casts each int to short; Narrow truncates the same way.
        ref short dst = ref MemoryMarshal.GetReference(output.Slice(0, 16));
        Vector128.Narrow(out0, out1).StoreUnsafe(ref dst, 0);
        Vector128.Narrow(out2, out3).StoreUnsafe(ref dst, 8);
    }

    /// <summary>One row's four residuals (source minus prediction) as 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ResidualRow(ReadOnlySpan<byte> source, int sourceOffset, ReadOnlySpan<byte> prediction, int predictionOffset)
    {
        var s = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(MemoryMarshal.Read<uint>(source.Slice(sourceOffset, 4))).AsByte())).AsInt32();
        var p = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(MemoryMarshal.Read<uint>(prediction.Slice(predictionOffset, 4))).AsByte())).AsInt32();
        return s - p;
    }

    /// <summary>The plain scalar transform; the reference <see cref="TransformVector"/> is held to bit for bit.</summary>
    internal static void TransformScalar(
        ReadOnlySpan<byte> source, int sourceOffset, int sourceStride,
        ReadOnlySpan<byte> prediction, int predictionOffset, int predictionStride,
        Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int srcRow = sourceOffset + (i * sourceStride);
            int predRow = predictionOffset + (i * predictionStride);

            int d0 = source[srcRow + 0] - prediction[predRow + 0];
            int d1 = source[srcRow + 1] - prediction[predRow + 1];
            int d2 = source[srcRow + 2] - prediction[predRow + 2];
            int d3 = source[srcRow + 3] - prediction[predRow + 3];

            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;

            tmp[(i * 4) + 0] = (a0 + a1) * 8;
            tmp[(i * 4) + 1] = ((a2 * Vp8ForwardTransformConstants.Sin) + (a3 * Vp8ForwardTransformConstants.Cos) + Vp8ForwardTransformConstants.FirstPassBiasOdd1) >> 9;
            tmp[(i * 4) + 2] = (a0 - a1) * 8;
            tmp[(i * 4) + 3] = ((a3 * Vp8ForwardTransformConstants.Sin) - (a2 * Vp8ForwardTransformConstants.Cos) + Vp8ForwardTransformConstants.FirstPassBiasOdd3) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[0 + i] - tmp[12 + i];

            output[0 + i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)((((a2 * Vp8ForwardTransformConstants.Sin) + (a3 * Vp8ForwardTransformConstants.Cos) + Vp8ForwardTransformConstants.SecondPassBiasOdd1) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)(((a3 * Vp8ForwardTransformConstants.Sin) - (a2 * Vp8ForwardTransformConstants.Cos) + Vp8ForwardTransformConstants.SecondPassBiasOdd3) >> 16);
        }
    }
}
