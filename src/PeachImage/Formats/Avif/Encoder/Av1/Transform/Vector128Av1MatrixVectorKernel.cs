using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// SIMD matrix-vector kernel using <see cref="Vector128{T}"/>'s cross-platform generic static API: each
/// row's dot product accumulates 2 <see cref="double"/> lanes at a time. JITs to SSE2 on x86 and AdvSimd on
/// Arm from one source file. See <see cref="Vector256Av1MatrixVectorKernel"/>'s remarks for the
/// floating-point-reassociation tolerance this and the 256-bit kernel share.
/// </summary>
/// <remarks>
/// Rows are computed four at a time. A row's dot product is one long dependent chain of multiply-then-add
/// (each add waits on the previous), so a single accumulator leaves most of the FP pipeline idle; four rows
/// give four independent chains, and the shared <c>input</c> chunk is loaded once for all four. Each row still
/// accumulates the same products in the same order (lane-wise over increasing columns, then one horizontal
/// add), so the result is bit-for-bit what the one-row-at-a-time form produced — no fused multiply-add and no
/// extra accumulators per row, which would change the rounding.
/// </remarks>
internal sealed class Vector128Av1MatrixVectorKernel : IAv1MatrixVectorKernel
{
    private const int Lanes = 2;
    private const int RowsPerPass = 4;

    public void Apply(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
    {
        // Bounds are established once here so the inner loops can use unchecked references.
        _ = input[size - 1];
        _ = output[size - 1];
        _ = matrix[size - 1, size - 1];

        ref double inputStart = ref MemoryMarshal.GetReference(input);

        int row = 0;
        for (; row + RowsPerPass <= size; row += RowsPerPass)
        {
            ref double r0 = ref matrix[row, 0];
            ref double r1 = ref matrix[row + 1, 0];
            ref double r2 = ref matrix[row + 2, 0];
            ref double r3 = ref matrix[row + 3, 0];

            var acc0 = Vector128<double>.Zero;
            var acc1 = Vector128<double>.Zero;
            var acc2 = Vector128<double>.Zero;
            var acc3 = Vector128<double>.Zero;

            int col = 0;
            for (; col + Lanes <= size; col += Lanes)
            {
                var x = Vector128.LoadUnsafe(ref inputStart, (nuint)col);
                acc0 += Vector128.LoadUnsafe(ref r0, (nuint)col) * x;
                acc1 += Vector128.LoadUnsafe(ref r1, (nuint)col) * x;
                acc2 += Vector128.LoadUnsafe(ref r2, (nuint)col) * x;
                acc3 += Vector128.LoadUnsafe(ref r3, (nuint)col) * x;
            }

            double sum0 = Vector128.Sum(acc0);
            double sum1 = Vector128.Sum(acc1);
            double sum2 = Vector128.Sum(acc2);
            double sum3 = Vector128.Sum(acc3);
            for (; col < size; col++)
            {
                double x = Unsafe.Add(ref inputStart, col);
                sum0 += Unsafe.Add(ref r0, col) * x;
                sum1 += Unsafe.Add(ref r1, col) * x;
                sum2 += Unsafe.Add(ref r2, col) * x;
                sum3 += Unsafe.Add(ref r3, col) * x;
            }

            output[row] = sum0;
            output[row + 1] = sum1;
            output[row + 2] = sum2;
            output[row + 3] = sum3;
        }

        // Any remaining rows (sizes here are powers of two >= 4, so normally none) use the one-row form.
        for (; row < size; row++)
        {
            ref double rowStart = ref matrix[row, 0];
            var acc = Vector128<double>.Zero;
            int col = 0;
            for (; col + Lanes <= size; col += Lanes)
            {
                acc += Vector128.LoadUnsafe(ref rowStart, (nuint)col) * Vector128.LoadUnsafe(ref inputStart, (nuint)col);
            }

            double sum = Vector128.Sum(acc);
            for (; col < size; col++)
            {
                sum += Unsafe.Add(ref rowStart, col) * Unsafe.Add(ref inputStart, col);
            }

            output[row] = sum;
        }
    }
}
