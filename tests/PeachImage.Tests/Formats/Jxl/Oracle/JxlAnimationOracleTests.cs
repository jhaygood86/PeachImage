using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Decodes animated / blended JPEG XL files (libjxl conformance suite and test data, BSD-3-Clause) and compares every
/// composited frame with libjxl's own RGBA output.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlAnimationOracleTests
{
    public static TheoryData<string, double, int> Cases() => new()
    {
        { "conformance_blendmodes.jxl", 0.6, 2 },
        { "testdata_cropped_traffic_light.jxl", 0.6, 2 },
        { "conformance_animation_spline.jxl", 0.6, 2 },
        { "conformance_spline_first_frame.jxl", 0.6, 2 },
        { "conformance_animation_icos4d.jxl", 0.6, 2 },
        { "conformance_animation_newtons_cradle.jxl", 0.6, 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFrame_MatchesLibjxl(string asset, double meanTolerance, int maxTolerance)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] jxl = JxlTestAssets.Load(asset);
        byte[] reference = LibjxlOracle.Decode(jxl, "rgba");

        using var stream = new MemoryStream(jxl);
        var animation = AnimatedImage.Load(stream);
        int frameBytes = animation.Width * animation.Height * 4;
        Assert.True(reference.Length % frameBytes == 0, "The reference is not a whole number of frames.");

        int index = 0;
        foreach (var frame in animation.Frames)
        {
            Assert.True((index + 1) * frameBytes <= reference.Length, $"More frames decoded than libjxl produced (frame {index}).");
            var ours = frame.Image.GetPixelSpan();
            var theirs = reference.AsSpan(index * frameBytes, frameBytes);
            long total = 0;
            int max = 0;
            for (int i = 0; i < frameBytes; i++)
            {
                int diff = Math.Abs(ours[i] - theirs[i]);
                total += diff;
                max = Math.Max(max, diff);
            }

            double mean = (double)total / frameBytes;
            Assert.True(mean <= meanTolerance && max <= maxTolerance, $"frame {index}: mean abs difference {mean:F2}, max {max}.");
            index++;
        }

        Assert.Equal(reference.Length / frameBytes, index);
    }
}
