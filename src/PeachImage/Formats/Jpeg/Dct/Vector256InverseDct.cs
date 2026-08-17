using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// SIMD inverse DCT kernel using <see cref="Vector256{T}"/>'s cross-platform generic static API. Each
/// 8-element separable-transform dot product (see <see cref="ScalarInverseDct"/> for the underlying math)
/// fits in exactly one 256-bit register, so this needs a single <see cref="Vector256.Dot{T}"/> per output
/// sample instead of <see cref="Vector128InverseDct"/>'s two. Selected only when
/// <see cref="Vector256.IsHardwareAccelerated"/> (effectively: AVX/AVX2 present) so it's never chosen over
/// the 128-bit kernel on hardware where 256-bit width would just be software-emulated.
/// </summary>
internal sealed class Vector256InverseDct : IInverseDctKernel
{
    private static readonly Vector256<float>[] WeightedCosineRows = BuildRows();

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
            var rowVector = Vector256.LoadUnsafe(ref dequantized[v * 8]);
            for (int x = 0; x < 8; x++)
            {
                rowPass[(v * 8) + x] = Vector256.Dot(rowVector, WeightedCosineRows[x]) * 0.5f;
            }
        }

        Span<float> column = stackalloc float[8];
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

            var columnVector = Vector256.LoadUnsafe(ref column[0]);
            for (int y = 0; y < 8; y++)
            {
                float sum = Vector256.Dot(columnVector, WeightedCosineRows[y]);
                float pixel = (sum * 0.5f) + 128f;
                int rounded = (int)MathF.Round(pixel, MidpointRounding.AwayFromZero);
                output[(y * outputStride) + x] = (byte)Math.Clamp(rounded, 0, 255);
            }
        }
    }

    private static Vector256<float>[] BuildRows()
    {
        double[] normalization = [1.0 / 1.4142135623730951, 1, 1, 1, 1, 1, 1, 1];
        var rows = new Vector256<float>[8];

        for (int x = 0; x < 8; x++)
        {
            float[] row = new float[8];
            for (int u = 0; u < 8; u++)
            {
                row[u] = (float)(normalization[u] * Math.Cos((((2 * x) + 1) * u * Math.PI) / 16.0));
            }

            rows[x] = Vector256.Create((ReadOnlySpan<float>)row);
        }

        return rows;
    }
}
