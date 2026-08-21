namespace PeachImage.Formats.Shared.Parallelism;

/// <summary>
/// Runs a per-row body either sequentially or via <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>,
/// depending on row count. Format-agnostic: used by both the image resizer's convolution passes and JPEG
/// decode's IDCT/chroma-upsampling/color-conversion reconstruction steps, all of which share the same
/// shape — every row is fully independent, writing only its own disjoint slice of the destination
/// buffer(s) — so this is purely a throughput decision, never a correctness one, provided the caller's
/// per-row body has no cross-row dependency of its own.
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
internal static class RowParallel
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
    /// correctly reflect the affinity-restricted count) fixed it. This same cap is shared by every caller
    /// (resize and JPEG decode alike) rather than each maintaining its own — bounding one call's own
    /// parallel fan-out, though it says nothing about many such calls running concurrently (e.g. a service
    /// decoding/resizing many uploads at once, each independently capping at this same ceiling against the
    /// shared process-wide thread pool) — a pre-existing tradeoff, not something either caller solves.
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

    /// <summary>
    /// Same as <see cref="For(int, Action{int})"/>, but for a per-row body that needs mutable scratch state
    /// (e.g. a rented buffer) — <paramref name="localFactory"/> runs once per partition (once total in the
    /// sequential fallback, once per worker thread when parallel), not once per row, so scratch state can be
    /// safely reused across a partition's rows without a fresh allocation/rent every row and without two
    /// concurrent rows racing on the same buffer. <paramref name="localFinally"/>, if given, runs once per
    /// partition after that partition's rows are done (e.g. to return a rented buffer).
    /// </summary>
    public static void For<TLocal>(int rowCount, Func<TLocal> localFactory, Action<int, TLocal> body, Action<TLocal>? localFinally = null)
    {
        if (rowCount < MinRowsForParallel)
        {
            TLocal local = localFactory();
            for (int i = 0; i < rowCount; i++)
            {
                body(i, local);
            }

            localFinally?.Invoke(local);
            return;
        }

        Parallel.For(
            0,
            rowCount,
            Options,
            localFactory,
            (i, _, local) =>
            {
                body(i, local);
                return local;
            },
            local => localFinally?.Invoke(local));
    }
}
