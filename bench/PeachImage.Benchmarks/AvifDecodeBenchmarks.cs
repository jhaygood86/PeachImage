using BenchmarkDotNet.Attributes;
using PeachImage.Benchmarks.Native;
using PeachImage.Formats.Avif;

namespace PeachImage.Benchmarks;

/// <summary>
/// AVIF decode throughput. Unlike the other formats' benchmarks, there's no SkiaSharp baseline column here
/// -- this repo's pinned SkiaSharp version doesn't decode AVIF at all (confirmed in the Phase 0 spike; see
/// README.md), so <c>LIBRARY_COMPARISON.md</c>'s AVIF section instead reports a supplementary
/// process-spawn <c>ffmpeg</c> timing comparison, called out with its own overhead caveat rather than
/// presented as a directly comparable BenchmarkDotNet row. Covers 8-bit 4:2:0 photographic content, with
/// alpha, and a small-image scenario to surface fixed per-decode overhead separately from throughput on
/// large images -- the same scenario shape as <c>WebpDecodeBenchmarks</c>. Also covers a
/// <see cref="AvifEncoderOptions.Lossless"/>-encoded source (4:4:4, WHT/IntraBC/palette path), since that's
/// a materially different decode path from the default 4:2:0/DCT content the other cases exercise, plus two
/// libaom-encoded photos with loop restoration and 128x128 superblocks enabled (8-bit and 10-bit), which the
/// other assets never reach.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AvifDecodeBenchmarks
{
    private byte[] _photographic420 = null!;
    private byte[] _photographicAlpha = null!;
    private byte[] _photographicLossless = null!;
    private byte[] _photographicLr = null!;
    private byte[] _photographic10BitLr = null!;
    private byte[] _small = null!;
    private PinnedBytes _nativePhotographic420 = null!;
    private PinnedBytes _nativePhotographicAlpha = null!;
    private PinnedBytes _nativePhotographicLossless = null!;
    private PinnedBytes _nativePhotographicLr = null!;
    private PinnedBytes _nativePhotographic10BitLr = null!;
    private PinnedBytes _nativeSmall = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _photographic420 = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_420.avif"));
        _photographicAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha.avif"));
        _photographicLossless = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lossless.avif"));
        _photographicLr = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_420_lr.avif"));
        _photographic10BitLr = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_420_10bit_lr.avif"));
        _small = File.ReadAllBytes(Path.Combine(assetsDir, "small_32x24.avif"));
        _nativePhotographic420 = new PinnedBytes(_photographic420);
        _nativePhotographicAlpha = new PinnedBytes(_photographicAlpha);
        _nativePhotographicLossless = new PinnedBytes(_photographicLossless);
        _nativePhotographicLr = new PinnedBytes(_photographicLr);
        _nativePhotographic10BitLr = new PinnedBytes(_photographic10BitLr);
        _nativeSmall = new PinnedBytes(_small);
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
    [BenchmarkCategory("Photographic-420-LR")]
    public Image PeachImage_Decode_PhotographicLr()
    {
        using var stream = new MemoryStream(_photographicLr);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Photographic-10Bit-LR")]
    public Image PeachImage_Decode_Photographic10BitLr()
    {
        using var stream = new MemoryStream(_photographic10BitLr);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Small-Image")]
    public Image PeachImage_Decode_Small()
    {
        using var stream = new MemoryStream(_small);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Photographic-Lossless")]
    public Image PeachImage_Decode_PhotographicLossless()
    {
        using var stream = new MemoryStream(_photographicLossless);
        return AvifDecoder.Decode(stream);
    }

    [Benchmark]
    [BenchmarkCategory("Photographic-420")]
    public int Libavif_Decode_Photographic420() => Libavif.Decode(_nativePhotographic420);

    [Benchmark]
    [BenchmarkCategory("Photographic-Alpha")]
    public int Libavif_Decode_PhotographicAlpha() => Libavif.Decode(_nativePhotographicAlpha);

    [Benchmark]
    [BenchmarkCategory("Photographic-Lossless")]
    public int Libavif_Decode_PhotographicLossless() => Libavif.Decode(_nativePhotographicLossless);

    [Benchmark]
    [BenchmarkCategory("Photographic-420-LR")]
    public int Libavif_Decode_PhotographicLr() => Libavif.Decode(_nativePhotographicLr);

    [Benchmark]
    [BenchmarkCategory("Photographic-10Bit-LR")]
    public int Libavif_Decode_Photographic10BitLr() => Libavif.Decode(_nativePhotographic10BitLr);

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public int Libavif_Decode_Small() => Libavif.Decode(_nativeSmall);
}
