using PeachImage.Formats.Webp;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other than
/// <see cref="WebpDecodingException"/> for a file PeachImage chooses to reject (an animated WebP is the
/// expected case here — see <see cref="WebpDecoder"/>'s docs); and whenever both PeachImage and SkiaSharp (a
/// mature, real-world WebP decoder backed by libwebp itself) successfully decode the same file, their RGB
/// pixel output should agree closely.
/// </summary>
internal static class CorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="WebpDecodingException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        var task = Task.Run(() => TryDecode(path));
        if (!task.Wait(PerFileTimeout))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = task.GetAwaiter().GetResult();
        if (!succeeded && exception is not WebpDecodingException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    /// <summary>
    /// Combines <see cref="AssertDecodesGracefully"/> with a differential pixel comparison against SkiaSharp
    /// when both decoders succeed. Opaque (<see cref="PixelFormat.Rgb24"/>) images are compared against
    /// SkiaSharp's default RGB decode; alpha-bearing (<see cref="PixelFormat.Rgba32"/>) images are compared
    /// against an <em>unpremultiplied</em> SkiaSharp decode (via <see cref="SKCodec"/> +
    /// <see cref="SKAlphaType.Unpremul"/>) instead of the default premultiplied one, since a premultiplied
    /// oracle isn't a trustworthy RGB reference wherever alpha isn't fully opaque — this closes the gap
    /// Bmp's/Gif's corpus assertions leave open by simply excluding alpha images from comparison entirely.
    /// </summary>
    public static void AssertDecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        AssertDecodesGracefully(path);

        Image? peachImage;
        try
        {
            using var stream = File.OpenRead(path);
            peachImage = new WebpDecoder().Decode(stream);
        }
        catch (WebpDecodingException)
        {
            return;
        }

        using (peachImage)
        {
            if (peachImage.Width < 1 || peachImage.Height < 1)
            {
                return;
            }

            if (peachImage.PixelFormat == PixelFormat.Rgb24)
            {
                using var skiaBitmap = SKBitmap.Decode(path);
                if (skiaBitmap is null || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
                {
                    return;
                }

                double averageDifference = ComputeAverageDifference(peachImage, skiaBitmap, includeAlpha: false);
                Assert.True(averageDifference < 4.0, $"{Path.GetFileName(path)}: average per-channel difference from SkiaSharp too high: {averageDifference:F2}");
                return;
            }

            if (peachImage.PixelFormat == PixelFormat.Rgba32)
            {
                using var codec = SKCodec.Create(path);
                if (codec is null)
                {
                    return;
                }

                var unpremulInfo = codec.Info.WithAlphaType(SKAlphaType.Unpremul);
                using var skiaBitmap = new SKBitmap(unpremulInfo);
                var result = codec.GetPixels(unpremulInfo, skiaBitmap.GetPixels());
                if (result != SKCodecResult.Success || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
                {
                    return;
                }

                double averageDifference = ComputeAverageDifference(peachImage, skiaBitmap, includeAlpha: true);
                Assert.True(averageDifference < 4.0, $"{Path.GetFileName(path)}: average per-channel difference (incl. alpha) from SkiaSharp too high: {averageDifference:F2}");
            }
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = new WebpDecoder().Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static double ComputeAverageDifference(Image peachImage, SKBitmap skiaBitmap, bool includeAlpha)
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

                double diff = Math.Abs(r - skiaPixel.Red) + Math.Abs(g - skiaPixel.Green) + Math.Abs(b - skiaPixel.Blue);
                int channels = 3;

                if (includeAlpha)
                {
                    double a = span[offset + 3];
                    diff += Math.Abs(a - skiaPixel.Alpha);
                    channels = 4;
                }

                sum += diff / channels;
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
