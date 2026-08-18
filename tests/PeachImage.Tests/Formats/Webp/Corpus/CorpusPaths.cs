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

    /// <summary>
    /// Where Skia's <c>resources/images</c> <c>.webp</c> fixtures land — libwebp-test-data has no animated
    /// WebP files at all, so this is the source of real-world (not hand-built) animated-WebP corpus coverage.
    /// A separate root/marker from <see cref="LibwebpTestDataRoot"/>/<see cref="MarkerFile"/> so a failed
    /// fetch of one source never blocks the other, mirroring why each format has its own independent corpus
    /// root in the first place.
    /// </summary>
    public static string SkiaImagesRoot => Path.Combine(Root, "skia-images");

    /// <summary>Written after a successful Skia fetch; its presence means <see cref="SkiaImagesRoot"/> is ready to use.</summary>
    public static string SkiaMarkerFile => Path.Combine(Root, ".fetched-skia");

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
