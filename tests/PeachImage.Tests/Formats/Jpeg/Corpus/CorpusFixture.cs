namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// Self-triggering corpus availability check: the first access to <see cref="IsAvailable"/> fetches the
/// corpus (if not already fetched) with no separate script or manual step required — plain <c>dotnet test</c>
/// is sufficient. Set <c>PEACHIMAGE_SKIP_CORPUS_FETCH</c> to any value to opt out of network access (e.g.
/// offline development or a CI job that intentionally doesn't want it); corpus-driven tests then report
/// as skipped rather than failing.
/// </summary>
internal static class CorpusFixture
{
    private static readonly Lazy<bool> Availability = new(FetchIfAllowed);

    /// <summary>Whether the corpus is fetched and ready to use. Triggers the fetch on first access.</summary>
    public static bool IsAvailable => Availability.Value;

    private static bool FetchIfAllowed()
    {
        if (Environment.GetEnvironmentVariable("PEACHIMAGE_SKIP_CORPUS_FETCH") is not null)
        {
            return File.Exists(CorpusPaths.MarkerFile);
        }

        return CorpusFetcher.FetchIfNeededAsync(TimeSpan.FromMinutes(15), CancellationToken.None).GetAwaiter().GetResult();
    }
}
