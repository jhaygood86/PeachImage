using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// <c>MemberData</c> source enumerating the libwebp-test-data corpus. Returns an empty set (rather than
/// throwing) when the corpus isn't available, so corpus-driven test classes simply report zero cases instead
/// of failing; <see cref="CorpusAvailabilityTests"/> is what makes that visible.
/// </summary>
internal static class CorpusFileSource
{
    public static IEnumerable<object[]> WebpFiles() => EnumerateFiles(CorpusPaths.LibwebpTestDataRoot);

    /// <summary>
    /// Every <c>.webp</c> file under Skia's <c>resources/images</c> fixture set (see <see cref="SkiaCorpusFetcher"/>)
    /// that's actually animated — auto-detected via <see cref="WebpDecoder.Identify"/>'s
    /// <see cref="ImageInfo.IsAnimated"/> rather than a hardcoded filename list, so any animated fixture Skia
    /// adds later is picked up automatically without a code change here.
    /// </summary>
    public static IEnumerable<object[]> AnimatedWebpFiles()
    {
        if (!SkiaCorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.SkiaImagesRoot))
        {
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
                yield return [file];
            }
        }
    }

    private static IEnumerable<object[]> EnumerateFiles(string directory)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.webp", SearchOption.AllDirectories))
        {
            yield return [file];
        }
    }
}
