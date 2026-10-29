using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// The deblocking loop filter (spec §7.14), restricted to the intra-only path: <c>ref</c> is always
/// <c>INTRA_FRAME</c> and <c>isIntra</c> is always true, so the inter-only <c>loop_filter_mode_deltas</c>
/// branch and the <c>modeType</c> distinction (spec §7.14.4) never apply. Mutates
/// <see cref="Av1FrameDecodeResult.Planes"/> in place, matching the spec's own in-place <c>CurrFrame</c>
/// semantics.
/// </summary>
internal static class Av1DeblockingFilter
{
    private const int MaxLoopFilter = 63;
    private const int SegLvlAltLfYV = 1;

    public static void Apply(Av1FrameDecodeResult result)
    {
        var seq = result.Sequence;
        var frame = result.Frame;
        var level = frame.LoopFilter.Level;

        // Only cells whose top-left sample is inside the frame can carry an edge; the frame-edge exclusions
        // (x == 0 for vertical edges, y == 0 for horizontal ones) are folded into the loop starts.
        int rowEnd = Math.Min(frame.MiRows, (frame.FrameHeight + 3) >> 2);
        int colEnd = Math.Min(frame.MiCols, (frame.FrameWidth + 3) >> 2);

        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            if (plane != 0 && level[1 + plane] == 0)
            {
                continue;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool chroma = plane != 0;
                int subX = chroma && seq.SubsamplingX ? 1 : 0;
                int subY = chroma && seq.SubsamplingY ? 1 : 0;
                int rowStep = plane == 0 ? 1 : 1 << subY;
                int colStep = plane == 0 ? 1 : 1 << subX;
                int rowStart = pass == 1 ? rowStep : 0;
                int colStart = pass == 0 ? colStep : 0;

                // A vertical-edge pass (0) filters along sample rows and a horizontal-edge pass (1) along
                // sample columns, so within a pass the mi rows (pass 0) or mi columns (pass 1) are fully
                // independent as long as each chunk keeps its own cells in order. Chunks are multiples of
                // BandCells (a multiple of every step), and the pass boundary is the barrier between them.
                int span = pass == 0 ? rowEnd : colEnd;
                int chunks = (span + BandCells - 1) / BandCells;
                if (span >= ParallelMinCells && chunks > 1 && Environment.ProcessorCount > 1)
                {
                    Parallel.For(0, chunks, chunk =>
                    {
                        int lo = chunk * BandCells;
                        int hi = Math.Min(span, lo + BandCells);
                        if (pass == 0)
                        {
                            FilterRegion(result, plane, pass, Math.Max(lo, rowStart), hi, rowStep, colStart, colEnd, colStep);
                        }
                        else
                        {
                            FilterRegion(result, plane, pass, rowStart, rowEnd, rowStep, Math.Max(lo, colStart), hi, colStep);
                        }
                    });
                }
                else
                {
                    FilterRegion(result, plane, pass, rowStart, rowEnd, rowStep, colStart, colEnd, colStep);
                }
            }
        }
    }

    /// <summary>Mi rows or columns per parallel chunk; a multiple of every plane's cell step.</summary>
    private const int BandCells = 32;

    /// <summary>Frames spanning fewer mi rows/columns than this are filtered on the calling thread.</summary>
    private const int ParallelMinCells = 128;

    private static void FilterRegion(Av1FrameDecodeResult result, int plane, int pass, int rowStart, int rowEnd, int rowStep, int colStart, int colEnd, int colStep)
    {
        var memo = new StrengthMemo();
        memo.Reset();
        for (int row = rowStart; row < rowEnd; row += rowStep)
        {
            for (int col = colStart; col < colEnd; col += colStep)
            {
                LoopFilterEdge(result, plane, pass, row, col, ref memo);
            }
        }
    }

    /// <summary>
    /// Filter strength of the last (segment, delta-LF) pair seen. Strength depends only on those two values
    /// (plus the fixed plane/pass of the current loop), which are constant across long runs of cells.
    /// </summary>
    private struct StrengthMemo
    {
        public int Segment;
        public int DeltaLf;
        public int Lvl;
        public int Limit;
        public int Blimit;
        public int Thresh;

        public void Reset() => Segment = -1;
    }

    /// <summary><c>loop_filter_edge</c> / the edge loop filter process (spec §7.14.2).</summary>
    private static void LoopFilterEdge(Av1FrameDecodeResult result, int plane, int pass, int row, int col, ref StrengthMemo memo)
    {
        var seq = result.Sequence;
        var frame = result.Frame;

        int subX = plane != 0 && seq.SubsamplingX ? 1 : 0;
        int subY = plane != 0 && seq.SubsamplingY ? 1 : 0;

        int dx = pass == 0 ? 1 : 0;
        int dy = pass == 0 ? 0 : 1;

        int x = col * 4;
        int y = row * 4;

        row |= subY;
        col |= subX;

        int xP = x >> subX;
        int yP = y >> subY;

        int lfStride = result.LoopfilterTxSizeStrides[plane];
        int txSz = result.LoopfilterTxSizes[plane][((row >> subY) * lfStride) + (col >> subX)];

        // applyFilter (spec §7.14.2) reduces to isTxEdge here: isIntra is always true for this
        // intra-only decoder, and applyFilter is 1 whenever isTxEdge && (isBlockEdge || !skip ||
        // isIntra) -- with isIntra always true, the isBlockEdge/skip terms never change the result, so
        // isBlockEdge (and the MiSizes/Skips reads it would need) is never computed. Transform dimensions
        // are powers of two, so the "multiple of the transform size" test is a mask.
        bool isTxEdge = pass == 0
            ? (xP & (Av1TxDimensions.Width[txSz] - 1)) == 0
            : (yP & (Av1TxDimensions.Height[txSz] - 1)) == 0;
        if (!isTxEdge)
        {
            return;
        }

        int prevRow = row - (dy << subY);
        int prevCol = col - (dx << subX);
        int prevTxSz = result.LoopfilterTxSizes[plane][((prevRow >> subY) * lfStride) + (prevCol >> subX)];
        int filterSize = FilterSize(txSz, prevTxSz, pass, plane);

        int idx = (row * frame.MiCols) + col;
        int segment = result.SegmentIds[idx];
        int i = plane == 0 ? pass : plane + 1;
        int deltaLf = frame.DeltaLfMulti ? result.DeltaLfs[i][idx] : result.DeltaLfs[0][idx];
        if (memo.Segment != segment || memo.DeltaLf != deltaLf)
        {
            memo.Segment = segment;
            memo.DeltaLf = deltaLf;
            memo.Lvl = FilterStrength(frame, segment, deltaLf, i, out memo.Limit, out memo.Blimit, out memo.Thresh);
        }

        int lvl = memo.Lvl;
        int limit = memo.Limit;
        int blimit = memo.Blimit;
        int thresh = memo.Thresh;
        if (lvl == 0)
        {
            int prevIdx = (prevRow * frame.MiCols) + prevCol;
            int prevDeltaLf = frame.DeltaLfMulti ? result.DeltaLfs[i][prevIdx] : result.DeltaLfs[0][prevIdx];
            lvl = FilterStrength(frame, result.SegmentIds[prevIdx], prevDeltaLf, i, out limit, out blimit, out thresh);
            if (lvl == 0)
            {
                return;
            }
        }

        // The four samples along this 4-sample edge segment: each filter reads/writes a line of samples
        // perpendicular to the edge (stride `step`), and consecutive lines are `advance` apart.
        var buf = result.Planes[plane];
        int stride = result.PlaneWidths[plane];
        int step = (dy * stride) + dx;
        int advance = (dx * stride) + dy;
        int pos = (yP * stride) + xP;
        int bitDepth = seq.BitDepth;
        for (int k = 0; k < 4; k++)
        {
            SampleFilter(buf, pos, step, plane, bitDepth, limit, blimit, thresh, filterSize);
            pos += advance;
        }
    }

    /// <summary><c>Filter size process</c> (spec §7.14.3).</summary>
    private static int FilterSize(int txSz, int prevTxSz, int pass, int plane)
    {
        int baseSize = pass == 0
            ? Math.Min(Av1TxDimensions.Width[prevTxSz], Av1TxDimensions.Width[txSz])
            : Math.Min(Av1TxDimensions.Height[prevTxSz], Av1TxDimensions.Height[txSz]);

        return plane == 0 ? Math.Min(16, baseSize) : Math.Min(8, baseSize);
    }

    /// <summary><c>Adaptive filter strength process</c> + <c>selection process</c> (spec §7.14.4-§7.14.5), restricted to the intra-only path. <paramref name="i"/> is the spec's loop-filter-level index (pass for luma, plane + 1 for chroma).</summary>
    private static int FilterStrength(Av1FrameHeader frame, int segment, int deltaLf, int i, out int limit, out int blimit, out int thresh)
    {
        int baseFilterLevel = Math.Clamp(deltaLf + frame.LoopFilter.Level[i], 0, MaxLoopFilter);
        int lvlSeg = baseFilterLevel;
        int feature = SegLvlAltLfYV + i;
        if (frame.Segmentation.Enabled && frame.Segmentation.FeatureEnabled[segment, feature])
        {
            lvlSeg = Math.Clamp(frame.Segmentation.FeatureData[segment, feature] + lvlSeg, 0, MaxLoopFilter);
        }

        if (frame.LoopFilter.DeltaEnabled)
        {
            int nShift = lvlSeg >> 5;
            lvlSeg = Math.Clamp(lvlSeg + (frame.LoopFilter.RefDeltas[0] << nShift), 0, MaxLoopFilter);
        }

        int lvl = lvlSeg;
        int sharpness = frame.LoopFilter.Sharpness;
        int shift = sharpness > 4 ? 2 : sharpness > 0 ? 1 : 0;
        limit = sharpness > 0 ? Math.Clamp(lvl >> shift, 1, 9 - sharpness) : Math.Max(1, lvl >> shift);
        blimit = (2 * (lvl + 2)) + limit;
        thresh = lvl >> 4;
        return lvl;
    }

    /// <summary>
    /// <c>Sample filtering process</c> (spec §7.14.6.1) including the <c>Filter mask process</c> (§7.14.6.2),
    /// on the line of samples through <paramref name="buf"/>[<paramref name="pos"/>] (q0) running
    /// perpendicular to the edge with element stride <paramref name="step"/> (p0 is at <c>pos - step</c>).
    /// Samples the selected filter length never consults (p2/p3/q2/q3 for short filters) are not read.
    /// </summary>
    private static void SampleFilter(int[] buf, int pos, int step, int plane, int bitDepth, int limit, int blimit, int thresh, int filterSize)
    {
        int q0 = buf[pos];
        int q1 = buf[pos + step];
        int p0 = buf[pos - step];
        int p1 = buf[pos - (2 * step)];

        int filterLen = filterSize == 4 ? 4 : plane != 0 ? 6 : filterSize == 8 ? 8 : 16;
        int q2 = 0, q3 = 0, p2 = 0, p3 = 0;
        if (filterLen >= 6)
        {
            q2 = buf[pos + (2 * step)];
            p2 = buf[pos - (3 * step)];
        }

        if (filterLen >= 8)
        {
            q3 = buf[pos + (3 * step)];
            p3 = buf[pos - (4 * step)];
        }

        int shift = bitDepth - 8;
        int threshBd = thresh << shift;
        int limitBd = limit << shift;
        int blimitBd = blimit << shift;

        int absP1P0 = Math.Abs(p1 - p0);
        int absQ1Q0 = Math.Abs(q1 - q0);
        bool hevMask = absP1P0 > threshBd || absQ1Q0 > threshBd;

        bool mask = absP1P0 > limitBd
            || absQ1Q0 > limitBd
            || (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) >> 1) > blimitBd;

        if (filterLen >= 6)
        {
            mask = mask || Math.Abs(p2 - p1) > limitBd || Math.Abs(q2 - q1) > limitBd;
        }

        if (filterLen >= 8)
        {
            mask = mask || Math.Abs(p3 - p2) > limitBd || Math.Abs(q3 - q2) > limitBd;
        }

        if (mask)
        {
            return;
        }

        int thresholdBd = 1 << shift;
        bool flatMask = false;
        if (filterSize >= 8)
        {
            bool m = absP1P0 > thresholdBd
                || absQ1Q0 > thresholdBd
                || Math.Abs(p2 - p0) > thresholdBd
                || Math.Abs(q2 - q0) > thresholdBd;

            if (filterLen >= 8)
            {
                m = m || Math.Abs(p3 - p0) > thresholdBd || Math.Abs(q3 - q0) > thresholdBd;
            }

            flatMask = !m;
        }

        if (filterSize == 4 || !flatMask)
        {
            NarrowFilter(buf, pos, step, hevMask, p1, p0, q0, q1, bitDepth);
            return;
        }

        if (filterSize == 8)
        {
            WideFilter(buf, pos, step, plane, 3);
            return;
        }

        // filterSize == 16 (luma only): flat_mask2 looks at the outer six samples.
        bool flatMask2 = !(Math.Abs(buf[pos - (7 * step)] - p0) > thresholdBd
            || Math.Abs(buf[pos + (6 * step)] - q0) > thresholdBd
            || Math.Abs(buf[pos - (6 * step)] - p0) > thresholdBd
            || Math.Abs(buf[pos + (5 * step)] - q0) > thresholdBd
            || Math.Abs(buf[pos - (5 * step)] - p0) > thresholdBd
            || Math.Abs(buf[pos + (4 * step)] - q0) > thresholdBd);

        WideFilter(buf, pos, step, plane, flatMask2 ? 4 : 3);
    }

    /// <summary><c>Narrow filter process</c> (spec §7.14.6.3).</summary>
    private static void NarrowFilter(int[] buf, int pos, int step, bool hevMask, int p1, int p0, int q0, int q1, int bitDepth)
    {
        int bound = 1 << (bitDepth - 1);
        int bias = 0x80 << (bitDepth - 8);
        int ps1 = p1 - bias;
        int ps0 = p0 - bias;
        int qs0 = q0 - bias;
        int qs1 = q1 - bias;

        int filter = hevMask ? Math.Clamp(ps1 - qs1, -bound, bound - 1) : 0;
        filter = Math.Clamp(filter + (3 * (qs0 - ps0)), -bound, bound - 1);
        int filter1 = Math.Clamp(filter + 4, -bound, bound - 1) >> 3;
        int filter2 = Math.Clamp(filter + 3, -bound, bound - 1) >> 3;

        buf[pos] = Math.Clamp(qs0 - filter1, -bound, bound - 1) + bias;
        buf[pos - step] = Math.Clamp(ps0 + filter2, -bound, bound - 1) + bias;

        if (!hevMask)
        {
            int f = (filter1 + 1) >> 1;
            buf[pos + step] = Math.Clamp(qs1 - f, -bound, bound - 1) + bias;
            buf[pos - (2 * step)] = Math.Clamp(ps1 + f, -bound, bound - 1) + bias;
        }
    }

    /// <summary><c>Wide filter process</c> (spec §7.14.6.4).</summary>
    [SkipLocalsInit]
    private static void WideFilter(int[] buf, int pos, int step, int plane, int log2Size)
    {
        int n = log2Size == 4 ? 6 : plane == 0 ? 3 : 2;
        int n2 = log2Size == 3 && plane == 0 ? 0 : 1;

        // Samples at offsets [-(n+1), n] around the edge, indexed offset + n + 1. Sums stay far inside int
        // (at most 2n+2 taps of weight <= 2 on samples < 2^12).
        Span<int> s = stackalloc int[14];
        int count = (2 * n) + 2;
        int p = pos - ((n + 1) * step);
        for (int k = 0; k < count; k++)
        {
            s[k] = buf[p];
            p += step;
        }

        Span<int> f = stackalloc int[12];
        int rounding = 1 << (log2Size - 1);
        for (int i = -n; i < n; i++)
        {
            int t = 0;
            for (int j = -n; j <= n; j++)
            {
                int idx = Math.Clamp(i + j, -(n + 1), n) + n + 1;
                t += s[idx] * (Math.Abs(j) <= n2 ? 2 : 1);
            }

            f[i + n] = (t + rounding) >> log2Size;
        }

        p = pos - (n * step);
        for (int k = 0; k < 2 * n; k++)
        {
            buf[p] = f[k];
            p += step;
        }
    }
}
