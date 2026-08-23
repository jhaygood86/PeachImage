using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the TIFF conformance corpus's three subsets. Each yields a single
/// <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/>
/// for why) when the corpus isn't available, so corpus-driven test classes report a genuine skip instead of
/// failing; <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> ValidFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "tiff-conformance", "valid"));

    public static IEnumerable<TheoryDataRow<string>> EdgeCaseFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "tiff-conformance", "edge-cases"));

    public static IEnumerable<TheoryDataRow<string>> RobustnessFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "tiff-conformance", "robustness"));

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External TIFF test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }
}
