using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlRestorationFilterTests
{
    private static float[][] RandomPlanes(int stride, int height, int seed)
    {
        var random = new Random(seed);
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = System.Buffers.ArrayPool<float>.Shared.Rent(stride * height);
            Array.Clear(planes[c]);
            for (int i = 0; i < planes[c].Length; i++)
            {
                // Smooth-ish content with noise, so that EPF weights span their whole range.
                planes[c][i] = (float)((0.5 * Math.Sin(i * 0.013)) + (0.05 * random.NextDouble()));
            }
        }

        return planes;
    }

    private static float[] RandomSigma(int blocksX, int blocksY, int seed)
    {
        var random = new Random(seed);
        var sigma = new float[blocksX * blocksY];
        for (int i = 0; i < sigma.Length; i++)
        {
            // Mostly active filters, some below the "skip" threshold.
            sigma[i] = random.Next(5) == 0 ? -4.5f : -(0.05f + (float)(random.NextDouble() * 3));
        }

        return sigma;
    }

    [Theory]
    [InlineData(37, 29, 1)]
    [InlineData(64, 64, 2)]
    [InlineData(131, 77, 3)]
    [InlineData(9, 200, 3)]
    [InlineData(256, 11, 2)]
    public void VectorizedAndScalarPathsAgreeWithTheReference(int width, int height, int epfIterations)
    {
        int stride = width + 5;
        var filter = JxlLoopFilter.Create(gaborish: true, epfIterations);
        int blocksX = (width + 7) / 8;
        int blocksY = (height + 7) / 8;
        float[] sigma = RandomSigma(blocksX, blocksY, 11);

        // Reference: the straightforward whole-frame scalar implementation.
        var expected = RandomPlanes(stride, height, 5);
        FramePostProcessing.GaborishReference(expected, stride, width, height, filter);
        FramePostProcessing.EdgePreservingFilterReference(expected, stride, width, height, filter, sigma, blocksX);

        foreach (bool vectorized in new[] { false, true })
        {
            var actual = RandomPlanes(stride, height, 5);
            RestorationFilters.Apply(actual, stride, width, height, filter, sigma, blocksX, vectorized);
            for (int c = 0; c < 3; c++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Assert.True(expected[c][(y * stride) + x] == actual[c][(y * stride) + x], $"vectorized={vectorized} c={c} x={x} y={y}: {expected[c][(y * stride) + x]} vs {actual[c][(y * stride) + x]}");
                    }
                }
            }
        }
    }
}
