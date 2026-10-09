using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>
/// Downloads the libjxl <c>conformance</c> test cases and <c>testdata</c> JPEG XL files into the gitignored
/// <see cref="CorpusPaths.Root"/>, using the GitHub Git Trees API to enumerate blobs and <c>raw.githubusercontent.com</c> to
/// fetch each one. Only the files the tests read are fetched (the codestream, the reference PNG and the thresholds), not the
/// large reference animations and arrays.
/// </summary>
internal static class CorpusFetcher
{
    private readonly record struct Source(string Owner, string Repo, string Branch, string Prefix, string Destination, Func<string, bool> Wanted);

    private static readonly Source[] Sources =
    [
        new("libjxl", "conformance", "master", "testcases/", "libjxl-conformance", path =>
            path.EndsWith("/input.jxl", StringComparison.Ordinal)
            || path.EndsWith("/ref.png", StringComparison.Ordinal)
            || path.EndsWith("/test.json", StringComparison.Ordinal)),
        new("libjxl", "testdata", "main", "jxl/", "libjxl-testdata", path =>
            path.EndsWith(".jxl", StringComparison.Ordinal) || path.EndsWith(".png", StringComparison.Ordinal)
            || path.Contains("/jpeg_reconstruction/", StringComparison.Ordinal) && path.EndsWith(".jpg", StringComparison.Ordinal)),
    ];

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PeachImage-Tests/1.0 (+https://github.com/jhaygood86/PeachImage)");

            foreach (var source in Sources)
            {
                await FetchRepoAsync(http, source, linkedCts.Token).ConfigureAwait(false);
            }

            Directory.CreateDirectory(CorpusPaths.Root);
            await File.WriteAllTextAsync(CorpusPaths.MarkerFile, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // No network, rate-limited, DNS unavailable, etc. -- corpus-driven tests self-skip; a fetch failure must never fail the build.
            return false;
        }
    }

    private static async Task FetchRepoAsync(HttpClient http, Source source, CancellationToken cancellationToken)
    {
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
            if (path.StartsWith(source.Prefix, StringComparison.Ordinal) && source.Wanted(path))
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

            string destination = Path.Combine(destinationRoot, path[source.Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
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
