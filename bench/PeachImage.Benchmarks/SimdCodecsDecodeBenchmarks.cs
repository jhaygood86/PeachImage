#if NET10_0_OR_GREATER
using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Png;
using PeachImage.Formats.Webp;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecsApi = Sdcb.SimdCodecs.SimdCodecs;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput and managed allocation: PeachImage vs. Sdcb.SimdCodecs (pure-managed, MIT). The baseline
/// column is SimdCodecs, so <c>Ratio</c> &lt; 1.00 means PeachImage is faster. SimdCodecs decodes into a
/// caller-owned RGBA8 buffer (allocated once in <see cref="Setup"/>, zero managed allocation per call);
/// PeachImage uses its normal <c>Decode(Stream)</c> API and disposes the result so its pooled pixel buffer is
/// recycled (the steady-state pattern; an undisposed image costs a fresh large allocation per call).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SimdCodecsDecodeBenchmarks
{
    private byte[] _jpeg1080p420 = null!;
    private byte[] _jpeg1080p444 = null!;
    private byte[] _jpeg12Mp420 = null!;
    private byte[] _jpegGray = null!;
    private byte[] _png24 = null!;
    private byte[] _png32 = null!;
    private byte[] _webpLossy = null!;
    private byte[] _webpLossless = null!;
    private byte[] _webpLossyAlpha = null!;
    private byte[] _webpLosslessAlpha = null!;
    private byte[] _rgba = null!;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets");
        byte[] Read(string n) => File.ReadAllBytes(Path.Combine(dir, n));
        _jpeg1080p420 = Read("photo_1920x1080_420.jpg");
        _jpeg1080p444 = Read("photo_1920x1080_444.jpg");
        _jpeg12Mp420 = Read("photo_4032x3024_420.jpg");
        _jpegGray = Read("gray_1920x1080.jpg");
        _png24 = Read("photo_1920x1080_24bpp.png");
        _png32 = Read("photo_1920x1080_32bpp_rgba.png");
        _webpLossy = Read("photo_1920x1080_lossy_q80.webp");
        _webpLossless = Read("photo_1920x1080_lossless.webp");
        _webpLossyAlpha = Read("photo_1920x1080_alpha_lossy_q80.webp");
        _webpLosslessAlpha = Read("photo_1920x1080_alpha_lossless.webp");

        // Largest scenario is the 12 MP JPEG; one buffer serves every SimdCodecs call.
        _rgba = new byte[4032 * 3024 * 4];
    }

    private int Simd(byte[] file)
    {
        if (!SimdCodecsApi.TryDecodeIntoRgba8(file, _rgba))
        {
            throw new InvalidOperationException("SimdCodecs decode failed.");
        }

        return _rgba[0];
    }

    // The decoded image is disposed so its pixel buffer returns to PeachImage's pool for the next call: that is
    // the steady-state usage the SimdCodecs side (one reused caller buffer) is being compared against.
    private static int Peach(byte[] file, Func<Stream, Image> decode)
    {
        using var s = new MemoryStream(file);
        using var image = decode(s);
        return image.Width;
    }

    // The caller-buffer path: one reused destination and decoder, like the SimdCodecs side.
    private readonly ImageDecoder _decoder = new(new DecoderOptions { SkipMetadata = true });

    private int PeachInto(byte[] file) => _decoder.DecodeInto(file, _rgba, out _).Width;

    [Benchmark] [BenchmarkCategory("JPEG-1080p-420")]
    public int PeachImageInto_Jpeg_1080p_420() => PeachInto(_jpeg1080p420);
    [Benchmark] [BenchmarkCategory("JPEG-1080p-444")]
    public int PeachImageInto_Jpeg_1080p_444() => PeachInto(_jpeg1080p444);
    [Benchmark] [BenchmarkCategory("JPEG-12MP-420")]
    public int PeachImageInto_Jpeg_12Mp_420() => PeachInto(_jpeg12Mp420);
    [Benchmark] [BenchmarkCategory("JPEG-Gray")]
    public int PeachImageInto_Jpeg_Gray() => PeachInto(_jpegGray);

    [Benchmark] [BenchmarkCategory("JPEG-1080p-420")]
    public int PeachImage_Jpeg_1080p_420() => Peach(_jpeg1080p420, s => JpegDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("JPEG-1080p-420")]
    public int SimdCodecs_Jpeg_1080p_420() => Simd(_jpeg1080p420);

    [Benchmark] [BenchmarkCategory("JPEG-1080p-444")]
    public int PeachImage_Jpeg_1080p_444() => Peach(_jpeg1080p444, s => JpegDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("JPEG-1080p-444")]
    public int SimdCodecs_Jpeg_1080p_444() => Simd(_jpeg1080p444);

    [Benchmark] [BenchmarkCategory("JPEG-12MP-420")]
    public int PeachImage_Jpeg_12Mp_420() => Peach(_jpeg12Mp420, s => JpegDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("JPEG-12MP-420")]
    public int SimdCodecs_Jpeg_12Mp_420() => Simd(_jpeg12Mp420);

    [Benchmark] [BenchmarkCategory("JPEG-Gray")]
    public int PeachImage_Jpeg_Gray() => Peach(_jpegGray, s => JpegDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("JPEG-Gray")]
    public int SimdCodecs_Jpeg_Gray() => Simd(_jpegGray);

    [Benchmark] [BenchmarkCategory("PNG-24bpp")]
    public int PeachImage_Png_24bpp() => Peach(_png24, s => PngDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("PNG-24bpp")]
    public int SimdCodecs_Png_24bpp() => Simd(_png24);

    [Benchmark] [BenchmarkCategory("PNG-32bpp-RGBA")]
    public int PeachImage_Png_32bpp() => Peach(_png32, s => PngDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("PNG-32bpp-RGBA")]
    public int SimdCodecs_Png_32bpp() => Simd(_png32);

    [Benchmark] [BenchmarkCategory("WebP-Lossy")]
    public int PeachImage_Webp_Lossy() => Peach(_webpLossy, s => WebpDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("WebP-Lossy")]
    public int SimdCodecs_Webp_Lossy() => Simd(_webpLossy);

    [Benchmark] [BenchmarkCategory("WebP-Lossless")]
    public int PeachImage_Webp_Lossless() => Peach(_webpLossless, s => WebpDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("WebP-Lossless")]
    public int SimdCodecs_Webp_Lossless() => Simd(_webpLossless);

    [Benchmark] [BenchmarkCategory("WebP-Lossy-Alpha")]
    public int PeachImage_Webp_LossyAlpha() => Peach(_webpLossyAlpha, s => WebpDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("WebP-Lossy-Alpha")]
    public int SimdCodecs_Webp_LossyAlpha() => Simd(_webpLossyAlpha);

    [Benchmark] [BenchmarkCategory("WebP-Lossless-Alpha")]
    public int PeachImage_Webp_LosslessAlpha() => Peach(_webpLosslessAlpha, s => WebpDecoder.Decode(s));
    [Benchmark(Baseline = true)] [BenchmarkCategory("WebP-Lossless-Alpha")]
    public int SimdCodecs_Webp_LosslessAlpha() => Simd(_webpLosslessAlpha);
}
#endif
