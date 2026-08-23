using System.Globalization;
using System.Text;
using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Asserts that decoding every corpus AVIF file this decoder can handle in an 8-bit, alpha-free pixel format
/// (Gray8/Rgb24 -- see <see cref="AvifFfmpegReferenceBaseline"/>'s remarks for why 10/12-bit and alpha are out
/// of scope for this specific check) produces pixels matching, within a measured tolerance, what <c>ffmpeg</c>'s
/// own, independent AVIF decoder (<c>libdav1d</c>) produced for the same file at the same sampled positions.
/// This is a real correctness check against ground truth, not the self-referential "did the decoder's output
/// change" regression check <see cref="AvifDecodeHashTests"/> is. Before this test existed, AVIF had no
/// independent decode oracle at all -- SkiaSharp (this repo's oracle for Bmp/Gif/Jpeg/Png/Webp) has no AVIF
/// codec. A tolerance (mirroring <c>WebpCorpusTests</c>' <c>CorpusAssertions.AssertWithinTolerance</c>), not an
/// exact match, is used deliberately -- see <see cref="AvifFfmpegReferenceBaseline"/>'s remarks for the
/// empirical investigation (a genuine, now-fixed decoder bug, plus a legitimate, already-documented
/// floating-point/chroma-upsampling divergence) that established why.
/// </summary>
public class AvifFfmpegReferenceTests
{
    /// <summary>
    /// Mean per-channel difference ceiling. Comfortably above every legitimate value measured across the
    /// corpus (typically well under 2) while still catching a systematically-wrong decode.
    /// </summary>
    internal const double MeanChannelTolerance = 5.0;

    /// <summary>
    /// Worst-single-channel ceiling. Set from the worst legitimate difference actually measured across the
    /// corpus (a photographic, 4:2:0 file -- <c>kodim03_yuv420_8bpc.avif</c>/<c>kodim23_yuv420_8bpc.avif</c> --
    /// where <c>Av1YuvToRgbConverter</c>'s documented nearest-neighbor chroma upsampling diverges most
    /// from <c>ffmpeg</c>/<c>libdav1d</c>'s bilinear one), not picked to be comfortably unreachable. 4:4:4
    /// (unsubsampled) files measure far lower, typically within 1-4 (ordinary last-bit floating-point rounding
    /// between two independent YUV-&gt;RGB matrix implementations). Anything meaningfully above this is
    /// therefore a new defect, not accumulated rounding or the known upsampling gap.
    /// </summary>
    internal const int MaxChannelTolerance = 90;

    [Fact]
    public void Decode_MatchesFfmpegReferenceForComparablePixelFormatsWithinTolerance()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifFfmpegReferenceBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        if (AvifFfmpegReferenceBaseline.IsWriteMode)
        {
            var computed = new SortedDictionary<string, AvifFfmpegReferenceRecord>(StringComparer.Ordinal);
            foreach (var (key, path) in inputs)
            {
                computed[key] = AvifFfmpegReferenceBaseline.Compute(path);
            }

            AvifFfmpegReferenceBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the decoder currently does.
            Assert.Fail($"Baseline regenerated at {AvifFfmpegReferenceBaseline.BaselinePath}. " +
                        $"Re-run without {AvifFfmpegReferenceBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = AvifFfmpegReferenceBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0,
            $"No baseline at {AvifFfmpegReferenceBaseline.BaselinePath}; generate it with " +
            $"{AvifFfmpegReferenceBaseline.WriteModeVariable}=write (requires ffmpeg/ffprobe with libdav1d on PATH).");

        var failures = new StringBuilder();
        int compared = 0;

        foreach (var (key, path) in inputs)
        {
            if (!baseline.TryGetValue(key, out var reference) || reference.Result == AvifFfmpegReferenceBaseline.SkippedMarker)
            {
                continue; // ffmpeg couldn't decode this one either (or it's alpha/grid/animated/irot/imir/clap) -- no oracle value here.
            }

            if (!TryParseResult(reference.Result, out int refWidth, out int refHeight, out byte[] refSamples))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: malformed baseline record {reference.Result}");
                continue;
            }

            Image? image;
            try
            {
                using var stream = File.OpenRead(path);
                image = AvifDecoder.Decode(stream);
            }
            catch (AvifDecodingException)
            {
                continue; // This decoder rejected it -- already covered by the broader graceful-decode corpus test.
            }
            catch (AvifUnsupportedFeatureException)
            {
                continue; // Out of this phase's decode scope -- already covered by the broader graceful-decode corpus test.
            }

            using (image)
            {
                if (image.PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24))
                {
                    continue;
                }

                if (image.Width != refWidth || image.Height != refHeight)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: dimensions disagree with ffmpeg ({refWidth}x{refHeight} vs {image.Width}x{image.Height})");
                    compared++;
                    continue;
                }

                byte[] actualSamples = SamplePixels(image);
                var difference = ComputeDifference(refSamples, actualSamples);
                compared++;

                if (difference.Mean >= MeanChannelTolerance || difference.Max > MaxChannelTolerance)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {difference} (mean tolerance {MeanChannelTolerance}, max tolerance {MaxChannelTolerance})");
                }
            }
        }

        Assert.SkipWhen(compared == 0, "No corpus file was decodable by both this decoder and ffmpeg in a directly-comparable pixel format.");
        Assert.True(
            failures.Length == 0,
            $"AVIF decode disagreed with the ffmpeg reference for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {compared} comparable files:\n{failures}");
    }

    private static bool TryParseResult(string result, out int width, out int height, out byte[] samples)
    {
        width = 0;
        height = 0;
        samples = [];

        int colon = result.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        string dimensions = result[..colon];
        int x = dimensions.IndexOf('x');
        if (x < 0
            || !int.TryParse(dimensions[..x], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(dimensions[(x + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        {
            return false;
        }

        try
        {
            samples = Convert.FromHexString(result[(colon + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Extracts the same evenly-spaced, deterministic pixel positions <see cref="AvifFfmpegReferenceBaseline.SamplePositions"/> defines, as packed RGBA8 bytes.</summary>
    private static byte[] SamplePixels(Image image)
    {
        int pixelCount = image.Width * image.Height;
        var src = image.GetPixelSpan();
        var output = new List<byte>();

        foreach (int index in AvifFfmpegReferenceBaseline.SamplePositions(pixelCount))
        {
            switch (image.PixelFormat)
            {
                case PixelFormat.Gray8:
                    byte v = src[index];
                    output.Add(v);
                    output.Add(v);
                    output.Add(v);
                    output.Add(255);
                    break;

                case PixelFormat.Rgb24:
                    int s = index * 3;
                    output.Add(src[s]);
                    output.Add(src[s + 1]);
                    output.Add(src[s + 2]);
                    output.Add(255);
                    break;

                default:
                    throw new InvalidOperationException($"Unreachable: {image.PixelFormat} is not one of the comparable formats.");
            }
        }

        return [.. output];
    }

    /// <summary>Mean per-channel difference across every sampled pixel (RGB only -- alpha is always opaque in this comparable set, see the class remarks), plus the worst single-channel difference anywhere in the sample.</summary>
    private static PixelDifference ComputeDifference(byte[] expected, byte[] actual)
    {
        int count = Math.Min(expected.Length, actual.Length) / 4;
        double sum = 0;
        long channelCount = 0;
        int max = 0;

        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            for (int c = 0; c < 3; c++)
            {
                int diff = Math.Abs(expected[o + c] - actual[o + c]);
                sum += diff;
                channelCount++;
                if (diff > max)
                {
                    max = diff;
                }
            }
        }

        return new PixelDifference(channelCount == 0 ? 0 : sum / channelCount, max);
    }

    private readonly record struct PixelDifference(double Mean, int Max)
    {
        public override string ToString() => $"mean {Mean:F3}, max {Max}";
    }
}
