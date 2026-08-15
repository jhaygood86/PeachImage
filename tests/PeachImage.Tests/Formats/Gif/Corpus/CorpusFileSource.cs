namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the two GIF corpora. Each returns an empty set (rather than
/// throwing) when the corpus isn't available, so corpus-driven test classes simply report zero cases instead
/// of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> GiflibFiles() => EnumerateFiles(CorpusPaths.GiflibRoot);

    public static IEnumerable<object[]> W3cFiles() => EnumerateFiles(CorpusPaths.W3cRoot);

    private static IEnumerable<object[]> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.gif", SearchOption.AllDirectories))
        {
            yield return [file];
        }
    }
}
