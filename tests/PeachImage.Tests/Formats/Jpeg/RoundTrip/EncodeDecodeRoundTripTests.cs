using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(JpegChromaSubsampling.Yuv444)]
    [InlineData(JpegChromaSubsampling.Yuv422)]
    [InlineData(JpegChromaSubsampling.Yuv420)]
    [InlineData(JpegChromaSubsampling.Yuv411)]
    public void RgbGradient_RoundTrips_WithinPsnrThreshold_AtEachSubsampling(JpegChromaSubsampling subsampling)
    {
        using var source = CreateGradientImage(64, 48);

        using var ms = new MemoryStream();
        var encoder = new JpegEncoder();
        encoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling });

        ms.Position = 0;
        var decoder = new JpegDecoder();
        using var decoded = decoder.Decode(ms);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);

        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low for {subsampling}: {psnr:F2} dB");
    }

    [Fact]
    public void GrayscaleImage_RoundTrips_WithinPsnrThreshold()
    {
        using var source = CreateGrayscaleImage(32, 32);

        using var ms = new MemoryStream();
        var encoder = new JpegEncoder();
        encoder.Encode(source, ms, new JpegEncoderOptions { Quality = 95 });

        ms.Position = 0;
        var decoder = new JpegDecoder();
        using var decoded = decoder.Decode(ms);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low: {psnr:F2} dB");
    }

    [Fact]
    public void HighQuality_ProducesHigherPsnrThanLowQuality()
    {
        using var source = CreateGradientImage(48, 48);

        double lowQualityPsnr = EncodeDecodeAndMeasurePsnr(source, quality: 20);
        double highQualityPsnr = EncodeDecodeAndMeasurePsnr(source, quality: 95);

        Assert.True(highQualityPsnr > lowQualityPsnr, $"Expected higher PSNR at quality 95 ({highQualityPsnr:F2} dB) than quality 20 ({lowQualityPsnr:F2} dB)");
    }

    [Fact]
    public void RestartIntervals_DecodeIdenticallyToWithoutRestarts()
    {
        using var source = CreateGradientImage(80, 64);

        using var withoutRestarts = new MemoryStream();
        new JpegEncoder().Encode(source, withoutRestarts, new JpegEncoderOptions { Quality = 90, RestartInterval = 0 });

        using var withRestarts = new MemoryStream();
        new JpegEncoder().Encode(source, withRestarts, new JpegEncoderOptions { Quality = 90, RestartInterval = 2 });

        withoutRestarts.Position = 0;
        withRestarts.Position = 0;

        using var decodedWithout = new JpegDecoder().Decode(withoutRestarts);
        using var decodedWith = new JpegDecoder().Decode(withRestarts);

        Assert.True(decodedWithout.GetPixelSpan().SequenceEqual(decodedWith.GetPixelSpan()));
    }

    private static double EncodeDecodeAndMeasurePsnr(Image source, int quality)
    {
        using var ms = new MemoryStream();
        new JpegEncoder().Encode(source, ms, new JpegEncoderOptions { Quality = quality });
        ms.Position = 0;
        using var decoded = new JpegDecoder().Decode(ms);
        return ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 3;
                pixels[offset] = (byte)(x * 255 / Math.Max(width - 1, 1));
                pixels[offset + 1] = (byte)(y * 255 / Math.Max(height - 1, 1));
                pixels[offset + 2] = (byte)(((x + y) * 255 / Math.Max(width + height - 2, 1)));
            }
        }

        return image;
    }

    private static Image CreateGrayscaleImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Gray8);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)(((x * 13) + (y * 7)) % 256);
            }
        }

        return image;
    }

    private static double ComputePsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);

        double sumSquaredError = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double diff = a[i] - b[i];
            sumSquaredError += diff * diff;
        }

        double meanSquaredError = sumSquaredError / a.Length;
        if (meanSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        return 10.0 * Math.Log10((255.0 * 255.0) / meanSquaredError);
    }
}
