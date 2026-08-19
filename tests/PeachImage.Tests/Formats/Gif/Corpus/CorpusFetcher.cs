using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Gif.Corpus;

/// <summary>
/// Downloads two small, independent GIF test corpora into the gitignored <see cref="CorpusPaths.Root"/>:
/// giflib's own regression images (<c>pic/</c> plus <c>tests/wedge.gif</c>, fetched via the GitHub Git Trees
/// API + <c>raw.githubusercontent.com</c>, mirroring the Bmp/Jpeg fetchers) and the W3C image-format test
/// page's GIF assets (a plain file listing, not a git repo, so fetched by direct URL instead).
/// </summary>
internal static class CorpusFetcher
{
    private static readonly (string Owner, string Repo, string Branch, string[] Paths, string Destination) GiflibSource =
        ("nesbox", "giflib", "master", ["pic", "tests/wedge.gif"], "giflib-test-suite");

    private static readonly string[] W3cAssetNames =
    [
        "w3c_home.gif",
        "w3c_home_256.gif",
        "w3c_home_gray.gif",
        "w3c_home_2.gif",
        "w3c_home_animation.gif",
    ];

    private const string W3cBaseUrl = "https://www.w3.org/People/mimasa/test/imgformat/img/";

    /// <summary>Fetches both corpora if they haven't been fetched already. Returns whether the corpus is available afterward (never throws).</summary>
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

            await FetchGiflibAsync(http, linkedCts.Token).ConfigureAwait(false);
            await FetchW3cAssetsAsync(http, linkedCts.Token).ConfigureAwait(false);

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

    private static async Task FetchGiflibAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var source = GiflibSource;
        string treeUrl = $"https://api.github.com/repos/{source.Owner}/{source.Repo}/git/trees/{source.Branch}?recursive=1";
        using var response = await HttpRetry.SendWithRetryAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, treeUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            GitHubAuth.Apply(request);
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
            if (source.Paths.Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                blobPaths.Add(path);
            }
        }

        string destinationRoot = Path.Combine(CorpusPaths.Root, source.Destination);
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(blobPaths, options, async (path, ct) =>
        {
            string url = $"https://raw.githubusercontent.com/{source.Owner}/{source.Repo}/{source.Branch}/" +
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

    private static async Task FetchW3cAssetsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CorpusPaths.W3cRoot);

        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(W3cAssetNames, options, async (name, ct) =>
        {
            try
            {
                using var response = await HttpRetry.GetWithRetryAsync(http, W3cBaseUrl + name, ct).ConfigureAwait(false);
                string destination = Path.Combine(CorpusPaths.W3cRoot, name);
                await using var fileStream = File.Create(destination);
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: one missing/failed file shouldn't abort the whole fetch.
            }
        }).ConfigureAwait(false);
    }
}
