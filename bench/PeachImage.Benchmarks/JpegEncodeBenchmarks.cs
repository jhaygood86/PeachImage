using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Jpeg;
using TurboJpegWrapper;

namespace PeachImage.Benchmarks;

/// <summary>
/// Encode throughput: PeachImage vs. real libjpeg-turbo (via Quamotion.TurboJpegWrapper). Both sides use
/// each library's default (non-optimized-Huffman) settings for an apples-to-apples v1 comparison.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class JpegEncodeBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Quality = 85;

    private Image _sourceImage = null!;
    private byte[] _sourceRgb = null!;
    private TJCompressor _turboJpeg = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var stream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_444.jpg"));
        _sourceImage = new JpegDecoder().Decode(stream);
        _sourceRgb = _sourceImage.GetPixelSpan().ToArray();
        _turboJpeg = new TJCompressor();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sourceImage.Dispose();
        _turboJpeg.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("1080p-4:2:0")]
    public MemoryStream PeachImage_Encode_1080p_420()
    {
        var stream = new MemoryStream();
        new JpegEncoder().Encode(_sourceImage, stream, new JpegEncoderOptions { Quality = Quality, Subsampling = JpegChromaSubsampling.Yuv420 });
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:2:0")]
    public byte[] TurboJpegTurbo_Encode_1080p_420() => EncodeWithTurboJpeg(TJSubsamplingOption.Chrominance420);

    [Benchmark]
    [BenchmarkCategory("1080p-4:4:4")]
    public MemoryStream PeachImage_Encode_1080p_444()
    {
        var stream = new MemoryStream();
        new JpegEncoder().Encode(_sourceImage, stream, new JpegEncoderOptions { Quality = Quality, Subsampling = JpegChromaSubsampling.Yuv444 });
        return stream;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("1080p-4:4:4")]
    public byte[] TurboJpegTurbo_Encode_1080p_444() => EncodeWithTurboJpeg(TJSubsamplingOption.Chrominance444);

    private byte[] EncodeWithTurboJpeg(TJSubsamplingOption subsampling)
    {
        int bufferSize = _turboJpeg.GetBufferSize(Width, Height, subsampling);
        var target = new byte[bufferSize];
        _turboJpeg.Compress(_sourceRgb, target, Width * 3, Width, Height, TJPixelFormat.RGB, subsampling, Quality, TJFlags.None);
        return target;
    }
}
