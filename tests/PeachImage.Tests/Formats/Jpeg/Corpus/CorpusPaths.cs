namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>Single source of truth for where the auto-fetched, gitignored external test corpora live on disk.</summary>
internal static class CorpusPaths
{
    /// <summary>The repo-root <c>tests/corpus</c> directory, resolved by walking up from the test assembly's output directory.</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>Where the Imazen <c>codec-corpus</c> repo's relevant subsets land.</summary>
    public static string ImazenRoot => Path.Combine(Root, "imazen-codec-corpus");

    /// <summary>Where the image-rs <c>jpeg-decoder</c> repo's relevant subsets land.</summary>
    public static string ImageRsRoot => Path.Combine(Root, "image-rs-jpeg-decoder");

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
        return Path.Combine(repoRoot, "tests", "corpus");
    }
}
