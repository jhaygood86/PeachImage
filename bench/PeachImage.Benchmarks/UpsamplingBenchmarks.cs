using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg.Decoding.Upsampling;

namespace PeachImage.Benchmarks;

/// <summary>
/// Isolated per-tier chroma-upsampling throughput, comparing <see cref="TriangleFilterUpsampler"/>'s
/// existing Vector128 (8-lane) tier against <see cref="Vector256TriangleFilterUpsampler"/>'s AVX2 (16-lane)
/// tier directly, bypassing <c>ChromaUpsamplerSelector</c>. Source dimensions are the 4:2:0 chroma-plane
/// size (half width and height of the luma plane) for the 1080p and 12MP assets
/// <c>JpegDecodeBenchmarks</c> already uses, so these numbers are directly comparable to what a real decode
/// actually drives through this code.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class UpsamplingBenchmarks
{
    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IChromaUpsampler tier side by side.")]
    private static readonly IChromaUpsampler Vector128Upsampler = new TriangleFilterUpsampler();

    [SuppressMessage("Performance", "CA1859", Justification = "Deliberately held as the interface type: these fields exist to compare every IChromaUpsampler tier side by side.")]
    private static readonly IChromaUpsampler Vector256Upsampler = new Vector256TriangleFilterUpsampler();

    private byte[] _source1080p = null!;
    private byte[] _destination1080p = null!;
    private byte[] _source12Mp = null!;
    private byte[] _destination12Mp = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);

        (_source1080p, _destination1080p) = MakePlanes(random, sourceWidth: 960, sourceHeight: 540);
        (_source12Mp, _destination12Mp) = MakePlanes(random, sourceWidth: 2016, sourceHeight: 1512);
    }

    private static (byte[] Source, byte[] Destination) MakePlanes(Random random, int sourceWidth, int sourceHeight)
    {
        var source = new byte[sourceWidth * sourceHeight];
        random.NextBytes(source);
        var destination = new byte[sourceWidth * 2 * sourceHeight * 2];
        return (source, destination);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("420-1080p")]
    public void Vector128_1080p() =>
        Vector128Upsampler.Upsample(_source1080p, 960, 540, _destination1080p, 1920, 1080, horizontalRatio: 2, verticalRatio: 2);

    [Benchmark]
    [BenchmarkCategory("420-1080p")]
    public void Vector256_1080p() =>
        Vector256Upsampler.Upsample(_source1080p, 960, 540, _destination1080p, 1920, 1080, horizontalRatio: 2, verticalRatio: 2);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("420-12MP")]
    public void Vector128_12Mp() =>
        Vector128Upsampler.Upsample(_source12Mp, 2016, 1512, _destination12Mp, 4032, 3024, horizontalRatio: 2, verticalRatio: 2);

    [Benchmark]
    [BenchmarkCategory("420-12MP")]
    public void Vector256_12Mp() =>
        Vector256Upsampler.Upsample(_source12Mp, 2016, 1512, _destination12Mp, 4032, 3024, horizontalRatio: 2, verticalRatio: 2);
}
