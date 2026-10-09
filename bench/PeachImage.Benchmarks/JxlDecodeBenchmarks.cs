using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jxl;

namespace PeachImage.Benchmarks;

/// <summary>
/// JPEG XL decode throughput: PeachImage only (SkiaSharp has no JPEG XL codec). The inputs are libjxl conformance files chosen to
/// cover the decoder's main paths: a large lossy VarDCT image (DC frame, patches, EPF, ICC output), a lossless Modular image with
/// patches, a lossy image with synthesized noise, and a lossless image whose splines dominate. Run pinned to P-cores and with
/// <c>--warmupCount 5 --iterationCount 20</c> when comparing changes (see LIBRARY_COMPARISON.md's methodology notes).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JxlDecodeBenchmarks
{
    private byte[] _lossy = null!;
    private byte[] _lossless = null!;
    private byte[] _noise = null!;
    private byte[] _splines = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _lossy = File.ReadAllBytes(Path.Combine(assetsDir, "jxl_lossy_4064x2704_icc.jxl"));
        _lossless = File.ReadAllBytes(Path.Combine(assetsDir, "jxl_lossless_patches_1600x1096.jxl"));
        _noise = File.ReadAllBytes(Path.Combine(assetsDir, "jxl_lossy_noise_500x606.jxl"));
        _splines = File.ReadAllBytes(Path.Combine(assetsDir, "jxl_lossless_splines_2048x2048.jxl"));
    }

    [Benchmark]
    [BenchmarkCategory("Lossy 4064x2704")]
    public Image PeachImage_Decode_Lossy() => Decode(_lossy);

    [Benchmark]
    [BenchmarkCategory("Lossless 1600x1096")]
    public Image PeachImage_Decode_LosslessPatches() => Decode(_lossless);

    [Benchmark]
    [BenchmarkCategory("Lossy noise 500x606")]
    public Image PeachImage_Decode_LossyNoise() => Decode(_noise);

    [Benchmark]
    [BenchmarkCategory("Lossless splines 2048x2048")]
    public Image PeachImage_Decode_LosslessSplines() => Decode(_splines);

    private static Image Decode(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return JxlDecoder.Decode(stream);
    }
}
