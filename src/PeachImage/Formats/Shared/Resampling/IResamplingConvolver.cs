using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Performs one axis of a separable resampling convolution over float pixel buffers. Every pixel occupies
/// exactly <see cref="ImageResizer.FloatsPerPixel"/> floats regardless of the source format's real channel
/// count (unused channels are simply zero and ignored downstream) — a fixed width that lets both passes use
/// a single unconditional <see cref="System.Runtime.Intrinsics.Vector128{T}"/>-per-pixel SIMD path with no
/// per-channel-count branching or partial-vector edge cases.
/// </summary>
/// <remarks>
/// Buffers are plain arrays, not <see cref="Span{T}"/>/<see cref="ReadOnlySpan{T}"/>, specifically so
/// implementations can parallelize their per-row loop with <see cref="RowParallel"/> —
/// <see cref="Span{T}"/> is a ref struct and can't be captured by the closure <c>Parallel.For</c> requires.
/// A caller-owned array may be larger than the logical region a call reads/writes (e.g. an
/// <see cref="System.Buffers.ArrayPool{T}"/> rental) — every index a call touches is bounded by its own
/// width/height parameters and <see cref="ResamplingWeightMap.DestinationSize"/>, never by the array's own
/// <c>Length</c>.
/// </remarks>
internal interface IResamplingConvolver
{
    /// <summary>
    /// Convolves vertically: <paramref name="source"/> is <paramref name="sourceHeight"/> rows of
    /// <paramref name="width"/> pixels; <paramref name="destination"/> receives <paramref name="weightMap"/>'s
    /// destination-row count of rows, same <paramref name="width"/>.
    /// </summary>
    void ConvolveVertical(float[] source, int width, int sourceHeight, float[] destination, ResamplingWeightMap weightMap);

    /// <summary>
    /// Convolves horizontally: <paramref name="source"/> is <paramref name="height"/> rows of
    /// <paramref name="sourceWidth"/> pixels; <paramref name="destination"/> receives <paramref name="weightMap"/>'s
    /// destination-column count of columns, same <paramref name="height"/>.
    /// </summary>
    void ConvolveHorizontal(float[] source, int sourceWidth, int height, float[] destination, ResamplingWeightMap weightMap);
}
