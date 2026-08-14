using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>SIMD forward DCT kernel, the <see cref="Vector128{T}"/> counterpart of <see cref="Vector128InverseDct"/>.</summary>
internal sealed class Vector128ForwardDct : IForwardDctKernel
{
    private static readonly (Vector128<float> Lo, Vector128<float> Hi)[] CosineRows = BuildRows();
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
            var lo = Vector128.Create(shifted[(y * 8) + 0], shifted[(y * 8) + 1], shifted[(y * 8) + 2], shifted[(y * 8) + 3]);
            var hi = Vector128.Create(shifted[(y * 8) + 4], shifted[(y * 8) + 5], shifted[(y * 8) + 6], shifted[(y * 8) + 7]);

            for (int u = 0; u < 8; u++)
            {
                var (cosLo, cosHi) = CosineRows[u];
                rowPass[(y * 8) + u] = Vector128.Dot(lo, cosLo) + Vector128.Dot(hi, cosHi);
            }
        }

        for (int u = 0; u < 8; u++)
        {
            var lo = Vector128.Create(rowPass[u], rowPass[8 + u], rowPass[16 + u], rowPass[24 + u]);
            var hi = Vector128.Create(rowPass[32 + u], rowPass[40 + u], rowPass[48 + u], rowPass[56 + u]);

            for (int v = 0; v < 8; v++)
            {
                var (cosLo, cosHi) = CosineRows[v];
                float sum = Vector128.Dot(lo, cosLo) + Vector128.Dot(hi, cosHi);
                output[(v * 8) + u] = 0.25 * Normalization[u] * Normalization[v] * sum;
            }
        }
    }

    private static (Vector128<float> Lo, Vector128<float> Hi)[] BuildRows()
    {
        var rows = new (Vector128<float>, Vector128<float>)[8];
        for (int freq = 0; freq < 8; freq++)
        {
            float[] row = new float[8];
            for (int spatial = 0; spatial < 8; spatial++)
            {
                row[spatial] = (float)Math.Cos((((2 * spatial) + 1) * freq * Math.PI) / 16.0);
            }

            rows[freq] = (
                Vector128.Create(row[0], row[1], row[2], row[3]),
                Vector128.Create(row[4], row[5], row[6], row[7]));
        }

        return rows;
    }
}
