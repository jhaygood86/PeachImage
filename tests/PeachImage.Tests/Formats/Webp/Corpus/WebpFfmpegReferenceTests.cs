using System.Globalization;
using System.IO.Hashing;
using System.Text;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Asserts that decoding every single-frame, VP8L (lossless) corpus WebP file produces exactly the pixels
/// <c>ffmpeg</c>'s own WebP decoder produced for the same file -- see <see cref="WebpFfmpegReferenceBaseline"/>'s
/// remarks for why lossy (VP8) files are out of scope for this specific, additive cross-check, and for why this
/// exists at all alongside <see cref="WebpCorpusTests"/> (SkiaSharp-differential, tolerant) and
/// <see cref="WebpDecodeHashTests"/> (self-referential regression baseline) rather than replacing either.
/// </summary>
public class WebpFfmpegReferenceTests
{
    [Fact]
    public void Decode_MatchesFfmpegReferenceForLosslessFiles()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "WebP corpus is not available.");

        var inputs = WebpFfmpegReferenceBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        if (WebpFfmpegReferenceBaseline.IsWriteMode)
        {
            var computed = new SortedDictionary<string, WebpFfmpegReferenceRecord>(StringComparer.Ordinal);
            foreach (var (key, path) in inputs)
            {
                computed[key] = WebpFfmpegReferenceBaseline.Compute(path);
            }

            WebpFfmpegReferenceBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the decoder currently does.
            Assert.Fail($"Baseline regenerated at {WebpFfmpegReferenceBaseline.BaselinePath}. " +
                        $"Re-run without {WebpFfmpegReferenceBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = WebpFfmpegReferenceBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0,
            $"No baseline at {WebpFfmpegReferenceBaseline.BaselinePath}; generate it with " +
            $"{WebpFfmpegReferenceBaseline.WriteModeVariable}=write (requires ffmpeg/ffprobe on PATH).");

        var failures = new StringBuilder();
        int compared = 0;

        foreach (var (key, path) in inputs)
        {
            if (!baseline.TryGetValue(key, out var reference) || reference.Result == WebpFfmpegReferenceBaseline.SkippedMarker)
            {
                continue; // Lossy, animated, or ffmpeg couldn't decode this one either -- no oracle value here.
            }

            Image? image;
            try
            {
                using var stream = File.OpenRead(path);
                image = WebpDecoder.Decode(stream);
            }
            catch (WebpDecodingException)
            {
                continue; // This decoder rejected it -- already covered by the broader graceful-decode corpus test.
            }

            using (image)
            {
                byte[] rgba = ToRgba8(image);
                string actual = string.Create(CultureInfo.InvariantCulture, $"{image.Width}x{image.Height}:{Convert.ToHexString(XxHash128.Hash(rgba))}");
                compared++;

                if (actual != reference.Result)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: expected {reference.Result}, got {actual}");
                }
            }
        }

        Assert.SkipWhen(compared == 0, "No corpus file was decodable by both this decoder and ffmpeg as a lossless WebP.");
        Assert.True(
            failures.Length == 0,
            $"WebP decode disagreed with the ffmpeg reference for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {compared} comparable files:\n{failures}");
    }

    /// <summary>Converts PeachImage's WebP output (always Rgb24 or Rgba32 -- there's no Gray8/16-bit WebP path) to 8-bit RGBA, matching the canonical layout <see cref="WebpFfmpegReferenceBaseline"/> stores.</summary>
    private static byte[] ToRgba8(Image image)
    {
        int pixelCount = image.Width * image.Height;
        var src = image.GetPixelSpan();

        if (image.PixelFormat == PixelFormat.Rgba32)
        {
            return src.ToArray();
        }

        var output = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 3, d = i * 4;
            output[d] = src[s];
            output[d + 1] = src[s + 1];
            output[d + 2] = src[s + 2];
            output[d + 3] = 255;
        }

        return output;
    }
}
