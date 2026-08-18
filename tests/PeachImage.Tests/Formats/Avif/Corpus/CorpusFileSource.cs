using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// <c>MemberData</c> source enumerating the libavif <c>tests/data</c> corpus. Yields a single
/// <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/>
/// for why) when the corpus isn't available, so corpus-driven test classes report a genuine skip instead of
/// failing; <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> AvifFiles() => EnumerateFiles(CorpusPaths.LibavifTestDataRoot);

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External AVIF test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.avif", SearchOption.AllDirectories))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }
}
