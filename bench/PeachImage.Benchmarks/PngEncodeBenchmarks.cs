using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Png;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Encode throughput: PeachImage vs. SkiaSharp. Unlike Bmp (whose encoder SkiaSharp doesn't support at
/// all), SkiaSharp's PNG encoder is fully functional, so this gets the same 10%-of-baseline treatment as
/// Jpeg's encode benchmarks rather than PeachImage-only tracking.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PngEncodeBenchmarks
{
    private Image _rgb24Source = null!;
    private Image _rgba32Source = null!;
    private Image _gray8Source = null!;
    private Image _lowColorSource = null!;
    private SKBitmap _rgb24SkiaSource = null!;
    private SKBitmap _rgba32SkiaSource = null!;
    private SKBitmap _gray8SkiaSource = null!;
    private SKBitmap _lowColorSkiaSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

        _rgb24Source = PngDecoder.Decode(File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_24bpp.png")));
        _rgba32Source = PngDecoder.Decode(File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_32bpp_rgba.png")));
        _gray8Source = PngDecoder.Decode(File.OpenRead(Path.Combine(assetsDir, "gray_1920x1080.png")));

        string lowColorPath = Path.Combine(assetsDir, "graphic_640x480_16color.gif");
        _lowColorSource = GifDecoder.Decode(File.OpenRead(lowColorPath));

        _rgb24SkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "photo_1920x1080_24bpp.png"));
        _rgba32SkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "photo_1920x1080_32bpp_rgba.png"));
        _gray8SkiaSource = SKBitmap.Decode(Path.Combine(assetsDir, "gray_1920x1080.png"));
        _lowColorSkiaSource = SKBitmap.Decode(lowColorPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _rgb24SkiaSource.Dispose();
        _rgba32SkiaSource.Dispose();
        _gray8SkiaSource.Dispose();
        _lowColorSkiaSource.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("24bpp-Truecolor")]
    public MemoryStream PeachImage_Encode_24bpp()
    {
        var stream = new MemoryStream();
        PngEncoder.Encode(_rgb24Source, stream);
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("24bpp-Truecolor")]
    public SKData SkiaSharp_Encode_24bpp() => _rgb24SkiaSource.Encode(SKEncodedImageFormat.Png, 100);

    [Benchmark]
    [BenchmarkCategory("32bpp-RGBA")]
    public MemoryStream PeachImage_Encode_32bppRgba()
    {
        var stream = new MemoryStream();
        PngEncoder.Encode(_rgba32Source, stream);
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("32bpp-RGBA")]
    public SKData SkiaSharp_Encode_32bppRgba() => _rgba32SkiaSource.Encode(SKEncodedImageFormat.Png, 100);

    [Benchmark]
    [BenchmarkCategory("8bpp-Grayscale")]
    public MemoryStream PeachImage_Encode_8bppGrayscale()
    {
        var stream = new MemoryStream();
        PngEncoder.Encode(_gray8Source, stream);
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("8bpp-Grayscale")]
    public SKData SkiaSharp_Encode_8bppGrayscale() => _gray8SkiaSource.Encode(SKEncodedImageFormat.Png, 100);

    // Issue #23: indexed (PLTE) palette construction. Low-color-count graphics/icon/screenshot content is
    // exactly the case indexed color targets — this scenario (16-color source) exercises
    // PngEncoderOptions.ColorMode's default Auto quantization path. SkiaSharp's SKBitmap.Encode does not
    // itself choose indexed color here (confirmed empirically: its output stays color type 2/truecolor
    // regardless of source color count), so this is a throughput comparison only — the win this feature
    // targets is file size, captured in LIBRARY_COMPARISON.md instead.
    [Benchmark]
    [BenchmarkCategory("LowColor-Indexed")]
    public MemoryStream PeachImage_Encode_LowColorIndexed()
    {
        var stream = new MemoryStream();
        PngEncoder.Encode(_lowColorSource, stream);
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LowColor-Indexed")]
    public SKData SkiaSharp_Encode_LowColorIndexed() => _lowColorSkiaSource.Encode(SKEncodedImageFormat.Png, 100);
}
