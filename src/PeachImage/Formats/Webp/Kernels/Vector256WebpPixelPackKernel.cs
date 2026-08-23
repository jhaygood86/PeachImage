using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>
/// SIMD tier using <see cref="Vector256{T}"/>'s cross-platform generic static API (JITs to AVX2 on x86) --
/// 8 pixels/32 bytes at a time for <see cref="GatherRgba32"/>. <see cref="ExtractRgb"/> has no matching
/// 256-bit tier: AVX2's <c>Shuffle</c> only permutes bytes within each 128-bit half independently, so
/// widening to 8 pixels would need to assemble a 24-byte-per-half interleaved run the shuffle-and-store
/// trick doesn't extend to for free (the same constraint <c>Vp8ColorConverterSelector</c>'s remarks describe
/// for its own missing 256-bit tier) -- it delegates to <see cref="Vector128WebpPixelPackKernel"/> instead.
/// </summary>
internal sealed class Vector256WebpPixelPackKernel : IWebpPixelPackKernel
{
    /// <summary>
    /// Swaps the R and B byte of each of the 8 pixels in a 32-byte load. AVX2's <c>vpshufb</c> shuffles each
    /// 128-bit half independently, so the high half's indices must reference bytes 16-31 (not repeat 0-15) or
    /// they'd zero out instead of selecting from that half -- see <see cref="Vector128WebpPixelPackKernel"/>'s
    /// <c>SwapRedBlue</c> for the per-pixel pattern this repeats, offset by 16 for the upper lane.
    /// </summary>
    private static readonly Vector256<byte> SwapRedBlue = Vector256.Create(
        Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15),
        Vector128.Create((byte)18, 17, 16, 19, 22, 21, 20, 23, 26, 25, 24, 27, 30, 29, 28, 31));

    /// <summary>Isolates the 8 alpha byte lanes (one per pixel) of a 32-byte vector; every other lane is zeroed.</summary>
    private static readonly Vector256<byte> AlphaLanes = Vector256.Create(
        Vector128.Create((byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF),
        Vector128.Create((byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF));

    private readonly Vector128WebpPixelPackKernel _extractFallback = new();

    public bool GatherRgba32(ReadOnlySpan<byte> rgba, Span<uint> argb)
    {
        int pixelsPerIteration = Vector256<byte>.Count / 4;
        int i = 0;
        var nonOpaqueAccumulator = Vector256<byte>.Zero;

        for (; i + pixelsPerIteration <= argb.Length; i += pixelsPerIteration)
        {
            var source = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(rgba.Slice(i * 4, Vector256<byte>.Count)));
            var packed = Vector256.Shuffle(source, SwapRedBlue);
            packed.AsUInt32().StoreUnsafe(ref argb[i]);

            nonOpaqueAccumulator |= ~Vector256.Equals(source, Vector256.Create((byte)0xFF));
        }

        bool hasAlpha = (nonOpaqueAccumulator & AlphaLanes) != Vector256<byte>.Zero;

        for (; i < argb.Length; i++)
        {
            int o = i * 4;
            byte r = rgba[o];
            byte g = rgba[o + 1];
            byte b = rgba[o + 2];
            byte a = rgba[o + 3];
            hasAlpha |= a != 0xFF;
            argb[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        return hasAlpha;
    }

    public void ExtractRgb(ReadOnlySpan<uint> argb, Span<byte> rgb) => _extractFallback.ExtractRgb(argb, rgb);
}
