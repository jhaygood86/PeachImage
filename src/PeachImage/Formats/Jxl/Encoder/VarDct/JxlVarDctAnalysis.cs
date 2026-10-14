using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// The whole-frame measurements the group workers rely on: the global DC colour correlation, and the adaptive quantization
/// field (finer steps where the content is smooth, where quantization error is most visible, coarser where it is busy).
/// </summary>
internal static class JxlVarDctAnalysis
{
    // How strongly activity moves the quantization (the exponent of the activity ratio) and the limits of the resulting multiplier.
    private const double Exponent = 0.3;
    private const double MinMultiplier = 0.55;
    private const double MaxMultiplier = 1.8;

    // Activity below this is treated as flat so empty blocks do not ask for unbounded precision.
    private const double ActivityFloor = 0.004;

    public static void Run(JxlVarDctFrameState state)
    {
        int blocksX = state.BlocksX;
        int blocksY = state.BlocksY;
        int stride = state.Stride;
        float[] planeX = state.Planes[0];
        float[] planeY = state.Planes[1];
        float[] planeB = state.Planes[2];
        var activity = new float[blocksX * blocksY];
        var rowXY = new double[blocksY];
        var rowBY = new double[blocksY];
        var rowYY = new double[blocksY];

        RowParallel.For(blocksY, by =>
        {
            double xy = 0, by2 = 0, yy = 0;
            for (int bx = 0; bx < blocksX; bx++)
            {
                double sumX = 0, sumY = 0, sumB = 0, grad = 0;
                for (int y = 0; y < 8; y++)
                {
                    int row = (((by * 8) + y) * stride) + (bx * 8);
                    for (int x = 0; x < 8; x++)
                    {
                        int i = row + x;
                        sumX += planeX[i];
                        sumY += planeY[i];
                        sumB += planeB[i];
                        if (x < 7)
                        {
                            grad += Math.Abs(planeY[i + 1] - planeY[i]);
                        }

                        if (y < 7)
                        {
                            grad += Math.Abs(planeY[i + stride] - planeY[i]);
                        }
                    }
                }

                double meanY = sumY / 64;
                xy += (sumX / 64) * meanY;
                by2 += (sumB / 64) * meanY;
                yy += meanY * meanY;
                activity[(by * blocksX) + bx] = (float)(grad / 112);
            }

            rowXY[by] = xy;
            rowBY[by] = by2;
            rowYY[by] = yy;
        });

        // DC colour correlation: the global least-squares ratio of X and of B to Y over the block means.
        double sXY = rowXY.Sum();
        double sBY = rowBY.Sum();
        double sYY = rowYY.Sum();
        if (state.Settings.ChromaFromLuma && sYY > 1e-12)
        {
            state.DcFactorX = (int)Math.Clamp(Math.Round(sXY / sYY * JxlVarDctConstants.ColorFactor), -128, 127);
            state.DcFactorB = (int)Math.Clamp(Math.Round(((sBY / sYY) - 1.0) * JxlVarDctConstants.ColorFactor), -128, 127);
        }

        state.DcCorrelation[0] = (float)state.DcFactorX / JxlVarDctConstants.ColorFactor;
        state.DcCorrelation[2] = 1f + ((float)state.DcFactorB / JxlVarDctConstants.ColorFactor);

        ComputeQuantizationField(state, activity);
    }

    private static void ComputeQuantizationField(JxlVarDctFrameState state, float[] activity)
    {
        double strength = state.Settings.AdaptiveQuantization;
        if (strength <= 0)
        {
            return;
        }

        // Normalize by the geometric mean so the field redistributes precision instead of changing the overall rate.
        double logSum = 0;
        foreach (float a in activity)
        {
            logSum += Math.Log(a + ActivityFloor);
        }

        double reference = Math.Exp(logSum / activity.Length);
        double exponent = Exponent * strength;
        for (int i = 0; i < activity.Length; i++)
        {
            double m = Math.Pow(reference / (activity[i] + ActivityFloor), exponent);
            state.AqMultiplier[i] = (float)Math.Clamp(m, MinMultiplier, MaxMultiplier);
        }
    }
}
