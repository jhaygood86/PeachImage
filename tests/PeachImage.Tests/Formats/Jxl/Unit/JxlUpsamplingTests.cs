using PeachImage.Formats.Jxl.Features;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlUpsamplingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefaultKernels_PreserveFlatAreas(int shift)
    {
        float[] kernels = JxlUpsampling.BuildKernels(shift, JxlUpsampling.DefaultWeights(shift));
        int n = 1 << shift;
        for (int k = 0; k < n * n; k++)
        {
            float sum = 0;
            for (int i = 0; i < 25; i++)
            {
                sum += kernels[(k * 25) + i];
            }

            Assert.Equal(1f, sum, 3);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Upsample_ConstantPlaneStaysConstant_AndSizesGrow(int shift)
    {
        const int width = 7;
        const int height = 5;
        var source = new float[width * height];
        Array.Fill(source, 0.25f);
        float[] kernels = JxlUpsampling.BuildKernels(shift, JxlUpsampling.DefaultWeights(shift));

        float[] result = JxlUpsampling.Upsample(source, width, width, height, shift, kernels, width << shift);

        Assert.Equal((width << shift) * (height << shift), result.Length);
        Assert.All(result, v => Assert.Equal(0.25f, v, 4));
    }

    [Fact]
    public void Upsample_NeverOvershootsTheInputRange()
    {
        // A hard step edge would ring with the negative lobes of the kernels; the clamp to the 5x5 range must prevent it.
        const int width = 16;
        const int height = 16;
        var source = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 8; x < width; x++)
            {
                source[(y * width) + x] = 1f;
            }
        }

        float[] kernels = JxlUpsampling.BuildKernels(1, JxlUpsampling.DefaultWeights2);
        float[] result = JxlUpsampling.Upsample(source, width, width, height, 1, kernels, width * 2);

        Assert.All(result, v => Assert.InRange(v, 0f, 1f));
    }

    [Theory]
    [InlineData(1, 5, 3)]
    [InlineData(1, 40, 17)]
    [InlineData(2, 33, 9)]
    [InlineData(3, 70, 6)]
    public void VectorizedUpsampleMatchesScalar(int shift, int width, int height)
    {
        var random = new Random(shift * 100 + width);
        int stride = width + 3;
        var source = new float[stride * height];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (float)random.NextDouble();
        }

        float[] kernels = JxlUpsampling.BuildKernels(shift, JxlUpsampling.DefaultWeights(shift));
        int dstStride = (width << shift) + 2;

        float[] expected = JxlUpsampling.Upsample(source, stride, width, height, shift, kernels, dstStride, vectorized: false);
        float[] actual = JxlUpsampling.Upsample(source, stride, width, height, shift, kernels, dstStride, vectorized: true);

        Assert.Equal(expected, actual);
    }
}
