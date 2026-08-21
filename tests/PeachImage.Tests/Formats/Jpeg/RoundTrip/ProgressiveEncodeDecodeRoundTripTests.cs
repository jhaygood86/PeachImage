using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.RoundTrip;

public class ProgressiveEncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(JpegChromaSubsampling.Yuv444)]
    [InlineData(JpegChromaSubsampling.Yuv422)]
    [InlineData(JpegChromaSubsampling.Yuv420)]
    [InlineData(JpegChromaSubsampling.Yuv411)]
    public void RgbGradient_RoundTrips_WithinPsnrThreshold_AtEachSubsampling(JpegChromaSubsampling subsampling)
    {
        var source = CreateGradientImage(64, 48);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling, Progressive = true });

        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);

        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low for {subsampling}: {psnr:F2} dB");
    }

    [Theory]
    [InlineData(JpegChromaSubsampling.Yuv420, 13, 11)]
    [InlineData(JpegChromaSubsampling.Yuv420, 65, 49)]
    [InlineData(JpegChromaSubsampling.Yuv422, 20, 13)]
    [InlineData(JpegChromaSubsampling.Yuv444, 17, 9)]
    [InlineData(JpegChromaSubsampling.Yuv411, 33, 15)]
    public void RgbGradient_RoundTrips_WithinPsnrThreshold_AtNonMultipleOf8Dimensions(JpegChromaSubsampling subsampling, int width, int height)
    {
        // Non-multiple-of-8 dimensions exercise ComponentPlan.ActualBlocksWide/High directly, since that's
        // exactly where progressive's non-interleaved AC scans stop short of the MCU-padded block grid.
        var source = CreateGradientImage(width, height);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling, Progressive = true });

        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);

        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low for {subsampling} at {width}x{height}: {psnr:F2} dB");
    }

    [Fact]
    public void GrayscaleImage_RoundTrips_WithinPsnrThreshold()
    {
        var source = CreateGrayscaleImage(32, 32);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 95, Progressive = true });

        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low: {psnr:F2} dB");
    }

    [Fact]
    public void RestartIntervals_DecodeIdenticallyToWithoutRestarts()
    {
        var source = CreateGradientImage(80, 64);

        using var withoutRestarts = new MemoryStream();
        JpegEncoder.Encode(source, withoutRestarts, new JpegEncoderOptions { Quality = 90, Progressive = true, RestartInterval = 0 });

        using var withRestarts = new MemoryStream();
        JpegEncoder.Encode(source, withRestarts, new JpegEncoderOptions { Quality = 90, Progressive = true, RestartInterval = 2 });

        withoutRestarts.Position = 0;
        withRestarts.Position = 0;

        var decodedWithout = JpegDecoder.Decode(withoutRestarts);
        var decodedWith = JpegDecoder.Decode(withRestarts);

        Assert.True(decodedWithout.GetPixelSpan().SequenceEqual(decodedWith.GetPixelSpan()));
    }

    [Fact]
    public void BaselineAndProgressive_ProduceNearIdenticalPixels()
    {
        var source = CreateGradientImage(96, 72);

        using var baselineStream = new MemoryStream();
        JpegEncoder.Encode(source, baselineStream, new JpegEncoderOptions { Quality = 85, Progressive = false });

        using var progressiveStream = new MemoryStream();
        JpegEncoder.Encode(source, progressiveStream, new JpegEncoderOptions { Quality = 85, Progressive = true });

        baselineStream.Position = 0;
        progressiveStream.Position = 0;

        var decodedBaseline = JpegDecoder.Decode(baselineStream);
        var decodedProgressive = JpegDecoder.Decode(progressiveStream);

        double psnr = ComputePsnr(decodedBaseline.GetPixelSpan(), decodedProgressive.GetPixelSpan());
        Assert.True(psnr > 45.0, $"Baseline and progressive outputs diverged more than expected: {psnr:F2} dB");
    }

    [Fact]
    public void LargeFlatImage_RoundTrips_WithinPsnrThreshold()
    {
        // Large, near-solid image: most AC coefficients quantize to zero, so this exercises long
        // in-scan EOB runs (and, at the block-count involved here, exercises the run-length accumulation
        // logic well beyond a handful of blocks) for both AC-first and AC-refine scans.
        var source = CreateFlatImage(512, 512);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Progressive = true });

        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);

        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low for large flat image: {psnr:F2} dB");
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

    private static Image CreateFlatImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 3;

                // A faint gradient (rather than a literally flat fill) keeps the DC scan's differential
                // coding non-degenerate while still driving nearly every AC coefficient to zero.
                pixels[offset] = (byte)(128 + ((x + y) / ((width + height) / 8)));
                pixels[offset + 1] = 128;
                pixels[offset + 2] = 128;
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
