namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real SATD-based intra-mode shortlist (<c>intra_model_rd</c>/<c>prune_intra_y_mode</c>,
/// <c>av1/encoder/intra_mode_search.c</c>/<c>intra_mode_search_utils.h</c>, read directly from the local
/// checkout at <c>C:\Sources\GoogleSource\aom</c>) -- a cheap, per-(mode, angle_delta)-candidate estimate
/// (a Hadamard-transform SATD over the real predicted residual, no entropy modeling at all) that decides
/// whether a candidate is even worth a real RD trial, run <em>after</em> HOG-based pruning
/// (<see cref="Av1IntraHogPruner"/>) has already removed some directional modes outright, but strictly
/// before the real, expensive WHT+quantize+entropy-cost estimate this project's own
/// <c>ComputeCandidateCost</c>/<c>ComputeLosslessWholeLeafCostPerSubBlock</c> otherwise always runs.
///
/// <para><b>Deliberate scope limitation, disclosed rather than silently accepted</b>: <c>prune_intra_y_mode</c>'s
/// own top-K tracking (<see cref="ThreshTop"/>) is genuinely order-dependent -- which candidates survive
/// depends on which ones were evaluated <em>first</em> for a given leaf, since a later candidate is judged
/// against whatever's already in the running top-K. libaom's own real per-leaf candidate order
/// (<c>intra_rd_search_mode_order</c>, <c>intra_mode_search.c</c>) is <c>DC, H, V, SMOOTH, PAETH, SMOOTH_V,
/// SMOOTH_H, D135, D203, D157, D67, D113, D45</c>, with angle_delta sub-ordering of its own -- different from
/// this project's own <c>CandidateModes</c> iteration order (<c>DC, V, H, D45, D135, D113, D157, D203, D67,
/// SMOOTH, SMOOTH_V, SMOOTH_H, PAETH</c>, angle_delta always <c>-3..3</c>). Reordering <c>CandidateModes</c>
/// to match would be a separate, real behavioral change in its own right (it also controls tie-breaking for
/// every other cost comparison in this class, via <c>cost &lt; bestCost</c>'s implicit first-candidate-wins
/// rule), so this port faithfully reproduces the <em>threshold formula and SATD computation</em> without
/// chasing exact candidate-order parity -- meaning the specific pattern of which candidates get pruned isn't
/// byte-identical to libaom's own for a given leaf, even though the underlying mechanism is real and
/// verified. Reordering <c>CandidateModes</c> itself is flagged as a distinct, precisely-scoped follow-up if
/// true byte-for-byte parity is pursued further.</para>
/// </summary>
internal static class Av1IntraModelRdPruner
{
    /// <summary><c>prune_intra_y_mode</c>'s own constants (<c>intra_mode_search.c:471-472</c>).</summary>
    private const double ThreshTop = 1.00;

    private const double ThreshBest = 1.50;

    /// <summary>
    /// <c>aom_hadamard_4x4_c</c> (<c>aom_dsp/avg.c</c>) -- a real, distinct transform from this project's own
    /// lossless <see cref="Av1ForwardWht"/> (confirmed by reading libaom's own <c>av1_quick_txfm</c>/
    /// <c>wht_fwd_txfm</c>: the real lossless coding path and this SATD-estimate path deliberately use
    /// <em>different</em> transforms, `aom_hadamard_4x4` here vs. the spec's own WHT for real coding), a
    /// separable 4x4 transform built from two passes of a 4-point butterfly with an intermediate right-shift
    /// (<c>hadamard_col4</c>), ported with the exact same index arithmetic as the original (column pass, then
    /// a cross-column pass over the intermediate buffer, then a final transpose "to match SSE2 behavior" per
    /// libaom's own comment) rather than simplified via abstract separable-transform reasoning, to avoid an
    /// off-by-one/transpose-direction error in a construction this indirect.
    /// </summary>
    internal static void Hadamard4x4(ReadOnlySpan<int> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<int> buffer = stackalloc int[16];
        Span<int> buffer2 = stackalloc int[16];

        for (int idx = 0; idx < 4; idx++)
        {
            HadamardCol4(srcDiff, idx, srcStride, buffer, idx * 4);
        }

        for (int idx = 0; idx < 4; idx++)
        {
            HadamardCol4(buffer, idx, 4, buffer2, idx * 4);
        }

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                coeff[(i * 4) + j] = buffer2[(j * 4) + i];
            }
        }
    }

    /// <summary><c>hadamard_col4</c> (<c>aom_dsp/avg.c</c>) -- a 4-point butterfly with an intermediate right-shift-by-1 (C's arithmetic right shift on a signed value, reproduced exactly by C#'s own <c>&gt;&gt;</c> on <c>int</c>).</summary>
    private static void HadamardCol4(ReadOnlySpan<int> src, int srcOffset, int stride, Span<int> dst, int dstOffset)
    {
        int b0 = (src[srcOffset + (0 * stride)] + src[srcOffset + (1 * stride)]) >> 1;
        int b1 = (src[srcOffset + (0 * stride)] - src[srcOffset + (1 * stride)]) >> 1;
        int b2 = (src[srcOffset + (2 * stride)] + src[srcOffset + (3 * stride)]) >> 1;
        int b3 = (src[srcOffset + (2 * stride)] - src[srcOffset + (3 * stride)]) >> 1;

        dst[dstOffset + 0] = b0 + b2;
        dst[dstOffset + 1] = b1 + b3;
        dst[dstOffset + 2] = b0 - b2;
        dst[dstOffset + 3] = b1 - b3;
    }

    /// <summary><c>aom_satd_c</c> (<c>aom_dsp/avg.c</c>) -- sum of absolute transform-domain coefficients, no entropy modeling at all.</summary>
    internal static long Satd(ReadOnlySpan<int> coeff)
    {
        long satd = 0;
        foreach (int c in coeff)
        {
            satd += Math.Abs(c);
        }

        return satd;
    }

    /// <summary>
    /// <c>prune_intra_y_mode</c> (<c>intra_mode_search.c:467-491</c>): inserts <paramref name="thisModelRd"/>
    /// into the sorted-ascending <paramref name="topModelRd"/> list (size = <c>TopIntraModelCountAllowed</c>,
    /// caller-initialized to <see cref="long.MaxValue"/> and reset once per leaf, never per candidate), then
    /// prunes (returns <see langword="true"/>) when it's worse than either the current <c>Count</c>-th-best
    /// candidate seen so far (by <see cref="ThreshTop"/>) or the single best candidate seen so far (by the
    /// looser <see cref="ThreshBest"/>) -- matching libaom's own two-threshold shape exactly, including
    /// updating the top-K list even for a candidate this call is about to prune.
    ///
    /// <para>Deliberately omits libaom's own <c>get_model_rd_index_for_pruning</c> neighbor-adaptive
    /// refinement (<see cref="Av1SpeedFeatures.AdaptTopModelRdCountUsingNeighbors"/>, active only at effort
    /// &gt;= 6): that refinement narrows which top-K slot is checked based on whether this leaf's own winning
    /// mode (once decided) matches its above/left neighbors' -- a real, effort-gated behavior, but this port
    /// always checks the last slot (<c>topModelRd.Length - 1</c>, i.e. the true <c>Count</c>-th-best), exactly
    /// matching libaom's own behavior at every effort level this project's default (2) and typical range
    /// actually use (<see langword="false"/> until effort 6). Flagged as a real, disclosed gap for the
    /// higher end of the effort range, not a bug in the common case.</para>
    /// </summary>
    internal static bool PruneIntraYMode(long thisModelRd, ref long bestModelRd, Span<long> topModelRd)
    {
        int count = topModelRd.Length;
        for (int i = 0; i < count; i++)
        {
            if (thisModelRd < topModelRd[i])
            {
                for (int j = count - 1; j > i; j--)
                {
                    topModelRd[j] = topModelRd[j - 1];
                }

                topModelRd[i] = thisModelRd;
                break;
            }
        }

        int indexForPruning = count - 1;
        if (topModelRd[indexForPruning] != long.MaxValue && thisModelRd > ThreshTop * topModelRd[indexForPruning])
        {
            return true;
        }

        if (thisModelRd != long.MaxValue && thisModelRd > ThreshBest * bestModelRd)
        {
            return true;
        }

        if (thisModelRd < bestModelRd)
        {
            bestModelRd = thisModelRd;
        }

        return false;
    }

    /// <summary>
    /// <c>model_intra_yrd_and_prune</c> (<c>intra_mode_search_utils.h:671-686</c>) -- a distinct, simpler
    /// single-threshold sibling of <see cref="PruneIntraYMode"/> used specifically to gate a FILTER_INTRA
    /// submode (<c>rd_pick_filter_intra_sby</c>, <c>intra_mode_search.c:275</c>), sharing the same running
    /// <paramref name="bestModelRd"/> the plain-mode loop's own <see cref="PruneIntraYMode"/> calls already
    /// maintain (libaom's own <c>best_model_rd</c> is one value threaded across both loops,
    /// <c>intra_mode_search.c:1610</c> -&gt; <c>:1677</c>) -- not the top-K array <see cref="PruneIntraYMode"/>
    /// tracks, which this sibling never reads or writes. Prunes when <paramref name="thisModelRd"/> exceeds
    /// <c>1.25 * bestModelRd</c> (<c>best_model_rd + (best_model_rd &gt;&gt; 2)</c>, C's integer right-shift
    /// reproduced exactly by C#'s own <c>&gt;&gt;</c> on <see langword="long"/>), otherwise still updates
    /// <paramref name="bestModelRd"/> when this candidate improves on it -- exactly libaom's own two-branch
    /// shape, not simplified into a single comparison.
    /// </summary>
    internal static bool PruneFilterIntraModelRd(long thisModelRd, ref long bestModelRd)
    {
        if (bestModelRd != long.MaxValue && thisModelRd > bestModelRd + (bestModelRd >> 2))
        {
            return true;
        }

        if (thisModelRd < bestModelRd)
        {
            bestModelRd = thisModelRd;
        }

        return false;
    }
}
