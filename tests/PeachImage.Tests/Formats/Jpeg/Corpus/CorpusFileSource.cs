using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// <c>MemberData</c> sources enumerating each corpus dataset's JPEG files. Each yields a single
/// <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/>
/// for why) when the corpus isn't available, so corpus-driven test classes report a genuine skip instead of
/// failing; <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// File enumeration/filtering stays on plain strings (<see cref="EnumerateJpegFiles"/>/<see cref="EnumerateAllFiles"/>)
/// and only gets wrapped into <see cref="TheoryDataRow{T}"/> at each public method via <see cref="ToTheoryData"/>,
/// so the extension filter in <see cref="EnumerateJpegFiles"/> doesn't need to unwrap a data row.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> ImazenConformanceValidFiles() =>
        ToTheoryData(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "valid")));

    /// <summary>
    /// The "invalid", "non-conformant", and "crash-repro" subsets: files deliberately malformed, or known to
    /// make different real-world decoders disagree (that's literally what "crash-repro" means here — each
    /// subfolder is named after the decoder/issue it reproduces a bug for). Only graceful accept-or-reject
    /// is asserted for these, never pixel fidelity against another decoder.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> ImazenConformanceNonConformantFiles() =>
        ToTheoryData(
            EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "invalid"))
                .Concat(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "non-conformant")))
                .Concat(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "crash-repro"))));

    public static IEnumerable<TheoryDataRow<string>> MozjpegFiles() =>
        ToTheoryData(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImazenRoot, "mozjpeg")));

    public static IEnumerable<TheoryDataRow<string>> ZuneFuzzFiles() =>
        // Fuzz inputs are named by content hash with no file extension (they're arbitrary byte sequences,
        // not necessarily even well-formed enough to deserve a ".jpg" name) — enumerate everything, not
        // just files that already look like JPEGs by name.
        ToTheoryData(EnumerateAllFiles(Path.Combine(CorpusPaths.ImazenRoot, "zune", "fuzz-corpus", "jpeg")));

    public static IEnumerable<TheoryDataRow<string>> ImageRsCrashtestFiles() =>
        ToTheoryData(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "crashtest", "images")));

    public static IEnumerable<TheoryDataRow<string>> ImageRsReftestFiles() =>
        ToTheoryData(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "reftest", "images")));

    public static IEnumerable<TheoryDataRow<string>> ImageRsIccFiles() =>
        ToTheoryData(EnumerateJpegFiles(Path.Combine(CorpusPaths.ImageRsRoot, "tests", "icc")));

    private static IEnumerable<TheoryDataRow<string>> ToTheoryData(IEnumerable<string> files)
    {
        bool any = false;
        foreach (var file in files)
        {
            any = true;
            yield return new TheoryDataRow<string>(file);
        }

        if (!any)
        {
            yield return CorpusSkip.Row("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set), or this dataset is legitimately empty.");
        }
    }

    private static IEnumerable<string> EnumerateJpegFiles(string directory)
    {
        foreach (var file in EnumerateAllFiles(directory))
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateAllFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }
}
