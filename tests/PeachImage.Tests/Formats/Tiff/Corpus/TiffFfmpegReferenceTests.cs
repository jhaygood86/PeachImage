using System.Globalization;
using System.IO.Hashing;
using System.Text;
using PeachImage.Formats.Tiff;

namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>
/// Asserts that decoding every corpus TIFF file this decoder can handle in an 8-bit, alpha-free pixel format
/// (Gray8/Rgb24 — see <see cref="TiffFfmpegReferenceBaseline"/>'s remarks for why 16-bit and CMYK are out of
/// scope for this specific check) produces exactly the pixels <c>ffmpeg</c>'s own, independent TIFF decoder
/// produced for the same file. This is a real correctness check against ground truth, not the
/// self-referential "did the decoder's output change" regression check
/// <c>AvifDecodeHashTests</c>/<c>WebpDecodeHashTests</c> are — see the project plan.
/// </summary>
/// <remarks>
/// Rgba32/Rgba64 are excluded too, confirmed empirically rather than assumed safe: for a TIFF with
/// associated (premultiplied) alpha (<c>ExtraSamples=1</c>), <c>ffmpeg</c>'s <c>-pix_fmt rgba</c> output is
/// the *raw, still-premultiplied* samples passed straight through, not the straight (un-premultiplied) alpha
/// this decoder correctly produces to match <c>PixelFormat.Rgba32</c>'s established straight-alpha
/// convention elsewhere in this codebase (confirmed by inspecting raw pixel bytes from a failing comparison:
/// ffmpeg's R channel equaled its A channel exactly, the signature of untouched premultiplied data). Since
/// this decoder correctly diverges from <c>ffmpeg</c> here rather than having a bug, and there's no way to
/// tell from the decoded <see cref="Image"/> alone whether a given RGBA source was premultiplied or not,
/// excluding every alpha-bearing file from this specific comparison is simpler and safer than trying to
/// detect just the premultiplied ones.
/// </remarks>
public class TiffFfmpegReferenceTests
{
    [Fact]
    public void Decode_MatchesFfmpegReferenceForComparablePixelFormats()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "TIFF corpus is not available.");

        var inputs = TiffFfmpegReferenceBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        if (TiffFfmpegReferenceBaseline.IsWriteMode)
        {
            var computed = new SortedDictionary<string, TiffFfmpegReferenceRecord>(StringComparer.Ordinal);
            foreach (var (key, path) in inputs)
            {
                computed[key] = TiffFfmpegReferenceBaseline.Compute(path);
            }

            TiffFfmpegReferenceBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the decoder currently does.
            Assert.Fail($"Baseline regenerated at {TiffFfmpegReferenceBaseline.BaselinePath}. " +
                        $"Re-run without {TiffFfmpegReferenceBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = TiffFfmpegReferenceBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0,
            $"No baseline at {TiffFfmpegReferenceBaseline.BaselinePath}; generate it with " +
            $"{TiffFfmpegReferenceBaseline.WriteModeVariable}=write (requires ffmpeg/ffprobe on PATH).");

        var failures = new StringBuilder();
        int compared = 0;

        foreach (var (key, path) in inputs)
        {
            if (!baseline.TryGetValue(key, out var reference) || reference.Result == TiffFfmpegReferenceBaseline.SkippedMarker)
            {
                continue; // ffmpeg couldn't decode this one either -- no oracle value here.
            }

            Image? image;
            try
            {
                using var stream = File.OpenRead(path);
                image = TiffDecoder.Decode(stream);
            }
            catch (TiffFormatException)
            {
                continue; // This decoder rejected it -- already covered by the broader graceful-decode corpus test.
            }

            using (image)
            {
                if (image.PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24))
                {
                    continue;
                }

                byte[] rgba = ToRgba8(image);
                string actual = string.Create(CultureInfo.InvariantCulture, $"{image.Width}x{image.Height}:{Convert.ToHexString(XxHash128.Hash(rgba))}");
                compared++;

                if (actual != reference.Result)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: expected {reference.Result}, got {actual}");
                }
            }
        }

        Assert.SkipWhen(compared == 0, "No corpus file was decodable by both this decoder and ffmpeg in a directly-comparable pixel format.");
        Assert.True(
            failures.Length == 0,
            $"TIFF decode disagreed with the ffmpeg reference for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {compared} comparable files:\n{failures}");
    }

    private static byte[] ToRgba8(Image image)
    {
        int pixelCount = image.Width * image.Height;
        var output = new byte[pixelCount * 4];
        var src = image.GetPixelSpan();

        switch (image.PixelFormat)
        {
            case PixelFormat.Gray8:
                for (int i = 0; i < pixelCount; i++)
                {
                    byte v = src[i];
                    int d = i * 4;
                    output[d] = v;
                    output[d + 1] = v;
                    output[d + 2] = v;
                    output[d + 3] = 255;
                }

                break;

            case PixelFormat.Rgb24:
                for (int i = 0; i < pixelCount; i++)
                {
                    int s = i * 3, d = i * 4;
                    output[d] = src[s];
                    output[d + 1] = src[s + 1];
                    output[d + 2] = src[s + 2];
                    output[d + 3] = 255;
                }

                break;

            default:
                throw new InvalidOperationException($"Unreachable: {image.PixelFormat} is not one of the comparable formats.");
        }

        return output;
    }
}
