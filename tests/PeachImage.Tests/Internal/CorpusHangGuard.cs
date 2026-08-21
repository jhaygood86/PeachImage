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
/// <remarks>
/// Decode itself now internally parallelizes large images' reconstruction (IDCT/upsampling/color
/// conversion — see <c>RowParallel</c>), which schedules onto the shared, process-wide <see cref="ThreadPool"/>.
/// That's a second, nested layer of concurrency underneath this guard's own <see cref="Environment.ProcessorCount"/>-wide
/// fan-out of guard threads: with up to that many guard threads each independently driving their own
/// <c>Parallel.For</c> work through the same pool, the pool's default (deliberately conservative,
/// grow-by-about-one-thread-per-few-hundred-milliseconds-under-sustained-starvation) growth heuristic can
/// stall badly — not a 2-3x slowdown from raw core contention, but potentially two-plus orders of magnitude
/// from a large decode's many sequential <c>Parallel.For</c> dispatches each separately waiting on pool
/// growth. Forcing a generous minimum worker count up front sidesteps that growth-heuristic delay entirely;
/// it does not weaken this guard's actual purpose — a genuine infinite loop still exceeds any timeout
/// regardless of how many threads are available.
/// </remarks>
internal static class CorpusHangGuard
{
    private static readonly SemaphoreSlim Gate = new(Environment.ProcessorCount);

    static CorpusHangGuard()
    {
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        int wanted = Environment.ProcessorCount * 8;
        ThreadPool.SetMinThreads(Math.Max(minWorker, wanted), minIo);
    }

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
