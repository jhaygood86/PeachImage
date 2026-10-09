using System.Numerics;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>Dequantization of one block's three channels with the reference decoder's reconstruction bias, plus chroma-from-luma.</summary>
internal static class DequantKernel
{
    /// <summary>
    /// For each coefficient: <c>dequant = AdjustQuantBias(q) * matrix * scale</c> per channel, then
    /// <c>x' = x + xCc * y</c> and <c>b' = b + bCc * y</c> (y is unchanged).
    /// </summary>
    public static void Dequantize(
        int[] qx,
        int[] qy,
        int[] qb,
        float[] matrices,
        int size,
        float scaleX,
        float scaleY,
        float scaleB,
        float[] biases,
        float xCc,
        float bCc,
        float[] dx,
        float[] dy,
        float[] db)
    {
        int k = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vScaleX = new Vector<float>(scaleX);
            var vScaleY = new Vector<float>(scaleY);
            var vScaleB = new Vector<float>(scaleB);
            var vXcc = new Vector<float>(xCc);
            var vBcc = new Vector<float>(bCc);
            var bias0 = new Vector<float>(biases[0]);
            var bias1 = new Vector<float>(biases[1]);
            var bias2 = new Vector<float>(biases[2]);
            var bias3 = new Vector<float>(biases[3]);
            for (; k <= size - Vector<float>.Count; k += Vector<float>.Count)
            {
                var x = AdjustBias(Vector.ConvertToSingle(new Vector<int>(qx, k)), bias0, bias3) * (new Vector<float>(matrices, k) * vScaleX);
                var y = AdjustBias(Vector.ConvertToSingle(new Vector<int>(qy, k)), bias1, bias3) * (new Vector<float>(matrices, size + k) * vScaleY);
                var b = AdjustBias(Vector.ConvertToSingle(new Vector<int>(qb, k)), bias2, bias3) * (new Vector<float>(matrices, (2 * size) + k) * vScaleB);
                ((vXcc * y) + x).CopyTo(dx, k);
                y.CopyTo(dy, k);
                ((vBcc * y) + b).CopyTo(db, k);
            }
        }

        for (; k < size; k++)
        {
            float x = AdjustQuantBias(0, qx[k], biases) * (matrices[k] * scaleX);
            float y = AdjustQuantBias(1, qy[k], biases) * (matrices[size + k] * scaleY);
            float b = AdjustQuantBias(2, qb[k], biases) * (matrices[(2 * size) + k] * scaleB);
            dx[k] = (xCc * y) + x;
            dy[k] = y;
            db[k] = (bCc * y) + b;
        }
    }

    /// <summary>The scalar reference: coefficients of magnitude below 1.125 move to the channel bias, larger ones shrink by <c>bias3 / q</c>.</summary>
    public static float AdjustQuantBias(int channel, int quantized, float[] biases)
    {
        float q = quantized;
        if (MathF.Abs(q) < 1.125f)
        {
            return q == 0 ? 0f : MathF.CopySign(biases[channel], q);
        }

        return q - (biases[3] / q);
    }

    private static Vector<float> AdjustBias(Vector<float> q, Vector<float> channelBias, Vector<float> bias3)
    {
        var small = Vector.LessThan(Vector.Abs(q), new Vector<float>(1.125f));
        var shrunk = q - (bias3 / q);
        var biased = Vector.ConditionalSelect(Vector.Equals(q, Vector<float>.Zero), Vector<float>.Zero, Vector.ConditionalSelect(Vector.LessThan(q, Vector<float>.Zero), -channelBias, channelBias));
        return Vector.ConditionalSelect(small, biased, shrunk);
    }
}
