using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.ColorConversion;

namespace PeachImage.Tests.PixelFormatConversion;

/// <summary>
/// Profile-less CMYK and YCCK JPEGs convert to RGB through the public API (<c>ConvertTo</c> and <c>ConvertToSrgb</c>) with
/// <c>R = 255 x (1 - C) x (1 - K)</c>. The JPEG encoder cannot write four components, so the fixtures are tiny hand-assembled
/// baseline files: one flat 8x8 block per component, with and without the Adobe APP14 marker.
/// </summary>
public class ProfilelessCmykTests
{
    // Standard CMYK ink values (0 = no ink) and the RGB they must become.
    public static TheoryData<string, byte, byte, byte, byte, byte, byte, byte> Inks => new()
    {
        { "pure cyan", 255, 0, 0, 0, 0, 255, 255 },
        { "pure magenta", 0, 255, 0, 0, 255, 0, 255 },
        { "pure black", 0, 0, 0, 255, 0, 0, 0 },
        { "white paper", 0, 0, 0, 0, 255, 255, 255 },
        { "half black", 0, 0, 0, 128, 127, 127, 127 },
    };

    [Theory]
    [MemberData(nameof(Inks))]
    public void CmykJpegWithoutAdobeMarker_ConvertsToExpectedRgb(string name, byte c, byte m, byte y, byte k, byte r, byte g, byte b)
    {
        byte[] jpeg = BuildFourComponentJpeg([c, m, y, k], adobeTransform: null);

        using var cmyk = Image.Load(jpeg);
        Assert.Equal(PixelFormat.Cmyk32, cmyk.PixelFormat);

        AssertAllPixels(cmyk.ConvertTo(PixelFormat.Rgb24), PixelFormat.Rgb24, r, g, b, name);
        AssertAllPixels(cmyk.ConvertTo(PixelFormat.Rgba32), PixelFormat.Rgba32, r, g, b, name);
        AssertAllPixels(cmyk.ConvertToSrgb(), PixelFormat.Rgba32, r, g, b, name);
    }

    [Theory]
    [MemberData(nameof(Inks))]
    public void AdobeInvertedCmykJpeg_ConvertsToExpectedRgb(string name, byte c, byte m, byte y, byte k, byte r, byte g, byte b)
    {
        // Adobe transform 0 stores the inks inverted; the decoder undoes that before the conversion.
        byte[] jpeg = BuildFourComponentJpeg([(byte)(255 - c), (byte)(255 - m), (byte)(255 - y), (byte)(255 - k)], adobeTransform: 0);

        using var cmyk = Image.Load(jpeg);
        Assert.Equal(PixelFormat.Cmyk32, cmyk.PixelFormat);

        AssertAllPixels(cmyk.ConvertTo(PixelFormat.Rgb24), PixelFormat.Rgb24, r, g, b, name);
        AssertAllPixels(cmyk.ConvertToSrgb(), PixelFormat.Rgba32, r, g, b, name);
    }

    [Theory]
    [MemberData(nameof(Inks))]
    public void YcckJpeg_ConvertsToExpectedRgb_WithAndWithoutRawYcck(string name, byte c, byte m, byte y, byte k, byte r, byte g, byte b)
    {
        // YCCK stores YCbCr of (255 - C, 255 - M, 255 - Y) plus K. Adobe transform 2 is what marks it as YCCK.
        (byte yy, byte cb, byte cr) = ScalarColorConverter.ConvertRgbPixel((byte)(255 - c), (byte)(255 - m), (byte)(255 - y));
        byte[] jpeg = BuildFourComponentJpeg([yy, cb, cr, k], adobeTransform: 2);

        // Default: the decoder converts YCCK to standard CMYK, so it takes the same path as any CMYK image.
        using var cmyk = Image.Load(jpeg);
        Assert.Equal(PixelFormat.Cmyk32, cmyk.PixelFormat);
        AssertAllPixels(cmyk.ConvertTo(PixelFormat.Rgb24), PixelFormat.Rgb24, r, g, b, name);
        AssertAllPixels(cmyk.ConvertToSrgb(), PixelFormat.Rgba32, r, g, b, name);

        // DecodeRawYcck: the raw planes come back as Ycck32, which converts too.
        using var raw = Image.Load(jpeg, new JpegDecoderOptions { DecodeRawYcck = true });
        Assert.Equal(PixelFormat.Ycck32, raw.PixelFormat);
        AssertAllPixels(raw.ConvertTo(PixelFormat.Rgb24), PixelFormat.Rgb24, r, g, b, name);
        AssertAllPixels(raw.ConvertToSrgb(), PixelFormat.Rgba32, r, g, b, name);
    }

    [Fact]
    public void ConvertTo_Gray8_UsesLumaOfTheInkColour()
    {
        using var cmyk = Image.Create(2, 2, PixelFormat.Cmyk32);
        for (int i = 0; i < 4; i++)
        {
            cmyk.GetPixelSpan()[(i * 4) + 3] = 255;
        }

        using var gray = cmyk.ConvertTo(PixelFormat.Gray8);
        Assert.All(gray.GetPixelSpan().ToArray(), v => Assert.Equal(0, v));
    }

    [Fact]
    public void ConvertTo_IntoCmyk_IsStillNotSupported()
    {
        using var rgb = Image.Create(2, 2, PixelFormat.Rgb24);
        Assert.Throws<NotSupportedException>(() => rgb.ConvertTo(PixelFormat.Cmyk32));
    }

    [Fact]
    public void ConvertToSrgb_WithoutProfile_ReturnsRgbForCmykButTheSameImageForRgb()
    {
        using var rgb = Image.Create(2, 2, PixelFormat.Rgb24);
        Assert.Same(rgb, rgb.ConvertToSrgb());

        using var cmyk = Image.Create(2, 2, PixelFormat.Cmyk32);
        using var converted = cmyk.ConvertToSrgb();
        Assert.NotSame(cmyk, converted);
        Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
    }

    private static void AssertAllPixels(Image image, PixelFormat expectedFormat, byte r, byte g, byte b, string name)
    {
        using (image)
        {
            Assert.Equal(expectedFormat, image.PixelFormat);
            int channels = expectedFormat.GetChannelCount();
            var pixels = image.GetPixelSpan();
            for (int i = 0; i < image.Width * image.Height; i++)
            {
                Assert.True(Math.Abs(pixels[i * channels] - r) <= 2, $"{name}: R {pixels[i * channels]} vs {r}");
                Assert.True(Math.Abs(pixels[(i * channels) + 1] - g) <= 2, $"{name}: G {pixels[(i * channels) + 1]} vs {g}");
                Assert.True(Math.Abs(pixels[(i * channels) + 2] - b) <= 2, $"{name}: B {pixels[(i * channels) + 2]} vs {b}");
            }
        }
    }

    /// <summary>
    /// An 8x8 baseline JPEG of four 1x1-sampled components, each one flat block holding the given 0..255 sample. Quantization is all ones, so the
    /// DC coefficient is exactly <c>(sample - 128) * 8</c>. The Huffman tables are a flat four-bit DC table and a one-bit end of block.
    /// </summary>
    private static byte[] BuildFourComponentJpeg(byte[] samples, byte? adobeTransform)
    {
        using var ms = new MemoryStream();

        void Marker(byte code, byte[] payload)
        {
            ms.WriteByte(0xFF);
            ms.WriteByte(code);
            int length = payload.Length + 2;
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
            ms.Write(payload);
        }

        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);

        if (adobeTransform is { } transform)
        {
            Marker(0xEE, [.. "Adobe"u8.ToArray(), 0, 100, 0, 0, 0, 0, transform]);
        }

        var dqt = new byte[65];
        Array.Fill(dqt, (byte)1, 1, 64);
        Marker(0xDB, dqt);

        // SOF0: 8-bit, 8x8, four components with 1x1 sampling and quantization table 0.
        Marker(0xC0, [8, 0, 8, 0, 8, 4, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0]);

        // DHT: class 0 (DC) id 0 with twelve four-bit codes for categories 0..11 (code = category); class 1 (AC) id 0 with a single one-bit
        // code for end of block (0x00).
        Marker(0xC4, [0x00, 0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        Marker(0xC4, [0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00]);

        // SOS: four components, all using DC table 0 and AC table 0; spectral selection 0..63, no successive approximation.
        Marker(0xDA, [4, 1, 0x00, 2, 0x00, 3, 0x00, 4, 0x00, 0, 63, 0]);

        var bits = new List<bool>();
        foreach (byte sample in samples)
        {
            int dc = (sample - 128) * 8;
            int category = 0;
            for (int magnitude = Math.Abs(dc); magnitude != 0; magnitude >>= 1)
            {
                category++;
            }

            for (int bit = 3; bit >= 0; bit--)
            {
                bits.Add(((category >> bit) & 1) != 0);
            }

            int value = dc >= 0 ? dc : dc + (1 << category) - 1;
            for (int bit = category - 1; bit >= 0; bit--)
            {
                bits.Add(((value >> bit) & 1) != 0);
            }

            bits.Add(false); // end of block
        }

        while (bits.Count % 8 != 0)
        {
            bits.Add(true);
        }

        for (int i = 0; i < bits.Count; i += 8)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
            {
                b = (b << 1) | (bits[i + j] ? 1 : 0);
            }

            ms.WriteByte((byte)b);
            if (b == 0xFF)
            {
                ms.WriteByte(0);
            }
        }

        ms.WriteByte(0xFF);
        ms.WriteByte(0xD9);
        return ms.ToArray();
    }
}
