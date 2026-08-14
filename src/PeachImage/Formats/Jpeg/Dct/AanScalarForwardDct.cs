namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// True minimal-multiply AAN (Arai-Agui-Nakajima) forward DCT kernel — 5 multiplies per 1D pass (vs.
/// <see cref="FastScalarForwardDct"/>'s 21), by letting each output frequency carry the fixed
/// <see cref="AanScaleFactors.OneDimensional"/> scale factor relative to the true DCT coefficient instead
/// of correcting for it inside the transform. The odd-branch butterfly wiring (the piece that could not be
/// safely re-derived from the DCT-IV definition alone — see issue #5) is transcribed from libjpeg-turbo's
/// <c>jfdctflt.c</c> (<c>jpeg_fdct_float</c>); see THIRD-PARTY-LICENSES.md for attribution.
/// </summary>
/// <remarks>
/// Raw output must be divided by an <see cref="AanScaleFactors.BuildForwardQuantTable"/>-scaled
/// quantization table — not the raw one — to be numerically equivalent to <see cref="ScalarForwardDct"/>.
/// The final <c>0.125</c> multiply below is not part of the classical AAN flowgraph itself; it's the
/// overall 2D descale that falls out of comparing this network's raw gain against the direct-definition
/// sum's, the same way libjpeg-turbo folds an equivalent constant into its own quantization-table setup
/// rather than the hot per-block path.
/// </remarks>
internal sealed class AanScalarForwardDct : IForwardDctKernel
{
    private static readonly double C2 = Math.Cos(2 * Math.PI / 16.0);
    private static readonly double C4 = Math.Cos(4 * Math.PI / 16.0);
    private static readonly double C6 = Math.Cos(6 * Math.PI / 16.0);
    private static readonly double C2MinusC6 = C2 - C6;
    private static readonly double C2PlusC6 = C2 + C6;

    public double[] PrepareQuantTable(ReadOnlySpan<ushort> quantTable) => AanScaleFactors.BuildForwardQuantTable(quantTable);

    public void Transform(ReadOnlySpan<byte> input, int inputStride, Span<double> output)
    {
        Span<double> shifted = stackalloc double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                shifted[(y * 8) + x] = input[(y * inputStride) + x] - 128.0;
            }
        }

        Span<double> rowPass = stackalloc double[64];
        Span<double> stage = stackalloc double[8];
        for (int y = 0; y < 8; y++)
        {
            Forward1D(shifted.Slice(y * 8, 8), stage);
            stage.CopyTo(rowPass.Slice(y * 8, 8));
        }

        Span<double> column = stackalloc double[8];
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

            Forward1D(column, stage);
            for (int v = 0; v < 8; v++)
            {
                output[(v * 8) + u] = stage[v] * 0.125;
            }
        }
    }

    /// <summary>
    /// Computes one 1D AAN forward pass: 2 multiplies for the even (0,2,4,6) branch, 3 for the odd (1,3,5,7)
    /// branch — the odd branch's <c>z5</c> term is shared between the <c>z2</c>/<c>z4</c> outputs rather
    /// than recomputed, which is the specific trick that makes 3 multiplies suffice for what looks like a
    /// 4-term rotation.
    /// </summary>
    private static void Forward1D(ReadOnlySpan<double> f, Span<double> result)
    {
        double s0 = f[0] + f[7], d0 = f[0] - f[7];
        double s1 = f[1] + f[6], d1 = f[1] - f[6];
        double s2 = f[2] + f[5], d2 = f[2] - f[5];
        double s3 = f[3] + f[4], d3 = f[3] - f[4];

        double t10 = s0 + s3, t13 = s0 - s3;
        double t11 = s1 + s2, t12 = s1 - s2;

        result[0] = t10 + t11;
        result[4] = t10 - t11;

        double z1 = (t12 + t13) * C4;
        result[2] = t13 + z1;
        result[6] = t13 - z1;

        double o10 = d3 + d2;
        double o11 = d2 + d1;
        double o12 = d1 + d0;

        double z5 = (o10 - o12) * C6;
        double z2 = (o10 * C2MinusC6) + z5;
        double z4 = (o12 * C2PlusC6) + z5;
        double z3 = o11 * C4;

        double z11 = d0 + z3;
        double z13 = d0 - z3;

        result[1] = z11 + z4;
        result[3] = z13 - z2;
        result[5] = z13 + z2;
        result[7] = z11 - z4;
    }
}
