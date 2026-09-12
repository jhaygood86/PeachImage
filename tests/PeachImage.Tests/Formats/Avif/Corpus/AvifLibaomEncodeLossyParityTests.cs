using System.Globalization;
using System.Text;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// The lossy sibling of <see cref="AvifLibaomEncodeParityTests"/> -- see
/// <see cref="AvifLibaomEncodeLossyParityBaseline"/>'s own remarks for the full design rationale (not a
/// byte-identity target; a self-consistency regression detector that also tracks the real, currently large
/// lossy byte-gap-vs-libaom for the project plan's own not-yet-started Phase 4).
///
/// <list type="bullet">
/// <item><see cref="Encode_RoundTripsWithReasonablePsnr"/> -- unconditional, no baseline or external tool
/// needed: every corpus file this encoder can lossy re-encode at the fixed quality must decode back within
/// a sane PSNR of the source (mirrors <c>EncodeDecodeRoundTripTests</c>' own PSNR philosophy, but against
/// real corpus content instead of synthetic patterns -- catches gross encoder/decoder desync the byte-count
/// check below wouldn't, since two encoders can produce different-but-still-broken byte counts).</item>
/// <item><see cref="Encode_MatchesRecordedLibaomBaseline"/> -- baseline-gated (mirrors the lossless
/// sibling's own exact-self-consistency philosophy): PeachImage's own current lossy OBU byte count for each
/// corpus file must exactly match what was recorded in the checked-in baseline.</item>
/// </list>
/// </summary>
public class AvifLibaomEncodeLossyParityTests
{
    private const int Effort = 2;
    private const int Quality = 75;

    /// <summary>Matches <c>EncodeDecodeRoundTripTests</c>' own real-content minimum -- real photographic corpus content at Quality 75/effort 2 comfortably clears this; a lower PSNR here means real desync, not just an expected lossy-compression trade-off.</summary>
    private const double MinPsnrDb = 18.0;

    [Fact]
    public void Encode_RoundTripsWithReasonablePsnr()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifLibaomEncodeLossyParityBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        var failures = new StringBuilder();
        int compared = 0;

        foreach (var (key, path) in inputs)
        {
            Image? source;
            try
            {
                using var stream = File.OpenRead(path);
                source = AvifDecoder.Decode(stream);
            }
            catch (AvifDecodingException)
            {
                continue; // Already covered by AvifCorpusTests' own graceful-decode check.
            }
            catch (AvifUnsupportedFeatureException)
            {
                continue; // Out of this decoder's current scope -- nothing for this encode-side check to exercise.
            }

            using (source)
            {
                if (source.PixelFormat != PixelFormat.Rgb24 || source.Width <= 0 || source.Height <= 0)
                {
                    continue; // See AvifLibaomEncodeLossyParityBaseline's identical scope note.
                }

                byte[] rgb = source.GetPixelSpan().ToArray();
                compared++;

                Av1EncodedFrame frame;
                try
                {
                    frame = Av1FrameEncoder.Encode(rgb, source.Width, source.Height, monoChrome: false, Quality, lossless: false, Effort);
                }
                catch (Exception ex)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: PeachImage encode threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                using var avifStream = new MemoryStream();
                AvifContainerWriter.Write(avifStream, frame);
                avifStream.Position = 0;

                try
                {
                    using var roundTripped = AvifDecoder.Decode(avifStream);
                    if (roundTripped.Width != source.Width || roundTripped.Height != source.Height || roundTripped.PixelFormat != PixelFormat.Rgb24)
                    {
                        failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: round-tripped dimensions/format differ from source");
                        continue;
                    }

                    double psnr = ComputePsnrDb(rgb, roundTripped.GetPixelSpan());
                    if (psnr < MinPsnrDb)
                    {
                        failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: round-tripped PSNR {psnr:F2} dB below required {MinPsnrDb} dB");
                    }
                }
                catch (Exception ex)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: round-trip decode threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Assert.SkipWhen(compared == 0, "No corpus file was both decodable and in this encoder's current Rgb24 scope.");
        Assert.True(
            failures.Length == 0,
            $"Lossy encode-then-decode round-trip failed for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {compared} files:\n{failures}");
    }

    [Fact]
    public void Encode_MatchesRecordedLibaomBaseline()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifLibaomEncodeLossyParityBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        if (AvifLibaomEncodeLossyParityBaseline.IsWriteMode)
        {
            var computed = new SortedDictionary<string, AvifLibaomEncodeLossyParityRecord>(StringComparer.Ordinal);
            foreach (var (key, path) in inputs)
            {
                computed[key] = AvifLibaomEncodeLossyParityBaseline.Compute(path);
            }

            AvifLibaomEncodeLossyParityBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the encoder currently does (see AvifDecodeHashTests/
            // AvifFfmpegReferenceTests/the lossless sibling's own identical guard).
            Assert.Fail($"Baseline regenerated at {AvifLibaomEncodeLossyParityBaseline.BaselinePath}. " +
                        $"Re-run without {AvifLibaomEncodeLossyParityBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = AvifLibaomEncodeLossyParityBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0,
            $"No baseline at {AvifLibaomEncodeLossyParityBaseline.BaselinePath}; generate it with " +
            $"{AvifLibaomEncodeLossyParityBaseline.WriteModeVariable}=write (requires a local aomenc.exe -- see " +
            $"{AvifLibaomEncodeLossyParityBaseline.AomencPathVariable}).");

        var failures = new StringBuilder();
        int compared = 0;
        long totalReference = 0;
        long totalActual = 0;

        foreach (var (key, path) in inputs)
        {
            if (!baseline.TryGetValue(key, out var recorded))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: not in the baseline (new fixture?)");
                continue;
            }

            if (recorded.Result == AvifLibaomEncodeLossyParityBaseline.SkippedMarker)
            {
                continue;
            }

            string[] parts = recorded.Result.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int referenceBytes)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int recordedActualBytes))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: malformed baseline record '{recorded.Result}'");
                continue;
            }

            using var stream = File.OpenRead(path);
            using var source = AvifDecoder.Decode(stream);
            byte[] rgb = source.GetPixelSpan().ToArray();

            int currentActualBytes;
            try
            {
                currentActualBytes = Av1FrameEncoder.Encode(rgb, source.Width, source.Height, monoChrome: false, Quality, lossless: false, Effort).ObuBytes.Length;
            }
            catch (Exception ex)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: PeachImage encode threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            compared++;
            totalReference += referenceBytes;
            totalActual += currentActualBytes;

            if (currentActualBytes != recordedActualBytes)
            {
                failures.AppendLine(CultureInfo.InvariantCulture,
                    $"  {key}: PeachImage's own byte count changed ({recordedActualBytes} -> {currentActualBytes}; aomenc reference={referenceBytes})");
            }
        }

        Assert.SkipWhen(compared == 0, "No corpus file had a comparable (non-skipped) baseline record.");

        string aggregate = totalReference > 0
            ? $" Aggregate at baseline time vs. now: reference={totalReference}, actual={totalActual} ({((double)totalActual - totalReference) / totalReference * 100.0:+0.00;-0.00}%)."
            : string.Empty;

        Assert.True(
            failures.Length == 0,
            $"PeachImage's own lossy encoded byte count changed for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {compared} files " +
            $"(a deliberate improvement or an accidental regression -- either way, review and regenerate the baseline with {AvifLibaomEncodeLossyParityBaseline.WriteModeVariable}=write):\n{failures}{aggregate}");
    }

    private static double ComputePsnrDb(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sumSquaredError = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int diff = a[i] - b[i];
            sumSquaredError += (long)diff * diff;
        }

        double mse = (double)sumSquaredError / a.Length;
        return mse == 0 ? 100.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);
    }
}
