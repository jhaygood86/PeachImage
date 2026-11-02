using System.Diagnostics;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>
/// Native 1/8 decode of a plain VarDCT still (the DC image alone, no AC decoding or inverse DCTs) against a full decode box-averaged
/// down. Everything else (1/2, 1/4, Modular, lossless) falls back to the full decode plus an averaging step in <c>Image.Load</c>.
/// </summary>
public class ScaledDecodeTests
{
    [Theory]
    // Mean absolute differences observed (of 255): 0.10 and 0.27 for the large images; about 4.0 for the tiny one, whose partial edge blocks
    // are a large share of the pixels and average padded content.
    [InlineData(1001, 757, 75, 1.0)]
    [InlineData(640, 480, 90, 1.0)]
    [InlineData(37, 29, 75, 6.0)]
    public void Denominator8_ReducesVarDctToTheCeilSize(int width, int height, int quality, double tolerance)
    {
        var data = Encode(width, height, new JxlEncoderOptions { Quality = quality });
        using var full = Image.Load(data);
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = 8 });

        Assert.Equal((width + 7) / 8, scaled.Width);
        Assert.Equal((height + 7) / 8, scaled.Height);
        Assert.Equal(full.PixelFormat, scaled.PixelFormat);
        using var reference = BoxReduce(full, 8);
        double mad = MeanAbsDiff(scaled, reference);
        Assert.True(mad < tolerance, $"Mean absolute difference {mad}");
    }

    [Fact]
    public void Denominator8_KeepsTheTargetPixelFormat()
    {
        var data = Encode(256, 200, new JxlEncoderOptions { Quality = 80 });
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = 8, TargetPixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(PixelFormat.Rgba32, scaled.PixelFormat);
        Assert.Equal(32, scaled.Width);
        Assert.Equal(25, scaled.Height);
    }

    [Fact]
    public void TargetSize_PicksTheNativeEighth()
    {
        var data = Encode(800, 600, new JxlEncoderOptions { Quality = 80 });
        using var scaled = Image.Load(data, new DecoderOptions { TargetSize = (90, 70) });
        Assert.Equal(100, scaled.Width);
        Assert.Equal(75, scaled.Height);
    }

    [Fact]
    public void Denominator8_IsDeterministicAcrossParallelism()
    {
        var data = Encode(512, 384, new JxlEncoderOptions { Quality = 80 });
        using var sequential = Image.Load(data, new DecoderOptions { ScaleDenominator = 8, MaxDegreeOfParallelism = 1 });
        using var parallel = Image.Load(data, new DecoderOptions { ScaleDenominator = 8 });
        Assert.Equal(0.0, MeanAbsDiff(sequential, parallel));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Denominators2And4_FallBackToTheExactScaledSize(int denominator)
    {
        var data = Encode(333, 211, new JxlEncoderOptions { Quality = 80 });
        using var full = Image.Load(data);
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = denominator });
        Assert.Equal((333 + denominator - 1) / denominator, scaled.Width);
        Assert.Equal((211 + denominator - 1) / denominator, scaled.Height);
        using var reference = BoxReduce(full, denominator);
        Assert.True(MeanAbsDiff(scaled, reference) < 0.01);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Lossless_FallsBackToTheExactScaledSize(int denominator)
    {
        var data = Encode(300, 201, new JxlEncoderOptions { Lossless = true });
        using var full = Image.Load(data);
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = denominator });
        Assert.Equal((300 + denominator - 1) / denominator, scaled.Width);
        Assert.Equal((201 + denominator - 1) / denominator, scaled.Height);
        using var reference = BoxReduce(full, denominator);
        Assert.True(MeanAbsDiff(scaled, reference) < 0.01);
    }

    [Fact]
    public void Denominator8_WithAlpha_FallsBackToTheExactScaledSize()
    {
        using var source = Image.Create(203, 99, PixelFormat.Rgba32);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % 203);
            pixels[i + 1] = 90;
            pixels[i + 2] = (byte)(255 - (i / 4 % 203));
            pixels[i + 3] = (byte)(i / 4 / 203 * 2);
        }

        using var stream = new MemoryStream();
        source.Save(stream, "jxl", new JxlEncoderOptions { Quality = 80 });
        using var scaled = Image.Load(stream.ToArray(), new DecoderOptions { ScaleDenominator = 8 });
        Assert.Equal(26, scaled.Width);
        Assert.Equal(13, scaled.Height);
    }

    [Fact]
    public void Denominator8_AppliesOrientationAfterReducing()
    {
        var data = Encode(400, 160, new JxlEncoderOptions { Quality = 80 });
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = 8, ApplyOrientation = true });
        Assert.Equal(50, scaled.Width);
        Assert.Equal(20, scaled.Height);
    }

    [Fact]
    public void Denominator8_IsMuchCheaperThanAFullDecode()
    {
        var data = Encode(1600, 1200, new JxlEncoderOptions { Quality = 75 });
        var fullOptions = new DecoderOptions { MaxDegreeOfParallelism = 1 };
        var reducedOptions = new DecoderOptions { ScaleDenominator = 8, MaxDegreeOfParallelism = 1 };
        Image.Load(data, fullOptions).Dispose();
        Image.Load(data, reducedOptions).Dispose();

        long full = long.MaxValue;
        long reduced = long.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var watch = Stopwatch.StartNew();
            Image.Load(data, fullOptions).Dispose();
            full = Math.Min(full, watch.ElapsedTicks);
            watch.Restart();
            Image.Load(data, reducedOptions).Dispose();
            reduced = Math.Min(reduced, watch.ElapsedTicks);
        }

        // Skipping the AC decode and the inverse DCTs leaves only the DC groups and a 1/64 sized colour conversion (about 50x faster here).
        Assert.True(reduced * 4 < full, $"reduced {reduced} ticks, full {full} ticks");
    }

    private static byte[] Encode(int width, int height, JxlEncoderOptions options)
    {
        using var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        var random = new Random(1234);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = x / (double)width;
                double v = y / (double)height;
                double r = 128 + (90 * Math.Sin((u * 7) + (v * 3)));
                double g = 128 + (80 * Math.Cos((v * 9) - (u * 2)));
                double b = 255 * (0.3 + (0.5 * u * v)) + (((x / 64) + (y / 64)) % 2 == 0 ? 20 : -20);
                double noise = (random.NextDouble() - 0.5) * 12;
                int i = ((y * width) + x) * 3;
                pixels[i] = Clamp(r + noise);
                pixels[i + 1] = Clamp(g + noise);
                pixels[i + 2] = Clamp(b + noise);
            }
        }

        using var stream = new MemoryStream();
        image.Save(stream, "jxl", options);
        return stream.ToArray();
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static Image BoxReduce(Image source, int n)
    {
        int bpp = source.GetPixelSpan().Length / (source.Width * source.Height);
        int w = (source.Width + n - 1) / n, h = (source.Height + n - 1) / n;
        var result = Image.Create(w, h, source.PixelFormat);
        var src = source.GetPixelSpan();
        var dst = result.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < bpp; c++)
                {
                    int sum = 0, count = 0;
                    for (int yy = y * n; yy < Math.Min(source.Height, (y + 1) * n); yy++)
                    {
                        for (int xx = x * n; xx < Math.Min(source.Width, (x + 1) * n); xx++)
                        {
                            sum += src[(((yy * source.Width) + xx) * bpp) + c];
                            count++;
                        }
                    }

                    dst[(((y * w) + x) * bpp) + c] = (byte)((sum + (count / 2)) / count);
                }
            }
        }

        return result;
    }

    private static double MeanAbsDiff(Image a, Image b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        Assert.Equal(x.Length, y.Length);
        long sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += Math.Abs(x[i] - y[i]);
        }

        return (double)sum / x.Length;
    }
}
