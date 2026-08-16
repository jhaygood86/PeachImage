using System.Globalization;
using System.Text;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Asserts that decoding every corpus AVIF asset still produces exactly the pixels recorded in
/// <see cref="AvifDecodeHashBaseline.BaselinePath"/>. This is the change detector that makes performance
/// refactoring of the decoder (vectorizing a kernel, pooling a buffer) safe: <c>AvifCorpusTests</c> only
/// checks graceful behavior, not exact pixel values, so it would never notice a SIMD kernel that's wrong
/// only in the last lane of a row. Mirrors <c>WebpDecodeHashTests</c>.
/// </summary>
/// <remarks>
/// A mismatch is not automatically a bug — some changes intentionally move output (a newly-supported
/// feature turning a SKIPPED entry into real pixels, for instance). It does always mean "look at this and
/// decide", and the fix is to regenerate the baseline in the same commit as the change that caused it.
/// </remarks>
public class AvifDecodeHashTests
{
    [Fact]
    public void Decode_ProducesTheSamePixelsAsTheRecordedBaseline()
    {
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");

        var inputs = AvifDecodeHashBaseline.EnumerateInputs();
        Assert.NotEmpty(inputs);

        var computed = new SortedDictionary<string, AvifDecodeHashRecord>(StringComparer.Ordinal);
        foreach (var (key, path) in inputs)
        {
            computed[key] = AvifDecodeHashBaseline.Compute(path);
        }

        if (AvifDecodeHashBaseline.IsWriteMode)
        {
            AvifDecodeHashBaseline.Save(computed);

            Assert.Fail($"Baseline regenerated at {AvifDecodeHashBaseline.BaselinePath}. " +
                        $"Re-run without {AvifDecodeHashBaseline.WriteModeVariable} and review the diff.");
        }

        var baseline = AvifDecodeHashBaseline.Load();
        Assert.SkipWhen(baseline.Count == 0, $"No baseline at {AvifDecodeHashBaseline.BaselinePath}; generate it with {AvifDecodeHashBaseline.WriteModeVariable}=write.");

        var failures = new StringBuilder();
        foreach (var (key, actual) in computed)
        {
            if (!baseline.TryGetValue(key, out var expected))
            {
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

        Assert.True(
            failures.Length == 0,
            $"AVIF decode output changed for {failures.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} of {computed.Count} inputs:\n{failures}");
    }
}
