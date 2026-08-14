using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// SIMD inverse DCT kernel using <see cref="Vector128{T}"/>'s cross-platform generic static API — the same
/// separable-transform math as <see cref="ScalarInverseDct"/>, just computed as 4-wide dot products via
/// <see cref="Vector128.Dot{T}"/>. One source file JITs to SSE2 on x86 and AdvSimd on Arm.
/// </summary>
internal sealed class Vector128InverseDct : IInverseDctKernel
{
    private static readonly (Vector128<float> Lo, Vector128<float> Hi)[] WeightedCosineRows = BuildRows();

    public void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> dequantTable, Span<byte> output, int outputStride)
    {
        Span<float> dequantized = stackalloc float[64];
        for (int i = 0; i < 64; i++)
        {
            dequantized[i] = coefficients[i] * dequantTable[i];
        }

        Span<float> rowPass = stackalloc float[64];
        for (int v = 0; v < 8; v++)
        {
            var lo = Vector128.Create(dequantized[(v * 8) + 0], dequantized[(v * 8) + 1], dequantized[(v * 8) + 2], dequantized[(v * 8) + 3]);
            var hi = Vector128.Create(dequantized[(v * 8) + 4], dequantized[(v * 8) + 5], dequantized[(v * 8) + 6], dequantized[(v * 8) + 7]);

            for (int x = 0; x < 8; x++)
            {
                var (weightedLo, weightedHi) = WeightedCosineRows[x];
                float sum = Vector128.Dot(lo, weightedLo) + Vector128.Dot(hi, weightedHi);
                rowPass[(v * 8) + x] = sum * 0.5f;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            var lo = Vector128.Create(rowPass[x], rowPass[8 + x], rowPass[16 + x], rowPass[24 + x]);
            var hi = Vector128.Create(rowPass[32 + x], rowPass[40 + x], rowPass[48 + x], rowPass[56 + x]);

            for (int y = 0; y < 8; y++)
            {
                var (weightedLo, weightedHi) = WeightedCosineRows[y];
                float sum = Vector128.Dot(lo, weightedLo) + Vector128.Dot(hi, weightedHi);
                float pixel = (sum * 0.5f) + 128f;
                int rounded = (int)MathF.Round(pixel, MidpointRounding.AwayFromZero);
                output[(y * outputStride) + x] = (byte)Math.Clamp(rounded, 0, 255);
            }
        }
    }

    private static (Vector128<float> Lo, Vector128<float> Hi)[] BuildRows()
    {
        double[] normalization = [1.0 / 1.4142135623730951, 1, 1, 1, 1, 1, 1, 1];
        var rows = new (Vector128<float>, Vector128<float>)[8];

        for (int x = 0; x < 8; x++)
        {
            float[] row = new float[8];
            for (int u = 0; u < 8; u++)
            {
                row[u] = (float)(normalization[u] * Math.Cos((((2 * x) + 1) * u * Math.PI) / 16.0));
            }

            rows[x] = (
                Vector128.Create(row[0], row[1], row[2], row[3]),
                Vector128.Create(row[4], row[5], row[6], row[7]));
        }

        return rows;
    }
}
