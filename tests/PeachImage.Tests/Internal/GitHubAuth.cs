using System.Net.Http.Headers;

namespace PeachImage.Tests.Internal;

/// <summary>
/// Attaches GitHub API authentication when available, raising the unauthenticated 60-requests/hour-per-IP
/// limit on <c>api.github.com</c> (trivially exhausted on a shared CI runner IP pool -- the actual root
/// cause of intermittent "corpus unavailable" flakiness across every format's corpus fetcher, since a
/// 429/403 on the single Git Trees/Contents API listing call cascades into zero files for that format) to
/// 5,000/hour per-token. In GitHub Actions this is the workflow's own ambient <c>GITHUB_TOKEN</c> -- no repo
/// secret setup needed. Local runs without a token fall back to unauthenticated requests exactly as before.
/// </summary>
internal static class GitHubAuth
{
    /// <summary>Adds an <c>Authorization</c> header to <paramref name="request"/> if a token is available via <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c> (the same env vars the <c>gh</c> CLI recognizes).</summary>
    public static void Apply(HttpRequestMessage request)
    {
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
