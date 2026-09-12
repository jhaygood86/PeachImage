namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// Real fixed-point core of <see cref="Av1ForwardQuantizer.Quantize"/>, matching libaom's own
/// <c>av1_quantize_fp_no_qmatrix</c> (<c>av1/encoder/av1_quantize.c</c>) per-coefficient formula exactly --
/// integer multiply-by-reciprocal-then-shift, not a floating-point division/round. See
/// <see cref="ScalarAv1QuantizeKernel.QuantizeOne"/> for the shared reference implementation every tier
/// calls.
/// </summary>
internal interface IAv1QuantizeKernel
{
    /// <summary>
    /// Quantizes <paramref name="coeff"/> (<paramref name="size"/> x <paramref name="size"/>, row-major,
    /// flat index 0 is the DC coefficient) into <paramref name="levelsOut"/>: index 0 uses
    /// <paramref name="quantFpDc"/>/<paramref name="roundFpDc"/>/<paramref name="dequantDc"/>, every other
    /// index uses the AC variants -- all already-precomputed per libaom's own real
    /// <c>av1_build_quantizer</c> formulas (<c>quant_fp = (1&lt;&lt;16)/dequant</c>,
    /// <c>round_fp = (64*dequant)&gt;&gt;7</c>). <paramref name="logScale"/> is 1 for 32x32 transforms
    /// (matching libaom's own real <c>log_scale</c>), 0 otherwise.
    /// </summary>
    void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, int quantFpDc, int quantFpAc, int roundFpDc, int roundFpAc, int dequantDc, int dequantAc, int logScale);
}
