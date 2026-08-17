using PeachImage.Formats.Gif;
using PeachImage.Tests.Internal;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other than
/// <see cref="GifDecodingException"/> for a file PeachImage chooses to reject; and whenever both PeachImage
/// and SkiaSharp (a mature, real-world GIF decoder — Skia decodes an animated GIF's first frame by default,
/// matching <see cref="GifDecoder.Decode"/>'s semantics) successfully decode the same file, their RGB pixel
/// output should agree closely — GIF decode of an already-palettized source is lossless, so the tolerance
/// here is tight, like Bmp's.
/// </summary>
internal static class CorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="GifDecodingException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not GifDecodingException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>
    /// Combines <see cref="AssertDecodesGracefully"/> with a differential RGB pixel comparison against
    /// SkiaSharp when both decoders succeed. Transparency-bearing (<see cref="PixelFormat.Rgba32"/>) decodes
    /// are excluded from the comparison, mirroring Bmp's corpus assertions: SkiaSharp's premultiplied-alpha
    /// handling isn't a trustworthy RGB oracle wherever alpha isn't fully opaque, and transparency/disposal
    /// correctness is verified separately by the round-trip and animation tests.
    /// </summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        Image? peachImage;
        try
        {
            using var stream = File.OpenRead(path);
            peachImage = GifDecoder.Decode(stream);
        }
        catch (GifDecodingException)
        {
            return;
        }

        using (peachImage)
        {
            if (peachImage.Width < 1 || peachImage.Height < 1)
            {
                return;
            }

            if (peachImage.PixelFormat != PixelFormat.Rgb24)
            {
                return;
            }

            using var skiaBitmap = SKBitmap.Decode(path);
            if (skiaBitmap is null || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
            {
                return;
            }

            double averageDifference = ComputeAverageRgbDifference(peachImage, skiaBitmap);
            Assert.True(averageDifference < 2.0, $"{Path.GetFileName(path)}: average per-channel difference from SkiaSharp too high: {averageDifference:F2}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = GifDecoder.Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static double ComputeAverageRgbDifference(Image peachImage, SKBitmap skiaBitmap)
    {
        var span = peachImage.GetPixelSpan();
        int bytesPerPixel = peachImage.PixelFormat.GetBytesPerPixel();
        int step = Math.Max(1, Math.Min(peachImage.Width, peachImage.Height) / 64);

        double sum = 0;
        long count = 0;
        for (int y = 0; y < peachImage.Height; y += step)
        {
            for (int x = 0; x < peachImage.Width; x += step)
            {
                var skiaPixel = skiaBitmap.GetPixel(x, y);
                int offset = ((y * peachImage.Width) + x) * bytesPerPixel;

                double r = span[offset];
                double g = span[offset + 1];
                double b = span[offset + 2];

                sum += Math.Abs(r - skiaPixel.Red) + Math.Abs(g - skiaPixel.Green) + Math.Abs(b - skiaPixel.Blue);
                count++;
            }
        }

        return count == 0 ? 0 : sum / (count * 3);
    }
}
