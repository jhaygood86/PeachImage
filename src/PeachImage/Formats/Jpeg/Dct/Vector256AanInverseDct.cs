using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// AVX2 AAN inverse DCT kernel: one 8x8 block per <see cref="Transform"/> call, vectorized across the
/// block's own 8 rows/columns via <see cref="AanVectorButterfly"/> — libjpeg-turbo's own approach (process
/// one block, transpose between the two 1D passes), not a batch of multiple blocks. Selected by
/// <see cref="DctKernelSelector"/> only when <see cref="System.Runtime.Intrinsics.X86.Avx.IsSupported"/>;
/// falls back to <see cref="AanScalarInverseDct"/> (now itself float, see that type) otherwise.
/// </summary>
internal sealed class Vector256AanInverseDct : IInverseDctKernel
{
    // 0.125 (the network's overall 2D descale) is folded into the dequant table here, exactly as
    // AanScalarInverseDct does, so it costs nothing per block. The remaining per-row addend is the level
    // shift (128) plus the round-to-nearest bias (0.5) — see AanScalarInverseDct for why biasing input
    // position 0 of an Inverse1D pass is equivalent to biasing every one of its 8 outputs; the same holds
    // here since AanVectorButterfly.AanInverse1D is a lane-wise transcription of that exact butterfly.
    private static readonly Vector256<float> LevelShiftAndRoundingBias = Vector256.Create(128.5f);
    private static readonly Vector256<float> MaxSample = Vector256.Create(255f);

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
        Vec8 m = default;
        m.V0 = LoadAndDequantizeRow(coefficients, dequantTable, 0 * 8);
        m.V1 = LoadAndDequantizeRow(coefficients, dequantTable, 1 * 8);
        m.V2 = LoadAndDequantizeRow(coefficients, dequantTable, 2 * 8);
        m.V3 = LoadAndDequantizeRow(coefficients, dequantTable, 3 * 8);
        m.V4 = LoadAndDequantizeRow(coefficients, dequantTable, 4 * 8);
        m.V5 = LoadAndDequantizeRow(coefficients, dequantTable, 5 * 8);
        m.V6 = LoadAndDequantizeRow(coefficients, dequantTable, 6 * 8);
        m.V7 = LoadAndDequantizeRow(coefficients, dequantTable, 7 * 8);

        // Pass 1: register index v (frequency row), lane index u (position within the row) — exactly the
        // natural-order layout the loads above produced, so this transforms along v for every u at once.
        AanVectorButterfly.AanInverse1D(ref m);

        // Reorient: register v / lane u -> register u / lane v, so the second pass can transform along u.
        AanVectorButterfly.Transpose8x8(ref m);
        AanVectorButterfly.AanInverse1D(ref m);

        // Reorient once more: register x (spatial column) / lane y -> register y (spatial row) / lane x, so
        // each register directly holds one output row's 8 samples in order.
        AanVectorButterfly.Transpose8x8(ref m);

        StoreRow(m.V0, output, 0 * outputStride);
        StoreRow(m.V1, output, 1 * outputStride);
        StoreRow(m.V2, output, 2 * outputStride);
        StoreRow(m.V3, output, 3 * outputStride);
        StoreRow(m.V4, output, 4 * outputStride);
        StoreRow(m.V5, output, 5 * outputStride);
        StoreRow(m.V6, output, 6 * outputStride);
        StoreRow(m.V7, output, 7 * outputStride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadAndDequantizeRow(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, int offset)
    {
        Span<float> row = stackalloc float[8];
        for (int u = 0; u < 8; u++)
        {
            row[u] = coefficients[offset + u] * dequantTable[offset + u];
        }

        return Vector256.Create((ReadOnlySpan<float>)row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Vector256<float> row, Span<byte> output, int offset)
    {
        var biased = row + LevelShiftAndRoundingBias;
        var clamped = Vector256.Min(Vector256.Max(biased, Vector256<float>.Zero), MaxSample);

        Span<float> values = stackalloc float[8];
        clamped.CopyTo(values);
        for (int i = 0; i < 8; i++)
        {
            // Clamped to [0, 255] above and biased by +0.5 for round-to-nearest, so a plain truncating cast
            // is exact — the same bias-and-truncate idea as AanScalarInverseDct's range-limit-table finish,
            // just via a vector Min/Max clamp instead of a table lookup (a table lookup here would need a
            // gather, which is slower than the two compare instructions Min/Max already are).
            output[offset + i] = (byte)values[i];
        }
    }
}
