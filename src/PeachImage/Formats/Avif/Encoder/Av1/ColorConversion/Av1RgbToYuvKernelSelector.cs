using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// Selects the fastest available <see cref="IAv1RgbToYuvKernel"/> for the current hardware at startup, using
/// the same Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as
/// <see cref="Jpeg.ColorConversion.ColorConverterSelector"/>.
/// </summary>
internal static class Av1RgbToYuvKernelSelector
{
    /// <summary>The kernel to use for this process.</summary>
    public static IAv1RgbToYuvKernel Instance { get; } = Select();

    private static IAv1RgbToYuvKernel Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256Av1RgbToYuvKernel();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128Av1RgbToYuvKernel();
        }

        return new ScalarAv1RgbToYuvKernel();
    }
}
