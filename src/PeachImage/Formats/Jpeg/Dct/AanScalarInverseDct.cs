using System.Runtime.CompilerServices;

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
/// The overall 2D descale (0.125) that falls out of comparing this network's raw gain against the
/// direct-definition sum's is folded into <see cref="PrepareDequantTable"/> — applied once per component,
/// the same way libjpeg-turbo folds an equivalent constant into its own quantization-table setup rather
/// than the hot per-block path.
/// </remarks>
internal sealed class AanScalarInverseDct : IInverseDctKernel
{
    private static readonly double C2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double C6 = Math.Cos(6 * Math.PI / 16.0);
    private static readonly float Sqrt2 = (float)Math.Sqrt(2.0);

    // The IDCT odd branch's rotation constants are exactly double the FDCT odd branch's — the inverse
    // network's shared-term trick needs twice the gain to undo the forward network's own descale.
    private static readonly float TwoC2 = (float)(2.0 * C2);
    private static readonly float TwoC2MinusC6 = (float)(2.0 * (C2 - C6));
    private static readonly float TwoC2PlusC6 = (float)(2.0 * (C2 + C6));

    // Level shift (128) plus the round-to-nearest bias (0.5), added once per column instead of once per
    // pixel: y[0] is the only input that feeds every one of Inverse1D's 8 outputs additively (e0 = y[0] +
    // y[4], e1 = y[0] - y[4], and every result[i] is a_j ± o_k), so biasing it here biases all 8 outputs.
    private const float LevelShiftAndRoundingBias = 128.5f;

    public float[] PrepareDequantTable(ReadOnlySpan<ushort> dequantTable)
    {
        var table = AanScaleFactors.BuildInverseDequantTable(dequantTable);
        for (int i = 0; i < 64; i++)
        {
            table[i] *= 0.125f;
        }

        return table;
    }

    [SkipLocalsInit]
    public void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride)
    {
        Span<float> rowPass = stackalloc float[64];
        Span<float> stage = stackalloc float[8];
        Span<float> row = stackalloc float[8];
        for (int v = 0; v < 8; v++)
        {
            int offset = v * 8;
            for (int u = 0; u < 8; u++)
            {
                row[u] = coefficients[offset + u] * dequantTable[offset + u];
            }

            Inverse1D(row, stage);
            stage.CopyTo(rowPass.Slice(offset, 8));
        }

        var rangeLimit = SampleRangeLimit.Table;
        Span<float> column = stackalloc float[8];
        for (int x = 0; x < 8; x++)
        {
            column[0] = rowPass[x] + LevelShiftAndRoundingBias;
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
                output[(y * outputStride) + x] = rangeLimit[(int)stage[y] & SampleRangeLimit.Mask];
            }
        }
    }

    /// <summary>
    /// Computes one 1D AAN inverse pass. The odd branch's final unwind (<c>o6</c>, then <c>o5</c> which
    /// needs <c>o6</c>, then <c>o4</c> which needs <c>o5</c>) is a sequential subtraction chain, not three
    /// independent combinations — this cascade is exactly the shape a naive sum/difference re-derivation
    /// misses.
    /// </summary>
    private static void Inverse1D(ReadOnlySpan<float> y, Span<float> result)
    {
        float e0 = y[0] + y[4];
        float e1 = y[0] - y[4];
        float e3 = y[2] + y[6];
        float e2 = ((y[2] - y[6]) * Sqrt2) - e3;

        float a0 = e0 + e3, a3 = e0 - e3;
        float a1 = e1 + e2, a2 = e1 - e2;

        float z13 = y[5] + y[3];
        float z10 = y[5] - y[3];
        float z11 = y[1] + y[7];
        float z12 = y[1] - y[7];

        float z5 = (z10 + z12) * TwoC2;
        float o10 = z5 - (z12 * TwoC2MinusC6);
        float o12 = z5 - (z10 * TwoC2PlusC6);

        float o7 = z11 + z13;
        float o11 = (z11 - z13) * Sqrt2;

        float o6 = o12 - o7;
        float o5 = o11 - o6;
        float o4 = o10 - o5;

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
