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
