using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>
/// AVX2 (16-lane) tier of <see cref="TriangleFilterUpsampler"/>'s <c>horizontalRatio == 2</c> path — same
/// column-blend-then-row-blend decomposition (see that type's remarks), doubled from 8 to 16 source columns
/// per iteration. Every other ratio (including the rare <c>horizontalRatio == 1</c> case) delegates straight
/// to a <see cref="TriangleFilterUpsampler"/> instance rather than re-implementing paths that are already
/// correct and not worth vectorizing further.
/// </summary>
/// <remarks>
/// The one non-obvious step widening from 8 to 16 lanes, and the one this class's own fuzz-test run against
/// <c>TriangleFilterUpsamplerTests</c>' independent reference caught on the first attempt (see the git
/// history for the naive version this replaced): <c>Vector256.Narrow</c> does <b>not</b> pack per 128-bit
/// lane the way the raw <c>vpackuswb</c> instruction does — .NET's portable API normalizes it to a flat
/// logical concatenation, <c>[left0..15, right0..15]</c>, not <c>[left0..7, right0..7, left8..15, right8..15]</c>.
/// A single <c>Vector256.Shuffle</c> (which, unlike <c>Narrow</c>, genuinely is restricted to shuffling
/// within each 128-bit lane, mirroring hardware <c>vpshufb</c>) cannot turn that flat layout into the
/// interleaved output — every other output byte would need to reach across the lane boundary. Splitting
/// <c>leftBlend</c>/<c>rightBlend</c> into their lower/upper 128-bit halves and running
/// <see cref="TriangleFilterUpsampler"/>'s exact proven <c>Vector128.Narrow</c> + <c>Vector128.Shuffle</c>
/// interleave twice — once per 8-column half — sidesteps the cross-lane question entirely by reusing logic
/// already verified correct, rather than reasoning about a new cross-lane permute from scratch.
/// </remarks>
internal sealed class Vector256TriangleFilterUpsampler : IChromaUpsampler
{
    private static readonly TriangleFilterUpsampler Fallback = new();
    private static readonly Vector128<byte> InterleaveMask = Vector128.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
    private static readonly Vector256<ushort> Eight = Vector256.Create((ushort)8);

    public void Upsample(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio)
    {
        if (horizontalRatio != 2 || verticalRatio is not (1 or 2))
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);
            return;
        }

        UpsampleHorizontalDoubling(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, verticalRatio);
    }

    /// <summary>Vectorized path for <c>horizontalRatio == 2</c> — see <see cref="TriangleFilterUpsampler"/>'s remarks for the decomposition this implements.</summary>
    private static void UpsampleHorizontalDoubling(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int verticalRatio)
    {
        var aBuffer = ArrayPool<ushort>.Shared.Rent(sourceWidth + 2);
        try
        {
            Span<ushort> a = aBuffer.AsSpan(0, sourceWidth + 2);

            for (int y = 0; y < destinationHeight; y++)
            {
                int srcY = Math.Clamp(y / verticalRatio, 0, sourceHeight - 1);
                int srcYNeighbor = verticalRatio == 2
                    ? Math.Clamp((y / verticalRatio) + (((y & 1) == 0) ? -1 : 1), 0, sourceHeight - 1)
                    : srcY;

                var main = source.Slice(srcY * sourceWidth, sourceWidth);
                var vNeighbor = source.Slice(srcYNeighbor * sourceWidth, sourceWidth);
                var dstRow = destination.Slice(y * destinationWidth, destinationWidth);

                ComputeColumnBlend(main, vNeighbor, a.Slice(1, sourceWidth));
                a[0] = a[1];
                a[sourceWidth + 1] = a[sourceWidth];

                BlendRow(a, sourceWidth, dstRow);
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(aBuffer);
        }
    }

    /// <summary>Computes <c>columnBlend[c] = 3*main[c] + vNeighbor[c]</c> for every source column, 16 at a time.</summary>
    private static void ComputeColumnBlend(ReadOnlySpan<byte> main, ReadOnlySpan<byte> vNeighbor, Span<ushort> columnBlend)
    {
        int c = 0;
        for (; c + 16 <= main.Length; c += 16)
        {
            var m = WidenToUShort(main.Slice(c, 16));
            var v = WidenToUShort(vNeighbor.Slice(c, 16));
            (m + m + m + v).StoreUnsafe(ref columnBlend[c]);
        }

        // Scalar tail for sourceWidth % 16 leftover columns — the routine case, not an edge case (chroma
        // widths are essentially never multiples of 16).
        for (; c < main.Length; c++)
        {
            columnBlend[c] = (ushort)((3 * main[c]) + vNeighbor[c]);
        }
    }

    /// <summary>
    /// Blends the padded column-blend buffer <paramref name="a"/> (length <c>sourceWidth + 2</c>, edges
    /// already replicated) into <paramref name="dstRow"/> (length <c>sourceWidth * 2</c>), 16 source columns
    /// (32 destination bytes) at a time.
    /// </summary>
    private static void BlendRow(ReadOnlySpan<ushort> a, int sourceWidth, Span<byte> dstRow)
    {
        int c = 0;
        for (; c + 16 <= sourceWidth; c += 16)
        {
            var center = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(a.Slice(c + 1, 16)));
            var left = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(a.Slice(c, 16)));
            var right = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(a.Slice(c + 2, 16)));

            var centerTimes3 = center + center + center;
            var leftBlend = Vector256.ShiftRightLogical(centerTimes3 + left + Eight, 4);
            var rightBlend = Vector256.ShiftRightLogical(centerTimes3 + right + Eight, 4);

            // See the type-level remarks: two independent Vector128 narrow+interleave passes (columns 0-7,
            // then 8-15), not one Vector256 pass — Vector256.Narrow's flat layout doesn't line up with what
            // a single per-lane Vector256.Shuffle can interleave.
            var combinedLow = Vector128.Narrow(leftBlend.GetLower(), rightBlend.GetLower());
            var combinedHigh = Vector128.Narrow(leftBlend.GetUpper(), rightBlend.GetUpper());
            Vector128.Shuffle(combinedLow, InterleaveMask).StoreUnsafe(ref dstRow[c * 2]);
            Vector128.Shuffle(combinedHigh, InterleaveMask).StoreUnsafe(ref dstRow[(c * 2) + 16]);
        }

        for (; c < sourceWidth; c++)
        {
            int center = a[c + 1];
            int left = a[c];
            int right = a[c + 2];
            dstRow[c * 2] = (byte)(((3 * center) + left + 8) >> 4);
            dstRow[(c * 2) + 1] = (byte)(((3 * center) + right + 8) >> 4);
        }
    }

    private static Vector256<ushort> WidenToUShort(ReadOnlySpan<byte> source16)
    {
        var byteVec = Vector256.Create(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source16)), Vector128<byte>.Zero);
        return Vector256.WidenLower(byteVec);
    }
}
