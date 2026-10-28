using BenchmarkDotNet.Attributes;
using PeachImage.Benchmarks.Native;
using PeachImage.Formats.Jpeg2000;

namespace PeachImage.Benchmarks;

/// <summary>
/// JPEG 2000 decode throughput: PeachImage only (SkiaSharp has no JPEG 2000 codec). Two 1280x960 RGB files written by OpenJPEG:
/// lossless (reversible 5/3 with the reversible colour transform) and lossy (irreversible 9/7). Run pinned to P-cores and with
/// <c>--warmupCount 5 --iterationCount 20</c> when comparing changes (see LIBRARY_COMPARISON.md's methodology notes).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class Jpeg2000DecodeBenchmarks
{
    private byte[] _lossless = null!;
    private byte[] _lossy = null!;
    private PinnedBytes _nativeLossless = null!;
    private PinnedBytes _nativeLossy = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _lossless = File.ReadAllBytes(Path.Combine(assetsDir, "jp2_lossless_1280x960.jp2"));
        _lossy = File.ReadAllBytes(Path.Combine(assetsDir, "jp2_lossy_1280x960.jp2"));
        _nativeLossless = new PinnedBytes(_lossless);
        _nativeLossy = new PinnedBytes(_lossy);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless 1280x960")]
    public Image PeachImage_Decode_Lossless() => Decode(_lossless);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy 1280x960")]
    public Image PeachImage_Decode_Lossy() => Decode(_lossy);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless 1280x960, half size")]
    public Image PeachImage_Decode_Lossless_HalfSize() =>
        Image.Load(_lossless, new Jpeg2000DecoderOptions { ReduceResolutionLevels = 1 });

    [Benchmark]
    [BenchmarkCategory("Lossless 1280x960")]
    public int OpenJpeg_Decode_Lossless() => Openjpeg.Decode(_nativeLossless);

    [Benchmark]
    [BenchmarkCategory("Lossy 1280x960")]
    public int OpenJpeg_Decode_Lossy() => Openjpeg.Decode(_nativeLossy);

    [Benchmark]
    [BenchmarkCategory("Lossless 1280x960, half size")]
    public int OpenJpeg_Decode_Lossless_HalfSize() => Openjpeg.Decode(_nativeLossless, reduceResolutionLevels: 1);

    private static Image Decode(byte[] data) => Image.Load(data);
}
