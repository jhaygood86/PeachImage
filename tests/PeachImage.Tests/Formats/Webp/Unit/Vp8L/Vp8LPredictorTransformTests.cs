using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LPredictorTransformTests
{
    [Fact]
    public void Mode0_FirstPixelOfImage_IsAlwaysFixedBlack_RegardlessOfTileData()
    {
        var transform = MakeTransform(width: 1, height: 1, bits: 0, tileData: [15]); // tile mode 15, must be ignored.
        uint[] pixels = [Pack(0, 50, 60, 70)];

        Vp8LPredictorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 50, 60, 70), pixels[0]);
    }

    [Fact]
    public void Row0_AlwaysUsesLeftPredictor_RegardlessOfTileData()
    {
        var transform = MakeTransform(width: 3, height: 1, bits: 0, tileData: [9, 9, 9]);
        uint[] pixels = [Pack(0, 0, 0, 0), Pack(0, 10, 0, 0), Pack(0, 5, 0, 0)];

        Vp8LPredictorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 0, 0, 0), pixels[0]);
        Assert.Equal(Pack(255, 10, 0, 0), pixels[1]); // black red (0) + residual 10
        Assert.Equal(Pack(255, 15, 0, 0), pixels[2]); // previous red (10) + residual 5
    }

    [Fact]
    public void FirstColumnOfEachRow_AlwaysUsesTopPredictor_RegardlessOfTileData()
    {
        var transform = MakeTransform(width: 1, height: 2, bits: 0, tileData: [9, 9]);
        uint[] pixels = [Pack(0, 0, 20, 0), Pack(0, 0, 5, 0)];

        Vp8LPredictorTransform.ApplyInverse(pixels, transform);

        Assert.Equal(Pack(255, 0, 20, 0), pixels[0]);
        Assert.Equal(Pack(255, 0, 25, 0), pixels[1]); // top green (20) + residual green (5)
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void InteriorPixel_MatchesIndependentReferenceFormula(int mode)
    {
        // 3x3 grid, bits=0 (one tile per pixel). Row 0 and column 0 are all forced (modes 0/1/2), which
        // gives known, easily-traced values for pixel (1,1)'s four neighbors: topLeft=(0,0), top=(1,0),
        // topRight=(2,0), left=(0,1). Only pixel (1,1) carries a nonzero residual and its tile is the only
        // one whose mode actually matters.
        var transform = MakeTransform(width: 3, height: 3, bits: 0, tileData: [9, 9, 9, 9, mode, 9, 9, 9, 9]);

        uint[] pixels =
        [
            Pack(0, 1, 2, 3), Pack(0, 40, 50, 60), Pack(0, 7, 8, 9), // row 0
            Pack(0, 0, 0, 0), Pack(0, 90, 10, 20), Pack(0, 0, 0, 0), // row 1 -- only (1,1)'s residual matters
            Pack(0, 0, 0, 0), Pack(0, 0, 0, 0), Pack(0, 0, 0, 0), // row 2
        ];

        Vp8LPredictorTransform.ApplyInverse(pixels, transform);

        uint topLeft = Pack(255, 1, 2, 3); // pixel (0,0): mode 0 (black) + zero residual.
        uint top = Pack(255, 41, 52, 63); // pixel (1,0): mode 1 (left=topLeft) + residual (0,40,50,60).
        uint topRight = Pack(255, 48, 60, 72); // pixel (2,0): mode 1 (left=top) + residual (0,7,8,9).
        uint left = Pack(255, 1, 2, 3); // pixel (0,1): mode 2 (top=topLeft) + zero residual.

        uint predicted = ReferencePredict(mode, left, top, topLeft, topRight);
        uint expected = AddWrappingReference(Pack(0, 90, 10, 20), predicted);

        Assert.Equal(expected, pixels[4]); // pixel (1,1) is flat index (1*3)+1=4.
    }

    [Fact]
    public void PredictorTopKernelTiers_AgreeOnRandomInput()
    {
        var random = new Random(2024);
        byte[] row = new byte[73]; // not a multiple of 16/32 -- exercises each kernel's scalar remainder loop too.
        byte[] top = new byte[73];
        random.NextBytes(row);
        random.NextBytes(top);

        byte[] scalarResult = (byte[])row.Clone();
        new ScalarVp8LTransformKernel().PredictorTopInverse(scalarResult, top);

        byte[] vector128Result = (byte[])row.Clone();
        new Vector128Vp8LTransformKernel().PredictorTopInverse(vector128Result, top);

        byte[] vector256Result = (byte[])row.Clone();
        new Vector256Vp8LTransformKernel().PredictorTopInverse(vector256Result, top);

        Assert.Equal(scalarResult, vector128Result);
        Assert.Equal(scalarResult, vector256Result);
    }

    // Independent (separately hand-typed, not copy-pasted from the production implementation) reference math.

    private static uint ReferencePredict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(top, left, topLeft),
        12 => ClampedAddSubtractFull(left, top, topLeft),
        13 => ClampedAddSubtractHalf(left, top, topLeft),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static uint Average2(uint a, uint b) => PerChannel(a, b, 0, (ca, cb, _) => (ca + cb) / 2);

    private static uint Select(uint a, uint b, uint c)
    {
        int score = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = (int)((a >> shift) & 0xFF);
            int cb = (int)((b >> shift) & 0xFF);
            int cc = (int)((c >> shift) & 0xFF);
            score += Math.Abs(cb - cc) - Math.Abs(ca - cc);
        }

        return score <= 0 ? a : b;
    }

    private static uint ClampedAddSubtractFull(uint a, uint b, uint c) =>
        PerChannel(a, b, c, (ca, cb, cc) => Math.Clamp(ca + cb - cc, 0, 255));

    private static uint ClampedAddSubtractHalf(uint a, uint b, uint c)
    {
        uint average = Average2(a, b);
        return PerChannel(average, c, 0, (avg, cc, _) => Math.Clamp(avg + ((avg - cc) / 2), 0, 255));
    }

    private static uint AddWrappingReference(uint a, uint b) => PerChannel(a, b, 0, (ca, cb, _) => (ca + cb) & 0xFF);

    private static uint PerChannel(uint a, uint b, uint c, Func<int, int, int, int> op)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = (int)((a >> shift) & 0xFF);
            int cb = (int)((b >> shift) & 0xFF);
            int cc = (int)((c >> shift) & 0xFF);
            result |= (uint)(op(ca, cb, cc) & 0xFF) << shift;
        }

        return result;
    }

    private static uint Pack(int a, int r, int g, int b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    private static Vp8LTransform MakeTransform(int width, int height, int bits, int[] tileData) =>
        new()
        {
            Type = Vp8LTransformType.Predictor,
            Xsize = width,
            Ysize = height,
            Bits = bits,
            Data = Array.ConvertAll(tileData, mode => (uint)mode << 8),
        };
}
