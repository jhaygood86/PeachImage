using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// CDEF (constrained directional enhancement filter, spec §7.15): direction search per 8x8 luma block
/// followed by primary/secondary directional filtering. Reads from <see cref="Av1FrameDecodeResult.Planes"/>
/// (the deblocked <c>CurrFrame</c>) and writes into freshly-allocated <c>CdefFrame</c> buffers (a copy of
/// the input, selectively overwritten), which then replace <see cref="Av1FrameDecodeResult.Planes"/> --
/// matching the spec's own read-from-CurrFrame / write-to-CdefFrame separation, which exists precisely so
/// that filtering one 8x8 block never sees another 8x8 block's already-filtered output.
/// </summary>
internal static class Av1Cdef
{
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

    public static void Apply(Av1FrameDecodeResult result)
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
        var cdefPlanes = new int[3][];
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            int length = result.PlaneWidths[plane] * result.PlaneHeights[plane];
            cdefPlanes[plane] = ArrayPool<int>.Shared.Rent(length);
            Array.Clear(cdefPlanes[plane], 0, length);
        }

        var state = new State(result, cdefPlanes);

        int step4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block8x8];
        int cdefSize4 = Av1BlockTables.Num4x4BlocksWide[Av1BlockSize.Block64x64];
        int cdefMask4 = ~(cdefSize4 - 1);

        for (int r = 0; r < frame.MiRows; r += step4)
        {
            for (int c = 0; c < frame.MiCols; c += step4)
            {
                int baseR = r & cdefMask4;
                int baseC = c & cdefMask4;
                int idx = result.CdefIdx[(baseR * frame.MiCols) + baseC];
                CdefBlock(state, r, c, idx);
            }
        }

        // The array being replaced here (whatever Av1FrameDecoder rented as the tile-decode reconstruction
        // target -- deblocking filters in place, so it's still the same object) has now been fully
        // superseded and is safe to return: cdefPlanes was seeded from it (see the CopyBlock note above)
        // and nothing else in the pipeline retains a separate reference to it.
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            ArrayPool<int>.Shared.Return(result.Planes[plane]);
            result.Planes[plane] = cdefPlanes[plane];
        }
    }

    private sealed class State(Av1FrameDecodeResult result, int[][] cdefPlanes)
    {
        public Av1FrameDecodeResult Result { get; } = result;

        public int[][] CdefPlanes { get; } = cdefPlanes;

        public bool CdefAvailable { get; set; }

        // Reusable CDEF-direction-search scratch (spec's cost[8]/partial[8][15]), cleared and reused for
        // every 8x8 block in the frame instead of allocating fresh each call -- this is called once per
        // non-skip block (tens of thousands per 1080p frame), so freshly allocating 10 small arrays per
        // call was a real, measured allocation hot spot.
        public readonly long[] Cost = new long[8];
        public readonly long[][] Partial = CreatePartial();

        private static long[][] CreatePartial()
        {
            var partial = new long[8][];
            for (int i = 0; i < 8; i++)
            {
                partial[i] = new long[15];
            }

            return partial;
        }
    }

    /// <summary><c>CDEF block process</c> (spec §7.15.1).</summary>
    private static void CdefBlock(State state, int r, int c, int idx)
    {
        var result = state.Result;
        var seq = result.Sequence;
        var frame = result.Frame;

        int startY = r * 4;
        int endY = startY + 8;
        int startX = c * 4;
        int endX = startX + 8;

        CopyBlock(result.Planes[0], state.CdefPlanes[0], result.PlaneWidths[0], startX, startY, endX, endY);

        if (seq.NumPlanes > 1)
        {
            int subX = seq.SubsamplingX ? 1 : 0;
            int subY = seq.SubsamplingY ? 1 : 0;
            CopyBlock(result.Planes[1], state.CdefPlanes[1], result.PlaneWidths[1], startX >> subX, startY >> subY, endX >> subX, endY >> subY);
            CopyBlock(result.Planes[2], state.CdefPlanes[2], result.PlaneWidths[2], startX >> subX, startY >> subY, endX >> subX, endY >> subY);
        }

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

        var (yDir, var) = CdefDirection(state, r, c);

        int priStr = frame.Cdef.YPriStrength[idx] << coeffShift;
        int secStr = frame.Cdef.YSecStrength[idx] << coeffShift;
        int dir = priStr == 0 ? 0 : yDir;
        int varStr = (var >> 6) != 0 ? Math.Min(FloorLog2(var >> 6), 12) : 0;
        priStr = var != 0 ? (((priStr * (4 + varStr)) + 8) >> 4) : 0;
        int damping = frame.Cdef.Damping + coeffShift;

        CdefFilter(state, 0, r, c, priStr, secStr, damping, dir);

        if (seq.NumPlanes == 1)
        {
            return;
        }

        priStr = frame.Cdef.UvPriStrength[idx] << coeffShift;
        secStr = frame.Cdef.UvSecStrength[idx] << coeffShift;
        dir = priStr == 0 ? 0 : CdefUvDir[seq.SubsamplingX ? 1 : 0][seq.SubsamplingY ? 1 : 0][yDir];
        damping = frame.Cdef.Damping + coeffShift - 1;

        CdefFilter(state, 1, r, c, priStr, secStr, damping, dir);
        CdefFilter(state, 2, r, c, priStr, secStr, damping, dir);
    }

    private static void CopyBlock(int[] src, int[] dst, int stride, int startX, int startY, int endX, int endY)
    {
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int idx = (y * stride) + x;
                dst[idx] = src[idx];
            }
        }
    }

    /// <summary><c>CDEF direction process</c> (spec §7.15.2).</summary>
    private static (int YDir, int Var) CdefDirection(State state, int r, int c)
    {
        var result = state.Result;
        var cost = state.Cost;
        var partial = state.Partial;
        Array.Clear(cost);
        for (int i = 0; i < 8; i++)
        {
            Array.Clear(partial[i]);
        }

        long bestCost = 0;
        int yDir = 0;

        var luma = result.Planes[0];
        int stride = result.PlaneWidths[0];
        int bitDepth = result.Sequence.BitDepth;

        int x0 = c * 4;
        int y0 = r * 4;

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                int x = (luma[((y0 + i) * stride) + x0 + j] >> (bitDepth - 8)) - 128;
                partial[0][i + j] += x;
                partial[1][i + (j / 2)] += x;
                partial[2][i] += x;
                partial[3][3 + i - (j / 2)] += x;
                partial[4][7 + i - j] += x;
                partial[5][3 - (i / 2) + j] += x;
                partial[6][j] += x;
                partial[7][(i / 2) + j] += x;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            cost[2] += partial[2][i] * partial[2][i];
            cost[6] += partial[6][i] * partial[6][i];
        }

        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];

        for (int i = 0; i < 7; i++)
        {
            cost[0] += ((partial[0][i] * partial[0][i]) + (partial[0][14 - i] * partial[0][14 - i])) * DivTable[i + 1];
            cost[4] += ((partial[4][i] * partial[4][i]) + (partial[4][14 - i] * partial[4][14 - i])) * DivTable[i + 1];
        }

        cost[0] += partial[0][7] * partial[0][7] * DivTable[8];
        cost[4] += partial[4][7] * partial[4][7] * DivTable[8];

        for (int i = 1; i < 8; i += 2)
        {
            for (int j = 0; j < 5; j++)
            {
                cost[i] += partial[i][3 + j] * partial[i][3 + j];
            }

            cost[i] *= DivTable[8];

            for (int j = 0; j < 3; j++)
            {
                cost[i] += ((partial[i][j] * partial[i][j]) + (partial[i][10 - j] * partial[i][10 - j])) * DivTable[(2 * j) + 2];
            }
        }

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
    private static void CdefFilter(State state, int plane, int r, int c, int priStr, int secStr, int damping, int dir)
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
                CdefFilterRow256(currPlane, cdefPlane, stride, x0, y0 + i, priTap0, priTap1, secTap0, secTap1, priDampingAdj, secDampingAdj, pDy0, pDx0, pDy1, pDx1, aDy0, aDx0, aDy1, aDx1, bDy0, bDx0, bDy1, bDx1);
            }

            return;
        }

        if (interior && w == 4 && Vector128.IsHardwareAccelerated)
        {
            for (int i = 0; i < h; i++)
            {
                CdefFilterRow128(currPlane, cdefPlane, stride, x0, y0 + i, priTap0, priTap1, secTap0, secTap1, priDampingAdj, secDampingAdj, pDy0, pDx0, pDy1, pDx1, aDy0, aDx0, aDy1, aDx1, bDy0, bDx0, bDy1, bDx1);
            }

            return;
        }

        for (int i = 0; i < h; i++)
        {
            int y = y0 + i;
            for (int j = 0; j < w; j++)
            {
                int x = x0 + j;
                int center = currPlane[(y * stride) + x];
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
                cdefPlane[(y * stride) + x] = Math.Clamp(result8, min, max);
            }
        }
    }

    /// <summary>
    /// Vectorized equivalent of <see cref="CdefFilter"/>'s interior per-pixel body, one row (8 luma
    /// samples) at a time. <c>threshold</c> never needs to be passed in separately: each dampingAdj
    /// argument is precomputed as exactly 0 whenever its corresponding threshold (priStr/secStr) is 0,
    /// and <c>absDiff - (absDiff &gt;&gt; 0) == 0</c> reproduces <see cref="ConstrainFast"/>'s explicit
    /// zero short-circuit without needing a branch here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CdefFilterRow256(
        int[] currPlane, int[] cdefPlane, int stride, int x0, int y,
        int priTap0, int priTap1, int secTap0, int secTap1, int priDampingAdj, int secDampingAdj,
        int pDy0, int pDx0, int pDy1, int pDx1, int aDy0, int aDx0, int aDy1, int aDx1, int bDy0, int bDx0, int bDy1, int bDx1)
    {
        int rowBase = (y * stride) + x0;
        var center = Vector256.LoadUnsafe(ref currPlane[rowBase]);
        var sum = Vector256<int>.Zero;
        var max = center;
        var min = center;

        AccumVec256(currPlane, stride, rowBase, -pDy0, -pDx0, priTap0, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -aDy0, -aDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -bDy0, -bDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, pDy0, pDx0, priTap0, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, aDy0, aDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, bDy0, bDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);

        AccumVec256(currPlane, stride, rowBase, -pDy1, -pDx1, priTap1, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -aDy1, -aDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, -bDy1, -bDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, pDy1, pDx1, priTap1, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, aDy1, aDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec256(currPlane, stride, rowBase, bDy1, bDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);

        var negMask = Vector256.LessThan(sum, Vector256<int>.Zero);
        var adjusted = sum - (negMask & Vector256.Create(1));
        var rounded = (Vector256.Create(8) + adjusted) >> 4;
        var resultVal = center + rounded;
        var clamped = Vector256.Min(Vector256.Max(resultVal, min), max);
        clamped.StoreUnsafe(ref cdefPlane[rowBase]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumVec256(int[] plane, int stride, int rowBase, int dy, int dx, int tap, int dampingAdj, Vector256<int> center, ref Vector256<int> sum, ref Vector256<int> max, ref Vector256<int> min)
    {
        var p = Vector256.LoadUnsafe(ref plane[rowBase + (dy * stride) + dx]);
        var diff = p - center;
        var absDiff = Vector256.Abs(diff);
        var constrainedAbs = absDiff - (absDiff >> dampingAdj);
        var negMask = Vector256.LessThan(diff, Vector256<int>.Zero);
        var constrained = Vector256.ConditionalSelect(negMask, -constrainedAbs, constrainedAbs);
        sum += constrained * Vector256.Create(tap);
        max = Vector256.Max(max, p);
        min = Vector256.Min(min, p);
    }

    /// <summary>Same as <see cref="CdefFilterRow256"/>, for the 4-wide (subsampled chroma) block width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CdefFilterRow128(
        int[] currPlane, int[] cdefPlane, int stride, int x0, int y,
        int priTap0, int priTap1, int secTap0, int secTap1, int priDampingAdj, int secDampingAdj,
        int pDy0, int pDx0, int pDy1, int pDx1, int aDy0, int aDx0, int aDy1, int aDx1, int bDy0, int bDx0, int bDy1, int bDx1)
    {
        int rowBase = (y * stride) + x0;
        var center = Vector128.LoadUnsafe(ref currPlane[rowBase]);
        var sum = Vector128<int>.Zero;
        var max = center;
        var min = center;

        AccumVec128(currPlane, stride, rowBase, -pDy0, -pDx0, priTap0, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -aDy0, -aDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -bDy0, -bDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, pDy0, pDx0, priTap0, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, aDy0, aDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, bDy0, bDx0, secTap0, secDampingAdj, center, ref sum, ref max, ref min);

        AccumVec128(currPlane, stride, rowBase, -pDy1, -pDx1, priTap1, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -aDy1, -aDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, -bDy1, -bDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, pDy1, pDx1, priTap1, priDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, aDy1, aDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);
        AccumVec128(currPlane, stride, rowBase, bDy1, bDx1, secTap1, secDampingAdj, center, ref sum, ref max, ref min);

        var negMask = Vector128.LessThan(sum, Vector128<int>.Zero);
        var adjusted = sum - (negMask & Vector128.Create(1));
        var rounded = (Vector128.Create(8) + adjusted) >> 4;
        var resultVal = center + rounded;
        var clamped = Vector128.Min(Vector128.Max(resultVal, min), max);
        clamped.StoreUnsafe(ref cdefPlane[rowBase]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumVec128(int[] plane, int stride, int rowBase, int dy, int dx, int tap, int dampingAdj, Vector128<int> center, ref Vector128<int> sum, ref Vector128<int> max, ref Vector128<int> min)
    {
        var p = Vector128.LoadUnsafe(ref plane[rowBase + (dy * stride) + dx]);
        var diff = p - center;
        var absDiff = Vector128.Abs(diff);
        var constrainedAbs = absDiff - (absDiff >> dampingAdj);
        var negMask = Vector128.LessThan(diff, Vector128<int>.Zero);
        var constrained = Vector128.ConditionalSelect(negMask, -constrainedAbs, constrainedAbs);
        sum += constrained * Vector128.Create(tap);
        max = Vector128.Max(max, p);
        min = Vector128.Min(min, p);
    }

    /// <summary>Reads one CDEF tap and folds it into the running sum/max/min.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accum(int[] plane, int stride, int y, int x, int dy, int dx, int tap, int strength, int dampingAdj, int center, ref long sum, ref int max, ref int min)
    {
        int p = plane[((y + dy) * stride) + x + dx];
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ConstrainFast(int diff, int threshold, int dampingAdj)
    {
        if (threshold == 0)
        {
            return 0;
        }

        int absDiff = Math.Abs(diff);
        int sign = diff < 0 ? -1 : 1;
        return sign * Math.Clamp(absDiff - (absDiff >> dampingAdj), 0, absDiff);
    }

    /// <summary><c>cdef_get_at</c> (spec §7.15.3), reading from the deblocked <c>CurrFrame</c> (not the being-written <c>CdefFrame</c>).</summary>
    private static int CdefGetAt(State state, int plane, int x0, int y0, int i, int j, int dir, int k, int sign, int subX, int subY)
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
            return result.Planes[plane][(y * result.PlaneWidths[plane]) + x];
        }

        state.CdefAvailable = false;
        return 0;
    }

    private static int FloorLog2(int x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }

        return s - 1;
    }
}
