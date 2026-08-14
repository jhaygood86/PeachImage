namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// Fast (but exact) forward DCT kernel using an even/odd butterfly network derived directly from the
/// ITU-T.81 A.3.3 definition: the mirror symmetry <c>cos((2(7-n)+1)u*pi/16) = (-1)^u * cos((2n+1)u*pi/16)</c>
/// splits each 1D pass into a 4-point DCT-II (even frequencies) and a 4-point DCT-IV (odd frequencies),
/// cutting 64 multiplies per pass down to 21 with no approximation and no per-frequency scale-factor
/// correction needed — its output is numerically equivalent, within floating-point rounding, to
/// <see cref="ScalarForwardDct"/>'s direct separable-transform sum.
/// </summary>
internal sealed class FastScalarForwardDct : IForwardDctKernel
{
    private static readonly double C1 = Math.Cos(1 * Math.PI / 16.0);
    private static readonly double C2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double C3 = Math.Cos(3 * Math.PI / 16.0);
    private static readonly double C4 = Math.Cos(4 * Math.PI / 16.0);
    private static readonly double C5 = Math.Cos(5 * Math.PI / 16.0);
    private static readonly double C6 = Math.Cos(6 * Math.PI / 16.0);
    private static readonly double C7 = Math.Cos(7 * Math.PI / 16.0);
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
        Span<double> stage = stackalloc double[8];
        for (int y = 0; y < 8; y++)
        {
            Forward1D(shifted.Slice(y * 8, 8), stage);
            stage.CopyTo(rowPass.Slice(y * 8, 8));
        }

        Span<double> column = stackalloc double[8];
        for (int u = 0; u < 8; u++)
        {
            column[0] = rowPass[u];
            column[1] = rowPass[8 + u];
            column[2] = rowPass[16 + u];
            column[3] = rowPass[24 + u];
            column[4] = rowPass[32 + u];
            column[5] = rowPass[40 + u];
            column[6] = rowPass[48 + u];
            column[7] = rowPass[56 + u];

            Forward1D(column, stage);
            for (int v = 0; v < 8; v++)
            {
                output[(v * 8) + u] = 0.25 * Normalization[u] * Normalization[v] * stage[v];
            }
        }
    }

    /// <summary>
    /// Computes the raw (unnormalized) 1D DCT-II sum <c>F(u) = sum_n f(n)*cos((2n+1)u*pi/16)</c> for
    /// <paramref name="f"/> via the even/odd butterfly network, writing the 8 results to <paramref name="result"/>.
    /// </summary>
    private static void Forward1D(ReadOnlySpan<double> f, Span<double> result)
    {
        double a0 = f[0] + f[7], b0 = f[0] - f[7];
        double a1 = f[1] + f[6], b1 = f[1] - f[6];
        double a2 = f[2] + f[5], b2 = f[2] - f[5];
        double a3 = f[3] + f[4], b3 = f[3] - f[4];

        double p0 = a0 + a3, p3 = a0 - a3;
        double p1 = a1 + a2, p2 = a1 - a2;

        result[0] = p0 + p1;
        result[4] = (p0 - p1) * C4;
        result[2] = (p3 * C2) + (p2 * C6);
        result[6] = (p3 * C6) - (p2 * C2);

        result[1] = (C1 * b0) + (C3 * b1) + (C5 * b2) + (C7 * b3);
        result[3] = (C3 * b0) - (C7 * b1) - (C1 * b2) - (C5 * b3);
        result[5] = (C5 * b0) - (C1 * b1) + (C7 * b2) + (C3 * b3);
        result[7] = (C7 * b0) - (C5 * b1) + (C3 * b2) - (C1 * b3);
    }
}
