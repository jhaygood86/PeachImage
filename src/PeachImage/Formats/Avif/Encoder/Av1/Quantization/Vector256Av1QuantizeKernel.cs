namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// <see cref="Av1QuantizeKernelSelector"/>'s AVX/AVX2-capable tier. See
/// <see cref="Vector128Av1QuantizeKernel"/>'s own remarks -- same thin per-element proxy to
/// <see cref="ScalarAv1QuantizeKernel.QuantizeOne"/>, same reasoning for not yet hand-vectorizing the real
/// fixed-point algorithm.
/// </summary>
internal sealed class Vector256Av1QuantizeKernel : IAv1QuantizeKernel
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
