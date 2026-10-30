using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// CDEF (constrained directional enhancement filter, spec §7.15): direction search per 8x8 luma block
/// followed by primary/secondary directional filtering. Reads from <see cref="Av1FrameDecodeResult{TPixel}.Planes"/>
/// (the deblocked <c>CurrFrame</c>) and writes into freshly-allocated <c>CdefFrame</c> buffers (a copy of
/// the input, selectively overwritten), which then replace <see cref="Av1FrameDecodeResult{TPixel}.Planes"/> --
/// matching the spec's own read-from-CurrFrame / write-to-CdefFrame separation, which exists precisely so
/// that filtering one 8x8 block never sees another 8x8 block's already-filtered output.
/// </summary>
internal static class Av1Cdef
{
    /// <summary>Frames with fewer 8-row strips than this are filtered on the calling thread.</summary>
    private const int ParallelMinStrips = 32;

    private static readonly int[] DivTable = [0, 840, 420, 280, 210, 168, 140, 120, 105];

    /// <summary><c>Cdef_Uv_Dir</c> (spec §7.15.1), indexed <c>[subsampling_x][subsampling_y][yDir]</c>.</summary>
    private static readonly int[][][] CdefUvDir =
    [
        [[0, 1, 2, 3, 4, 5, 6, 7], [1, 2, 2, 2, 3, 4, 6, 0]],
        [[7, 0, 2, 4, 5, 6, 6, 6], [0, 1, 2, 3, 4, 5, 6, 7]],
    ];

    private static readonly int[][][] CdefDirections =
    [
        [[-1, 1], [-2, 2]],
        [[0, 1], [-1, 2]],
        [[0, 1], [0, 2]],
        [[0, 1], [1, 2]],
        [[1, 1], [2, 2]],
        [[1, 0], [2, 1]],
        [[1, 0], [2, 0]],
        [[1, 0], [2, -1]],
    ];

    private static readonly int[][] CdefPriTaps = [[4, 2], [3, 3]];
    private static readonly int[][] CdefSecTaps = [[2, 1], [2, 1]];

    public static void Apply<TPixel>(Av1FrameDecodeResult<TPixel> result)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var seq = result.Sequence;
        var frame = result.Frame;

        if (!seq.EnableCdef || frame.CodedLossless)
        {
            return;
        }

        // The r/c loop below (bounded by frame.MiRows/MiCols, the true coded size) visits every 8x8 block
        // exactly once, and CdefBlock's first action for every block is an unconditional CopyBlock() from
        // result.Planes into this array -- so that region is fully overwritten before CDEF reads it back
        // and needs no explicit initialization. But the array itself is rented to PlaneWidths x
        // PlaneHeights, the superblock-*aligned* canvas, which is usually larger than MiRows*4 x MiCols*4
        // -- and unlike `new int[]`, ArrayPool.Rent does NOT zero that padding. A downstream consumer
        // (Av1TileComposer.CopyRegion's mismatched-tile-size clamp) can legitimately read into it for an
        // undersized/reused tile item, so it's cleared explicitly here to match `new int[]`'s guarantee.
        var cdefPlanes = new TPixel[3][];
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            int length = result.PlaneWidths[plane] * result.PlaneHeights[plane];
            cdefPlanes[plane] = ArrayPool<TPixel>.Shared.Rent(length);
            ClearPadding(cdefPlanes[plane], result, seq, frame, plane);
        }

        int step4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block8x8];
        int cdefSize4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block64x64];
        int cdefMask4 = ~(cdefSize4 - 1);

        // Each 8-luma-row strip reads only the (immutable) deblocked planes and writes only its own rows of
        // the output planes, so strips run independently; the per-strip State carries the availability flag.
        int strips = (frame.MiRows + step4 - 1) / step4;
        if (strips >= ParallelMinStrips && Environment.ProcessorCount > 1)
        {
            Parallel.For(0, strips, () => new State<TPixel>(result, cdefPlanes), (strip, _, state) =>
            {
                CdefStrip(result, state, cdefPlanes, strip * step4, step4, cdefMask4);
                return state;
            }, static _ => { });
        }
        else
        {
            var state = new State<TPixel>(result, cdefPlanes);
            for (int strip = 0; strip < strips; strip++)
            {
                CdefStrip(result, state, cdefPlanes, strip * step4, step4, cdefMask4);
            }
        }

        // The array being replaced here (whatever Av1FrameDecoder rented as the tile-decode reconstruction
        // target -- deblocking filters in place, so it's still the same object) has now been fully
        // superseded and is safe to return: cdefPlanes was seeded from it (see the CopyBlock note above)
        // and nothing else in the pipeline retains a separate reference to it.
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            ArrayPool<TPixel>.Shared.Return(result.Planes[plane]);
            result.Planes[plane] = cdefPlanes[plane];
        }
    }

    private static void CdefStrip<TPixel>(Av1FrameDecodeResult<TPixel> result, State<TPixel> state, TPixel[][] cdefPlanes, int r, int step4, int cdefMask4)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var frame = result.Frame;
        CopyStrip(result, cdefPlanes, result.Sequence, r, frame.MiCols);

        for (int c = 0; c < frame.MiCols; c += step4)
        {
            int baseR = r & cdefMask4;
            int baseC = c & cdefMask4;
            int idx = result.CdefIdx[(baseR * frame.MiCols) + baseC];
            CdefBlock(state, r, c, idx);
        }
    }

    private sealed class State<TPixel>(Av1FrameDecodeResult<TPixel> result, TPixel[][] cdefPlanes)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        public Av1FrameDecodeResult<TPixel> Result { get; } = result;

        public TPixel[][] CdefPlanes { get; } = cdefPlanes;

        public bool CdefAvailable { get; set; }
    }

    /// <summary><c>CDEF block process</c> (spec §7.15.1).</summary>
    private static void CdefBlock<TPixel>(State<TPixel> state, int r, int c, int idx)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = state.Result;
        var seq = result.Sequence;
        var frame = result.Frame;

        if (idx == -1)
        {
            return;
        }

        int coeffShift = seq.BitDepth - 8;
        int miCols = frame.MiCols;
        bool skip = result.Skips[(r * miCols) + c]
            && (r + 1 >= frame.MiRows || result.Skips[((r + 1) * miCols) + c])
            && (c + 1 >= miCols || result.Skips[(r * miCols) + c + 1])
            && (r + 1 >= frame.MiRows || c + 1 >= miCols || result.Skips[((r + 1) * miCols) + c + 1]);

        if (skip)
        {
            return;
        }

        // The direction (and variance) only steer the primary taps, so a block whose luma and chroma
        // primary strengths are both zero never needs the search: dir stays 0 and the adjusted priStr stays 0.
        bool needDirection = frame.Cdef.YPriStrength[idx] != 0 || (seq.NumPlanes > 1 && frame.Cdef.UvPriStrength[idx] != 0);
        int yDir = 0;
        int var = 0;
        if (needDirection)
        {
            (yDir, var) = CdefDirection(state, r, c);
        }

        int priStr = frame.Cdef.YPriStrength[idx] << coeffShift;
        int secStr = frame.Cdef.YSecStrength[idx] << coeffShift;
        int dir = priStr == 0 ? 0 : yDir;
        int varStr = (var >> 6) != 0 ? Math.Min(FloorLog2(var >> 6), 12) : 0;
        priStr = var != 0 ? (((priStr * (4 + varStr)) + 8) >> 4) : 0;
        int damping = frame.Cdef.Damping + coeffShift;

        // With both strengths zero every constrained tap is 0, so the output is the unchanged center
        // sample, which the strip copy already wrote.
        if (priStr != 0 || secStr != 0)
        {
            CdefFilter(state, 0, r, c, priStr, secStr, damping, dir);
        }

        if (seq.NumPlanes == 1)
        {
            return;
        }

        priStr = frame.Cdef.UvPriStrength[idx] << coeffShift;
        secStr = frame.Cdef.UvSecStrength[idx] << coeffShift;
        dir = priStr == 0 ? 0 : CdefUvDir[seq.SubsamplingX ? 1 : 0][seq.SubsamplingY ? 1 : 0][yDir];
        damping = frame.Cdef.Damping + coeffShift - 1;

        if (priStr != 0 || secStr != 0)
        {
            CdefFilter(state, 1, r, c, priStr, secStr, damping, dir);
            CdefFilter(state, 2, r, c, priStr, secStr, damping, dir);
        }
    }

    /// <summary>
    /// Zeroes the part of a rented output plane that <see cref="CopyStrip"/> will not overwrite: the columns
    /// right of the copied width in each copied row, and every row below the last strip.
    /// </summary>
    private static void ClearPadding<TPixel>(TPixel[] plane, Av1FrameDecodeResult<TPixel> result, Av1SequenceHeader seq, Av1FrameHeader frame, int planeIndex)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int subX = planeIndex > 0 && seq.SubsamplingX ? 1 : 0;
        int subY = planeIndex > 0 && seq.SubsamplingY ? 1 : 0;
        int stride = result.PlaneWidths[planeIndex];
        int height = result.PlaneHeights[planeIndex];
        int copiedWidth = (((frame.MiCols + 1) & ~1) * 4) >> subX;

        // CopyStrip covers rows [0, last strip end), where strips start at even mi rows and span 8 luma rows.
        int lastStripStartMi = ((frame.MiRows - 1) / 2) * 2;
        int stripEnd = Math.Min(height, ((lastStripStartMi * 4) + 8) >> subY);

        for (int y = 0; y < stripEnd; y++)
        {
            plane.AsSpan((y * stride) + copiedWidth, stride - copiedWidth).Clear();
        }

        plane.AsSpan(stripEnd * stride, (height - stripEnd) * stride).Clear();
    }

    /// <summary>
    /// Seeds the output planes with the deblocked samples for the 8-luma-row strip starting at mi row
    /// <paramref name="r"/>: exactly the union of the per-8x8-block regions (columns up to MiCols rounded up to
    /// an even count of 4-sample units) the spec's per-block copy covers, but as one contiguous copy per row.
    /// </summary>
    private static void CopyStrip<TPixel>(Av1FrameDecodeResult<TPixel> result, TPixel[][] cdefPlanes, Av1SequenceHeader seq, int r, int miCols)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int widthLuma = ((miCols + 1) & ~1) * 4;
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            int subX = plane > 0 && seq.SubsamplingX ? 1 : 0;
            int subY = plane > 0 && seq.SubsamplingY ? 1 : 0;
            int stride = result.PlaneWidths[plane];
            int startY = (r * 4) >> subY;
            int endY = ((r * 4) + 8) >> subY;
            int width = widthLuma >> subX;
            for (int y = startY; y < endY; y++)
            {
                result.Planes[plane].AsSpan(y * stride, width).CopyTo(cdefPlanes[plane].AsSpan(y * stride, width));
            }
        }
    }

    /// <summary><c>CDEF direction process</c> (spec §7.15.2).</summary>
    [SkipLocalsInit]
    private static (int YDir, int Var) CdefDirection<TPixel>(State<TPixel> state, int r, int c)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = state.Result;

        // partial[d][k] of the spec flattened to d * 16 + k (15 used per direction). Sample sums are tiny
        // (64 samples of magnitude <= 128), so int is exact; only the squared costs need 64 bits.
        Span<int> partial = stackalloc int[8 * 16];
        partial.Clear();

        var luma = result.Planes[0];
        int stride = result.PlaneWidths[0];
        int shift = result.Sequence.BitDepth - 8;

        int x0 = c * 4;
        int y0 = r * 4;

        for (int i = 0; i < 8; i++)
        {
            int rowBase = ((y0 + i) * stride) + x0;
            for (int j = 0; j < 8; j++)
            {
                int x = (int.CreateTruncating(luma[rowBase + j]) >> shift) - 128;
                partial[i + j] += x;
                partial[16 + i + (j >> 1)] += x;
                partial[32 + i] += x;
                partial[48 + 3 + i - (j >> 1)] += x;
                partial[64 + 7 + i - j] += x;
                partial[80 + 3 - (i >> 1) + j] += x;
                partial[96 + j] += x;
                partial[112 + (i >> 1) + j] += x;
            }
        }

        Span<long> cost = stackalloc long[8];
        cost.Clear();

        for (int i = 0; i < 8; i++)
        {
            cost[2] += (long)partial[32 + i] * partial[32 + i];
            cost[6] += (long)partial[96 + i] * partial[96 + i];
        }

        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];

        for (int i = 0; i < 7; i++)
        {
            cost[0] += (((long)partial[i] * partial[i]) + ((long)partial[14 - i] * partial[14 - i])) * DivTable[i + 1];
            cost[4] += (((long)partial[64 + i] * partial[64 + i]) + ((long)partial[64 + 14 - i] * partial[64 + 14 - i])) * DivTable[i + 1];
        }

        cost[0] += (long)partial[7] * partial[7] * DivTable[8];
        cost[4] += (long)partial[64 + 7] * partial[64 + 7] * DivTable[8];

        for (int i = 1; i < 8; i += 2)
        {
            int pb = i * 16;
            for (int j = 0; j < 5; j++)
            {
                cost[i] += (long)partial[pb + 3 + j] * partial[pb + 3 + j];
            }

            cost[i] *= DivTable[8];

            for (int j = 0; j < 3; j++)
            {
                cost[i] += (((long)partial[pb + j] * partial[pb + j]) + ((long)partial[pb + 10 - j] * partial[pb + 10 - j])) * DivTable[(2 * j) + 2];
            }
        }

        long bestCost = 0;
        int yDir = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cost[i] > bestCost)
            {
                bestCost = cost[i];
                yDir = i;
            }
        }

        int var = (int)((bestCost - cost[(yDir + 4) & 7]) >> 10);
        return (yDir, var);
    }

    /// <summary><c>CDEF filter process</c> (spec §7.15.3).</summary>
    private static void CdefFilter<TPixel>(State<TPixel> state, int plane, int r, int c, int priStr, int secStr, int damping, int dir)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = state.Result;
        var seq = result.Sequence;
        var frame = result.Frame;

        int subX = plane != 0 && seq.SubsamplingX ? 1 : 0;
        int subY = plane != 0 && seq.SubsamplingY ? 1 : 0;
        int coeffShift = seq.BitDepth - 8;

        int x0 = (c * 4) >> subX;
        int y0 = (r * 4) >> subY;
        int w = 8 >> subX;
        int h = 8 >> subY;

        var currPlane = result.Planes[plane];
        var cdefPlane = state.CdefPlanes[plane];
        int stride = result.PlaneWidths[plane];

        int tapIdx = (priStr >> coeffShift) & 1;
        int priTap0 = CdefPriTaps[tapIdx][0];
        int priTap1 = CdefPriTaps[tapIdx][1];
        int secTap0 = CdefSecTaps[tapIdx][0];
        int secTap1 = CdefSecTaps[tapIdx][1];

        int dirA = (dir - 2) & 7;
        int dirB = (dir + 2) & 7;
        int pDy0 = CdefDirections[dir][0][0], pDx0 = CdefDirections[dir][0][1];
        int pDy1 = CdefDirections[dir][1][0], pDx1 = CdefDirections[dir][1][1];
        int aDy0 = CdefDirections[dirA][0][0], aDx0 = CdefDirections[dirA][0][1];
        int aDy1 = CdefDirections[dirA][1][0], aDx1 = CdefDirections[dirA][1][1];
        int bDy0 = CdefDirections[dirB][0][0], bDx0 = CdefDirections[dirB][0][1];
        int bDy1 = CdefDirections[dirB][1][0], bDx1 = CdefDirections[dirB][1][1];

        // Every tap this call can ever make is offset from (y0+i, x0+j) by at most 2 samples along
        // either axis (the largest component in Cdef_Directions), for both primary and secondary taps
        // combined. If the block's footprint extended by that fixed margin still translates (via the
        // same <<sub>>2 MI-unit conversion is_inside_filter_region uses) to a range fully inside
        // [0,MiRows)x[0,MiCols), every tap this call makes is unconditionally available -- so the
        // per-tap availability check (state.CdefAvailable, a property write/read on every one of ~12
        // taps per sample) can be skipped entirely for the (overwhelming majority, interior) case,
        // falling back to the exact original bounds-checked path only for blocks near the frame edge.
        int rMin = ((y0 - 2) << subY) >> 2;
        int rMax = ((y0 + h + 1) << subY) >> 2;
        int cMin = ((x0 - 2) << subX) >> 2;
        int cMax = ((x0 + w + 1) << subX) >> 2;
        bool interior = rMin >= 0 && rMax < frame.MiRows && cMin >= 0 && cMax < frame.MiCols;

        int priDampingAdj = DampingAdjust(priStr, damping);
        int secDampingAdj = DampingAdjust(secStr, damping);

        // Interior blocks (the common case) can be vectorized a full row at a time: w is always exactly
        // 8 (luma, or 4:4:4 chroma) or 4 (subsampled chroma), matching Vector256<int>/Vector128<int>
        // exactly, and every tap is plain integer arithmetic -- unlike float SIMD, integer vector ops are
        // bit-for-bit identical to their scalar equivalents on any hardware, so this carries none of the
        // cross-hardware precision risk a floating-point kernel would. Falls through to the scalar loop
        // below (unchanged) when the block is edge-adjacent or the current hardware lacks the relevant
        // vector width.
        if (interior && w == 8 && Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < h; i++)
            {
                CdefFilterRow256(currPlane, cdefPlane, stride, x0, y0 + i, priTap0, priTap1, secTap0, secTap1, priStr, secStr, priDampingAdj, secDampingAdj, pDy0, pDx0, pDy1, pDx1, aDy0, aDx0, aDy1, aDx1, bDy0, bDx0, bDy1, bDx1);
            }

            return;
        }

        // 8-wide blocks without Vector256 (Arm64 NEON is 128-bit only) run as two independent 4-lane halves:
        // each column's result depends only on currPlane, which is read-only here, so the halves don't interact.
        if (interior && (w == 4 || w == 8) && Vector128.IsHardwareAccelerated)
        {
            for (int i = 0; i < h; i++)
            {
                for (int xOff = 0; xOff < w; xOff += 4)
                {
                    CdefFilterRow128(currPlane, cdefPlane, stride, x0 + xOff, y0 + i, priTap0, priTap1, secTap0, secTap1, priStr, secStr, priDampingAdj, secDampingAdj, pDy0, pDx0, pDy1, pDx1, aDy0, aDx0, aDy1, aDx1, bDy0, bDx0, bDy1, bDx1);
                }
            }

            return;
        }

        for (int i = 0; i < h; i++)
        {
            int y = y0 + i;
            for (int j = 0; j < w; j++)
            {
                int x = x0 + j;
                int center = int.CreateTruncating(currPlane[(y * stride) + x]);
                long sum = 0;
                int max = center;
                int min = center;

                if (interior)
                {
                    Accum(currPlane, stride, y, x, -pDy0, -pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, -aDy0, -aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, -bDy0, -bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, pDy0, pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, aDy0, aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, bDy0, bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);

                    Accum(currPlane, stride, y, x, -pDy1, -pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, -aDy1, -aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, -bDy1, -bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, pDy1, pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, aDy1, aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                    Accum(currPlane, stride, y, x, bDy1, bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
                }
                else
                {
                    for (int k = 0; k < 2; k++)
                    {
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            int p = CdefGetAt(state, plane, x0, y0, i, j, dir, k, sign, subX, subY);
                            if (state.CdefAvailable)
                            {
                                sum += CdefPriTaps[tapIdx][k] * ConstrainFast(p - center, priStr, priDampingAdj);
                                max = Math.Max(p, max);
                                min = Math.Min(p, min);
                            }

                            for (int dirOff = -2; dirOff <= 2; dirOff += 4)
                            {
                                int s = CdefGetAt(state, plane, x0, y0, i, j, (dir + dirOff) & 7, k, sign, subX, subY);
                                if (state.CdefAvailable)
                                {
                                    sum += CdefSecTaps[tapIdx][k] * ConstrainFast(s - center, secStr, secDampingAdj);
                                    max = Math.Max(s, max);
                                    min = Math.Min(s, min);
                                }
                            }
                        }
                    }
                }

                int result8 = center + (int)((8 + sum - (sum < 0 ? 1 : 0)) >> 4);
                cdefPlane[(y * stride) + x] = TPixel.CreateTruncating(Math.Clamp(result8, min, max));
            }
        }
    }

    /// <summary>
    /// Vectorized equivalent of <see cref="CdefFilter"/>'s interior per-pixel body, one row (8 luma
    /// samples) at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CdefFilterRow256<TPixel>(
        TPixel[] currPlane, TPixel[] cdefPlane, int stride, int x0, int y,
        int priTap0, int priTap1, int secTap0, int secTap1, int priStr, int secStr, int priDampingAdj, int secDampingAdj,
        int pDy0, int pDx0, int pDy1, int pDx1, int aDy0, int aDx0, int aDy1, int aDx1, int bDy0, int bDx0, int bDy1, int bDx1)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int rowBase = (y * stride) + x0;
        var center = Av1PixelVector.Load8(ref currPlane[rowBase]);
        var sum = Vector256<int>.Zero;
        var max = center;
        var min = center;

        AccumVec256(currPlane, stride, rowBase, -pDy0, -pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -aDy0, -aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -bDy0, -bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, pDy0, pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, aDy0, aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, bDy0, bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);

        AccumVec256(currPlane, stride, rowBase, -pDy1, -pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -aDy1, -aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -bDy1, -bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, pDy1, pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, aDy1, aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, bDy1, bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);

        var negMask = Vector256.LessThan(sum, Vector256<int>.Zero);
        var adjusted = sum - (negMask & Vector256.Create(1));
        var rounded = (Vector256.Create(8) + adjusted) >> 4;
        var resultVal = center + rounded;
        var clamped = Vector256.Min(Vector256.Max(resultVal, min), max);
        Av1PixelVector.Store8(clamped, ref cdefPlane[rowBase]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumVec256<TPixel>(TPixel[] plane, int stride, int rowBase, int dy, int dx, int tap, int threshold, int dampingAdj, Vector256<int> center, ref Vector256<int> sum, ref Vector256<int> max, ref Vector256<int> min)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var p = Av1PixelVector.Load8(ref plane[rowBase + (dy * stride) + dx]);
        var diff = p - center;
        var absDiff = Vector256.Abs(diff);
        // constrain(diff, threshold, damping): sign * Clip3(0, absDiff, threshold - (absDiff >> dampingAdj))
        // (spec §7.15.3). threshold == 0 forces the whole expression to 0 the same way ConstrainFast's
        // explicit early-out does, since Max(0, 0 - anything) clamps to 0 regardless of absDiff.
        var constrainedAbs = Vector256.Min(absDiff, Vector256.Max(Vector256<int>.Zero, Vector256.Create(threshold) - (absDiff >> dampingAdj)));
        var negMask = Vector256.LessThan(diff, Vector256<int>.Zero);
        var constrained = Vector256.ConditionalSelect(negMask, -constrainedAbs, constrainedAbs);
        sum += constrained * Vector256.Create(tap);
        max = Vector256.Max(max, p);
        min = Vector256.Min(min, p);
    }

    /// <summary>Same as <see cref="CdefFilterRow256"/>, for the 4-wide (subsampled chroma) block width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CdefFilterRow128<TPixel>(
        TPixel[] currPlane, TPixel[] cdefPlane, int stride, int x0, int y,
        int priTap0, int priTap1, int secTap0, int secTap1, int priStr, int secStr, int priDampingAdj, int secDampingAdj,
        int pDy0, int pDx0, int pDy1, int pDx1, int aDy0, int aDx0, int aDy1, int aDx1, int bDy0, int bDx0, int bDy1, int bDx1)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int rowBase = (y * stride) + x0;
        var center = Av1PixelVector.Load4(ref currPlane[rowBase]);
        var sum = Vector128<int>.Zero;
        var max = center;
        var min = center;

        AccumVec128(currPlane, stride, rowBase, -pDy0, -pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -aDy0, -aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -bDy0, -bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, pDy0, pDx0, priTap0, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, aDy0, aDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, bDy0, bDx0, secTap0, secStr, secDampingAdj, center, ref sum, ref max, ref min);

        AccumVec128(currPlane, stride, rowBase, -pDy1, -pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -aDy1, -aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -bDy1, -bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, pDy1, pDx1, priTap1, priStr, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, aDy1, aDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, bDy1, bDx1, secTap1, secStr, secDampingAdj, center, ref sum, ref max, ref min);

        var negMask = Vector128.LessThan(sum, Vector128<int>.Zero);
        var adjusted = sum - (negMask & Vector128.Create(1));
        var rounded = (Vector128.Create(8) + adjusted) >> 4;
        var resultVal = center + rounded;
        var clamped = Vector128.Min(Vector128.Max(resultVal, min), max);
        Av1PixelVector.Store4(clamped, ref cdefPlane[rowBase]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumVec128<TPixel>(TPixel[] plane, int stride, int rowBase, int dy, int dx, int tap, int threshold, int dampingAdj, Vector128<int> center, ref Vector128<int> sum, ref Vector128<int> max, ref Vector128<int> min)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var p = Av1PixelVector.Load4(ref plane[rowBase + (dy * stride) + dx]);
        var diff = p - center;
        var absDiff = Vector128.Abs(diff);
        // constrain(diff, threshold, damping): sign * Clip3(0, absDiff, threshold - (absDiff >> dampingAdj))
        // (spec §7.15.3).
        var constrainedAbs = Vector128.Min(absDiff, Vector128.Max(Vector128<int>.Zero, Vector128.Create(threshold) - (absDiff >> dampingAdj)));
        var negMask = Vector128.LessThan(diff, Vector128<int>.Zero);
        var constrained = Vector128.ConditionalSelect(negMask, -constrainedAbs, constrainedAbs);
        sum += constrained * Vector128.Create(tap);
        max = Vector128.Max(max, p);
        min = Vector128.Min(min, p);
    }

    /// <summary>Reads one CDEF tap and folds it into the running sum/max/min.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accum<TPixel>(TPixel[] plane, int stride, int y, int x, int dy, int dx, int tap, int strength, int dampingAdj, int center, ref long sum, ref int max, ref int min)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        int p = int.CreateTruncating(plane[((y + dy) * stride) + x + dx]);
        sum += tap * ConstrainFast(p - center, strength, dampingAdj);
        if (p > max)
        {
            max = p;
        }

        if (p < min)
        {
            min = p;
        }
    }

    /// <summary>
    /// <c>dampingAdj = max(0, damping - FloorLog2(threshold))</c> (spec's own <c>Constrain</c> process),
    /// hoisted out of the per-tap hot path: <paramref name="threshold"/>/<paramref name="damping"/> are
    /// invariant for an entire <see cref="CdefFilter"/> call (only 2 distinct threshold values -- priStr,
    /// secStr -- across up to 768 taps per 8x8 block), so computing it once per call instead of once per
    /// tap removes ~768 redundant <see cref="FloorLog2"/> bit-loops.
    /// </summary>
    private static int DampingAdjust(int threshold, int damping) =>
        threshold == 0 ? 0 : Math.Max(0, damping - FloorLog2(threshold));

    /// <summary><c>constrain(diff, threshold, damping)</c> (spec §7.15.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ConstrainFast(int diff, int threshold, int dampingAdj)
    {
        if (threshold == 0)
        {
            return 0;
        }

        int absDiff = Math.Abs(diff);
        int sign = diff < 0 ? -1 : 1;
        return sign * Math.Clamp(threshold - (absDiff >> dampingAdj), 0, absDiff);
    }

    /// <summary><c>cdef_get_at</c> (spec §7.15.3), reading from the deblocked <c>CurrFrame</c> (not the being-written <c>CdefFrame</c>).</summary>
    private static int CdefGetAt<TPixel>(State<TPixel> state, int plane, int x0, int y0, int i, int j, int dir, int k, int sign, int subX, int subY)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = state.Result;
        var frame = result.Frame;

        int y = y0 + i + (sign * CdefDirections[dir][k][0]);
        int x = x0 + j + (sign * CdefDirections[dir][k][1]);

        int candidateR = (y << subY) >> 2;
        int candidateC = (x << subX) >> 2;

        if (candidateC >= 0 && candidateC < frame.MiCols && candidateR >= 0 && candidateR < frame.MiRows)
        {
            state.CdefAvailable = true;
            return int.CreateTruncating(result.Planes[plane][(y * result.PlaneWidths[plane]) + x]);
        }

        state.CdefAvailable = false;
        return 0;
    }

    private static int FloorLog2(int x) => x == 0 ? -1 : System.Numerics.BitOperations.Log2((uint)x);
}
