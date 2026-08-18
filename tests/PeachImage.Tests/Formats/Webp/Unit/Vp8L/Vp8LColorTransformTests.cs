using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LColorTransformTests
{
    [Fact]
    public void ApplyInverse_PositiveGreen_AddsScaledDeltaToRed()
    {
        // greenToRed=32 (2^5, so delta = green exactly via >>5), greenToBlue=0, redToBlue=0.
        uint colorCode = Pack24(32, 0, 0);
        var transform = MakeTransform(width: 1, height: 1, bits: 0, tileData: [colorCode]);

        uint[] pixels = [Pack(255, 100, 8, 50)]; // green=8 -> delta=(32*8)>>5=8.
        Vp8LColorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 108, 8, 50), pixels[0]);
    }

    [Fact]
    public void ApplyInverse_NegativeGreen_SubtractsScaledDeltaFromRed_WithWraparound()
    {
        uint colorCode = Pack24(32, 0, 0);
        var transform = MakeTransform(width: 1, height: 1, bits: 0, tileData: [colorCode]);

        // green byte 200 is -56 as a signed byte. delta=(32*-56)>>5=-56. red=10-56=-46, which wraps to 210.
        uint[] pixels = [Pack(255, 10, 200, 0)];
        Vp8LColorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 210, 200, 0), pixels[0]);
    }

    [Fact]
    public void ApplyInverse_ZeroMultipliers_LeavesRedAndBlueUnchanged()
    {
        uint colorCode = Pack24(0, 0, 0);
        var transform = MakeTransform(width: 1, height: 1, bits: 0, tileData: [colorCode]);

        uint[] pixels = [Pack(255, 77, 33, 99)];
        Vp8LColorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 77, 33, 99), pixels[0]);
    }

    [Fact]
    public void ApplyInverse_BlueDelta_UsesJustUpdatedRed_NotOriginalRed()
    {
        // greenToRed=32 (delta1=green), greenToBlue=0, redToBlue=32 (its delta uses the *new*, post-delta1
        // red -- not the original). green=4 -> delta1=4 -> new_red=10+4=14. blue's second delta =
        // (32*14)>>5=14 (would be (32*10)>>5=10 if it incorrectly used the original, pre-delta1 red).
        uint colorCode = Pack24(32, 0, 32);
        var transform = MakeTransform(width: 1, height: 1, bits: 0, tileData: [colorCode]);

        uint[] pixels = [Pack(255, 10, 4, 0)];
        Vp8LColorTransform.ApplyInverse(pixels, transform);

        uint result = pixels[0];
        Assert.Equal(14, (int)((result >> 16) & 0xFF));
        Assert.Equal(14, (int)(result & 0xFF));
    }

    [Fact]
    public void ApplyInverse_MultipleTiles_EachRunUsesItsOwnTilesMultipliers()
    {
        // width=4, bits=1 -> tile width 2, two tiles across the row. Tile 0: greenToRed=32. Tile 1: greenToRed=64.
        uint tile0 = Pack24(32, 0, 0);
        uint tile1 = Pack24(64, 0, 0);
        var transform = MakeTransform(width: 4, height: 1, bits: 1, tileData: [tile0, tile1]);

        uint[] pixels =
        [
            Pack(255, 0, 8, 0), // tile 0: delta=(32*8)>>5=8 -> red=8.
            Pack(255, 0, 8, 0), // tile 0: delta=8 -> red=8.
            Pack(255, 0, 8, 0), // tile 1: delta=(64*8)>>5=16 -> red=16.
            Pack(255, 0, 8, 0), // tile 1: delta=16 -> red=16.
        ];

        Vp8LColorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(8, (int)((pixels[0] >> 16) & 0xFF));
        Assert.Equal(8, (int)((pixels[1] >> 16) & 0xFF));
        Assert.Equal(16, (int)((pixels[2] >> 16) & 0xFF));
        Assert.Equal(16, (int)((pixels[3] >> 16) & 0xFF));
    }

    /// <summary>
    /// Direct kernel-tier agreement test, mirroring <c>Vp8LSubtractGreenTransformTests.AllThreeKernelTiers_AgreeOnRandomInput</c>:
    /// constructs each tier directly (bypassing hardware detection) so all three run in CI regardless of the
    /// CI machine's ISA, across pixel-array lengths spanning the Vector128/Vector256 SIMD widths and their
    /// scalar tails.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(100)]
    public void AllThreeKernelTiers_AgreeOnRandomInput(int pixelCount)
    {
        var random = new Random(1000 + pixelCount);
        uint[] source = new uint[pixelCount];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (uint)random.Next();
        }

        sbyte greenToRed = (sbyte)random.Next(-128, 128);
        sbyte greenToBlue = (sbyte)random.Next(-128, 128);
        sbyte redToBlue = (sbyte)random.Next(-128, 128);

        AssertAllTiersAgree(source, greenToRed, greenToBlue, redToBlue);
    }

    /// <summary>Extreme multiplier/color combinations (min/max sbyte on both sides), to stress the sign-extension and shift math at its boundaries rather than relying on random sampling to hit them.</summary>
    [Fact]
    public void AllThreeKernelTiers_Agree_OnExtremeMultipliersAndChannelValues()
    {
        List<uint> source = [];
        foreach (int a in new[] { 0, 255 })
        {
            foreach (int g in new[] { 0, 1, 127, 128, 200, 255 })
            {
                foreach (int r in new[] { 0, 1, 127, 128, 200, 255 })
                {
                    foreach (int b in new[] { 0, 1, 127, 128, 200, 255 })
                    {
                        source.Add(Pack(a, r, g, b));
                    }
                }
            }
        }

        foreach (sbyte greenToRed in new sbyte[] { sbyte.MinValue, -1, 0, 1, sbyte.MaxValue })
        {
            foreach (sbyte greenToBlue in new sbyte[] { sbyte.MinValue, 0, sbyte.MaxValue })
            {
                foreach (sbyte redToBlue in new sbyte[] { sbyte.MinValue, 0, sbyte.MaxValue })
                {
                    AssertAllTiersAgree([.. source], greenToRed, greenToBlue, redToBlue);
                }
            }
        }
    }

    private static void AssertAllTiersAgree(uint[] source, sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue)
    {
        uint[] scalarResult = (uint[])source.Clone();
        new ScalarVp8LTransformKernel().ColorTransformInverse(scalarResult, greenToRed, greenToBlue, redToBlue);

        uint[] vector128Result = (uint[])source.Clone();
        new Vector128Vp8LTransformKernel().ColorTransformInverse(vector128Result, greenToRed, greenToBlue, redToBlue);

        uint[] vector256Result = (uint[])source.Clone();
        new Vector256Vp8LTransformKernel().ColorTransformInverse(vector256Result, greenToRed, greenToBlue, redToBlue);

        Assert.Equal(scalarResult, vector128Result);
        Assert.Equal(scalarResult, vector256Result);
    }

    private static uint Pack(int a, int r, int g, int b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    private static uint Pack24(int greenToRed, int greenToBlue, int redToBlue) =>
        ((uint)greenToRed & 0xFF) | (((uint)greenToBlue & 0xFF) << 8) | (((uint)redToBlue & 0xFF) << 16);

    private static Vp8LTransform MakeTransform(int width, int height, int bits, uint[] tileData) =>
        new()
        {
            Type = Vp8LTransformType.CrossColor,
            Xsize = width,
            Ysize = height,
            Bits = bits,
            Data = tileData,
        };
}
