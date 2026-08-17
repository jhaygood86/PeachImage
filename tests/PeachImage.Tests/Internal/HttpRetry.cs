namespace PeachImage.Tests.Internal;

/// <summary>
/// Wraps a corpus fetcher's HTTP calls with a short retry-with-backoff for transient failures (network
/// blips, DNS hiccups, GitHub API rate-limit/5xx responses), shared by every format's <c>CorpusFetcher</c>.
/// CI has shown single-shot corpus fetches fail intermittently on some runners even though the corpus
/// source itself is reachable a moment later -- previously, one failed call (most commonly the initial Git
/// Trees API listing) aborted the entire fetch, leaving every corpus-driven test with zero fixtures for the
/// rest of that CI run. Non-transient failures (4xx other than 429) fail fast rather than retrying, since
/// retrying a genuine "not found" just spends the fetch's overall timeout budget for no benefit.
/// </summary>
internal static class HttpRetry
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan[] Delays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(2)];

    /// <summary>
    /// Sends the request built by <paramref name="requestFactory"/> (invoked fresh on every attempt, since a
    /// sent <see cref="HttpRequestMessage"/> can't be resent), retrying transient failures up to
    /// <see cref="MaxAttempts"/> times. Throws on the final attempt's failure, same as a non-retrying
    /// <c>SendAsync</c> + <c>EnsureSuccessStatusCode</c> would -- callers' existing catch-and-skip handling
    /// is unchanged.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool lastAttempt = attempt == MaxAttempts - 1;

            try
            {
                using var request = requestFactory();
                HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || lastAttempt)
                {
                    response.EnsureSuccessStatusCode();
                    return response;
                }

                response.Dispose();
            }
            catch (Exception ex) when (!lastAttempt && ex is not OperationCanceledException)
            {
                // Transient network/DNS/timeout failure -- fall through and retry.
            }

            await Task.Delay(Delays[Math.Min(attempt, Delays.Length - 1)], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Convenience wrapper of <see cref="SendWithRetryAsync"/> for a plain GET.</summary>
    public static Task<HttpResponseMessage> GetWithRetryAsync(HttpClient http, string url, CancellationToken cancellationToken) =>
        SendWithRetryAsync(http, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode == System.Net.HttpStatusCode.TooManyRequests || statusCode == System.Net.HttpStatusCode.RequestTimeout;
}
