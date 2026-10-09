namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored JPEG XL test corpus lives on disk. Independent of every other format's corpus root/marker, so a partial or failed fetch of one format never blocks another.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus/jxl</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the libjxl <c>conformance</c> repository's <c>testcases</c> (input.jxl, ref.png, test.json) land.</summary>
    public static string ConformanceRoot => Path.Combine(Root, "libjxl-conformance");

    /// <summary>Where the libjxl <c>testdata</c> repository's <c>jxl</c> subtree lands.</summary>
    public static string TestDataRoot => Path.Combine(Root, "libjxl-testdata");

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
        return Path.Combine(repoRoot, "tests", "corpus", "jxl");
    }
}
