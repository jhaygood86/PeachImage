namespace PeachImage.Tests.Formats.Bmp.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating the BMP conformance corpus's three subsets. Each returns an empty
/// set (rather than throwing) when the corpus isn't available, so corpus-driven test classes simply report
/// zero cases instead of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> ValidFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "valid"));

    public static IEnumerable<object[]> NonConformantFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "non-conformant"));

    public static IEnumerable<object[]> InvalidFiles() =>
        EnumerateFiles(Path.Combine(CorpusPaths.ImazenRoot, "bmp-conformance", "invalid"));

    private static IEnumerable<object[]> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            yield return [file];
        }
    }
}
