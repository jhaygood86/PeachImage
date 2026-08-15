namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored PNG test corpus lives on disk. Independent of the Bmp/Jpeg corpora's root/marker, so a partial or failed fetch of one format never blocks the others.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus/png</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the Imazen <c>codec-corpus</c> repo's <c>pngsuite</c> subtree lands.</summary>
    public static string PngSuiteRoot => Path.Combine(Root, "imazen-codec-corpus", "pngsuite");

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
        return Path.Combine(repoRoot, "tests", "corpus", "png");
    }
}
