namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent transcription of libaom's own real <c>av1_quantize_fp_no_qmatrix</c> and
/// <c>av1_build_quantizer</c>'s own <c>quant_fp</c>/<c>round_fp</c> formulas (both
/// <c>av1/encoder/av1_quantize.c</c>), kept deliberately separate from
/// <see cref="PeachImage.Formats.Avif.Encoder.Av1.Quantization.ScalarAv1QuantizeKernel.QuantizeOne"/>'s own
/// implementation (written independently from the same real C source, not derived from that method) so a
/// test comparing the two is a genuine check against libaom's own real algorithm, not a self-consistency
/// check against one transcription.
/// </summary>
internal static class LibaomReferenceQuantize
{
    /// <summary><c>av1_build_quantizer</c>'s own real formulas at <c>sharpness == 0</c> (this project's own only supported case -- no sharpness setting is exposed).</summary>
    public static (int QuantFp, int RoundFp) BuildQuantizer(int dequant)
    {
        int quantFp = (1 << 16) / dequant;
        int roundFp = (64 * dequant) >> 7;
        return (quantFp, roundFp);
    }

    /// <summary>
    /// <c>av1_quantize_fp_no_qmatrix</c>'s own real per-coefficient body, for a single coefficient at
    /// either the DC (<paramref name="isDc"/> <see langword="true"/>) or AC position.
    /// </summary>
    public static int QuantizeOneCoefficient(int coeffValue, int quantFp, int roundFp, int dequant, int logScale)
    {
        long absCoeff = coeffValue < 0 ? -(long)coeffValue : coeffValue;

        if ((absCoeff << (1 + logScale)) < dequant)
        {
            return 0;
        }

        long rounding = logScale == 0 ? roundFp : (roundFp + 1) >> 1;
        long clamped = absCoeff + rounding;
        clamped = clamped < short.MinValue ? short.MinValue : clamped > short.MaxValue ? short.MaxValue : clamped;
        long tmp = (clamped * quantFp) >> (16 - logScale);

        return coeffValue < 0 ? -(int)tmp : (int)tmp;
    }

    /// <summary>Quantizes a full <paramref name="size"/> x <paramref name="size"/> row-major block (index 0 is DC).</summary>
    public static void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, int dcQ, int acQ, int logScale)
    {
        (int quantFpDc, int roundFpDc) = BuildQuantizer(dcQ);
        (int quantFpAc, int roundFpAc) = BuildQuantizer(acQ);

        int total = size * size;
        levelsOut[0] = QuantizeOneCoefficient(coeff[0], quantFpDc, roundFpDc, dcQ, logScale);
        for (int i = 1; i < total; i++)
        {
            levelsOut[i] = QuantizeOneCoefficient(coeff[i], quantFpAc, roundFpAc, acQ, logScale);
        }
    }
}
