using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Loop restoration (spec §7.17): Wiener and self-guided (SGRPROJ) restoration filters, applied per
/// restoration unit over 64-luma-sample-tall stripes. Reads two frozen inputs -- the deblocked-only
/// samples (<c>UpscaledCurrFrame</c>, snapshotted by the caller before CDEF ran) and the post-CDEF samples
/// (<c>UpscaledCdefFrame</c>, <see cref="Av1FrameDecodeResult{TPixel}.Planes"/> as left by <see cref="Av1Cdef"/>)
/// -- and writes a fresh <c>LrFrame</c> which then replaces <see cref="Av1FrameDecodeResult{TPixel}.Planes"/>, since
/// samples outside the current 64-sample stripe must come from the pre-CDEF version (spec §7.17.1's note on
/// why loop restoration is designed to avoid needing extra CDEF line buffers).
/// </summary>
/// <remarks>
/// The spec describes the process per 4x4 block, recomputing every neighbour sample through the stripe-aware
/// "get source sample" rule. A stripe never straddles a restoration-unit row (unit sizes are multiples of the
/// stripe height once subsampling is accounted for), so the work here is done per (plane, stripe, unit
/// column) region instead: the region's source samples -- with the 3-sample border both filters need -- are
/// gathered once through that rule, and the filters run over the whole region with sliding box sums. The
/// arithmetic per output sample is exactly the spec's, so the output is bit-identical. Regions are
/// independent and run in parallel.
/// </remarks>
internal static class Av1LoopRestoration
{
    private const int FilterBits = 7;
    private const int SgrprojPrjBits = 7;
    private const int SgrprojRstBits = 4;
    private const int SgrprojMtableBits = 20;
    private const int SgrprojRecipBits = 12;
    private const int SgrprojSgrBits = 8;

    /// <summary>Border (in samples) gathered around a region: the Wiener filter's radius, and the self-guided box radius (2) plus the one-sample a/b margin.</summary>
    private const int Border = 3;

    /// <summary>Frames with fewer regions than this are restored on the calling thread.</summary>
    private const int ParallelMinRegions = 4;

    public static void Apply<TPixel>(Av1FrameDecodeResult<TPixel> result, TPixel[][] deblockedPlanes)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var frame = result.Frame;

        if (!frame.LoopRestoration.UsesLr)
        {
            return;
        }

        // Rented (explicit width*height, not Clone()'s implicit "copy the source's whole backing Length",
        // since that source may itself be a larger-than-requested rented array) rather than `new`'d.
        var lrPlanes = new TPixel[3][];
        for (int plane = 0; plane < result.Sequence.NumPlanes; plane++)
        {
            int length = result.PlaneWidths[plane] * result.PlaneHeights[plane];
            lrPlanes[plane] = ArrayPool<TPixel>.Shared.Rent(length);
            Array.Copy(result.Planes[plane], lrPlanes[plane], length);
        }

        var regions = BuildRegions(result);
        var context = new Context<TPixel>(result, deblockedPlanes, lrPlanes);

        if (regions.Count >= ParallelMinRegions && Environment.ProcessorCount > 1)
        {
            Parallel.For(0, regions.Count, () => new Scratch(), (index, _, scratch) =>
            {
                RestoreRegion(context, regions[index], scratch);
                return scratch;
            }, static _ => { });
        }
        else
        {
            var scratch = new Scratch();
            foreach (var region in regions)
            {
                RestoreRegion(context, region, scratch);
            }
        }

        // deblockedPlanes is read only while gathering region samples above -- its life ends here. The array
        // being replaced below (whatever Av1Cdef left behind, or the original tile-decode reconstruction
        // target if CDEF didn't run) is likewise dead once superseded.
        for (int plane = 0; plane < result.Sequence.NumPlanes; plane++)
        {
            ArrayPool<TPixel>.Shared.Return(deblockedPlanes[plane]);
            ArrayPool<TPixel>.Shared.Return(result.Planes[plane]);
            result.Planes[plane] = lrPlanes[plane];
        }
    }

    private sealed class Context<TPixel>(Av1FrameDecodeResult<TPixel> result, TPixel[][] currPlanes, TPixel[][] lrPlanes)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        public Av1FrameDecodeResult<TPixel> Result { get; } = result;

        public TPixel[][] CurrPlanes { get; } = currPlanes;

        public TPixel[][] CdefPlanes { get; } = result.Planes;

        public TPixel[][] LrPlanes { get; } = lrPlanes;
    }

    /// <summary>One stripe of one restoration-unit column of a plane, with inclusive sample bounds.</summary>
    private readonly record struct Region(int Plane, int StripeStartY, int StripeEndY, int X0, int X1, int Y0, int Y1, int UnitIdx, int Type);

    /// <summary>
    /// Splits every restored plane into (stripe x unit column) regions. Unit rows are keyed off the stripe's
    /// first luma row exactly as the spec's per-block rule does ((row*4 + 8) >> subY, divided by the unit size,
    /// clamped to the last row), which is the same for every row of a stripe.
    /// </summary>
    private static List<Region> BuildRegions(Av1FrameDecodeResult result)
    {
        var seq = result.Sequence;
        var frame = result.Frame;
        var regions = new List<Region>();

        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            if (frame.LoopRestoration.FrameRestorationType[plane] == Av1LoopRestorationParams.RestoreNone)
            {
                continue;
            }

            var grid = result.RestorationUnits[plane];
            if (grid is null)
            {
                continue;
            }

            int subX = plane != 0 && seq.SubsamplingX ? 1 : 0;
            int subY = plane != 0 && seq.SubsamplingY ? 1 : 0;
            int planeEndX = Round2(frame.UpscaledWidth, subX) - 1;
            int planeEndY = Round2(frame.FrameHeight, subY) - 1;
            int unitSize = grid.UnitSize;

            for (int stripe = 0; ; stripe++)
            {
                int stripeStartY = (-8 + (stripe * 64)) >> subY;
                int stripeEndY = stripeStartY + (64 >> subY) - 1;
                int y0 = Math.Max(0, stripeStartY);
                if (y0 > planeEndY)
                {
                    break;
                }

                int y1 = Math.Min(planeEndY, stripeEndY);
                int firstLumaRow = Math.Max(0, (stripe * 64) - 8);
                int unitRow = Math.Min(grid.UnitRows - 1, ((firstLumaRow + 8) >> subY) / unitSize);

                for (int unitCol = 0; unitCol < grid.UnitCols; unitCol++)
                {
                    int x0 = unitCol * unitSize;
                    int x1 = unitCol == grid.UnitCols - 1 ? planeEndX : Math.Min(planeEndX, ((unitCol + 1) * unitSize) - 1);
                    if (x0 > planeEndX)
                    {
                        break;
                    }

                    int unitIdx = (unitRow * grid.UnitCols) + unitCol;
                    int type = grid.LrType[unitIdx];
                    if (type == Av1LoopRestorationParams.RestoreWiener || type == Av1LoopRestorationParams.RestoreSgrproj)
                    {
                        regions.Add(new Region(plane, stripeStartY, stripeEndY, x0, x1, y0, y1, unitIdx, type));
                    }
                }
            }
        }

        return regions;
    }

    private static int Round2(int x, int n) => n == 0 ? x : (x + (1 << (n - 1))) >> n;

    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);

    /// <summary>Per-worker scratch, grown on demand and reused across regions.</summary>
    private sealed class Scratch
    {
        public int[] Src = [];
        public int[] Intermediate = [];
        public int[] ColSum = [];
        public long[] ColSq = [];
        public int[] A = [];
        public int[] B = [];
        public int[] Flt0 = [];
        public int[] Flt1 = [];

        public static void Ensure<T>(ref T[] array, int length)
        {
            if (array.Length < length)
            {
                array = new T[length];
            }
        }
    }

    private static void RestoreRegion<TPixel>(Context<TPixel> context, Region region, Scratch scratch)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = context.Result;
        var grid = result.RestorationUnits[region.Plane]!;
        int w = region.X1 - region.X0 + 1;
        int h = region.Y1 - region.Y0 + 1;
        int sw = w + (2 * Border);

        GatherSource(context, region, w, h, sw, scratch);

        if (region.Type == Av1LoopRestorationParams.RestoreWiener)
        {
            WienerFilter(context, grid, region, w, h, sw, scratch);
        }
        else
        {
            SelfGuidedFilter(context, grid, region, w, h, sw, scratch);
        }
    }

    /// <summary>
    /// Fills <see cref="Scratch.Src"/> with the region's samples plus a <see cref="Border"/>-sample margin using
    /// the <c>Get source sample process</c> (spec §7.17.6): coordinates clamp to the plane, rows above the
    /// stripe (at most two) and below it come from the deblocked-only frame, rows inside it from the CDEF output.
    /// </summary>
    private static void GatherSource<TPixel>(Context<TPixel> context, Region region, int w, int h, int sw, Scratch scratch)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = context.Result;
        int plane = region.Plane;
        int stride = result.PlaneWidths[plane];
        var seq = result.Sequence;
        var frame = result.Frame;
        int subX = plane != 0 && seq.SubsamplingX ? 1 : 0;
        int subY = plane != 0 && seq.SubsamplingY ? 1 : 0;
        int planeEndX = Round2(frame.UpscaledWidth, subX) - 1;
        int planeEndY = Round2(frame.FrameHeight, subY) - 1;

        int rows = h + (2 * Border);
        Scratch.Ensure(ref scratch.Src, rows * sw);
        var src = scratch.Src;

        for (int r = 0; r < rows; r++)
        {
            int y = Math.Clamp(region.Y0 - Border + r, 0, planeEndY);
            TPixel[] source;
            if (y < region.StripeStartY)
            {
                y = Math.Max(region.StripeStartY - 2, y);
                source = context.CurrPlanes[plane];
            }
            else if (y > region.StripeEndY)
            {
                y = Math.Min(region.StripeEndY + 2, y);
                source = context.CurrPlanes[plane];
            }
            else
            {
                source = context.CdefPlanes[plane];
            }

            int rowBase = y * stride;
            int dst = r * sw;

            // Columns [X0 - Border, X0) and (X1, X1 + Border] clamp to the plane; the rest is a straight copy.
            int xStart = region.X0 - Border;
            for (int c = 0; c < sw; c++)
            {
                int x = xStart + c;
                if ((uint)x > (uint)planeEndX)
                {
                    x = Math.Clamp(x, 0, planeEndX);
                }

                src[dst + c] = int.CreateTruncating(source[rowBase + x]);
            }
        }
    }

    /// <summary><c>Wiener filter process</c> (spec §7.17.4), with rounding variables from §7.11.3.2 (<c>isCompound = 0</c>), over a whole region.</summary>
    private static void WienerFilter<TPixel>(Context<TPixel> context, Av1RestorationUnitGrid grid, Region region, int w, int h, int sw, Scratch scratch)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = context.Result;
        int bitDepth = result.Sequence.BitDepth;

        int interRound0 = bitDepth == 12 ? 5 : 3;
        int interRound1 = bitDepth == 12 ? 9 : 11;

        Span<int> vfilter = stackalloc int[7];
        Span<int> hfilter = stackalloc int[7];
        WienerCoefficients(grid, region.UnitIdx, 0, vfilter);
        WienerCoefficients(grid, region.UnitIdx, 1, hfilter);

        int offset = 1 << (bitDepth + FilterBits - interRound0 - 1);
        int limit = (1 << (bitDepth + 1 + FilterBits - interRound0)) - 1;
        int lo = -offset;
        int hi = limit - offset;

        int interRows = h + 6;
        Scratch.Ensure(ref scratch.Intermediate, interRows * w);
        var src = scratch.Src;
        var inter = scratch.Intermediate;

        // Horizontal pass: source column c + t of row r is the spec's (x + c + t - 3, y + r - 3).
        int roundH = 1 << (interRound0 - 1);
        for (int r = 0; r < interRows; r++)
        {
            int srcRow = r * sw;
            int dstRow = r * w;
            int c = 0;

            if (Vector256.IsHardwareAccelerated)
            {
                for (; c + 8 <= w; c += 8)
                {
                    var acc = Vector256<int>.Zero;
                    for (int t = 0; t < 7; t++)
                    {
                        acc += Vector256.Create(hfilter[t]) * Vector256.LoadUnsafe(ref src[srcRow + c + t]);
                    }

                    acc = Vector256.ShiftRightArithmetic(acc + Vector256.Create(roundH), interRound0);
                    acc = Vector256.Min(Vector256.Max(acc, Vector256.Create(lo)), Vector256.Create(hi));
                    acc.StoreUnsafe(ref inter[dstRow + c]);
                }
            }

            for (; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 7; t++)
                {
                    s += hfilter[t] * src[srcRow + c + t];
                }

                inter[dstRow + c] = Math.Clamp((s + roundH) >> interRound0, lo, hi);
            }
        }

        var lr = context.LrPlanes[region.Plane];
        int stride = result.PlaneWidths[region.Plane];
        int maxSample = (1 << bitDepth) - 1;
        int roundV = 1 << (interRound1 - 1);

        for (int r = 0; r < h; r++)
        {
            int outRow = ((region.Y0 + r) * stride) + region.X0;
            int c = 0;

            if (Vector256.IsHardwareAccelerated)
            {
                for (; c + 8 <= w; c += 8)
                {
                    var acc = Vector256<int>.Zero;
                    for (int t = 0; t < 7; t++)
                    {
                        acc += Vector256.Create(vfilter[t]) * Vector256.LoadUnsafe(ref inter[((r + t) * w) + c]);
                    }

                    acc = Vector256.ShiftRightArithmetic(acc + Vector256.Create(roundV), interRound1);
                    acc = Vector256.Min(Vector256.Max(acc, Vector256<int>.Zero), Vector256.Create(maxSample));
                    Av1PixelVector.Store8(acc, ref lr[outRow + c]);
                }
            }

            for (; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 7; t++)
                {
                    s += vfilter[t] * inter[((r + t) * w) + c];
                }

                lr[outRow + c] = TPixel.CreateTruncating(Math.Clamp((s + roundV) >> interRound1, 0, maxSample));
            }
        }
    }

    /// <summary><c>Wiener coefficient process</c> (spec §7.17.5).</summary>
    private static void WienerCoefficients(Av1RestorationUnitGrid grid, int unitIdx, int pass, Span<int> filter)
    {
        filter[3] = 128;
        for (int i = 0; i < 3; i++)
        {
            int c = grid.LrWiener[(((unitIdx * 2) + pass) * 3) + i];
            filter[i] = c;
            filter[6 - i] = c;
            filter[3] -= 2 * c;
        }
    }

    /// <summary><c>Self guided filter process</c> (spec §7.17.2), over a whole region.</summary>
    private static void SelfGuidedFilter<TPixel>(Context<TPixel> context, Av1RestorationUnitGrid grid, Region region, int w, int h, int sw, Scratch scratch)
        where TPixel : unmanaged, IBinaryInteger<TPixel>
    {
        var result = context.Result;
        int bitDepth = result.Sequence.BitDepth;
        int unitIdx = region.UnitIdx;

        int set = grid.LrSgrSet[unitIdx];
        int r0 = Av1SgrParams.Table[set][0];
        int r1 = Av1SgrParams.Table[set][2];

        Scratch.Ensure(ref scratch.Flt0, h * w);
        Scratch.Ensure(ref scratch.Flt1, h * w);
        if (r0 != 0)
        {
            BoxFilter(scratch, region, w, h, sw, set, 0, bitDepth, scratch.Flt0);
        }

        if (r1 != 0)
        {
            BoxFilter(scratch, region, w, h, sw, set, 1, bitDepth, scratch.Flt1);
        }

        int w0 = grid.LrSgrXqd[(unitIdx * 2) + 0];
        int w1 = grid.LrSgrXqd[(unitIdx * 2) + 1];
        int w2 = (1 << SgrprojPrjBits) - w0 - w1;

        var lr = context.LrPlanes[region.Plane];
        int stride = result.PlaneWidths[region.Plane];
        int maxSample = (1 << bitDepth) - 1;
        var src = scratch.Src;
        var flt0 = scratch.Flt0;
        var flt1 = scratch.Flt1;

        for (int i = 0; i < h; i++)
        {
            int outRow = ((region.Y0 + i) * stride) + region.X0;
            int srcRow = ((i + Border) * sw) + Border;
            for (int j = 0; j < w; j++)
            {
                long u = (long)src[srcRow + j] << SgrprojRstBits;
                long v = w1 * u;
                v += r0 != 0 ? (long)w0 * flt0[(i * w) + j] : w0 * u;
                v += r1 != 0 ? (long)w2 * flt1[(i * w) + j] : w2 * u;
                int s = Round2(v, SgrprojRstBits + SgrprojPrjBits);
                lr[outRow + j] = TPixel.CreateTruncating(Math.Clamp(s, 0, maxSample));
            }
        }
    }

    /// <summary>
    /// <c>Box filter process</c> (spec §7.17.3) for the whole region. The a/b arrays cover the region plus a
    /// one-sample margin; the (2r+1)^2 window sums come from vertical column sums slid horizontally. Pass 0 only
    /// ever weights odd absolute rows (its 5-6-5 pattern has zero weight on even ones), so even rows' a/b are
    /// skipped. Region <c>Y0</c> is always even in plane coordinates for both passes' parity rules to hold, as in
    /// the spec's per-block form (blocks start on even rows).
    /// </summary>
    private static void BoxFilter(Scratch scratch, Region region, int w, int h, int sw, int set, int pass, int bitDepth, int[] f)
    {
        int r = Av1SgrParams.Table[set][pass * 2];
        int eps = Av1SgrParams.Table[set][(pass * 2) + 1];

        int n = ((2 * r) + 1) * ((2 * r) + 1);
        long n2e = (long)n * n * eps;
        long s = ((1L << SgrprojMtableBits) + (n2e / 2)) / n2e;
        long oneOverN = ((1L << SgrprojRecipBits) + (n / 2)) / n;

        int aw = w + 2;
        int ah = h + 2;
        Scratch.Ensure(ref scratch.A, aw * ah);
        Scratch.Ensure(ref scratch.B, aw * ah);
        Scratch.Ensure(ref scratch.ColSum, sw);
        Scratch.Ensure(ref scratch.ColSq, sw);
        var src = scratch.Src;
        var a = scratch.A;
        var b = scratch.B;
        var colSum = scratch.ColSum;
        var colSq = scratch.ColSq;

        // A source row index of i + Border corresponds to region row i; a/b row i + 1 to region row i.
        for (int i = -1; i < h + 1; i++)
        {
            if (pass == 0 && (((region.Y0 + i) & 1) == 0))
            {
                continue;
            }

            int center = i + Border;
            for (int cc = 0; cc < sw; cc++)
            {
                int sum = 0;
                long sq = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int c = src[((center + dy) * sw) + cc];
                    sum += c;
                    sq += (long)c * c;
                }

                colSum[cc] = sum;
                colSq[cc] = sq;
            }

            int outRow = (i + 1) * aw;
            for (int j = -1; j < w + 1; j++)
            {
                int cj = j + Border;
                long bSum = 0;
                long aSum = 0;
                for (int dx = -r; dx <= r; dx++)
                {
                    bSum += colSum[cj + dx];
                    aSum += colSq[cj + dx];
                }

                long aRounded = Round2(aSum, 2 * (bitDepth - 8));
                long d = Round2(bSum, bitDepth - 8);
                long p = Math.Max(0, (aRounded * n) - (d * d));
                long z = Round2(p * s, SgrprojMtableBits);

                int a2;
                if (z >= 255)
                {
                    a2 = 256;
                }
                else if (z == 0)
                {
                    a2 = 1;
                }
                else
                {
                    a2 = (int)(((z << SgrprojSgrBits) + (z / 2)) / (z + 1));
                }

                long b2 = ((1L << SgrprojSgrBits) - a2) * bSum * oneOverN;

                a[outRow + j + 1] = a2;
                b[outRow + j + 1] = Round2(b2, SgrprojRecipBits);
            }
        }

        for (int i = 0; i < h; i++)
        {
            int absRow = region.Y0 + i;
            int shift = pass == 0 && (absRow & 1) != 0 ? 4 : 5;
            int centerRow = (i + 1) * aw;
            int srcRow = ((i + Border) * sw) + Border;

            for (int j = 0; j < w; j++)
            {
                long aAcc;
                long bAcc;
                int k = centerRow + j + 1;

                if (pass == 0)
                {
                    // Weights are non-zero only for odd absolute rows: the row above and below an even row,
                    // or the row itself for an odd one.
                    if ((absRow & 1) != 0)
                    {
                        aAcc = 6L * a[k];
                        aAcc += 5L * (a[k - 1] + a[k + 1]);
                        bAcc = 6L * b[k];
                        bAcc += 5L * (b[k - 1] + b[k + 1]);
                    }
                    else
                    {
                        int up = k - aw;
                        int down = k + aw;
                        aAcc = 6L * (a[up] + a[down]) + (5L * (a[up - 1] + a[up + 1] + a[down - 1] + a[down + 1]));
                        bAcc = 6L * (b[up] + b[down]) + (5L * (b[up - 1] + b[up + 1] + b[down - 1] + b[down + 1]));
                    }
                }
                else
                {
                    int up = k - aw;
                    int down = k + aw;
                    aAcc = 4L * (a[k] + a[k - 1] + a[k + 1] + a[up] + a[down]) + (3L * (a[up - 1] + a[up + 1] + a[down - 1] + a[down + 1]));
                    bAcc = 4L * (b[k] + b[k - 1] + b[k + 1] + b[up] + b[down]) + (3L * (b[up - 1] + b[up + 1] + b[down - 1] + b[down + 1]));
                }

                long v = (aAcc * src[srcRow + j]) + bAcc;
                f[(i * w) + j] = Round2(v, SgrprojSgrBits + shift - SgrprojRstBits);
            }
        }
    }
}
