using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Downloads the <c>.webp</c> fixtures from Google's Skia graphics library repo's <c>resources/images</c>
/// directory (<c>github.com/google/skia</c>, BSD-3-Clause) into the gitignored
/// <see cref="CorpusPaths.SkiaImagesRoot"/>, via the GitHub Contents API (that one directory is small and
/// flat, so unlike <see cref="CorpusFetcher"/>'s recursive Git Trees walk of libwebp-test-data, a single
/// non-recursive listing call suffices). Skia's own test resources include several animated WebP files used
/// to exercise its own <c>SkCodec</c> multi-frame decode path — real-world animated-WebP fixtures that
/// libwebp-test-data doesn't have at all.
/// </summary>
internal static class SkiaCorpusFetcher
{
    private const string Owner = "google";
    private const string Repo = "skia";
    private const string Branch = "main";
    private const string Path = "resources/images";

    /// <summary>Fetches the fixtures if they haven't been fetched already. Returns whether they're available afterward (never throws).</summary>
    public static async Task<bool> FetchIfNeededAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (File.Exists(CorpusPaths.SkiaMarkerFile))
        {
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PeachImage-Tests/1.0 (+https://github.com/jhaygood86/PeachImage)");

            await FetchWebpFixturesAsync(http, linkedCts.Token).ConfigureAwait(false);

            Directory.CreateDirectory(CorpusPaths.Root);
            await File.WriteAllTextAsync(CorpusPaths.SkiaMarkerFile, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // No network, rate-limited, DNS unavailable, etc. — corpus-driven tests will self-skip; a
            // fetch failure must never fail the build.
            return false;
        }
    }

    private static async Task FetchWebpFixturesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        string contentsUrl = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{Path}?ref={Branch}";
        using var response = await HttpRetry.SendWithRetryAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, contentsUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            GitHubAuth.Apply(request);
            return request;
        }, cancellationToken).ConfigureAwait(false);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var entries = new List<(string Name, string DownloadUrl)>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()!;
            if (!name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? downloadUrl = entry.GetProperty("download_url").GetString();
            if (downloadUrl is not null)
            {
                entries.Add((name, downloadUrl));
            }
        }

        Directory.CreateDirectory(CorpusPaths.SkiaImagesRoot);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(entries, options, async (entry, ct) =>
        {
            string destination = System.IO.Path.Combine(CorpusPaths.SkiaImagesRoot, entry.Name);

            try
            {
                using var fileResponse = await HttpRetry.GetWithRetryAsync(http, entry.DownloadUrl, ct).ConfigureAwait(false);
                await using var fileStream = File.Create(destination);
                await fileResponse.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: one missing/failed file shouldn't abort the whole fetch.
            }
        }).ConfigureAwait(false);
    }
}
