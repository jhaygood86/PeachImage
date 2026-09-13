using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Variance;

/// <summary>
/// SIMD tier of <see cref="IAv1BlockSumSquaresKernel"/>, matching libaom's own real
/// <c>aom_var_2d_u8_sse2</c> shape (<c>aom_dsp/x86/sum_squares_sse2.c</c>): accumulate sum and sum-of-squares
/// across the block in vector lanes, horizontal-reduce once at the end. Uses <see cref="int"/> lanes
/// throughout rather than libaom's own 8-bit-packed-then-unpacked representation, since this project's pixel
/// buffers are already <see cref="int"/>.
///
/// <para><b>Why accumulating the whole block into one running vector (no periodic flush) is safe</b>: unlike
/// libaom's own <c>aom_var_2d_u8_sse2</c>, which flushes its 32-bit lane accumulator to a 64-bit scalar every
/// 8 rows to guard against overflow at libaom's own full range of callers, this project's real callers
/// (<c>Av1TileEncoder</c>/<c>Av1ScreenContentEstimator</c>) only ever pass 8-bit source samples (this
/// encoder has no bit-depth option yet -- see <c>AvifEncoderOptions</c>'s own class doc) over blocks no
/// larger than 64x64. The worst case per lane is therefore <c>64 * 255^2 &#8776; 4.16M</c> for sum-of-squares
/// (sum itself is smaller still) -- many orders of magnitude below <see cref="int"/>'s overflow point, so a
/// single un-flushed running accumulator for the whole call is exact, not just fast.</para>
/// </summary>
internal sealed class Vector128Av1BlockSumSquaresKernel : IAv1BlockSumSquaresKernel
{
    private const int Lanes = 4;

    public void Compute(ReadOnlySpan<int> source, int stride, int x, int y, int w, int h, out long sum, out long sumSquares)
    {
        var sumVec = Vector128<int>.Zero;
        var sqVec = Vector128<int>.Zero;
        long tailSum = 0;
        long tailSumSquares = 0;

        for (int dy = 0; dy < h; dy++)
        {
            int rowBase = ((y + dy) * stride) + x;
            ReadOnlySpan<int> row = source.Slice(rowBase, w);

            int dx = 0;
            for (; dx + Lanes <= w; dx += Lanes)
            {
                var v = Vector128.Create(row.Slice(dx, Lanes));
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

        sum = Vector128.Sum(sumVec) + tailSum;
        sumSquares = Vector128.Sum(sqVec) + tailSumSquares;
    }
}
