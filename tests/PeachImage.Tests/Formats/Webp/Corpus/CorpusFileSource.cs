namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// <c>MemberData</c> source enumerating the libwebp-test-data corpus. Returns an empty set (rather than
/// throwing) when the corpus isn't available, so corpus-driven test classes simply report zero cases instead
/// of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> WebpFiles() => EnumerateFiles(CorpusPaths.LibwebpTestDataRoot);

    private static IEnumerable<object[]> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.webp", SearchOption.AllDirectories))
        {
            yield return [file];
        }
    }
}
