using System.Buffers;
using System.Numerics;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>The size of one resolution level of a tile-component and the parity of its origin.</summary>
internal readonly record struct J2kResolutionSize(int Width, int Height, int OriginX, int OriginY);

/// <summary>
/// Inverse discrete wavelet transforms (T.800 Annex F): the reversible 5/3 on integers and the irreversible 9/7 on floats.
/// Subbands are expected in the usual quadrant layout inside <c>data</c>; lifting runs on contiguous low/high halves, which keeps
/// the inner loops vectorizable, and interleaves afterwards.
/// </summary>
internal static class J2kDwt
{
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;

    private const int StripWidth = 64;

    /// <summary>Reconstructs a tile-component with the reversible transform. <paramref name="levels"/> holds the size of every resolution, lowest first.</summary>
    public static void Inverse53(int[] data, int stride, ReadOnlySpan<J2kResolutionSize> levels)
    {
        for (int r = 1; r < levels.Length; r++)
        {
            var res = levels[r];
            var previous = levels[r - 1];
            int width = res.Width;
            int height = res.Height;
            if (width == 0 || height == 0)
            {
                continue;
            }

            int lowWidth = previous.Width;
            int lowHeight = previous.Height;
            int casX = res.OriginX & 1;
            int casY = res.OriginY & 1;

            Parallel53Rows(data, stride, width, height, lowWidth, casX);
            Parallel53Columns(data, stride, width, height, lowHeight, casY);
        }
    }

    /// <summary>Reconstructs a tile-component with the irreversible transform.</summary>
    public static void Inverse97(float[] data, int stride, ReadOnlySpan<J2kResolutionSize> levels)
    {
        for (int r = 1; r < levels.Length; r++)
        {
            var res = levels[r];
            var previous = levels[r - 1];
            int width = res.Width;
            int height = res.Height;
            if (width == 0 || height == 0)
            {
                continue;
            }

            int lowWidth = previous.Width;
            int lowHeight = previous.Height;
            int casX = res.OriginX & 1;
            int casY = res.OriginY & 1;

            Parallel97Rows(data, stride, width, height, lowWidth, casX);
            Parallel97Columns(data, stride, width, height, lowHeight, casY);
        }
    }

    // ------------------------------------------------------------------ 5/3

    private static void Parallel53Rows(int[] data, int stride, int width, int height, int lowCount, int cas)
    {
        void Body(int from, int to)
        {
            int[] temp = ArrayPool<int>.Shared.Rent(width);
            try
            {
                for (int y = from; y < to; y++)
                {
                    var row = data.AsSpan(y * stride, width);
                    Lift53(row[..lowCount], row[lowCount..], cas);
                    Interleave(row, temp.AsSpan(0, width), lowCount, cas);
                    temp.AsSpan(0, width).CopyTo(row);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(temp);
            }
        }

        RunRanges(height, 16, 64, Body);
    }

    private static void Parallel53Columns(int[] data, int stride, int width, int height, int lowCount, int cas)
    {
        int strips = (width + StripWidth - 1) / StripWidth;
        void Body(int from, int to)
        {
            int[] temp = ArrayPool<int>.Shared.Rent(height * StripWidth);
            try
            {
                for (int s = from; s < to; s++)
                {
                    int x0 = s * StripWidth;
                    int count = Math.Min(StripWidth, width - x0);
                    LiftColumns53(data, stride, x0, count, height, lowCount, cas);
                    InterleaveRows(data, stride, x0, count, height, lowCount, cas, temp);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(temp);
            }
        }

        RunRanges(strips, 1, 2, Body);
    }

    private static void Lift53(Span<int> low, Span<int> high, int cas)
    {
        int nl = low.Length;
        int nh = high.Length;
        if (nl + nh == 1)
        {
            if (cas == 1)
            {
                high[0] /= 2;
            }

            return;
        }

        // Update lows from neighbouring highs, then highs from the updated lows.
        LiftLow53(low, high, cas == 0 ? -1 : 0);
        LiftHigh53(high, low, cas == 0 ? 0 : -1);
    }

    private static void LiftLow53(Span<int> target, ReadOnlySpan<int> source, int shift)
    {
        int n = target.Length;
        int sourceCount = source.Length;
        int lo = Math.Max(0, -shift);
        int hi = Math.Min(n - 1, sourceCount - 2 - shift);
        for (int i = 0; i < Math.Min(lo, n); i++)
        {
            target[i] -= (source[Clamp(i + shift, sourceCount)] + source[Clamp(i + shift + 1, sourceCount)] + 2) >> 2;
        }

        int next = lo;
        int lanes = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            var two = new Vector<int>(2);
            for (; next + lanes - 1 <= hi; next += lanes)
            {
                var a = new Vector<int>(source.Slice(next + shift));
                var b = new Vector<int>(source.Slice(next + shift + 1));
                var t = new Vector<int>(target.Slice(next));
                (t - Vector.ShiftRightArithmetic(a + b + two, 2)).CopyTo(target.Slice(next));
            }
        }

        for (; next < n; next++)
        {
            target[next] -= (source[Clamp(next + shift, sourceCount)] + source[Clamp(next + shift + 1, sourceCount)] + 2) >> 2;
        }
    }

    private static void LiftHigh53(Span<int> target, ReadOnlySpan<int> source, int shift)
    {
        int n = target.Length;
        int sourceCount = source.Length;
        int lo = Math.Max(0, -shift);
        int hi = Math.Min(n - 1, sourceCount - 2 - shift);
        for (int i = 0; i < Math.Min(lo, n); i++)
        {
            target[i] += (source[Clamp(i + shift, sourceCount)] + source[Clamp(i + shift + 1, sourceCount)]) >> 1;
        }

        int next = lo;
        int lanes = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; next + lanes - 1 <= hi; next += lanes)
            {
                var a = new Vector<int>(source.Slice(next + shift));
                var b = new Vector<int>(source.Slice(next + shift + 1));
                var t = new Vector<int>(target.Slice(next));
                (t + Vector.ShiftRightArithmetic(a + b, 1)).CopyTo(target.Slice(next));
            }
        }

        for (; next < n; next++)
        {
            target[next] += (source[Clamp(next + shift, sourceCount)] + source[Clamp(next + shift + 1, sourceCount)]) >> 1;
        }
    }

    private static void LiftColumns53(int[] data, int stride, int x0, int count, int height, int lowRows, int cas)
    {
        int highRows = height - lowRows;
        if (height == 1)
        {
            if (cas == 1)
            {
                var only = data.AsSpan(x0, count);
                for (int x = 0; x < count; x++)
                {
                    only[x] /= 2;
                }
            }

            return;
        }

        int lowShift = cas == 0 ? -1 : 0;
        int highShift = cas == 0 ? 0 : -1;
        for (int i = 0; i < lowRows; i++)
        {
            var t = data.AsSpan((i * stride) + x0, count);
            var a = data.AsSpan(((lowRows + Clamp(i + lowShift, highRows)) * stride) + x0, count);
            var b = data.AsSpan(((lowRows + Clamp(i + lowShift + 1, highRows)) * stride) + x0, count);
            RowOp53(t, a, b, low: true);
        }

        for (int i = 0; i < highRows; i++)
        {
            var t = data.AsSpan(((lowRows + i) * stride) + x0, count);
            var a = data.AsSpan((Clamp(i + highShift, lowRows) * stride) + x0, count);
            var b = data.AsSpan((Clamp(i + highShift + 1, lowRows) * stride) + x0, count);
            RowOp53(t, a, b, low: false);
        }
    }

    private static void RowOp53(Span<int> target, ReadOnlySpan<int> a, ReadOnlySpan<int> b, bool low)
    {
        int i = 0;
        int lanes = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            var two = new Vector<int>(2);
            for (; i + lanes <= target.Length; i += lanes)
            {
                var va = new Vector<int>(a.Slice(i));
                var vb = new Vector<int>(b.Slice(i));
                var vt = new Vector<int>(target.Slice(i));
                var result = low ? vt - Vector.ShiftRightArithmetic(va + vb + two, 2) : vt + Vector.ShiftRightArithmetic(va + vb, 1);
                result.CopyTo(target.Slice(i));
            }
        }

        for (; i < target.Length; i++)
        {
            target[i] = low ? target[i] - ((a[i] + b[i] + 2) >> 2) : target[i] + ((a[i] + b[i]) >> 1);
        }
    }

    // ------------------------------------------------------------------ 9/7

    private static void Parallel97Rows(float[] data, int stride, int width, int height, int lowCount, int cas)
    {
        void Body(int from, int to)
        {
            float[] temp = ArrayPool<float>.Shared.Rent(width);
            try
            {
                for (int y = from; y < to; y++)
                {
                    var row = data.AsSpan(y * stride, width);
                    Lift97(row[..lowCount], row[lowCount..], cas);
                    Interleave(row, temp.AsSpan(0, width), lowCount, cas);
                    temp.AsSpan(0, width).CopyTo(row);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(temp);
            }
        }

        RunRanges(height, 16, 64, Body);
    }

    private static void Parallel97Columns(float[] data, int stride, int width, int height, int lowCount, int cas)
    {
        int strips = (width + StripWidth - 1) / StripWidth;
        void Body(int from, int to)
        {
            float[] temp = ArrayPool<float>.Shared.Rent(height * StripWidth);
            try
            {
                for (int s = from; s < to; s++)
                {
                    int x0 = s * StripWidth;
                    int count = Math.Min(StripWidth, width - x0);
                    LiftColumns97(data, stride, x0, count, height, lowCount, cas);
                    InterleaveRows(data, stride, x0, count, height, lowCount, cas, temp);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(temp);
            }
        }

        RunRanges(strips, 1, 2, Body);
    }

    private static void Lift97(Span<float> low, Span<float> high, int cas)
    {
        int nl = low.Length;
        int nh = high.Length;
        if (nl + nh == 1)
        {
            if (cas == 1)
            {
                high[0] *= 0.5f;
            }

            return;
        }

        Scale(low, K);
        Scale(high, 1f / K);
        int lowShift = cas == 0 ? -1 : 0;
        int highShift = cas == 0 ? 0 : -1;
        Lift97Step(low, high, lowShift, -Delta);
        Lift97Step(high, low, highShift, -Gamma);
        Lift97Step(low, high, lowShift, -Beta);
        Lift97Step(high, low, highShift, -Alpha);
    }

    private static void Scale(Span<float> values, float factor)
    {
        int i = 0;
        int lanes = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + lanes <= values.Length; i += lanes)
            {
                (new Vector<float>(values.Slice(i)) * factor).CopyTo(values.Slice(i));
            }
        }

        for (; i < values.Length; i++)
        {
            values[i] *= factor;
        }
    }

    private static void Lift97Step(Span<float> target, ReadOnlySpan<float> source, int shift, float coefficient)
    {
        int n = target.Length;
        int sourceCount = source.Length;
        int lo = Math.Max(0, -shift);
        int hi = Math.Min(n - 1, sourceCount - 2 - shift);
        for (int i = 0; i < Math.Min(lo, n); i++)
        {
            target[i] += coefficient * (source[Clamp(i + shift, sourceCount)] + source[Clamp(i + shift + 1, sourceCount)]);
        }

        int next = lo;
        int lanes = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; next + lanes - 1 <= hi; next += lanes)
            {
                var a = new Vector<float>(source.Slice(next + shift));
                var b = new Vector<float>(source.Slice(next + shift + 1));
                var t = new Vector<float>(target.Slice(next));
                (t + ((a + b) * coefficient)).CopyTo(target.Slice(next));
            }
        }

        for (; next < n; next++)
        {
            target[next] += coefficient * (source[Clamp(next + shift, sourceCount)] + source[Clamp(next + shift + 1, sourceCount)]);
        }
    }

    private static void LiftColumns97(float[] data, int stride, int x0, int count, int height, int lowRows, int cas)
    {
        int highRows = height - lowRows;
        if (height == 1)
        {
            if (cas == 1)
            {
                var only = data.AsSpan(x0, count);
                Scale(only, 0.5f);
            }

            return;
        }

        for (int i = 0; i < lowRows; i++)
        {
            Scale(data.AsSpan((i * stride) + x0, count), K);
        }

        for (int i = 0; i < highRows; i++)
        {
            Scale(data.AsSpan(((lowRows + i) * stride) + x0, count), 1f / K);
        }

        int lowShift = cas == 0 ? -1 : 0;
        int highShift = cas == 0 ? 0 : -1;
        ColumnStep97(data, stride, x0, count, 0, lowRows, lowRows, highRows, lowShift, -Delta);
        ColumnStep97(data, stride, x0, count, lowRows, highRows, 0, lowRows, highShift, -Gamma);
        ColumnStep97(data, stride, x0, count, 0, lowRows, lowRows, highRows, lowShift, -Beta);
        ColumnStep97(data, stride, x0, count, lowRows, highRows, 0, lowRows, highShift, -Alpha);
    }

    private static void ColumnStep97(float[] data, int stride, int x0, int count, int targetStart, int targetRows, int sourceStart, int sourceRows, int shift, float coefficient)
    {
        for (int i = 0; i < targetRows; i++)
        {
            var t = data.AsSpan(((targetStart + i) * stride) + x0, count);
            var a = data.AsSpan(((sourceStart + Clamp(i + shift, sourceRows)) * stride) + x0, count);
            var b = data.AsSpan(((sourceStart + Clamp(i + shift + 1, sourceRows)) * stride) + x0, count);
            int j = 0;
            int lanes = Vector<float>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                for (; j + lanes <= count; j += lanes)
                {
                    var va = new Vector<float>(a.Slice(j));
                    var vb = new Vector<float>(b.Slice(j));
                    var vt = new Vector<float>(t.Slice(j));
                    (vt + ((va + vb) * coefficient)).CopyTo(t.Slice(j));
                }
            }

            for (; j < count; j++)
            {
                t[j] += coefficient * (a[j] + b[j]);
            }
        }
    }

    // ------------------------------------------------------------------ shared

    private static int Clamp(int index, int count) => index < 0 ? 0 : index >= count ? count - 1 : index;

    private static void Interleave<T>(ReadOnlySpan<T> source, Span<T> destination, int lowCount, int cas)
    {
        int highCount = source.Length - lowCount;
        int lowOffset = cas == 0 ? 0 : 1;
        int highOffset = cas == 0 ? 1 : 0;
        for (int i = 0; i < lowCount; i++)
        {
            destination[(2 * i) + lowOffset] = source[i];
        }

        for (int i = 0; i < highCount; i++)
        {
            destination[(2 * i) + highOffset] = source[lowCount + i];
        }
    }

    private static void InterleaveRows<T>(T[] data, int stride, int x0, int count, int height, int lowRows, int cas, T[] temp)
    {
        int highRows = height - lowRows;
        int lowOffset = cas == 0 ? 0 : 1;
        int highOffset = cas == 0 ? 1 : 0;
        for (int i = 0; i < lowRows; i++)
        {
            data.AsSpan((i * stride) + x0, count).CopyTo(temp.AsSpan(((2 * i) + lowOffset) * count, count));
        }

        for (int i = 0; i < highRows; i++)
        {
            data.AsSpan(((lowRows + i) * stride) + x0, count).CopyTo(temp.AsSpan(((2 * i) + highOffset) * count, count));
        }

        for (int y = 0; y < height; y++)
        {
            temp.AsSpan(y * count, count).CopyTo(data.AsSpan((y * stride) + x0, count));
        }
    }

    private static void RunRanges(int total, int chunk, int minimumForParallel, Action<int, int> body)
    {
        int chunks = (total + chunk - 1) / chunk;
        if (chunks <= 1 || total < minimumForParallel)
        {
            body(0, total);
            return;
        }

        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
            body(c * chunk, Math.Min(total, (c + 1) * chunk)));
    }
}
