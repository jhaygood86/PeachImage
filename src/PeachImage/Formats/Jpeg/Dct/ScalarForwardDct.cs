namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Reference (always-correct, not performance-optimized) forward DCT kernel, implemented directly from the
/// JPEG FDCT definition (ITU-T.81 A.3.3) using double-precision arithmetic.
/// </summary>
internal sealed class ScalarForwardDct : IForwardDctKernel
{
    private static readonly double[,] Cosines = BuildCosineTable();
    private static readonly double[] Normalization = [1.0 / 1.4142135623730951, 1, 1, 1, 1, 1, 1, 1];

    public void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output)
    {
        Span<double> shifted = stackalloc double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                shifted[(y * 8) + x] = input[(y * inputStride) + x] - 128.0;
            }
        }

        Span<double> rowPass = stackalloc double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++)
                {
                    sum += shifted[(y * 8) + x] * Cosines[x, u];
                }

                rowPass[(y * 8) + u] = sum;
            }
        }

        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    sum += rowPass[(y * 8) + u] * Cosines[y, v];
                }

                output[(v * 8) + u] = 0.25 * Normalization[u] * Normalization[v] * sum;
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
