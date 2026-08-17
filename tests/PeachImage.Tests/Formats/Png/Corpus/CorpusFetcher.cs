using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Png.Corpus;

/// <summary>
/// Downloads the <c>pngsuite</c> subtree of the Imazen <c>codec-corpus</c> repository — a mirror of
/// Willem van Schaik's classic PngSuite conformance set — into the gitignored <see cref="CorpusPaths.Root"/>,
/// using the GitHub Git Trees API to enumerate blobs under that subpath and <c>raw.githubusercontent.com</c>
/// to fetch each one — no <c>git</c> executable required, and no full-repo clone. Mirrors the Bmp/Jpeg
/// corpus fetchers' approach, scoped to a single repo/path.
/// </summary>
internal static class CorpusFetcher
{
    private static readonly (string Owner, string Repo, string Branch, string[] Paths, string Destination) ImazenSource =
        ("imazen", "codec-corpus", "main", ["pngsuite"], "imazen-codec-corpus");

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

            await FetchRepoAsync(http, ImazenSource, linkedCts.Token).ConfigureAwait(false);

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

    private static async Task FetchRepoAsync(HttpClient http, (string Owner, string Repo, string Branch, string[] Paths, string Destination) source, CancellationToken cancellationToken)
    {
        string treeUrl = $"https://api.github.com/repos/{source.Owner}/{source.Repo}/git/trees/{source.Branch}?recursive=1";
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
}
