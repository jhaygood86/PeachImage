using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Metadata;

/// <summary>Per-format <c>MaxExpansionRatios</c> and the early refusal of a JPEG whose header claims far more than its data can hold.</summary>
public class ExpansionRatioGuardTests
{
    [Fact]
    public void InflatedJpegHeader_IsRefusedFromTheHeader_UnderAPerFormatRatio_WithLittleAllocation()
    {
        byte[] bomb = InflateDeclaredSize(EncodeJpeg(64), 8000, 8000);
        Assert.True(bomb.Length < 4000, $"the bomb should be tiny, was {bomb.Length} bytes");

        var options = new DecoderOptions { MaxExpansionRatios = new Dictionary<string, double> { ["jpeg"] = 200 } };

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ImageTooLargeException>(() => Image.Load(bomb, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("MaxExpansionRatio", ex.Message);
        Assert.True(allocated < 16 * 1024 * 1024, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public void InflatedJpegHeader_IsRefusedEvenWithNoLimitsSet_BeforeAnyBufferIsAllocated()
    {
        byte[] bomb = InflateDeclaredSize(EncodeJpeg(64), 8000, 8000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<JpegFormatException>(() => Image.Load(bomb));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16 * 1024 * 1024, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public void GenuineJpeg_StillDecodesUnderATightJpegRatio()
    {
        byte[] jpeg = EncodeJpeg(256);
        var options = new DecoderOptions { MaxExpansionRatios = new Dictionary<string, double> { ["jpeg"] = 200 } };

        using var image = Image.Load(jpeg, options);

        Assert.Equal(256, image.Width);
    }

    [Fact]
    public void FlatPng_NeedsALooseRatio_AndStillDecodesUnderThePerFormatPngLimit()
    {
        using var flat = Image.Create(400, 400, PixelFormat.Rgba32);
        flat.GetPixelSpan().Clear(); // Create may hand back pooled, non-zeroed memory.
        using var encoded = new MemoryStream();
        flat.Save(encoded, "png");
        byte[] png = encoded.ToArray();

        double ratio = 400.0 * 400 * 4 / png.Length;
        Assert.True(ratio > 2000, $"a flat 400x400 RGBA PNG expands {ratio:N0}x");

        // One global limit tight enough for JPEG rejects it...
        Assert.Throws<ImageTooLargeException>(() => Image.Load(png, new DecoderOptions { MaxExpansionRatio = 200 }));

        // ...while a per-format table keeps JPEG tight and lets flat PNG through.
        var perFormat = new DecoderOptions
        {
            MaxExpansionRatio = 200,
            MaxExpansionRatios = new Dictionary<string, double> { ["png"] = 30_000 },
        };
        using var image = Image.Load(png, perFormat);
        Assert.Equal(400, image.Width);
    }

    [Fact]
    public void PerFormatRatio_MatchesFormatNameIgnoringCase_AndFallsBackToTheGlobalRatio()
    {
        var flatSource = Image.Create(64, 64, PixelFormat.Rgb24);
        flatSource.GetPixelSpan().Clear();
        byte[] png = Encode(flatSource, "png");

        Assert.Throws<ImageTooLargeException>(() => Image.Load(png, new DecoderOptions { MaxExpansionRatio = 1 }));

        var overridden = new DecoderOptions
        {
            MaxExpansionRatio = 1,
            MaxExpansionRatios = new Dictionary<string, double> { ["PNG"] = 100_000 },
        };
        using var image = Image.Load(png, overridden);
        Assert.Equal(64, image.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void NonPositiveRatio_IsRejected(double ratio)
    {
        byte[] jpeg = EncodeJpeg(16);
        var options = new DecoderOptions { MaxExpansionRatios = new Dictionary<string, double> { ["jpeg"] = ratio } };

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.Load(jpeg, options));
    }

    [Fact]
    public void TruncatedBaselineJpeg_FailsInsteadOfDecodingZeroBlocks()
    {
        using var source = Image.Create(1200, 900, PixelFormat.Rgb24);
        new Random(9).NextBytes(source.GetPixelSpan());
        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90 });
        byte[] jpeg = ms.ToArray();

        // Cut the entropy-coded data just after the header, keeping enough bytes to look plausible for the claimed canvas.
        byte[] truncated = jpeg[..(jpeg.Length / 4)];

        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(truncated));
    }

    private static byte[] EncodeJpeg(int size)
    {
        // A mildly textured image so the file is a few hundred bytes to a few KB, like a real small photo.
        using var source = Image.Create(size, size, PixelFormat.Rgb24);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(128 + (((i / 3) % size) / 4) + (i % 3));
        }

        return Encode(source, "jpeg");
    }

    private static byte[] Encode(Image source, string format)
    {
        using (source)
        using (var ms = new MemoryStream())
        {
            source.Save(ms, format);
            return ms.ToArray();
        }
    }

    /// <summary>Rewrites the width and height in the first start-of-frame segment, leaving the entropy-coded data as it was.</summary>
    private static byte[] InflateDeclaredSize(byte[] jpeg, int width, int height)
    {
        byte[] result = (byte[])jpeg.Clone();
        for (int i = 2; i < result.Length - 9; i++)
        {
            if (result[i] == 0xFF && result[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                result[i + 5] = (byte)(height >> 8);
                result[i + 6] = (byte)height;
                result[i + 7] = (byte)(width >> 8);
                result[i + 8] = (byte)width;
                return result;
            }
        }

        throw new InvalidOperationException("No SOF marker found.");
    }
}
