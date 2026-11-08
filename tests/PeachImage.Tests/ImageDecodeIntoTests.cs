using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Png;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests;

/// <summary>Decoding into a caller-owned buffer has to produce exactly the pixels <see cref="Image.Load(ReadOnlySpan{byte}, DecoderOptions?)"/> does.</summary>
public class ImageDecodeIntoTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void DecodeInto_MatchesLoad(string format)
    {
        byte[] file = Encode(format, 53, 31);
        using var expected = Image.Load(file);

        byte[] buffer = new byte[expected.GetPixelSpan().Length];
        var info = Image.DecodeInto(file, buffer, out _);

        Assert.Equal(expected.Width, info.Width);
        Assert.Equal(expected.Height, info.Height);
        Assert.Equal(expected.PixelFormat, info.PixelFormat);
        Assert.True(expected.GetPixelSpan().SequenceEqual(buffer));
    }

    public static TheoryData<JpegChromaSubsampling, bool, bool, int, int, int, bool> JpegCases() => new()
    {
        { JpegChromaSubsampling.Yuv420, true, false, 200, 301, 0, false },
        { JpegChromaSubsampling.Yuv420, true, false, 200, 301, 13, false },
        { JpegChromaSubsampling.Yuv444, true, false, 64, 129, 5, false },
        { JpegChromaSubsampling.Yuv422, true, false, 90, 71, 0, false },
        { JpegChromaSubsampling.Yuv411, true, false, 130, 50, 0, false },
        { JpegChromaSubsampling.Yuv420, false, false, 150, 203, 0, false },
        { JpegChromaSubsampling.Yuv420, false, false, 150, 203, 9, false },
        { JpegChromaSubsampling.Yuv420, true, true, 64, 64, 0, false },
        { JpegChromaSubsampling.Yuv420, true, false, 1, 1, 0, false },
        { JpegChromaSubsampling.Yuv420, true, false, 130, 77, 0, true },
    };

    [Theory]
    [MemberData(nameof(JpegCases))]
    public void Jpeg_DecodesIntoTheCallersBuffer_ExactlyAsLoadDoes(JpegChromaSubsampling subsampling, bool color, bool progressive, int width, int height, int padding, bool fastUpsampling)
    {
        byte[] file = EncodeJpeg(color, width, height, subsampling, progressive);
        DecoderOptions options = new JpegDecoderOptions { FastUpsampling = fastUpsampling };
        using var expected = Image.Load(file, options);

        int rowBytes = expected.Width * expected.PixelFormat.GetBytesPerPixel();
        int stride = rowBytes + padding;
        byte[] buffer = new byte[(stride * (expected.Height - 1)) + rowBytes];
        Array.Fill(buffer, (byte)0xAB);
        var info = Image.DecodeInto(file, buffer, out var metadata, padding == 0 ? 0 : stride, options);

        Assert.Equal(expected.PixelFormat, info.PixelFormat);
        Assert.Equal(expected.Metadata.Profiles.Count, metadata.Profiles.Count);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(buffer.AsSpan(y * stride, rowBytes)), $"row {y}");
            if (y < expected.Height - 1)
            {
                Assert.All(buffer.AsSpan((y * stride) + rowBytes, padding).ToArray(), b => Assert.Equal(0xAB, b));
            }
        }
    }

    public static TheoryData<PixelFormat, bool, int> PngCases() => new()
    {
        { PixelFormat.Rgb24, false, 0 },
        { PixelFormat.Rgb24, true, 0 },
        { PixelFormat.Rgb24, false, 11 },
        { PixelFormat.Rgba32, false, 0 },
        { PixelFormat.Rgba32, true, 6 },
        { PixelFormat.Gray8, false, 0 },
        { PixelFormat.Gray8, true, 3 },
        { PixelFormat.Gray16, false, 0 },
        { PixelFormat.Rgb48, false, 5 },
        { PixelFormat.Rgba64, true, 0 },
    };

    [Theory]
    [MemberData(nameof(PngCases))]
    public void Png_DecodesIntoTheCallersBuffer_ExactlyAsLoadDoes(PixelFormat format, bool interlace, int padding)
    {
        using var source = Image.Create(37, 23, format);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 13) ^ (i >> 2));
        }

        using var encoded = new MemoryStream();
        source.Save(encoded, "png", new PngEncoderOptions { Interlace = interlace });
        byte[] file = encoded.ToArray();

        using var expected = Image.Load(file);
        int rowBytes = expected.Width * expected.PixelFormat.GetBytesPerPixel();
        int stride = rowBytes + padding;
        byte[] buffer = new byte[(stride * (expected.Height - 1)) + rowBytes];
        Array.Fill(buffer, (byte)0xAB);
        var info = Image.DecodeInto(file, buffer, out _, padding == 0 ? 0 : stride);

        Assert.Equal(expected.PixelFormat, info.PixelFormat);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(buffer.AsSpan(y * stride, rowBytes)), $"row {y}");
            if (y < expected.Height - 1)
            {
                Assert.All(buffer.AsSpan((y * stride) + rowBytes, padding).ToArray(), b => Assert.Equal(0xAB, b));
            }
        }
    }

    public static TheoryData<bool, bool, int, int, int> WebpCases() => new()
    {
        { true, false, 61, 37, 0 },
        { true, false, 61, 37, 9 },
        { true, true, 48, 33, 0 },
        { true, true, 48, 33, 5 },
        { false, false, 61, 37, 0 },
        { false, false, 61, 37, 7 },
        { false, true, 40, 29, 0 },
        { false, true, 40, 29, 3 },
        { true, false, 1, 1, 0 },
        { false, false, 1, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(WebpCases))]
    public void Webp_DecodesIntoTheCallersBuffer_ExactlyAsLoadDoes(bool lossy, bool alpha, int width, int height, int padding)
    {
        var format = alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        using var source = Image.Create(width, height, format);
        var pixels = source.GetPixelSpan();
        var random = new Random((width * 17) + height);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp(((i / 3) % 241) + random.Next(-20, 21), 0, 255);
        }

        using var encoded = new MemoryStream();
        source.Save(encoded, "webp", new WebpEncoderOptions { Lossless = !lossy });
        byte[] file = encoded.ToArray();

        using var expected = Image.Load(file);
        int rowBytes = expected.Width * expected.PixelFormat.GetBytesPerPixel();
        int stride = rowBytes + padding;
        byte[] buffer = new byte[(stride * (expected.Height - 1)) + rowBytes];
        Array.Fill(buffer, (byte)0xAB);
        var info = Image.DecodeInto(file, buffer, out _, padding == 0 ? 0 : stride);

        Assert.Equal(expected.PixelFormat, info.PixelFormat);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(buffer.AsSpan(y * stride, rowBytes)), $"row {y}");
            if (y < expected.Height - 1)
            {
                Assert.All(buffer.AsSpan((y * stride) + rowBytes, padding).ToArray(), b => Assert.Equal(0xAB, b));
            }
        }
    }

    [Fact]
    public void RowStride_PadsRowsAndLeavesPaddingUntouched()
    {
        byte[] file = Encode("png", 10, 6);
        using var expected = Image.Load(file);
        int rowBytes = expected.Width * expected.PixelFormat.GetBytesPerPixel();
        int stride = rowBytes + 7;

        int required = Image.Identify(new MemoryStream(file)).GetRequiredBufferSize(rowStride: stride);
        Assert.Equal((stride * (expected.Height - 1)) + rowBytes, required);

        byte[] buffer = new byte[required];
        Array.Fill(buffer, (byte)0xAB);
        Image.DecodeInto(file, buffer, out _, stride);

        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(buffer.AsSpan(y * stride, rowBytes)));
            if (y < expected.Height - 1)
            {
                Assert.All(buffer.AsSpan((y * stride) + rowBytes, 7).ToArray(), b => Assert.Equal(0xAB, b));
            }
        }
    }

    [Fact]
    public void TargetPixelFormat_SetsTheDestinationLayout()
    {
        byte[] file = Encode("jpeg", 20, 12);
        var options = new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 };
        using var expected = Image.Load(file, options);

        var identified = Image.Identify(new MemoryStream(file));
        Assert.Equal(20 * 12 * 4, identified.GetRequiredBufferSize(options));

        byte[] buffer = new byte[20 * 12 * 4];
        var info = Image.DecodeInto(file, buffer, out _, options: options);
        Assert.Equal(PixelFormat.Rgba32, info.PixelFormat);
        Assert.True(expected.GetPixelSpan().SequenceEqual(buffer));
    }

    [Fact]
    public void ScaleDenominator_ShrinksTheRequiredSize()
    {
        byte[] file = Encode("jpeg", 64, 48);
        var options = new DecoderOptions { ScaleDenominator = 4 };
        using var expected = Image.Load(file, options);

        var identified = Image.Identify(new MemoryStream(file));
        int required = identified.GetRequiredBufferSize(options);
        Assert.Equal(expected.GetPixelSpan().Length, required);

        byte[] buffer = new byte[required];
        var info = Image.DecodeInto(file, buffer, out _, options: options);
        Assert.Equal((expected.Width, expected.Height), (info.Width, info.Height));
        Assert.True(expected.GetPixelSpan().SequenceEqual(buffer));
    }

    [Fact]
    public void TooSmallDestination_ThrowsNamingTheRequiredSize_AndTryReturnsFalse()
    {
        byte[] file = Encode("png", 16, 16);
        int required = Image.Identify(new MemoryStream(file)).GetRequiredBufferSize();
        byte[] buffer = new byte[required - 1];

        var exception = Assert.Throws<ArgumentException>(() => Image.DecodeInto(file, buffer, out _));
        Assert.Contains(required.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);

        using var decoder = new ImageDecoder();
        Assert.False(decoder.TryDecodeInto(file, buffer, out var info, out _));
        Assert.Equal(required, info.GetRequiredBufferSize());
        Assert.True(decoder.TryDecodeInto(file, new byte[required], out _, out _));
    }

    [Fact]
    public void StrideSmallerThanARow_Throws()
    {
        byte[] file = Encode("png", 16, 16);
        byte[] buffer = new byte[16 * 16 * 3];
        Assert.Throws<ArgumentException>(() => Image.DecodeInto(file, buffer, out _, rowStride: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Image.DecodeInto(file, buffer, out _, rowStride: -1));
    }

    [Fact]
    public void SkipMetadata_ReturnsEmptyMetadata()
    {
        byte[] file = Encode("png", 8, 8);
        byte[] buffer = new byte[8 * 8 * 3];
        Image.DecodeInto(file, buffer, out var metadata, options: new DecoderOptions { SkipMetadata = true });
        Assert.Empty(metadata.Profiles);
    }

    [Fact]
    public void Stream_DecodesFromItsCurrentPosition_AndDisposedDecoderThrows()
    {
        byte[] file = Encode("png", 9, 5);
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3]);
        stream.Write(file);
        stream.Position = 3;

        var decoder = new ImageDecoder();
        byte[] buffer = new byte[9 * 5 * 3];
        var info = decoder.DecodeInto(stream, buffer, out _);
        Assert.Equal(9, info.Width);

        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.DecodeInto(file, buffer, out _));
    }

    private static byte[] EncodeJpeg(bool color, int width, int height, JpegChromaSubsampling subsampling, bool progressive)
    {
        var random = new Random((width * 31) + height);
        int channels = color ? 3 : 1;
        using var image = Image.Create(width, height, color ? PixelFormat.Rgb24 : PixelFormat.Gray8);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp(((i / channels) % 251) + random.Next(-25, 26), 0, 255);
        }

        using var output = new MemoryStream();
        image.Save(output, "jpeg", new JpegEncoderOptions { Quality = 85, Subsampling = subsampling, Progressive = progressive });
        return output.ToArray();
    }

    private static byte[] Encode(string format, int width, int height)
    {
        using var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 7) ^ (i >> 3));
        }

        using var output = new MemoryStream();
        image.Save(output, format);
        return output.ToArray();
    }
}
