namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Runs a per-row convolution body either sequentially or via <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>,
/// depending on row count. Convolution rows are fully independent (each writes a disjoint slice of the
/// destination buffer), so this is a pure throughput decision, not a correctness one.
/// </summary>
/// <remarks>
/// A row-fused pipeline (converting bytes to float and convolving, or convolving and narrowing back to
/// bytes, in one dispatch per stage-pair instead of two) was tried here and measured — profiling a tight
/// loop of resizes showed most wall time going to thread-pool/GC machinery, suggesting fewer
/// <c>Parallel.For</c> dispatches per resize should help. It didn't: two different implementations (a
/// per-thread scratch buffer via <c>Parallel.For&lt;TLocal&gt;</c>, and a simpler per-row
/// <see cref="System.Buffers.ArrayPool{T}"/> rent) both left downscale roughly unchanged and made upscale
/// measurably <em>slower</em> and noticeably noisier than the unfused four-dispatch version, despite
/// dispatching half as often. The lesson: this profile's overhead signal didn't translate into a real win
/// through the fusion route that seemed to target it directly — treat "the profile suggests X" as a
/// hypothesis to benchmark, not a conclusion, even when the hypothesis is well-reasoned.
/// </remarks>
internal static class ResamplingParallel
{
    /// <summary>
    /// Below this many rows, <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>'s
    /// task-scheduling overhead is likely to cost more than the sequential loop it would replace — small
    /// images (and this repo's many tiny-image unit tests) fall well under this.
    /// </summary>
    internal const int MinRowsForParallel = 64;

    /// <summary>
    /// Explicitly caps <c>Parallel.For</c>'s degree of parallelism at <see cref="Environment.ProcessorCount"/>
    /// rather than leaving it to the default (unbounded) heuristic. Left unbounded, the .NET thread pool's
    /// dynamic thread injection can spin up more worker threads than the process can actually run
    /// concurrently — confirmed empirically under a CPU-affinity-restricted process (the pinning this repo's
    /// own benchmark methodology uses for reproducibility, per LIBRARY_COMPARISON.md), where the unbounded
    /// default caused enough context-switch/oversubscription overhead to make several filters' parallel
    /// upscale path *slower* than the sequential code it replaced. Explicitly capping
    /// <c>ParallelOptions.MaxDegreeOfParallelism</c> at <see cref="Environment.ProcessorCount"/> (which does
    /// correctly reflect the affinity-restricted count) fixed it.
    /// </summary>
    private static readonly ParallelOptions Options = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

    public static void For(int rowCount, Action<int> body)
    {
        if (rowCount < MinRowsForParallel)
        {
            for (int i = 0; i < rowCount; i++)
            {
                body(i);
            }

            return;
        }

        Parallel.For(0, rowCount, Options, body);
    }
}
