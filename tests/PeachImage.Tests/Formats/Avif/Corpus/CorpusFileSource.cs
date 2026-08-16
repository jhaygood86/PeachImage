namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// <c>MemberData</c> source enumerating the libavif <c>tests/data</c> corpus. Returns an empty set
/// (rather than throwing) when the corpus isn't available, so corpus-driven test classes simply report
/// zero cases instead of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> AvifFiles() => EnumerateFiles(CorpusPaths.LibavifTestDataRoot);

    private static IEnumerable<object[]> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.avif", SearchOption.AllDirectories))
        {
            yield return [file];
        }
    }
}
