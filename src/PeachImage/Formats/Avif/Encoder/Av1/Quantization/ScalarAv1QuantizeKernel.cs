namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// Reference (always-correct) quantize kernel -- a direct, per-coefficient port of libaom's own real
/// <c>av1_quantize_fp_no_qmatrix</c> (<c>av1/encoder/av1_quantize.c</c>). Also the scalar tail every SIMD
/// tier falls back to, via <see cref="QuantizeOne"/>.
/// </summary>
internal sealed class ScalarAv1QuantizeKernel : IAv1QuantizeKernel
{
    public void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, int quantFpDc, int quantFpAc, int roundFpDc, int roundFpAc, int dequantDc, int dequantAc, int logScale)
    {
        int total = size * size;
        levelsOut[0] = QuantizeOne(coeff[0], quantFpDc, roundFpDc, dequantDc, logScale);
        for (int i = 1; i < total; i++)
        {
            levelsOut[i] = QuantizeOne(coeff[i], quantFpAc, roundFpAc, dequantAc, logScale);
        }
    }

    /// <summary>
    /// One coefficient of libaom's own real <c>av1_quantize_fp_no_qmatrix</c> loop body
    /// (<c>av1/encoder/av1_quantize.c</c>): a hard zero-bin threshold check
    /// (<c>abs_coeff &lt;&lt; (1 + log_scale) &gt;= thresh</c>, where <paramref name="dequant"/> is the
    /// threshold) followed by <c>round, multiply by the fixed-point reciprocal, shift</c> -- not a plain
    /// division/round, so a coefficient just below the deadzone is dropped to exactly 0 rather than
    /// rounding to a nonzero level the way naive rounding could for some inputs. <see langword="long"/>
    /// intermediates throughout, matching the real code's own <c>int64_t abs_coeff</c> (needed so
    /// <c>abs_coeff &lt;&lt; (1 + log_scale)</c> and <c>abs_coeff * quant_fp</c> can't overflow 32 bits for
    /// large coefficients, even though every value this project's own lossless path actually reaches stays
    /// far smaller).
    /// </summary>
    internal static int QuantizeOne(int coeffValue, int quantFp, int roundFp, int dequant, int logScale)
    {
        long absCoeff = coeffValue < 0 ? -(long)coeffValue : coeffValue;
        long thresh = dequant;

        if ((absCoeff << (1 + logScale)) < thresh)
        {
            return 0;
        }

        int rounding = logScale == 0 ? roundFp : (roundFp + 1) >> 1; // ROUND_POWER_OF_TWO(round_fp, log_scale).
        absCoeff = Math.Clamp(absCoeff + rounding, short.MinValue, short.MaxValue);
        int tmp32 = (int)((absCoeff * quantFp) >> (16 - logScale));

        return coeffValue < 0 ? -tmp32 : tmp32;
    }
}
