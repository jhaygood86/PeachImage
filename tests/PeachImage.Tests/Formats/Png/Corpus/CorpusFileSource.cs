using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the PngSuite corpus's buckets, derived from
/// <see cref="PngSuiteFileName.Classify"/> since (unlike Bmp/Jpeg's pre-bucketed source directories)
/// PngSuite ships as a flat file set. Only two buckets are exposed — <see cref="ValidFiles"/> and
/// <see cref="InvalidFiles"/> — rather than mirroring Bmp/Jpeg's three-way valid/non-conformant/invalid
/// split: PNG's one known "real decoders may legitimately disagree" case (SkiaSharp premultiplies alpha
/// on decode, confirmed empirically against this same corpus) is handled directly inside
/// <see cref="PngCorpusAssertions"/> by skipping the pixel-fidelity comparison for any decode that
/// produces an alpha channel, rather than needing a separate corpus bucket for it.
/// Each yields a single <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows —
/// see <see cref="CorpusSkip"/> for why) when the corpus isn't available, so corpus-driven test classes
/// report a genuine skip instead of failing; <see cref="CorpusAvailabilityTests"/> makes the same signal
/// visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> ValidFiles() => Bucket(PngSuiteBucket.Valid);

    public static IEnumerable<TheoryDataRow<string>> InvalidFiles() => Bucket(PngSuiteBucket.Invalid);

    private static IEnumerable<TheoryDataRow<string>> Bucket(PngSuiteBucket target)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.PngSuiteRoot))
        {
            yield return CorpusSkip.Row("External PNG test corpus (PngSuite) is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(CorpusPaths.PngSuiteRoot, "*.png"))
        {
            if (PngSuiteFileName.Classify(file) == target)
            {
                yield return new TheoryDataRow<string>(file);
            }
        }
    }
}
