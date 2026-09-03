namespace PeachImage.Formats.Avif.Encoder.Av1.Transform;

/// <summary>
/// Reference (always-correct, not performance-optimized) matrix-vector kernel -- a plain sequential
/// accumulation dot product per row, exactly <see cref="Av1ForwardTransform"/>'s original inline loop before
/// this tiered-kernel split.
/// </summary>
internal sealed class ScalarAv1MatrixVectorKernel : IAv1MatrixVectorKernel
{
    public void Apply(double[,] matrix, ReadOnlySpan<double> input, Span<double> output, int size)
    {
        for (int row = 0; row < size; row++)
        {
            double sum = 0;
            for (int col = 0; col < size; col++)
            {
                sum += matrix[row, col] * input[col];
            }

            output[row] = sum;
        }
    }
}
