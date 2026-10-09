using PeachImage.Formats.Jxl.Features;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlNoiseTests
{
    private static float[][] Planes(int stride, int height, int seed)
    {
        var random = new Random(seed);
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new float[stride * height];
            for (int i = 0; i < planes[c].Length; i++)
            {
                planes[c][i] = (float)(random.NextDouble() * 1.1);
            }
        }

        return planes;
    }

    [Theory]
    [InlineData(37, 29, 16)]
    [InlineData(300, 170, 256)]
    [InlineData(9, 9, 256)]
    public void VectorizedNoiseMatchesScalar(int width, int height, int groupDim)
    {
        var parameters = new JxlNoiseParams();
        for (int i = 0; i < parameters.Lut.Length; i++)
        {
            parameters.Lut[i] = 0.05f + (i * 0.03f);
        }

        int stride = width + 3;
        var expected = Planes(stride, height, 4);
        var actual = Planes(stride, height, 4);

        JxlNoise.Add(parameters, expected, stride, width, height, groupDim, 0.1f, 1f, 1, 0, vectorized: false);
        JxlNoise.Add(parameters, actual, stride, width, height, groupDim, 0.1f, 1f, 1, 0, vectorized: true);

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                Assert.Equal(expected[c].AsSpan(y * stride, width).ToArray(), actual[c].AsSpan(y * stride, width).ToArray());
            }
        }
    }

    [Fact]
    public void NoiseIsDeterministicAndDependsOnTheFrameIndices()
    {
        var parameters = new JxlNoiseParams();
        Array.Fill(parameters.Lut, 0.2f);
        var a = Planes(64, 64, 9);
        var b = Planes(64, 64, 9);
        var c = Planes(64, 64, 9);

        JxlNoise.Add(parameters, a, 64, 64, 64, 256, 0f, 1f, 1, 0);
        JxlNoise.Add(parameters, b, 64, 64, 64, 256, 0f, 1f, 1, 0);
        JxlNoise.Add(parameters, c, 64, 64, 64, 256, 0f, 1f, 2, 0);

        Assert.Equal(a[0], b[0]);
        Assert.NotEqual(a[0], c[0]);
    }
}
