namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Self-triggering availability check for the Skia <c>resources/images</c> corpus source (see
/// <see cref="SkiaCorpusFetcher"/>), independent of <see cref="CorpusFixture"/>'s libwebp-test-data gate so a
/// failure fetching one source never blocks the other.
/// </summary>
internal static class SkiaCorpusFixture
{
    private static readonly Lazy<bool> Availability = new(FetchIfAllowed);

    /// <summary>Whether the Skia corpus source is fetched and ready to use. Triggers the fetch on first access.</summary>
    public static bool IsAvailable => Availability.Value;

    private static bool FetchIfAllowed()
    {
        if (Environment.GetEnvironmentVariable("PEACHIMAGE_SKIP_CORPUS_FETCH") is not null)
        {
            return File.Exists(CorpusPaths.SkiaMarkerFile);
        }

        return SkiaCorpusFetcher.FetchIfNeededAsync(TimeSpan.FromMinutes(15), CancellationToken.None).GetAwaiter().GetResult();
    }
}
