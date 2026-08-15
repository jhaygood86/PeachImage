using System.Globalization;
using System.Text;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Asserts that decoding every corpus and benchmark WebP asset still produces exactly the pixels recorded in
/// <see cref="WebpDecodeHashBaseline.BaselinePath"/>. This is the change detector that makes performance
/// refactoring of the decoder safe: <see cref="WebpCorpusTests"/>'s SkiaSharp differential samples pixels and
/// tolerates small average error by design (it's checking correctness against an independent decoder), which
/// makes it far too loose to notice, say, a SIMD kernel that's wrong only in the last lane of each row.
/// </summary>
/// <remarks>
/// A mismatch is not automatically a bug — some changes intentionally move output closer to libwebp. It does
/// always mean "look at this and decide", and the fix is to justify the change via
/// <see cref="WebpCorpusTests"/> and regenerate the baseline in the same commit.
/// </remarks>
public class WebpDecodeHashTests
{
    /// <summary>Runs as one test rather than a per-file theory so a change reports as a single aggregated diff, and so write mode can rewrite the whole file atomically.</summary>
    [Fact]
    public void Decode_ProducesTheSamePixelsAsTheRecordedBaseline()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "WebP corpus is not available.");

        var inputs = WebpDecodeHashBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        var computed = new SortedDictionary<string, WebpDecodeHashRecord>(StringComparer.Ordinal);
        foreach (var (key, path) in inputs)
        {
            computed[key] = WebpDecodeHashBaseline.Compute(path);
        }

        if (WebpDecodeHashBaseline.IsWriteMode)
        {
            WebpDecodeHashBaseline.Save(computed);

            // Regenerating must never be able to turn a run green -- otherwise the env var becomes a way to
            // silently accept whatever the decoder currently does.
            Assert.Fail($"Baseline regenerated at {WebpDecodeHashBaseline.BaselinePath}. " +
                        $"Re-run without {WebpDecodeHashBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = WebpDecodeHashBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0, $"No baseline at {WebpDecodeHashBaseline.BaselinePath}; generate it with {WebpDecodeHashBaseline.WriteModeVariable}=write.");

        var failures = new StringBuilder();
        foreach (var (key, actual) in computed)
        {
            if (!baseline.TryGetValue(key, out var expected))
            {
                // Corpus growth must be a deliberate, reviewed re-baseline -- never a silent widening of what
                // is (and isn't) covered.
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: not in the baseline (new fixture?)");
                continue;
            }

            if (expected.InputHash != actual.InputHash)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: INPUT FILE changed ({expected.InputHash} -> {actual.InputHash}); the fixture itself differs, not the decoder");
                continue;
            }

            if (expected.Result != actual.Result)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {expected.Result} -> {actual.Result}");
            }
        }

        // Baseline entries whose file is simply absent are skipped (a partial corpus fetch), not failed.
        Assert.True(
            failures.Length == 0,
            $"WebP decode output changed for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {computed.Count} inputs:\n{failures}");
    }
}
