using System.Numerics;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlColorKernelTests
{
    [Fact]
    public void VectorPow_MatchesMathPow()
    {
        var random = new Random(3);
        foreach (float exponent in new[] { 1f / 2.4f, 0.45f, 1f / 2.6f, 2.4f, 0.5f })
        {
            var input = new float[Vector<float>.Count];
            for (int trial = 0; trial < 2000; trial++)
            {
                for (int i = 0; i < input.Length; i++)
                {
                    input[i] = (float)Math.Pow(10, -6 + (random.NextDouble() * 6.3));
                }

                var result = VectorMath.Pow(new Vector<float>(input), exponent);
                for (int i = 0; i < input.Length; i++)
                {
                    double expected = Math.Pow(input[i], exponent);
                    Assert.True(Math.Abs(result[i] - expected) <= 3e-6 * expected, $"{input[i]}^{exponent}: {result[i]} vs {expected}");
                }
            }
        }
    }

    private static float[][] RandomPlanes(int stride, int height, int seed, float low, float high)
    {
        var random = new Random(seed);
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new float[stride * height];
            for (int i = 0; i < planes[c].Length; i++)
            {
                planes[c][i] = low + (float)(random.NextDouble() * (high - low));
            }
        }

        return planes;
    }

    [Theory]
    [InlineData((int)JxlTransferFunction.Srgb, 0u)]
    [InlineData((int)JxlTransferFunction.Bt709, 0u)]
    [InlineData((int)JxlTransferFunction.Dci, 0u)]
    [InlineData((int)JxlTransferFunction.Srgb, 4_545_454u)]
    public void TransferKernel_MatchesScalarReference(int function, uint gamma)
    {
        const int width = 37;
        const int height = 9;
        const int stride = 41;
        var encoding = JxlColorEncoding.Create((JxlTransferFunction)function, gamma);
        var expected = RandomPlanes(stride, height, 1, -0.2f, 1.3f);
        var actual = RandomPlanes(stride, height, 1, -0.2f, 1.3f);

        FramePostProcessing.LinearToTransferReference(expected, stride, width, height, encoding);
        ColorKernels.LinearToTransfer(actual, stride, width, height, encoding);

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * stride) + x;
                    Assert.True(Math.Abs(expected[c][i] - actual[c][i]) <= 2e-6f, $"{expected[c][i]} vs {actual[c][i]}");
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XybKernel_MatchesScalarReference(bool gray)
    {
        const int width = 53;
        const int height = 7;
        const int stride = 64;
        var transform = new JxlCustomTransformData();
        var expected = RandomPlanes(stride, height, 2, 0f, 1f);
        var actual = RandomPlanes(stride, height, 2, 0f, 1f);

        FramePostProcessing.XybToLinearRgbReference(expected, stride, width, height, transform, 255f, gray);
        ColorKernels.XybToLinearRgb(actual, stride, width, height, transform, 255f, gray);

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                Assert.Equal(expected[c].AsSpan((y * stride), width).ToArray(), actual[c].AsSpan(y * stride, width).ToArray());
            }
        }
    }
}
