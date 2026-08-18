using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Webp;
using SkiaSharp;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput: PeachImage vs. SkiaSharp (a mature, real-world WebP decoder backed by libwebp itself —
/// the same library used as the corpus tests' differential oracle). The acceptance bar (see the project plan)
/// is PeachImage's Mean within 10% of SkiaSharp's Mean for every scenario below. Covers both of WebP's
/// unrelated bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha, a small-image scenario to
/// surface fixed per-decode overhead separately from throughput on large images, and an animated scenario
/// (all frames, mirroring <see cref="GifDecodeBenchmarks"/>'s equivalent — same source content/dimensions/frame
/// count as its <c>animated_320x240_24frames.gif</c>, transcoded via ffmpeg's libwebp_anim muxer, so the two
/// are directly comparable).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WebpDecodeBenchmarks
{
    private byte[] _losslessPhotographic = null!;
    private byte[] _lossyPhotographic = null!;
    private byte[] _losslessGraphic = null!;
    private byte[] _lossyWithAlpha = null!;
    private byte[] _losslessWithAlpha = null!;
    private byte[] _small = null!;
    private byte[] _animated = null!;

    [GlobalSetup]
    public void Setup()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        _losslessPhotographic = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lossless.webp"));
        _lossyPhotographic = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_lossy_q80.webp"));
        _losslessGraphic = File.ReadAllBytes(Path.Combine(assetsDir, "graphic_640x480_lossless.webp"));
        _lossyWithAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossy_q80.webp"));
        _losslessWithAlpha = File.ReadAllBytes(Path.Combine(assetsDir, "photo_1920x1080_alpha_lossless.webp"));
        _small = File.ReadAllBytes(Path.Combine(assetsDir, "small_32x24_lossless.webp"));
        _animated = File.ReadAllBytes(Path.Combine(assetsDir, "animated_320x240_24frames.webp"));
    }

    [Benchmark]
    [BenchmarkCategory("Lossless-Photographic")]
    public Image PeachImage_Decode_LosslessPhotographic()
    {
        using var stream = new MemoryStream(_losslessPhotographic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Photographic")]
    public SKBitmap SkiaSharp_Decode_LosslessPhotographic() => SKBitmap.Decode(_losslessPhotographic)!;

    [Benchmark]
    [BenchmarkCategory("Lossy-Photographic")]
    public Image PeachImage_Decode_LossyPhotographic()
    {
        using var stream = new MemoryStream(_lossyPhotographic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy-Photographic")]
    public SKBitmap SkiaSharp_Decode_LossyPhotographic() => SKBitmap.Decode(_lossyPhotographic)!;

    [Benchmark]
    [BenchmarkCategory("Lossless-Graphic")]
    public Image PeachImage_Decode_LosslessGraphic()
    {
        using var stream = new MemoryStream(_losslessGraphic);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Graphic")]
    public SKBitmap SkiaSharp_Decode_LosslessGraphic() => SKBitmap.Decode(_losslessGraphic)!;

    [Benchmark]
    [BenchmarkCategory("Lossy-Alpha")]
    public Image PeachImage_Decode_LossyAlpha()
    {
        using var stream = new MemoryStream(_lossyWithAlpha);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossy-Alpha")]
    public SKBitmap SkiaSharp_Decode_LossyAlpha() => SKBitmap.Decode(_lossyWithAlpha)!;

    [Benchmark]
    [BenchmarkCategory("Lossless-Alpha")]
    public Image PeachImage_Decode_LosslessAlpha()
    {
        using var stream = new MemoryStream(_losslessWithAlpha);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Lossless-Alpha")]
    public SKBitmap SkiaSharp_Decode_LosslessAlpha() => SKBitmap.Decode(_losslessWithAlpha)!;

    [Benchmark]
    [BenchmarkCategory("Small-Image")]
    public Image PeachImage_Decode_Small()
    {
        using var stream = new MemoryStream(_small);
        return WebpDecoder.Decode(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Small-Image")]
    public SKBitmap SkiaSharp_Decode_Small() => SKBitmap.Decode(_small)!;

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
