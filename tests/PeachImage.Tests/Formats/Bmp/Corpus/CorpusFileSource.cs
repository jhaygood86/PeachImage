using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Bmp.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the BMP conformance corpus's three subsets. Each yields a single
/// <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/>
/// for why) when the corpus isn't available, so corpus-driven test classes report a genuine skip instead of
/// failing; <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> ValidFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "valid"));

    public static IEnumerable<TheoryDataRow<string>> NonConformantFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "non-conformant"));

    public static IEnumerable<TheoryDataRow<string>> InvalidFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "invalid"));

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External BMP test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }
}
