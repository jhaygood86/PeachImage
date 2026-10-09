using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// Fast one-dimensional inverse DCT (O(N log N) per column, N a power of two up to 256) applied to many columns at once.
/// </summary>
/// <remarks>
/// <para>
/// Computes <c>x[n] = c[0] + sqrt(2) * sum_{k&gt;=1} c[k] cos((n + 1/2) k pi / N)</c> -- the format's unnormalised inverse DCT --
/// for each of <c>width</c> independent columns, laid out as rows of <c>width</c> contiguous floats. The recursion splits the
/// output into the DCT-III of the even coefficients (size N/2) and, for the odd coefficients, a size N/2 transform of the
/// pairwise sums <c>c[2m-1] + c[2m+1]</c> weighted by <c>1 / (2 cos((2n + 1) pi / 2N))</c>; the two halves combine as
/// <c>x[n] = e[n] + o[n]</c> and <c>x[N-1-n] = e[n] - o[n]</c>. The even half reads every other source row in place and writes
/// straight into the top half of the destination, so only the odd half needs scratch memory. Every step is a whole-row
/// operation over the <c>width</c> columns, so the inner loops are <see cref="Vector{T}"/> loops (AVX2 on x64, NEON on ARM64) with a
/// scalar tail, run on unchecked references to keep the per-row overhead small.
/// </para>
/// </remarks>
internal static class FastIdct
{
    private const float Sqrt2 = 1.41421356237f;

    // Multipliers[log2 N][n] = 1 / (2 cos((2n + 1) pi / (2N))) for n < N/2.
    private static readonly float[][] Multipliers = BuildMultipliers();

    private static float[][] BuildMultipliers()
    {
        var result = new float[9][];
        for (int log2 = 1; log2 <= 8; log2++)
        {
            int n = 1 << log2;
            result[log2] = new float[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                result[log2][i] = (float)(1.0 / (2.0 * Math.Cos(((2 * i) + 1) * Math.PI / (2 * n))));
            }
        }

        return result;
    }

    /// <summary>The scratch floats <see cref="Transform"/> needs for a transform of <paramref name="size"/> rows of <paramref name="width"/> columns.</summary>
    public static int WorkSize(int size, int width) => 3 * size * width;

    /// <summary>
    /// Writes the inverse DCT of the <paramref name="size"/> coefficient rows in <paramref name="input"/> (contiguous, <paramref name="width"/>
    /// floats per row) to <paramref name="output"/> (rows <paramref name="outputStride"/> floats apart).
    /// </summary>
    public static void Transform(int size, ReadOnlySpan<float> input, Span<float> output, int outputStride, int width, Span<float> work)
    {
        if (input.Length < size * width || work.Length < WorkSize(size, width) || output.Length < (((size - 1) * outputStride) + width))
        {
            throw new ArgumentException("A buffer is too small for the transform.");
        }

        ref float workRef = ref MemoryMarshal.GetReference(work);
        ref float inputRef = ref MemoryMarshal.GetReference(input);

        // The AC coefficients carry a sqrt(2) weight; fold it into a scaled copy so the recursion is a plain DCT-III.
        for (int i = 0; i < width; i++)
        {
            Unsafe.Add(ref workRef, i) = Unsafe.Add(ref inputRef, i);
        }

        ScaleRows(ref Unsafe.Add(ref inputRef, width), ref Unsafe.Add(ref workRef, width), (size - 1) * width, Sqrt2);

        Recurse(size, ref workRef, width, ref MemoryMarshal.GetReference(output), outputStride, width, ref Unsafe.Add(ref workRef, size * width));
    }

    private static void Recurse(int size, ref float source, int sourceStride, ref float destination, int destinationStride, int width, ref float work)
    {
        if (size == 1)
        {
            for (int i = 0; i < width; i++)
            {
                Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
            }

            return;
        }

        int half = size / 2;
        int plane = half * width;
        ref float odd = ref work;
        ref float oddOut = ref Unsafe.Add(ref work, plane);
        ref float sub = ref Unsafe.Add(ref work, 2 * plane);

        // Odd coefficients: b[0] = a[1], b[m] = a[2m-1] + a[2m+1].
        for (int i = 0; i < width; i++)
        {
            Unsafe.Add(ref odd, i) = Unsafe.Add(ref source, (sourceStride) + i);
        }

        for (int m = 1; m < half; m++)
        {
            AddRows(
                ref Unsafe.Add(ref source, ((2 * m) - 1) * sourceStride),
                ref Unsafe.Add(ref source, ((2 * m) + 1) * sourceStride),
                ref Unsafe.Add(ref odd, m * width),
                width);
        }

        Recurse(half, ref odd, width, ref oddOut, width, width, ref sub);

        // Even coefficients, read in place with doubled stride, written into the top half of the destination.
        Recurse(half, ref source, 2 * sourceStride, ref destination, destinationStride, width, ref sub);

        float[] multipliers = Multipliers[BitOperations.Log2((uint)size)];
        for (int n = 0; n < half; n++)
        {
            Combine(
                ref Unsafe.Add(ref destination, n * destinationStride),
                ref Unsafe.Add(ref destination, (size - 1 - n) * destinationStride),
                ref Unsafe.Add(ref oddOut, n * width),
                multipliers[n],
                width);
        }
    }

    private static void ScaleRows(ref float source, ref float destination, int count, float factor)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var f = new Vector<float>(factor);
            for (; i <= count - Vector<float>.Count; i += Vector<float>.Count)
            {
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref source, (nuint)i) * f, ref destination, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i) * factor;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRows(ref float a, ref float b, ref float result, int width)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= width - Vector<float>.Count; i += Vector<float>.Count)
            {
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref a, (nuint)i) + Vector.LoadUnsafe(ref b, (nuint)i), ref result, (nuint)i);
            }
        }

        for (; i < width; i++)
        {
            Unsafe.Add(ref result, i) = Unsafe.Add(ref a, i) + Unsafe.Add(ref b, i);
        }
    }

    // On entry `top` holds e; on exit top = e + m * o and bottom = e - m * o.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Combine(ref float top, ref float bottom, ref float o, float multiplier, int width)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var m = new Vector<float>(multiplier);
            for (; i <= width - Vector<float>.Count; i += Vector<float>.Count)
            {
                var ev = Vector.LoadUnsafe(ref top, (nuint)i);
                var ov = Vector.LoadUnsafe(ref o, (nuint)i) * m;
                Vector.StoreUnsafe(ev + ov, ref top, (nuint)i);
                Vector.StoreUnsafe(ev - ov, ref bottom, (nuint)i);
            }
        }

        for (; i < width; i++)
        {
            float ev = Unsafe.Add(ref top, i);
            float ov = Unsafe.Add(ref o, i) * multiplier;
            Unsafe.Add(ref top, i) = ev + ov;
            Unsafe.Add(ref bottom, i) = ev - ov;
        }
    }
}
