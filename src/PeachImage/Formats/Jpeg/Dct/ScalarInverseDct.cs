namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Reference (always-correct, not performance-optimized) inverse DCT kernel, implemented directly from the
/// JPEG IDCT definition (ITU-T.81 A.3.3) using double-precision arithmetic. SIMD kernels used for actual
/// throughput are separate implementations validated against this one.
/// </summary>
internal sealed class ScalarInverseDct : IInverseDctKernel
{
    private static readonly double[,] Cosines = BuildCosineTable();
    private static readonly double[] Normalization = [1.0 / 1.4142135623730951, 1, 1, 1, 1, 1, 1, 1];

    public void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> dequantTable, Span<byte> output, int outputStride)
    {
        Span<double> dequantized = stackalloc double[64];
        for (int i = 0; i < 64; i++)
        {
            dequantized[i] = coefficients[i] * (double)dequantTable[i];
        }

        Span<double> rowPass = stackalloc double[64];
        for (int v = 0; v < 8; v++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += Normalization[u] * dequantized[(v * 8) + u] * Cosines[x, u];
                }

                rowPass[(v * 8) + x] = sum * 0.5;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    sum += Normalization[v] * rowPass[(v * 8) + x] * Cosines[y, v];
                }

                double pixel = (sum * 0.5) + 128.0;
                int rounded = (int)Math.Round(pixel, MidpointRounding.AwayFromZero);
                output[(y * outputStride) + x] = (byte)Math.Clamp(rounded, 0, 255);
            }
        }
    }

    private static double[,] BuildCosineTable()
    {
        var table = new double[8, 8];
        for (int x = 0; x < 8; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                table[x, u] = Math.Cos((((2 * x) + 1) * u * Math.PI) / 16.0);
            }
        }

        return table;
    }
}
