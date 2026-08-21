using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// AVX/AVX2-accelerated tier, selected by <see cref="ResamplingConvolverSelector"/> when supported.
/// </summary>
/// <remarks>
/// Only <see cref="ConvolveVertical"/> gets a genuinely wider (8-lane) implementation here — that pass
/// accumulates whole contiguous rows, so doubling the vector width doubles real throughput, the same way
/// <see cref="Formats.Jpeg.Decoding.Upsampling.Vector256TriangleFilterUpsampler"/> doubles
/// <see cref="Formats.Jpeg.Decoding.Upsampling.TriangleFilterUpsampler"/>'s column-blend width.
/// <see cref="ConvolveHorizontal"/> accumulates one destination pixel (exactly
/// <see cref="ImageResizer.FloatsPerPixel"/> = 4 floats) at a time from windows whose source offsets differ
/// per destination pixel — there's no natural 8-wide grouping of two such windows without a cross-lane
/// gather, so rather than fake a wider tier that wouldn't actually move more data per instruction, this
/// delegates straight to <see cref="Vector128ResamplingConvolver"/>, which is already at the natural width
/// for a single pixel's channels (and already parallelizes its own per-row loop). This mirrors
/// <see cref="Formats.Jpeg.Decoding.Upsampling.Vector256TriangleFilterUpsampler"/> delegating its own
/// non-specialized cases back to the Vector128 tier instead of reimplementing them.
/// </remarks>
internal sealed class Vector256ResamplingConvolver : IResamplingConvolver
{
    private static readonly Vector128ResamplingConvolver HorizontalFallback = new();

    public void ConvolveVertical(float[] source, int width, int sourceHeight, float[] destination, ResamplingWeightMap weightMap)
    {
        int rowLength = width * ImageResizer.FloatsPerPixel;

        RowParallel.For(weightMap.DestinationSize, destY =>
        {
            var destRow = destination.AsSpan(destY * rowLength, rowLength);
            destRow.Clear();

            int start = weightMap.Starts[destY];
            var weights = weightMap.GetWeights(destY);
            for (int k = 0; k < weights.Length; k++)
            {
                var sourceRow = source.AsSpan((start + k) * rowLength, rowLength);
                Accumulate(sourceRow, weights[k], destRow);
            }
        });
    }

    public void ConvolveHorizontal(float[] source, int sourceWidth, int height, float[] destination, ResamplingWeightMap weightMap) =>
        HorizontalFallback.ConvolveHorizontal(source, sourceWidth, height, destination, weightMap);

    /// <summary>Accumulates <c>accumulator += source * weight</c> elementwise, 8 floats (two pixels) at a time with a scalar tail for any remainder.</summary>
    private static void Accumulate(ReadOnlySpan<float> source, float weight, Span<float> accumulator)
    {
        var weightVec = Vector256.Create(weight);
        int i = 0;
        for (; i + 8 <= source.Length; i += 8)
        {
            var s = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source.Slice(i, 8)));
            var a = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(accumulator.Slice(i, 8)));
            (a + (s * weightVec)).StoreUnsafe(ref accumulator[i]);
        }

        for (; i < source.Length; i++)
        {
            accumulator[i] += source[i] * weight;
        }
    }
}
