using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Bmp;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world BMP decoder — the same library used as
/// the corpus tests' differential oracle). The acceptance bar (see the project plan) is PeachImage's Mean
/// within 10% of SkiaSharp's Mean for every scenario below.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BmpDecodeBenchmarks
{
    private byte[] _truecolor24Bpp = null!;
    private byte[] _alpha32Bpp = null!;
    private byte[] _indexed8Bpp = null!;
    private byte[] _indexed8BppRle = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _truecolor24Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_24bpp.bmp"));
        _alpha32Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_32bpp_alpha.bmp"));
        _indexed8Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_8bpp_indexed.bmp"));
        _indexed8BppRle = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_8bpp_rle.bmp"));
    }

    [Benchmark]
    [BenchmarkCategory("24bpp-Truecolor")]
    public Image PeachImage_Decode_24bpp()
    {
        using var stream = new MemoryStream(_truecolor24Bpp);
        return BmpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("24bpp-Truecolor")]
    public SKBitmap SkiaSharp_Decode_24bpp() => SKBitmap.Decode(_truecolor24Bpp)!;

    [Benchmark]
    [BenchmarkCategory("32bpp-Alpha")]
    public Image PeachImage_Decode_32bppAlpha()
    {
        using var stream = new MemoryStream(_alpha32Bpp);
        return BmpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("32bpp-Alpha")]
    public SKBitmap SkiaSharp_Decode_32bppAlpha() => SKBitmap.Decode(_alpha32Bpp)!;

    [Benchmark]
    [BenchmarkCategory("8bpp-Indexed")]
    public Image PeachImage_Decode_8bppIndexed()
    {
        using var stream = new MemoryStream(_indexed8Bpp);
        return BmpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("8bpp-Indexed")]
    public SKBitmap SkiaSharp_Decode_8bppIndexed() => SKBitmap.Decode(_indexed8Bpp)!;

    [Benchmark]
    [BenchmarkCategory("8bpp-Indexed-RLE")]
    public Image PeachImage_Decode_8bppIndexedRle()
    {
        using var stream = new MemoryStream(_indexed8BppRle);
        return BmpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("8bpp-Indexed-RLE")]
    public SKBitmap SkiaSharp_Decode_8bppIndexedRle() => SKBitmap.Decode(_indexed8BppRle)!;
}
