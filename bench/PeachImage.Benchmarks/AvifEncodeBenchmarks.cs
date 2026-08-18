using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// AVIF encode throughput. Like <see cref="AvifDecodeBenchmarks"/>, there's no SkiaSharp baseline column
/// here -- this repo's pinned SkiaSharp version doesn't support AVIF at all (see that class's remarks).
/// Covers 8-bit 4:2:0 photographic content and a small-image scenario to surface fixed per-encode overhead
/// separately from throughput on large images. Sources are decoded from this repo's existing AVIF decode
/// benchmark assets (opaque only -- this encoder doesn't produce an alpha item in this version) rather than
/// separately maintained fixtures.
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
    public MemoryStream PeachImage_Encode_Photographic420() => Encode(_photographic420);

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public MemoryStream PeachImage_Encode_Small() => Encode(_small);

    private static Image Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return AvifDecoder.Decode(stream);
    }

    private static MemoryStream Encode(Image image)
    {
        var stream = new MemoryStream();
        AvifEncoder.Encode(image, stream, new AvifEncoderOptions());
        return stream;
    }
}
