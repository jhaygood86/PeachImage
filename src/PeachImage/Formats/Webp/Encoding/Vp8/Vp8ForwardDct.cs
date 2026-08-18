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
