using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jxl;

namespace PeachImage.Benchmarks;

/// <summary>
/// JPEG XL encode throughput: PeachImage only (SkiaSharp has no JPEG XL codec). Photographic 1920x1080 sources cover the lossless
/// Modular coder and the lossy VarDCT coder, plus an RGBA source for the alpha path. Run pinned to P-cores and with
/// <c>--warmupCount 5 --iterationCount 20</c> when comparing changes (see LIBRARY_COMPARISON.md's methodology notes).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JxlEncodeBenchmarks
{
    private static readonly JxlEncoderOptions Lossless = new() { Lossless = true };
    private static readonly JxlEncoderOptions Lossy = new() { Quality = 75 };

    private Image _rgb = null!;
    private Image _gray = null!;
    private Image _rgba = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _rgb = Image.Load(Path.Combine(assetsDir, "photo_1920x1080_24bpp.png"));
        _gray = Image.Load(Path.Combine(assetsDir, "gray_1920x1080.png"));
        _rgba = Image.Load(Path.Combine(assetsDir, "photo_1920x1080_32bpp_rgba.png"));
    }

    [Benchmark]
    [BenchmarkCategory("Lossless RGB 1920x1080")]
    public MemoryStream PeachImage_Encode_LosslessRgb() => Encode(_rgb, Lossless);

    [Benchmark]
    [BenchmarkCategory("Lossless gray 1920x1080")]
    public MemoryStream PeachImage_Encode_LosslessGray() => Encode(_gray, Lossless);

    [Benchmark]
    [BenchmarkCategory("Lossless RGBA 1920x1080")]
    public MemoryStream PeachImage_Encode_LosslessRgba() => Encode(_rgba, Lossless);

    [Benchmark]
    [BenchmarkCategory("Lossy RGB 1920x1080")]
    public MemoryStream PeachImage_Encode_LossyRgb() => Encode(_rgb, Lossy);

    [Benchmark]
    [BenchmarkCategory("Lossy gray 1920x1080")]
    public MemoryStream PeachImage_Encode_LossyGray() => Encode(_gray, Lossy);

    private static MemoryStream Encode(Image image, JxlEncoderOptions options)
    {
        var stream = new MemoryStream();
        image.Save(stream, "jxl", options);
        return stream;
    }
}
