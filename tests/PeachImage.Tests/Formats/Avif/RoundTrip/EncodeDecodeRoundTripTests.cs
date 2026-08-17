using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.RoundTrip;

/// <summary>
/// End-to-end round-trip tests through the fully public API (<see cref="Image.Save(Stream, string, EncoderOptions?)"/>
/// / <see cref="Image.Load(Stream, DecoderOptions?)"/>), exercising the real container writer and the real,
/// unmodified decoder together. v1 is lossy, so these assert PSNR thresholds rather than exact pixel
/// equality -- placeholders calibrated against this encoder's actual current output, to be revisited as the
/// encoder improves (mirroring how WebP's own round-trip tolerances were set from measured worst-case
/// differences, not guessed up front).
/// </summary>
public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(37, 29)] // non-multiple-of-64, exercises the encoder's edge-replication padding
    [InlineData(5, 3)]
    [InlineData(1, 1)]
    public void Rgb24SolidColor_RoundTrips_ViaPublicApi(int width, int height)
    {
        using var source = CreateSolidColorImage(width, height, 180, 90, 40);

        using var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 80 });

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        AssertPsnrAtLeast(source, decoded, minPsnrDb: 28.0);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(50, 40)]
    public void Rgb24Gradient_RoundTrips_ViaPublicApi(int width, int height)
    {
        using var source = CreateGradientImage(width, height);

        using var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 90 });

        AssertPsnrAtLeast(source, decoded, minPsnrDb: 20.0);
    }

    [Fact]
    public void Gray8Image_RoundTrips_ViaPublicApi()
    {
        using var source = CreateGrayscaleImage(48, 32);

        using var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 85 });

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        AssertPsnrAtLeast(source, decoded, minPsnrDb: 25.0);
    }

    [Fact]
    public void Rgba32Image_FullyOpaque_RoundTrips_ViaAutoDowngrade()
    {
        using var source = Image.Create(24, 24, PixelFormat.Rgba32);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < 24 * 24; i++)
        {
            pixels[(i * 4) + 0] = 200;
            pixels[(i * 4) + 1] = 100;
            pixels[(i * 4) + 2] = 50;
            pixels[(i * 4) + 3] = 255;
        }

        using var ms = new MemoryStream();
        source.Save(ms, "avif", new AvifEncoderOptions { Quality = 90 });

        ms.Position = 0;
        using var decoded = Image.Load(ms);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
    }

    [Fact]
    public void Rgba32Image_NonOpaque_Throws()
    {
        using var source = Image.Create(8, 8, PixelFormat.Rgba32);
        var pixels = source.GetPixelSpan();
        pixels[3] = 128; // one non-opaque pixel

        using var ms = new MemoryStream();
        Assert.Throws<AvifUnsupportedFeatureException>(() => source.Save(ms, "avif", new AvifEncoderOptions()));
    }

    [Theory]
    [InlineData(PixelFormat.Gray16)]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.Rgba64)]
    [InlineData(PixelFormat.Cmyk32)]
    public void UnsupportedPixelFormat_Throws(PixelFormat format)
    {
        using var source = Image.Create(8, 8, format);
        using var ms = new MemoryStream();
        Assert.Throws<AvifEncodingException>(() => source.Save(ms, "avif", new AvifEncoderOptions()));
    }

    [Fact]
    public void DifferentQualityLevels_ProduceDifferentFileSizes()
    {
        using var source = CreateGradientImage(64, 64);

        using var highQualityStream = new MemoryStream();
        source.Save(highQualityStream, "avif", new AvifEncoderOptions { Quality = 95 });

        using var lowQualityStream = new MemoryStream();
        source.Save(lowQualityStream, "avif", new AvifEncoderOptions { Quality = 5 });

        Assert.NotEqual(highQualityStream.Length, lowQualityStream.Length);
    }

    [Fact]
    public void Identify_ReportsCorrectDimensionsWithoutFullDecode()
    {
        using var source = CreateGradientImage(40, 30);
        using var ms = new MemoryStream();
        source.Save(ms, "avif", new AvifEncoderOptions());

        ms.Position = 0;
        var info = Image.Identify(ms);

        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal("avif", info.FormatName);
    }

    private static Image EncodeThenDecode(Image source, AvifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        source.Save(ms, "avif", options);
        ms.Position = 0;
        return Image.Load(ms);
    }

    private static Image CreateSolidColorImage(int width, int height, byte r, byte g, byte b)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            pixels[(i * 3) + 0] = r;
            pixels[(i * 3) + 1] = g;
            pixels[(i * 3) + 2] = b;
        }

        return image;
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                pixels[idx + 0] = (byte)(width <= 1 ? 0 : col * 255 / (width - 1));
                pixels[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                pixels[idx + 2] = 128;
            }
        }

        return image;
    }

    private static Image CreateGrayscaleImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Gray8);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 5) % 256);
        }

        return image;
    }

    private static void AssertPsnrAtLeast(Image source, Image decoded, double minPsnrDb)
    {
        var src = source.GetPixelSpan();
        var dst = decoded.GetPixelSpan();
        Assert.Equal(src.Length, dst.Length);

        long sumSquaredError = 0;
        for (int i = 0; i < src.Length; i++)
        {
            int diff = src[i] - dst[i];
            sumSquaredError += diff * diff;
        }

        double mse = sumSquaredError / (double)src.Length;
        double psnr = mse <= 0 ? 100.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);

        Assert.True(psnr >= minPsnrDb, $"PSNR {psnr:F2} dB below required {minPsnrDb} dB (MSE {mse:F2}).");
    }
}
