namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// <see cref="Av1QuantizeKernelSelector"/>'s SSE2/AdvSimd-capable tier. Currently a thin, per-element proxy
/// to <see cref="ScalarAv1QuantizeKernel.QuantizeOne"/> rather than genuine data-parallel SIMD: the real
/// fixed-point algorithm (<c>long</c>-intermediate multiply-shift with a data-dependent hard zero-bin
/// branch, see <see cref="ScalarAv1QuantizeKernel"/>'s own remarks) is materially more involved to vectorize
/// correctly than the old reciprocal-multiply-and-round approach this class used to implement -- a
/// hand-rolled cross-platform 64-bit multiply/shift/branch chain is real, nontrivial surface for a new
/// correctness bug, so this class keeps the kernel-selector architecture's own three-tier shape (for a
/// future genuine vectorization pass to fill back in) while guaranteeing byte-identical output to
/// <see cref="ScalarAv1QuantizeKernel"/> today.
/// </summary>
internal sealed class Vector128Av1QuantizeKernel : IAv1QuantizeKernel
{
    public void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, int quantFpDc, int quantFpAc, int roundFpDc, int roundFpAc, int dequantDc, int dequantAc, int logScale)
    {
        int total = size * size;
        levelsOut[0] = ScalarAv1QuantizeKernel.QuantizeOne(coeff[0], quantFpDc, roundFpDc, dequantDc, logScale);
        for (int i = 1; i < total; i++)
        {
            levelsOut[i] = ScalarAv1QuantizeKernel.QuantizeOne(coeff[i], quantFpAc, roundFpAc, dequantAc, logScale);
        }
    }
}
