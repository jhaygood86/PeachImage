using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// Whole-frame float post-processing for XYB-coded frames, in the reference decoder's order: Gaborish smoothing, the
/// edge-preserving filter (up to three passes), conversion from the XYB opsin space to linear RGB, and the output transfer function.
/// Planes are <c>stride</c> floats wide; filters mirror at the edges of the real <c>width x height</c> image.
/// </summary>
internal static class FramePostProcessing
{
    /// <summary>Constant from the format: scales the quantization field into the EPF sigma.</summary>
    public const float InverseSigmaNumerator = -1.1715728752538099024f;

    private const float MinSigma = -3.90524291751269967465540850526868f;

    /// <summary>EPF skips pixels whose inverse sigma is below this.</summary>
    internal const float MinSigmaValue = MinSigma;

    // Reflects an out-of-range index back into [0, size), duplicating the edge sample (symmetric extension).
    internal static int Mirror(int i, int size)
    {
        while (i < 0 || i >= size)
        {
            i = i < 0 ? -i - 1 : (2 * size) - i - 1;
        }

        return i;
    }

    /// <summary>Applies the Gaborish 3x3 smoothing to the three planes in place.</summary>
    internal static void GaborishReference(float[][] planes, int stride, int width, int height, JxlLoopFilter filter)
    {
        var temp = new float[planes[0].Length];
        for (int c = 0; c < 3; c++)
        {
            float w1 = filter.GabWeights[c * 2];
            float w2 = filter.GabWeights[(c * 2) + 1];
            float norm = 1.0f / (1.0f + (4 * (w1 + w2)));
            float centre = 1.0f * norm;
            w1 *= norm;
            w2 *= norm;
            var src = planes[c];
            for (int y = 0; y < height; y++)
            {
                int yt = Mirror(y - 1, height) * stride;
                int ym = y * stride;
                int yb = Mirror(y + 1, height) * stride;
                for (int x = 0; x < width; x++)
                {
                    int xl = Mirror(x - 1, width);
                    int xr = Mirror(x + 1, width);
                    float sum1 = src[ym + xl] + src[ym + xr] + src[yt + x] + src[yb + x];
                    float sum2 = src[yt + xl] + src[yt + xr] + src[yb + xl] + src[yb + xr];
                    temp[ym + x] = (sum2 * w2) + ((sum1 * w1) + (src[ym + x] * centre));
                }
            }

            // Rows are copied back whole; the padding beyond `width` keeps its old (unused) values.
            for (int y = 0; y < height; y++)
            {
                Array.Copy(temp, y * stride, src, y * stride, width);
            }
        }
    }

    /// <summary>
    /// Builds the per-8x8-block inverse sigma map for a VarDCT frame (negative; the reciprocal of the filter strength).
    /// </summary>
    public static float[] SigmaFromQuantField(JxlLoopFilter filter, float quantScale, int blocksX, int blocksY, int[] rawQuantField, byte[] acStrategy, byte[] sharpness)
    {
        var sigma = new float[blocksX * blocksY];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                byte entry = acStrategy[(by * blocksX) + bx];
                if ((entry & 1) == 0)
                {
                    continue;
                }

                int strategy = entry >> 1;
                int cx = AcStrategy.CoveredBlocksX(strategy);
                int cy = AcStrategy.CoveredBlocksY(strategy);
                float sigmaQuant = filter.EpfQuantMul / (quantScale * rawQuantField[(by * blocksX) + bx] * InverseSigmaNumerator);
                for (int iy = 0; iy < cy; iy++)
                {
                    for (int ix = 0; ix < cx; ix++)
                    {
                        int index = ((by + iy) * blocksX) + bx + ix;
                        float s = sigmaQuant * filter.EpfSharpLut[sharpness[index]];
                        s = Math.Min(-1e-4f, s);
                        sigma[index] = 1.0f / s;
                    }
                }
            }
        }

        return sigma;
    }

    /// <summary>The constant inverse sigma Modular frames use for every block.</summary>
    public static float[] ConstantSigma(JxlLoopFilter filter, int blocksX, int blocksY)
    {
        var sigma = new float[blocksX * blocksY];
        Array.Fill(sigma, InverseSigmaNumerator / filter.EpfSigmaForModular);
        return sigma;
    }

    /// <summary>Runs the edge-preserving filter passes selected by <c>epf_iters</c> over the three planes, in place.</summary>
    internal static void EdgePreservingFilterReference(float[][] planes, int stride, int width, int height, JxlLoopFilter filter, float[] sigma, int blocksX)
    {
        int iterations = filter.EpfIterations;
        if (iterations >= 3)
        {
            EpfReference(planes, stride, width, height, filter, sigma, blocksX, 0);
        }

        if (iterations >= 1)
        {
            EpfReference(planes, stride, width, height, filter, sigma, blocksX, 1);
        }

        if (iterations >= 2)
        {
            EpfReference(planes, stride, width, height, filter, sigma, blocksX, 2);
        }
    }

    // Neighbour offsets (dy, dx) whose similarity is measured, per pass.
    private static readonly (int Dy, int Dx)[] Neighbours0 =
    [
        (-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0),
    ];

    private static readonly (int Dy, int Dx)[] Neighbours1 = [(-1, 0), (0, -1), (0, 1), (1, 0)];

    private static readonly (int Dy, int Dx)[] PlusShape = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    private static void EpfReference(float[][] planes, int stride, int width, int height, JxlLoopFilter filter, float[] sigma, int blocksX, int pass)
    {
        var output = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            output[c] = new float[planes[c].Length];
        }

        float baseScale = pass switch
        {
            0 => filter.EpfPass0SigmaScale * 1.65f,
            1 => 1.65f,
            _ => filter.EpfPass2SigmaScale * 1.65f,
        };
        float borderScale = baseScale * filter.EpfBorderSadMul;
        var neighbours = pass == 0 ? Neighbours0 : Neighbours1;
        var scale = filter.EpfChannelScale;

        // Mirrored coordinate tables avoid repeated edge handling in the inner loops.
        int[] xs = new int[width + 8];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = Mirror(i - 4, width);
        }

        int[] ys = new int[height + 8];
        for (int i = 0; i < ys.Length; i++)
        {
            ys[i] = Mirror(i - 4, height);
        }

        for (int y = 0; y < height; y++)
        {
            bool borderRow = (y & 7) == 0 || (y & 7) == 7;
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + x;
                float rowSigma = sigma[((y >> 3) * blocksX) + (x >> 3)];
                if (rowSigma < MinSigma)
                {
                    output[0][index] = planes[0][index];
                    output[1][index] = planes[1][index];
                    output[2][index] = planes[2][index];
                    continue;
                }

                int ix = x & 7;
                float sadMul = borderRow || ix == 0 || ix == 7 ? borderScale : baseScale;
                float invSigma = rowSigma * sadMul;

                float weightSum = 1f;
                float accX = planes[0][index];
                float accY = planes[1][index];
                float accB = planes[2][index];

                if (pass == 2)
                {
                    float cx = planes[0][index];
                    float cy = planes[1][index];
                    float cb = planes[2][index];
                    foreach (var (dy, dx) in neighbours)
                    {
                        int ni = (ys[y + dy + 4] * stride) + xs[x + dx + 4];
                        float nx = planes[0][ni];
                        float ny = planes[1][ni];
                        float nb = planes[2][ni];
                        float s = (MathF.Abs(nx - cx) * scale[0]) + (MathF.Abs(ny - cy) * scale[1]) + (MathF.Abs(nb - cb) * scale[2]);
                        float weight = MathF.Max(0f, (s * invSigma) + 1f);
                        weightSum += weight;
                        accX += weight * nx;
                        accY += weight * ny;
                        accB += weight * nb;
                    }
                }
                else
                {
                    for (int n = 0; n < neighbours.Length; n++)
                    {
                        var (sdy, sdx) = neighbours[n];
                        float total = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            var plane = planes[c];
                            float channelSad = 0;
                            foreach (var (ody, odx) in PlusShape)
                            {
                                float a = plane[(ys[y + ody + 4] * stride) + xs[x + odx + 4]];
                                float b = plane[(ys[y + sdy + ody + 4] * stride) + xs[x + sdx + odx + 4]];
                                channelSad += MathF.Abs(a - b);
                            }

                            total += channelSad * scale[c];
                        }

                        int ni = (ys[y + sdy + 4] * stride) + xs[x + sdx + 4];
                        float weight = MathF.Max(0f, (total * invSigma) + 1f);
                        weightSum += weight;
                        accX += weight * planes[0][ni];
                        accY += weight * planes[1][ni];
                        accB += weight * planes[2][ni];
                    }
                }

                float inv = 1.0f / weightSum;
                output[0][index] = accX * inv;
                output[1][index] = accY * inv;
                output[2][index] = accB * inv;
            }
        }

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < height; y++)
            {
                Array.Copy(output[c], y * stride, planes[c], y * stride, width);
            }
        }
    }

    /// <summary>
    /// Converts XYB opsin samples to linear RGB in place, using the (possibly custom) inverse opsin matrix scaled for the
    /// image's intensity target. For grayscale output all three planes receive the luminance.
    /// </summary>
    internal static void XybToLinearRgbReference(float[][] planes, int stride, int width, int height, JxlCustomTransformData transform, float intensityTarget, bool gray)
    {
        float[] biases = transform.OpsinBiases;
        float cbrtR = MathF.Cbrt(biases[0]);
        float cbrtG = MathF.Cbrt(biases[1]);
        float cbrtB = MathF.Cbrt(biases[2]);
        float scale = 255.0f / intensityTarget;
        var m = new float[9];
        for (int i = 0; i < 9; i++)
        {
            m[i] = transform.InverseMatrix[i] * scale;
        }

        if (gray)
        {
            // sRGB luminances applied to the matrix rows: every output channel becomes Y.
            float[] luminances = [0.2126f, 0.7152f, 0.0722f];
            var gm = new float[9];
            for (int j = 0; j < 3; j++)
            {
                for (int i = 0; i < 3; i++)
                {
                    gm[(j * 3) + i] = (luminances[0] * m[i]) + (luminances[1] * m[3 + i]) + (luminances[2] * m[6 + i]);
                }
            }

            m = gm;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                float optX = planes[0][i];
                float optY = planes[1][i];
                float optB = planes[2][i];

                float gammaR = optY + optX - cbrtR;
                float gammaG = optY - optX - cbrtG;
                float gammaB = optB - cbrtB;
                float mixedR = (gammaR * gammaR * gammaR) + biases[0];
                float mixedG = (gammaG * gammaG * gammaG) + biases[1];
                float mixedB = (gammaB * gammaB * gammaB) + biases[2];

                planes[0][i] = (m[0] * mixedR) + (m[1] * mixedG) + (m[2] * mixedB);
                planes[1][i] = (m[3] * mixedR) + (m[4] * mixedG) + (m[5] * mixedB);
                planes[2][i] = (m[6] * mixedR) + (m[7] * mixedG) + (m[8] * mixedB);
            }
        }
    }

    /// <summary>Applies the output transfer function (linear light to the encoded signal) to the three planes in place.</summary>
    internal static void LinearToTransferReference(float[][] planes, int stride, int width, int height, JxlColorEncoding encoding)
    {
        Func<float, float>? curve = SelectCurve(encoding);
        if (curve is null)
        {
            return; // Linear output.
        }

        for (int c = 0; c < 3; c++)
        {
            var plane = planes[c];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    plane[row + x] = curve(plane[row + x]);
                }
            }
        }
    }

    internal static Func<float, float>? SelectCurve(JxlColorEncoding encoding)
    {
        if (encoding.HaveGamma)
        {
            // The stored gamma is already the exponent applied to linear light.
            float inverseGamma = (float)(encoding.Gamma / (double)JxlColorEncoding.GammaMultiplier);
            return v => v <= 1e-5f ? 0f : MathF.Pow(v, inverseGamma);
        }

        switch (encoding.TransferFunction)
        {
            case JxlTransferFunction.Linear:
                return null;
            case JxlTransferFunction.Srgb:
                return v =>
                {
                    float a = MathF.Abs(v);
                    float encoded = a <= 0.0031308f ? 12.92f * a : (1.055f * MathF.Pow(a, 1.0f / 2.4f)) - 0.055f;
                    return MathF.CopySign(encoded, v);
                };
            case JxlTransferFunction.Bt709:
                return v =>
                {
                    float a = MathF.Abs(v);
                    float encoded = a < 0.018053968510807f ? 4.5f * a : (1.09929682680944f * MathF.Pow(a, 0.45f)) - 0.09929682680944f;
                    return MathF.CopySign(encoded, v);
                };
            case JxlTransferFunction.Dci:
                return v => v <= 1e-5f ? 0f : MathF.Pow(v, 1.0f / 2.6f);
            default:
                throw new JxlUnsupportedFeatureException($"The {encoding.TransferFunction} transfer function is not supported yet.");
        }
    }
}
