using System.Runtime.InteropServices;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Decodes lossy (VarDCT) JPEG XL files that libjxl just produced and compares against libjxl's own 16-bit decode. Float
/// rounding in the transforms and filters means results can differ from libjxl by a few levels of 16-bit, so a small tolerance is used.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlVarDctOracleTests
{
    // 16-bit levels: well under one 8-bit level (257) on average; the maximum allows isolated rounding differences.
    private const double MeanTolerance = 40;
    private const int MaxTolerance = 600;

    public static TheoryData<string, string, string, string> Cases() => new()
    {
        { "mandelbrot=size=64x48", "rgb48le", "-distance 1 -effort 7", "rgb48le" },
        { "gradients=size=200x120", "rgb48le", "-distance 1", "rgb48le" },
        { "mandelbrot=size=256x256", "rgb48le", "-distance 2 -effort 5", "rgb48le" },
        { "mandelbrot=size=256x256", "rgb48le", "-distance 2 -effort 6", "rgb48le" },
        { "mandelbrot=size=200x150", "rgb48le", "-distance 1.5 -effort 5", "rgb48le" },
        { "smptebars=size=133x77", "rgb48le", "-distance 0.5", "rgb48le" },
        { "mandelbrot=size=300x200", "rgb48le", "-distance 3 -effort 9", "rgb48le" },
        { "mandelbrot=size=600x700", "rgb48le", "-distance 1", "rgb48le" },
        { "gradients=size=1100x700", "rgb48le", "-distance 8 -effort 9", "rgb48le" },
        { "gradients=size=1100x700", "rgb48le", "-distance 14 -effort 7", "rgb48le" },
        { "smptebars=size=1000x600", "rgb48le", "-distance 6 -effort 9", "rgb48le" },
        { "mandelbrot=size=100x80,format=gray", "gray16le", "-distance 1", "gray16le" },
        { "mandelbrot=size=100x80", "rgba64le", "-distance 1", "rgba64le" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LossyVarDct_MatchesLibjxlWithinTolerance(string source, string inputFormat, string options, string referenceFormat)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] jxl = LibjxlOracle.Encode(source, inputFormat, options);
        byte[] reference = LibjxlOracle.Decode(jxl, referenceFormat);
        PixelFormat target = referenceFormat switch
        {
            "rgb48le" => PixelFormat.Rgb48,
            "rgba64le" => PixelFormat.Rgba64,
            _ => PixelFormat.Gray16,
        };

        using var stream = new MemoryStream(jxl);
        using var image = Image.Load(stream, new DecoderOptions { TargetPixelFormat = target });

        var ours = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        var theirs = MemoryMarshal.Cast<byte, ushort>(reference.AsSpan());
        Assert.Equal(theirs.Length, ours.Length);

        long total = 0;
        int max = 0;
        int worst = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            int diff = Math.Abs(ours[i] - theirs[i]);
            total += diff;
            if (diff > max)
            {
                max = diff;
                worst = i;
            }
        }

        double mean = (double)total / ours.Length;
        Assert.True(
            mean <= MeanTolerance && max <= MaxTolerance,
            $"mean abs difference {mean:F1}, max {max} at sample {worst} (ours {ours[worst]}, libjxl {theirs[worst]}), {image.Width}x{image.Height}.");
    }
}
