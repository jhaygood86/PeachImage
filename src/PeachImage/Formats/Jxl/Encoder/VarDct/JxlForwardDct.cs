namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// The forward 8x8 DCT in the decoder's convention (see <c>DctTransforms</c>): <c>c[k] = 1/N * sum x[n] B[n][k]</c> with
/// <c>B[n][0] = 1</c> and <c>B[n][k] = sqrt(2) cos((n + 1/2) k pi / N)</c>, applied along both axes. Coefficient
/// <c>(kx, ky)</c> is stored at <c>kx * 8 + ky</c>, which is the layout of a square block's coefficients.
/// </summary>
internal static class JxlForwardDct
{
    private const int N = 8;

    // Basis[n * N + k].
    private static readonly float[] Basis = BuildBasis();

    /// <summary>
    /// Transforms the 8x8 block of <paramref name="plane"/> whose top-left sample is at <paramref name="offset"/>
    /// (row stride <paramref name="stride"/>) into <paramref name="coefficients"/> (64 values).
    /// </summary>
    public static void Transform(ReadOnlySpan<float> plane, int offset, int stride, Span<float> coefficients)
    {
        // Pass 1, along y for every column: t[x * N + ky] = 1/N * sum_y B[y][ky] * p[y][x].
        Span<float> t = stackalloc float[N * N];
        for (int x = 0; x < N; x++)
        {
            for (int ky = 0; ky < N; ky++)
            {
                float sum = 0;
                for (int y = 0; y < N; y++)
                {
                    sum += Basis[(y * N) + ky] * plane[offset + (y * stride) + x];
                }

                t[(x * N) + ky] = sum * (1f / N);
            }
        }

        // Pass 2, along x: c[kx * N + ky] = 1/N * sum_x B[x][kx] * t[x][ky].
        for (int kx = 0; kx < N; kx++)
        {
            for (int ky = 0; ky < N; ky++)
            {
                float sum = 0;
                for (int x = 0; x < N; x++)
                {
                    sum += Basis[(x * N) + kx] * t[(x * N) + ky];
                }

                coefficients[(kx * N) + ky] = sum * (1f / N);
            }
        }
    }

    private static float[] BuildBasis()
    {
        var basis = new float[N * N];
        for (int n = 0; n < N; n++)
        {
            for (int k = 0; k < N; k++)
            {
                basis[(n * N) + k] = k == 0 ? 1f : (float)(Math.Sqrt(2.0) * Math.Cos((n + 0.5) * k * Math.PI / N));
            }
        }

        return basis;
    }
}
