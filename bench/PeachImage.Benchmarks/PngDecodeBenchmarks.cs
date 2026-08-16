using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Png;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world PNG decoder — the same library used
/// as the corpus tests' differential oracle). The acceptance bar (see the project plan) is PeachImage's
/// Mean within 10% of SkiaSharp's Mean for every scenario below. No 8bpp-Palette scenario is included:
/// PeachImage's PNG encoder doesn't build indexed output (no automatic quantization, per the project
/// plan), and SkiaSharp's simple decode/encode round-trip doesn't preserve a true palette color type
/// either, so there's no readily available palette PNG asset in this environment — deferred until either
/// gap closes.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PngDecodeBenchmarks
{
    private byte[] _truecolor24Bpp = null!;
    private byte[] _rgba32Bpp = null!;
    private byte[] _gray8Bpp = null!;
    private byte[] _interlacedTruecolor = null!;
    private byte[] _truecolor48Bpp = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _truecolor24Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_24bpp.png"));
        _rgba32Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_32bpp_rgba.png"));
        _gray8Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "gray_1920x1080.png"));
        _interlacedTruecolor = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_24bpp_interlaced.png"));
        _truecolor48Bpp = File.ReadAllBytes(Path.Combine(assetsDir, "photo_960x540_48bpp.png"));
    }

    [Benchmark]
    [BenchmarkCategory("24bpp-Truecolor")]
    public Image PeachImage_Decode_24bpp()
    {
        using var stream = new MemoryStream(_truecolor24Bpp);
        return PngDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("24bpp-Truecolor")]
    public SKBitmap SkiaSharp_Decode_24bpp() => SKBitmap.Decode(_truecolor24Bpp)!;

    [Benchmark]
    [BenchmarkCategory("32bpp-RGBA")]
    public Image PeachImage_Decode_32bppRgba()
    {
        using var stream = new MemoryStream(_rgba32Bpp);
        return PngDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("32bpp-RGBA")]
    public SKBitmap SkiaSharp_Decode_32bppRgba() => SKBitmap.Decode(_rgba32Bpp)!;

    [Benchmark]
    [BenchmarkCategory("8bpp-Grayscale")]
    public Image PeachImage_Decode_8bppGrayscale()
    {
        using var stream = new MemoryStream(_gray8Bpp);
        return PngDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("8bpp-Grayscale")]
    public SKBitmap SkiaSharp_Decode_8bppGrayscale() => SKBitmap.Decode(_gray8Bpp)!;

    [Benchmark]
    [BenchmarkCategory("Interlaced-Truecolor")]
    public Image PeachImage_Decode_InterlacedTruecolor()
    {
        using var stream = new MemoryStream(_interlacedTruecolor);
        return PngDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Interlaced-Truecolor")]
    public SKBitmap SkiaSharp_Decode_InterlacedTruecolor() => SKBitmap.Decode(_interlacedTruecolor)!;

    [Benchmark]
    [BenchmarkCategory("48bpp-Truecolor")]
    public Image PeachImage_Decode_48bpp()
    {
        using var stream = new MemoryStream(_truecolor48Bpp);
        return PngDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("48bpp-Truecolor")]
    public SKBitmap SkiaSharp_Decode_48bpp() => SKBitmap.Decode(_truecolor48Bpp)!;
}
