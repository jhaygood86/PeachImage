using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// Selects the best available <see cref="IVp8ColorConverter"/> for the current hardware once at startup,
/// mirroring the tier-selector pattern used elsewhere in this codebase (Jpeg's <c>ColorConverterSelector</c>,
/// <c>Vp8LTransformKernelSelector</c>).
/// </summary>
/// <remarks>
/// On x86 with SSSE3 this is <see cref="Sse2Vp8ColorConverter"/> (16 pixels per iteration, 16-bit lanes);
/// elsewhere <see cref="Vector128Vp8ColorConverter"/> (4 pixels per iteration in 32-bit lanes, portable), then
/// the scalar tier. All three produce identical output.
/// </remarks>
internal static class Vp8ColorConverterSelector
{
    /// <summary>The converter to use for this process.</summary>
    [SuppressMessage(
        "Performance",
        "CA1859",
        Justification = "Returns IVp8ColorConverter by design: the concrete tier is chosen at runtime from hardware support, so the property cannot be typed to one implementation.")]
    public static IVp8ColorConverter Instance { get; } = Select();

    private static IVp8ColorConverter Select()
    {
        if (Sse2Vp8ColorConverter.IsSupported)
        {
            return new Sse2Vp8ColorConverter();
        }

        return Vector128.IsHardwareAccelerated ? new Vector128Vp8ColorConverter() : new Vp8ScalarColorConverter();
    }
}
