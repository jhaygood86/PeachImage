using BenchmarkDotNet.Attributes;
using PeachImage.Benchmarks.Native;
using PeachImage.Formats.Tiff;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage only. SkiaSharp has no TIFF codec at all (confirmed via its
/// <c>SKEncodedImageFormat</c>/<c>SkCodec</c> format list — there is no baseline decoder here to hold
/// PeachImage to a 10%-of-baseline bar against, unlike every other format's decode benchmark). This class
/// tracks PeachImage's own decode throughput across the three compression modes it supports (none, LZW,
/// PackBits) over time. See <c>LIBRARY_COMPARISON.md</c>'s TIFF section for an `ffmpeg`-context-only number
/// alongside these, collected the same way as the AVIF section's `ffmpeg`/libdav1d comparison.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class TiffDecodeBenchmarks
{
    private byte[] _uncompressed = null!;
    private byte[] _lzw = null!;
    private byte[] _packBits = null!;
    private PinnedBytes _nativeUncompressed = null!;
    private PinnedBytes _nativeLzw = null!;
    private PinnedBytes _nativePackBits = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _uncompressed = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_uncompressed.tif"));
        _lzw = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lzw.tif"));
        _packBits = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_packbits.tif"));
        _nativeUncompressed = new PinnedBytes(_uncompressed);
        _nativeLzw = new PinnedBytes(_lzw);
        _nativePackBits = new PinnedBytes(_packBits);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Uncompressed")]
    public Image PeachImage_Decode_Uncompressed()
    {
        using var stream = new MemoryStream(_uncompressed);
        return TiffDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LZW")]
    public Image PeachImage_Decode_Lzw()
    {
        using var stream = new MemoryStream(_lzw);
        return TiffDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PackBits")]
    public Image PeachImage_Decode_PackBits()
    {
        using var stream = new MemoryStream(_packBits);
        return TiffDecoder.Decode(stream);
    }

    [Benchmark]
    [BenchmarkCategory("Uncompressed")]
    public byte[] Libtiff_Decode_Uncompressed() => Libtiff.Decode(_nativeUncompressed);

    [Benchmark]
    [BenchmarkCategory("LZW")]
    public byte[] Libtiff_Decode_Lzw() => Libtiff.Decode(_nativeLzw);

    [Benchmark]
    [BenchmarkCategory("PackBits")]
    public byte[] Libtiff_Decode_PackBits() => Libtiff.Decode(_nativePackBits);
}
