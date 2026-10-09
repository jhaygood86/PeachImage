using System.Numerics;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// The per-pixel colour stages that run on every decoded XYB frame -- XYB to linear RGB and the output transfer function -- as
/// row-parallel <see cref="Vector{T}"/> kernels. The scalar versions in <see cref="FramePostProcessing"/> are the reference.
/// </summary>
internal static class ColorKernels
{
    /// <summary>Converts XYB opsin samples to linear RGB in place (see <see cref="FramePostProcessing.XybToLinearRgbReference"/>).</summary>
    public static void XybToLinearRgb(float[][] planes, int stride, int width, int height, JxlCustomTransformData transform, float intensityTarget, bool gray, bool vectorized = true, float[]? srgbToEncoding = null)
    {
        float[] biases = transform.OpsinBiases;
        float cbrtR = MathF.Cbrt(biases[0]);
        float cbrtG = MathF.Cbrt(biases[1]);
        float cbrtB = MathF.Cbrt(biases[2]);
        float scale = 255.0f / intensityTarget;
        float[] inverse = transform.InverseMatrix;
        if (srgbToEncoding is not null && !gray)
        {
            // Output in other primaries: fold the sRGB -> encoding matrix into the opsin inverse matrix.
            inverse = new float[9];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    inverse[(r * 3) + c] = (srgbToEncoding[r * 3] * transform.InverseMatrix[c]) + (srgbToEncoding[(r * 3) + 1] * transform.InverseMatrix[3 + c]) + (srgbToEncoding[(r * 3) + 2] * transform.InverseMatrix[6 + c]);
                }
            }
        }

        var m = new float[9];
        for (int i = 0; i < 9; i++)
        {
            m[i] = inverse[i] * scale;
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

        RowParallel.For(height, y =>
        {
            int row = y * stride;
            float[] p0 = planes[0];
            float[] p1 = planes[1];
            float[] p2 = planes[2];
            int x = 0;
            if (vectorized && Vector.IsHardwareAccelerated)
            {
                var vcR = new Vector<float>(cbrtR);
                var vcG = new Vector<float>(cbrtG);
                var vcB = new Vector<float>(cbrtB);
                var vbR = new Vector<float>(biases[0]);
                var vbG = new Vector<float>(biases[1]);
                var vbB = new Vector<float>(biases[2]);
                var m0 = new Vector<float>(m[0]);
                var m1 = new Vector<float>(m[1]);
                var m2 = new Vector<float>(m[2]);
                var m3 = new Vector<float>(m[3]);
                var m4 = new Vector<float>(m[4]);
                var m5 = new Vector<float>(m[5]);
                var m6 = new Vector<float>(m[6]);
                var m7 = new Vector<float>(m[7]);
                var m8 = new Vector<float>(m[8]);
                for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                {
                    int i = row + x;
                    var optX = new Vector<float>(p0, i);
                    var optY = new Vector<float>(p1, i);
                    var optB = new Vector<float>(p2, i);
                    var gammaR = optY + optX - vcR;
                    var gammaG = optY - optX - vcG;
                    var gammaB = optB - vcB;
                    var mixedR = (gammaR * gammaR * gammaR) + vbR;
                    var mixedG = (gammaG * gammaG * gammaG) + vbG;
                    var mixedB = (gammaB * gammaB * gammaB) + vbB;
                    ((m0 * mixedR) + (m1 * mixedG) + (m2 * mixedB)).CopyTo(p0, i);
                    ((m3 * mixedR) + (m4 * mixedG) + (m5 * mixedB)).CopyTo(p1, i);
                    ((m6 * mixedR) + (m7 * mixedG) + (m8 * mixedB)).CopyTo(p2, i);
                }
            }

            for (; x < width; x++)
            {
                int i = row + x;
                float optX = p0[i];
                float optY = p1[i];
                float optB = p2[i];
                float gammaR = optY + optX - cbrtR;
                float gammaG = optY - optX - cbrtG;
                float gammaB = optB - cbrtB;
                float mixedR = (gammaR * gammaR * gammaR) + biases[0];
                float mixedG = (gammaG * gammaG * gammaG) + biases[1];
                float mixedB = (gammaB * gammaB * gammaB) + biases[2];
                p0[i] = (m[0] * mixedR) + (m[1] * mixedG) + (m[2] * mixedB);
                p1[i] = (m[3] * mixedR) + (m[4] * mixedG) + (m[5] * mixedB);
                p2[i] = (m[6] * mixedR) + (m[7] * mixedG) + (m[8] * mixedB);
            }
        });
    }

    /// <summary>
    /// Converts planes holding (Cb, Y, Cr) -- Y centred on 128/255 -- to (R, G, B) in place with the full-range BT.601 matrix of JFIF.
    /// </summary>
    public static void YCbCrToRgb(float[][] planes, int stride, int width, int height)
    {
        const float c128 = 128f / 255f;
        const float crcr = 1.402f;
        const float cgcb = -0.114f * 1.772f / 0.587f;
        const float cgcr = -0.299f * 1.402f / 0.587f;
        const float cbcb = 1.772f;
        RowParallel.For(height, y =>
        {
            int row = y * stride;
            float[] p0 = planes[0];
            float[] p1 = planes[1];
            float[] p2 = planes[2];
            int x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var vc128 = new Vector<float>(c128);
                var vcrcr = new Vector<float>(crcr);
                var vcgcb = new Vector<float>(cgcb);
                var vcgcr = new Vector<float>(cgcr);
                var vcbcb = new Vector<float>(cbcb);
                for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                {
                    int i = row + x;
                    var yy = new Vector<float>(p1, i) + vc128;
                    var cb = new Vector<float>(p0, i);
                    var cr = new Vector<float>(p2, i);
                    ((vcrcr * cr) + yy).CopyTo(p0, i);
                    ((vcgcr * cr) + ((vcgcb * cb) + yy)).CopyTo(p1, i);
                    ((vcbcb * cb) + yy).CopyTo(p2, i);
                }
            }

            for (; x < width; x++)
            {
                int i = row + x;
                float yy = p1[i] + c128;
                float cb = p0[i];
                float cr = p2[i];
                p0[i] = (crcr * cr) + yy;
                p1[i] = (cgcr * cr) + ((cgcb * cb) + yy);
                p2[i] = (cbcb * cb) + yy;
            }
        });
    }

    // PQ (SMPTE ST 2084) and HLG (BT.2100) encode absolute display light; both are scalar and rare, so they are not vectorized.
    private static void HdrTransfer(float[][] planes, int stride, int width, int height, JxlColorEncoding encoding, float intensityTarget)
    {
        bool pq = encoding.TransferFunction == JxlTransferFunction.Pq;
        float[] luminances = JxlColorSpaceMath.Luminances(encoding);
        float exponent = 0;
        if (!pq)
        {
            // The HLG opto-optical transfer: display light is turned back into scene light before encoding.
            float gamma = (1f / 1.2f) * MathF.Pow(1.111f, -MathF.Log2(intensityTarget / 1000f));
            exponent = gamma - 1f;
        }

        bool applyOotf = !pq && (exponent < -0.01f || exponent > 0.01f);
        float toTenThousand = intensityTarget / 10000f;
        RowParallel.For(height, y =>
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                float r = planes[0][row + x];
                float g = planes[1][row + x];
                float b = planes[2][row + x];
                if (applyOotf)
                {
                    float luminance = (luminances[0] * r) + (luminances[1] * g) + (luminances[2] * b);
                    float ratio = MathF.Min(MathF.Pow(luminance, exponent), 1e9f);
                    r *= ratio;
                    g *= ratio;
                    b *= ratio;
                }

                planes[0][row + x] = pq ? Pq(r, toTenThousand) : Hlg(r);
                planes[1][row + x] = pq ? Pq(g, toTenThousand) : Hlg(g);
                planes[2][row + x] = pq ? Pq(b, toTenThousand) : Hlg(b);
            }
        });
    }

    private static float Pq(float x, float toTenThousand)
    {
        if (x == 0f)
        {
            return 0f;
        }

        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;
        double y = Math.Abs((double)x) * toTenThousand;
        double p = Math.Pow(y, m1);
        double e = Math.Pow((c1 + (c2 * p)) / (1.0 + (c3 * p)), m2);
        return MathF.CopySign((float)e, x);
    }

    private static float Hlg(float s)
    {
        if (s == 0f)
        {
            return 0f;
        }

        const double a = 0.17883277;
        const double b = 1 - (4 * a);
        const double c = 0.5599107295;
        double v = Math.Abs((double)s);
        double e = v <= 1.0 / 12.0 ? Math.Sqrt(3.0 * v) : (a * Math.Log((12 * v) - b)) + c;
        return MathF.CopySign((float)e, s);
    }

    private enum CurveKind
    {
        Gamma,
        Srgb,
        Bt709,
        Dci,
    }

    /// <summary>Applies the output transfer function (linear light to the encoded signal) to the three planes in place.</summary>
    public static void LinearToTransfer(float[][] planes, int stride, int width, int height, JxlColorEncoding encoding, bool vectorized = true, float intensityTarget = 255f)
    {
        if (!encoding.HaveGamma && encoding.TransferFunction is JxlTransferFunction.Pq or JxlTransferFunction.Hlg)
        {
            HdrTransfer(planes, stride, width, height, encoding, intensityTarget);
            return;
        }

        Func<float, float>? curve = FramePostProcessing.SelectCurve(encoding);
        if (curve is null)
        {
            return; // Linear output.
        }

        CurveKind kind;
        float exponent = 0;
        if (encoding.HaveGamma)
        {
            kind = CurveKind.Gamma;
            exponent = (float)(encoding.Gamma / (double)JxlColorEncoding.GammaMultiplier);
        }
        else
        {
            kind = encoding.TransferFunction switch
            {
                JxlTransferFunction.Srgb => CurveKind.Srgb,
                JxlTransferFunction.Bt709 => CurveKind.Bt709,
                _ => CurveKind.Dci,
            };
        }

        RowParallel.For(height * 3, index =>
        {
            float[] plane = planes[index / height];
            int row = (index % height) * stride;
            int x = 0;
            if (vectorized && Vector.IsHardwareAccelerated)
            {
                for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                {
                    Apply(kind, exponent, new Vector<float>(plane, row + x)).CopyTo(plane, row + x);
                }
            }

            for (; x < width; x++)
            {
                plane[row + x] = curve(plane[row + x]);
            }
        });
    }

    private static Vector<float> Apply(CurveKind kind, float gamma, Vector<float> v)
    {
        var zero = Vector<float>.Zero;
        switch (kind)
        {
            case CurveKind.Gamma:
                return Vector.ConditionalSelect(
                    Vector.LessThanOrEqual(v, new Vector<float>(1e-5f)),
                    zero,
                    VectorMath.Pow(Vector.Max(v, new Vector<float>(1e-5f)), gamma));
            case CurveKind.Dci:
                return Vector.ConditionalSelect(
                    Vector.LessThanOrEqual(v, new Vector<float>(1e-5f)),
                    zero,
                    VectorMath.Pow(Vector.Max(v, new Vector<float>(1e-5f)), 1.0f / 2.6f));
            case CurveKind.Srgb:
            {
                var a = Vector.Abs(v);
                var low = a * new Vector<float>(12.92f);
                var high = (new Vector<float>(1.055f) * VectorMath.Pow(Vector.Max(a, new Vector<float>(0.0031308f)), 1.0f / 2.4f)) - new Vector<float>(0.055f);
                var encoded = Vector.ConditionalSelect(Vector.LessThanOrEqual(a, new Vector<float>(0.0031308f)), low, high);
                return Vector.ConditionalSelect(Vector.LessThan(v, zero), -encoded, encoded);
            }

            default:
            {
                var a = Vector.Abs(v);
                var low = a * new Vector<float>(4.5f);
                var high = (new Vector<float>(1.09929682680944f) * VectorMath.Pow(Vector.Max(a, new Vector<float>(0.018053968510807f)), 0.45f)) - new Vector<float>(0.09929682680944f);
                var encoded = Vector.ConditionalSelect(Vector.LessThan(a, new Vector<float>(0.018053968510807f)), low, high);
                return Vector.ConditionalSelect(Vector.LessThan(v, zero), -encoded, encoded);
            }
        }
    }
}
