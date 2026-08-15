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
/// Each returns an empty set (rather than throwing) when the corpus isn't available, so corpus-driven
/// test classes simply report zero cases instead of failing; <see cref="CorpusAvailabilityTests"/> is
/// what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> ValidFiles() => Bucket(PngSuiteBucket.Valid);

    public static IEnumerable<object[]> InvalidFiles() => Bucket(PngSuiteBucket.Invalid);

    private static IEnumerable<object[]> Bucket(PngSuiteBucket target)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.PngSuiteRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(CorpusPaths.PngSuiteRoot, "*.png"))
        {
            if (PngSuiteFileName.Classify(file) == target)
            {
                yield return [file];
            }
        }
    }
}
