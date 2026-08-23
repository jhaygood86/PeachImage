using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Kernels;

/// <summary>SIMD tier using <see cref="Vector128{T}"/>'s cross-platform generic static API (JITs to SSE2 on x86, AdvSimd on Arm) -- 4 pixels/16 bytes at a time.</summary>
internal sealed class Vector128WebpPixelPackKernel : IWebpPixelPackKernel
{
    /// <summary>Swaps the R and B byte of each of the 4 pixels in a 16-byte load: <c>R,G,B,A</c> in memory becomes the little-endian byte order of <c>0xAARRGGBB</c> (<c>B,G,R,A</c>).</summary>
    private static readonly Vector128<byte> SwapRedBlue = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    /// <summary>Isolates the 4 alpha byte lanes (one per pixel) of a 16-byte vector; every other lane is zeroed.</summary>
    private static readonly Vector128<byte> AlphaLanes = Vector128.Create(
        (byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF);

    /// <summary>
    /// Rearranges a 16-byte <c>B,G,R,A</c> (x4 pixels) load into <c>R,G,B</c> (x4 pixels, 12 bytes) in its low
    /// 12 bytes; the high 4 bytes are filler (reused source bytes), since the store below always writes a
    /// full 16 bytes and only the low 12 belong to the current iteration -- see <see cref="ExtractRgb"/>.
    /// </summary>
    private static readonly Vector128<byte> ExtractRgbShuffle = Vector128.Create(
        (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 2, 2, 2, 2);

    public bool GatherRgba32(ReadOnlySpan<byte> rgba, Span<uint> argb)
    {
        int pixelsPerIteration = Vector128<byte>.Count / 4;
        int i = 0;
        var nonOpaqueAccumulator = Vector128<byte>.Zero;

        for (; i + pixelsPerIteration <= argb.Length; i += pixelsPerIteration)
        {
            var source = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(rgba.Slice(i * 4, Vector128<byte>.Count)));
            var packed = Vector128.Shuffle(source, SwapRedBlue);
            packed.AsUInt32().StoreUnsafe(ref argb[i]);

            nonOpaqueAccumulator |= ~Vector128.Equals(source, Vector128.Create((byte)0xFF));
        }

        bool hasAlpha = (nonOpaqueAccumulator & AlphaLanes) != Vector128<byte>.Zero;

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

    public void ExtractRgb(ReadOnlySpan<uint> argb, Span<byte> rgb)
    {
        const int Lanes = 4; // Pixels converted per vector iteration.
        int i = 0;

        // Mirrors Vector128Vp8ColorConverter.ConvertRow's interleaved-store trick: each store writes a full
        // 16 bytes but only 12 belong to this iteration (the rest are rewritten by the next one), so the loop
        // must stop while 16 bytes of destination space remain; the tail falls to the scalar loop below.
        for (; i + Lanes <= argb.Length && (i * 3) + Vector128<byte>.Count <= rgb.Length; i += Lanes)
        {
            var source = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(argb.Slice(i, Lanes))).AsByte();
            var shuffled = Vector128.Shuffle(source, ExtractRgbShuffle);
            shuffled.StoreUnsafe(ref rgb[i * 3]);
        }

        for (; i < argb.Length; i++)
        {
            uint pixel = argb[i];
            int o = i * 3;
            rgb[o + 0] = (byte)(pixel >> 16);
            rgb[o + 1] = (byte)(pixel >> 8);
            rgb[o + 2] = (byte)pixel;
        }
    }
}
