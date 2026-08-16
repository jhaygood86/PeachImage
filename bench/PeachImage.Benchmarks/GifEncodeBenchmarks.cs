using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Gif;

namespace PeachImage.Benchmarks;

/// <summary>
/// Encode throughput: PeachImage only. SkiaSharp's <c>SKPixmap.Encode</c> does not support
/// <see cref="global::SkiaSharp.SKEncodedImageFormat.Gif"/> (confirmed empirically — it returns null), so
/// there is no real-world GIF encoder available here to hold PeachImage to a 10%-of-baseline bar against, as
/// is done for decode (see <see cref="GifDecodeBenchmarks"/>). This class exists to track PeachImage's own GIF
/// encode throughput (with and without dithering, and for animation) over time.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GifEncodeBenchmarks
{
    private Image _lowColorGraphic = null!;
    private Image _photographicSource = null!;
    private AnimatedImage _animatedSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

        using (var stream = File.OpenRead(Path.Combine(assetsDir, "graphic_640x480_16color.gif")))
        {
            _lowColorGraphic = GifDecoder.Decode(stream);
        }

        using (var stream = File.OpenRead(Path.Combine(assetsDir, "photo_1920x1080_dithered.gif")))
        {
            _photographicSource = GifDecoder.Decode(stream);
        }

        using (var stream = File.OpenRead(Path.Combine(assetsDir, "animated_320x240_24frames.gif")))
        {
            _animatedSource = AnimatedImage.Load(stream);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _lowColorGraphic.Dispose();
        _photographicSource.Dispose();
        _animatedSource.Dispose();
    }

    [Benchmark]
    [BenchmarkCategory("LowColor-Static")]
    public MemoryStream PeachImage_Encode_LowColorGraphic()
    {
        var stream = new MemoryStream();
        GifEncoder.Encode(_lowColorGraphic, stream, new GifEncoderOptions { MaxColors = 16 });
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("Photographic-NoDither")]
    public MemoryStream PeachImage_Encode_PhotographicNoDither()
    {
        var stream = new MemoryStream();
        GifEncoder.Encode(_photographicSource, stream, new GifEncoderOptions { MaxColors = 256, Dither = false });
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("Photographic-Dithered")]
    public MemoryStream PeachImage_Encode_PhotographicDithered()
    {
        var stream = new MemoryStream();
        GifEncoder.Encode(_photographicSource, stream, new GifEncoderOptions { MaxColors = 256, Dither = true });
        return stream;
    }

    [Benchmark]
    [BenchmarkCategory("Animated-MultiFrame")]
    public MemoryStream PeachImage_Encode_AnimatedAllFrames()
    {
        var stream = new MemoryStream();
        _animatedSource.Save(stream, "gif", new GifEncoderOptions { MaxColors = 64 });
        return stream;
    }
}
