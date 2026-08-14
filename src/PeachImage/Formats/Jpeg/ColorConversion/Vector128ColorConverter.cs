using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// SIMD color converter using <see cref="Vector128{T}"/>'s cross-platform generic static API: the BT.601
/// multiply-add chain (see <see cref="ScalarColorConverter"/> for the underlying math) is computed 4 pixels
/// at a time. JITs to SSE2 on x86 and AdvSimd on Arm from one source file.
/// </summary>
/// <remarks>See <see cref="Vector256ColorConverter"/>'s remarks — same widen/narrow approach, 4 lanes instead of 8.</remarks>
internal sealed class Vector128ColorConverter : IColorConverter
{
    private const int Lanes = 4;

    private static readonly Vector128<float> V1_402 = Vector128.Create(1.402f);
    private static readonly Vector128<float> V0_344136 = Vector128.Create(0.344136f);
    private static readonly Vector128<float> V0_714136 = Vector128.Create(0.714136f);
    private static readonly Vector128<float> V1_772 = Vector128.Create(1.772f);
    private static readonly Vector128<float> V128 = Vector128.Create(128f);
    private static readonly Vector128<float> VZero = Vector128<float>.Zero;
    private static readonly Vector128<float> V255 = Vector128.Create(255f);
    private static readonly Vector128<float> V0_299 = Vector128.Create(0.299f);
    private static readonly Vector128<float> V0_587 = Vector128.Create(0.587f);
    private static readonly Vector128<float> V0_114 = Vector128.Create(0.114f);
    private static readonly Vector128<float> V0_168736 = Vector128.Create(0.168736f);
    private static readonly Vector128<float> V0_331264 = Vector128.Create(0.331264f);
    private static readonly Vector128<float> V0_5 = Vector128.Create(0.5f);
    private static readonly Vector128<float> V0_418688 = Vector128.Create(0.418688f);
    private static readonly Vector128<float> V0_081312 = Vector128.Create(0.081312f);

    private static readonly Vector128<float> RoundingBias = Vector128.Create(0.5f);

    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            StoreRounded(Clamp(yv + (V1_402 * crv)), rgb, (i * 3) + 0, 3);
            StoreRounded(Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv)), rgb, (i * 3) + 1, 3);
            StoreRounded(Clamp(yv + (V1_772 * cbv)), rgb, (i * 3) + 2, 3);
        }

        for (; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ScalarColorConverter.ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 3;
            rgb[offset] = r;
            rgb[offset + 1] = g;
            rgb[offset + 2] = b;
        }
    }

    public void YcckToCmyk(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, Span<byte> cmyk, int pixelCount)
    {
        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            var rv = Clamp(yv + (V1_402 * crv));
            var gv = Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv));
            var bv = Clamp(yv + (V1_772 * cbv));

            // Round R/G/B first, then invert — see Vector256ColorConverter.YcckToCmyk's remarks.
            StoreRounded(rv, cmyk, (i * 4) + 0, 4, invert: true);
            StoreRounded(gv, cmyk, (i * 4) + 1, 4, invert: true);
            StoreRounded(bv, cmyk, (i * 4) + 2, 4, invert: true);

            for (int lane = 0; lane < Lanes; lane++)
            {
                cmyk[((i + lane) * 4) + 3] = k[i + lane];
            }
        }

        for (; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ScalarColorConverter.ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 4;
            cmyk[offset] = (byte)(255 - r);
            cmyk[offset + 1] = (byte)(255 - g);
            cmyk[offset + 2] = (byte)(255 - b);
            cmyk[offset + 3] = k[i];
        }
    }

    public void RgbToYCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int pixelCount)
    {
        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var (rv, gv, bv) = LoadWidenedInterleaved(rgb, i);

            StoreRounded(Clamp((V0_299 * rv) + (V0_587 * gv) + (V0_114 * bv)), y, i, 1);
            StoreRounded(Clamp(V128 - (V0_168736 * rv) - (V0_331264 * gv) + (V0_5 * bv)), cb, i, 1);
            StoreRounded(Clamp(V128 + (V0_5 * rv) - (V0_418688 * gv) - (V0_081312 * bv)), cr, i, 1);
        }

        for (; i < pixelCount; i++)
        {
            (y[i], cb[i], cr[i]) = ScalarColorConverter.ConvertRgbPixel(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
        }
    }

    private static Vector128<float> LoadWidened(ReadOnlySpan<byte> source, int offset)
    {
        Span<byte> padded = stackalloc byte[16];
        source.Slice(offset, Lanes).CopyTo(padded);
        return WidenBytes(padded);
    }

    private static (Vector128<float> R, Vector128<float> G, Vector128<float> B) LoadWidenedInterleaved(ReadOnlySpan<byte> rgb, int i)
    {
        Span<byte> rPadded = stackalloc byte[16];
        Span<byte> gPadded = stackalloc byte[16];
        Span<byte> bPadded = stackalloc byte[16];
        for (int lane = 0; lane < Lanes; lane++)
        {
            int offset = (i + lane) * 3;
            rPadded[lane] = rgb[offset];
            gPadded[lane] = rgb[offset + 1];
            bPadded[lane] = rgb[offset + 2];
        }

        return (WidenBytes(rPadded), WidenBytes(gPadded), WidenBytes(bPadded));
    }

    /// <summary><paramref name="padded16"/>'s first <see cref="Lanes"/> bytes widened to a <see cref="Vector128{Single}"/> via byte-&gt;ushort-&gt;uint widen then a hardware uint-&gt;float convert — the rest of the 16 bytes is don't-care padding, never read past lane 3.</summary>
    private static Vector128<float> WidenBytes(ReadOnlySpan<byte> padded16)
    {
        var byteVec = Vector128.Create(padded16);
        var ushortVec = Vector128.WidenLower(byteVec);
        var uintVec = Vector128.WidenLower(ushortVec);
        return Vector128.ConvertToSingle(uintVec);
    }

    private static Vector128<int> ToRoundedInt(Vector128<float> value) => Vector128.ConvertToInt32(value + RoundingBias);

    private static void StoreRounded(Vector128<float> value, Span<byte> destination, int firstOffset, int stride, bool invert = false)
    {
        Span<int> rounded = stackalloc int[Lanes];
        ToRoundedInt(value).CopyTo(rounded);
        for (int lane = 0; lane < Lanes; lane++)
        {
            destination[firstOffset + (lane * stride)] = (byte)(invert ? 255 - rounded[lane] : rounded[lane]);
        }
    }

    private static Vector128<float> Clamp(Vector128<float> value) => Vector128.Min(Vector128.Max(value, VZero), V255);
}
