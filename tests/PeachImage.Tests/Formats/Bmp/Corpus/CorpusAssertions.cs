using PeachImage.Formats.Bmp;
using PeachImage.Tests.Internal;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Bmp.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other than
/// <see cref="BmpFormatException"/> for a file PeachImage chooses to reject; and whenever both PeachImage and
/// SkiaSharp (a mature, real-world BMP decoder) successfully decode the same file, their RGB pixel output
/// should agree closely — a much tighter tolerance than Jpeg's, since BMP is lossless.
/// </summary>
internal static class CorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="BmpFormatException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not BmpFormatException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>
    /// Combines <see cref="AssertDecodesGracefully"/> with a differential RGB pixel comparison against
    /// SkiaSharp when both decoders succeed. Alpha-bearing (<see cref="PixelFormat.Rgba32"/>) decodes are
    /// excluded from the comparison entirely, not just their alpha channel: SkiaSharp's decoded bitmap is
    /// alpha-premultiplied for BMPs with a real alpha channel, which distorts RGB comparisons too wherever
    /// alpha isn't fully opaque — confirmed empirically (every alpha-bearing corpus file showed a spurious
    /// RGB delta against Skia, while every opaque file matched closely). SkiaSharp's own BMP alpha semantics
    /// (premultiplication, mask handling) aren't a trustworthy oracle either way; alpha correctness is
    /// verified separately by exact-equality round-trip tests.
    /// </summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        Image? peachImage;
        try
        {
            using var stream = File.OpenRead(path);
            peachImage = BmpDecoder.Decode(stream);
        }
        catch (BmpFormatException)
        {
            return;
        }

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

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = BmpDecoder.Decode(stream);
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
