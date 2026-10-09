using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Decodes files from the libjxl conformance suite (BSD-3-Clause, https://github.com/libjxl/conformance) that exercise
/// frame-level tools -- noise, patches -- and compares against libjxl's own 8-bit decode.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlConformanceOracleTests
{
    // 8-bit levels. Lossless content must match exactly; lossy content may differ by one level where libjxl dithers.
    public static TheoryData<string, double, int> Cases() => new()
    {
        { "conformance_patches_lossless.jxl", 0, 0 },
        { "conformance_noise.jxl", 0.6, 2 },
        { "conformance_opsin_inverse.jxl", 0.6, 2 },
        { "conformance_splines.jxl", 0.6, 2 },
        { "conformance_upsampling.jxl", 0.6, 2 },
        { "conformance_spline_first_frame.jxl", 0.6, 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesLibjxl(string asset, double meanTolerance, int maxTolerance)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] jxl = JxlTestAssets.Load(asset);
        byte[] reference = LibjxlOracle.Decode(jxl, "rgb24");

        using var image = Image.Load(new MemoryStream(jxl), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb24 });

        var ours = image.GetPixelSpan();
        ReadOnlySpan<byte> theirs = reference;
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
            mean <= meanTolerance && max <= maxTolerance,
            $"mean abs difference {mean:F1}, max {max} at sample {worst} (ours {ours[worst]}, libjxl {theirs[worst]}), {image.Width}x{image.Height}.");
    }
}
