using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Gif;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world GIF decoder — the same library used as
/// the corpus tests' differential oracle). The acceptance bar (see the project plan) is PeachImage's Mean
/// within 10% of SkiaSharp's Mean for every scenario below. The animated scenario decodes every frame through
/// both libraries (PeachImage via <see cref="AnimatedImage.Load(Stream, DecoderOptions?)"/>, SkiaSharp via <see cref="SKCodec"/>
/// frame-indexed decode), not just the first frame, since that's GIF's defining use case.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class GifDecodeBenchmarks
{
    private byte[] _lowColorGraphic = null!;
    private byte[] _photographicDithered = null!;
    private byte[] _animated = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _lowColorGraphic = File.ReadAllBytes(Path.Combine(assetsDir, "graphic_640x480_16color.gif"));
        _photographicDithered = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_dithered.gif"));
        _animated = File.ReadAllBytes(Path.Combine(assetsDir, "animated_320x240_24frames.gif"));
    }

    [Benchmark]
    [BenchmarkCategory("LowColor-Static")]
    public Image PeachImage_Decode_LowColorGraphic()
    {
        using var stream = new MemoryStream(_lowColorGraphic);
        return GifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LowColor-Static")]
    public SKBitmap SkiaSharp_Decode_LowColorGraphic() => SKBitmap.Decode(_lowColorGraphic)!;

    [Benchmark]
    [BenchmarkCategory("Photographic-Dithered")]
    public Image PeachImage_Decode_PhotographicDithered()
    {
        using var stream = new MemoryStream(_photographicDithered);
        return GifDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Photographic-Dithered")]
    public SKBitmap SkiaSharp_Decode_PhotographicDithered() => SKBitmap.Decode(_photographicDithered)!;

    [Benchmark]
    [BenchmarkCategory("Animated-MultiFrame")]
    public int PeachImage_Decode_AnimatedAllFrames()
    {
        using var stream = new MemoryStream(_animated);
        var animatedImage = AnimatedImage.Load(stream);
        int count = 0;
        foreach (var frame in animatedImage.Frames)
        {
            count++;
            frame.Dispose();
        }

        return count;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Animated-MultiFrame")]
    public int SkiaSharp_Decode_AnimatedAllFrames()
    {
        using var stream = new SKMemoryStream(_animated);
        using var codec = SKCodec.Create(stream)!;

        var imageInfo = codec.Info;
        int frameCount = codec.FrameCount;
        using var bitmap = new SKBitmap(imageInfo);

        for (int i = 0; i < frameCount; i++)
        {
            codec.GetPixels(imageInfo, bitmap.GetPixels(), new SKCodecOptions(i));
        }

        return frameCount;
    }
}
