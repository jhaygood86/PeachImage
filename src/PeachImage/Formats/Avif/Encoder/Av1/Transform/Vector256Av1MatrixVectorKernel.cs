using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// SIMD matrix-vector kernel using <see cref="Vector256{T}"/>'s cross-platform generic static API: each
/// row's dot product accumulates 4 <see cref="double"/> lanes at a time (Av1ForwardTransform only ever
/// calls this with <c>size</c> in {4, 8, 16, 32}, all multiples of <see cref="Lanes"/>, so the scalar tail
/// below never actually executes for this class's real callers -- kept anyway so the kernel stays correct
/// for any <c>size</c>, not just the four this encoder happens to use).
/// </summary>
/// <remarks>
/// SIMD horizontal summation reassociates the addition order versus the scalar kernel's strictly sequential
/// accumulation (this class sums <see cref="Lanes"/>-wide partial products pairwise via
/// <see cref="Vector256.Sum{T}(Vector256{T})"/>, not left-to-right one term at a time) -- floating-point
/// addition is not associative, so results can differ from <see cref="ScalarAv1MatrixVectorKernel"/> in the
/// last bit or two. This is expected and harmless here: the caller (<see cref="Av1ForwardTransform.Forward2D"/>)
/// rounds to an <see cref="int"/> at the very end, and the existing round-trip-through-the-decoder-inverse
/// tests (<c>Av1ForwardTransformTests</c>) already carry a several-LSB tolerance for exactly this kind of
/// floating-point noise.
/// </remarks>
internal sealed class Vector256Av1MatrixVectorKernel : IAv1MatrixVectorKernel
{
    private const int Lanes = 4;

    public void Apply(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
    {
        for (int row = 0; row < size; row++)
        {
            ref double rowStart = ref matrix[row, 0];
            ReadOnlySpan<double> rowSpan = MemoryMarshal.CreateReadOnlySpan(ref rowStart, size);

            var acc = Vector256<double>.Zero;
            int col = 0;
            for (; col + Lanes <= size; col += Lanes)
            {
                var rowChunk = Vector256.Create(rowSpan.Slice(col, Lanes));
                var inputChunk = Vector256.Create(input.Slice(col, Lanes));
                acc += rowChunk * inputChunk;
            }

            double sum = Vector256.Sum(acc);
            for (; col < size; col++)
            {
                sum += rowSpan[col] * input[col];
            }

            output[row] = sum;
        }
    }
}
