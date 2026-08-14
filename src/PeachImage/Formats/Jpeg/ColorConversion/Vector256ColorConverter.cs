using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// SIMD color converter using <see cref="Vector256{T}"/>'s cross-platform generic static API: the BT.601
/// multiply-add chain is computed 8 pixels at a time. Selected only when
/// <see cref="Vector256.IsHardwareAccelerated"/> (effectively: AVX/AVX2 present).
/// </summary>
internal sealed class Vector256ColorConverter : IColorConverter
{
    private const int Lanes = 8;

    private static readonly Vector256<float> V1_402 = Vector256.Create(1.402f);
    private static readonly Vector256<float> V0_344136 = Vector256.Create(0.344136f);
    private static readonly Vector256<float> V0_714136 = Vector256.Create(0.714136f);
    private static readonly Vector256<float> V1_772 = Vector256.Create(1.772f);
    private static readonly Vector256<float> V128 = Vector256.Create(128f);
    private static readonly Vector256<float> VZero = Vector256<float>.Zero;
    private static readonly Vector256<float> V255 = Vector256.Create(255f);
    private static readonly Vector256<float> V0_299 = Vector256.Create(0.299f);
    private static readonly Vector256<float> V0_587 = Vector256.Create(0.587f);
    private static readonly Vector256<float> V0_114 = Vector256.Create(0.114f);
    private static readonly Vector256<float> V0_168736 = Vector256.Create(0.168736f);
    private static readonly Vector256<float> V0_331264 = Vector256.Create(0.331264f);
    private static readonly Vector256<float> V0_5 = Vector256.Create(0.5f);
    private static readonly Vector256<float> V0_418688 = Vector256.Create(0.418688f);
    private static readonly Vector256<float> V0_081312 = Vector256.Create(0.081312f);

    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        Span<float> rBuf = stackalloc float[Lanes];
        Span<float> gBuf = stackalloc float[Lanes];
        Span<float> bBuf = stackalloc float[Lanes];

        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = Load(y, i);
            var cbv = Load(cb, i) - V128;
            var crv = Load(cr, i) - V128;

            Clamp(yv + (V1_402 * crv)).CopyTo(rBuf);
            Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv)).CopyTo(gBuf);
            Clamp(yv + (V1_772 * cbv)).CopyTo(bBuf);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int offset = (i + lane) * 3;
                rgb[offset] = ToByte(rBuf[lane]);
                rgb[offset + 1] = ToByte(gBuf[lane]);
                rgb[offset + 2] = ToByte(bBuf[lane]);
            }
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
        Span<float> rBuf = stackalloc float[Lanes];
        Span<float> gBuf = stackalloc float[Lanes];
        Span<float> bBuf = stackalloc float[Lanes];

        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = Load(y, i);
            var cbv = Load(cb, i) - V128;
            var crv = Load(cr, i) - V128;

            Clamp(yv + (V1_402 * crv)).CopyTo(rBuf);
            Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv)).CopyTo(gBuf);
            Clamp(yv + (V1_772 * cbv)).CopyTo(bBuf);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int offset = (i + lane) * 4;
                cmyk[offset] = (byte)(255 - ToByte(rBuf[lane]));
                cmyk[offset + 1] = (byte)(255 - ToByte(gBuf[lane]));
                cmyk[offset + 2] = (byte)(255 - ToByte(bBuf[lane]));
                cmyk[offset + 3] = k[i + lane];
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
        Span<float> yBuf = stackalloc float[Lanes];
        Span<float> cbBuf = stackalloc float[Lanes];
        Span<float> crBuf = stackalloc float[Lanes];
        Span<float> rLane = stackalloc float[Lanes];
        Span<float> gLane = stackalloc float[Lanes];
        Span<float> bLane = stackalloc float[Lanes];

        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            for (int lane = 0; lane < Lanes; lane++)
            {
                int offset = (i + lane) * 3;
                rLane[lane] = rgb[offset];
                gLane[lane] = rgb[offset + 1];
                bLane[lane] = rgb[offset + 2];
            }

            var rv = Vector256.Create((ReadOnlySpan<float>)rLane);
            var gv = Vector256.Create((ReadOnlySpan<float>)gLane);
            var bv = Vector256.Create((ReadOnlySpan<float>)bLane);

            Clamp((V0_299 * rv) + (V0_587 * gv) + (V0_114 * bv)).CopyTo(yBuf);
            Clamp(V128 - (V0_168736 * rv) - (V0_331264 * gv) + (V0_5 * bv)).CopyTo(cbBuf);
            Clamp(V128 + (V0_5 * rv) - (V0_418688 * gv) - (V0_081312 * bv)).CopyTo(crBuf);

            for (int lane = 0; lane < Lanes; lane++)
            {
                y[i + lane] = ToByte(yBuf[lane]);
                cb[i + lane] = ToByte(cbBuf[lane]);
                cr[i + lane] = ToByte(crBuf[lane]);
            }
        }

        for (; i < pixelCount; i++)
        {
            (y[i], cb[i], cr[i]) = ScalarColorConverter.ConvertRgbPixel(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
        }
    }

    private static Vector256<float> Load(ReadOnlySpan<byte> source, int offset)
    {
        Span<float> lanes = stackalloc float[Lanes];
        for (int i = 0; i < Lanes; i++)
        {
            lanes[i] = source[offset + i];
        }

        return Vector256.Create((ReadOnlySpan<float>)lanes);
    }

    private static Vector256<float> Clamp(Vector256<float> value) => Vector256.Min(Vector256.Max(value, VZero), V255);

    private static byte ToByte(float value) => (byte)MathF.Round(value, MidpointRounding.AwayFromZero);
}
