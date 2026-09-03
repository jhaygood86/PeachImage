using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// Selects the fastest available <see cref="IAv1QuantizeKernel"/> for the current hardware at startup, using
/// the same Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as
/// <see cref="ColorConversion.Av1RgbToYuvKernelSelector"/> and <see cref="Transform.Av1MatrixVectorKernelSelector"/>.
/// </summary>
internal static class Av1QuantizeKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1QuantizeKernel Instance { get; } = Select();

    private static IAv1QuantizeKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Av1QuantizeKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1QuantizeKernel();
        }

        return new ScalarAv1QuantizeKernel();
    }
}
