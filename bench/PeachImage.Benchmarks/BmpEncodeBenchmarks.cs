using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Bmp;
using PeachImage.Formats.Jpeg;

namespace PeachImage.Benchmarks;

/// <summary>
/// Encode throughput: PeachImage only. SkiaSharp's <c>SKBitmap.Encode</c> does not support
/// <see cref="global::SkiaSharp.SKEncodedImageFormat.Bmp"/> (confirmed empirically — it returns null), so
/// there is no real-world BMP encoder available here to hold PeachImage to a 10%-of-baseline bar against, as
/// is done for decode (see <see cref="BmpDecodeBenchmarks"/>) and for Jpeg (see <see cref="JpegEncodeBenchmarks"/>).
/// This class exists to track PeachImage's own BMP encode throughput over time.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BmpEncodeBenchmarks
{
    private Image _rgb24Source = null!;
    private Image _rgba32Source = null!;
    private Image _gray8Source = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var jpegStream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_444.jpg"));
        _rgb24Source = JpegDecoder.Decode(jpegStream);

        using var alphaStream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_32bpp_alpha.bmp"));
        _rgba32Source = BmpDecoder.Decode(alphaStream);

        using var grayStream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_8bpp_indexed.bmp"));
        _gray8Source = BmpDecoder.Decode(grayStream, new BmpDecoderOptions { TargetPixelFormat = PixelFormat.Gray8 });
    }

    [Benchmark]
    [BenchmarkCategory("24bpp-Truecolor")]
    public MemoryStream PeachImage_Encode_24bpp()
    {
        var stream = new MemoryStream();
        BmpEncoder.Encode(_rgb24Source, stream);
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("32bpp-Alpha")]
    public MemoryStream PeachImage_Encode_32bppAlpha()
    {
        var stream = new MemoryStream();
        BmpEncoder.Encode(_rgba32Source, stream);
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("8bpp-Indexed")]
    public MemoryStream PeachImage_Encode_8bppIndexed()
    {
        var stream = new MemoryStream();
        BmpEncoder.Encode(_gray8Source, stream);
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("8bpp-Indexed-RLE")]
    public MemoryStream PeachImage_Encode_8bppIndexedRle()
    {
        var stream = new MemoryStream();
        BmpEncoder.Encode(_gray8Source, stream, new BmpEncoderOptions { UseRunLengthEncoding = true });
        return stream;
    }
}
