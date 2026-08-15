namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored WebP test corpus lives on disk. Independent of the Bmp/Gif/Jpeg/Png corpora's roots/markers, so a partial or failed fetch of one format never blocks another.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus/webp</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the libwebp-test-data suite's <c>.webp</c> fixtures land.</summary>
    public static string LibwebpTestDataRoot => Path.Combine(Root, "libwebp-test-data");

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
        return Path.Combine(repoRoot, "tests", "corpus", "webp");
    }
}
