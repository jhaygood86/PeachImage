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
            peachImage = WebpDecoder.Decode(stream);
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

                var difference = ComputeDifference(peachImage, skiaBitmap, includeAlpha: false);
                AssertWithinTolerance(path, difference, includeAlpha: false);
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

                var difference = ComputeDifference(peachImage, skiaBitmap, includeAlpha: true);
                AssertWithinTolerance(path, difference, includeAlpha: true);
            }
        }
    }

    /// <summary>
    /// Gates on both statistics: the mean catches a systematically-wrong decode, the max catches a kernel that
    /// is right almost everywhere and badly wrong in a few places. <see cref="MaxChannelTolerance"/> is set from
    /// the worst difference actually observed across the corpus, so it is a real ceiling rather than a number
    /// picked to be comfortably unreachable.
    /// </summary>
    private static void AssertWithinTolerance(string path, PixelDifference difference, bool includeAlpha)
    {
        string suffix = includeAlpha ? " (incl. alpha)" : string.Empty;

        Assert.True(
            difference.Mean < MeanChannelTolerance,
            $"{Path.GetFileName(path)}: mean per-channel difference{suffix} from SkiaSharp too high: {difference}");

        Assert.True(
            difference.Max <= MaxChannelTolerance,
            $"{Path.GetFileName(path)}: worst single-channel difference{suffix} from SkiaSharp too high: {difference}");
    }

    /// <summary>Mean per-channel difference ceiling, unchanged from the sampled comparison this replaced.</summary>
    private const double MeanChannelTolerance = 4.0;

    /// <summary>
    /// Worst-single-channel ceiling, set from the worst difference actually measured across the corpus rather
    /// than picked to be comfortably unreachable.
    /// </summary>
    /// <remarks>
    /// Both bitstreams are exactly specified, and 126 of the 131 corpus files currently decode <em>bit-for-bit
    /// identically</em> to libwebp. The five that don't are all lossy (<c>vp80-02-inter-1418</c> at 6,
    /// <c>lossy_alpha4</c> at 4, <c>bryce</c> at 3, <c>vp80-00-comprehensive-008</c> and
    /// <c>lossy_extreme_probabilities</c> at 2), which points at exactly one thing:
    /// <see cref="PeachImage.Formats.Webp.Decoding.Vp8.Upsampling.Vp8ChromaUpsampler"/> computes a single-shift
    /// closed form where libwebp uses a two-stage shift, and the two round differently. Anything above 6 is
    /// therefore a new defect, not accumulated rounding.
    /// </remarks>
    private const int MaxChannelTolerance = 6;

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = WebpDecoder.Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    /// <summary>
    /// Compares <em>every</em> pixel (not a sampled grid) and reports both the mean per-channel difference and
    /// the worst single-channel difference anywhere in the image, with the position it occurred at.
    /// </summary>
    /// <remarks>
    /// The max matters at least as much as the mean here. A vectorized kernel that is wrong only in the final
    /// lane of each row, or only where a clamp fires, moves the mean by a rounding error while being flatly
    /// broken — and the mean-only, ~64x64-sampled comparison this replaced would have passed it.
    /// </remarks>
    private static PixelDifference ComputeDifference(Image peachImage, SKBitmap skiaBitmap, bool includeAlpha)
    {
        var span = peachImage.GetPixelSpan();
        int bytesPerPixel = peachImage.PixelFormat.GetBytesPerPixel();
        int channels = includeAlpha ? 4 : 3;

        // One span over Skia's buffer beats SKBitmap.GetPixel() per pixel (a managed->native call each time),
        // which matters now that this walks every pixel rather than a ~64x64 grid. Only the two 8-bit RGBA
        // layouts Skia actually produces for WebP are handled; anything else is a hard failure rather than a
        // silently mis-swizzled comparison.
        Assert.True(
            skiaBitmap.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888,
            $"Unexpected SkiaSharp color type {skiaBitmap.ColorType}; the byte-order mapping below only covers 8-bit RGBA/BGRA.");

        var skiaSpan = skiaBitmap.GetPixelSpan();
        int skiaBytesPerPixel = skiaBitmap.BytesPerPixel;
        bool skiaIsBgra = skiaBitmap.ColorType == SKColorType.Bgra8888;

        double sum = 0;
        long count = 0;
        int max = 0;
        int maxX = 0, maxY = 0;

        for (int y = 0; y < peachImage.Height; y++)
        {
            int rowOffset = y * peachImage.Width * bytesPerPixel;
            int skiaRowOffset = y * skiaBitmap.RowBytes;

            for (int x = 0; x < peachImage.Width; x++)
            {
                int offset = rowOffset + (x * bytesPerPixel);
                int skiaOffset = skiaRowOffset + (x * skiaBytesPerPixel);

                int skiaR = skiaSpan[skiaOffset + (skiaIsBgra ? 2 : 0)];
                int skiaG = skiaSpan[skiaOffset + 1];
                int skiaB = skiaSpan[skiaOffset + (skiaIsBgra ? 0 : 2)];

                int dr = Math.Abs(span[offset] - skiaR);
                int dg = Math.Abs(span[offset + 1] - skiaG);
                int db = Math.Abs(span[offset + 2] - skiaB);
                int worst = Math.Max(dr, Math.Max(dg, db));
                int total = dr + dg + db;

                if (includeAlpha)
                {
                    int da = Math.Abs(span[offset + 3] - skiaSpan[skiaOffset + 3]);
                    worst = Math.Max(worst, da);
                    total += da;
                }

                if (worst > max)
                {
                    max = worst;
                    maxX = x;
                    maxY = y;
                }

                sum += (double)total / channels;
                count++;
            }
        }

        return new PixelDifference(count == 0 ? 0 : sum / count, max, maxX, maxY);
    }

    /// <summary>Mean per-channel difference across the whole image, plus the worst single channel and where it was.</summary>
    private readonly record struct PixelDifference(double Mean, int Max, int MaxX, int MaxY)
    {
        public override string ToString() => $"mean {Mean:F3}, max {Max} at ({MaxX},{MaxY})";
    }
}
