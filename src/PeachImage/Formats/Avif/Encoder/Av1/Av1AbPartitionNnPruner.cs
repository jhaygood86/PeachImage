namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real <c>ml_prune_ab_partition</c> (<c>av1/encoder/partition_strategy.c:1223-1320</c>),
/// the NN half of <c>ml_prune_partition</c> -- unconditionally active at every ALLINTRA speed
/// (<c>set_allintra_speed_features_framesize_independent</c>'s own unconditional
/// <c>sf-&gt;part_sf.ml_prune_partition = 1</c>), same "no speed gate" status as
/// <c>prune_ext_partition_types_search_level</c> (see <see cref="Av1TileEncoder.RdPickPartition"/>'s own AB-partition
/// remarks for that mechanism).
///
/// <para><b>Ordering relative to the arithmetic gate is load-bearing, not additive</b>: real libaom calls this
/// function strictly AFTER <c>av1_prune_ab_partitions</c>'s own directional/RD-ratio arithmetic gate has already
/// written <c>ab_partitions_allowed[]</c> (partition_strategy.c:1995-2005) -- and when this function doesn't
/// early-return (bsize &gt;= 8x8 and <c>best_rd &lt; 1e9</c>, both unconditionally true for every AB candidate this
/// project's own <c>RdPickPartition</c> ever offers, since AB is gated to sizeMi in {4,8,16} = Block16x16/32x32/64x64
/// and this port's own lossless cost units never approach 1e9 -- see <see cref="Prune"/>'s own remarks), it calls
/// libaom's own <c>av1_zero_array(ab_partitions_allowed, NUM_AB_PARTS)</c> and rebuilds all 4 flags purely from its
/// own NN score threshold -- i.e. it REPLACES the arithmetic gate's own decision wholesale, not ANDs with it. This
/// port mirrors that exactly: <see cref="Prune"/> takes the 4 already-gated flags by <c>ref</c> and only leaves them
/// untouched on its own early-return path (bestRd &gt;= 1e9, the "no valid partition found yet" case).</para>
///
/// <para><c>av1_ab_partition_nnconfig_128</c> is out of scope (never reached, see
/// <see cref="Av1AbPartitionNnWeights"/>'s own remarks); <c>BLOCK_8X8</c> is libaom's own real
/// <c>nn_config = NULL</c> case (no model exists for that size, arithmetic gate stands unmodified) -- also
/// unreachable here, since <see cref="Av1TileEncoder.RdPickPartition"/> never offers AB candidates below sizeMi 4.
/// </para>
/// </summary>
internal static class Av1AbPartitionNnPruner
{
    /// <summary>
    /// libaom's own <c>1000000000</c> sentinel (<c>partition_strategy.c:1232</c>, <c>:1345</c>): a real RD cost this
    /// large signals "no valid partition found yet" (this project's own equivalent of an aborted/unbounded search),
    /// not a genuine achievable cost -- ported as the literal same magnitude, not rescaled, since this project's own
    /// lossless cost units (<see cref="Av1TileEncoder"/>'s own <c>ScaleSignalingBits</c> is the identity function for
    /// lossless) are a raw bit-cost count, the same general order of magnitude as libaom's own real RD-cost units for
    /// realistic block sizes -- both stay many orders of magnitude below this sentinel for any real leaf.
    /// </summary>
    private const long InvalidRdSentinel = 1_000_000_000L;

    /// <summary>
    /// <c>ml_prune_ab_partition</c>. <paramref name="partCtx"/> is libaom's own <c>pc_tree-&gt;partitioning</c> --
    /// this project's own <c>bestType</c> snapshotted at the same point <c>Av1TileEncoder.RdPickPartition</c> already
    /// takes <c>abPruneBestCost</c>, cast directly to <c>float</c> with no remapping since
    /// <see cref="Decoding.Av1.Av1PartitionType"/>'s own ordinals (None=0..Vert4=9) already match AV1's real
    /// <c>PARTITION_TYPE</c> enum exactly. <paramref name="varCtx"/> is libaom's own
    /// <c>get_unsigned_bits(x-&gt;source_variance)</c> -- the same <c>pbSourceVariance</c>
    /// <c>Av1TileEncoder.EstimatePbSourceVariance</c> already computes for the arithmetic gate, bit-length-encoded
    /// (<c>floor(log2(n))+1</c>, 0 for n=0). <paramref name="horzRd0"/>/<paramref name="horzRd1"/>/
    /// <paramref name="vertRd0"/>/<paramref name="vertRd1"/>/<paramref name="splitRd"/> are the exact same
    /// already-computed sibling costs the arithmetic gate itself consumes (top/bottom half, left/right half, and the
    /// 4 SPLIT quadrants respectively) -- libaom's own <c>horz_rd[0..1]</c>/<c>vert_rd[0..1]</c>/<c>split_rd[0..3]</c>.
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
        int sizeMi,
        ref bool horzAAllowed,
        ref bool horzBAllowed,
        ref bool vertAAllowed,
        ref bool vertBAllowed)
    {
        // "Do not prune if there is no valid partition" (partition_strategy.c:1345) -- leaves the arithmetic
        // gate's own already-computed flags untouched, exactly like libaom's own early return before its own
        // av1_zero_array call.
        if (bestRd >= InvalidRdSentinel)
        {
            return;
        }

        (float[] weights0, float[] bias0, float[] weights1, float[] bias1, int threshOffset) = sizeMi switch
        {
            4 => (Av1AbPartitionNnWeights.Weights16Layer0, Av1AbPartitionNnWeights.Bias16Layer0, Av1AbPartitionNnWeights.Weights16Layer1, Av1AbPartitionNnWeights.Bias16Layer1, 150),
            8 => (Av1AbPartitionNnWeights.Weights32Layer0, Av1AbPartitionNnWeights.Bias32Layer0, Av1AbPartitionNnWeights.Weights32Layer1, Av1AbPartitionNnWeights.Bias32Layer1, 100),
            16 => (Av1AbPartitionNnWeights.Weights64Layer0, Av1AbPartitionNnWeights.Bias64Layer0, Av1AbPartitionNnWeights.Weights64Layer1, Av1AbPartitionNnWeights.Bias64Layer1, 0),
            _ => throw new System.ArgumentOutOfRangeException(nameof(sizeMi), sizeMi, "AB-partition NN pruning is only defined for sizeMi 4/8/16 (Block16x16/32x32/64x64)."),
        };

        Span<float> features = stackalloc float[10];
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

        Span<float> score = stackalloc float[16];
        Predict(features, weights0, bias0, weights1, bias1, score);

        Span<int> intScore = stackalloc int[16];
        int maxScore = -1000;
        for (int i = 0; i < 16; i++)
        {
            intScore[i] = (int)(100 * score[i]);
            maxScore = Math.Max(intScore[i], maxScore);
        }

        int thresh = maxScore - threshOffset;

        horzAAllowed = false;
        horzBAllowed = false;
        vertAAllowed = false;
        vertBAllowed = false;
        for (int i = 0; i < 16; i++)
        {
            if (intScore[i] >= thresh)
            {
                if ((i & 1) != 0)
                {
                    horzAAllowed = true;
                }

                if ((i & 2) != 0)
                {
                    horzBAllowed = true;
                }

                if ((i & 4) != 0)
                {
                    vertAAllowed = true;
                }

                if ((i & 8) != 0)
                {
                    vertBAllowed = true;
                }
            }
        }

        static long ClampRd(long v) => v > 0 && v < InvalidRdSentinel ? v : 0;
    }

    /// <summary>
    /// <c>av1_nn_predict_c</c> (<c>av1/encoder/ml.c:31-70</c>) specialized to this NN's own fixed shape (10 inputs,
    /// one 64-node ReLU hidden layer, 16 linear outputs) plus <c>av1_nn_output_prec_reduce</c>'s 1/512-precision
    /// quantization applied to every output (<c>reduce_prec = 1</c> at the real call site,
    /// <c>partition_strategy.c:1296</c>) -- same truncating-cast idiom as
    /// <see cref="Av1IntraCnnPartitionPruner.PredictMlp"/>'s own identical precedent, for the same reason (C's
    /// <c>(int)</c> cast on a float truncates toward zero; <c>MathF.Round</c> does not match on negative
    /// halfway values).
    /// </summary>
    private static void Predict(ReadOnlySpan<float> features, ReadOnlySpan<float> weights0, ReadOnlySpan<float> bias0, ReadOnlySpan<float> weights1, ReadOnlySpan<float> bias1, Span<float> scores)
    {
        const int numFeatures = 10;
        const int hidden = 64;
        const int numOutputs = 16;

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
}
