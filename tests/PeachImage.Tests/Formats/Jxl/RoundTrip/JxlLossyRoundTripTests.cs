using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.RoundTrip;

/// <summary>Encodes with the lossy VarDCT encoder and decodes with the existing decoder; the pixels must come back close to the source.</summary>
public class JxlLossyRoundTripTests
{
    public static TheoryData<int, int> Sizes => new()
    {
        { 1, 1 },
        { 7, 5 },
        { 17, 31 },
        { 64, 48 },
        { 256, 256 },
        { 257, 17 },
        { 300, 200 },
        { 520, 530 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgb24_StaysCloseToTheSource(int width, int height) => AssertClose(Create(width, height, PixelFormat.Rgb24), PixelFormat.Rgb24, 28);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Gray8_StaysCloseToTheSource(int width, int height) => AssertClose(Create(width, height, PixelFormat.Gray8), PixelFormat.Gray8, 28);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba32_StaysCloseToTheSource(int width, int height) => AssertClose(Create(width, height, PixelFormat.Rgba32), PixelFormat.Rgba32, 28);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgb48_StaysCloseToTheSource(int width, int height) => AssertClose(Create(width, height, PixelFormat.Rgb48), PixelFormat.Rgb48, 28);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Gray16_StaysCloseToTheSource(int width, int height) => AssertClose(Create(width, height, PixelFormat.Gray16), PixelFormat.Gray16, 28);

    [Fact]
    public void HigherQuality_IsLargerAndCloser()
    {
        var source = Create(200, 150, PixelFormat.Rgb24);
        (long lowSize, double lowPsnr) = Measure(source, 28);
        (long highSize, double highPsnr) = Measure(source, 90);

        Assert.True(highSize > lowSize, $"q90 ({highSize} B) should be larger than q30 ({lowSize} B).");
        Assert.True(highPsnr > lowPsnr + 1.5, $"q90 ({highPsnr:F1} dB) should be clearly closer than q30 ({lowPsnr:F1} dB).");
    }

    [Fact]
    public void LossyOutput_IsNotReportedAsLossless()
    {
        using var stream = new MemoryStream(Encode(Create(70, 20, PixelFormat.Rgb24), 75));
        Assert.False(Image.Identify(stream).IsLosslessEncoding);
    }

    [Fact]
    public void TransparentRgba_KeepsAlphaExactly()
    {
        var source = Create(300, 140, PixelFormat.Rgba32);
        using var stream = new MemoryStream(Encode(source, 75));
        var decoded = Image.Load(stream);

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        var s = source.GetPixelSpan();
        var d = decoded.GetPixelSpan();
        for (int i = 3; i < s.Length; i += 4)
        {
            Assert.Equal(s[i], d[i]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    public void EveryEffortTier_StaysCloseToTheSource(int effort)
    {
        var source = Create(330, 250, PixelFormat.Rgb24);

        using var stream = new MemoryStream(Encode(source, 85, new JxlEncoderOptions { Quality = 85, Effort = effort }));
        Assert.True(Psnr(source, Image.Load(stream)) >= 28);
    }

    [Fact]
    public void HigherEffort_UsesLargerTransformsAndShrinksSmoothImages()
    {
        var source = Create(512, 384, PixelFormat.Rgb24);

        long fast = Encode(source, 80, new JxlEncoderOptions { Quality = 80, Effort = 3 }).Length;
        long thorough = Encode(source, 80, new JxlEncoderOptions { Quality = 80, Effort = 7 }).Length;

        Assert.True(thorough < fast, $"Effort 7 ({thorough} B) should beat effort 3 ({fast} B) on a smooth image.");
    }

    [Fact]
    public void AdaptiveQuantization_StaysCloseToTheSource()
    {
        var source = Create(300, 220, PixelFormat.Rgb24);

        using var stream = new MemoryStream(Encode(source, 80, new JxlEncoderOptions { Quality = 80, AdaptiveQuantization = true }));
        Assert.True(Psnr(source, Image.Load(stream)) >= 28);
    }

    [Fact]
    public void EffortOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(Create(8, 8, PixelFormat.Rgb24), 80, new JxlEncoderOptions { Effort = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(Create(8, 8, PixelFormat.Rgb24), 80, new JxlEncoderOptions { Effort = 10 }));
    }

    [Fact]
    public void QualityOutOfRange_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(Create(8, 8, PixelFormat.Rgb24), 101));

    private static byte[] Encode(Image image, int quality) => Encode(image, quality, new JxlEncoderOptions { Quality = quality });

    private static byte[] Encode(Image image, int quality, JxlEncoderOptions options)
    {
        _ = quality;
        using var output = new MemoryStream();
        image.Save(output, "jxl", options);
        return output.ToArray();
    }

    private static (long Size, double Psnr) Measure(Image source, int quality)
    {
        byte[] encoded = Encode(source, quality);
        using var stream = new MemoryStream(encoded);
        return (encoded.Length, Psnr(source, Image.Load(stream)));
    }

    private static void AssertClose(Image source, PixelFormat expectedFormat, double minPsnr)
    {
        using var stream = new MemoryStream(Encode(source, 90));
        var decoded = Image.Load(stream);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(expectedFormat, decoded.PixelFormat);
        double psnr = Psnr(source, decoded);
        Assert.True(psnr >= minPsnr, $"PSNR {psnr:F1} dB is below {minPsnr} dB.");
    }

    internal static double Psnr(Image a, Image b)
    {
        bool sixteen = a.PixelFormat is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        double peak = sixteen ? 65535.0 : 255.0;
        double sum = 0;
        int count;
        if (sixteen)
        {
            var x = MemoryMarshal.Cast<byte, ushort>(a.GetPixelSpan());
            var y = MemoryMarshal.Cast<byte, ushort>(b.GetPixelSpan());
            count = x.Length;
            for (int i = 0; i < count; i++)
            {
                double d = x[i] - y[i];
                sum += d * d;
            }
        }
        else
        {
            var x = a.GetPixelSpan();
            var y = b.GetPixelSpan();
            count = x.Length;
            for (int i = 0; i < count; i++)
            {
                double d = x[i] - y[i];
                sum += d * d;
            }
        }

        return 10 * Math.Log10(peak * peak / ((sum / count) + 1e-12));
    }

    // A smooth, photo-like image: gradients and soft shapes with mild noise, so it quantizes the way real content does.
    internal static Image Create(int width, int height, PixelFormat format)
    {
        var image = Image.Create(width, height, format);
        var random = new Random((width * 31) + height);
        int channels = format switch
        {
            PixelFormat.Gray8 or PixelFormat.Gray16 => 1,
            PixelFormat.Rgb24 or PixelFormat.Rgb48 => 3,
            _ => 4,
        };

        bool sixteen = format is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        double Value(int x, int y, int c)
        {
            double u = x / (double)Math.Max(1, width - 1);
            double v = y / (double)Math.Max(1, height - 1);
            double wave = 0.5 + (0.25 * Math.Sin((u * 6) + c)) + (0.2 * Math.Cos((v * 5) - c));
            double blob = Math.Exp(-(((u - 0.4) * (u - 0.4)) + ((v - 0.6) * (v - 0.6))) * 18) * 0.3;
            return Math.Clamp(wave + blob + ((random.NextDouble() - 0.5) * 0.04), 0, 1);
        }

        if (sixteen)
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
            for (int i = 0; i < samples.Length; i++)
            {
                int pixel = i / channels;
                int c = i % channels;
                samples[i] = c == 3 ? (ushort)(((pixel % width) * 65535L / Math.Max(1, width - 1)) & 0xFFFF) : (ushort)(Value(pixel % width, pixel / width, c) * 65535);
            }
        }
        else
        {
            var pixels = image.GetPixelSpan();
            for (int i = 0; i < pixels.Length; i++)
            {
                int pixel = i / channels;
                int c = i % channels;
                pixels[i] = c == 3 ? (byte)(((pixel % width) * 255L / Math.Max(1, width - 1)) & 0xFF) : (byte)(Value(pixel % width, pixel / width, c) * 255);
            }
        }

        return image;
    }
}
