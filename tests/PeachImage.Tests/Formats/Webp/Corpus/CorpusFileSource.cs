using PeachImage.Formats.Webp;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// <c>MemberData</c> source enumerating the libwebp-test-data corpus. Each yields a single
/// <see cref="CorpusSkip"/> row (rather than throwing, or returning zero rows — see <see cref="CorpusSkip"/>
/// for why) when the corpus isn't available, so corpus-driven test classes report a genuine skip instead of
/// failing; <see cref="CorpusAvailabilityTests"/> makes the same signal visible for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<TheoryDataRow<string>> WebpFiles() => EnumerateFiles(CorpusPaths.LibwebpTestDataRoot);

    /// <summary>
    /// Every <c>.webp</c> file under Skia's <c>resources/images</c> fixture set (see <see cref="SkiaCorpusFetcher"/>)
    /// that's actually animated — auto-detected via <see cref="WebpDecoder.Identify"/>'s
    /// <see cref="ImageInfo.IsAnimated"/> rather than a hardcoded filename list, so any animated fixture Skia
    /// adds later is picked up automatically without a code change here.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> AnimatedWebpFiles()
    {
        if (!SkiaCorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.SkiaImagesRoot))
        {
            yield return CorpusSkip.Row("Skia's animated WebP test fixtures are not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(CorpusPaths.SkiaImagesRoot, "*.webp", SearchOption.TopDirectoryOnly))
        {
            bool isAnimated;
            try
            {
                using var stream = File.OpenRead(file);
                isAnimated = WebpDecoder.Identify(stream).IsAnimated;
            }
            catch (WebpDecodingException)
            {
                continue;
            }

            if (isAnimated)
            {
                yield return new TheoryDataRow<string>(file);
            }
        }
    }

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External WebP test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.webp", SearchOption.AllDirectories))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }
}
