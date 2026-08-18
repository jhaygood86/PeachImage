using System.Net.Http.Headers;
using System.Text.Json;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Downloads the <c>.avif</c> fixtures from the official <c>AOMediaCodec/libavif</c> repository's
/// <c>tests/data</c> subtree (basic stills, high-bit-depth, HDR/wide-gamut color series, grid-tiled
/// images, gain maps, animation, metadata edge cases -- see the repo's own <c>README.md</c> for the full
/// per-file breakdown) into the gitignored <see cref="CorpusPaths.Root"/>, via the GitHub Git Trees API +
/// <c>raw.githubusercontent.com</c>, mirroring the Bmp/Gif/Jpeg/Png/Webp fetchers exactly. Filters to
/// <c>tests/data/*.avif</c> only -- the repo also contains the library's own C/CMake source, which isn't
/// needed for decode-only differential testing.
/// </summary>
internal static class CorpusFetcher
{
    private const string Owner = "AOMediaCodec";
    private const string Repo = "libavif";
    private const string Branch = "main";
    private const string DataPathPrefix = "tests/data/";

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

            await FetchAvifFixturesAsync(http, linkedCts.Token).ConfigureAwait(false);

            Directory.CreateDirectory(CorpusPaths.Root);
            await File.WriteAllTextAsync(CorpusPaths.MarkerFile, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // No network, rate-limited, DNS unavailable, etc. -- corpus-driven tests will self-skip; a
            // fetch failure must never fail the build.
            return false;
        }
    }

    private static async Task FetchAvifFixturesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        string treeUrl = $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{Branch}?recursive=1";
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
            if (path.StartsWith(DataPathPrefix, StringComparison.Ordinal) && path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase))
            {
                blobPaths.Add(path);
            }
        }

        string destinationRoot = CorpusPaths.LibavifTestDataRoot;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(blobPaths, options, async (path, ct) =>
        {
            string url = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/" +
                string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

            string relativePath = path[DataPathPrefix.Length..];
            string destination = Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
