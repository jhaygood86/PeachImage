using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// AVIF encode throughput. Like <see cref="AvifDecodeBenchmarks"/>, there's no SkiaSharp baseline column
/// here -- this repo's pinned SkiaSharp version doesn't support AVIF at all (see that class's remarks).
/// Covers 8-bit photographic content and a small-image scenario, each at both default (4:2:0, quality 75)
/// and <see cref="AvifEncoderOptions.Lossless"/> (4:4:4, WHT/IntraBC/palette path) settings, to surface fixed
/// per-encode overhead separately from throughput on large images and lossy from lossless cost. Sources are
/// decoded from this repo's existing AVIF decode benchmark assets (opaque only -- this encoder doesn't
/// produce an alpha item in this version) rather than separately maintained fixtures.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AvifEncodeBenchmarks
{
    private Image _photographic420 = null!;
    private Image _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _photographic420 = Decode(Path.Combine(assetsDir, "photo_1920x1080_420.avif"));
        _small = Decode(Path.Combine(assetsDir, "small_32x24.avif"));
    }

    [Benchmark]
    [BenchmarkCategory("Photographic-420")]
    public MemoryStream PeachImage_Encode_Photographic420() => Encode(_photographic420, Lossy);

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public MemoryStream PeachImage_Encode_Small() => Encode(_small, Lossy);

    [Benchmark]
    [BenchmarkCategory("Photographic-Lossless")]
    public MemoryStream PeachImage_Encode_Photographic420_Lossless() => Encode(_photographic420, Lossless);

    [Benchmark]
    [BenchmarkCategory("Small-Image-Lossless")]
    public MemoryStream PeachImage_Encode_Small_Lossless() => Encode(_small, Lossless);

    private static readonly AvifEncoderOptions Lossy = new();
    private static readonly AvifEncoderOptions Lossless = new() { Lossless = true };

    private static Image Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return AvifDecoder.Decode(stream);
    }

    private static MemoryStream Encode(Image image, AvifEncoderOptions options)
    {
        var stream = new MemoryStream();
        AvifEncoder.Encode(image, stream, options);
        return stream;
    }
}
