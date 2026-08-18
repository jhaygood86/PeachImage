using PeachImage.Formats.Webp;
using PeachImage.Tests.Internal;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// The animated-WebP equivalent of <see cref="CorpusAssertions"/>: decoding via
/// <see cref="WebpDecoder.DecodeAnimation"/> (walking every lazily-decoded frame, not just the first) must
/// never crash or hang, and whenever both PeachImage and SkiaSharp successfully decode the same animated
/// file, every frame's composited pixels should agree closely — decoded via SkiaSharp's <see cref="SKCodec"/>
/// multi-frame API (<c>GetFrameInfo</c>/<c>GetPixels</c> with <see cref="SKCodecOptions.FrameIndex"/>/
/// <see cref="SKCodecOptions.PriorFrame"/>, decoding frames strictly in order so Skia's own compositing —
/// blend vs. overwrite, dispose-to-background before the next frame — matches what <c>WebpFrameCompositor</c>
/// does), reusing <see cref="CorpusAssertions"/>'s pixel-comparison/tolerance helpers per frame.
/// </summary>
internal static class AnimatedCorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Asserts that decoding every frame of <paramref name="path"/> either succeeds or throws <see cref="WebpDecodingException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not WebpDecodingException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>Combines <see cref="AssertDecodesGracefully"/> with a per-frame differential pixel comparison against SkiaSharp when both decoders succeed and agree on frame count.</summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        List<AnimatedImageFrame> frames;
        try
        {
            using var stream = File.OpenRead(path);
            var animated = WebpDecoder.DecodeAnimation(stream);
            // Each pulled frame's Image aliases the decoder's persistent compositor canvas and is
            // invalidated once the next frame is pulled, so every frame must be cloned as it's pulled (not
            // just ToList()'d) to compare all of them against Skia afterward.
            frames = animated.Frames.Select(frame => frame.Clone()).ToList();
        }
        catch (WebpDecodingException)
        {
            return;
        }

        using var codec = SKCodec.Create(path);
        if (codec is null || codec.FrameCount != frames.Count)
        {
            return;
        }

        var info = codec.Info.WithAlphaType(SKAlphaType.Unpremul);
        using var skiaBitmap = new SKBitmap(info);
        int priorFrame = -1;

        for (int i = 0; i < frames.Count; i++)
        {
            var peachFrame = frames[i].Image;
            if (peachFrame.Width != info.Width || peachFrame.Height != info.Height)
            {
                return;
            }

            var options = new SKCodecOptions(i, priorFrame);
            var result = codec.GetPixels(info, skiaBitmap.GetPixels(), options);
            if (result != SKCodecResult.Success)
            {
                return;
            }

            priorFrame = i;

            var difference = CorpusAssertions.ComputeDifference(peachFrame, skiaBitmap, includeAlpha: true);
            CorpusAssertions.AssertWithinTolerance($"{path} [frame {i}]", difference, includeAlpha: true);
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var animated = WebpDecoder.DecodeAnimation(stream);
            foreach (var frame in animated.Frames)
            {
                _ = frame;
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}
