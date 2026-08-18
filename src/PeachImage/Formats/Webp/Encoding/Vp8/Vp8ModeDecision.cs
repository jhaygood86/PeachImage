using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Intra prediction mode selection: for each candidate mode, predicts directly into the working reconstruction
/// plane (reusing <see cref="Vp8IntraPredictionWholeBlock"/>/<see cref="Vp8IntraPrediction4x4"/> unchanged —
/// they only ever read already-finalized neighbor pixels above/left of the block being predicted, never the
/// block's own current contents), scores it against the source block by sum-of-absolute-differences, and keeps
/// the cheapest. This is a v1 distortion-only decision (no rate-distortion Lagrangian term weighing actual
/// entropy-coding cost) — full RDO needs a working bit-cost estimator, a materially larger piece of machinery;
/// SAD-only selection is what most first-working intra encoders converge to, and is the natural place to add an
/// RD term later without restructuring the surrounding pipeline.
/// </summary>
internal static class Vp8ModeDecision
{
    private static readonly int[] WholeBlockModes =
    [
        Vp8PredictionModes.DcPred,
        Vp8PredictionModes.VPred,
        Vp8PredictionModes.HPred,
        Vp8PredictionModes.TmPred,
    ];

    /// <summary>
    /// Tries DC/V/H/TM at <paramref name="origin"/> (16x16 luma or 8x8 chroma, per <paramref name="size"/>),
    /// leaves the winning mode's prediction committed in <paramref name="recon"/>, and returns that mode.
    /// </summary>
    public static int SelectWholeBlockMode(
        Span<byte> recon, int origin, int stride, int size, bool hasAbove, bool hasLeft,
        ReadOnlySpan<byte> source, int sourceOrigin, int sourceStride,
        out int bestSad)
    {
        int bestMode = WholeBlockModes[0];
        bestSad = int.MaxValue;

        foreach (int mode in WholeBlockModes)
        {
            Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(mode, recon, origin, stride, size, hasAbove, hasLeft);
            int sad = Sad(source, sourceOrigin, sourceStride, recon, origin, stride, size);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = mode;
            }
        }

        Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(bestMode, recon, origin, stride, size, hasAbove, hasLeft);
        return bestMode;
    }

    /// <summary>
    /// Tries all 10 4x4 B_PRED modes at <paramref name="origin"/>, leaves the winning mode's prediction
    /// committed in <paramref name="recon"/>, and returns that mode. Callers evaluating a real (not just a
    /// decision-heuristic) B_PRED subblock must call this only once <paramref name="recon"/>'s above/left
    /// neighbors already hold real reconstructed pixels (i.e. in the same raster-order, causal sequence
    /// <see cref="Decoding.Vp8.Vp8FrameDecoder"/> reconstructs subblocks in), since <see cref="Vp8IntraPrediction4x4"/>
    /// reads them directly.
    /// </summary>
    public static int SelectSubblockMode(
        Span<byte> recon, int origin, int stride, ReadOnlySpan<byte> aboveRight,
        ReadOnlySpan<byte> source, int sourceOrigin, int sourceStride,
        out int bestSad)
    {
        int bestMode = 0;
        bestSad = int.MaxValue;

        for (int mode = 0; mode < Vp8PredictionModes.NumBModes; mode++)
        {
            Vp8IntraPrediction4x4.Predict(mode, recon, origin, stride, aboveRight);
            int sad = Sad(source, sourceOrigin, sourceStride, recon, origin, stride, 4);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = mode;
            }
        }

        Vp8IntraPrediction4x4.Predict(bestMode, recon, origin, stride, aboveRight);
        return bestMode;
    }

    private static int Sad(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride, int size)
    {
        int sad = 0;
        for (int y = 0; y < size; y++)
        {
            int ao = aOffset + (y * aStride);
            int bo = bOffset + (y * bStride);
            for (int x = 0; x < size; x++)
            {
                sad += Math.Abs(a[ao + x] - b[bo + x]);
            }
        }

        return sad;
    }
}
