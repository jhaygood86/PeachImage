using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Tests.Resizing;

/// <summary>
/// Verifies <see cref="Vector128ResamplingConvolver"/> and <see cref="Vector256ResamplingConvolver"/> against
/// each other and against an independent, non-vectorized reference implementation (not a call into either
/// production tier) — the same shape as <c>TriangleFilterUpsamplerTests</c>' independent-reference approach,
/// across widths/heights deliberately including non-multiples of 4 and 8 (the tiers' scalar remainder
/// handling is the part most likely to be wrong).
/// </summary>
public class ResamplingConvolverDifferentialTests
{
    private const int Stride = ImageResizer.FloatsPerPixel;

    [Theory]
    [InlineData(1, 5, 1, 5)]
    [InlineData(3, 5, 3, 5)]
    [InlineData(4, 5, 4, 5)]
    [InlineData(7, 5, 2, 5)]
    [InlineData(8, 5, 20, 5)]
    [InlineData(9, 5, 17, 5)]
    [InlineData(16, 6, 4, 6)]
    [InlineData(17, 6, 40, 6)]
    [InlineData(11, 80, 7, 80)] // height (80) exceeds ResamplingParallel.MinRowsForParallel (64): exercises the Parallel.For branch, not just the sequential fallback every other case above stays under.
    public void ConvolveHorizontal_AllTiersAgree_WithIndependentReference(int sourceWidth, int height, int destWidth, int seedHeight)
    {
        var random = new Random((sourceWidth * 1000) + destWidth + seedHeight);
        float[] source = RandomBuffer(random, sourceWidth * height);
        var map = new ResamplingWeightMap(sourceWidth, destWidth, ResamplingKernelFactory.Create(ResamplingFilter.Lanczos3));

        float[] vector128Result = new float[destWidth * height * Stride];
        new Vector128ResamplingConvolver().ConvolveHorizontal(source, sourceWidth, height, vector128Result, map);

        float[] vector256Result = new float[destWidth * height * Stride];
        new Vector256ResamplingConvolver().ConvolveHorizontal(source, sourceWidth, height, vector256Result, map);

        float[] reference = ReferenceConvolveHorizontal(source, sourceWidth, height, destWidth, map);

        AssertClose(reference, vector128Result);
        AssertClose(reference, vector256Result);
    }

    [Theory]
    [InlineData(1, 5, 1, 5)]
    [InlineData(3, 5, 3, 5)]
    [InlineData(4, 5, 4, 5)]
    [InlineData(7, 5, 2, 5)]
    [InlineData(8, 5, 20, 5)]
    [InlineData(9, 5, 17, 5)]
    [InlineData(16, 6, 4, 6)]
    [InlineData(17, 6, 40, 6)]
    [InlineData(11, 80, 70, 80)] // destHeight (70) exceeds ResamplingParallel.MinRowsForParallel (64): exercises the Parallel.For branch.
    public void ConvolveVertical_AllTiersAgree_WithIndependentReference(int width, int sourceHeight, int destHeight, int seedWidth)
    {
        var random = new Random((width * 1000) + destHeight + seedWidth + 7);
        float[] source = RandomBuffer(random, width * sourceHeight);
        var map = new ResamplingWeightMap(sourceHeight, destHeight, ResamplingKernelFactory.Create(ResamplingFilter.MitchellNetravali));

        float[] vector128Result = new float[width * destHeight * Stride];
        new Vector128ResamplingConvolver().ConvolveVertical(source, width, sourceHeight, vector128Result, map);

        float[] vector256Result = new float[width * destHeight * Stride];
        new Vector256ResamplingConvolver().ConvolveVertical(source, width, sourceHeight, vector256Result, map);

        float[] reference = ReferenceConvolveVertical(source, width, sourceHeight, destHeight, map);

        AssertClose(reference, vector128Result);
        AssertClose(reference, vector256Result);
    }

    private static float[] RandomBuffer(Random random, int pixelCount)
    {
        var buffer = new float[pixelCount * Stride];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)(random.NextDouble() * 255.0);
        }

        return buffer;
    }

    private static float[] ReferenceConvolveHorizontal(float[] source, int sourceWidth, int height, int destWidth, ResamplingWeightMap map)
    {
        var result = new float[destWidth * height * Stride];
        for (int y = 0; y < height; y++)
        {
            for (int destX = 0; destX < destWidth; destX++)
            {
                int start = map.Starts[destX];
                var weights = map.GetWeights(destX);
                double[] accumulator = new double[Stride];

                for (int k = 0; k < weights.Length; k++)
                {
                    int sourceOffset = ((y * sourceWidth) + start + k) * Stride;
                    for (int c = 0; c < Stride; c++)
                    {
                        accumulator[c] += source[sourceOffset + c] * (double)weights[k];
                    }
                }

                int destOffset = ((y * destWidth) + destX) * Stride;
                for (int c = 0; c < Stride; c++)
                {
                    result[destOffset + c] = (float)accumulator[c];
                }
            }
        }

        return result;
    }

    private static float[] ReferenceConvolveVertical(float[] source, int width, int sourceHeight, int destHeight, ResamplingWeightMap map)
    {
        var result = new float[width * destHeight * Stride];
        for (int destY = 0; destY < destHeight; destY++)
        {
            int start = map.Starts[destY];
            var weights = map.GetWeights(destY);

            for (int x = 0; x < width; x++)
            {
                double[] accumulator = new double[Stride];
                for (int k = 0; k < weights.Length; k++)
                {
                    int sourceOffset = (((start + k) * width) + x) * Stride;
                    for (int c = 0; c < Stride; c++)
                    {
                        accumulator[c] += source[sourceOffset + c] * (double)weights[k];
                    }
                }

                int destOffset = ((destY * width) + x) * Stride;
                for (int c = 0; c < Stride; c++)
                {
                    result[destOffset + c] = (float)accumulator[c];
                }
            }
        }

        return result;
    }

    private static void AssertClose(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) < 0.01f, $"index {i}: expected {expected[i]}, got {actual[i]}");
        }
    }
}
