using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Ccitt;

namespace PeachImage.Benchmarks;

/// <summary>
/// CCITT fax decode throughput: PeachImage only (SkiaSharp has no TIFF or fax codec). A 1700x2200 page-like bilevel image as a
/// Group 4 TIFF, a Group 3 TIFF, and the bare Group 4 strip decoded through <see cref="CcittFaxDecoderOptions"/> as a PDF would.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CcittDecodeBenchmarks
{
    private byte[] _group4 = null!;
    private byte[] _group3 = null!;
    private byte[] _rawGroup4 = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _group4 = File.ReadAllBytes(Path.Combine(assetsDir, "fax_page_1700x2200_g4.tif"));
        _group3 = File.ReadAllBytes(Path.Combine(assetsDir, "fax_page_1700x2200_g3.tif"));
        _rawGroup4 = File.ReadAllBytes(Path.Combine(assetsDir, "fax_page_1700x2200_g4.raw"));
    }

    [Benchmark]
    [BenchmarkCategory("Group 4 TIFF 1700x2200")]
    public Image PeachImage_Decode_Group4Tiff() => Image.Load(_group4);

    [Benchmark]
    [BenchmarkCategory("Group 3 TIFF 1700x2200")]
    public Image PeachImage_Decode_Group3Tiff() => Image.Load(_group3);

    [Benchmark]
    [BenchmarkCategory("Raw Group 4 stream 1700x2200")]
    public Image PeachImage_Decode_RawGroup4()
    {
        using var stream = new MemoryStream(_rawGroup4);
        return Image.Load(stream, new CcittFaxDecoderOptions { K = -1, Columns = 1700, Rows = 2200 });
    }
}
