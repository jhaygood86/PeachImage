using PeachImage.Formats.Jpeg2000;

namespace PeachImage.Tests.Formats.Jpeg2000;

/// <summary>Native 1/N decode (dropping resolution levels) against a full decode box-averaged down.</summary>
public class ScaledDecodeTests
{
    private const string Prefix = "PeachImage.Tests.Formats.Jpeg2000.Assets.";

    [Theory]
    [InlineData("rgb_big.jp2", 2)]
    [InlineData("rgb_big.jp2", 4)]
    [InlineData("gray_lossy.jp2", 2)]
    [InlineData("box_srgb.jp2", 2)]
    [InlineData("eph.j2k", 2)]
    [InlineData("rgb_lossy_hq.jp2", 2)]
    [InlineData("rgb_levels6.jp2", 4)]
    public void ScaleDenominator_ReducesNativelyToTheCeilSize(string file, int denominator)
    {
        var data = ReadAsset(file);
        using var full = Image.Load(data);
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = denominator });

        Assert.Equal((full.Width + denominator - 1) / denominator, scaled.Width);
        Assert.Equal((full.Height + denominator - 1) / denominator, scaled.Height);
        Assert.Equal(full.PixelFormat, scaled.PixelFormat);
        using var reference = BoxReduce(full, denominator);
        // The wavelet low-pass is not a box average (different phase), and these synthetic assets are sharp-edged: allow 4 per halving.
        Assert.True(MeanAbsDiff(scaled, reference) < 4.0 * denominator);
    }

    [Fact]
    public void ScaleDenominator_MatchesExplicitResolutionReduction()
    {
        var data = ReadAsset("rgb_lossless.jp2");
        using var native = Image.Load(data, new DecoderOptions { ScaleDenominator = 4 });
        using var explicitly = Image.Load(data, new Jpeg2000DecoderOptions { ReduceResolutionLevels = 2 });
        Assert.Equal(explicitly.Width, native.Width);
        Assert.Equal(explicitly.Height, native.Height);
        Assert.Equal(0.0, MeanAbsDiff(native, explicitly));
    }

    [Fact]
    public void ScaleDenominator_ComposesWithExplicitReduction()
    {
        var data = ReadAsset("rgb_lossless.jp2");
        using var composed = Image.Load(data, new Jpeg2000DecoderOptions { ReduceResolutionLevels = 1, ScaleDenominator = 2 });
        using var quarter = Image.Load(data, new Jpeg2000DecoderOptions { ReduceResolutionLevels = 2 });
        Assert.Equal(quarter.Width, composed.Width);
        Assert.Equal(quarter.Height, composed.Height);
        Assert.Equal(0.0, MeanAbsDiff(composed, quarter));
    }

    [Fact]
    public void TargetSize_PicksTheLargestCoveringScale()
    {
        using var image = Image.Load(ReadAsset("rgb_lossless.jp2"), new DecoderOptions { TargetSize = (30, 20) });
        Assert.Equal(38, image.Width); // 150 / 4 = 37.5 still covers 30 wide; 150 / 8 would not.
        Assert.Equal(28, image.Height);
    }

    [Theory]
    [InlineData("rgb_levels0.jp2", 2)]
    [InlineData("rgb_levels1.jp2", 4)]
    [InlineData("rgb_levels3.jp2", 8)]
    public void TooFewLevels_FallsBackToTheExactScaledSize(string file, int denominator)
    {
        var data = ReadAsset(file);
        using var full = Image.Load(data);
        using var scaled = Image.Load(data, new DecoderOptions { ScaleDenominator = denominator });

        Assert.Equal((full.Width + denominator - 1) / denominator, scaled.Width);
        Assert.Equal((full.Height + denominator - 1) / denominator, scaled.Height);
        using var reference = BoxReduce(full, denominator);
        // The wavelet low-pass is not a box average (different phase), and these synthetic assets are sharp-edged: allow 4 per halving.
        Assert.True(MeanAbsDiff(scaled, reference) < 4.0 * denominator);
    }

    [Fact]
    public void Parallelism_StillFlowsAndGivesTheSameResult()
    {
        var data = ReadAsset("rgb_lossy_tiles.jp2");
        using var sequential = Image.Load(data, new DecoderOptions { ScaleDenominator = 2, MaxDegreeOfParallelism = 1 });
        using var parallel = Image.Load(data, new DecoderOptions { ScaleDenominator = 2 });
        Assert.Equal(0.0, MeanAbsDiff(sequential, parallel));
    }

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
                            sum += src[((yy * source.Width) + xx) * bpp + c];
                            count++;
                        }
                    }

                    dst[((y * w) + x) * bpp + c] = (byte)((sum + (count / 2)) / count);
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

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(ScaledDecodeTests).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
