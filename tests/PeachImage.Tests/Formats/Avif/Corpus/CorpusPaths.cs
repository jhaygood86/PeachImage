namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored AVIF test corpus lives on disk. Independent of the other formats' corpus roots/markers, so a partial or failed AVIF fetch never blocks or is blocked by another format's.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus/avif</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the <c>libavif</c> project's <c>tests/data</c> conformance fixtures land.</summary>
    public static string LibavifTestDataRoot => Path.Combine(Root, "libavif-test-data");

    /// <summary>Written after a successful fetch; its presence means the corpus is ready to use.</summary>
    public static string MarkerFile => Path.Combine(Root, ".fetched");

    private static string ComputeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeachImage.slnx")))
        {
            dir = dir.Parent;
        }

        string repoRoot = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(repoRoot, "tests", "corpus", "avif");
    }
}
