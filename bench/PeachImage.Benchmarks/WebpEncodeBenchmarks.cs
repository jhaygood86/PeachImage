using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Webp;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// WebP encode throughput: PeachImage vs. SkiaSharp's WebP encoder (backed by libwebp itself), for both
/// lossless (VP8L) and lossy (VP8) output. The lossless benchmarks cover photographic, alpha, and
/// graphic/palette content, plus a small-image scenario to surface fixed per-encode overhead separately from
/// throughput on large images; the lossy benchmarks reuse the same photographic source.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WebpEncodeBenchmarks
{
    private static readonly SKWebpEncoderOptions LosslessSkiaOptions = new(SKWebpEncoderCompression.Lossless, 100);
    private static readonly SKWebpEncoderOptions LossySkiaOptions = new(SKWebpEncoderCompression.Lossy, 75);

    private Image _losslessPhotographicSource = null!;
    private Image _losslessAlphaSource = null!;
    private Image _losslessGraphicSource = null!;
    private Image _smallSource = null!;

    private SKBitmap _losslessPhotographicSkiaSource = null!;
    private SKBitmap _losslessAlphaSkiaSource = null!;
    private SKBitmap _losslessGraphicSkiaSource = null!;
    private SKBitmap _smallSkiaSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

        _losslessPhotographicSource = Decode(Path.Combine(assetsDir, "photo_1920x1080_lossless.webp"));
        _losslessAlphaSource = Decode(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossless.webp"));
        _losslessGraphicSource = Decode(Path.Combine(assetsDir, "graphic_640x480_lossless.webp"));
        _smallSource = Decode(Path.Combine(assetsDir, "small_32x24_lossless.webp"));

        _losslessPhotographicSkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "photo_1920x1080_lossless.webp"));
        _losslessAlphaSkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossless.webp"));
        _losslessGraphicSkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "graphic_640x480_lossless.webp"));
        _smallSkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "small_32x24_lossless.webp"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _losslessPhotographicSkiaSource.Dispose();
        _losslessAlphaSkiaSource.Dispose();
        _losslessGraphicSkiaSource.Dispose();
        _smallSkiaSource.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("Lossless-Photographic")]
    public MemoryStream PeachImage_Encode_LosslessPhotographic() => Encode(_losslessPhotographicSource);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Photographic")]
    public SKData SkiaSharp_Encode_LosslessPhotographic() => EncodeSkia(_losslessPhotographicSkiaSource);

    [Benchmark]
    [BenchmarkCategory("Lossless-Alpha")]
    public MemoryStream PeachImage_Encode_LosslessAlpha() => Encode(_losslessAlphaSource);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Alpha")]
    public SKData SkiaSharp_Encode_LosslessAlpha() => EncodeSkia(_losslessAlphaSkiaSource);

    [Benchmark]
    [BenchmarkCategory("Lossless-Graphic")]
    public MemoryStream PeachImage_Encode_LosslessGraphic() => Encode(_losslessGraphicSource);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Graphic")]
    public SKData SkiaSharp_Encode_LosslessGraphic() => EncodeSkia(_losslessGraphicSkiaSource);

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public MemoryStream PeachImage_Encode_Small() => Encode(_smallSource);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Small-Image")]
    public SKData SkiaSharp_Encode_Small() => EncodeSkia(_smallSkiaSource);

    [Benchmark]
    [BenchmarkCategory("Lossy-Photographic")]
    public MemoryStream PeachImage_Encode_LossyPhotographic() => EncodeLossy(_losslessPhotographicSource);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy-Photographic")]
    public SKData SkiaSharp_Encode_LossyPhotographic() => EncodeSkiaLossy(_losslessPhotographicSkiaSource);

    private static Image Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return WebpDecoder.Decode(stream);
    }

    private static MemoryStream Encode(Image image)
    {
        var stream = new MemoryStream();
        WebpEncoder.Encode(image, stream, new WebpEncoderOptions());
        return stream;
    }

    private static MemoryStream EncodeLossy(Image image)
    {
        var stream = new MemoryStream();
        WebpEncoder.Encode(image, stream, new WebpEncoderOptions { Lossless = false });
        return stream;
    }

    private static SKData EncodeSkia(SKBitmap bitmap) => SKWebpEncoder.Encode(bitmap.PeekPixels(), LosslessSkiaOptions)!;

    private static SKData EncodeSkiaLossy(SKBitmap bitmap) => SKWebpEncoder.Encode(bitmap.PeekPixels(), LossySkiaOptions)!;
}
