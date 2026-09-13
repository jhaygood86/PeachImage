using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Variance;

/// <summary>
/// AVX2-width tier of <see cref="IAv1BlockSumSquaresKernel"/>, matching libaom's own real
/// <c>aom_var_2d_u8_avx2</c> (<c>aom_dsp/x86/sum_squares_avx2.c</c>) -- same mechanism as
/// <see cref="Vector128Av1BlockSumSquaresKernel"/> (see its remarks, including the no-periodic-flush
/// overflow argument, which applies identically here since it bounds the *total*, not a per-lane share) but
/// <see cref="Lanes"/> columns per iteration instead of 4.
/// </summary>
internal sealed class Vector256Av1BlockSumSquaresKernel : IAv1BlockSumSquaresKernel
{
    private const int Lanes = 8;

    public void Compute(ReadOnlySpan<int> source, int stride, int x, int y, int w, int h, out long sum, out long sumSquares)
    {
        var sumVec = Vector256<int>.Zero;
        var sqVec = Vector256<int>.Zero;
        long tailSum = 0;
        long tailSumSquares = 0;

        for (int dy = 0; dy < h; dy++)
        {
            int rowBase = ((y + dy) * stride) + x;
            ReadOnlySpan<int> row = source.Slice(rowBase, w);

            int dx = 0;
            for (; dx + Lanes <= w; dx += Lanes)
            {
                var v = Vector256.Create(row.Slice(dx, Lanes));
                sumVec += v;
                sqVec += v * v;
            }

            for (; dx < w; dx++)
            {
                int px = row[dx];
                tailSum += px;
                tailSumSquares += (long)px * px;
            }
        }

        sum = Vector256.Sum(sumVec) + tailSum;
        sumSquares = Vector256.Sum(sqVec) + tailSumSquares;
    }
}
