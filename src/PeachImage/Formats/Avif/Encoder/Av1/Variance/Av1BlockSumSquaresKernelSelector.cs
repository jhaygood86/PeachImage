using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Variance;

/// <summary>
/// Selects the fastest available <see cref="IAv1BlockSumSquaresKernel"/> for the current hardware at
/// startup, using the same Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as
/// <see cref="Quantization.Av1QuantizeKernelSelector"/> and <see cref="Transform.Av1MatrixVectorKernelSelector"/>.
/// </summary>
internal static class Av1BlockSumSquaresKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1BlockSumSquaresKernel Instance { get; } = Select();

    private static IAv1BlockSumSquaresKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Av1BlockSumSquaresKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1BlockSumSquaresKernel();
        }

        return new ScalarAv1BlockSumSquaresKernel();
    }
}
