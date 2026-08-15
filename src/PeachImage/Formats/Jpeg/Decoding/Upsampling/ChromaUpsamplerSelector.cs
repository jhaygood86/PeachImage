using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>
/// Selects the fastest available "fancy" chroma upsampler for the current hardware at startup, the same
/// Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) dispatch pattern as <see cref="ColorConversion.ColorConverterSelector"/>.
/// Only two tiers: <see cref="TriangleFilterUpsampler"/>'s existing <see cref="Vector128{T}"/> path has no
/// hardware-acceleration guard today and already works correctly (via .NET's portable-API software fallback)
/// on any platform, so it doubles as the default/fallback tier without a separate scalar-only branch.
/// </summary>
internal static class ChromaUpsamplerSelector
{
    /// <summary>The chroma upsampler to use for this process.</summary>
    public static IChromaUpsampler Instance { get; } = Select();

    private static IChromaUpsampler Select() =>
        Vector256.IsHardwareAccelerated ? new Vector256TriangleFilterUpsampler() : new TriangleFilterUpsampler();
}
