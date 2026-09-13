using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// The standing-test counterpart to <c>tools/PeachImage.LibaomParity --corpus</c> -- see
/// <see cref="AvifLibaomEncodeParityBaseline"/>'s own remarks for the full design rationale. Two independent
/// checks, mirroring the project plan's own definition-of-done items 3 (byte-identical-or-tracked encode
/// parity, as a permanent standing test) and 4 (an independent round-trip pixel check, not implied by the
/// byte check alone):
///
/// <list type="bullet">
/// <item><see cref="Encode_RoundTripsExactlyToSourcePixels"/> -- unconditional, no baseline or external tool
/// needed: every corpus file this encoder can losslessly re-encode must decode back to the exact source
/// pixels. This is the hard, always-true correctness invariant lossless coding promises.</item>
/// <item><see cref="Encode_MatchesRecordedLibaomBaseline"/> -- baseline-gated (mirrors
/// <see cref="AvifDecodeHashTests"/>'s own exact-self-consistency philosophy): PeachImage's own current OBU
/// byte count for each corpus file must exactly match what was recorded in the checked-in baseline, keeping
/// the real <c>aomenc</c> reference byte count (and therefore the tracked byte-gap-vs-libaom) visible in the
/// same file without needing <c>aomenc</c> present at ordinary test-run time.</item>
/// </list>
/// </summary>
public class AvifLibaomEncodeParityTests
{
    private const int Effort = 2;

    [Fact]
    public void Encode_RoundTripsExactlyToSourcePixels()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifLibaomEncodeParityBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        // Each file's decode/encode/round-trip is fully independent (own local buffers; the shared
        // ArrayPool<int> instances backing the encoder's scratch buffers are inherently thread-safe, and the
        // encoder itself holds no mutable static state), so this runs across all available cores instead of
        // one file at a time -- this loop alone can otherwise dominate this test's real-world wall time given
        // the encoder's own scalar, no-SIMD, two-pass-doubled per-file cost (SIMD is deliberately scoped as
        // this project's own last phase, see the master plan's Phase 6 remarks).
        var failures = new ConcurrentQueue<string>();
        int compared = 0;

        Parallel.ForEach(inputs, item =>
        {
            var (key, path) = item;

            Image? source;
            try
            {
                using var stream = File.OpenRead(path);
                source = AvifDecoder.Decode(stream);
            }
            catch (AvifDecodingException)
            {
                return; // Already covered by AvifCorpusTests' own graceful-decode check.
            }
            catch (AvifUnsupportedFeatureException)
            {
                return; // Out of this decoder's current scope -- nothing for this encode-side check to exercise.
            }

            using (source)
            {
                if (source.PixelFormat != PixelFormat.Rgb24 || source.Width <= 0 || source.Height <= 0)
                {
                    return; // See AvifLibaomEncodeParityBaseline's identical scope note.
                }

                byte[] rgb = source.GetPixelSpan().ToArray();
                Interlocked.Increment(ref compared);

                Av1EncodedFrame frame;
                try
                {
                    frame = Av1FrameEncoder.Encode(rgb, source.Width, source.Height, monoChrome: false, quality: 75, lossless: true, Effort);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"  {key}: PeachImage encode threw {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                using var avifStream = new MemoryStream();
                AvifContainerWriter.Write(avifStream, frame);
                avifStream.Position = 0;

                try
                {
                    using var roundTripped = AvifDecoder.Decode(avifStream);
                    bool matches = roundTripped.Width == source.Width && roundTripped.Height == source.Height
                        && roundTripped.PixelFormat == PixelFormat.Rgb24 && roundTripped.GetPixelSpan().SequenceEqual(rgb);

                    if (!matches)
                    {
                        failures.Enqueue($"  {key}: round-tripped pixels differ from source (encode-then-decode is not lossless)");
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"  {key}: round-trip decode threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        Assert.SkipWhen(compared == 0, "No corpus file was both decodable and in this encoder's current Rgb24 scope.");
        Assert.True(
            failures.IsEmpty,
            $"Lossless encode-then-decode round-trip failed for {failures.Count} of {compared} files:\n{string.Join('\n', failures)}");
    }

    [Fact]
    public void Encode_MatchesRecordedLibaomBaseline()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifLibaomEncodeParityBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        if (AvifLibaomEncodeParityBaseline.IsWriteMode)
        {
            var computed = new SortedDictionary<string, AvifLibaomEncodeParityRecord>(StringComparer.Ordinal);
            foreach (var (key, path) in inputs)
            {
                computed[key] = AvifLibaomEncodeParityBaseline.Compute(path);
            }

            AvifLibaomEncodeParityBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the encoder currently does (see AvifDecodeHashTests/
            // AvifFfmpegReferenceTests' own identical guard).
            Assert.Fail($"Baseline regenerated at {AvifLibaomEncodeParityBaseline.BaselinePath}. " +
                        $"Re-run without {AvifLibaomEncodeParityBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = AvifLibaomEncodeParityBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0,
            $"No baseline at {AvifLibaomEncodeParityBaseline.BaselinePath}; generate it with " +
            $"{AvifLibaomEncodeParityBaseline.WriteModeVariable}=write (requires a local aomenc.exe -- see " +
            $"{AvifLibaomEncodeParityBaseline.AomencPathVariable}).");

        var failures = new ConcurrentQueue<string>();
        int compared = 0;
        long totalReference = 0;
        long totalActual = 0;

        // See Encode_RoundTripsExactlyToSourcePixels's own remarks on why per-file work is safe to parallelize.
        Parallel.ForEach(inputs, item =>
        {
            var (key, path) = item;

            if (!baseline.TryGetValue(key, out var recorded))
            {
                failures.Enqueue($"  {key}: not in the baseline (new fixture?)");
                return;
            }

            if (recorded.Result == AvifLibaomEncodeParityBaseline.SkippedMarker)
            {
                return;
            }

            string[] parts = recorded.Result.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int referenceBytes)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int recordedActualBytes))
            {
                failures.Enqueue($"  {key}: malformed baseline record '{recorded.Result}'");
                return;
            }

            using var stream = File.OpenRead(path);
            using var source = AvifDecoder.Decode(stream);
            byte[] rgb = source.GetPixelSpan().ToArray();

            int currentActualBytes;
            try
            {
                currentActualBytes = Av1FrameEncoder.Encode(rgb, source.Width, source.Height, monoChrome: false, quality: 75, lossless: true, Effort).ObuBytes.Length;
            }
            catch (Exception ex)
            {
                failures.Enqueue($"  {key}: PeachImage encode threw {ex.GetType().Name}: {ex.Message}");
                return;
            }

            Interlocked.Increment(ref compared);
            Interlocked.Add(ref totalReference, referenceBytes);
            Interlocked.Add(ref totalActual, currentActualBytes);

            if (currentActualBytes != recordedActualBytes)
            {
                failures.Enqueue($"  {key}: PeachImage's own byte count changed ({recordedActualBytes} -> {currentActualBytes}; aomenc reference={referenceBytes})");
            }
        });

        Assert.SkipWhen(compared == 0, "No corpus file had a comparable (non-skipped) baseline record.");

        string aggregate = totalReference > 0
            ? $" Aggregate at baseline time vs. now: reference={totalReference}, actual={totalActual} ({((double)totalActual - totalReference) / totalReference * 100.0:+0.00;-0.00}%)."
            : string.Empty;

        Assert.True(
            failures.IsEmpty,
            $"PeachImage's own encoded byte count changed for {failures.Count} of {compared} files " +
            $"(a deliberate improvement or an accidental regression -- either way, review and regenerate the baseline with {AvifLibaomEncodeParityBaseline.WriteModeVariable}=write):\n{string.Join('\n', failures)}{aggregate}");
    }
}
