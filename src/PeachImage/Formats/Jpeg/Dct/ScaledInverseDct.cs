namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// DCT-domain downscaling: reconstructs an 8x8 block as an <c>M x M</c> block (<c>M</c> = 4, 2 or 1, i.e. a 1/2, 1/4 or 1/8 scale)
/// by inverse-transforming only its lowest <c>M x M</c> frequencies with an <c>M</c>-point orthonormal IDCT, the same idea as libjpeg's
/// reduced-size IDCTs. The 8-point coefficients are rescaled by <c>M/8</c> so the block's mean level is preserved; at 1/8 the result is
/// the block's DC value. Far cheaper than a full transform followed by a resize, and it needs no input at the discarded frequencies.
/// </summary>
internal static class ScaledInverseDct
{
    // Orthonormal M-point DCT basis, [frequency * M + sample], for M = 1, 2, 4 (index 0, 1, 2 = log2(M)).
    private static readonly float[][] Basis = [BuildBasis(1), BuildBasis(2), BuildBasis(4)];

    private static float[] BuildBasis(int m)
    {
        var basis = new float[m * m];
        for (int u = 0; u < m; u++)
        {
            double c = u == 0 ? Math.Sqrt(0.5) : 1.0;
            for (int x = 0; x < m; x++)
            {
                basis[(u * m) + x] = (float)(c * Math.Sqrt(2.0 / m) * Math.Cos(((2 * x) + 1) * u * Math.PI / (2.0 * m)));
            }
        }

        return basis;
    }

    /// <summary>The output block size for a given scale denominator (<c>8 / denominator</c>).</summary>
    public static int BlockSize(int scaleDenominator) => 8 / scaleDenominator;

    /// <summary>Widens the raw natural-order quantization table and folds in the <c>M/8</c> rescale for block size <paramref name="blockSize"/>.</summary>
    public static float[] PrepareDequantTable(ReadOnlySpan<ushort> dequantTable, int blockSize)
    {
        float factor = blockSize / 8f;
        var table = new float[64];
        for (int i = 0; i < 64; i++)
        {
            table[i] = dequantTable[i] * factor;
        }

        return table;
    }

    /// <summary>Dequantizes <paramref name="coefficients"/> (64, natural order) and writes a <paramref name="blockSize"/> x <paramref name="blockSize"/> block of samples to <paramref name="output"/>.</summary>
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride, int blockSize)
    {
        int m = blockSize;
        var basis = Basis[m == 4 ? 2 : m == 2 ? 1 : 0];

        Span<float> rows = stackalloc float[16];
        for (int v = 0; v < m; v++)
        {
            for (int x = 0; x < m; x++)
            {
                float sum = 0;
                for (int u = 0; u < m; u++)
                {
                    int index = (v * 8) + u;
                    sum += coefficients[index] * dequantTable[index] * basis[(u * m) + x];
                }

                rows[(v * m) + x] = sum;
            }
        }

        for (int y = 0; y < m; y++)
        {
            for (int x = 0; x < m; x++)
            {
                float sum = 128.5f;
                for (int v = 0; v < m; v++)
                {
                    sum += rows[(v * m) + x] * basis[(v * m) + y];
                }

                output[(y * outputStride) + x] = sum <= 0 ? (byte)0 : sum >= 255 ? (byte)255 : (byte)sum;
            }
        }
    }
}
