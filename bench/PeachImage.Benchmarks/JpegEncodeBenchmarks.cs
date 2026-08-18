using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Encode throughput: PeachImage vs. SkiaSharp, both at explicit 4:2:0/4:4:4 chroma subsampling via
/// <see cref="SKJpegEncoderOptions"/> for an apples-to-apples comparison.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JpegEncodeBenchmarks
{
    private const int Quality = 85;

    private Image _sourceImage = null!;
    private SKBitmap _skiaSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        string sourcePath = Path.Combine(assetsDir, "photo_1920x1080_444.jpg");

        using var stream = File.OpenRead(sourcePath);
        _sourceImage = JpegDecoder.Decode(stream);
        _skiaSource = SKBitmap.Decode(sourcePath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _skiaSource.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("1080p-4:2:0")]
    public MemoryStream PeachImage_Encode_1080p_420()
    {
        var stream = new MemoryStream();
        JpegEncoder.Encode(_sourceImage, stream, new JpegEncoderOptions { Quality = Quality, Subsampling = JpegChromaSubsampling.Yuv420 });
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:2:0")]
    public SKData SkiaSharp_Encode_1080p_420() => EncodeWithSkia(SKJpegEncoderDownsample.Downsample420);

    [Benchmark]
    [BenchmarkCategory("1080p-4:4:4")]
    public MemoryStream PeachImage_Encode_1080p_444()
    {
        var stream = new MemoryStream();
        JpegEncoder.Encode(_sourceImage, stream, new JpegEncoderOptions { Quality = Quality, Subsampling = JpegChromaSubsampling.Yuv444 });
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:4:4")]
    public SKData SkiaSharp_Encode_1080p_444() => EncodeWithSkia(SKJpegEncoderDownsample.Downsample444);

    private SKData EncodeWithSkia(SKJpegEncoderDownsample downsample)
    {
        using var pixmap = _skiaSource.PeekPixels();
        return pixmap.Encode(new SKJpegEncoderOptions(Quality, downsample, SKJpegEncoderAlphaOption.Ignore))!;
    }
}
