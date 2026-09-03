using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// SIMD matrix-vector kernel using <see cref="Vector128{T}"/>'s cross-platform generic static API: each
/// row's dot product accumulates 2 <see cref="double"/> lanes at a time. JITs to SSE2 on x86 and AdvSimd on
/// Arm from one source file. See <see cref="Vector256Av1MatrixVectorKernel"/>'s remarks for the
/// floating-point-reassociation tolerance this and the 256-bit kernel share.
/// </summary>
internal sealed class Vector128Av1MatrixVectorKernel : IAv1MatrixVectorKernel
{
    private const int Lanes = 2;

    public void Apply(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
    {
        for (int row = 0; row < size; row++)
        {
            ref double rowStart = ref matrix[row, 0];
            ReadOnlySpan<double> rowSpan = MemoryMarshal.CreateReadOnlySpan(ref rowStart, size);

            var acc = Vector128<double>.Zero;
            int col = 0;
            for (; col + Lanes <= size; col += Lanes)
            {
                var rowChunk = Vector128.Create(rowSpan.Slice(col, Lanes));
                var inputChunk = Vector128.Create(input.Slice(col, Lanes));
                acc += rowChunk * inputChunk;
            }

            double sum = Vector128.Sum(acc);
            for (; col < size; col++)
            {
                sum += rowSpan[col] * input[col];
            }

            output[row] = sum;
        }
    }
}
