using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Tests.Resizing;

public class ResamplingWeightMapTests
{
    public static IEnumerable<object[]> NonNearestFilters()
    {
        foreach (ResamplingFilter filter in Enum.GetValues<ResamplingFilter>())
        {
            if (filter != ResamplingFilter.NearestNeighbor)
            {
                yield return [filter];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NonNearestFilters))]
    public void EveryWindow_WeightsSumToOne(ResamplingFilter filter)
    {
        var kernel = ResamplingKernelFactory.Create(filter);
        foreach (var (sourceSize, destinationSize) in new[] { (100, 20), (20, 100), (7, 7), (1, 5), (5, 1) })
        {
            var map = new ResamplingWeightMap(sourceSize, destinationSize, kernel);
            for (int i = 0; i < destinationSize; i++)
            {
                double sum = 0.0;
                foreach (float w in map.GetWeights(i))
                {
                    sum += w;
                }

                Assert.True(Math.Abs(sum - 1.0) < 1e-4, $"{filter} src={sourceSize} dst={destinationSize} index={i}: sum={sum}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(NonNearestFilters))]
    public void EveryWindow_StaysWithinSourceBounds(ResamplingFilter filter)
    {
        var kernel = ResamplingKernelFactory.Create(filter);
        foreach (var (sourceSize, destinationSize) in new[] { (100, 20), (20, 100), (7, 7), (1, 5), (5, 1) })
        {
            var map = new ResamplingWeightMap(sourceSize, destinationSize, kernel);
            for (int i = 0; i < destinationSize; i++)
            {
                Assert.True(map.Starts[i] >= 0, $"{filter}: Starts[{i}] = {map.Starts[i]} < 0");
                int end = map.Starts[i] + map.GetWeights(i).Length - 1;
                Assert.True(end < sourceSize, $"{filter}: window end {end} >= sourceSize {sourceSize}");
            }
        }
    }

    [Fact]
    public void Downscaling_WidensWindow_ComparedToUpscaling()
    {
        // The "scaled filter" anti-aliasing technique: a downscale's window should cover more source taps
        // than an upscale's, for the same kernel — without this widening, minification aliases.
        var kernel = ResamplingKernelFactory.Create(ResamplingFilter.Lanczos3);

        var downscale = new ResamplingWeightMap(1000, 100, kernel); // 10x downscale
        var upscale = new ResamplingWeightMap(100, 1000, kernel); // 10x upscale

        int midDownscale = downscale.GetWeights(50).Length;
        int midUpscale = upscale.GetWeights(500).Length;

        Assert.True(midDownscale > midUpscale, $"downscale window {midDownscale} should exceed upscale window {midUpscale}");
    }

    [Fact]
    public void FirstAndLastDestinationIndex_ClampToSourceEdges()
    {
        var kernel = ResamplingKernelFactory.Create(ResamplingFilter.Lanczos5);
        var map = new ResamplingWeightMap(10, 40, kernel);

        Assert.Equal(0, map.Starts[0]);
        int lastIndex = map.DestinationSize - 1;
        int lastEnd = map.Starts[lastIndex] + map.GetWeights(lastIndex).Length - 1;
        Assert.Equal(9, lastEnd);
    }

    [Fact]
    public void SingleSourcePixel_ProducesTrivialWindow()
    {
        var kernel = ResamplingKernelFactory.Create(ResamplingFilter.Bicubic);
        var map = new ResamplingWeightMap(1, 5, kernel);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, map.Starts[i]);
            var weights = map.GetWeights(i);
            Assert.Equal(1, weights.Length);
            Assert.Equal(1.0f, weights[0], precision: 4);
        }
    }
}
