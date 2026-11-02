using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.Unit;

/// <summary>Scaled (DCT-domain) JPEG decode through <c>ScaleDenominator</c> / <c>TargetSize</c>, and <c>ApplyOrientation</c>.</summary>
public class ScaledDecodeTests
{
    [Fact]
    public void TargetSize_PicksTheLargestReductionThatStillCoversIt_6000x4000To750x500()
    {
        byte[] jpeg = EncodeSmooth(6000, 4000, JpegChromaSubsampling.Yuv420, progressive: false);

        // Buffers come from shared pools, so allocation counts say little here; compare wall time instead. Both decodes run sequentially
        // (a single thread), so the scaled one skipping most of the IDCT, upsampling and colour conversion shows plainly.
        var sequential = new DecoderOptions { MaxDegreeOfParallelism = 1 };
        var scaledOptions = new DecoderOptions { MaxDegreeOfParallelism = 1, TargetSize = (600, 420) };

        using (Image.Load(jpeg, scaledOptions))
        {
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var full = Image.Load(jpeg, sequential))
        {
            Assert.Equal(6000, full.Width);
        }

        var fullTime = watch.Elapsed;

        watch.Restart();
        using var scaled = Image.Load(jpeg, scaledOptions);
        var scaledTime = watch.Elapsed;

        Assert.Equal(750, scaled.Width);
        Assert.Equal(500, scaled.Height);
        Assert.True(scaledTime * 2 < fullTime, $"scaled {scaledTime.TotalMilliseconds:F0} ms vs full {fullTime.TotalMilliseconds:F0} ms");
    }

    [Theory]
    [InlineData(2, false, JpegChromaSubsampling.Yuv420)]
    [InlineData(4, false, JpegChromaSubsampling.Yuv420)]
    [InlineData(8, false, JpegChromaSubsampling.Yuv420)]
    [InlineData(8, true, JpegChromaSubsampling.Yuv420)]
    [InlineData(4, true, JpegChromaSubsampling.Yuv444)]
    public void ScaledDecode_MatchesAFullDecodeResizedToTheSameSize_WithinATolerance(int denominator, bool progressive, JpegChromaSubsampling subsampling)
    {
        byte[] jpeg = EncodeSmooth(1203, 801, subsampling, progressive);

        using var scaled = Image.Load(jpeg, new DecoderOptions { ScaleDenominator = denominator });
        using var full = Image.Load(jpeg);
        using var reference = full.Resize(scaled.Width, scaled.Height, new ResizeOptions { Filter = ResamplingFilter.Box });

        Assert.Equal((1203 + denominator - 1) / denominator, scaled.Width);
        Assert.Equal((801 + denominator - 1) / denominator, scaled.Height);
        Assert.True(MeanAbsoluteDifference(scaled, reference) < 3.0, $"mean difference {MeanAbsoluteDifference(scaled, reference):F2}");
    }

    [Fact]
    public void ScaledDecode_ThenFinalResize_MatchesFullDecodeThenResize_WithinATolerance()
    {
        byte[] jpeg = EncodeSmooth(1600, 1000, JpegChromaSubsampling.Yuv420, progressive: false);

        using var scaled = Image.Load(jpeg, new DecoderOptions { TargetSize = (320, 200) });
        using var viaScaled = scaled.Resize(320, 200);
        using var full = Image.Load(jpeg);
        using var viaFull = full.Resize(320, 200);

        Assert.Equal(400, scaled.Width);
        Assert.True(MeanAbsoluteDifference(viaScaled, viaFull) < 3.0);
    }

    [Fact]
    public void ScaleDenominator_One_IsTheDefaultFullDecode()
    {
        byte[] jpeg = EncodeSmooth(64, 48, JpegChromaSubsampling.Yuv420, progressive: false);

        using var expected = Image.Load(jpeg);
        using var explicitOne = Image.Load(jpeg, new DecoderOptions { ScaleDenominator = 1 });

        Assert.True(expected.GetPixelSpan().SequenceEqual(explicitOne.GetPixelSpan()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(16)]
    public void InvalidScaleDenominator_IsRejected(int denominator)
    {
        byte[] jpeg = EncodeSmooth(16, 16, JpegChromaSubsampling.Yuv420, progressive: false);

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.Load(jpeg, new DecoderOptions { ScaleDenominator = denominator }));
    }

    [Fact]
    public void MaxPixels_IsCheckedAgainstTheDeclaredSize_NotTheScaledSize()
    {
        byte[] jpeg = EncodeSmooth(800, 600, JpegChromaSubsampling.Yuv420, progressive: false);

        Assert.Throws<ImageTooLargeException>(() => Image.Load(jpeg, new DecoderOptions { ScaleDenominator = 8, MaxPixels = 100 * 100 }));

        using var image = Image.Load(jpeg, new DecoderOptions { ScaleDenominator = 8, MaxPixels = 800 * 600 });
        Assert.Equal(100, image.Width);
        Assert.Equal(75, image.Height);
    }

    [Fact]
    public void FormatsWithoutNativeReduction_ComeBackAtTheSameScaledSize()
    {
        using var source = Image.Create(101, 61, PixelFormat.Rgb24);
        new Random(4).NextBytes(source.GetPixelSpan());
        using var ms = new MemoryStream();
        source.Save(ms, "png");

        using var scaled = Image.Load(ms.ToArray(), new DecoderOptions { ScaleDenominator = 4 });

        Assert.Equal(26, scaled.Width);
        Assert.Equal(16, scaled.Height);
    }

    [Theory]
    [InlineData(ImageOrientation.Normal, 160, 100)]
    [InlineData(ImageOrientation.MirrorHorizontal, 160, 100)]
    [InlineData(ImageOrientation.Rotate180, 160, 100)]
    [InlineData(ImageOrientation.MirrorVertical, 160, 100)]
    [InlineData(ImageOrientation.Transpose, 100, 160)]
    [InlineData(ImageOrientation.Rotate90, 100, 160)]
    [InlineData(ImageOrientation.Transverse, 100, 160)]
    [InlineData(ImageOrientation.Rotate270, 100, 160)]
    public void ApplyOrientation_EqualsLoadThenApplyOrientation(ImageOrientation orientation, int expectedWidth, int expectedHeight)
    {
        byte[] jpeg = WithOrientation(EncodeSmooth(160, 100, JpegChromaSubsampling.Yuv420, progressive: false), orientation);

        using var upright = Image.Load(jpeg, new DecoderOptions { ApplyOrientation = true });
        using var stored = Image.Load(jpeg);
        using var expected = stored.ApplyOrientation(orientation);

        Assert.Equal(expectedWidth, upright.Width);
        Assert.Equal(expectedHeight, upright.Height);
        Assert.True(expected.GetPixelSpan().SequenceEqual(upright.GetPixelSpan()));
        Assert.Equal(PixelFormat.Rgb24, upright.PixelFormat);
    }

    [Fact]
    public void ApplyOrientation_WithTargetSize_ComparesTheTargetAgainstTheUprightSize()
    {
        // Stored 1600x1000 with orientation 6 displays as 1000x1600. At 1/8 that is 125x200, which covers 100x100 but not 126x201,
        // so the second target needs 1/4 (250x400). The stored 200x125 would never match the target's orientation.
        byte[] jpeg = WithOrientation(EncodeSmooth(1600, 1000, JpegChromaSubsampling.Yuv420, progressive: false), ImageOrientation.Rotate90);

        using var eighth = Image.Load(jpeg, new DecoderOptions { ApplyOrientation = true, TargetSize = (100, 100) });
        Assert.Equal(125, eighth.Width);
        Assert.Equal(200, eighth.Height);

        using var quarter = Image.Load(jpeg, new DecoderOptions { ApplyOrientation = true, TargetSize = (126, 201) });
        Assert.Equal(250, quarter.Width);
        Assert.Equal(400, quarter.Height);
    }

    private static double MeanAbsoluteDifference(Image a, Image b)
    {
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        Assert.Equal(x.Length, y.Length);
        long total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            total += Math.Abs(x[i] - y[i]);
        }

        return (double)total / x.Length;
    }

    /// <summary>A smooth, low-frequency image, where a DCT-domain reduction and a box resize agree closely.</summary>
    private static byte[] EncodeSmooth(int width, int height, JpegChromaSubsampling subsampling, bool progressive)
    {
        using var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 3;
                pixels[offset] = (byte)(x * 255 / Math.Max(width - 1, 1));
                pixels[offset + 1] = (byte)(y * 255 / Math.Max(height - 1, 1));
                pixels[offset + 2] = (byte)(128 + (int)(60 * Math.Sin((x + y) / 90.0)));
            }
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(image, ms, new JpegEncoderOptions { Quality = 92, Subsampling = subsampling, Progressive = progressive });
        return ms.ToArray();
    }

    private static byte[] WithOrientation(byte[] jpeg, ImageOrientation orientation)
    {
        // "Exif\0\0", big-endian TIFF header, one IFD0 entry: Orientation (0x0112, SHORT, count 1).
        byte[] payload =
        [
            .. "Exif\0\0"u8.ToArray(),
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,
            0, 0, 0, 0,
        ];
        int length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }
}
