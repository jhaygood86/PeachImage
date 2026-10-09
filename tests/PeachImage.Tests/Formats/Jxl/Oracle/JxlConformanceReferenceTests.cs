using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Decodes libjxl conformance files whose pixels are in the embedded ICC profile's space (so ffmpeg, which converts to its
/// own colour space, is no oracle) and compares against samples taken from the suite's own reference PNGs
/// (https://github.com/libjxl/conformance, BSD-3-Clause). A <c>.samples</c> file holds width, height and step (int32) followed by
/// every <c>step</c>-th pixel of every <c>step</c>-th row of the reference as RGB24.
/// </summary>
public class JxlConformanceReferenceTests
{
    [Theory]
    [InlineData("conformance_patches")]
    [InlineData("conformance_progressive")] // DC frame + patch source frame + two passes + EPF, XYB with a non-sRGB-curve ICC profile.
    public void Decode_MatchesReferenceSamples(string name)
    {
        byte[] samples = JxlTestAssets.Load(name + ".samples");
        int width = BitConverter.ToInt32(samples, 0);
        int height = BitConverter.ToInt32(samples, 4);
        int step = BitConverter.ToInt32(samples, 8);

        using var image = Image.Load(new MemoryStream(JxlTestAssets.Load(name + ".jxl")), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb24 });
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);

        var pixels = image.GetPixelSpan();
        int index = 12;
        long total = 0;
        int max = 0;
        int count = 0;
        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                for (int c = 0; c < 3; c++)
                {
                    int diff = Math.Abs(pixels[(((y * width) + x) * 3) + c] - samples[index++]);
                    total += diff;
                    max = Math.Max(max, diff);
                    count++;
                }
            }
        }

        Assert.Equal(samples.Length, index);
        Assert.True(max <= 2 && (double)total / count <= 0.3, $"mean abs difference {(double)total / count:F2}, max {max}.");
    }
}
