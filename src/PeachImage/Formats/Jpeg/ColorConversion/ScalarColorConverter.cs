namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// Reference (always-correct, not performance-optimized) color converter using ITU-R BT.601 coefficients,
/// the standard JPEG/JFIF luma/chroma matrix. The per-pixel conversion helpers are also reused by the SIMD
/// converters' scalar tail loops (for pixel counts not evenly divisible by the vector width).
/// </summary>
internal sealed class ScalarColorConverter : IColorConverter
{
    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 3;
            rgb[offset] = r;
            rgb[offset + 1] = g;
            rgb[offset + 2] = b;
        }
    }

    public void YcckToCmyk(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, Span<byte> cmyk, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 4;
            cmyk[offset] = (byte)(255 - r);
            cmyk[offset + 1] = (byte)(255 - g);
            cmyk[offset + 2] = (byte)(255 - b);
            cmyk[offset + 3] = k[i];
        }
    }

    public void RgbToYCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 3;
            (y[i], cb[i], cr[i]) = ConvertRgbPixel(rgb[offset], rgb[offset + 1], rgb[offset + 2]);
        }
    }

    /// <summary>Converts one Y/Cb/Cr sample triple to RGB.</summary>
    public static (byte R, byte G, byte B) ConvertYCbCrPixel(byte yValue, byte cbValue, byte crValue)
    {
        double y = yValue;
        double cb = cbValue - 128.0;
        double cr = crValue - 128.0;

        double r = y + (1.402 * cr);
        double g = y - (0.344136 * cb) - (0.714136 * cr);
        double b = y + (1.772 * cb);

        return (ClampByte(r), ClampByte(g), ClampByte(b));
    }

    /// <summary>Converts one R/G/B sample triple to Y/Cb/Cr.</summary>
    public static (byte Y, byte Cb, byte Cr) ConvertRgbPixel(byte rValue, byte gValue, byte bValue)
    {
        double r = rValue;
        double g = gValue;
        double b = bValue;

        double yy = (0.299 * r) + (0.587 * g) + (0.114 * b);
        double cbb = 128.0 - (0.168736 * r) - (0.331264 * g) + (0.5 * b);
        double crr = 128.0 + (0.5 * r) - (0.418688 * g) - (0.081312 * b);

        return (ClampByte(yy), ClampByte(cbb), ClampByte(crr));
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
