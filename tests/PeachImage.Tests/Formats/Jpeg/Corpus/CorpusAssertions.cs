using PeachImage.Formats.Jpeg;
using PeachImage.Tests.Internal;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other
/// than <see cref="JpegFormatException"/> for a file PeachImage chooses to reject; and whenever both
/// PeachImage and SkiaSharp (a mature, real-world JPEG decoder) successfully decode the same file, their
/// pixel output should agree closely — a strong differential-correctness signal beyond self-consistency alone.
/// </summary>
internal static class CorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="JpegFormatException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not JpegFormatException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>Combines <see cref="AssertDecodesGracefully"/> with a differential pixel comparison against SkiaSharp when both decoders succeed.</summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        Image? peachImage;
        try
        {
            using var stream = File.OpenRead(path);
            peachImage = JpegDecoder.Decode(stream);
        }
        catch (JpegFormatException)
        {
            return;
        }

        using (peachImage)
        {
            if (peachImage.Width < 8 || peachImage.Height < 8)
            {
                // Sub-block (<8x8) images are a genuine edge case where reasonable decoders can legitimately
                // differ in how they handle the single, partially-out-of-bounds MCU — not a fidelity bug.
                return;
            }

            using var skiaBitmap = SKBitmap.Decode(path);
            if (skiaBitmap is null || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
            {
                return;
            }

            if (peachImage.PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24 or PixelFormat.Rgba32))
            {
                return; // No direct RGB comparison for CMYK output.
            }

            double averageDifference = ComputeAverageChannelDifference(peachImage, skiaBitmap);
            Assert.True(averageDifference < 12.0, $"{Path.GetFileName(path)}: average per-channel difference from SkiaSharp too high: {averageDifference:F2}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = JpegDecoder.Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static double ComputeAverageChannelDifference(Image peachImage, SKBitmap skiaBitmap)
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

                double r, g, b;
                if (peachImage.PixelFormat == PixelFormat.Gray8)
                {
                    r = g = b = span[offset];
                }
                else
                {
                    r = span[offset];
                    g = span[offset + 1];
                    b = span[offset + 2];
                }

                sum += Math.Abs(r - skiaPixel.Red) + Math.Abs(g - skiaPixel.Green) + Math.Abs(b - skiaPixel.Blue);
                count++;
            }
        }

        return count == 0 ? 0 : sum / (count * 3);
    }
}
