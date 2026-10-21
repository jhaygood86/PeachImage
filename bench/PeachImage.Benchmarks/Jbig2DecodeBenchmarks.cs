using BenchmarkDotNet.Attributes;

namespace PeachImage.Benchmarks;

/// <summary>
/// JBIG2 decode throughput: PeachImage only (SkiaSharp has no JBIG2 codec). An 850x1100 page-like bitmap as an arithmetic-coded
/// generic region (template 0 with typical prediction) and as an MMR (Group 4) generic region.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class Jbig2DecodeBenchmarks
{
    private byte[] _arithmetic = null!;
    private byte[] _mmr = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _arithmetic = File.ReadAllBytes(Path.Combine(assetsDir, "jbig2_generic_850x1100.jb2"));
        _mmr = File.ReadAllBytes(Path.Combine(assetsDir, "jbig2_mmr_850x1100.jb2"));
    }

    [Benchmark]
    [BenchmarkCategory("Arithmetic generic region 850x1100")]
    public Image PeachImage_Decode_Arithmetic() => Image.Load(_arithmetic);

    [Benchmark]
    [BenchmarkCategory("MMR generic region 850x1100")]
    public Image PeachImage_Decode_Mmr() => Image.Load(_mmr);
}
