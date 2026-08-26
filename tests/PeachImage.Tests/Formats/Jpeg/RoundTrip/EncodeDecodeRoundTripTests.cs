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
        var source = CreateGradientImage(64, 48);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling });

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
        // Dimensions deliberately not multiples of 8 (or of 8*samplingFactor) so the decoder's boundary-block
        // path — the partial last block-row/-column FrameReconstructor.ReconstructComponentPlane handles via
        // a scratch buffer, distinct from its interior-block fast path — actually gets exercised. Every other
        // round-trip test in this file uses exact-multiple-of-8 dimensions and never touches it.
        var source = CreateGradientImage(width, height);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling });

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
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 95 });

        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        double psnr = ComputePsnr(source.GetPixelSpan(), decoded.GetPixelSpan());
        Assert.True(psnr > 30.0, $"PSNR too low: {psnr:F2} dB");
    }

    [Fact]
    public void HighQuality_ProducesHigherPsnrThanLowQuality()
    {
        var source = CreateGradientImage(48, 48);

        double lowQualityPsnr = EncodeDecodeAndMeasurePsnr(source, quality: 20);
        double highQualityPsnr = EncodeDecodeAndMeasurePsnr(source, quality: 95);

        Assert.True(highQualityPsnr > lowQualityPsnr, $"Expected higher PSNR at quality 95 ({highQualityPsnr:F2} dB) than quality 20 ({lowQualityPsnr:F2} dB)");
    }

    [Theory]
    [InlineData(JpegChromaSubsampling.Yuv444)]
    [InlineData(JpegChromaSubsampling.Yuv420)]
    public void OptimizedHuffmanTables_ProduceBitIdenticalPixelsToStandardTables(JpegChromaSubsampling subsampling)
    {
        // Huffman table choice is a lossless entropy-coding detail layered on top of already-quantized
        // coefficients -- it can only ever change the encoded byte count, never the reconstructed pixels.
        var source = CreateGradientImage(64, 48);

        using var standardMs = new MemoryStream();
        JpegEncoder.Encode(source, standardMs, new JpegEncoderOptions { Quality = 85, Subsampling = subsampling, OptimizeHuffmanTables = false });

        using var optimizedMs = new MemoryStream();
        JpegEncoder.Encode(source, optimizedMs, new JpegEncoderOptions { Quality = 85, Subsampling = subsampling, OptimizeHuffmanTables = true });

        standardMs.Position = 0;
        optimizedMs.Position = 0;
        var decodedStandard = JpegDecoder.Decode(standardMs);
        var decodedOptimized = JpegDecoder.Decode(optimizedMs);

        Assert.True(decodedStandard.GetPixelSpan().SequenceEqual(decodedOptimized.GetPixelSpan()));
    }

    [Fact]
    public void OptimizedHuffmanTables_ProduceSmallerFileSize()
    {
        // A noisy/high-frequency source gives the optimizer real room to win: its symbol distribution
        // diverges further from the standard tables' fixed, typical-image tuning than a smooth gradient's does.
        var source = CreateNoisyImage(96, 96);

        using var standardMs = new MemoryStream();
        JpegEncoder.Encode(source, standardMs, new JpegEncoderOptions { Quality = 85, OptimizeHuffmanTables = false });

        using var optimizedMs = new MemoryStream();
        JpegEncoder.Encode(source, optimizedMs, new JpegEncoderOptions { Quality = 85, OptimizeHuffmanTables = true });

        Assert.True(
            optimizedMs.Length <= standardMs.Length,
            $"Expected optimized-table output ({optimizedMs.Length} bytes) to be no larger than standard-table output ({standardMs.Length} bytes).");
    }

    [Fact]
    public void RestartIntervals_DecodeIdenticallyToWithoutRestarts()
    {
        var source = CreateGradientImage(80, 64);

        using var withoutRestarts = new MemoryStream();
        JpegEncoder.Encode(source, withoutRestarts, new JpegEncoderOptions { Quality = 90, RestartInterval = 0 });

        using var withRestarts = new MemoryStream();
        JpegEncoder.Encode(source, withRestarts, new JpegEncoderOptions { Quality = 90, RestartInterval = 2 });

        withoutRestarts.Position = 0;
        withRestarts.Position = 0;

        var decodedWithout = JpegDecoder.Decode(withoutRestarts);
        var decodedWith = JpegDecoder.Decode(withRestarts);

        Assert.True(decodedWithout.GetPixelSpan().SequenceEqual(decodedWith.GetPixelSpan()));
    }

    private static double EncodeDecodeAndMeasurePsnr(Image source, int quality)
    {
        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = quality });
        ms.Position = 0;
        var decoded = JpegDecoder.Decode(ms);
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

    private static Image CreateNoisyImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        var random = new Random(12345);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)random.Next(256);
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
