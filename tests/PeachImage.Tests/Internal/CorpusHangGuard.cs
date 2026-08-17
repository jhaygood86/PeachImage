namespace PeachImage.Tests.Internal;

/// <summary>
/// Runs a corpus-decode probe on a dedicated background thread with a wall-clock timeout, shared by every
/// format's corpus-driven hang guard. A dedicated thread (not <c>Task.Run</c>) keeps the guard from
/// competing with xunit's own thread-pool-based parallel test execution for workers — blocking
/// <c>Task.Wait</c> on a <c>Task.Run</c> work item starved the pool under load and made fast decodes
/// spuriously "time out". Concurrent guard threads are additionally capped at
/// <see cref="Environment.ProcessorCount"/> via a shared semaphore so that xunit's own heavy corpus-theory
/// parallelism can't spin up an unbounded number of real OS threads — which independently reintroduces
/// enough scheduling contention on constrained CI hardware to blow the timeout on an otherwise-fast decode.
/// </summary>
internal static class CorpusHangGuard
{
    private static readonly SemaphoreSlim Gate = new(Environment.ProcessorCount);

    /// <summary>Runs <paramref name="work"/> on a gated background thread, returning whether it completed within <paramref name="timeout"/>.</summary>
    public static bool TryRun<T>(Func<T> work, TimeSpan timeout, out T result)
    {
        Gate.Wait();

        T captured = default!;
        var thread = new Thread(() =>
        {
            try
            {
                captured = work();
            }
            finally
            {
                Gate.Release();
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();

        bool completed = thread.Join(timeout);
        result = captured;
        return completed;
    }
}
