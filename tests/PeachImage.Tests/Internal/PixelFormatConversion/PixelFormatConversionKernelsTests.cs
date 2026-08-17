using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Tests.Internal.PixelFormatConversion;

/// <summary>
/// Verifies every <see cref="PixelFormatConversionKernels"/> entry point against an independent, deliberately
/// naive scalar reference (not the kernel's own tail loop) across pixel counts that straddle the kernels'
/// vector-batch sizes (4/8/16), so both the vectorized bulk path and the scalar tail are exercised.
/// </summary>
public class PixelFormatConversionKernelsTests
{
    private static readonly int[] PixelCounts = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 100, 137];

    [Theory]
    [MemberData(nameof(Counts))]
    public void ExpandRgb24ToRgba32_MatchesReference(int pixelCount)
    {
        var rgb = RandomBytes(pixelCount * 3, 1);
        var actual = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ExpandRgb24ToRgba32(rgb, actual, pixelCount);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            expected[(i * 4) + 0] = rgb[(i * 3) + 0];
            expected[(i * 4) + 1] = rgb[(i * 3) + 1];
            expected[(i * 4) + 2] = rgb[(i * 3) + 2];
            expected[(i * 4) + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void NarrowRgba32ToRgb24_MatchesReference(int pixelCount)
    {
        var rgba = RandomBytes(pixelCount * 4, 2);
        var actual = new byte[pixelCount * 3];
        PixelFormatConversionKernels.NarrowRgba32ToRgb24(rgba, actual, pixelCount);

        var expected = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            expected[(i * 3) + 0] = rgba[(i * 4) + 0];
            expected[(i * 3) + 1] = rgba[(i * 4) + 1];
            expected[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ExpandGray8ToRgb24_MatchesReference(int pixelCount)
    {
        var gray = RandomBytes(pixelCount, 3);
        var actual = new byte[pixelCount * 3];
        PixelFormatConversionKernels.ExpandGray8ToRgb24(gray, actual, pixelCount);

        var expected = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            expected[(i * 3) + 0] = expected[(i * 3) + 1] = expected[(i * 3) + 2] = gray[i];
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ExpandGray8ToRgba32_MatchesReference(int pixelCount)
    {
        var gray = RandomBytes(pixelCount, 4);
        var actual = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ExpandGray8ToRgba32(gray, actual, pixelCount);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            expected[(i * 4) + 0] = expected[(i * 4) + 1] = expected[(i * 4) + 2] = gray[i];
            expected[(i * 4) + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ComputeLumaFromRgb24_MatchesFloatReferenceWithinRoundingTolerance(int pixelCount)
    {
        var rgb = RandomBytes(pixelCount * 3, 5);
        var actual = new byte[pixelCount];
        PixelFormatConversionKernels.ComputeLumaFromRgb24(rgb, actual, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            byte expected = ReferenceLuma(rgb[(i * 3) + 0], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
            Assert.True(Math.Abs(expected - actual[i]) <= 1, $"pixel {i}: expected~{expected}, actual={actual[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ComputeLumaFromRgba32_MatchesFloatReferenceWithinRoundingTolerance(int pixelCount)
    {
        var rgba = RandomBytes(pixelCount * 4, 6);
        var actual = new byte[pixelCount];
        PixelFormatConversionKernels.ComputeLumaFromRgba32(rgba, actual, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            byte expected = ReferenceLuma(rgba[(i * 4) + 0], rgba[(i * 4) + 1], rgba[(i * 4) + 2]);
            Assert.True(Math.Abs(expected - actual[i]) <= 1, $"pixel {i}: expected~{expected}, actual={actual[i]}");
        }
    }

    [Fact]
    public void ComputeLumaFromRgb24_OfPureGray_RecoversExactly()
    {
        // R=G=B for every pixel: any correctly-weighted average must recover the source value exactly,
        // regardless of per-channel rounding — this is the shape every Gray-source round-trip test relies on.
        var random = new Random(7);
        const int pixelCount = 64;
        var rgb = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            byte g = (byte)random.Next(256);
            rgb[(i * 3) + 0] = rgb[(i * 3) + 1] = rgb[(i * 3) + 2] = g;
        }

        var actual = new byte[pixelCount];
        PixelFormatConversionKernels.ComputeLumaFromRgb24(rgb, actual, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            Assert.Equal(rgb[i * 3], actual[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ConvertCmyk32ToRgba32_MatchesReference(int pixelCount)
    {
        var cmyk = RandomBytes(pixelCount * 4, 8);
        var actual = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ConvertCmyk32ToRgba32(cmyk, actual, pixelCount);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            byte c = cmyk[(i * 4) + 0], m = cmyk[(i * 4) + 1], y = cmyk[(i * 4) + 2], k = cmyk[(i * 4) + 3];
            expected[(i * 4) + 0] = (byte)(255 - Math.Min(255, c + k));
            expected[(i * 4) + 1] = (byte)(255 - Math.Min(255, m + k));
            expected[(i * 4) + 2] = (byte)(255 - Math.Min(255, y + k));
            expected[(i * 4) + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertCmyk32ToRgba32_SaturatesAtExtremes()
    {
        byte[] cmyk = [255, 255, 255, 255, 0, 0, 0, 0, 200, 50, 10, 100];
        var actual = new byte[12];
        PixelFormatConversionKernels.ConvertCmyk32ToRgba32(cmyk, actual, 3);

        Assert.Equal<byte>([0, 0, 0, 255], actual[..4]);
        Assert.Equal<byte>([255, 255, 255, 255], actual[4..8]);
        Assert.Equal<byte>([0, 105, 145, 255], actual[8..12]); // 255-min(255,300), 255-min(255,150), 255-min(255,110)
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void WidenBytesToUInt16_MatchesReference(int pixelCount)
    {
        var source = RandomBytes(pixelCount, 9);
        var actual = new ushort[pixelCount];
        PixelFormatConversionKernels.WidenBytesToUInt16(source, actual);

        for (int i = 0; i < pixelCount; i++)
        {
            byte v = source[i];
            Assert.Equal((ushort)((v << 8) | v), actual[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void NarrowUInt16ToBytes_MatchesReference(int pixelCount)
    {
        var source = RandomUInt16s(pixelCount, 10);
        var actual = new byte[pixelCount];
        PixelFormatConversionKernels.NarrowUInt16ToBytes(source, actual);

        for (int i = 0; i < pixelCount; i++)
        {
            Assert.Equal((byte)(source[i] >> 8), actual[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ExpandNarrowGray16ToRgba32_MatchesReference(int pixelCount)
    {
        var gray16 = RandomUInt16s(pixelCount, 11);
        var actual = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ExpandNarrowGray16ToRgba32(gray16, actual, pixelCount);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            byte g = (byte)(gray16[i] >> 8);
            expected[(i * 4) + 0] = expected[(i * 4) + 1] = expected[(i * 4) + 2] = g;
            expected[(i * 4) + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ExpandNarrowRgb48ToRgba32_MatchesReference(int pixelCount)
    {
        var rgb48 = RandomUInt16s(pixelCount * 3, 12);
        var actual = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ExpandNarrowRgb48ToRgba32(rgb48, actual, pixelCount);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            expected[(i * 4) + 0] = (byte)(rgb48[(i * 3) + 0] >> 8);
            expected[(i * 4) + 1] = (byte)(rgb48[(i * 3) + 1] >> 8);
            expected[(i * 4) + 2] = (byte)(rgb48[(i * 3) + 2] >> 8);
            expected[(i * 4) + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }

    public static TheoryData<int> Counts()
    {
        var data = new TheoryData<int>();
        foreach (int count in PixelCounts)
        {
            data.Add(count);
        }

        return data;
    }

    private static byte ReferenceLuma(byte r, byte g, byte b) =>
        (byte)Math.Clamp(Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b), MidpointRounding.AwayFromZero), 0, 255);

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static ushort[] RandomUInt16s(int length, int seed)
    {
        var random = new Random(seed);
        var values = new ushort[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = (ushort)random.Next(65536);
        }

        return values;
    }
}
