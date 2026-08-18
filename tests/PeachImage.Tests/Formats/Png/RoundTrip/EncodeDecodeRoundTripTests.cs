using System.Runtime.InteropServices;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 48, false)]
    [InlineData(64, 48, true)]
    [InlineData(5, 3, false)] // Non-multiple-of-8 width exercises 1/2/4-bit-style row-tail handling for other tests; here just an odd-size sanity check.
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(4, 4, true)] // Small enough that some Adam7 passes contribute zero pixels.
    public void Rgb24Gradient_RoundTrips_Exactly(int width, int height, bool interlace)
    {
        var source = CreateGradientImage(width, height);

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(32, 32, false)]
    [InlineData(32, 32, true)]
    [InlineData(5, 3, false)]
    public void Gray8Image_RoundTrips_Exactly(int width, int height, bool interlace)
    {
        var source = CreateGrayscaleImage(width, height);

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rgba32Image_RoundTrips_Exactly_IncludingAlpha(bool interlace)
    {
        var source = CreateRgbaImage(40, 24);

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gray16Image_RoundTrips_Exactly(bool interlace)
    {
        var source = Image.Create(17, 11, PixelFormat.Gray16);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(i * 977);
        }

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Gray16, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rgb48Image_RoundTrips_Exactly(bool interlace)
    {
        var source = Image.Create(13, 9, PixelFormat.Rgb48);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(i * 6151);
        }

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Rgb48, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rgba64Image_RoundTrips_Exactly(bool interlace)
    {
        var source = Image.Create(11, 15, PixelFormat.Rgba64);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(i * 3079);
        }

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { Interlace = interlace });

        Assert.Equal(PixelFormat.Rgba64, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void RgbImage_WithTransparentColor_RoundTrips_ToRgba32WithMatchingAlphaHole()
    {
        const int width = 6;
        const int height = 4;
        var source = Image.Create(width, height, PixelFormat.Rgb24);
        var span = source.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            // A distinct, deterministic non-key color everywhere except one marked pixel.
            span[(i * 3) + 0] = 10;
            span[(i * 3) + 1] = 20;
            span[(i * 3) + 2] = 30;
        }

        int keyPixelIndex = 7;
        span[(keyPixelIndex * 3) + 0] = 200;
        span[(keyPixelIndex * 3) + 1] = 100;
        span[(keyPixelIndex * 3) + 2] = 50;

        var decoded = EncodeThenDecode(source, new PngEncoderOptions { TransparentColor = (200, 100, 50) });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        var decodedSpan = decoded.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            byte expectedAlpha = i == keyPixelIndex ? (byte)0 : (byte)255;
            Assert.Equal(expectedAlpha, decodedSpan[(i * 4) + 3]);
            Assert.Equal(span[(i * 3) + 0], decodedSpan[(i * 4) + 0]);
            Assert.Equal(span[(i * 3) + 1], decodedSpan[(i * 4) + 1]);
            Assert.Equal(span[(i * 3) + 2], decodedSpan[(i * 4) + 2]);
        }
    }

    [Fact]
    public void PhysicalResolution_RoundTrips()
    {
        var source = CreateGradientImage(8, 8);
        source.Metadata.HorizontalResolution = 2835; // 72 DPI in pixels-per-meter
        source.Metadata.VerticalResolution = 2835;

        var decoded = EncodeThenDecode(source, new PngEncoderOptions());

        Assert.Equal(2835, decoded.Metadata.HorizontalResolution);
        Assert.Equal(2835, decoded.Metadata.VerticalResolution);
    }

    [Fact]
    public void TextEntry_RoundTrips()
    {
        var source = CreateGradientImage(4, 4);
        source.Metadata.Profiles.Add(new RawMetadataProfile
        {
            Kind = MetadataProfileKind.Text,
            Data = System.Text.Encoding.UTF8.GetBytes("Title\0\0\0Héllo wörld"),
        });

        var decoded = EncodeThenDecode(source, new PngEncoderOptions());

        var textProfile = decoded.Metadata.Profiles.Single(p => p.Kind == MetadataProfileKind.Text);
        Assert.True(PngTextEntry.TryParse(textProfile, out var entry));
        Assert.Equal("Title", entry!.Keyword);
        Assert.Equal("Héllo wörld", entry.Text);
    }

    [Fact]
    public void Cmyk32Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Cmyk32);
        using var ms = new MemoryStream();

        Assert.Throws<PngEncodingException>(() => PngEncoder.Encode(source, ms));
    }

    private static Image EncodeThenDecode(Image source, PngEncoderOptions encoderOptions)
    {
        using var ms = new MemoryStream();
        PngEncoder.Encode(source, ms, encoderOptions);

        ms.Position = 0;
        return PngDecoder.Decode(ms);
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }

    private static Image CreateGrayscaleImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Gray8);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)((x * 7) + (y * 13));
            }
        }

        return image;
    }

    private static Image CreateRgbaImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 4) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 4) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 4) + 2] = (byte)((x + y) % 256);
                row[(x * 4) + 3] = (byte)((x * 255) / Math.Max(1, width - 1));
            }
        }

        return image;
    }
}
