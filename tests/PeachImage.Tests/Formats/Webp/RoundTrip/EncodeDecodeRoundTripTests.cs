using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 48)]
    [InlineData(5, 3)] // Non-power-of-two size exercises predictor/tile boundary math.
    [InlineData(1, 1)]
    [InlineData(17, 31)]
    public void Rgb24Gradient_RoundTrips_Exactly(int width, int height)
    {
        using var source = CreateGradientImage(width, height);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Rgba32Image_RoundTrips_Exactly_IncludingAlpha()
    {
        using var source = CreateRgbaImage(40, 24, alphaGradient: true);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Rgba32Image_FullyOpaque_RoundTrips_Exactly_ViaAutoDowngrade()
    {
        using var source = CreateRgbaImage(20, 16, alphaGradient: false);

        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions());

        ms.Position = 0;
        using var decoded = WebpDecoder.Decode(ms, new WebpDecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Gray8Image_RoundTrips_Exactly()
    {
        using var source = CreateGrayscaleImage(32, 32);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions(), targetPixelFormat: PixelFormat.Gray8);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void FewColorImage_RoundTrips_Exactly_ViaColorIndexingTransform()
    {
        using var source = CreateFewColorImage(48, 40);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void ManyColorImage_RoundTrips_Exactly_ViaPredictorTransform()
    {
        using var source = CreateNoisyImage(96, 64, seed: 12345);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(WebpCompressionLevel.Fastest)]
    [InlineData(WebpCompressionLevel.Default)]
    [InlineData(WebpCompressionLevel.SmallestSize)]
    public void NoisyImage_RoundTrips_Exactly_AtEveryCompressionLevel(WebpCompressionLevel level)
    {
        using var source = CreateNoisyImage(40, 32, seed: 777);

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions { CompressionLevel = level });

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void IccProfile_RoundTrips_ViaExtendedContainer()
    {
        using var source = CreateGradientImage(8, 8);
        byte[] iccBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = iccBytes });

        using var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        var iccProfile = Assert.Single(decoded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(iccBytes, iccProfile.Data);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Gray16Image_Encode_Throws()
    {
        using var source = Image.Create(4, 4, PixelFormat.Gray16);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Rgb48Image_Encode_Throws()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb48);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Rgba64Image_Encode_Throws()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgba64);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Cmyk32Image_Encode_Throws()
    {
        using var source = Image.Create(4, 4, PixelFormat.Cmyk32);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    private static Image EncodeThenDecode(Image source, WebpEncoderOptions encoderOptions, PixelFormat? targetPixelFormat = null)
    {
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, encoderOptions);

        ms.Position = 0;
        var decoderOptions = targetPixelFormat is { } target ? new WebpDecoderOptions { TargetPixelFormat = target } : null;
        return WebpDecoder.Decode(ms, decoderOptions);
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

    private static Image CreateRgbaImage(int width, int height, bool alphaGradient)
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
                row[(x * 4) + 3] = alphaGradient ? (byte)((x * 255) / Math.Max(1, width - 1)) : (byte)255;
            }
        }

        return image;
    }

    private static Image CreateFewColorImage(int width, int height)
    {
        (byte R, byte G, byte B)[] palette =
        [
            (0, 0, 0),
            (255, 255, 255),
            (255, 0, 0),
            (0, 255, 0),
            (0, 0, 255),
        ];

        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                var color = palette[(x + y) % palette.Length];
                row[(x * 3) + 0] = color.R;
                row[(x * 3) + 1] = color.G;
                row[(x * 3) + 2] = color.B;
            }
        }

        return image;
    }

    private static Image CreateNoisyImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            random.NextBytes(row);
        }

        return image;
    }
}
