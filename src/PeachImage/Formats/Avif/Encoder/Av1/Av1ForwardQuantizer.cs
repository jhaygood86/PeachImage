using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Forward-quantizes AV1 transform coefficients, and maps a 0-100 <c>AvifEncoderOptions.Quality</c> to a
/// <c>base_q_idx</c> -- the write-side mirror of <see cref="Av1Dequantizer"/>, reusing its exact
/// <see cref="Av1QuantLookup"/> tables so a given <c>base_q_idx</c> means the same quantizer step on both
/// sides. Restricted to the non-quantizer-matrix path and the four square DCT_DCT sizes this v1 encoder
/// uses (see <see cref="Av1ForwardTransform"/>), so <c>dqDenom</c> is only ever 1 (4x4/8x8/16x16) or 2
/// (32x32) -- the two <c>Av1Dequantizer.Dequantize</c> handles for non-64-sized transforms.
/// </summary>
internal static class Av1ForwardQuantizer
{
    /// <summary>
    /// Quantizes <paramref name="coeff"/> (a flat <paramref name="size"/> x <paramref name="size"/>
    /// row-major buffer, the output of <see cref="Av1ForwardTransform.Forward2D"/>) into
    /// <paramref name="levelsOut"/> (same shape), the integer levels that get entropy-coded.
    /// </summary>
    public static void Quantize(int[] coeff, int[] levelsOut, int size, int baseQIdx)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(size);
        int dcQ = Av1Dequantizer.DcQ(baseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(baseQIdx, 8);
        int dqDenom = txSz == Av1TxSize.Tx32x32 ? 2 : 1;

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                int q = i == 0 && j == 0 ? dcQ : acQ;
                int idx = (i * size) + j;
                double level = coeff[idx] * dqDenom / (double)q;
                levelsOut[idx] = (int)Math.Round(level, MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <summary>
    /// Maps a 0-100 <c>AvifEncoderOptions.Quality</c> value to a 1-255 <c>base_q_idx</c> (never 0,
    /// which would trigger AV1's coded-lossless path -- see <see cref="Av1FrameHeaderWriter.Write"/>). A
    /// simple monotonic linear mapping; matching any particular encoder's quality-to-size curve exactly is
    /// a tuning refinement, not a v1 correctness requirement.
    /// </summary>
    public static int QualityToBaseQIdx(int quality)
    {
        int clampedQuality = Math.Clamp(quality, 0, 100);
        double t = (100 - clampedQuality) / 100.0;
        int baseQIdx = (int)Math.Round(1 + (t * 254));
        return Math.Clamp(baseQIdx, 1, 255);
    }
}
