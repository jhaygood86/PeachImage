using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// AVIF decode throughput. Unlike the other formats' benchmarks, there's no SkiaSharp baseline column here
/// -- this repo's pinned SkiaSharp version doesn't decode AVIF at all (confirmed in the Phase 0 spike; see
/// README.md), so <c>LIBRARY_COMPARISON.md</c>'s AVIF section instead reports a supplementary
/// process-spawn <c>ffmpeg</c> timing comparison, called out with its own overhead caveat rather than
/// presented as a directly comparable BenchmarkDotNet row. Covers 8-bit 4:2:0 photographic content, with
/// alpha, and a small-image scenario to surface fixed per-decode overhead separately from throughput on
/// large images -- the same scenario shape as <c>WebpDecodeBenchmarks</c>.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AvifDecodeBenchmarks
{
    private byte[] _photographic420 = null!;
    private byte[] _photographicAlpha = null!;
    private byte[] _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _photographic420 = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_420.avif"));
        _photographicAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha.avif"));
        _small = File.ReadAllBytes(Path.Combine(assetsDir, "small_32x24.avif"));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Photographic-420")]
    public Image PeachImage_Decode_Photographic420()
    {
        using var stream = new MemoryStream(_photographic420);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Photographic-Alpha")]
    public Image PeachImage_Decode_PhotographicAlpha()
    {
        using var stream = new MemoryStream(_photographicAlpha);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Small-Image")]
    public Image PeachImage_Decode_Small()
    {
        using var stream = new MemoryStream(_small);
        return AvifDecoder.Decode(stream);
    }
}
