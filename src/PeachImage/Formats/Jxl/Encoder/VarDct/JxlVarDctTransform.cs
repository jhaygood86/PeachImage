using System.Collections.Concurrent;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// The forward side of the DCT-family block transforms the encoder uses (<see cref="AcStrategyType.Dct"/>,
/// <see cref="AcStrategyType.Dct16x16"/>, <see cref="AcStrategyType.Dct32x32"/> and the 16x8 / 8x16 / 32x16 / 16x32 rectangles),
/// in the decoder's convention (see <c>DctTransforms</c>): <c>c[k] = 1/N * sum x[n] B[n][k]</c> with <c>B[n][0] = 1</c> and
/// <c>B[n][k] = sqrt(2) cos((n + 1/2) k pi / N)</c> along each axis. Coefficients are stored with the smaller dimension as rows:
/// a block taller than or as tall as it is wide is indexed <c>[horizontal frequency][vertical frequency]</c>, a wider one
/// <c>[vertical][horizontal]</c>.
/// </summary>
internal static class JxlVarDctTransform
{
    private static readonly ConcurrentDictionary<int, float[]> Bases = new();

    /// <summary>The strategies the encoder can choose, with the 8x8 DCT first.</summary>
    public static ReadOnlySpan<int> Supported =>
    [
        (int)AcStrategyType.Dct,
        (int)AcStrategyType.Dct16x16,
        (int)AcStrategyType.Dct32x32,
        (int)AcStrategyType.Dct16x8,
        (int)AcStrategyType.Dct8x16,
        (int)AcStrategyType.Dct32x16,
        (int)AcStrategyType.Dct16x32,
    ];

    /// <summary>The block's height in pixels.</summary>
    public static int Rows(int strategy) => AcStrategy.CoveredBlocksY(strategy) * 8;

    /// <summary>The block's width in pixels.</summary>
    public static int Cols(int strategy) => AcStrategy.CoveredBlocksX(strategy) * 8;

    /// <summary>
    /// Transforms the block of <paramref name="plane"/> whose top-left sample is at <paramref name="offset"/> (row stride
    /// <paramref name="stride"/>) into <paramref name="coefficients"/>, which holds <c>rows * cols</c> values.
    /// </summary>
    public static void Forward(int strategy, ReadOnlySpan<float> plane, int offset, int stride, Span<float> coefficients, Span<float> scratch)
    {
        int rows = Rows(strategy);
        int cols = Cols(strategy);
        float[] basisRows = Basis(rows);
        float[] basisCols = Basis(cols);

        // Pass 1, along y for every column: t[ky * cols + x] = 1/R * sum_y B_R[y][ky] * p[y][x].
        var t = scratch[..(rows * cols)];
        float invRows = 1f / rows;
        float invCols = 1f / cols;
        for (int ky = 0; ky < rows; ky++)
        {
            var row = t.Slice(ky * cols, cols);
            row.Clear();
            for (int y = 0; y < rows; y++)
            {
                float b = basisRows[(y * rows) + ky] * invRows;
                var source = plane.Slice(offset + (y * stride), cols);
                for (int x = 0; x < cols; x++)
                {
                    row[x] += b * source[x];
                }
            }
        }

        // Pass 2, along x: c[ky][kx] = 1/C * sum_x B_C[x][kx] * t[ky][x], stored in the decoder's layout.
        bool wide = rows < cols;
        for (int ky = 0; ky < rows; ky++)
        {
            var row = t.Slice(ky * cols, cols);
            for (int kx = 0; kx < cols; kx++)
            {
                float sum = 0;
                for (int x = 0; x < cols; x++)
                {
                    sum += basisCols[(x * cols) + kx] * row[x];
                }

                coefficients[wide ? (ky * cols) + kx : (kx * rows) + ky] = sum * invCols;
            }
        }
    }

    /// <summary>
    /// Finds the DC values (one per covered 8x8 block, row-major <c>coveredY x coveredX</c>) whose lowest-frequency expansion in
    /// the decoder (<c>DctTransforms.LowestFrequenciesFromDc</c>) equals the block's own lowest-frequency coefficients, so the
    /// DC image carries exactly what the large transform leaves out of its coded coefficients.
    /// </summary>
    public static void LowestFrequenciesToDc(int strategy, ReadOnlySpan<float> coefficients, Span<float> dc)
    {
        int cx = AcStrategy.CoveredBlocksX(strategy);
        int cy = AcStrategy.CoveredBlocksY(strategy);
        if (cx * cy == 1)
        {
            dc[0] = coefficients[0];
            return;
        }

        int rows = cy * 8;
        int cols = cx * 8;
        bool wide = rows < cols;
        int stride = Math.Max(rows, cols);

        // Undo the decoder's resampling to get the small DCT of the DC image, indexed [ky][kx].
        Span<float> small = stackalloc float[cx * cy];
        for (int ky = 0; ky < cy; ky++)
        {
            for (int kx = 0; kx < cx; kx++)
            {
                float c = wide ? coefficients[(ky * stride) + kx] : coefficients[(kx * stride) + ky];
                small[(ky * cx) + kx] = c / (DctTransforms.ResampleScale(ky, rows) * DctTransforms.ResampleScale(kx, cols));
            }
        }

        // The decoder transforms the DC image forward with the same convention, so the DC values are its inverse transform.
        float[] basisY = Basis(cy);
        float[] basisX = Basis(cx);
        for (int y = 0; y < cy; y++)
        {
            for (int x = 0; x < cx; x++)
            {
                float sum = 0;
                for (int ky = 0; ky < cy; ky++)
                {
                    for (int kx = 0; kx < cx; kx++)
                    {
                        sum += small[(ky * cx) + kx] * basisY[(y * cy) + ky] * basisX[(x * cx) + kx];
                    }
                }

                dc[(y * cx) + x] = sum;
            }
        }
    }

    // B[n * size + k].
    private static float[] Basis(int size) => Bases.GetOrAdd(size, static n =>
    {
        var basis = new float[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < n; k++)
            {
                basis[(i * n) + k] = k == 0 ? 1f : (float)(Math.Sqrt(2.0) * Math.Cos((i + 0.5) * k * Math.PI / n));
            }
        }

        return basis;
    });
}
