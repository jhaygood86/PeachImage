using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Webp;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world WebP decoder backed by libwebp itself —
/// the same library used as the corpus tests' differential oracle). The acceptance bar (see the project plan)
/// is PeachImage's Mean within 10% of SkiaSharp's Mean for every scenario below. Covers both of WebP's
/// unrelated bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha, plus a small-image scenario
/// to surface fixed per-decode overhead separately from throughput on large images.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WebpDecodeBenchmarks
{
    private byte[] _losslessPhotographic = null!;
    private byte[] _lossyPhotographic = null!;
    private byte[] _losslessGraphic = null!;
    private byte[] _lossyWithAlpha = null!;
    private byte[] _losslessWithAlpha = null!;
    private byte[] _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _losslessPhotographic = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lossless.webp"));
        _lossyPhotographic = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lossy_q80.webp"));
        _losslessGraphic = File.ReadAllBytes(Path.Combine(assetsDir, "graphic_640x480_lossless.webp"));
        _lossyWithAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossy_q80.webp"));
        _losslessWithAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossless.webp"));
        _small = File.ReadAllBytes(Path.Combine(assetsDir, "small_32x24_lossless.webp"));
    }

    [Benchmark]
    [BenchmarkCategory("Lossless-Photographic")]
    public Image PeachImage_Decode_LosslessPhotographic()
    {
        using var stream = new MemoryStream(_losslessPhotographic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Photographic")]
    public SKBitmap SkiaSharp_Decode_LosslessPhotographic() => SKBitmap.Decode(_losslessPhotographic)!;

    [Benchmark]
    [BenchmarkCategory("Lossy-Photographic")]
    public Image PeachImage_Decode_LossyPhotographic()
    {
        using var stream = new MemoryStream(_lossyPhotographic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy-Photographic")]
    public SKBitmap SkiaSharp_Decode_LossyPhotographic() => SKBitmap.Decode(_lossyPhotographic)!;

    [Benchmark]
    [BenchmarkCategory("Lossless-Graphic")]
    public Image PeachImage_Decode_LosslessGraphic()
    {
        using var stream = new MemoryStream(_losslessGraphic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Graphic")]
    public SKBitmap SkiaSharp_Decode_LosslessGraphic() => SKBitmap.Decode(_losslessGraphic)!;

    [Benchmark]
    [BenchmarkCategory("Lossy-Alpha")]
    public Image PeachImage_Decode_LossyAlpha()
    {
        using var stream = new MemoryStream(_lossyWithAlpha);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy-Alpha")]
    public SKBitmap SkiaSharp_Decode_LossyAlpha() => SKBitmap.Decode(_lossyWithAlpha)!;

    [Benchmark]
    [BenchmarkCategory("Lossless-Alpha")]
    public Image PeachImage_Decode_LosslessAlpha()
    {
        using var stream = new MemoryStream(_losslessWithAlpha);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Alpha")]
    public SKBitmap SkiaSharp_Decode_LosslessAlpha() => SKBitmap.Decode(_losslessWithAlpha)!;

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public Image PeachImage_Decode_Small()
    {
        using var stream = new MemoryStream(_small);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Small-Image")]
    public SKBitmap SkiaSharp_Decode_Small() => SKBitmap.Decode(_small)!;
}
