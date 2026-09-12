namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own 4x4 <c>aom_dsp/intrapred.c</c> basic-intra-prediction
/// reference functions (<c>paeth_predictor</c>, <c>smooth_predictor</c>, <c>smooth_v_predictor</c>,
/// <c>smooth_h_predictor</c>, <c>dc_predictor</c> -- the "both neighbors available" case, matching
/// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction.Predict"/>'s own
/// <c>haveLeft &amp;&amp; haveAbove</c> path), kept deliberately separate from
/// <see cref="Av1IntraPrediction"/>'s own spec-derived implementation so a test comparing the two is a
/// genuine check against libaom's own real algorithm, not a self-consistency check against a single
/// spec-text transcription. <see cref="Av1IntraPrediction"/> was built directly from the AV1 spec's own
/// §7.11.2 text rather than from libaom's C source, so this is a real second, independent source, not a
/// restatement of the same one.
/// </summary>
internal static class LibaomReferenceIntraPred
{
    /// <summary><c>SMOOTH_WEIGHT_LOG2_SCALE</c> (<c>aom_dsp/intrapred_common.h</c>).</summary>
    private const int SmoothWeightLog2Scale = 8;

    /// <summary><c>smooth_weights_4</c> (<c>aom_dsp/intrapred_common.h</c>) -- the 4-wide/4-tall table this
    /// 4x4-only reference needs (libaom's own real table is one array indexed by <c>bw - 4</c> across every
    /// supported block size; only the first 4 entries are reachable at bw = bh = 4).</summary>
    private static readonly int[] SmoothWeights4 = [255, 149, 85, 64];

    private static int DivideRound(long value, int bits) => (int)((value + (1L << (bits - 1))) >> bits);

    /// <summary><c>paeth_predictor</c>/<c>paeth_predictor_single</c> (<c>aom_dsp/intrapred.c:47-71</c>).</summary>
    public static void Paeth(ReadOnlySpan<int> above, ReadOnlySpan<int> left, int topLeft, Span<int> dst)
    {
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                int baseVal = above[c] + left[r] - topLeft;
                int pLeft = Math.Abs(baseVal - left[r]);
                int pTop = Math.Abs(baseVal - above[c]);
                int pTopLeft = Math.Abs(baseVal - topLeft);

                dst[(r * 4) + c] = pLeft <= pTop && pLeft <= pTopLeft ? left[r] : pTop <= pTopLeft ? above[c] : topLeft;
            }
        }
    }

    /// <summary><c>smooth_predictor</c> (<c>aom_dsp/intrapred.c:84-113</c>).</summary>
    public static void Smooth(ReadOnlySpan<int> above, ReadOnlySpan<int> left, Span<int> dst)
    {
        int belowPred = left[3];
        int rightPred = above[3];
        const int log2Scale = 1 + SmoothWeightLog2Scale;
        const int scale = 1 << SmoothWeightLog2Scale;

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                long thisPred = ((long)SmoothWeights4[r] * above[c])
                    + ((long)(scale - SmoothWeights4[r]) * belowPred)
                    + ((long)SmoothWeights4[c] * left[r])
                    + ((long)(scale - SmoothWeights4[c]) * rightPred);
                dst[(r * 4) + c] = DivideRound(thisPred, log2Scale);
            }
        }
    }

    /// <summary><c>smooth_v_predictor</c> (<c>aom_dsp/intrapred.c:115-142</c>).</summary>
    public static void SmoothV(ReadOnlySpan<int> above, ReadOnlySpan<int> left, Span<int> dst)
    {
        int belowPred = left[3];
        const int log2Scale = SmoothWeightLog2Scale;
        const int scale = 1 << SmoothWeightLog2Scale;

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                long thisPred = ((long)SmoothWeights4[r] * above[c]) + ((long)(scale - SmoothWeights4[r]) * belowPred);
                dst[(r * 4) + c] = DivideRound(thisPred, log2Scale);
            }
        }
    }

    /// <summary><c>smooth_h_predictor</c> (<c>aom_dsp/intrapred.c:144-171</c>).</summary>
    public static void SmoothH(ReadOnlySpan<int> above, ReadOnlySpan<int> left, Span<int> dst)
    {
        int rightPred = above[3];
        const int log2Scale = SmoothWeightLog2Scale;
        const int scale = 1 << SmoothWeightLog2Scale;

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                long thisPred = ((long)SmoothWeights4[c] * left[r]) + ((long)(scale - SmoothWeights4[c]) * rightPred);
                dst[(r * 4) + c] = DivideRound(thisPred, log2Scale);
            }
        }
    }

    /// <summary><c>dc_predictor</c> (<c>aom_dsp/intrapred.c:216-234</c>) -- the "both neighbors available" case.</summary>
    public static int Dc(ReadOnlySpan<int> above, ReadOnlySpan<int> left)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            sum += above[i];
        }

        for (int i = 0; i < 4; i++)
        {
            sum += left[i];
        }

        const int count = 8; // bw + bh, 4 + 4.
        return (sum + (count >> 1)) / count;
    }

    /// <summary><c>dc_left_predictor</c> (<c>aom_dsp/intrapred.c:186-199</c>) -- above unavailable.</summary>
    public static int DcLeft(ReadOnlySpan<int> left)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            sum += left[i];
        }

        const int bh = 4;
        return (sum + (bh >> 1)) / bh;
    }

    /// <summary><c>dc_top_predictor</c> (<c>aom_dsp/intrapred.c:201-214</c>) -- left unavailable.</summary>
    public static int DcTop(ReadOnlySpan<int> above)
    {
        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            sum += above[i];
        }

        const int bw = 4;
        return (sum + (bw >> 1)) / bw;
    }

    /// <summary><c>dc_128_predictor</c> (<c>aom_dsp/intrapred.c:173-184</c>) -- neither neighbor available.</summary>
    public static int Dc128(int bitDepth) => 1 << (bitDepth - 1);
}
