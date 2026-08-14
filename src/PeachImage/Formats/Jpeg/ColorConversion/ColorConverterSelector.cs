using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// Selects the fastest available color converter for the current hardware at startup, using the same
/// Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) &gt; scalar dispatch pattern as <see cref="Dct.DctKernelSelector"/>.
/// </summary>
internal static class ColorConverterSelector
{
    /// <summary>The color converter to use for this process.</summary>
    public static IColorConverter Instance { get; } = Select();

    private static IColorConverter Select()
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return new Vector256ColorConverter();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return new Vector128ColorConverter();
        }

        return new ScalarColorConverter();
    }
}
