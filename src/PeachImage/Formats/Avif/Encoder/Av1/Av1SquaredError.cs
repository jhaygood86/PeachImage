using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>Sum of squared differences between two sample planes, shared by the encoder's CDEF and deblocking-level searches.</summary>
internal static class Av1SquaredError
{
    /// <summary>
    /// Returns the sum of <c>(a[i] - b[i])^2</c> over every sample of <paramref name="a"/>, or 0 when either plane
    /// is <see langword="null"/> (a monochrome frame's absent chroma).
    /// </summary>
    public static long Compute(int[]? a, int[]? b) => Compute(a, b, a?.Length ?? 0);

    /// <summary>
    /// As <see cref="Compute(int[], int[])"/>, over only the first <paramref name="length"/> samples.
    /// </summary>
    /// <remarks>
    /// AV1 samples are at most 12 bits, so a difference is at most 4095 in magnitude and its square fits a 32-bit
    /// lane with room to spare (an unsigned square stays exact for any difference up to 65535). The vector path
    /// squares in 32-bit lanes and widens to 64-bit before accumulating, so the total is exact however long the
    /// planes are, and equal to <see cref="Scalar"/>.
    /// </remarks>
    public static long Compute(int[]? a, int[]? b, int length)
    {
        if (a is null || b is null)
        {
            return 0;
        }

        if (Vector128.IsHardwareAccelerated && length >= 2 * Vector128<int>.Count)
        {
            return Vector(a, b, length);
        }

        return Scalar(a, b, length);
    }

    internal static long Scalar(int[] a, int[] b, int length)
    {
        long sse = 0;
        for (int i = 0; i < length; i++)
        {
            int diff = a[i] - b[i];
            sse += (long)diff * diff;
        }

        return sse;
    }

    internal static long Vector(int[] a, int[] b, int length)
    {
        _ = a[length - 1];
        _ = b[length - 1];
        ref int ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref int rb = ref MemoryMarshal.GetArrayDataReference(b);

        // Two independent accumulators to overlap the widening adds.
        var acc0 = Vector128<ulong>.Zero;
        var acc1 = Vector128<ulong>.Zero;
        int i = 0;
        int lastBlock = length - (2 * Vector128<int>.Count);
        for (; i <= lastBlock; i += 2 * Vector128<int>.Count)
        {
            var d0 = Vector128.LoadUnsafe(ref ra, (nuint)i) - Vector128.LoadUnsafe(ref rb, (nuint)i);
            var d1 = Vector128.LoadUnsafe(ref ra, (nuint)(i + 4)) - Vector128.LoadUnsafe(ref rb, (nuint)(i + 4));
            var s0 = Vector128.Abs(d0).AsUInt32();
            var s1 = Vector128.Abs(d1).AsUInt32();
            s0 *= s0;
            s1 *= s1;
            acc0 += Vector128.WidenLower(s0) + Vector128.WidenUpper(s0);
            acc1 += Vector128.WidenLower(s1) + Vector128.WidenUpper(s1);
        }

        long sse = (long)Vector128.Sum(acc0 + acc1);
        for (; i < length; i++)
        {
            int diff = a[i] - b[i];
            sse += (long)diff * diff;
        }

        return sse;
    }
}
