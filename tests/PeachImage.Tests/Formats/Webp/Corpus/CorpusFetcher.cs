using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Webp.Corpus;

/// <summary>
/// Downloads the <c>.webp</c> fixtures from the official GitHub mirror of Chromium's libwebp-test-data repo
/// (<c>github.com/webmproject/libwebp-test-data</c> — a read-only mirror of
/// <c>chromium.googlesource.com/webm/libwebp-test-data</c>, with no separate googlesource-specific fetch
/// needed) into the gitignored <see cref="CorpusPaths.Root"/>, via the GitHub Git Trees API +
/// <c>raw.githubusercontent.com</c>, mirroring the Bmp/Gif/Jpeg fetchers exactly. Filters to <c>*.webp</c>
/// only — the repo's <c>.sh</c>/source-image/`.md5` files aren't needed for decode-only differential testing.
/// </summary>
internal static class CorpusFetcher
{
    private const string Owner = "webmproject";
    private const string Repo = "libwebp-test-data";
    private const string Branch = "main";

    /// <summary>Fetches the corpus if it hasn't been fetched already. Returns whether the corpus is available afterward (never throws).</summary>
    public static async Task<bool> FetchIfNeededAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (File.Exists(CorpusPaths.MarkerFile))
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
            await File.WriteAllTextAsync(CorpusPaths.MarkerFile, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
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
        string treeUrl = $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{Branch}?recursive=1";
        using var response = await HttpRetry.SendWithRetryAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, treeUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return request;
        }, cancellationToken).ConfigureAwait(false);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var blobPaths = new List<string>();
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob")
            {
                continue;
            }

            string path = entry.GetProperty("path").GetString()!;
            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                blobPaths.Add(path);
            }
        }

        string destinationRoot = CorpusPaths.LibwebpTestDataRoot;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(blobPaths, options, async (path, ct) =>
        {
            string url = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/" +
                string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

            string destination = Path.Combine(destinationRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                using var fileResponse = await HttpRetry.GetWithRetryAsync(http, url, ct).ConfigureAwait(false);
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
