namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Fast (but exact) inverse DCT kernel — the exact transpose of <see cref="FastScalarForwardDct"/>'s
/// even/odd butterfly network. Because every stage of that network is a symmetric linear map (a mirror
/// butterfly, a 4-point DCT-II, and a 4-point DCT-IV), its transpose is obtained by running the same
/// stage formulas in reverse topological order — no separate derivation needed. Numerically equivalent,
/// within floating-point rounding, to <see cref="ScalarInverseDct"/>'s direct separable-transform sum,
/// using 21 multiplies per 1D pass instead of 64, with no per-frequency scale-factor correction needed.
/// </summary>
internal sealed class FastScalarInverseDct : IInverseDctKernel
{
    private static readonly double C1 = Math.Cos(1 * Math.PI / 16.0);
    private static readonly double C2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double C3 = Math.Cos(3 * Math.PI / 16.0);
    private static readonly double C4 = Math.Cos(4 * Math.PI / 16.0);
    private static readonly double C5 = Math.Cos(5 * Math.PI / 16.0);
    private static readonly double C6 = Math.Cos(6 * Math.PI / 16.0);
    private static readonly double C7 = Math.Cos(7 * Math.PI / 16.0);
    private static readonly double[] Normalization = [1.0 / 1.4142135623730951, 1, 1, 1, 1, 1, 1, 1];

    public void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride)
    {
        Span<double> weighted = stackalloc double[64];
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                int i = (v * 8) + u;
                weighted[i] = Normalization[u] * coefficients[i] * dequantTable[i];
            }
        }

        Span<double> rowPass = stackalloc double[64];
        Span<double> stage = stackalloc double[8];
        for (int v = 0; v < 8; v++)
        {
            Inverse1D(weighted.Slice(v * 8, 8), stage);
            for (int x = 0; x < 8; x++)
            {
                rowPass[(v * 8) + x] = stage[x] * 0.5;
            }
        }

        Span<double> column = stackalloc double[8];
        for (int x = 0; x < 8; x++)
        {
            column[0] = Normalization[0] * rowPass[x];
            column[1] = Normalization[1] * rowPass[8 + x];
            column[2] = Normalization[2] * rowPass[16 + x];
            column[3] = Normalization[3] * rowPass[24 + x];
            column[4] = Normalization[4] * rowPass[32 + x];
            column[5] = Normalization[5] * rowPass[40 + x];
            column[6] = Normalization[6] * rowPass[48 + x];
            column[7] = Normalization[7] * rowPass[56 + x];

            Inverse1D(column, stage);
            for (int y = 0; y < 8; y++)
            {
                double pixel = (stage[y] * 0.5) + 128.0;
                int rounded = (int)Math.Round(pixel, MidpointRounding.AwayFromZero);
                output[(y * outputStride) + x] = (byte)Math.Clamp(rounded, 0, 255);
            }
        }
    }

    /// <summary>
    /// Computes the raw (unnormalized) 1D inverse sum <c>f(x) = sum_u y(u)*cos((2x+1)u*pi/16)</c> for
    /// weighted frequency-domain input <paramref name="y"/>, via the transpose of the forward butterfly
    /// network, writing the 8 results to <paramref name="result"/>.
    /// </summary>
    private static void Inverse1D(ReadOnlySpan<double> y, Span<double> result)
    {
        double b0 = (C1 * y[1]) + (C3 * y[3]) + (C5 * y[5]) + (C7 * y[7]);
        double b1 = (C3 * y[1]) - (C7 * y[3]) - (C1 * y[5]) - (C5 * y[7]);
        double b2 = (C5 * y[1]) - (C1 * y[3]) + (C7 * y[5]) + (C3 * y[7]);
        double b3 = (C7 * y[1]) - (C5 * y[3]) + (C3 * y[5]) - (C1 * y[7]);

        double p3 = (y[2] * C2) + (y[6] * C6);
        double p2 = (y[2] * C6) - (y[6] * C2);
        double q1 = y[4] * C4;
        double q0 = y[0];
        double p0 = q0 + q1;
        double p1 = q0 - q1;
        double a0 = p0 + p3, a3 = p0 - p3;
        double a1 = p1 + p2, a2 = p1 - p2;

        result[0] = a0 + b0;
        result[7] = a0 - b0;
        result[1] = a1 + b1;
        result[6] = a1 - b1;
        result[2] = a2 + b2;
        result[5] = a2 - b2;
        result[3] = a3 + b3;
        result[4] = a3 - b3;
    }
}
