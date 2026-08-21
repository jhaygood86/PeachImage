using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Baseline SIMD tier: <see cref="Vector128{T}"/> (4-lane) main loop, using the portable API so it also
/// works (via .NET's software fallback) on hardware without SIMD acceleration — the same universal-fallback
/// role <see cref="Formats.Jpeg.Decoding.Upsampling.TriangleFilterUpsampler"/> plays for chroma upsampling.
/// The vertical pass processes 4 contiguous floats (one pixel, since every pixel is exactly
/// <see cref="ImageResizer.FloatsPerPixel"/> floats wide) at a time; the horizontal pass processes exactly
/// one pixel (4 floats) per destination column, which is already the natural SIMD width for a single pixel's
/// channels regardless of instruction set — see <see cref="Vector256ResamplingConvolver"/>'s remarks for why
/// its horizontal pass reuses this same implementation rather than widening it. Both passes' outer per-row
/// loop runs through <see cref="RowParallel"/> — each row only reads from the source buffer and
/// writes a disjoint destination row, so rows have no cross-dependency to serialize on.
/// </summary>
internal sealed class Vector128ResamplingConvolver : IResamplingConvolver
{
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

    public void ConvolveHorizontal(float[] source, int sourceWidth, int height, float[] destination, ResamplingWeightMap weightMap)
    {
        const int stride = ImageResizer.FloatsPerPixel;
        int destWidth = weightMap.DestinationSize;

        RowParallel.For(height, y =>
        {
            var sourceRow = source.AsSpan(y * sourceWidth * stride, sourceWidth * stride);
            var destRow = destination.AsSpan(y * destWidth * stride, destWidth * stride);

            for (int destX = 0; destX < destWidth; destX++)
            {
                int start = weightMap.Starts[destX];
                var weights = weightMap.GetWeights(destX);

                var accumulator = Vector128<float>.Zero;
                for (int k = 0; k < weights.Length; k++)
                {
                    var sourcePixel = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(sourceRow.Slice((start + k) * stride, stride)));
                    accumulator += sourcePixel * Vector128.Create(weights[k]);
                }

                accumulator.StoreUnsafe(ref MemoryMarshal.GetReference(destRow.Slice(destX * stride, stride)));
            }
        });
    }

    /// <summary>Accumulates <c>accumulator += source * weight</c> elementwise, 4 floats at a time with a scalar tail for any remainder.</summary>
    private static void Accumulate(ReadOnlySpan<float> source, float weight, Span<float> accumulator)
    {
        var weightVec = Vector128.Create(weight);
        int i = 0;
        for (; i + 4 <= source.Length; i += 4)
        {
            var s = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source.Slice(i, 4)));
            var a = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(accumulator.Slice(i, 4)));
            (a + (s * weightVec)).StoreUnsafe(ref accumulator[i]);
        }

        for (; i < source.Length; i++)
        {
            accumulator[i] += source[i] * weight;
        }
    }
}
