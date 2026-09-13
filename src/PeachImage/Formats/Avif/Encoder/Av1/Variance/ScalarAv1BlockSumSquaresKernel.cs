namespace PeachImage.Formats.Avif.Encoder.Av1.Variance;

/// <summary>Scalar reference tier of <see cref="IAv1BlockSumSquaresKernel"/> -- the same two-accumulator loop every call site used inline before this kernel existed.</summary>
internal sealed class ScalarAv1BlockSumSquaresKernel : IAv1BlockSumSquaresKernel
{
    public void Compute(ReadOnlySpan<int> source, int stride, int x, int y, int w, int h, out long sum, out long sumSquares)
    {
        long localSum = 0;
        long localSumSquares = 0;

        for (int dy = 0; dy < h; dy++)
        {
            int rowBase = ((y + dy) * stride) + x;
            for (int dx = 0; dx < w; dx++)
            {
                int px = source[rowBase + dx];
                localSum += px;
                localSumSquares += (long)px * px;
            }
        }

        sum = localSum;
        sumSquares = localSumSquares;
    }
}
