namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real <c>av1_ml_prune_4_partition</c> (<c>av1/encoder/partition_strategy.c:1326-1523</c>),
/// the 4-way half of <c>ml_prune_partition</c> -- same unconditional-1 ALLINTRA status as
/// <see cref="Av1AbPartitionNnPruner"/>'s own AB half.
///
/// <para><b>Combination semantics differ from the AB pruner</b>: real libaom's own code branches on
/// <c>ml_model_index = (ml_4_partition_search_level_index &lt; 3)</c>, and only ports the <c>ml_model_index == 1</c>
/// (softmax, mean/std-normalized) branch here -- the branch this project's own tested effort range (0-2, where
/// <see cref="Av1SpeedFeatures.Ml4PartitionSearchLevelIndex"/> tops out at 2) actually reaches. That branch does
/// NOT zero-and-rebuild the incoming allowed flags the way <see cref="Av1AbPartitionNnPruner.Prune"/>'s own
/// <c>ml_model_index == 0</c>-shaped logic does -- it only force-enables when a softmax probability clears
/// <c>search_thresh</c>, force-disables when it falls below <c>not_search_thresh</c>, and otherwise leaves the
/// flag exactly as the caller's own structural gate left it (a real three-way outcome, not a full override).
/// <see cref="Prune"/> mirrors this precisely: <c>horz4Allowed</c>/<c>vert4Allowed</c> are <c>ref</c> parameters
/// mutated only on a decisive probability, never reset first.</para>
///
/// <para>The <c>ml_model_index == 0</c> branch (plain, non-normalized 4-output softmax-free model, reachable
/// once <see cref="Av1SpeedFeatures.Ml4PartitionSearchLevelIndex"/> reaches 3, i.e. effort &gt;= 3) is
/// deliberately NOT ported -- <see cref="Prune"/> throws rather than silently produce a wrong decision if this
/// is ever reached before that model's own weight tables are ported too.</para>
/// </summary>
internal static class Av1FourWayPartitionNnPruner
{
    /// <summary>Same libaom sentinel as <see cref="Av1AbPartitionNnPruner"/>'s own identical constant -- see its remarks.</summary>
    private const long InvalidRdSentinel = 1_000_000_000L;

    /// <summary>
    /// <c>av1_ml_prune_4_partition</c>. <paramref name="partCtx"/>/<paramref name="varCtx"/> are the same
    /// <c>pc_tree-&gt;partitioning</c>/<c>get_unsigned_bits(source_variance)</c> values
    /// <see cref="Av1AbPartitionNnPruner.Prune"/> already takes. <paramref name="horzRd0"/>/<paramref name="horzRd1"/>/
    /// <paramref name="vertRd0"/>/<paramref name="vertRd1"/>/<paramref name="splitRd"/> are libaom's own
    /// <c>rect_part_rd[HORZ4]</c>/<c>rect_part_rd[VERT4]</c>/<c>split_rd</c> -- confirmed via direct source
    /// reading that libaom's own tiny <c>PART4_TYPES</c> enum (<c>HORZ4 = 0, VERT4 = 1</c>,
    /// <c>encodeframe_utils.h:58</c>) reuses the exact same ordinals as <c>RECT_PART_TYPE</c>'s own
    /// <c>HORZ = 0, VERT = 1</c>, so <c>rect_part_rd[HORZ4]</c> is genuinely the same array slot as
    /// <c>rect_part_rd[HORZ]</c> -- i.e. these are literally the same HORZ/VERT/SPLIT sibling costs the
    /// arithmetic gate and the AB NN pruner already consume, not a distinct HORZ_4/VERT_4-specific cost.
    /// <paramref name="horz4SourceVar"/>/<paramref name="vert4SourceVar"/> are libaom's own
    /// <c>horz_4_source_var</c>/<c>vert_4_source_var</c> (4 entries each, one per quarter-slice, in the exact
    /// position order <c>Av1TileEncoder.RdPickPartition</c>'s own Horz4/Vert4 leaf loop already uses).
    /// <paramref name="pbSourceVariance"/> is the same value <paramref name="varCtx"/> was bit-length-encoded
    /// from -- needed again here in raw form for the variance-ratio denominator.
    /// </summary>
    internal static void Prune(
        int partCtx,
        int varCtx,
        long bestRd,
        long horzRd0,
        long horzRd1,
        long vertRd0,
        long vertRd1,
        System.ReadOnlySpan<long> splitRd,
        System.ReadOnlySpan<int> horz4SourceVar,
        System.ReadOnlySpan<int> vert4SourceVar,
        int pbSourceVariance,
        int sizeMi,
        int levelIndex,
        int resIdx,
        ref bool horz4Allowed,
        ref bool vert4Allowed)
    {
        if (bestRd >= InvalidRdSentinel)
        {
            return;
        }

        int mlModelIndex = levelIndex < 3 ? 1 : 0;
        if (mlModelIndex == 0)
        {
            throw new System.NotSupportedException(
                "ml_4_partition_search_level_index >= 3 (effort >= 3) selects libaom's other, non-normalized " +
                "4-way partition NN model (ml_model_index == 0), whose weight tables this port has not ported.");
        }

        (float[] weights0, float[] bias0, float[] weights1, float[] bias1, int hidden, float[] mean, float[] std, int bsizeIdx) = sizeMi switch
        {
            4 => (Av1FourWayPartitionNnWeights.Weights16Layer0, Av1FourWayPartitionNnWeights.Bias16Layer0, Av1FourWayPartitionNnWeights.Weights16Layer1, Av1FourWayPartitionNnWeights.Bias16Layer1, 24, Av1FourWayPartitionNnWeights.Mean16, Av1FourWayPartitionNnWeights.Std16, 3),
            8 => (Av1FourWayPartitionNnWeights.Weights32Layer0, Av1FourWayPartitionNnWeights.Bias32Layer0, Av1FourWayPartitionNnWeights.Weights32Layer1, Av1FourWayPartitionNnWeights.Bias32Layer1, 32, Av1FourWayPartitionNnWeights.Mean32, Av1FourWayPartitionNnWeights.Std32, 2),
            16 => (Av1FourWayPartitionNnWeights.Weights64Layer0, Av1FourWayPartitionNnWeights.Bias64Layer0, Av1FourWayPartitionNnWeights.Weights64Layer1, Av1FourWayPartitionNnWeights.Bias64Layer1, 24, Av1FourWayPartitionNnWeights.Mean64, Av1FourWayPartitionNnWeights.Std64, 1),
            _ => throw new System.ArgumentOutOfRangeException(nameof(sizeMi), sizeMi, "4-way-partition NN pruning is only defined for sizeMi 4/8/16 (Block16x16/32x32/64x64)."),
        };

        Span<float> features = stackalloc float[18];
        features[0] = partCtx;
        features[1] = varCtx;

        long rdcost = Math.Min(int.MaxValue, bestRd);
        Span<long> subBlockRdcost = stackalloc long[8];
        subBlockRdcost[0] = ClampRd(horzRd0);
        subBlockRdcost[1] = ClampRd(horzRd1);
        subBlockRdcost[2] = ClampRd(vertRd0);
        subBlockRdcost[3] = ClampRd(vertRd1);
        subBlockRdcost[4] = ClampRd(splitRd[0]);
        subBlockRdcost[5] = ClampRd(splitRd[1]);
        subBlockRdcost[6] = ClampRd(splitRd[2]);
        subBlockRdcost[7] = ClampRd(splitRd[3]);

        for (int i = 0; i < 8; i++)
        {
            float rdRatio = 1.0f;
            if (subBlockRdcost[i] > 0 && subBlockRdcost[i] < rdcost)
            {
                rdRatio = (float)subBlockRdcost[i] / rdcost;
            }

            features[2 + i] = rdRatio;
        }

        float denom = pbSourceVariance + 1;
        for (int i = 0; i < 4; i++)
        {
            float ratio = (horz4SourceVar[i] + 1) / denom;
            features[10 + i] = Math.Clamp(ratio, 0.1f, 10.0f);
        }

        for (int i = 0; i < 4; i++)
        {
            float ratio = (vert4SourceVar[i] + 1) / denom;
            features[14 + i] = Math.Clamp(ratio, 0.1f, 10.0f);
        }

        // ml_model_index == 1 (the only branch ported, see this class's own remarks) always normalizes.
        for (int i = 0; i < 18; i++)
        {
            features[i] = (features[i] - mean[i]) / std[i];
        }

        Span<float> score = stackalloc float[3];
        Predict(features, weights0, bias0, weights1, bias1, hidden, score);

        Span<float> probs = stackalloc float[3];
        Softmax(score, probs);

        float searchThresh = Av1FourWayPartitionNnWeights.SearchThresh[levelIndex, resIdx, bsizeIdx];
        float notSearchThresh = Av1FourWayPartitionNnWeights.NotSearchThresh[levelIndex, resIdx, bsizeIdx];

        if (probs[1] >= searchThresh)
        {
            horz4Allowed = true;
        }

        if (probs[1] < notSearchThresh)
        {
            horz4Allowed = false;
        }

        if (probs[2] >= searchThresh)
        {
            vert4Allowed = true;
        }

        if (probs[2] < notSearchThresh)
        {
            vert4Allowed = false;
        }

        static long ClampRd(long v) => v > 0 && v < InvalidRdSentinel ? v : 0;
    }

    /// <summary>
    /// <c>av1_nn_predict_c</c> specialized to this NN's own per-block-size shape (18 inputs, one ReLU hidden
    /// layer of <paramref name="hidden"/> nodes -- 24 for the 16x16/64x64 models, 32 for the 32x32 model --
    /// then 3 linear softmax logits), plus <c>av1_nn_output_prec_reduce</c>'s 1/512 quantization on all 3
    /// outputs (<c>reduce_prec = 1</c> at the real call site) -- same idiom as
    /// <see cref="Av1AbPartitionNnPruner.Predict"/>'s own identical precedent.
    /// </summary>
    private static void Predict(ReadOnlySpan<float> features, ReadOnlySpan<float> weights0, ReadOnlySpan<float> bias0, ReadOnlySpan<float> weights1, ReadOnlySpan<float> bias1, int hidden, Span<float> scores)
    {
        const int numFeatures = 18;
        const int numOutputs = 3;

        Span<float> h = stackalloc float[hidden];
        for (int n = 0; n < hidden; n++)
        {
            float val = bias0[n];
            int rowBase = n * numFeatures;
            for (int i = 0; i < numFeatures; i++)
            {
                val += weights0[rowBase + i] * features[i];
            }

            h[n] = MathF.Max(val, 0f);
        }

        const int prec = 512;
        for (int n = 0; n < numOutputs; n++)
        {
            float val = bias1[n];
            int rowBase = n * hidden;
            for (int i = 0; i < hidden; i++)
            {
                val += weights1[rowBase + i] * h[i];
            }

            scores[n] = (int)((val * prec) + 0.5f) / (float)prec;
        }
    }

    /// <summary>
    /// <c>av1_nn_softmax</c> (<c>av1/encoder/ml.c:143-...</c>): max-subtraction for overflow safety, clamped to
    /// [-10, 0] before <c>expf</c> to avoid underflow, matching libaom's own real clamp exactly.
    /// </summary>
    private static void Softmax(ReadOnlySpan<float> input, Span<float> output)
    {
        float maxInput = input[0];
        for (int i = 1; i < input.Length; i++)
        {
            maxInput = MathF.Max(maxInput, input[i]);
        }

        float sumOut = 0f;
        for (int i = 0; i < input.Length; i++)
        {
            float normalizedInput = MathF.Max(input[i] - maxInput, -10.0f);
            output[i] = MathF.Exp(normalizedInput);
            sumOut += output[i];
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] /= sumOut;
        }
    }
}
