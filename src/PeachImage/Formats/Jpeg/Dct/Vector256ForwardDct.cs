using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>SIMD forward DCT kernel, the <see cref="Vector256{T}"/> counterpart of <see cref="Vector256InverseDct"/>.</summary>
internal sealed class Vector256ForwardDct : IForwardDctKernel
{
    private static readonly Vector256<float>[] CosineRows = BuildRows();
    private static readonly float[] Normalization = [1f / 1.4142135f, 1, 1, 1, 1, 1, 1, 1];

    public void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output)
    {
        Span<float> shifted = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                shifted[(y * 8) + x] = input[(y * inputStride) + x] - 128f;
            }
        }

        Span<float> rowPass = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            var rowVector = Vector256.LoadUnsafe(ref shifted[y * 8]);
            for (int u = 0; u < 8; u++)
            {
                rowPass[(y * 8) + u] = Vector256.Dot(rowVector, CosineRows[u]);
            }
        }

        Span<float> column = stackalloc float[8];
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

            var columnVector = Vector256.LoadUnsafe(ref column[0]);
            for (int v = 0; v < 8; v++)
            {
                float sum = Vector256.Dot(columnVector, CosineRows[v]);
                output[(v * 8) + u] = 0.25 * Normalization[u] * Normalization[v] * sum;
            }
        }
    }

    private static Vector256<float>[] BuildRows()
    {
        var rows = new Vector256<float>[8];
        for (int freq = 0; freq < 8; freq++)
        {
            float[] row = new float[8];
            for (int spatial = 0; spatial < 8; spatial++)
            {
                row[spatial] = (float)Math.Cos((((2 * spatial) + 1) * freq * Math.PI) / 16.0);
            }

            rows[freq] = Vector256.Create((ReadOnlySpan<float>)row);
        }

        return rows;
    }
}
