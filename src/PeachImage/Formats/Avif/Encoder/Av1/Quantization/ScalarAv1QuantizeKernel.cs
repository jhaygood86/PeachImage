namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// Reference (always-correct, not performance-optimized) quantize kernel: one reciprocal-multiply and one
/// <see cref="Math.Round(double, MidpointRounding)"/> per coefficient.
/// </summary>
internal sealed class ScalarAv1QuantizeKernel : IAv1QuantizeKernel
{
    public void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, double dcReciprocal, double acReciprocal)
    {
        int total = size * size;
        levelsOut[0] = RoundAwayFromZero(coeff[0] * dcReciprocal);
        for (int i = 1; i < total; i++)
        {
            levelsOut[i] = RoundAwayFromZero(coeff[i] * acReciprocal);
        }
    }

    /// <summary>Shared by the SIMD kernels' scalar remainder tails, so every tier rounds identically.</summary>
    internal static int RoundAwayFromZero(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
