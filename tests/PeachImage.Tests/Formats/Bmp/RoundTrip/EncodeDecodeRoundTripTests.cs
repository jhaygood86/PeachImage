using PeachImage.Formats.Bmp;

namespace PeachImage.Tests.Formats.Bmp.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 48)]
    [InlineData(5, 3)] // Non-multiple-of-4 width exercises 24bpp row padding math.
    [InlineData(1, 1)]
    public void Rgb24Gradient_RoundTrips_Exactly(int width, int height)
    {
        var source = CreateGradientImage(width, height);

        var decoded = EncodeThenDecode(source, new BmpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(32, 32, false)]
    [InlineData(32, 32, true)]
    [InlineData(5, 3, false)] // Non-multiple-of-4 width exercises 8bpp row padding math.
    public void Gray8Image_RoundTrips_Exactly(int width, int height, bool useRle)
    {
        var source = CreateGrayscaleImage(width, height);

        var decoded = EncodeThenDecode(source, new BmpEncoderOptions { UseRunLengthEncoding = useRle }, targetPixelFormat: PixelFormat.Gray8);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Rgba32Image_RoundTrips_Exactly_IncludingAlpha()
    {
        var source = CreateRgbaImage(40, 24);

        var decoded = EncodeThenDecode(source, new BmpEncoderOptions());

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Cmyk32Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Cmyk32);
        using var ms = new MemoryStream();

        Assert.Throws<BmpEncodingException>(() => BmpEncoder.Encode(source, ms));
    }

    private static Image EncodeThenDecode(Image source, BmpEncoderOptions encoderOptions, PixelFormat? targetPixelFormat = null)
    {
        using var ms = new MemoryStream();
        BmpEncoder.Encode(source, ms, encoderOptions);

        ms.Position = 0;
        var decoderOptions = targetPixelFormat is { } target ? new BmpDecoderOptions { TargetPixelFormat = target } : null;
        return BmpDecoder.Decode(ms, decoderOptions);
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
                row[(x * 4) + 3] = (byte)((x * 255) / Math.Max(1, width - 1)); // Alpha gradient — must round-trip exactly.
            }
        }

        return image;
    }
}
