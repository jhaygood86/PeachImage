using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// The Lagrangian <c>D + lambda*R</c> rate-distortion cost this encoder's mode/angle/partition search
/// (<c>Av1TileEncoder.ComputeCandidateCost</c>) compares candidates with, replacing the two proxies it used
/// before this: non-lossless candidates were ranked by distortion alone (SSE, no rate term at all), and
/// lossless candidates by a hand-tuned per-coefficient magnitude/log2 proxy that only approximated real bit
/// cost. <see cref="Av1SymbolEncoder.EstimateSymbolCost"/> now gives every candidate a real (not proxied) bit
/// count for its actual quantized residual, straight from the same context-derivation code the real bitstream
/// writer uses (<c>Av1CoefficientWriter.WriteCoeffs</c>, called against an <see cref="Av1TrialSymbolSink"/>) --
/// this type only supplies the other half of the Lagrangian formula: the <c>qindex -> lambda</c> mapping that
/// weighs that real bit count against distortion, and the combine step itself.
/// </summary>
internal static class Av1RdCost
{
    /// <summary>
    /// The keyframe rate multiplier libaom's own <c>def_kf_rd_multiplier(qindex)</c> uses (av1/encoder/rd.c)
    /// -- AVIF still images are always a single intra frame, spec's closest analog to a keyframe, so this is
    /// the one of libaom's three per-frame-type curves (<c>def_kf_rd_multiplier</c>/<c>def_arf_rd_multiplier</c>/
    /// <c>def_inter_rd_multiplier</c>, which only differ by a small additive constant: 3.3/3.25/3.2
    /// respectively) that actually applies here.
    /// </summary>
    private const double KfRdMultiplierBase = 3.3;

    private const double KfRdMultiplierSlope = 0.0015;

    /// <summary>
    /// <c>RDDIV_BITS</c> (av1/encoder/rd.h) -- see <see cref="QIndexToLambda"/>'s remarks for how this and
    /// <c>AV1_PROB_COST_SHIFT</c> combine (and cancel) to turn libaom's <c>rdmult</c> into a plain
    /// distortion-per-bit multiplier.
    /// </summary>
    private const double RdDivShift = 128.0; // 2^7

    /// <summary>
    /// Maps a frame's <c>base_q_idx</c> to the lambda this frame's whole candidate search should weigh real
    /// bit counts by, derived directly from libaom's own <c>av1_compute_rd_mult_based_on_qindex</c> (av1/encoder/rd.c)
    /// rather than an empirically-guessed constant (an earlier version of this method used one, and it turned
    /// out to be roughly 45x too small -- see the project plan's partition/TX-size RDO phase for the
    /// measured regression an under-weighted rate term caused once <c>Av1TileEncoder.DecidePartition</c>
    /// started using this for non-lossless partition decisions, not just mode/angle search). Used unscaled
    /// (no additional empirical fudge factor): a few nearby scale factors were tried empirically against this
    /// project's own benchmark comparison and each made the size/quality trade-off measurably worse in one
    /// direction or the other -- not a smooth response, consistent with this search's decisions being
    /// discrete (merge-or-split, mode A-or-B) rather than a continuously differentiable optimization, so nearby
    /// candidate scale factors can flip a few consequential decisions the "wrong" way even when the aggregate
    /// trend (smaller output at higher lambda) stays monotonic. The unscaled, directly reference-derived value
    /// was the best of everything tried.
    ///
    /// <para>libaom computes <c>rdmult = q^2 * (3.3 + 0.0015*qindex)</c> (the keyframe curve; <c>q</c> is the
    /// DC quantizer step, <c>av1_dc_quant_QTX</c> -- this encoder's own <see cref="Av1Dequantizer.DcQ"/> is
    /// the same table) and combines it with rate/distortion via <c>RDCOST(rdmult, rate, dist) =
    /// round(rate*rdmult / 2^AV1_PROB_COST_SHIFT) + dist*2^RDDIV_BITS</c> (av1/encoder/rd.h), where
    /// <c>rate</c> is itself pre-scaled by <c>2^AV1_PROB_COST_SHIFT</c> (the same fixed-point convention
    /// <c>av1_cost_symbol</c>-style cost functions use). Substituting <c>rate = rawBits * 2^AV1_PROB_COST_SHIFT</c>
    /// makes that shift cancel out of the first term exactly, leaving <c>RDCOST = rawBits*rdmult +
    /// dist*2^RDDIV_BITS</c> -- dividing through by <c>2^RDDIV_BITS</c> to normalize against distortion (this
    /// encoder's own <see cref="Av1RdCost.CombineCost"/> convention, <c>dist + lambda*rawBits</c>) gives
    /// exactly <c>lambda = rdmult / 2^RDDIV_BITS</c>, computed below. <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>
    /// already returns plain, unscaled bit counts (never libaom's <c>AV1_PROB_COST_SHIFT</c>-scaled fixed-point
    /// form), so this encoder's own rate term is exactly the "raw bits" this derivation assumes -- no shift of
    /// its own needs to be undone here.</para>
    ///
    /// <para>Lossless (<paramref name="baseQIdx"/> &lt;= 0, AV1's coded-lossless trigger) returns 0:
    /// lossless distortion is always exactly zero once a candidate is really committed (every 4x4
    /// Walsh-Hadamard sub-block reconstructs bit-exactly regardless of which candidate wins), so weighing a
    /// meaningless zero-distortion term would just add noise -- lossless candidates are, correctly, ranked by
    /// real bit count alone.</para>
    /// </summary>
    public static double QIndexToLambda(int baseQIdx)
    {
        if (baseQIdx <= 0)
        {
            return 0.0;
        }

        int dcQ = Av1Dequantizer.DcQ(baseQIdx, 8);
        double rdMult = dcQ * dcQ * (KfRdMultiplierBase + (KfRdMultiplierSlope * baseQIdx));
        return rdMult / RdDivShift;
    }

    /// <summary>
    /// <c>D + lambda*R</c>: combines a candidate's distortion (<paramref name="sse"/>, spatial-domain sum of
    /// squared error against source) with its real entropy-coded bit count (<paramref name="bits"/>, from
    /// <see cref="Av1TrialSymbolSink.Bits"/>) via <paramref name="lambda"/> (<see cref="QIndexToLambda"/>).
    /// For lossless (<paramref name="lambda"/> == 0), this reduces to <paramref name="sse"/> alone, which is
    /// always 0 for a real lossless residual measured against itself -- callers still pass the real
    /// <paramref name="bits"/> count so lossless search results (already rate-only by construction, per
    /// <see cref="QIndexToLambda"/>'s remarks) aren't silently ignored; see
    /// <c>Av1TileEncoder.ComputeCandidateCost</c>'s lossless branch, which calls this with <paramref name="sse"/>
    /// fixed at 0 and <paramref name="lambda"/> fixed at 1.0 instead, treating bits as the entire cost.
    /// </summary>
    public static long CombineCost(long sse, long bits, double lambda) => sse + (long)Math.Round(lambda * bits);
}
