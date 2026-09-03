using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// Selects the fastest available <see cref="IAv1MatrixVectorKernel"/> for the current hardware at startup,
/// using the same Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as
/// <see cref="ColorConversion.Av1RgbToYuvKernelSelector"/>.
/// </summary>
internal static class Av1MatrixVectorKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1MatrixVectorKernel Instance { get; } = Select();

    private static IAv1MatrixVectorKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Av1MatrixVectorKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1MatrixVectorKernel();
        }

        return new ScalarAv1MatrixVectorKernel();
    }
}
