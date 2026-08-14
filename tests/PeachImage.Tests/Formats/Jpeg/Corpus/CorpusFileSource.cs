namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating each corpus dataset's JPEG files. Each returns an empty set
/// (rather than throwing) when the corpus isn't available, so corpus-driven test classes simply report
/// zero cases instead of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> ImazenConformanceValidFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "valid"));

    /// <summary>
    /// The "invalid", "non-conformant", and "crash-repro" subsets: files deliberately malformed, or known to
    /// make different real-world decoders disagree (that's literally what "crash-repro" means here — each
    /// subfolder is named after the decoder/issue it reproduces a bug for). Only graceful accept-or-reject
    /// is asserted for these, never pixel fidelity against another decoder.
    /// </summary>
    public static IEnumerable<object[]> ImazenConformanceNonConformantFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "invalid"))
            .Concat(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "non-conformant")))
            .Concat(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "crash-repro")));

    public static IEnumerable<object[]> MozjpegFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "mozjpeg"));

    public static IEnumerable<object[]> ZuneFuzzFiles() =>
        // Fuzz inputs are named by content hash with no file extension (they're arbitrary byte sequences,
        // not necessarily even well-formed enough to deserve a ".jpg" name) — enumerate everything, not
        // just files that already look like JPEGs by name.
        EnumerateAllFiles(Path.Combine(CorpusPaths.ImazenRoot, "zune", "fuzz-corpus", "jpeg"));

    public static IEnumerable<object[]> ImageRsCrashtestFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "crashtest", "images"));

    public static IEnumerable<object[]> ImageRsReftestFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "reftest", "images"));

    public static IEnumerable<object[]> ImageRsIccFiles() =>
        EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "icc"));

    private static IEnumerable<object[]> EnumerateJpegFiles(string directory)
    {
        foreach (var file in EnumerateAllFiles(directory))
        {
            string extension = Path.GetExtension((string)file[0]);
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<object[]> EnumerateAllFiles(string directory)
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
