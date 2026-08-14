namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// True minimal-multiply AAN (Arai-Agui-Nakajima) inverse DCT kernel — 5 multiplies per 1D pass (vs.
/// <see cref="FastScalarInverseDct"/>'s 21). The odd-branch butterfly wiring (the piece that could not be
/// safely re-derived from the DCT-III definition alone — see issue #5) is transcribed from libjpeg-turbo's
/// <c>jidctflt.c</c> (<c>jpeg_idct_float</c>); see THIRD-PARTY-LICENSES.md for attribution. Consumes an
/// <see cref="AanScaleFactors.BuildInverseDequantTable"/>-scaled dequantization table directly (no
/// additional per-frequency correction inside the transform) to be numerically equivalent to
/// <see cref="ScalarInverseDct"/>.
/// </summary>
/// <remarks>
/// The final <c>0.125</c> multiply below is not part of the classical AAN flowgraph itself; it's the
/// overall 2D descale that falls out of comparing this network's raw gain against the direct-definition
/// sum's, the same way libjpeg-turbo folds an equivalent constant into its own quantization-table setup
/// rather than the hot per-block path.
/// </remarks>
internal sealed class AanScalarInverseDct : IInverseDctKernel
{
    private static readonly double C2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double C6 = Math.Cos(6 * Math.PI / 16.0);
    private static readonly double Sqrt2 = Math.Sqrt(2.0);

    // The IDCT odd branch's rotation constants are exactly double the FDCT odd branch's — the inverse
    // network's shared-term trick needs twice the gain to undo the forward network's own descale.
    private static readonly double TwoC2 = 2.0 * C2;
    private static readonly double TwoC2MinusC6 = 2.0 * (C2 - C6);
    private static readonly double TwoC2PlusC6 = 2.0 * (C2 + C6);

    public float[] PrepareDequantTable(ReadOnlySpan<ushort> dequantTable) => AanScaleFactors.BuildInverseDequantTable(dequantTable);

    public void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride)
    {
        Span<double> weighted = stackalloc double[64];
        for (int i = 0; i < 64; i++)
        {
            weighted[i] = coefficients[i] * dequantTable[i];
        }

        Span<double> rowPass = stackalloc double[64];
        Span<double> stage = stackalloc double[8];
        for (int v = 0; v < 8; v++)
        {
            Inverse1D(weighted.Slice(v * 8, 8), stage);
            stage.CopyTo(rowPass.Slice(v * 8, 8));
        }

        Span<double> column = stackalloc double[8];
        for (int x = 0; x < 8; x++)
        {
            column[0] = rowPass[x];
            column[1] = rowPass[8 + x];
            column[2] = rowPass[16 + x];
            column[3] = rowPass[24 + x];
            column[4] = rowPass[32 + x];
            column[5] = rowPass[40 + x];
            column[6] = rowPass[48 + x];
            column[7] = rowPass[56 + x];

            Inverse1D(column, stage);
            for (int y = 0; y < 8; y++)
            {
                double pixel = (stage[y] * 0.125) + 128.0;
                int rounded = (int)Math.Round(pixel, MidpointRounding.AwayFromZero);
                output[(y * outputStride) + x] = (byte)Math.Clamp(rounded, 0, 255);
            }
        }
    }

    /// <summary>
    /// Computes one 1D AAN inverse pass. The odd branch's final unwind (<c>o6</c>, then <c>o5</c> which
    /// needs <c>o6</c>, then <c>o4</c> which needs <c>o5</c>) is a sequential subtraction chain, not three
    /// independent combinations — this cascade is exactly the shape a naive sum/difference re-derivation
    /// misses.
    /// </summary>
    private static void Inverse1D(ReadOnlySpan<double> y, Span<double> result)
    {
        double e0 = y[0] + y[4];
        double e1 = y[0] - y[4];
        double e3 = y[2] + y[6];
        double e2 = ((y[2] - y[6]) * Sqrt2) - e3;

        double a0 = e0 + e3, a3 = e0 - e3;
        double a1 = e1 + e2, a2 = e1 - e2;

        double z13 = y[5] + y[3];
        double z10 = y[5] - y[3];
        double z11 = y[1] + y[7];
        double z12 = y[1] - y[7];

        double z5 = (z10 + z12) * TwoC2;
        double o10 = z5 - (z12 * TwoC2MinusC6);
        double o12 = z5 - (z10 * TwoC2PlusC6);

        double o7 = z11 + z13;
        double o11 = (z11 - z13) * Sqrt2;

        double o6 = o12 - o7;
        double o5 = o11 - o6;
        double o4 = o10 - o5;

        result[0] = a0 + o7;
        result[7] = a0 - o7;
        result[1] = a1 + o6;
        result[6] = a1 - o6;
        result[2] = a2 + o5;
        result[5] = a2 - o5;
        result[3] = a3 + o4;
        result[4] = a3 - o4;
    }
}
