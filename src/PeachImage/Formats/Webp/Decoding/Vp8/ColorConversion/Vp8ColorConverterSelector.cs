using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// Selects the best available <see cref="IVp8ColorConverter"/> for the current hardware once at startup,
/// mirroring the tier-selector pattern used elsewhere in this codebase (Jpeg's <c>ColorConverterSelector</c>,
/// <c>Vp8LTransformKernelSelector</c>).
/// </summary>
/// <remarks>
/// There is no <see cref="Vector256{T}"/> tier. The conversion runs in 32-bit lanes (see
/// <see cref="Vector128Vp8ColorConverter"/>), so a 256-bit tier would convert 8 pixels per iteration and have
/// to assemble a 24-byte interleaved run, which the 128-bit shuffle-and-OR store does not extend to for free.
/// Worth revisiting only if a profile still shows conversion high after this change.
/// </remarks>
internal static class Vp8ColorConverterSelector
{
    /// <summary>The converter to use for this process.</summary>
    [SuppressMessage(
        "Performance",
        "CA1859",
        Justification = "Returns IVp8ColorConverter by design: the concrete tier is chosen at runtime from hardware support, so the property cannot be typed to one implementation.")]
    public static IVp8ColorConverter Instance { get; } = Select();

    private static IVp8ColorConverter Select() =>
        Vector128.IsHardwareAccelerated ? new Vector128Vp8ColorConverter() : new Vp8ScalarColorConverter();
}
