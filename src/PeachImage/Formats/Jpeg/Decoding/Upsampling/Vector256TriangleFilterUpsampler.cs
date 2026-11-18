using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>
/// AVX2 (16-lane) tier of <see cref="TriangleFilterUpsampler"/>'s <c>horizontalRatio == 2</c> path — same
/// column-blend-then-row-blend decomposition (see that type's remarks), doubled from 8 to 16 source columns
/// per iteration. Every other ratio (including the rare <c>horizontalRatio == 1</c> case) delegates straight
/// to a <see cref="TriangleFilterUpsampler"/> instance rather than re-implementing paths that are already
/// correct and not worth vectorizing further.
/// </summary>
/// <remarks>
/// The left and right blends are interleaved by placing the right blend in each 16-bit lane's high byte, which
/// lays out the bytes in output order with no cross-lane permute (a byte shuffle after <c>Vector256.Narrow</c>
/// would need one).
/// </remarks>
internal sealed class Vector256TriangleFilterUpsampler : IChromaUpsampler
{
    private static readonly TriangleFilterUpsampler Fallback = new();
    private static readonly Vector256<ushort> Eight = Vector256.Create((ushort)8);

    public void Upsample(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio,
        ParallelSettings parallel = default)
    {
        if (horizontalRatio != 2 || verticalRatio is not (1 or 2))
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, horizontalRatio, verticalRatio, parallel);
            return;
        }

        UpsampleHorizontalDoubling(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, verticalRatio, parallel);
    }

    /// <summary>Vectorized path for <c>horizontalRatio == 2</c> — see <see cref="TriangleFilterUpsampler"/>'s remarks for the decomposition this implements.</summary>
    private static void UpsampleHorizontalDoubling(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight,
        int verticalRatio, ParallelSettings parallel)
    {
        // Rented once per RowParallel partition (not once for the whole plane, and not fresh per row) via
        // the TLocal hook — see TriangleFilterUpsampler.UpsampleHorizontalDoubling's remarks for why.
        RowParallel.For<ushort[]>(
            destinationHeight,
            () => ArrayPool<ushort>.Shared.Rent(sourceWidth + 2),
            (y, aBuffer) =>
        {
            UpsampleRowHorizontalDoubling(
                source, sourceWidth, sourceHeight, verticalRatio, y,
                aBuffer.AsSpan(0, sourceWidth + 2),
                destination.AsSpan(y * destinationWidth, destinationWidth));
        },
            aBuffer => ArrayPool<ushort>.Shared.Return(aBuffer),
            parallel);
    }

    /// <summary>Computes <c>columnBlend[c] = 3*main[c] + vNeighbor[c]</c> for every source column, 16 at a time.</summary>
    private static void UpsampleRowHorizontalDoubling(
        byte[] source, int sourceWidth, int sourceHeight, int verticalRatio, int y,
        Span<ushort> a, Span<byte> dstRow)
    {
        int srcY = Math.Clamp(y / verticalRatio, 0, sourceHeight - 1);
        int srcYNeighbor = verticalRatio == 2
            ? Math.Clamp((y / verticalRatio) + (((y & 1) == 0) ? -1 : 1), 0, sourceHeight - 1)
            : srcY;

        var main = source.AsSpan(srcY * sourceWidth, sourceWidth);
        var vNeighbor = source.AsSpan(srcYNeighbor * sourceWidth, sourceWidth);

        ComputeColumnBlend(main, vNeighbor, a.Slice(1, sourceWidth));
        a[0] = a[1];
        a[sourceWidth + 1] = a[sourceWidth];

        BlendRow(a, sourceWidth, dstRow);
    }

    public bool TryUpsampleRow(
        byte[] source, int sourceWidth, int sourceHeight,
        int horizontalRatio, int verticalRatio,
        int y, Span<byte> destinationRow, Span<ushort> scratch)
    {
        if (horizontalRatio != 2 || verticalRatio is not (1 or 2))
        {
            return false;
        }

        UpsampleRowHorizontalDoubling(source, sourceWidth, sourceHeight, verticalRatio, y, scratch.Slice(0, sourceWidth + 2), destinationRow);
        return true;
    }

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

            // Both blends are at most 255; see the type-level remarks.
            (leftBlend | (rightBlend << 8)).AsByte().StoreUnsafe(ref dstRow[c * 2]);
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
