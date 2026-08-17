using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// AVX2 AAN forward DCT kernel — the <see cref="Vector256AanInverseDct"/> counterpart. One 8x8 block per
/// <see cref="Transform"/> call, vectorized across the block's own rows/columns via
/// <see cref="AanVectorButterfly"/>, transposing between the two 1D passes.
/// </summary>
/// <remarks>
/// Keeps <see cref="IForwardDctKernel"/>'s existing <c>Span&lt;double&gt;</c> output / <c>double[]</c> quant
/// table exactly as-is — following the precedent <see cref="Vector128ForwardDct"/>/
/// <see cref="Vector256ForwardDct"/> already set in this codebase (compute internally in <c>float</c>,
/// widen only at the final store). This avoids any interface or call-site change; <see cref="AanScaleFactors.BuildForwardQuantTable"/>
/// and <see cref="ScalarForwardDct"/> (the double-precision oracle) are untouched.
/// </remarks>
internal sealed class Vector256AanForwardDct : IForwardDctKernel
{
    private static readonly Vector256<float> Eighth = Vector256.Create(0.125f);

    public double[] PrepareQuantTable(ReadOnlySpan<ushort> quantTable) => AanScaleFactors.BuildForwardQuantTable(quantTable);

    [SkipLocalsInit]
    public void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output)
    {
        Vec8 m = default;
        m.V0 = LoadShiftedRow(input, 0 * inputStride);
        m.V1 = LoadShiftedRow(input, 1 * inputStride);
        m.V2 = LoadShiftedRow(input, 2 * inputStride);
        m.V3 = LoadShiftedRow(input, 3 * inputStride);
        m.V4 = LoadShiftedRow(input, 4 * inputStride);
        m.V5 = LoadShiftedRow(input, 5 * inputStride);
        m.V6 = LoadShiftedRow(input, 6 * inputStride);
        m.V7 = LoadShiftedRow(input, 7 * inputStride);

        // Pass 1: register index y (spatial row), lane index x (position within the row) — the natural
        // load layout above, so this transforms along y for every x at once.
        AanVectorButterfly.AanForward1D(ref m);

        // Reorient: register y / lane x -> register x / lane v, so the second pass can transform along x.
        AanVectorButterfly.Transpose8x8(ref m);
        AanVectorButterfly.AanForward1D(ref m);

        // Reorient once more: register u (horizontal frequency) / lane v -> register v / lane u, so each
        // register directly holds one output frequency-row's 8 coefficients in natural order.
        AanVectorButterfly.Transpose8x8(ref m);

        StoreCoefficientRow(m.V0, output, 0 * 8);
        StoreCoefficientRow(m.V1, output, 1 * 8);
        StoreCoefficientRow(m.V2, output, 2 * 8);
        StoreCoefficientRow(m.V3, output, 3 * 8);
        StoreCoefficientRow(m.V4, output, 4 * 8);
        StoreCoefficientRow(m.V5, output, 5 * 8);
        StoreCoefficientRow(m.V6, output, 6 * 8);
        StoreCoefficientRow(m.V7, output, 7 * 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadShiftedRow(ReadOnlySpan<byte> input, int offset)
    {
        // Exactly 8 scalar reads, not a wide vector load: FrameEncoder's padded plane is an exactly-sized
        // array (not pooled, no slack past the last block), so a wider load on the last block/row would
        // read out of bounds.
        Span<float> row = stackalloc float[8];
        for (int x = 0; x < 8; x++)
        {
            row[x] = input[offset + x] - 128f;
        }

        return Vector256.LoadUnsafe(ref row[0]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCoefficientRow(Vector256<float> row, Span<double> output, int offset)
    {
        var scaled = row * Eighth;
        Span<float> values = stackalloc float[8];
        scaled.StoreUnsafe(ref values[0]);
        for (int i = 0; i < 8; i++)
        {
            output[offset + i] = values[i];
        }
    }
}
