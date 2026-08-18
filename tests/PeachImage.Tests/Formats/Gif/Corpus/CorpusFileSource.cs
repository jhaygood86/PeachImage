using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the two GIF corpora. Each yields a single <see cref="CorpusSkip"/>
/// row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/> for why) when the
/// corpus isn't available, so corpus-driven test classes report a genuine skip instead of failing;
/// <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> GiflibFiles() => EnumerateFiles(CorpusPaths.GiflibRoot);

    public static IEnumerable<TheoryDataRow<string>> W3cFiles() => EnumerateFiles(CorpusPaths.W3cRoot);

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External GIF test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.gif", SearchOption.AllDirectories))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }
}
