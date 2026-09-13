using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

/// <summary>
/// Selects the fastest available <see cref="IAv1PaethKernel"/> for the current hardware at startup, using
/// the same Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as
/// <see cref="Encoder.Av1.Quantization.Av1QuantizeKernelSelector"/> and
/// <see cref="Encoder.Av1.Transform.Av1MatrixVectorKernelSelector"/>.
/// </summary>
internal static class Av1PaethKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1PaethKernel Instance { get; } = Select();

    private static IAv1PaethKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Av1PaethKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1PaethKernel();
        }

        return new ScalarAv1PaethKernel();
    }
}
