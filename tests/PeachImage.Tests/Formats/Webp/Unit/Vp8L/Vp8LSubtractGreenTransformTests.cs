using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LSubtractGreenTransformTests
{
    [Fact]
    public void ApplyInverse_AddsGreenToRedAndBlue_WithWraparound()
    {
        // alpha=255, red=5, green=10, blue=250 -> red becomes 15 (5+10), blue wraps to 4 ((250+10) mod 256).
        uint input = Pack(255, 5, 10, 250);
        uint[] pixels = [input];

        Vp8LSubtractGreenTransform.ApplyInverse(pixels);

        Assert.Equal(Pack(255, 15, 10, 4), pixels[0]);
    }

    [Fact]
    public void ApplyInverse_ZeroGreen_LeavesRedAndBlueUnchanged()
    {
        uint input = Pack(128, 77, 0, 200);
        uint[] pixels = [input];

        Vp8LSubtractGreenTransform.ApplyInverse(pixels);

        Assert.Equal(input, pixels[0]);
    }

    [Fact]
    public void ApplyInverse_AlphaAndGreenChannels_AreNeverModified()
    {
        uint input = Pack(42, 1, 250, 1);
        uint[] pixels = [input];

        Vp8LSubtractGreenTransform.ApplyInverse(pixels);

        uint result = pixels[0];
        Assert.Equal(42u, result >> 24);
        Assert.Equal(250u, (result >> 8) & 0xFF);
    }

    [Fact]
    public void ApplyInverse_MultiplePixels_EachIndependentlyCorrect()
    {
        uint[] pixels =
        [
            Pack(255, 0, 0, 0),
            Pack(255, 200, 100, 200),
            Pack(0, 255, 255, 255),
        ];

        uint[] expected =
        [
            Pack(255, 0, 0, 0),
            Pack(255, (200 + 100) & 0xFF, 100, (200 + 100) & 0xFF),
            Pack(0, (255 + 255) & 0xFF, 255, (255 + 255) & 0xFF),
        ];

        Vp8LSubtractGreenTransform.ApplyInverse(pixels);

        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void AllThreeKernelTiers_AgreeOnRandomInput()
    {
        var random = new Random(99);
        uint[] source = new uint[37]; // not a multiple of 4 or 8 -- exercises each kernel's scalar remainder loop too.
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (uint)random.Next();
        }

        AssertAllTiersAgree(source);
    }

    /// <summary>
    /// Regression test for a carry-propagation bug in both SIMD tiers: they added green to red and blue with a
    /// single <c>uint</c>-lane add, so a carry out of the blue byte ran through the green byte and into red.
    /// Red therefore came out one too high for every pixel with green == 255 and blue &gt;= 1 — visible in
    /// libwebp-test-data's <c>lossless1..3.webp</c> and <c>lossless_big_random_alpha.webp</c>, which the
    /// SkiaSharp corpus differential flagged only once it compared every pixel rather than a sampled grid.
    /// </summary>
    /// <remarks>
    /// The pre-existing random-input tier-agreement test above missed it twice over: the carry needs
    /// green == 255 specifically (~0.4% of random pixels), and the one hand-written case that does have
    /// green == 255 lives in a 3-element array, which is shorter than <c>Vector128&lt;uint&gt;.Count</c> and so
    /// never reaches either vectorized body at all. Hence the deliberately long, deliberately
    /// carry-triggering buffer here.
    /// </remarks>
    [Fact]
    public void AllThreeKernelTiers_Agree_WhenGreenIsMaxAndBlueWouldCarryOutOfItsByte()
    {
        var source = new List<uint>();
        for (int blue = 0; blue <= 255; blue++)
        {
            for (int red = 250; red <= 255; red++)
            {
                source.Add(Pack(a: 128, r: red, g: 255, b: blue));
            }
        }

        AssertAllTiersAgree([.. source]);
    }

    private static void AssertAllTiersAgree(uint[] source)
    {
        uint[] scalarResult = (uint[])source.Clone();
        new ScalarVp8LTransformKernel().SubtractGreenInverse(scalarResult);

        uint[] vector128Result = (uint[])source.Clone();
        new Vector128Vp8LTransformKernel().SubtractGreenInverse(vector128Result);

        uint[] vector256Result = (uint[])source.Clone();
        new Vector256Vp8LTransformKernel().SubtractGreenInverse(vector256Result);

        Assert.Equal(scalarResult, vector128Result);
        Assert.Equal(scalarResult, vector256Result);
    }

    private static uint Pack(int a, int r, int g, int b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
}
