using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Internal.PixelFormatConversion;

/// <summary>
/// Shared, SIMD-optimized pixel-reshaping kernels used by every format's <c>PixelFormatConverter</c>
/// (Bmp/Gif/Jpeg/Png/Webp/Avif). These operations — widening a source channel layout into a wider one,
/// narrowing the reverse, broadcasting grayscale into color channels, averaging color into grayscale, bit-depth
/// widen/narrow, and the CMYK-&gt;RGB formula — are pure byte reshaping with no format-specific color-space math
/// (unlike each format's own YCbCr/YUV decoders, which legitimately stay separate per format). Consolidated
/// here instead of being duplicated per format so there is one vectorized implementation to verify and
/// maintain rather than six.
/// </summary>
/// <remarks>
/// Every kernel has a scalar fallback (used directly when <see cref="Vector128.IsHardwareAccelerated"/> is
/// <see langword="false"/>, and for the non-vector-width-aligned tail of every loop) plus a
/// <see cref="Vector128{T}"/> tier built from the portable, cross-platform generic static API
/// (<c>Vector128.Shuffle</c>/<c>Widen*</c>/<c>Narrow</c>/<c>ShiftRight*</c>), matching this codebase's existing
/// SIMD conventions (see <c>Jpeg.ColorConversion.Vector128ColorConverter</c>). <see cref="Vector256{T}"/> is
/// deliberately not attempted here: unlike JPEG's YCbCr math (wide float multiply-add chains that benefit from
/// doubling lane count), every kernel below is a single shuffle/narrow/widen pass, so a 256-bit tier would
/// mean juggling two independent 128-bit-lane-local shuffle tables (AVX2's <c>vpshufb</c> does not cross the
/// 128-bit lane boundary) for a proportionally smaller share of total work than JPEG's per-pixel float chain —
/// the same "not worth it" call this codebase already made for WebP's VP8 color converter.
/// <see cref="ConvertCmyk32ToRgba32"/> is the one kernel that needs genuine platform-specific intrinsics
/// (<see cref="Sse2.AddSaturate(Vector128{byte}, Vector128{byte})"/>/<see cref="AdvSimd.AddSaturate(Vector128{byte}, Vector128{byte})"/>)
/// rather than the portable API, because saturating arithmetic has no portable <c>Vector128{T}</c> equivalent.
/// </remarks>
internal static class PixelFormatConversionKernels
{
    // Alpha (or 16-bit alpha's low/high byte pair) is 0xFF at exactly the lanes that should read as 255 and
    // 0x00 everywhere else, so `x | mask` both "fills the gap left by an out-of-range shuffle index (0)" and
    // "forces a lane to 255 regardless of its current value" — no separate blend/select step is needed.
    private static readonly Vector128<byte> Alpha255EveryFourthLane =
        Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);

    // 4 pixels of Rgb24 (12 bytes) -> 4 pixels of Rgba32 (16 bytes): copy R,G,B, leave the alpha lane as the
    // shuffle's "out of range" zero (index >= 16), then OR in Alpha255EveryFourthLane.
    private static readonly Vector128<byte> Rgb24ToRgba32Shuffle =
        Vector128.Create((byte)0, 1, 2, 255, 3, 4, 5, 255, 6, 7, 8, 255, 9, 10, 11, 255);

    // 4 pixels of Rgba32 (16 bytes) -> 4 pixels of Rgb24 packed into the low 12 bytes (last 4 bytes unused).
    private static readonly Vector128<byte> Rgba32ToRgb24Shuffle =
        Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 255, 255, 255, 255);

    // Broadcasts K (source lane 3/7/11/15 per pixel) across all 4 lanes of that pixel, so it can be
    // saturating-added against [C,M,Y,K] in one instruction.
    private static readonly Vector128<byte> CmykBroadcastKShuffle =
        Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);

    // 8 pixels of Gray8 (8 bytes, zero-padded to 16) -> 24 bytes of Rgb24, split as a 16-byte low store plus
    // an 8-byte high store (Rgb24's 3-byte stride never lands on a 16-byte boundary, so a single store can't
    // cover a whole batch — the same lo/hi split this codebase's own RGB24 YCbCr store already uses).
    private static readonly Vector128<byte> Gray8ToRgb24ShuffleLo =
        Vector128.Create((byte)0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5);

    private static readonly Vector128<byte> Gray8ToRgb24ShuffleHi =
        Vector128.Create((byte)5, 5, 6, 6, 6, 7, 7, 7, 255, 255, 255, 255, 255, 255, 255, 255);

    // 16 pixels of Gray8 loaded as one 16-byte vector -> four 16-byte Rgba32 stores (4 pixels each), one
    // shuffle mask per quarter of the source vector.
    private static readonly Vector128<byte>[] Gray8ToRgba32Shuffles =
    [
        Vector128.Create((byte)0, 0, 0, 255, 1, 1, 1, 255, 2, 2, 2, 255, 3, 3, 3, 255),
        Vector128.Create((byte)4, 4, 4, 255, 5, 5, 5, 255, 6, 6, 6, 255, 7, 7, 7, 255),
        Vector128.Create((byte)8, 8, 8, 255, 9, 9, 9, 255, 10, 10, 10, 255, 11, 11, 11, 255),
        Vector128.Create((byte)12, 12, 12, 255, 13, 13, 13, 255, 14, 14, 14, 255, 15, 15, 15, 255),
    ];

    private static readonly Vector128<ushort> LumaWeightR = Vector128.Create((ushort)77);
    private static readonly Vector128<ushort> LumaWeightG = Vector128.Create((ushort)151);
    private static readonly Vector128<ushort> LumaWeightB = Vector128.Create((ushort)28);
    private static readonly Vector128<ushort> LumaRoundingBias = Vector128.Create((ushort)128);

    /// <summary>Expands 8-bit Rgb24 to Rgba32, filling alpha with 255.</summary>
    public static void ExpandRgb24ToRgba32(ReadOnlySpan<byte> rgb, Span<byte> rgba, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                var shuffled = Vector128.Shuffle(LoadBytes(rgb, i * 3, 12), Rgb24ToRgba32Shuffle) | Alpha255EveryFourthLane;
                shuffled.StoreUnsafe(ref rgba[i * 4]);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 3, d = i * 4;
            rgba[d] = rgb[s];
            rgba[d + 1] = rgb[s + 1];
            rgba[d + 2] = rgb[s + 2];
            rgba[d + 3] = 255;
        }
    }

    /// <summary>Narrows 8-bit Rgba32 to Rgb24, dropping alpha.</summary>
    public static void NarrowRgba32ToRgb24(ReadOnlySpan<byte> rgba, Span<byte> rgb, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                var shuffled = Vector128.Shuffle(LoadBytes(rgba, i * 4, 16), Rgba32ToRgb24Shuffle);
                StoreBytes(shuffled, rgb, i * 3, 12);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 4, d = i * 3;
            rgb[d] = rgba[s];
            rgb[d + 1] = rgba[s + 1];
            rgb[d + 2] = rgba[s + 2];
        }
    }

    /// <summary>Broadcasts 8-bit Gray8 into Rgb24 (R=G=B=gray).</summary>
    public static void ExpandGray8ToRgb24(ReadOnlySpan<byte> gray, Span<byte> rgb, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= pixelCount; i += 8)
            {
                var source = LoadBytes(gray, i, 8);
                Vector128.Shuffle(source, Gray8ToRgb24ShuffleLo).StoreUnsafe(ref rgb[i * 3]);
                StoreBytes(Vector128.Shuffle(source, Gray8ToRgb24ShuffleHi), rgb, (i * 3) + 16, 8);
            }
        }

        for (; i < pixelCount; i++)
        {
            byte g = gray[i];
            int d = i * 3;
            rgb[d] = g;
            rgb[d + 1] = g;
            rgb[d + 2] = g;
        }
    }

    /// <summary>Broadcasts 8-bit Gray8 into Rgba32 (R=G=B=gray, A=255).</summary>
    public static void ExpandGray8ToRgba32(ReadOnlySpan<byte> gray, Span<byte> rgba, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= pixelCount; i += 16)
            {
                var source = LoadBytes(gray, i, 16);
                for (int quarter = 0; quarter < 4; quarter++)
                {
                    var shuffled = Vector128.Shuffle(source, Gray8ToRgba32Shuffles[quarter]) | Alpha255EveryFourthLane;
                    shuffled.StoreUnsafe(ref rgba[(i + (quarter * 4)) * 4]);
                }
            }
        }

        for (; i < pixelCount; i++)
        {
            byte g = gray[i];
            int d = i * 4;
            rgba[d] = g;
            rgba[d + 1] = g;
            rgba[d + 2] = g;
            rgba[d + 3] = 255;
        }
    }

    /// <summary>Computes ITU-R BT.601 luma (fixed-point, weights scaled by 256) from 8-bit Rgb24 into Gray8.</summary>
    public static void ComputeLumaFromRgb24(ReadOnlySpan<byte> rgb, Span<byte> gray, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Span<byte> rPad = stackalloc byte[16];
            Span<byte> gPad = stackalloc byte[16];
            Span<byte> bPad = stackalloc byte[16];
            for (; i + 8 <= pixelCount; i += 8)
            {
                for (int lane = 0; lane < 8; lane++)
                {
                    int s = (i + lane) * 3;
                    rPad[lane] = rgb[s];
                    gPad[lane] = rgb[s + 1];
                    bPad[lane] = rgb[s + 2];
                }

                StoreLuma(Vector128.LoadUnsafe(ref rPad[0]), Vector128.LoadUnsafe(ref gPad[0]), Vector128.LoadUnsafe(ref bPad[0]), gray, i);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 3;
            gray[i] = Luma(rgb[s], rgb[s + 1], rgb[s + 2]);
        }
    }

    /// <summary>Computes ITU-R BT.601 luma (fixed-point, weights scaled by 256) from 8-bit Rgba32 (alpha ignored) into Gray8.</summary>
    public static void ComputeLumaFromRgba32(ReadOnlySpan<byte> rgba, Span<byte> gray, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Span<byte> rPad = stackalloc byte[16];
            Span<byte> gPad = stackalloc byte[16];
            Span<byte> bPad = stackalloc byte[16];
            for (; i + 8 <= pixelCount; i += 8)
            {
                for (int lane = 0; lane < 8; lane++)
                {
                    int s = (i + lane) * 4;
                    rPad[lane] = rgba[s];
                    gPad[lane] = rgba[s + 1];
                    bPad[lane] = rgba[s + 2];
                }

                StoreLuma(Vector128.LoadUnsafe(ref rPad[0]), Vector128.LoadUnsafe(ref gPad[0]), Vector128.LoadUnsafe(ref bPad[0]), gray, i);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 4;
            gray[i] = Luma(rgba[s], rgba[s + 1], rgba[s + 2]);
        }
    }

    /// <summary>
    /// Converts 8-bit Cmyk32 to Rgba32 using the naive additive formula
    /// <c>R = 255 - min(255, C + K)</c> (and likewise for G/B from M/Y), A = 255. Not colorimetric/ICC-aware —
    /// matches the convention already documented on <c>Jpeg.Decoding.PixelFormatConverter</c>.
    /// </summary>
    public static void ConvertCmyk32ToRgba32(ReadOnlySpan<byte> cmyk, Span<byte> rgba, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.IsSupported))
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                var source = LoadBytes(cmyk, i * 4, 16);
                var kBroadcast = Vector128.Shuffle(source, CmykBroadcastKShuffle);
                var saturatedSum = Sse2.IsSupported ? Sse2.AddSaturate(source, kBroadcast) : AdvSimd.AddSaturate(source, kBroadcast);
                var inverted = saturatedSum ^ Vector128<byte>.AllBitsSet; // 255 - x, for a byte, is exactly ~x.
                var result = inverted | Alpha255EveryFourthLane;
                result.StoreUnsafe(ref rgba[i * 4]);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 4, d = i * 4;
            byte k = cmyk[s + 3];
            rgba[d] = (byte)(255 - Math.Min(255, cmyk[s] + k));
            rgba[d + 1] = (byte)(255 - Math.Min(255, cmyk[s + 1] + k));
            rgba[d + 2] = (byte)(255 - Math.Min(255, cmyk[s + 2] + k));
            rgba[d + 3] = 255;
        }
    }

    /// <summary>Widens each 8-bit sample to 16 bits by duplicating it into both bytes (<c>v -&gt; (v &lt;&lt; 8) | v</c>), independent of channel count.</summary>
    public static void WidenBytesToUInt16(ReadOnlySpan<byte> source, Span<ushort> destination)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                var bytes = LoadBytes(source, i, 16);
                var lower = Vector128.WidenLower(bytes);
                var upper = Vector128.WidenUpper(bytes);
                (lower | Vector128.ShiftLeft(lower, 8)).StoreUnsafe(ref destination[i]);
                (upper | Vector128.ShiftLeft(upper, 8)).StoreUnsafe(ref destination[i + 8]);
            }
        }

        for (; i < source.Length; i++)
        {
            byte v = source[i];
            destination[i] = (ushort)((v << 8) | v);
        }
    }

    /// <summary>Narrows each 16-bit sample to 8 bits by top-byte truncation (<c>v -&gt; v &gt;&gt; 8</c>), independent of channel count.</summary>
    public static void NarrowUInt16ToBytes(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                var lo = Vector128.ShiftRightLogical(LoadUInt16s(source, i, 8), 8);
                var hi = Vector128.ShiftRightLogical(LoadUInt16s(source, i + 8, 8), 8);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = (byte)(source[i] >> 8);
        }
    }

    /// <summary>Narrows 16-bit Gray16 to 8-bit Rgba32 in one pass (top-byte truncation, then broadcast with A=255) — no Gray8 intermediate buffer.</summary>
    public static void ExpandNarrowGray16ToRgba32(ReadOnlySpan<ushort> gray16, Span<byte> rgba32, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= pixelCount; i += 8)
            {
                var narrowed = NarrowTop8(Vector128.ShiftRightLogical(LoadUInt16s(gray16, i, 8), 8));
                (Vector128.Shuffle(narrowed, Gray8ToRgba32Shuffles[0]) | Alpha255EveryFourthLane).StoreUnsafe(ref rgba32[i * 4]);
                (Vector128.Shuffle(narrowed, Gray8ToRgba32Shuffles[1]) | Alpha255EveryFourthLane).StoreUnsafe(ref rgba32[(i + 4) * 4]);
            }
        }

        for (; i < pixelCount; i++)
        {
            byte g = (byte)(gray16[i] >> 8);
            int d = i * 4;
            rgba32[d] = g;
            rgba32[d + 1] = g;
            rgba32[d + 2] = g;
            rgba32[d + 3] = 255;
        }
    }

    /// <summary>Narrows 16-bit Rgb48 to 8-bit Rgba32 in one pass (top-byte truncation, then expand with A=255) — no Rgb24 intermediate buffer.</summary>
    public static void ExpandNarrowRgb48ToRgba32(ReadOnlySpan<ushort> rgb48, Span<byte> rgba32, int pixelCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                var lo = Vector128.ShiftRightLogical(LoadUInt16s(rgb48, i * 3, 8), 8);
                var hi = Vector128.ShiftRightLogical(LoadUInt16s(rgb48, (i * 3) + 8, 4), 8);
                var narrowed = Vector128.Narrow(lo, hi); // First 12 bytes are the 4 pixels' R,G,B; rest is don't-care.
                (Vector128.Shuffle(narrowed, Rgb24ToRgba32Shuffle) | Alpha255EveryFourthLane).StoreUnsafe(ref rgba32[i * 4]);
            }
        }

        for (; i < pixelCount; i++)
        {
            int s = i * 3, d = i * 4;
            rgba32[d] = (byte)(rgb48[s] >> 8);
            rgba32[d + 1] = (byte)(rgb48[s + 1] >> 8);
            rgba32[d + 2] = (byte)(rgb48[s + 2] >> 8);
            rgba32[d + 3] = 255;
        }
    }

    private static byte Luma(byte r, byte g, byte b) =>
        (byte)Math.Clamp(Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b), MidpointRounding.AwayFromZero), 0, 255);

    private static void StoreLuma(Vector128<byte> r, Vector128<byte> g, Vector128<byte> b, Span<byte> gray, int offset)
    {
        var weighted = (Vector128.WidenLower(r) * LumaWeightR) + (Vector128.WidenLower(g) * LumaWeightG) + (Vector128.WidenLower(b) * LumaWeightB) + LumaRoundingBias;
        var shifted = Vector128.ShiftRightLogical(weighted, 8);
        NarrowTop8(shifted).GetLower().StoreUnsafe(ref gray[offset]);
    }

    /// <summary>Right-shifts each of 8 ushort lanes by 8 (already done by the caller here) — narrows a value already known to fit in a byte down to one, without a saturating narrow's extra clamp instructions being needed for correctness (values are guaranteed &lt;= 255).</summary>
    private static Vector128<byte> NarrowTop8(Vector128<ushort> alreadyByteRange) => Vector128.Narrow(alreadyByteRange, alreadyByteRange);

    /// <summary>
    /// Loads 16 bytes starting at <paramref name="offset"/>. When a batch's natural width is under 16 bytes
    /// (e.g. Rgb24's 12-byte, 4-pixel groups), the direct path deliberately reads a few bytes belonging to
    /// the <em>next</em> batch — always safe (still inside the buffer, still valid pixel data) except for the
    /// last batch, where <paramref name="validBytes"/>-limited padded copy is used instead. Only that padded
    /// fallback needs a stack copy: an early, unconditional "always pad" version of this measured
    /// meaningfully <em>slower</em> than the scalar loop it replaced (a stackalloc + <c>CopyTo</c> on every
    /// single batch, most of which didn't need it) — this bounds check is what makes the fast path actually
    /// fast.
    /// </summary>
    private static Vector128<byte> LoadBytes(ReadOnlySpan<byte> source, int offset, int validBytes)
    {
        if (offset + 16 <= source.Length)
        {
            return Vector128.LoadUnsafe(in source[offset]);
        }

        Span<byte> pad = stackalloc byte[16];
        source.Slice(offset, validBytes).CopyTo(pad);
        return Vector128.LoadUnsafe(ref pad[0]);
    }

    /// <summary>Ushort counterpart of <see cref="LoadBytes"/> — <paramref name="validCount"/> is in ushort elements, and the fast-path threshold is 8 elements (16 bytes = one <see cref="Vector128{UInt16}"/>).</summary>
    private static Vector128<ushort> LoadUInt16s(ReadOnlySpan<ushort> source, int offset, int validCount)
    {
        if (offset + 8 <= source.Length)
        {
            return Vector128.LoadUnsafe(in source[offset]);
        }

        Span<ushort> pad = stackalloc ushort[8];
        source.Slice(offset, validCount).CopyTo(pad);
        return Vector128.LoadUnsafe(ref pad[0]);
    }

    /// <summary>Store counterpart of <see cref="LoadBytes"/>: writes 16 bytes directly when the destination has that much room left from <paramref name="offset"/> (spilling harmlessly into a region the next batch's own store will overwrite), else copies only the first <paramref name="validBytes"/> bytes so the true tail never writes past <paramref name="destination"/>'s end.</summary>
    private static void StoreBytes(Vector128<byte> value, Span<byte> destination, int offset, int validBytes)
    {
        if (offset + 16 <= destination.Length)
        {
            value.StoreUnsafe(ref destination[offset]);
            return;
        }

        Span<byte> pad = stackalloc byte[16];
        value.StoreUnsafe(ref pad[0]);
        pad[..validBytes].CopyTo(destination.Slice(offset, validBytes));
    }
}
