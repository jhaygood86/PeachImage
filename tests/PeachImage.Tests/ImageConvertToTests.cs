using System.Runtime.InteropServices;

namespace PeachImage.Tests;

public class ImageConvertToTests
{
    private static readonly PixelFormat[] Convertible =
    [
        PixelFormat.Gray8, PixelFormat.Rgb24, PixelFormat.Rgba32,
        PixelFormat.Gray16, PixelFormat.Rgb48, PixelFormat.Rgba64,
        PixelFormat.GrayF32, PixelFormat.RgbF32, PixelFormat.RgbaF32,
    ];

    public static TheoryData<PixelFormat, PixelFormat> AllPairs()
    {
        var data = new TheoryData<PixelFormat, PixelFormat>();
        foreach (var from in Convertible)
        {
            foreach (var to in Convertible)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Fact]
    public void SameFormat_ReturnsTheSameInstance()
    {
        using var image = Image.Create(3, 2, PixelFormat.Rgb24);
        Assert.Same(image, image.ConvertTo(PixelFormat.Rgb24));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPair_ConvertsAGradientWithinQuantization(PixelFormat from, PixelFormat to)
    {
        // Odd sizes exercise the vector tails; more than 64 rows exercises the parallel path.
        const int width = 37;
        const int height = 70;
        using var source = Image.Create(width, height, from);
        Fill(source);

        using var converted = source.ConvertTo(to);
        Assert.Equal(to, converted.PixelFormat);
        Assert.Equal(width, converted.Width);
        Assert.Equal(height, converted.Height);

        // Compare through float RGBA; gray targets compare against luma, colour-to-gray loses chroma by design.
        for (int y = 0; y < height; y += 7)
        {
            for (int x = 0; x < width; x += 5)
            {
                var want = ReadRgba(source, x, y);
                var got = ReadRgba(converted, x, y);
                bool toGray = to is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
                bool fromGray = from is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
                if (toGray && !fromGray)
                {
                    want = (want[0] * 0.299f + want[1] * 0.587f + want[2] * 0.114f, 0, 0, 0).ToArray();
                    Assert.InRange(got[0], want[0] - 0.012f, want[0] + 0.012f);
                    continue;
                }

                int channels = Math.Min(to.GetChannelCount(), from.GetChannelCount());
                float tolerance = (from.GetBytesPerSample() == 1 || to.GetBytesPerSample() == 1) ? 0.004f : 0.0001f;
                for (int c = 0; c < channels; c++)
                {
                    Assert.InRange(got[c], want[c] - tolerance, want[c] + tolerance);
                }

                if (to.HasAlpha() && !from.HasAlpha())
                {
                    Assert.Equal(1f, got[3]);
                }
            }
        }
    }

    [Fact]
    public void FloatToInteger_ClampsHdrAndNegativeValues()
    {
        using var source = Image.Create(4, 1, PixelFormat.RgbF32);
        var samples = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        float[] values = [2.5f, -1f, 0.5f, 1f, 0f, 0.25f, float.NaN, 100f, 0.999f, 0f, 0f, 0f];
        values.CopyTo(samples);

        using var rgb = source.ConvertTo(PixelFormat.Rgb24);
        byte[] expected = [255, 0, 128, 255, 0, 64, 0, 255, 255, 0, 0, 0];
        Assert.Equal(expected, rgb.GetPixelSpan().ToArray());

        using var rgb48 = source.ConvertTo(PixelFormat.Rgb48);
        var wide = MemoryMarshal.Cast<byte, ushort>(rgb48.GetPixelSpan());
        Assert.Equal(65535, wide[0]);
        Assert.Equal(0, wide[1]);
        Assert.Equal(32768, wide[2]);
    }

    [Fact]
    public void EightBitToSixteenBit_ReplicatesTheByte()
    {
        using var source = Image.Create(256, 1, PixelFormat.Gray8);
        for (int i = 0; i < 256; i++)
        {
            source.GetPixelSpan()[i] = (byte)i;
        }

        using var wide = source.ConvertTo(PixelFormat.Gray16);
        var samples = MemoryMarshal.Cast<byte, ushort>(wide.GetPixelSpan());
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(i * 257, samples[i]);
        }

        using var back = wide.ConvertTo(PixelFormat.Gray8);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((byte)i, back.GetPixelSpan()[i]);
        }
    }

    [Fact]
    public void AlphaIsDropped_AndMetadataCarriesOver()
    {
        using var source = Image.Create(2, 1, PixelFormat.Rgba32);
        byte[] pixels = [10, 20, 30, 40, 50, 60, 70, 80];
        pixels.CopyTo(source.GetPixelSpan());
        source.Metadata.HorizontalResolution = 144;
        source.Metadata.VerticalResolution = 72;
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = [1, 2, 3] });
        source.HasAlpha = true;

        using var rgb = source.ConvertTo(PixelFormat.Rgb24);
        Assert.Equal(new byte[] { 10, 20, 30, 50, 60, 70 }, rgb.GetPixelSpan().ToArray());
        Assert.Equal(144, rgb.Metadata.HorizontalResolution);
        Assert.Equal(72, rgb.Metadata.VerticalResolution);
        Assert.Single(rgb.Metadata.Profiles);
        Assert.True(rgb.HasAlpha);
    }

    [Theory]
    [InlineData(PixelFormat.Cmyk32, PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgb24, PixelFormat.Cmyk32)]
    [InlineData(PixelFormat.Ycck32, PixelFormat.Rgba32)]
    public void Cmyk_IsNotSupported(PixelFormat from, PixelFormat to)
    {
        using var image = Image.Create(2, 2, from);
        Assert.Throws<NotSupportedException>(() => image.ConvertTo(to));
    }

    [Fact]
    public void ConvertedFloatImage_CanBeEncoded()
    {
        using var source = Image.Create(8, 8, PixelFormat.RgbaF32);
        Fill(source);
        using var rgba = source.ConvertTo(PixelFormat.Rgba32);
        using var stream = new MemoryStream();
        rgba.Save(stream, "png");
        stream.Position = 0;
        using var decoded = Image.Load(stream);
        Assert.Equal(8, decoded.Width);
    }

    // Deterministic, varied content in whichever format: gray ramp for gray formats, a colour gradient otherwise.
    private static void Fill(Image image)
    {
        var format = image.PixelFormat;
        int channels = format.GetChannelCount();
        for (int y = 0; y < image.Height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < image.Width; x++)
            {
                float r = (x * 7 % 31) / 30f;
                float g = (y * 5 % 29) / 28f;
                float b = ((x + y) * 3 % 23) / 22f;
                float a = 0.5f + (((x * y) % 5) / 10f);
                float[] values = channels == 1 ? [r] : [r, g, b, a];
                for (int c = 0; c < channels; c++)
                {
                    Write(row, (x * channels) + c, format, values[c]);
                }
            }
        }
    }

    private static void Write(Span<byte> row, int index, PixelFormat format, float value)
    {
        switch (format.GetBytesPerSample())
        {
            case 1:
                row[index] = (byte)((value * 255f) + 0.5f);
                break;
            case 2:
                MemoryMarshal.Cast<byte, ushort>(row)[index] = (ushort)((value * 65535f) + 0.5f);
                break;
            default:
                MemoryMarshal.Cast<byte, float>(row)[index] = value;
                break;
        }
    }

    private static float ReadSample(ReadOnlySpan<byte> row, PixelFormat format, int index) => format.GetBytesPerSample() switch
    {
        1 => row[index] / 255f,
        2 => MemoryMarshal.Cast<byte, ushort>(row)[index] / 65535f,
        _ => MemoryMarshal.Cast<byte, float>(row)[index],
    };

    private static float[] ReadRgba(Image image, int x, int y)
    {
        var format = image.PixelFormat;
        int channels = format.GetChannelCount();
        float Read(int index) => ReadSample(image.GetRowSpan(y), format, index);

        float[] result = new float[4];
        if (channels == 1)
        {
            float v = Read(x);
            return [v, v, v, 1f];
        }

        for (int c = 0; c < channels; c++)
        {
            result[c] = Read((x * channels) + c);
        }

        if (channels == 3)
        {
            result[3] = 1f;
        }

        return result;
    }
}

internal static class ConvertToTestExtensions
{
    public static float[] ToArray(this (float, int, int, int) tuple) => [tuple.Item1, tuple.Item2, tuple.Item3, tuple.Item4];
}
