using System.Buffers;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Decoding.Upsampling;

/// <summary>
/// Bilinear ("fancy", libjpeg-style) upsampler: interpolates between neighboring source samples rather than
/// replicating them, producing visibly smoother output for subsampled chroma at a modest extra cost.
/// Falls back to nearest-neighbor for ratios it doesn't specialize (anything other than 1x1/2x1/1x2/2x2).
/// </summary>
/// <remarks>
/// The <c>horizontalRatio == 2</c> path (covers 4:2:0 and 4:2:2 — the two ratios real JPEGs actually use)
/// is vectorized: the per-pixel formula factors into a column-blend-then-row-blend decomposition —
/// <c>A[c] = 3*main[c] + vNeighbor[c]</c> per source column, then <c>dst[2c] = (3*A[c]+A[c-1]+8)&gt;&gt;4</c>
/// and <c>dst[2c+1] = (3*A[c]+A[c+1]+8)&gt;&gt;4</c> (edges of <c>A</c> replicated, matching the original's
/// clamped-neighbor behavior at row boundaries) — structurally the same split libjpeg's own
/// <c>h2v2_fancy_upsample</c> uses. Verified algebraically: expanding <c>3*A[c]+A[c-1]</c> gives
/// <c>9*main[c] + 3*main[c-1] + 3*vNeighbor[c] + vNeighbor[c-1]</c>, exactly the original's
/// <c>9*main + 3*hNeighbor + 3*vNeighbor + diag</c> for output column <c>2c</c> (and symmetrically for
/// <c>2c+1</c> with <c>c+1</c>). The weighted sum is mathematically guaranteed in [0,255] for valid byte
/// inputs (weights sum to 16), so the original's defensive <c>Math.Clamp</c> is dead code here and isn't
/// ported. The rare <c>horizontalRatio == 1</c> case (vertical-only subsampling, essentially never seen in
/// real files) keeps the original scalar per-pixel loop.
/// </remarks>
internal sealed class TriangleFilterUpsampler : IChromaUpsampler
{
    private static readonly NearestNeighborUpsampler Fallback = new();
    private static readonly Vector128<byte> InterleaveMask = Vector128.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
    private static readonly Vector128<ushort> Eight = Vector128.Create((ushort)8);

    public void Upsample(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio)
    {
        if (horizontalRatio == 1 && verticalRatio == 1)
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, 1, 1);
            return;
        }

        if (horizontalRatio is not (1 or 2) || verticalRatio is not (1 or 2))
        {
            Fallback.Upsample(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);
            return;
        }

        if (horizontalRatio == 2)
        {
            UpsampleHorizontalDoubling(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, verticalRatio);
            return;
        }

        UpsampleScalar(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, horizontalRatio, verticalRatio);
    }

    /// <summary>Vectorized path for <c>horizontalRatio == 2</c> — see the type-level remarks for the decomposition this implements.</summary>
    private static void UpsampleHorizontalDoubling(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int verticalRatio)
    {
        // a[] holds one padded row's worth of column-blend values: a[0]/a[sourceWidth+1] are the
        // edge-replicated padding, a[1..sourceWidth] the real per-column blends (a[i] holds A[i-1] in the
        // remarks' 0-indexed notation). Rented once for the whole plane, not per row — sourceWidth can be
        // large enough (a 12MP source's chroma plane) that a per-row stackalloc would risk a stack overflow.
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

    /// <summary>Computes <c>columnBlend[c] = 3*main[c] + vNeighbor[c]</c> for every source column.</summary>
    private static void ComputeColumnBlend(ReadOnlySpan<byte> main, ReadOnlySpan<byte> vNeighbor, Span<ushort> columnBlend)
    {
        int c = 0;
        for (; c + 8 <= main.Length; c += 8)
        {
            var m = WidenToUShort(main.Slice(c, 8));
            var v = WidenToUShort(vNeighbor.Slice(c, 8));
            (m + m + m + v).CopyTo(columnBlend.Slice(c, 8));
        }

        // Scalar tail for sourceWidth % 8 leftover columns — must compute these directly, not zero-pad:
        // chroma widths are essentially never multiples of 8, so this is the routine case, not an edge case.
        for (; c < main.Length; c++)
        {
            columnBlend[c] = (ushort)((3 * main[c]) + vNeighbor[c]);
        }
    }

    /// <summary>
    /// Blends the padded column-blend buffer <paramref name="a"/> (length <c>sourceWidth + 2</c>, edges
    /// already replicated) into <paramref name="dstRow"/> (length <c>sourceWidth * 2</c>).
    /// </summary>
    private static void BlendRow(ReadOnlySpan<ushort> a, int sourceWidth, Span<byte> dstRow)
    {
        int c = 0;
        for (; c + 8 <= sourceWidth; c += 8)
        {
            var center = Vector128.Create(a.Slice(c + 1, 8));
            var left = Vector128.Create(a.Slice(c, 8));
            var right = Vector128.Create(a.Slice(c + 2, 8));

            var centerTimes3 = center + center + center;
            var leftBlend = Vector128.ShiftRightLogical(centerTimes3 + left + Eight, 4);
            var rightBlend = Vector128.ShiftRightLogical(centerTimes3 + right + Eight, 4);

            // Narrow packs [leftBlend0..7, rightBlend0..7] into one 16-byte vector; Shuffle interleaves that
            // into actual output order [left0,right0,left1,right1,...] — dst[2c]=leftBlend[c] (uses the
            // column to c's left), dst[2c+1]=rightBlend[c] (uses the column to c's right).
            var combined = Vector128.Narrow(leftBlend, rightBlend);
            var interleaved = Vector128.Shuffle(combined, InterleaveMask);
            interleaved.CopyTo(dstRow.Slice(c * 2, 16));
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

    private static Vector128<ushort> WidenToUShort(ReadOnlySpan<byte> source8)
    {
        Span<byte> padded = stackalloc byte[16];
        source8.CopyTo(padded);
        return Vector128.WidenLower(Vector128.Create((ReadOnlySpan<byte>)padded));
    }

    /// <summary>Original per-pixel scalar path — kept for <c>horizontalRatio == 1</c> (vertical-only subsampling, essentially never seen in real files, not worth vectorizing).</summary>
    private static void UpsampleScalar(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight,
        int horizontalRatio, int verticalRatio)
    {
        for (int y = 0; y < destinationHeight; y++)
        {
            int srcY = Math.Clamp(y / verticalRatio, 0, sourceHeight - 1);
            int srcYNeighbor = verticalRatio == 2
                ? Math.Clamp(((y / verticalRatio) + (((y & 1) == 0) ? -1 : 1)), 0, sourceHeight - 1)
                : srcY;

            var dstRow = destination.Slice(y * destinationWidth, destinationWidth);
            var srcRowMain = source.Slice(srcY * sourceWidth, sourceWidth);
            var srcRowNeighbor = source.Slice(srcYNeighbor * sourceWidth, sourceWidth);

            for (int x = 0; x < destinationWidth; x++)
            {
                int srcX = Math.Clamp(x / horizontalRatio, 0, sourceWidth - 1);
                int srcXNeighbor = horizontalRatio == 2
                    ? Math.Clamp(((x / horizontalRatio) + (((x & 1) == 0) ? -1 : 1)), 0, sourceWidth - 1)
                    : srcX;

                // 9:3:3:1 weighted blend of the sample and its diagonal/adjacent neighbors, matching libjpeg's
                // "fancy" (triangle-filter) chroma upsampling: mostly the nearest sample, softened toward its neighbors.
                int main = srcRowMain[srcX];
                int horizontalNeighbor = srcRowMain[srcXNeighbor];
                int verticalNeighbor = srcRowNeighbor[srcX];
                int diagonalNeighbor = srcRowNeighbor[srcXNeighbor];

                int blended = ((main * 9) + (horizontalNeighbor * 3) + (verticalNeighbor * 3) + diagonalNeighbor + 8) / 16;
                dstRow[x] = (byte)Math.Clamp(blended, 0, 255);
            }
        }
    }
}
