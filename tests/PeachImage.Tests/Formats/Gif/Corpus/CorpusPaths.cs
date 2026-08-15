namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored GIF test corpus lives on disk. Independent of the Bmp/Jpeg corpora's roots/markers, so a partial or failed fetch of one format never blocks another.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus/gif</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the giflib test suite's <c>pic/</c> and <c>tests/wedge.gif</c> land.</summary>
    public static string GiflibRoot => Path.Combine(Root, "giflib-test-suite");

    /// <summary>Where the W3C GIF test assets (image format test page) land.</summary>
    public static string W3cRoot => Path.Combine(Root, "w3c-gif-test-assets");

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
        return Path.Combine(repoRoot, "tests", "corpus", "gif");
    }
}
