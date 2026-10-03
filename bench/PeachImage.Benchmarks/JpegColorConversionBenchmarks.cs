using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg.ColorConversion;

namespace PeachImage.Benchmarks;

/// <summary>
/// Isolated JPEG color-converter throughput per tier, bypassing <c>ColorConverterSelector</c>. Each call converts
/// 1920 x 64 pixels, about the working set a real decode drives through the converter per row partition.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JpegColorConversionBenchmarks
{
    private const int Pixels = 1920 * 64;

    private static readonly ScalarColorConverter Scalar = new ScalarColorConverter();
    private static readonly Vector128ColorConverter Vector128Tier = new Vector128ColorConverter();

    private byte[] _y = null!, _cb = null!, _cr = null!, _k = null!, _rgb = null!, _cmyk = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(3);
        _y = new byte[Pixels];
        _cb = new byte[Pixels];
        _cr = new byte[Pixels];
        _k = new byte[Pixels];
        _rgb = new byte[Pixels * 3];
        _cmyk = new byte[Pixels * 4];
        rng.NextBytes(_y);
        rng.NextBytes(_cb);
        rng.NextBytes(_cr);
        rng.NextBytes(_k);
        rng.NextBytes(_rgb);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("YCbCrToRgb")]
    public void ScalarYCbCrToRgb() => Scalar.YCbCrToRgb(_y, _cb, _cr, _rgb, Pixels);

    [Benchmark, BenchmarkCategory("YCbCrToRgb")]
    public void Vector128YCbCrToRgb() => Vector128Tier.YCbCrToRgb(_y, _cb, _cr, _rgb, Pixels);

    [Benchmark(Baseline = true), BenchmarkCategory("YcckToCmyk")]
    public void ScalarYcckToCmyk() => Scalar.YcckToCmyk(_y, _cb, _cr, _k, _cmyk, Pixels);

    [Benchmark, BenchmarkCategory("YcckToCmyk")]
    public void Vector128YcckToCmyk() => Vector128Tier.YcckToCmyk(_y, _cb, _cr, _k, _cmyk, Pixels);

    [Benchmark(Baseline = true), BenchmarkCategory("RgbToYCbCr")]
    public void ScalarRgbToYCbCr() => Scalar.RgbToYCbCr(_rgb, _y, _cb, _cr, Pixels);

    [Benchmark, BenchmarkCategory("RgbToYCbCr")]
    public void Vector128RgbToYCbCr() => Vector128Tier.RgbToYCbCr(_rgb, _y, _cb, _cr, Pixels);
}
