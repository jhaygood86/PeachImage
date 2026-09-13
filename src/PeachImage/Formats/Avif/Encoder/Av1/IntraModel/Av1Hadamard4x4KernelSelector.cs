using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.IntraModel;

/// <summary>
/// Selects the fastest available <see cref="IAv1Hadamard4x4Kernel"/> for the current hardware at startup.
/// Vector128 (SSE2/AdvSimd) &gt; scalar only -- see <see cref="Vector128Av1Hadamard4x4Kernel"/>'s remarks for
/// why there is no Vector256 tier here, unlike <see cref="Quantization.Av1QuantizeKernelSelector"/> and
/// <see cref="Transform.Av1MatrixVectorKernelSelector"/>.
/// </summary>
internal static class Av1Hadamard4x4KernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1Hadamard4x4Kernel Instance { get; } = Select();

    private static IAv1Hadamard4x4Kernel Select()
    {
        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1Hadamard4x4Kernel();
        }

        return new ScalarAv1Hadamard4x4Kernel();
    }
}
