using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Selects the fastest available resampling convolver tier for the current hardware at startup, the same
/// Vector256 (AVX/AVX2) &gt; Vector128 (SSE2/AdvSimd) dispatch pattern as
/// <see cref="Formats.Jpeg.Decoding.Upsampling.ChromaUpsamplerSelector"/>. <see cref="Vector128ResamplingConvolver"/>'s
/// portable API works correctly (via .NET's software fallback) on any platform, so it doubles as the
/// default/fallback tier without a separate scalar-only implementation.
/// </summary>
internal static class ResamplingConvolverSelector
{
    /// <summary>The resampling convolver to use for this process.</summary>
    public static IResamplingConvolver Instance { get; } = Select();

    private static IResamplingConvolver Select() =>
        Vector256.IsHardwareAccelerated ? new Vector256ResamplingConvolver() : new Vector128ResamplingConvolver();
}
