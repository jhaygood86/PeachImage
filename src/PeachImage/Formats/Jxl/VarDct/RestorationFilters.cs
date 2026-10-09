using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// The Gabor-like smoothing and edge-preserving filter (EPF) stages of the reference pipeline, as row-parallel, vectorized
/// kernels. Planes are processed in 8-row bands on all cores; within a row, pixels away from the frame edge are computed
/// with <see cref="Vector{T}"/> (which the JIT maps to 256-bit AVX2 or 128-bit NEON registers) and the few pixels near an
/// edge fall back to the scalar mirrored-neighbour code that is also the reference implementation
/// (<see cref="FramePostProcessing.GaborishReference"/>, <see cref="FramePostProcessing.EpfReference"/>). Both paths perform the
/// same operations in the same order, so they produce identical results.
/// </summary>
internal static class RestorationFilters
{
    // Neighbour offsets (dy, dx) whose similarity is measured, per pass.
    private static readonly (int Dy, int Dx)[] Neighbours0 =
    [
        (-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0),
    ];

    private static readonly (int Dy, int Dx)[] Neighbours1 = [(-1, 0), (0, -1), (0, 1), (1, 0)];

    // Distance from the frame edge within which a vector load could leave the plane (largest reach is +-3 pixels).
    private const int Margin = 3;

    /// <summary>
    /// Applies Gaborish (when enabled) and the EPF passes selected by the filter parameters to the three planes. The planes
    /// array's elements may be replaced by other buffers of the same size; callers must use the array, not its old elements. The
    /// planes must have been rented from <see cref="ArrayPool{T}.Shared"/>: buffers are exchanged with (and returned to) that pool.
    /// </summary>
    public static void Apply(float[][] planes, int stride, int width, int height, JxlLoopFilter filter, float[]? sigma, int blocksX, bool vectorized = true)
    {
        bool epf = filter.EpfIterations > 0 && sigma is not null;
        if (!filter.Gab && !epf)
        {
            return;
        }

        var pool = ArrayPool<float>.Shared;
        int length = stride * height;
        var scratch = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            scratch[c] = pool.Rent(length);
        }

        try
        {
            ApplyFilters(planes, scratch, stride, width, height, filter, sigma, blocksX, vectorized, epf);
        }
        finally
        {
            // After the swaps the scratch set holds whichever buffers the result does not live in.
            for (int c = 0; c < 3; c++)
            {
                pool.Return(scratch[c]);
            }
        }
    }

    private static void ApplyFilters(float[][] planes, float[][] scratch, int stride, int width, int height, JxlLoopFilter filter, float[]? sigma, int blocksX, bool vectorized, bool epf)
    {
        if (filter.Gab)
        {
            Gaborish(planes, scratch, stride, width, height, filter, vectorized);
            Swap(planes, scratch);
        }

        if (epf)
        {
            int[] ys = MirrorTable(height);
            int[] xs = MirrorTable(width);
            int iterations = filter.EpfIterations;
            if (iterations >= 3)
            {
                EpfPass(planes, scratch, stride, width, height, filter, sigma!, blocksX, 0, xs, ys, vectorized);
                Swap(planes, scratch);
            }

            EpfPass(planes, scratch, stride, width, height, filter, sigma!, blocksX, 1, xs, ys, vectorized);
            Swap(planes, scratch);

            if (iterations >= 2)
            {
                EpfPass(planes, scratch, stride, width, height, filter, sigma!, blocksX, 2, xs, ys, vectorized);
                Swap(planes, scratch);
            }
        }
    }

    private static void Swap(float[][] a, float[][] b)
    {
        for (int c = 0; c < 3; c++)
        {
            (a[c], b[c]) = (b[c], a[c]);
        }
    }

    // table[i + 4] = mirrored index of i, for i in [-4, size + 4).
    private static int[] MirrorTable(int size)
    {
        var table = new int[size + 8];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = FramePostProcessing.Mirror(i - 4, size);
        }

        return table;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(float[] array, int index) => Vector.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(array), (nuint)index);

    // ---- Gaborish -----------------------------------------------------------------------------------------------------

    private static void Gaborish(float[][] source, float[][] destination, int stride, int width, int height, JxlLoopFilter filter, bool vectorized)
    {
        int bands = (height + 7) / 8;
        for (int c = 0; c < 3; c++)
        {
            float w1 = filter.GabWeights[c * 2];
            float w2 = filter.GabWeights[(c * 2) + 1];
            float norm = 1.0f / (1.0f + (4 * (w1 + w2)));
            float centre = 1.0f * norm;
            w1 *= norm;
            w2 *= norm;
            float[] src = source[c];
            float[] dst = destination[c];
            RowParallel.For(bands, band =>
            {
                int y0 = band * 8;
                int y1 = Math.Min(height, y0 + 8);
                for (int y = y0; y < y1; y++)
                {
                    GaborishRow(src, dst, stride, width, height, y, centre, w1, w2, vectorized);
                }
            });
        }
    }

    private static void GaborishRow(float[] src, float[] dst, int stride, int width, int height, int y, float centre, float w1, float w2, bool vectorized)
    {
        int ym = y * stride;
        int yt = FramePostProcessing.Mirror(y - 1, height) * stride;
        int yb = FramePostProcessing.Mirror(y + 1, height) * stride;
        int x = 0;
        if (vectorized && Vector.IsHardwareAccelerated && width > 2 + Vector<float>.Count)
        {
            // Left edge pixel (mirrored neighbour) in scalar, then vectors over the interior.
            GaborishPixel(src, dst, width, ym, yt, yb, 0, centre, w1, w2);
            int n = Vector<float>.Count;
            var vc = new Vector<float>(centre);
            var vw1 = new Vector<float>(w1);
            var vw2 = new Vector<float>(w2);
            for (x = 1; x + n <= width - 1; x += n)
            {
                var sum1 = Load(src, ym + x - 1) + Load(src, ym + x + 1) + Load(src, yt + x) + Load(src, yb + x);
                var sum2 = Load(src, yt + x - 1) + Load(src, yt + x + 1) + Load(src, yb + x - 1) + Load(src, yb + x + 1);
                var result = (sum2 * vw2) + ((sum1 * vw1) + (Load(src, ym + x) * vc));
                result.CopyTo(dst, ym + x);
            }
        }

        for (; x < width; x++)
        {
            GaborishPixel(src, dst, width, ym, yt, yb, x, centre, w1, w2);
        }
    }

    private static void GaborishPixel(float[] src, float[] dst, int width, int ym, int yt, int yb, int x, float centre, float w1, float w2)
    {
        int xl = FramePostProcessing.Mirror(x - 1, width);
        int xr = FramePostProcessing.Mirror(x + 1, width);
        float sum1 = src[ym + xl] + src[ym + xr] + src[yt + x] + src[yb + x];
        float sum2 = src[yt + xl] + src[yt + xr] + src[yb + xl] + src[yb + xr];
        dst[ym + x] = (sum2 * w2) + ((sum1 * w1) + (src[ym + x] * centre));
    }

    // ---- Edge-preserving filter --------------------------------------------------------------------------------------

    private sealed class RowScratch(int width)
    {
        public float[] InvSigma { get; } = new float[width + 16];

        public float[] Sigma { get; } = new float[width + 16];
    }

    private static void EpfPass(
        float[][] source,
        float[][] destination,
        int stride,
        int width,
        int height,
        JxlLoopFilter filter,
        float[] sigma,
        int blocksX,
        int pass,
        int[] xs,
        int[] ys,
        bool vectorized)
    {
        float baseScale = pass switch
        {
            0 => filter.EpfPass0SigmaScale * 1.65f,
            1 => 1.65f,
            _ => filter.EpfPass2SigmaScale * 1.65f,
        };
        float borderScale = baseScale * filter.EpfBorderSadMul;
        float[] scale = filter.EpfChannelScale;
        int bands = (height + 7) / 8;
        RowParallel.For(
            bands,
            () => new RowScratch(width),
            (band, scratch) =>
            {
                int y0 = band * 8;
                int y1 = Math.Min(height, y0 + 8);
                for (int y = y0; y < y1; y++)
                {
                    FillRowSigma(scratch, sigma, blocksX, width, y, baseScale, borderScale);
                    EpfRow(source, destination, stride, width, height, y, pass, scale, scratch, xs, ys, vectorized);
                }
            });
    }

    private static void FillRowSigma(RowScratch scratch, float[] sigma, int blocksX, int width, int y, float baseScale, float borderScale)
    {
        bool borderRow = (y & 7) == 0 || (y & 7) == 7;
        int blockRow = (y >> 3) * blocksX;
        for (int x = 0; x < width; x++)
        {
            float s = sigma[blockRow + (x >> 3)];
            int ix = x & 7;
            float sadMul = borderRow || ix == 0 || ix == 7 ? borderScale : baseScale;
            scratch.Sigma[x] = s;
            scratch.InvSigma[x] = s * sadMul;
        }
    }

    private static void EpfRow(
        float[][] src,
        float[][] dst,
        int stride,
        int width,
        int height,
        int y,
        int pass,
        float[] scale,
        RowScratch scratch,
        int[] xs,
        int[] ys,
        bool vectorized)
    {
        int xStart = 0;
        int xEnd = width;
        if (vectorized && Vector.IsHardwareAccelerated && y >= Margin && y < height - Margin && width > (2 * Margin) + Vector<float>.Count)
        {
            xStart = Margin;
            int n = Vector<float>.Count;
            int x = xStart;
            for (; x + n <= width - Margin; x += n)
            {
                if (pass == 2)
                {
                    EpfVectorPass2(src, dst, stride, y, x, scale, scratch);
                }
                else
                {
                    EpfVectorSad(src, dst, stride, y, x, pass == 0 ? Neighbours0 : Neighbours1, scale, scratch);
                }
            }

            xEnd = x;

            // Scalar: the left edge, then the tail that does not fill a vector.
            for (int i = 0; i < xStart; i++)
            {
                EpfPixel(src, dst, stride, i, y, pass, scale, scratch, xs, ys);
            }

            for (int i = xEnd; i < width; i++)
            {
                EpfPixel(src, dst, stride, i, y, pass, scale, scratch, xs, ys);
            }

            return;
        }

        for (int x = xStart; x < xEnd; x++)
        {
            EpfPixel(src, dst, stride, x, y, pass, scale, scratch, xs, ys);
        }
    }

    private static readonly (int Dy, int Dx)[] PlusShape = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    // The scalar reference of one pixel (mirrors neighbours at the edges).
    private static void EpfPixel(float[][] src, float[][] dst, int stride, int x, int y, int pass, float[] scale, RowScratch scratch, int[] xs, int[] ys)
    {
        int index = (y * stride) + x;
        if (scratch.Sigma[x] < FramePostProcessing.MinSigmaValue)
        {
            dst[0][index] = src[0][index];
            dst[1][index] = src[1][index];
            dst[2][index] = src[2][index];
            return;
        }

        float invSigma = scratch.InvSigma[x];
        float weightSum = 1f;
        float accX = src[0][index];
        float accY = src[1][index];
        float accB = src[2][index];

        if (pass == 2)
        {
            float cx = accX;
            float cy = accY;
            float cb = accB;
            foreach (var (dy, dx) in Neighbours1)
            {
                int ni = (ys[y + dy + 4] * stride) + xs[x + dx + 4];
                float nx = src[0][ni];
                float ny = src[1][ni];
                float nb = src[2][ni];
                float s = (MathF.Abs(nx - cx) * scale[0]) + (MathF.Abs(ny - cy) * scale[1]) + (MathF.Abs(nb - cb) * scale[2]);
                float weight = MathF.Max(0f, (s * invSigma) + 1f);
                weightSum += weight;
                accX += weight * nx;
                accY += weight * ny;
                accB += weight * nb;
            }
        }
        else
        {
            var neighbours = pass == 0 ? Neighbours0 : Neighbours1;
            for (int n = 0; n < neighbours.Length; n++)
            {
                var (sdy, sdx) = neighbours[n];
                float total = 0;
                for (int c = 0; c < 3; c++)
                {
                    var plane = src[c];
                    float channelSad = 0;
                    foreach (var (ody, odx) in PlusShape)
                    {
                        float a = plane[(ys[y + ody + 4] * stride) + xs[x + odx + 4]];
                        float b = plane[(ys[y + sdy + ody + 4] * stride) + xs[x + sdx + odx + 4]];
                        channelSad += MathF.Abs(a - b);
                    }

                    total += channelSad * scale[c];
                }

                int ni = (ys[y + sdy + 4] * stride) + xs[x + sdx + 4];
                float weight = MathF.Max(0f, (total * invSigma) + 1f);
                weightSum += weight;
                accX += weight * src[0][ni];
                accY += weight * src[1][ni];
                accB += weight * src[2][ni];
            }
        }

        float inv = 1.0f / weightSum;
        dst[0][index] = accX * inv;
        dst[1][index] = accY * inv;
        dst[2][index] = accB * inv;
    }

    // Passes 0 and 1: similarity is the sum of absolute differences over a plus-shaped patch around the pixel and its neighbour.
    private static void EpfVectorSad(float[][] src, float[][] dst, int stride, int y, int x, (int Dy, int Dx)[] neighbours, float[] scale, RowScratch scratch)
    {
        float[] p0 = src[0];
        float[] p1 = src[1];
        float[] p2 = src[2];
        int index = (y * stride) + x;
        var invSigma = Load(scratch.InvSigma, x);
        var keep = Vector.LessThan(Load(scratch.Sigma, x), new Vector<float>(FramePostProcessing.MinSigmaValue));
        var c0 = Load(p0, index);
        var c1 = Load(p1, index);
        var c2 = Load(p2, index);
        var weightSum = Vector<float>.One;
        var acc0 = c0;
        var acc1 = c1;
        var acc2 = c2;
        var s0 = new Vector<float>(scale[0]);
        var s1 = new Vector<float>(scale[1]);
        var s2 = new Vector<float>(scale[2]);
        var zero = Vector<float>.Zero;
        var one = Vector<float>.One;
        foreach (var (dy, dx) in neighbours)
        {
            int n = index + (dy * stride) + dx;
            var sad0 = PlusSad(p0, index, n, stride);
            var sad1 = PlusSad(p1, index, n, stride);
            var sad2 = PlusSad(p2, index, n, stride);
            var total = zero + (sad0 * s0);
            total += sad1 * s1;
            total += sad2 * s2;
            var weight = Vector.Max(zero, (total * invSigma) + one);
            weightSum += weight;
            acc0 += weight * Load(p0, n);
            acc1 += weight * Load(p1, n);
            acc2 += weight * Load(p2, n);
        }

        var inv = one / weightSum;
        Vector.ConditionalSelect(keep, c0, acc0 * inv).CopyTo(dst[0], index);
        Vector.ConditionalSelect(keep, c1, acc1 * inv).CopyTo(dst[1], index);
        Vector.ConditionalSelect(keep, c2, acc2 * inv).CopyTo(dst[2], index);
    }

    // |a - b| summed over the plus shape (centre, up, left, down, right), in the reference's order.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector<float> PlusSad(float[] plane, int a, int b, int stride)
    {
        var sad = Vector<float>.Zero + Vector.Abs(Load(plane, a) - Load(plane, b));
        sad += Vector.Abs(Load(plane, a - stride) - Load(plane, b - stride));
        sad += Vector.Abs(Load(plane, a - 1) - Load(plane, b - 1));
        sad += Vector.Abs(Load(plane, a + stride) - Load(plane, b + stride));
        sad += Vector.Abs(Load(plane, a + 1) - Load(plane, b + 1));
        return sad;
    }

    // Pass 2: similarity is the weighted absolute difference of the pixel and each of its four neighbours.
    private static void EpfVectorPass2(float[][] src, float[][] dst, int stride, int y, int x, float[] scale, RowScratch scratch)
    {
        float[] p0 = src[0];
        float[] p1 = src[1];
        float[] p2 = src[2];
        int index = (y * stride) + x;
        var invSigma = Load(scratch.InvSigma, x);
        var keep = Vector.LessThan(Load(scratch.Sigma, x), new Vector<float>(FramePostProcessing.MinSigmaValue));
        var c0 = Load(p0, index);
        var c1 = Load(p1, index);
        var c2 = Load(p2, index);
        var weightSum = Vector<float>.One;
        var acc0 = c0;
        var acc1 = c1;
        var acc2 = c2;
        var s0 = new Vector<float>(scale[0]);
        var s1 = new Vector<float>(scale[1]);
        var s2 = new Vector<float>(scale[2]);
        var one = Vector<float>.One;
        foreach (var (dy, dx) in Neighbours1)
        {
            int n = index + (dy * stride) + dx;
            var n0 = Load(p0, n);
            var n1 = Load(p1, n);
            var n2 = Load(p2, n);
            var s = (Vector.Abs(n0 - c0) * s0) + (Vector.Abs(n1 - c1) * s1) + (Vector.Abs(n2 - c2) * s2);
            var weight = Vector.Max(Vector<float>.Zero, (s * invSigma) + one);
            weightSum += weight;
            acc0 += weight * n0;
            acc1 += weight * n1;
            acc2 += weight * n2;
        }

        var inv = one / weightSum;
        Vector.ConditionalSelect(keep, c0, acc0 * inv).CopyTo(dst[0], index);
        Vector.ConditionalSelect(keep, c1, acc1 * inv).CopyTo(dst[1], index);
        Vector.ConditionalSelect(keep, c2, acc2 * inv).CopyTo(dst[2], index);
    }
}
