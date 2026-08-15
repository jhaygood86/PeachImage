using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world JPEG decoder — the same library used
/// as the Bmp/Png benchmarks' baseline and the corpus tests' differential oracle, so all three formats
/// compare against a single consistent library). The acceptance bar (see the project plan) is
/// PeachImage's Mean within 10% of SkiaSharp's Mean for every scenario below.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JpegDecodeBenchmarks
{
    private byte[] _photo1080p420 = null!;
    private byte[] _photo1080p444 = null!;
    private byte[] _photo12Mp420 = null!;
    private byte[] _grayscale1080p = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _photo1080p420 = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_420.jpg"));
        _photo1080p444 = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_444.jpg"));
        _photo12Mp420 = File.ReadAllBytes(Path.Combine(assetsDir, "photo_4032x3024_420.jpg"));
        _grayscale1080p = File.ReadAllBytes(Path.Combine(assetsDir, "gray_1920x1080.jpg"));
    }

    [Benchmark]
    [BenchmarkCategory("1080p-4:2:0")]
    public Image PeachImage_Decode_1080p_420()
    {
        using var stream = new MemoryStream(_photo1080p420);
        return new JpegDecoder().Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:2:0")]
    public SKBitmap SkiaSharp_Decode_1080p_420() => SKBitmap.Decode(_photo1080p420)!;

    [Benchmark]
    [BenchmarkCategory("1080p-4:4:4")]
    public Image PeachImage_Decode_1080p_444()
    {
        using var stream = new MemoryStream(_photo1080p444);
        return new JpegDecoder().Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:4:4")]
    public SKBitmap SkiaSharp_Decode_1080p_444() => SKBitmap.Decode(_photo1080p444)!;

    [Benchmark]
    [BenchmarkCategory("12MP-4:2:0")]
    public Image PeachImage_Decode_12Mp_420()
    {
        using var stream = new MemoryStream(_photo12Mp420);
        return new JpegDecoder().Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("12MP-4:2:0")]
    public SKBitmap SkiaSharp_Decode_12Mp_420() => SKBitmap.Decode(_photo12Mp420)!;

    [Benchmark]
    [BenchmarkCategory("1080p-Grayscale")]
    public Image PeachImage_Decode_Grayscale()
    {
        using var stream = new MemoryStream(_grayscale1080p);
        return new JpegDecoder().Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-Grayscale")]
    public SKBitmap SkiaSharp_Decode_Grayscale() => SKBitmap.Decode(_grayscale1080p)!;
}
