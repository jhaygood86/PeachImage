using PeachImage.Formats.Webp.Decoding.Vp8L;

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
