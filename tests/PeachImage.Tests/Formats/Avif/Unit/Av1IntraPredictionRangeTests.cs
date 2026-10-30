using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Every intra prediction must stay inside the sample range. The edge upsample filter (-1, 9, 9, -1) / 16
/// overshoots on high-contrast edges unless its output is clipped (spec section 7.11.2.11), and storing the
/// unclipped value in a narrow sample plane would wrap it. Regression test for exactly that: the decoder used to
/// let such predictions through and only agreed with the bitstream's own residual by accident.
/// </summary>
public class Av1IntraPredictionRangeTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void DirectionalPrediction_StaysInSampleRange_OnHighContrastEdges(int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        var random = new Random(7);
        const int size = 32;
        var plane = new int[size * size];
        var pred = new int[64 * 64];
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);

        int[][] shapes = [[2, 2], [3, 2], [2, 3], [3, 3]];
        int checkedBlocks = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = random.Next(2) == 0 ? 0 : max;
            }

            foreach (int[] shape in shapes)
            {
                int w = 1 << shape[0];
                int h = 1 << shape[1];
                for (int mode = Av1IntraMode.VPred; mode <= Av1IntraMode.D67Pred; mode++)
                {
                    for (int delta = -3; delta <= 3; delta++)
                    {
                        Av1IntraPrediction.BuildEdges(above, left, plane, size, 8, 8, w, h, true, true, true, true, size - 1, size - 1, bitDepth);
                        Av1IntraPrediction.Predict(
                            pred, w, h, shape[0], shape[1], above, left, mode, true, true,
                            useFilterIntra: false, filterIntraMode: 0, delta, enableIntraEdgeFilter: true, filterTypeSmooth: trial % 2 == 0,
                            size - 1, size - 1, 8, 8, bitDepth);

                        for (int i = 0; i < w * h; i++)
                        {
                            Assert.InRange(pred[i], 0, max);
                        }

                        checkedBlocks++;
                    }
                }
            }
        }

        Assert.True(checkedBlocks > 0);
    }
}
