using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// The forward opsin transform: sRGB-encoded samples to the XYB space VarDCT codes. It inverts the decoder's
/// <c>ColorKernels.XybToLinearRgb</c> for the default inverse matrix and biases: linear RGB is mixed into three
/// cone-like responses by the inverse of the decoder's matrix, biased, cube-rooted, and recombined as X = (L - M) / 2,
/// Y = (L + M) / 2 and B = S.
/// </summary>
internal static class JxlForwardXyb
{
    // The decoder's bias is added after the cube; the encoder's mirror image is added before the cube root.
    private static readonly float Bias = -JxlCustomTransformData.DefaultOpsinBiases[0];

    private static readonly float CbrtBias = MathF.Cbrt(Bias);

    private static readonly float[] Mix = InvertDefaultInverseMatrix();

    /// <summary>
    /// Converts <paramref name="channels"/> (one plane for gray, three for RGB, each <c>width * height</c> samples of
    /// <paramref name="bitsPerSample"/> bits, sRGB transfer) into three XYB planes of <paramref name="paddedWidth"/> x
    /// <paramref name="paddedHeight"/>, replicating the last row and column into the padding.
    /// </summary>
    public static float[][] Convert(int[][] channels, int colorChannels, int width, int height, int bitsPerSample, bool isFloat, int paddedWidth, int paddedHeight)
    {
        float[]? lut = isFloat ? null : BuildLinearLut(bitsPerSample);
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new float[paddedWidth * paddedHeight];
        }

        int[] r = channels[0];
        int[] g = colorChannels == 3 ? channels[1] : channels[0];
        int[] b = colorChannels == 3 ? channels[2] : channels[0];
        RowParallel.For(paddedHeight, y =>
        {
            int sy = Math.Min(y, height - 1);
            int src = sy * width;
            int dst = y * paddedWidth;
            for (int x = 0; x < paddedWidth; x++)
            {
                int i = src + Math.Min(x, width - 1);
                float lr = lut is null ? SrgbToLinear(BitConverter.Int32BitsToSingle(r[i])) : lut[r[i]];
                float lg = lut is null ? SrgbToLinear(BitConverter.Int32BitsToSingle(g[i])) : lut[g[i]];
                float lb = lut is null ? SrgbToLinear(BitConverter.Int32BitsToSingle(b[i])) : lut[b[i]];
                float mixedL = (Mix[0] * lr) + (Mix[1] * lg) + (Mix[2] * lb);
                float mixedM = (Mix[3] * lr) + (Mix[4] * lg) + (Mix[5] * lb);
                float mixedS = (Mix[6] * lr) + (Mix[7] * lg) + (Mix[8] * lb);
                float gl = MathF.Cbrt(MathF.Max(mixedL, 0f) + Bias) - CbrtBias;
                float gm = MathF.Cbrt(MathF.Max(mixedM, 0f) + Bias) - CbrtBias;
                float gs = MathF.Cbrt(MathF.Max(mixedS, 0f) + Bias) - CbrtBias;
                planes[0][dst + x] = (gl - gm) * 0.5f;
                planes[1][dst + x] = (gl + gm) * 0.5f;
                planes[2][dst + x] = gs;
            }
        });

        return planes;
    }

    // The sRGB transfer function extended to the whole real line (odd-symmetric), so HDR values above 1 and negative
    // values survive; NaN is treated as black.
    private static float SrgbToLinear(float v)
    {
        if (float.IsNaN(v))
        {
            return 0f;
        }

        float a = MathF.Min(MathF.Abs(v), 1e6f);
        float linear = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
        return v < 0 ? -linear : linear;
    }

    private static float[] BuildLinearLut(int bitsPerSample)
    {
        int size = 1 << bitsPerSample;
        var lut = new float[size];
        double max = size - 1;
        for (int i = 0; i < size; i++)
        {
            double v = i / max;
            lut[i] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
        }

        return lut;
    }

    private static float[] InvertDefaultInverseMatrix()
    {
        var m = JxlCustomTransformData.DefaultInverseMatrix;
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        double inv = 1.0 / det;
        return
        [
            (float)(((e * i) - (f * h)) * inv),
            (float)(((c * h) - (b * i)) * inv),
            (float)(((b * f) - (c * e)) * inv),
            (float)(((f * g) - (d * i)) * inv),
            (float)(((a * i) - (c * g)) * inv),
            (float)(((c * d) - (a * f)) * inv),
            (float)(((d * h) - (e * g)) * inv),
            (float)(((b * g) - (a * h)) * inv),
            (float)(((a * e) - (b * d)) * inv),
        ];
    }
}
