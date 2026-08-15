using System.Runtime.InteropServices;
using PeachImage.Formats.Png;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other
/// than <see cref="PngFormatException"/> for a file PeachImage chooses to reject; and whenever both
/// PeachImage and SkiaSharp (a mature, real-world PNG decoder) successfully decode the same file, their
/// RGB pixel output should agree closely — PNG is lossless, so the tolerance is tight, matching Bmp's.
/// </summary>
internal static class PngCorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="PngFormatException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        var task = Task.Run(() => TryDecode(path));
        if (!task.Wait(PerFileTimeout))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = task.GetAwaiter().GetResult();
        if (!succeeded && exception is not PngFormatException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>
    /// Combines <see cref="AssertDecodesGracefully"/> with a differential RGB pixel comparison against
    /// SkiaSharp when both decoders succeed. Decodes that produce an alpha channel
    /// (<see cref="PixelFormat.Rgba32"/>/<see cref="PixelFormat.Rgba64"/>) are excluded from the pixel
    /// comparison entirely: confirmed empirically against this corpus that SkiaSharp's <see cref="SKBitmap.Decode(string)"/>
    /// premultiplies alpha (basn6a08.png — real alpha channel — measured ~4.4 average per-channel RGB
    /// difference; tbrn2c08.png — tRNS-promoted alpha with transparent regions — measured ~112.8), the
    /// same caveat already documented for Bmp. Gamma/color-space-bearing files (gAMA/cHRM/sRGB) are
    /// NOT excluded — also confirmed empirically that SkiaSharp applies no gamma correction on decode
    /// (g25n2c08.png, cs5n2c08.png, ccwn2c08.png all measured 0.000 average difference), so PeachImage's
    /// own no-color-management default agrees with it exactly. Interlaced files are likewise NOT
    /// excluded — a decode divergence there would be a real bug, not an expected difference.
    /// </summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        Image? peachImage;
        try
        {
            using var stream = File.OpenRead(path);
            peachImage = new PngDecoder().Decode(stream);
        }
        catch (PngFormatException)
        {
            return;
        }

        using (peachImage)
        {
            if (peachImage.Width < 1 || peachImage.Height < 1)
            {
                return;
            }

            if (peachImage.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Rgba64)
            {
                return;
            }

            using var skiaBitmap = SKBitmap.Decode(path);
            if (skiaBitmap is null || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
            {
                return;
            }

            double averageDifference = ComputeAverageRgbDifference(peachImage, skiaBitmap);
            Assert.True(averageDifference < 1.0, $"{Path.GetFileName(path)}: average per-channel difference from SkiaSharp too high: {averageDifference:F2}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = new PngDecoder().Decode(stream);
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
        int channels = peachImage.PixelFormat.GetChannelCount();
        bool is16Bit = peachImage.PixelFormat.GetBytesPerSample() == 2;
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
                if (is16Bit)
                {
                    var samples = MemoryMarshal.Cast<byte, ushort>(span.Slice(offset, bytesPerPixel));
                    r = samples[0] / 257.0; // 16-bit -> 8-bit display-range downsample, for comparison purposes only.
                    g = (channels >= 2 ? samples[1] : samples[0]) / 257.0;
                    b = (channels >= 3 ? samples[2] : samples[0]) / 257.0;
                }
                else
                {
                    r = span[offset];
                    g = channels >= 2 ? span[offset + 1] : r;
                    b = channels >= 3 ? span[offset + 2] : r;
                }

                sum += Math.Abs(r - skiaPixel.Red) + Math.Abs(g - skiaPixel.Green) + Math.Abs(b - skiaPixel.Blue);
                count++;
            }
        }

        return count == 0 ? 0 : sum / (count * 3);
    }
}
