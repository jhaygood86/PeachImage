using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Encodes one single-tile intra frame: walks the superblock grid (spec §5.11.4 <c>decode_partition()</c>'s
/// write-side mirror). Non-lossless frames still force every 64x64 superblock to split all the way down to
/// a uniform 8x8 leaf grid (this encoder does not implement non-lossless partition-tree RDO -- see
/// <see cref="EncodePartitionForced"/>'s remarks for why). Lossless frames use 128x128 superblocks instead
/// (<c>use_128x128_superblock</c>, matching libaom's own default choice for non-tiny images -- see
/// <c>Av1SequenceHeaderWriter</c>) and run a real (if approximate) rate-distortion partition search
/// (<see cref="RdPickPartition"/>, Phase D) at each partition level down to spec's true 4x4 floor, comparing
/// the actual per-coefficient rate cost (<see cref="ComputeCandidateCost"/>) of keeping an
/// 8x8/16x16/32x32/64x64/128x128 region as one leaf against the summed cost of its 4 quadrants, rather
/// than a pure flatness/variance threshold -- merging above 4x4 only ever reduces per-leaf mode/skip/partition
/// signaling, never residual coefficient cost by itself (AV1 forces TX_4X4 for every lossless block regardless
/// of coding-block size, so the same number of 4x4 Walsh-Hadamard sub-blocks get coded either way), but a real
/// cost comparison (unlike the pure-flatness heuristic it replaced) can also correctly choose *not* to merge
/// when a coarser single-mode prediction across the merged region would cost more in residual than it saves
/// in signaling -- see <see cref="RdPickPartition"/>'s remarks for the full reasoning and the project plan's
/// Phase A/D results for the measurements motivating this. Every leaf gets a real, rate-cost-based intra mode
/// search (13 candidate modes x 7 angle_delta values for the 8 directional ones -- forced to just angle_delta
/// 0 for a 4x4 leaf, spec's own floor for signaling angle_delta at all, see <see cref="EncodeLeaf"/>'s
/// <c>angleDeltaAllowed</c> remarks -- plus 5 FILTER_INTRA candidates when DC_PRED wins -- see
/// <see cref="EncodeLeaf"/>). Chroma gets the same real directional/angle search too
/// (<see cref="SearchUvMode"/>), for both lossless and non-lossless: non-lossless chroma's transform type is
/// mode-dependent (<c>Av1TxTypeTables.ModeToTxfm</c>), so <see cref="EncodeChromaRegion"/> forward-transforms
/// with the matching DCT/ADST-mixed <c>Av1ForwardTransform</c> operator for whatever <c>uv_mode</c> the search
/// picks, rather than always DCT -- see that method's remarks. CFL (<see cref="TryCflCandidate"/>) is searched
/// for both lossless and non-lossless chroma, gated on spec's own <c>is_cfl_allowed()</c> restriction (always
/// true for non-lossless here; for lossless, only at the one leaf size where the chroma plane's own residual
/// size is exactly 4x4 -- see <see cref="SearchUvMode"/>'s own <c>cflAllowedForCost</c> remarks).
///
/// <para>Requires the luma plane's width/height to already be padded to a multiple of the real superblock
/// size -- 128 for lossless, 64 otherwise (the caller's job -- see <c>Av1FrameEncoder</c>) -- so every
/// superblock is a full, in-bounds block: this eliminates every one of <c>decode_partition()</c>'s
/// edge-of-frame special cases (the <c>hasRows</c>/<c>hasCols</c>-driven HORZ/VERT-forced partitions), which
/// this encoder does not implement.</para>
/// </summary>
internal static class Av1TileEncoder
{
    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1b-ii (Round N+60 root-cause fix) -- the real, "currently
    /// live" per-plane coefficient context a SEARCH/cost-estimation call should seed its own scratch trial
    /// from, phase-aware. <see cref="TileState.YCoeffCtx"/>/<see cref="TileState.UCoeffCtx"/>/
    /// <see cref="TileState.VCoeffCtx"/> only ever advance via real, bitstream-emitting commits (correctly
    /// suppressed for the whole <see cref="Av1EncodePhase.Decide"/> pass) -- reading them directly for a cost
    /// estimate during Decide would silently see an untouched, all-default context for every leaf after the
    /// very first, rather than the real accumulated context every earlier leaf's own decide-tracked commit
    /// already produced in <see cref="TileState.DecideYCoeffCtx"/>/etc. During <see cref="Av1EncodePhase.Fused"/>
    /// and <see cref="Av1EncodePhase.Emit"/>, the real contexts are exactly what's live (real commits happen
    /// in both), so this is a genuine no-op there -- only <see cref="Av1EncodePhase.Decide"/> needs the
    /// substitution. Never used at an actual <c>WriteCoeffs</c> commit call site (real commits always target
    /// <see cref="TileState.YCoeffCtx"/> directly, decide-tracking commits always target
    /// <see cref="TileState.DecideYCoeffCtx"/> directly -- both already correct, unconditional on phase, since
    /// each commit call is itself already gated to only fire in its own matching phase).
    /// </summary>
    private static Av1CoefficientWriter.PlaneContext LiveYCoeffCtx(TileState s) => s.Phase == Av1EncodePhase.Decide ? s.DecideYCoeffCtx : s.YCoeffCtx;

    /// <summary>See <see cref="LiveYCoeffCtx"/>'s own remarks -- the U-plane counterpart.</summary>
    private static Av1CoefficientWriter.PlaneContext LiveUCoeffCtx(TileState s) => s.Phase == Av1EncodePhase.Decide ? s.DecideUCoeffCtx! : s.UCoeffCtx!;

    /// <summary>See <see cref="LiveYCoeffCtx"/>'s own remarks -- the V-plane counterpart.</summary>
    private static Av1CoefficientWriter.PlaneContext LiveVCoeffCtx(TileState s) => s.Phase == Av1EncodePhase.Decide ? s.DecideVCoeffCtx! : s.VCoeffCtx!;

    // All 13 luma intra modes (everything but the chroma-only UV_CFL_PRED) -- Phase D's directional +
    // angle_delta search (see EncodeLeaf) tries every one of the 8 directional modes at every one of AV1's
    // 7 angle_delta values (-3..3, spec MAX_ANGLE_DELTA), not just angle_delta == 0 as this encoder did
    // before Phase D.
    //
    // Order matches libaom's own real `intra_rd_search_mode_order` (av1/encoder/intra_mode_search.c:38-42),
    // not the Av1IntraMode enum's own declaration order -- confirmed by direct source reading, not assumed:
    // DC, H, V, SMOOTH, PAETH, SMOOTH_V, SMOOTH_H, D135, D203, D157, D67, D113, D45. This matters beyond
    // cosmetics: every real RD comparison this array feeds (this method's own mode loops, and
    // Av1IntraModelRdPruner's own SATD-shortlist admission, which iterates in this same order) uses a strict
    // `cost < bestCost`/`this_model_rd < top_intra_model_rd[i]` comparison -- confirmed matching libaom's own
    // real `this_rd < best_rd` (intra_mode_search.c:1651) and `this_model_rd < top_intra_model_rd[i]`
    // (:474) -- so an exact cost tie between two candidates is always won by whichever was evaluated first,
    // and this array's own order is what "first" means. Also used for chroma (SearchUvMode/
    // EstimateLosslessChromaCost): libaom's own real chroma order, `uv_rd_search_mode_order`
    // (intra_mode_search.c:44-49), is DC, CFL, H, V, SMOOTH, PAETH, SMOOTH_V, SMOOTH_H, D135, D203, D157, D67,
    // D113, D45 -- removing CFL (which this project handles via a separate code path, TryCflCandidate, not
    // this array, since its own cost computation -- least-squares alpha estimation plus a real trial window --
    // is structurally unlike every other candidate here) leaves *exactly* this same 13-entry order, so one
    // shared array correctly serves both planes; no separate chroma-specific ordering is needed.
    //
    // Deliberately NOT ported: libaom's own separate, third order for the angle-delta *refinement* phase
    // specifically (raw PREDICTION_MODE enum order -- V, H, D45, D135, D113, D157, D203, D67 -- for mode_idx
    // values past the 13 base modes, intra_mode_search.c:403-421) -- libaom's own real search is a genuinely
    // different two-phase shape (evaluate all 13 base modes once, then refine angle_delta only for whichever
    // directional mode is still live) from this project's own single nested loop (every mode tried at every
    // angle_delta inline). Reproducing that two-phase shape is a separate, larger architectural change than
    // reordering this array, not attempted here.
    private static readonly int[] CandidateModes =
        [
            Av1IntraMode.DcPred, Av1IntraMode.HPred, Av1IntraMode.VPred, Av1IntraMode.SmoothPred, Av1IntraMode.PaethPred,
            Av1IntraMode.SmoothVPred, Av1IntraMode.SmoothHPred, Av1IntraMode.D135Pred, Av1IntraMode.D203Pred,
            Av1IntraMode.D157Pred, Av1IntraMode.D67Pred, Av1IntraMode.D113Pred, Av1IntraMode.D45Pred,
        ];

    private const int MaxAngleDelta = 3;

    /// <summary>
    /// libaom's <c>av1_derived_filter_intra_mode_used_flag</c> (<c>av1/encoder/intra_mode_search.c</c>),
    /// indexed by <see cref="Av1IntraMode"/> value: which of the 5 <c>FILTER_INTRA_MODE</c> sub-modes (bit
    /// order matches <see cref="Decoding.Av1.Av1IntraPrediction"/>'s own <c>IntraFilterTaps</c> indexing --
    /// spec's <c>FILTER_DC_PRED, FILTER_V_PRED, FILTER_H_PRED, FILTER_D157_PRED, FILTER_PAETH_PRED</c> order)
    /// <see cref="Av1SpeedFeatures.PruneFilterIntraLevel"/> level 1 restricts filter_intra evaluation to,
    /// given whichever plain intra mode currently won the real search. Always includes bit 0
    /// (<c>FILTER_DC_PRED</c>) plus whichever sub-mode "corresponds" to the winning mode (e.g. bit 2,
    /// <c>FILTER_H_PRED</c>, when <see cref="Av1IntraMode.HPred"/> won). Indexed by whatever plain mode is
    /// winning so far when filter_intra search runs (<c>EncodeLeaf</c>/<c>EncodeRectangularLeaf</c>'s own
    /// <c>bestMode</c> at that point) -- filter_intra is tried regardless of which plain mode that is,
    /// matching libaom's own unconditional-with-restriction search exactly (see those methods' own remarks;
    /// this table is no longer only ever indexed at <see cref="Av1IntraMode.DcPred"/>).
    /// </summary>
    private static readonly int[] FilterIntraModeUsedFlag =
    [
        0x01, // DcPred
        0x03, // VPred
        0x05, // HPred
        0x01, // D45Pred
        0x01, // D135Pred
        0x01, // D113Pred
        0x09, // D157Pred
        0x01, // D203Pred
        0x01, // D67Pred
        0x01, // SmoothPred
        0x01, // SmoothVPred
        0x01, // SmoothHPred
        0x11, // PaethPred
    ];

    /// <summary>
    /// libaom's <c>av1_derived_chroma_intra_mode_used_flag</c> (<c>av1/encoder/intra_mode_search.c</c>),
    /// indexed by the winning <em>luma</em> mode (<see cref="Av1IntraMode"/> value, DC through Paeth): which
    /// chroma (<c>uv_mode</c>) candidates <see cref="Av1SpeedFeatures.PruneChromaModesUsingLumaWinner"/>
    /// restricts <see cref="SearchUvMode"/>'s own candidate loop to. Bit position equals the candidate's own
    /// <see cref="Av1IntraMode"/> value directly (DC=bit0 ... Paeth=bit12, CFL=bit13 -- CFL is never a valid
    /// value to index this table BY, but is always one of the bits every row sets). Every row unconditionally
    /// keeps DC (bit 0), SMOOTH (bit 9), and CFL (bit 13) available regardless of the luma winner -- confirmed
    /// by direct reading, not an approximation -- plus exactly one more bit for whichever directional/
    /// SMOOTH_V/SMOOTH_H/Paeth mode numerically matches the luma winner itself (already included in the DC/
    /// SMOOTH_PRED rows' own base set, so those two rows have no extra bit beyond it). CFL's own candidate is
    /// evaluated through a completely separate code path in <see cref="SearchUvMode"/>
    /// (<see cref="TryCflCandidate"/>, gated on <c>cflAllowedForCost</c> alone), so this table is only ever
    /// consulted inside the main <see cref="CandidateModes"/> loop and never needs to gate CFL itself.
    /// </summary>
    private static readonly int[] ChromaModeUsedFlagByLumaWinner =
    [
        0x2201, // DcPred:      DC, Smooth, Cfl
        0x2203, // VPred:       + V
        0x2205, // HPred:       + H
        0x2209, // D45Pred:     + D45
        0x2211, // D135Pred:    + D135
        0x2221, // D113Pred:    + D113
        0x2241, // D157Pred:    + D157
        0x2281, // D203Pred:    + D203
        0x2301, // D67Pred:     + D67
        0x2201, // SmoothPred:  DC, Smooth, Cfl
        0x2601, // SmoothVPred: + SmoothV
        0x2a01, // SmoothHPred: + SmoothH
        0x3201, // PaethPred:   + Paeth
    ];

    /// <summary>
    /// Computes the two libaom speed features that prune LUMA's own <see cref="CandidateModes"/> search --
    /// <see cref="Av1SpeedFeatures.IntraPruningWithHog"/> (populates <paramref name="directionalModeSkipMask"/>,
    /// see <see cref="Av1IntraHogPruner"/>'s own remarks) and <see cref="Av1SpeedFeatures.DisableSmoothIntra"/>
    /// (<paramref name="skipSmoothVh"/>/<paramref name="skipSmoothPlain"/>) -- shared by <see cref="EstimateLumaCost(TileState, int, int, int)"/>
    /// and <see cref="EncodeLeaf"/>'s real search so both apply the identical restriction (an
    /// estimate that considers a candidate the real commit will then prune away would bias the partition
    /// decision toward an achievable-looking cost the real search can never actually deliver). Deliberately a
    /// complete no-op (every output left at "don't prune anything") whenever <paramref name="lossless"/> is
    /// false: <see cref="TileState.SpeedFeatures"/> is only ever meaningful for lossless (see
    /// <c>EncodeTile</c>'s own <c>effort</c> parameter remarks), and non-lossless behavior must never change
    /// as a side effect of this.
    ///
    /// <para>libaom's own comment on why SMOOTH_PRED needs special-casing here (<c>intra_mode_search.c</c>):
    /// "the functionality of filter intra modes and smooth prediction overlap. Hence smooth prediction is
    /// pruned only if all the filter intra modes are enabled" -- i.e. SMOOTH_H_PRED/SMOOTH_V_PRED are always
    /// dropped once <see cref="Av1SpeedFeatures.DisableSmoothIntra"/> is set, but plain SMOOTH_PRED survives
    /// unless filter_intra is <em>also</em> unpruned (<see cref="Av1SpeedFeatures.PruneFilterIntraLevel"/> ==
    /// 0) -- a genuinely non-obvious interaction between two different fields, confirmed by directly reading
    /// libaom's own real mode loop rather than assumed from the field names alone.</para>
    /// </summary>
    private static void ComputeLumaPruning(TileState s, bool lossless, int[] source, int stride, int x, int y, int widthPixels, int heightPixels, Span<bool> directionalModeSkipMask, out bool skipSmoothVh, out bool skipSmoothPlain)
    {
        skipSmoothVh = false;
        skipSmoothPlain = false;
        if (!lossless)
        {
            return;
        }

        if (s.SpeedFeatures.IntraPruningWithHog > 0)
        {
            Av1IntraHogPruner.ComputeSkipMask(source, stride, x, y, widthPixels, heightPixels, Av1IntraHogPruner.Thresh[s.SpeedFeatures.IntraPruningWithHog - 1], chromaSubsamplingScale: 1, directionalModeSkipMask);
        }

        skipSmoothVh = s.SpeedFeatures.DisableSmoothIntra;
        skipSmoothPlain = skipSmoothVh && s.SpeedFeatures.PruneFilterIntraLevel == 0;
    }

    /// <summary>Whether <paramref name="mode"/> should be skipped in LUMA's own candidate search, given the pruning state <see cref="ComputeLumaPruning"/> computed.</summary>
    private static bool IsLumaModePruned(int mode, ReadOnlySpan<bool> directionalModeSkipMask, bool skipSmoothVh, bool skipSmoothPlain)
    {
        if (Av1IntraMode.IsDirectional(mode) && directionalModeSkipMask[mode])
        {
            return true;
        }

        if (skipSmoothVh && (mode == Av1IntraMode.SmoothVPred || mode == Av1IntraMode.SmoothHPred))
        {
            return true;
        }

        return skipSmoothPlain && mode == Av1IntraMode.SmoothPred;
    }

    // Matches Av1TileDecoder's own BlockDecodedStride exactly (34 = 32 sub-4x4 units per 128x128-superblock
    // side + a 2-wide margin for the -1 offset both sides of the array need) -- see BlockDecoded's remarks.
    private const int BlockDecodedStride = 34;

    /// <summary>
    /// Encodes the tile and returns its raw byte payload (ready to wrap in a <c>tile_group_obu()</c>).
    /// <paramref name="yPlane"/>/<paramref name="uPlane"/>/<paramref name="vPlane"/> are the true source
    /// planes (already padded); <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/>
    /// are same-sized output buffers this method fills with the encoder's own local reconstruction (the
    /// same pixels a real decoder will independently reconstruct from this tile's bitstream) -- callers
    /// that only need the encoded bytes may pass fresh same-sized arrays and ignore them.
    /// </summary>
    /// <param name="yPlane">The true (already-padded) source luma plane.</param>
    /// <param name="yWidth">The padded luma plane width.</param>
    /// <param name="yHeight">The padded luma plane height.</param>
    /// <param name="uPlane">The true (already-padded) source chroma-U plane, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="vPlane">The true (already-padded) source chroma-V plane, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="chromaWidth">The padded chroma plane width.</param>
    /// <param name="chromaHeight">The padded chroma plane height.</param>
    /// <param name="reconY">Output buffer this method fills with the encoder's own local luma reconstruction.</param>
    /// <param name="reconU">Output buffer this method fills with the encoder's own local chroma-U reconstruction, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="reconV">Output buffer this method fills with the encoder's own local chroma-V reconstruction, or <see langword="null"/> when <paramref name="monoChrome"/>.</param>
    /// <param name="monoChrome">Whether this frame is monochrome (no chroma planes).</param>
    /// <param name="baseQIdx">The frame's base quantizer index.</param>
    /// <param name="lossless">
    /// When <see langword="true"/>, every block's transform is AV1's lossless Walsh-Hadamard path
    /// (<see cref="Av1ForwardWht"/>) at 4x4 granularity instead of DCT_DCT (a leaf's luma area splits into
    /// <c>(leafSize/4)^2</c> 4x4 transform sub-blocks, matching AV1's forced <c>TX_4X4</c>-when-lossless
    /// rule; chroma follows suit, at 4x4 either way -- see <paramref name="chroma444"/> for how many chroma
    /// sub-blocks that means per leaf). <paramref name="baseQIdx"/> must be 0 in that case (AV1's
    /// coded-lossless trigger).
    /// </param>
    /// <param name="chroma444">
    /// Whether <paramref name="uPlane"/>/<paramref name="vPlane"/> are full luma resolution (4:4:4, no
    /// subsampling) rather than half-resolution in both dimensions (4:2:0, this encoder's only other mode).
    /// Only ever <see langword="true"/> together with <paramref name="lossless"/> and a non-monochrome frame
    /// -- see <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate for why. Each luma leaf gets a
    /// matching same-size chroma region (mirroring luma's own lossless sub-block pattern) instead of 4:2:0's
    /// half-resolution chroma region.
    /// </param>
    /// <param name="effort">
    /// libaom ALL_INTRA-mode <c>--cpu-used</c> equivalent, 0-9 (see <see cref="AvifEncoderOptions.Effort"/>
    /// and <see cref="Av1SpeedFeatures"/>). Only meaningful when <paramref name="lossless"/>.
    /// </param>
    /// <param name="allowScreenContentTools">
    /// Real, content-based decision (<see cref="Av1ScreenContentEstimator"/>) for whether palette is
    /// structurally present in this frame's bitstream -- see <see cref="TileState.AllowScreenContentTools"/>.
    /// Must be <see langword="false"/> whenever <paramref name="lossless"/> is <see langword="false"/> (the
    /// caller, <see cref="Av1FrameEncoder.Encode"/>, only ever computes a real estimate for lossless frames).
    /// </param>
    /// <param name="allowIntrabc">
    /// Real, content-based decision for whether IntraBC is structurally present -- see
    /// <see cref="TileState.AllowIntrabc"/>. Only ever <see langword="true"/> when
    /// <paramref name="allowScreenContentTools"/> also is (spec requires both).
    /// </param>
    /// <param name="trueWidth">
    /// The bitstream's own real, unpadded frame width (<c>headerWidth</c> in <see cref="Av1FrameEncoder.Encode"/>'s
    /// terms) -- see <see cref="TileState.TrueMiCols"/> for why this can differ from <paramref name="yWidth"/>.
    /// Defaults to 0, meaning "same as <paramref name="yWidth"/>" (used only by direct test call sites that
    /// don't otherwise care about this distinction).
    /// </param>
    /// <param name="trueHeight">The height counterpart to <paramref name="trueWidth"/>; see its remarks.</param>
    /// <param name="onLeafCommitted">
    /// Diagnostic-only hook (project plan's Phase 1/Step 6 structural decision-log tool,
    /// <c>tools/PeachImage.LibaomParity</c>): invoked once per real leaf commit with a full
    /// <see cref="Av1BlockDecisionRecord"/> of that leaf's own decision. <see langword="null"/> in every
    /// production call site -- never allocates or checks anything beyond one null-check per leaf when unset.
    /// </param>
    public static byte[] EncodeTile(
        int[] yPlane, int yWidth, int yHeight,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        int[] reconY, int[]? reconU, int[]? reconV,
        bool monoChrome, int baseQIdx, bool lossless = false, bool chroma444 = false, int effort = 2, bool allowScreenContentTools = false, bool allowIntrabc = false, int trueWidth = 0, int trueHeight = 0, Action<Av1BlockDecisionRecord>? onLeafCommitted = null)
    {
        var state = BuildTileState(yPlane, yWidth, yHeight, uPlane, vPlane, chromaWidth, chromaHeight, reconY, reconU, reconV, monoChrome, baseQIdx, lossless, chroma444, effort, allowScreenContentTools, allowIntrabc, trueWidth, trueHeight, onLeafCommitted);

        try
        {
            // Superblock size: 128x128 (sizeMi 32) for lossless, matching Av1SequenceHeaderWriter's
            // use_128x128_superblock signaling (always exactly lossless -- see its own remarks) and
            // Av1FrameEncoder's matching 128-pixel-multiple padding; 64x64 (sizeMi 16) otherwise, this
            // encoder's original, still-current non-lossless configuration.
            int sbSizeMi = lossless ? 32 : 16;
            for (int r = 0; r < state.MiRows; r += sbSizeMi)
            {
                for (int c = 0; c < state.MiCols; c += sbSizeMi)
                {
                    // Real port of libaom's av1_set_cost_upd_freq (see TileState.CostCdf's own remarks):
                    // every RD-search cost estimate for this superblock -- partition-type comparison and
                    // every leaf's own real mode search alike -- reads this frozen snapshot, taken here,
                    // once, reflecting everything committed by every earlier superblock but nothing from
                    // this one yet (nothing below mutates Cdf until a leaf is actually committed).
                    //
                    // Two-pass tile-encoder architecture, Stage 1b-i (see TileState.DecideCdf's own
                    // remarks): DecideCdf refreshed from Cdf first, then CostCdf from DecideCdf -- today a
                    // pure pass-through (DecideCdf always exactly mirrors Cdf at this point, since decide
                    // and commit still happen together per superblock), but this is the real plumbing
                    // EmitTile reuses once DecideCdf's own refresh stops being a Cdf copy and becomes a
                    // genuine, independent decide-time adaptation instead.
                    state.DecideCdf.CopyFrom(state.Cdf);
                    state.CostCdf.CopyFrom(state.DecideCdf);

                    // Two-pass tile-encoder architecture, Stage 1b (Round N+57) -- see
                    // TileState.CostCdfSnapshots's own remarks: an independent deep copy (not a reference),
                    // captured here so EmitTile can re-derive this exact superblock's own CostCdf value
                    // instead of reading DecideCdf's by-then-final, whole-frame-adapted state. Purely
                    // additive today (this fused path never reads it back) -- kept for parity with
                    // DecideTile below, which real EmitTile calls do consume.
                    var costCdfSnapshot = new Av1CdfContext(baseQIdx);
                    costCdfSnapshot.CopyFrom(state.CostCdf);
                    state.CostCdfSnapshots.Add(costCdfSnapshot);

                    ClearBlockDecodedFlags(state, r, c, sbSize4: sbSizeMi);

                    // Decide phase runs to completion for this whole superblock's own subtree in one
                    // recursive pass, before any of it is actually committed -- unlike the old, lazy
                    // per-node DecidePartition (called on demand by EncodePartitionForced itself as it
                    // walked down), RdPickPartition's own real bound-threading needs the whole recursive
                    // decision tree available up front: a child's own inbound bound comes from its parent's
                    // still-in-progress candidate comparison, not from anything EncodePartitionForced's own
                    // separate, later, decision-agnostic walk could reconstruct after the fact.
                    DecideSuperblockPartition(state, r, c, sbSizeMi);
                    EncodePartitionForced(state, r, c, sizeMi: sbSizeMi);
                }
            }

            return state.Symbols.Flush();
        }
        finally
        {
            ReturnTileStateBuffers(state);
        }
    }

    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1b-ii: a parallel, switchable path alongside the fused
    /// <see cref="EncodeTile"/> above -- not yet called from any production code path (<see cref="Av1FrameEncoder"/>
    /// still calls <see cref="EncodeTile"/> exclusively). Runs <see cref="DecideTile"/> to completion for the
    /// WHOLE frame (zero real bitstream bytes written -- see <see cref="Av1EncodePhase"/>'s own remarks),
    /// then <see cref="EmitTile"/> to completion for the whole frame (real bitstream bytes only). Exists so
    /// this split can be verified byte-identical against <see cref="EncodeTile"/>'s own real output on real
    /// images before anything production-facing switches over to it, per the project plan's own explicit
    /// "build as a parallel, switchable path first" discipline.
    /// </summary>
    internal static byte[] EncodeTileTwoPass(
        int[] yPlane, int yWidth, int yHeight,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        int[] reconY, int[]? reconU, int[]? reconV,
        bool monoChrome, int baseQIdx, bool lossless = false, bool chroma444 = false, int effort = 2, bool allowScreenContentTools = false, bool allowIntrabc = false, int trueWidth = 0, int trueHeight = 0, Action<Av1BlockDecisionRecord>? onLeafCommitted = null)
    {
        var state = BuildTileState(yPlane, yWidth, yHeight, uPlane, vPlane, chromaWidth, chromaHeight, reconY, reconU, reconV, monoChrome, baseQIdx, lossless, chroma444, effort, allowScreenContentTools, allowIntrabc, trueWidth, trueHeight, onLeafCommitted);

        try
        {
            int sbSizeMi = lossless ? 32 : 16;
            DecideTile(state, baseQIdx, sbSizeMi);
            EmitTile(state, sbSizeMi);
            return state.Symbols.Flush();
        }
        finally
        {
            ReturnTileStateBuffers(state);
        }
    }

    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1b-ii -- the Decide pass: walks every superblock in the
    /// frame exactly once, in the same order <see cref="EncodeTile"/>'s own fused loop already does, running
    /// every real search (<see cref="DecideSuperblockPartition"/>/<see cref="EncodePartitionForced"/>, which
    /// still recurses into <see cref="EncodeLeaf"/>/<see cref="EncodeRectangularLeaf"/>'s own unmodified
    /// search+reconstruct logic) but writing zero real bitstream bytes -- <see cref="TileState.Phase"/> is
    /// <see cref="Av1EncodePhase.Decide"/> throughout, which every dual-call site already gates correctly
    /// (Rounds N+58/N+59). <see cref="TileState.DecideCdf"/> is seeded exactly once here, at the very start
    /// (not per-superblock, unlike the fused loop's own <c>CopyFrom(Cdf)</c> refresh) -- see its own remarks
    /// for why a per-superblock reset would be wrong once Decide is a genuine whole-frame pass: it would
    /// silently discard every earlier superblock's own real decide-time CDF adaptation instead of building on
    /// it. Real reconstruction (<c>ReconY</c>/<c>ReconU</c>/<c>ReconV</c>) still happens for real here,
    /// unconditionally, exactly as it always has -- <see cref="EmitTile"/> depends on it already being final.
    /// </summary>
    private static void DecideTile(TileState state, int baseQIdx, int sbSizeMi)
    {
        state.Phase = Av1EncodePhase.Decide;
        state.DecideCdf.CopyFrom(state.Cdf);

        for (int r = 0; r < state.MiRows; r += sbSizeMi)
        {
            for (int c = 0; c < state.MiCols; c += sbSizeMi)
            {
                state.CostCdf.CopyFrom(state.DecideCdf);

                var costCdfSnapshot = new Av1CdfContext(baseQIdx);
                costCdfSnapshot.CopyFrom(state.CostCdf);
                state.CostCdfSnapshots.Add(costCdfSnapshot);

                ClearBlockDecodedFlags(state, r, c, sbSize4: sbSizeMi);
                DecideSuperblockPartition(state, r, c, sbSizeMi);
                EncodePartitionForced(state, r, c, sizeMi: sbSizeMi);
            }
        }
    }

    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1b-ii -- the Emit pass: walks every superblock a SECOND
    /// time, in the identical order <see cref="DecideTile"/> just did, re-running the exact same
    /// <see cref="EncodePartitionForced"/>/<see cref="EncodeLeaf"/>/<see cref="EncodeRectangularLeaf"/> logic
    /// (unmodified -- <see cref="TileState.Phase"/> is <see cref="Av1EncodePhase.Emit"/> throughout, which
    /// every dual-call site already gates to real-only) rather than replaying persisted decisions from grids.
    /// This is deliberately a full re-run, not a grid-load-and-skip: every decided value (mode search,
    /// filter_intra, palette, IntraBC candidate ranking, tx-depth/trellis) is a pure, deterministic function
    /// of state that is either already final and unchanged (<c>ReconY</c>/<c>ReconU</c>/<c>ReconV</c>, real
    /// source pixels, the immutable whole-frame <see cref="TileState.IntrabcHashTable"/>) or is explicitly
    /// re-derived to match Decide's own point-in-time value exactly:
    /// <list type="bullet">
    /// <item><description><see cref="TileState.CostCdf"/> is refreshed from this superblock's own
    /// <see cref="TileState.CostCdfSnapshots"/> entry (captured during Decide), not from
    /// <see cref="TileState.DecideCdf"/> -- which by now holds its final, whole-frame-adapted state, not the
    /// intermediate value Decide's own search actually read for this superblock. Every real RD comparison in
    /// this file (mode search, palette, IntraBC ranking, tx-depth, <see cref="OptimizeCoeffTrellis"/>) reads
    /// only <see cref="TileState.CostCdf"/>, never <see cref="TileState.Cdf"/> directly -- so this one
    /// substitution is sufficient to make every comparison reproduce Decide's own answer exactly.</description></item>
    /// <item><description><see cref="TileState.BlockDecoded"/> needs no special handling: its own addressing
    /// is always superblock-local (<c>r &amp; SbMiMask</c>/<c>c &amp; SbMiMask</c>, see
    /// <see cref="ClearBlockDecodedFlags"/>), so it never carries information across superblocks in the first
    /// place -- the same per-superblock <see cref="ClearBlockDecodedFlags"/> call this loop already makes
    /// resets it identically to how Decide's own loop did.</description></item>
    /// <item><description><see cref="TileState.PositionsBySize"/>/<see cref="TileState.IntrabcSignatureIndex"/>
    /// (IntraBC's causal candidate index, incrementally built by <see cref="RecordIntrabcHashEntry"/> as each
    /// leaf commits) DO carry information across the whole frame and must be reset to empty before this pass
    /// begins -- without this, every leaf's IntraBC search here would see the ENTIRE frame's positions
    /// (already fully populated by Decide), including leaves that come later in bitstream order, which a real
    /// decoder could never do.</description></item>
    /// </list>
    /// Real bitstream writes now fire for real (<see cref="Av1EncodePhase.Emit"/> unblocks them at every
    /// gated call site); decide-tracking writes are correctly suppressed the same way.
    /// </summary>
    private static void EmitTile(TileState state, int sbSizeMi)
    {
        state.Phase = Av1EncodePhase.Emit;
        state.PositionsBySize.Clear();
        state.IntrabcSignatureIndex.Clear();

        int superblockIndex = 0;
        for (int r = 0; r < state.MiRows; r += sbSizeMi)
        {
            for (int c = 0; c < state.MiCols; c += sbSizeMi)
            {
                state.CostCdf.CopyFrom(state.CostCdfSnapshots[superblockIndex]);
                superblockIndex++;

                ClearBlockDecodedFlags(state, r, c, sbSize4: sbSizeMi);
                EncodePartitionForced(state, r, c, sizeMi: sbSizeMi);
            }
        }
    }

    private static void ReturnTileStateBuffers(TileState state)
    {
        AvifBufferPool.SharedInt32.Return(state.Pred);
        AvifBufferPool.SharedInt32.Return(state.BestPred);
        AvifBufferPool.SharedInt32.Return(state.Residual);
        AvifBufferPool.SharedInt32.Return(state.Coeff);
        AvifBufferPool.SharedInt32.Return(state.Levels);
        AvifBufferPool.SharedInt32.Return(state.ReconDequant);
        AvifBufferPool.SharedInt32.Return(state.ReconResidual);
        AvifBufferPool.SharedInt32.Return(state.CflLumaAc);
        AvifBufferPool.SharedInt32.Return(state.LumaLevels);
        AvifBufferPool.SharedInt32.Return(state.PaletteColorMap);
        AvifBufferPool.SharedInt32.Return(state.PaletteColorMapUv);
        AvifBufferPool.SharedInt32.Return(state.PaletteTrialColorMap);
        AvifBufferPool.SharedInt32.Return(state.PaletteKMeansDataY);
        AvifBufferPool.SharedInt32.Return(state.PaletteKMeansDataU);
        AvifBufferPool.SharedInt32.Return(state.PaletteKMeansDataV);
    }

    private static TileState BuildTileState(
        int[] yPlane, int yWidth, int yHeight,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        int[] reconY, int[]? reconU, int[]? reconV,
        bool monoChrome, int baseQIdx, bool lossless, bool chroma444, int effort, bool allowScreenContentTools, bool allowIntrabc, int trueWidth, int trueHeight, Action<Av1BlockDecisionRecord>? onLeafCommitted)
    {
        int miCols = yWidth / 4;
        int miRows = yHeight / 4;

        // Real (unpadded) frame dimensions in mi units -- spec's own MiCols/MiRows formula (§5.5.1's
        // compute_image_size(), matching Av1FrameHeaderWriter.Write's identical computation for the same
        // headerWidth/headerHeight), NOT yWidth/yHeight's own miCols/miRows above: those describe this
        // encoder's internal, superblock-padded working canvas (needed so its traversal never has to special-
        // case a partial superblock), while TrueMiCols/TrueMiRows describe where the bitstream's own coded
        // frame edge actually falls. For non-lossless, Av1FrameEncoder always passes the padded dimensions
        // here too (see its own remarks), making TrueMiCols/TrueMiRows == MiCols/MiRows exactly and every
        // hasRows/hasCols restriction below a guaranteed no-op -- this parameter only ever does something new
        // for lossless.
        int trueMiCols = 2 * (((trueWidth == 0 ? yWidth : trueWidth) + 7) >> 3);
        int trueMiRows = 2 * (((trueHeight == 0 ? yHeight : trueHeight) + 7) >> 3);

        // Chroma is always 4:4:4 (subX = subY = 0) whenever lossless has chroma planes at all (see
        // Av1FrameEncoder's own chroma444 remarks) -- so the lossless case where TrueMiCols/TrueMiRows differ
        // from MiCols/MiRows only ever needs this same, unsubsampled edge bound. Non-lossless's real 4:2:0
        // subsampling (subX = subY = 1) only matters here in the always-no-op case (TrueMiCols == MiCols), so
        // this still exactly reproduces this encoder's original ChromaWidth-1/ChromaHeight-1 there.
        int chromaSubX = chroma444 || monoChrome ? 0 : 1;
        int chromaSubY = chroma444 || monoChrome ? 0 : 1;

        var cdf = new Av1CdfContext(baseQIdx);
        var symbols = new Av1SymbolEncoder(disableCdfUpdate: false);

        var speedFeatures = Av1SpeedFeatures.Compute(effort, allowScreenContentTools);

        var state = new TileState
        {
            OnLeafCommitted = onLeafCommitted,
            SpeedFeatures = speedFeatures,
            AllowScreenContentTools = allowScreenContentTools,
            AllowIntrabc = allowIntrabc,
            TrueMiCols = trueMiCols,
            TrueMiRows = trueMiRows,
            EdgeMaxX = (trueMiCols * 4) - 1,
            EdgeMaxY = (trueMiRows * 4) - 1,
            ChromaEdgeMaxX = ((trueMiCols * 4) >> chromaSubX) - 1,
            ChromaEdgeMaxY = ((trueMiRows * 4) >> chromaSubY) - 1,
            SourceY = yPlane,
            SourceU = uPlane,
            SourceV = vPlane,
            ReconY = reconY,
            ReconU = reconU,
            ReconV = reconV,
            YWidth = yWidth,
            YHeight = yHeight,
            ChromaWidth = chromaWidth,
            ChromaHeight = chromaHeight,
            MonoChrome = monoChrome,
            MiCols = miCols,
            MiRows = miRows,
            BaseQIdx = baseQIdx,
            Lossless = lossless,
            Chroma444 = chroma444,

            // false unconditionally, matching real aomenc's own observed CLI default -- see this field's
            // own remarks (TileState.ReducedTxSet) for the libaom-source confirmation and the Round N+43
            // bug hunt that made this safe to enable (a real encoder/decoder CDF-context desync in
            // Av1CoefficientWriter's own WriteEobPt, see its remarks).
            ReducedTxSet = false,
            SbMiMask = lossless ? 31 : 15,
            Cdf = cdf,

            // Refreshed for real before any read (the top-level superblock loop below refreshes it, once
            // per SB, before that SB's own EncodePartitionForced call even begins) -- built from defaults
            // here exactly like ScratchCdf's own identical construction, for the same reason (shape parity
            // via baseQIdx; values are irrelevant until the first real refresh).
            CostCdf = new Av1CdfContext(baseQIdx),

            // Two-pass tile-encoder architecture, Stage 1b-i -- see TileState.DecideCdf's own remarks.
            // Same construction pattern as CostCdf/ScratchCdf above, for the same reason (shape parity via
            // baseQIdx; values irrelevant until the first real refresh, which happens before any read).
            DecideCdf = new Av1CdfContext(baseQIdx),

            // Two-pass tile-encoder architecture, Stage 1b (Round N+57) -- see TileState.CostCdfSnapshots's
            // own remarks. Empty until the top-level superblock loop below appends this frame's first
            // snapshot.
            CostCdfSnapshots = [],

            // Two-pass tile-encoder architecture, Stage 1b-ii -- see Av1EncodePhase's own remarks. Explicit
            // here (matching the field's own default) rather than left implicit, since a future
            // DecideTile/EmitTile reusing this same construction sets this to Decide/Emit instead.
            Phase = Av1EncodePhase.Fused,

            Symbols = symbols,
            YModes = new int[miCols * miRows],
            UvModes = new int[miCols * miRows],
            MiSizes = new int[miCols * miRows],

            // Per-mi-unit resolved luma tx_size (Av1TxSize), the write-side mirror of Av1TileDecoder's own
            // _interTxSizes -- only meaningful once !lossless (see EncodeLeaf's own tx-size-RDO remarks);
            // never read/written for a lossless frame (tx_size there is always TX_4X4, and this project's
            // frames are never a mix of lossless/non-lossless leaves -- Lossless is a frame-wide flag here).
            LumaTxSizes = new int[miCols * miRows],
            Skips = new bool[miCols * miRows],

            // Two-pass tile-encoder architecture, Stage 1a -- see TileState's own field remarks.
            AngleDeltaYGrid = new int[miCols * miRows],
            AngleDeltaUvGrid = new int[miCols * miRows],
            FilterIntraGrid = new bool[miCols * miRows],
            FilterIntraModeGrid = new int[miCols * miRows],
            TxTypeGrid = new int[miCols * miRows],
            CflAlphaUGrid = new int[miCols * miRows],
            CflAlphaVGrid = new int[miCols * miRows],
            PaletteColorMapYGrid = new int[yWidth * yHeight],
            PaletteColorMapUvGrid = new int[chromaWidth * chromaHeight],

            IsInters = new bool[miCols * miRows],
            MvRowsGrid = new int[miCols * miRows],
            MvColsGrid = new int[miCols * miRows],
            Written = new bool[miCols * miRows],
            PositionsBySize = [],
            IntrabcSignatureIndex = [],
            BlockDecoded = [new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride], new bool[BlockDecodedStride * BlockDecodedStride]],
            PartitionDecisions = [],
            PaletteSizesY = new int[miCols * miRows],
            PaletteSizesUV = new int[miCols * miRows],
            PaletteColorsYGrid = new int[miCols * miRows * 8],
            PaletteColorsUGrid = new int[miCols * miRows * 8],
            PaletteColorsY = new int[8],
            PaletteColorsU = new int[8],
            PaletteColorsV = new int[8],
            // 128*128, not 64*64: lossless's real leaf ceiling is now a full 128x128 superblock (see
            // Av1TileEncoder's own remarks on 128x128 superblocks), and a palette leaf can be that big.
            PaletteColorMap = AvifBufferPool.SharedInt32.Rent(128 * 128),

            // Second, independent color-map scratch buffer: needed only by the approximate-palette
            // residual commit path in EncodeLeaf, which (per the AV1 spec's palette_tokens() ordering)
            // must write BOTH planes' color-map tokens before encoding EITHER plane's residual -- so
            // Y's map must still be intact when UV's map is built, ruling out reusing PaletteColorMap
            // (the single shared buffer every other palette path already reuses) for both at once.
            PaletteColorMapUv = AvifBufferPool.SharedInt32.Rent(128 * 128),
            PaletteTrialColorMap = AvifBufferPool.SharedInt32.Rent(64 * 64),
            PaletteTrialColorsY = new int[8],
            PaletteTrialColorsU = new int[8],
            PaletteTrialColorsV = new int[8],
            PaletteKMeansDataY = AvifBufferPool.SharedInt32.Rent(64 * 64),
            PaletteKMeansDataU = AvifBufferPool.SharedInt32.Rent(64 * 64),
            PaletteKMeansDataV = AvifBufferPool.SharedInt32.Rent(64 * 64),
            PaletteTopColors = new int[8],
            PaletteCountBuf = new int[256],
            YCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            UCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),
            VCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            // Same sizing as the real YCoeffCtx/UCoeffCtx/VCoeffCtx immediately above.
            DecideYCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            DecideUCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),
            DecideVCoeffCtx = monoChrome ? null : new Av1CoefficientWriter.PlaneContext(chroma444 ? miCols : miCols / 2, chroma444 ? miRows : miRows / 2),
            DecideCommitSink = new Av1AdaptingTrialSymbolSink(),

            // Sized like YCoeffCtx (the largest of Y/U/V, since chroma's x4/y4 range is always a subset of
            // luma's numeric range -- true even at 4:4:4, where they're equal) so one shared, reused scratch
            // buffer safely backs ComputeCandidateCost's trial costing for every plane -- see
            // Av1CoefficientWriter.PlaneContext.SeedFrom's remarks.
            ScratchCoeffCtx = new Av1CoefficientWriter.PlaneContext(miCols, miRows),
            TrialSink = new Av1TrialSymbolSink(),

            // Same baseQIdx as the real cdf above, so ScratchCdf's coefficient tables start with matching
            // shapes (its own construction picks the same default quantizer-indexed slice) -- CopyFrom only
            // ever copies element values into these already-allocated arrays, never reassigns them, so
            // shape parity here is required. Its actual values are irrelevant until the first CopyFrom call
            // (every use site reseeds before reading), so building it from defaults like this is fine.
            ScratchCdf = new Av1CdfContext(baseQIdx),
            AdaptingTrialSink = new Av1AdaptingTrialSymbolSink(),
            Lambda = Av1RdCost.QIndexToLambda(baseQIdx),

            // Rented once for the whole tile and reused/overwritten across every block below, rather than
            // allocated fresh per block. Pred/BestPred must be sized for the largest leaf this encoder can
            // now produce (128x128 = 16384 elements, lossless only -- a full 128x128 superblock kept as one
            // leaf, see EncodeTile's own remarks on 128x128 superblocks): the whole-leaf mode search predicts
            // into them at the leaf's real size before any residual coding happens.
            // Residual/Coeff/Levels/ReconResidual only ever hold one transform block's worth of data at a
            // time -- 1024 elements covers this encoder's largest single transform (a non-lossless 32x32
            // DCT_DCT leaf, see EncodePartitionForced/EncodeLeaf's partition/TX-size RDO remarks; also enough
            // for the largest non-lossless chroma region a 32x32 luma leaf produces at 4:2:0, 16x16 = 256) --
            // or 16 for any lossless 4x4 WHT sub-block, regardless of how big the coding block containing it
            // is. ReconDequant alone stays fixed at 64*64 regardless of block size -- see
            // Av1LocalReconstructor.Reconstruct's remarks on why that stride can't shrink.
            Pred = AvifBufferPool.SharedInt32.Rent(128 * 128),
            BestPred = AvifBufferPool.SharedInt32.Rent(128 * 128),
            Residual = AvifBufferPool.SharedInt32.Rent(32 * 32),
            Coeff = AvifBufferPool.SharedInt32.Rent(32 * 32),
            Levels = AvifBufferPool.SharedInt32.Rent(32 * 32),
            ReconDequant = AvifBufferPool.SharedInt32.Rent(64 * 64),
            ReconResidual = AvifBufferPool.SharedInt32.Rent(32 * 32),

            // Separate from Residual: TryCflPlane's alpha-candidate loop needs this to stay stable across
            // multiple ComputeCandidateCost calls, but ComputeCandidateCost's own non-lossless branch
            // clobbers TileState.Residual as its own scratch on every call -- aliasing the two would silently
            // feed ApplyCflAlpha the previous candidate's leftover pixel residual instead of real luma AC
            // data from the second alpha candidate onward.
            CflLumaAc = AvifBufferPool.SharedInt32.Rent(32 * 32),

            // Non-lossless luma's real, final quantized levels, computed and cached before SearchUvMode runs
            // (so CFL's search has this leaf's own real reconstructed luma to work with) and consumed later
            // at the leaf's normal bitstream-order commit position. Separate from TileState.Levels: that
            // buffer is shared, trial-only scratch that ComputeCandidateCost's own non-lossless branch
            // (called many times over, by SearchUvMode's mode loop and CFL alike) clobbers on every call.
            LumaLevels = AvifBufferPool.SharedInt32.Rent(32 * 32),

            // See TileState.SubBlockAbove/SubBlockLeft's own remarks: shared 4x4-sub-block edge scratch,
            // rented once for the whole tile like every other buffer above, instead of every per-4x4-sub-block
            // prediction call allocating its own pair.
            SubBlockAbove = new Av1EdgeArray(16),
            SubBlockLeft = new Av1EdgeArray(16),
        };

        // Built once, up front, from this tile's own already-fully-known source pixels -- matching libaom's
        // own call site (encode_frame_internal, before any block's own RD search runs). Only when IntraBC can
        // ever be used at all (see IntrabcHashTable's own remarks): building it for a frame that will never
        // query it would be pure waste. Not lossless-gated (project plan Phase 4: non-lossless IntraBC is now
        // supported, exact-match only) -- allowIntrabc alone is the real, decoder-matching gate (see
        // intrabcStructurallyPresent's own remarks in EncodeLeaf). HashMax8x8IntrabcBlocks (effort >= 4) caps
        // the largest hashed size to 8 -- see Av1SpeedFeatures' own remarks -- so sizes 16 and above are never
        // even computed there, matching libaom's own real construction-time saving, not just a query-time
        // skip; the other branch's own 128 vs. 32 cap mirrors this encoder's own real leaf-size ceiling for
        // each mode (128x128 superblocks only ever exist for lossless -- see Av1SequenceHeaderWriter's
        // use_128x128_superblock remarks -- and non-lossless's own commit-time write path never produces a
        // leaf bigger than 32x32 regardless, see the project plan's transform-size RDO phase), avoiding
        // wasted hash construction for sizes this mode could never actually query.
        if (allowIntrabc)
        {
            int maxBlockSize = speedFeatures.HashMax8x8IntrabcBlocks ? 8 : (lossless ? 128 : 32);
            state.IntrabcHashTable = new Av1IntrabcHashTable(yPlane, yWidth, yHeight, maxBlockSize);
        }

        return state;
    }

    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1b-ii (project plan's own "Two-pass tile-encoder
    /// architecture" section) -- read from <see cref="TileState.Phase"/>, never threaded as a parameter:
    /// every function that needs to know the current phase already receives <c>TileState s</c>, so gating
    /// on <c>s.Phase</c> avoids touching the signature (and every call site) of the ~10 functions and ~40
    /// dual real/decide write-call-site pairs this stage's gating needs to guard.
    ///
    /// <para><see cref="Fused"/> is today's real, only-ever-used value: every search section and every
    /// real/decide write-call-site pair runs unconditionally, exactly matching this project's current
    /// single-pass encode -- a genuine no-op until something sets <see cref="TileState.Phase"/> to anything
    /// else. <see cref="Decide"/> and <see cref="Emit"/> are the two real phases a future <c>DecideTile</c>/
    /// <c>EmitTile</c> split uses: search sections and decide-tracking writes run only when
    /// <c>Phase != Emit</c> (true for both <see cref="Fused"/> and <see cref="Decide"/>); real bitstream
    /// writes run only when <c>Phase != Decide</c> (true for both <see cref="Fused"/> and <see cref="Emit"/>).
    /// This means every existing dual-call site's own gating condition is written the same way regardless of
    /// which of the three values is live, and <see cref="Fused"/> always satisfies both conditions --
    /// verified as a true no-op by construction, not just by testing.</para>
    /// </summary>
    private enum Av1EncodePhase
    {
        Fused,
        Decide,
        Emit,
    }

    private sealed class TileState
    {
        /// <summary>Diagnostic-only hook -- see <see cref="EncodeTile"/>'s own <c>onLeafCommitted</c> parameter remarks. Always <see langword="null"/> in production.</summary>
        public Action<Av1BlockDecisionRecord>? OnLeafCommitted;

        /// <summary>Two-pass tile-encoder architecture, Stage 1b-ii -- see <see cref="Av1EncodePhase"/>'s own
        /// remarks. Defaults to <see cref="Av1EncodePhase.Fused"/> (today's only real value); explicitly
        /// initialized in <see cref="EncodeTile"/> rather than relying on the default, so a future
        /// <c>DecideTile</c>/<c>EmitTile</c> reusing the same <see cref="TileState"/> construction path has one
        /// obvious place to set it.</summary>
        public Av1EncodePhase Phase = Av1EncodePhase.Fused;

        public required int[] SourceY;
        public required int[]? SourceU;
        public required int[]? SourceV;
        public required int[] ReconY;
        public required int[]? ReconU;
        public required int[]? ReconV;
        public required int YWidth;
        public required int YHeight;
        public required int ChromaWidth;
        public required int ChromaHeight;
        public required bool MonoChrome;
        public required int MiCols;
        public required int MiRows;
        public required int BaseQIdx;
        public required bool Lossless;
        public required bool Chroma444;

        /// <summary>
        /// <c>reduced_tx_set</c> -- the value <see cref="Av1FrameHeaderWriter"/> signals in the frame header
        /// (see its own remarks: spec-provably inert for lossless, real content for non-lossless). Real
        /// aomenc's own <c>reduced_tx_type_set</c> config defaults to <see langword="false"/> (confirmed
        /// directly from libaom source, <c>av1/encoder/encodeframe.c</c>'s own unconditional
        /// <c>features-&gt;reduced_tx_set_used = oxcf-&gt;txfm_cfg.reduced_tx_type_set</c> pass-through, no
        /// lossless-specific override) -- this project's own non-lossless tx-type search must therefore use
        /// the FULL <see cref="Av1TxTypeTables.TxTypeIntraInvSet1"/> (7-symbol) set at TX_4X4/TX_8X8, not the
        /// reduced 5-symbol <see cref="Av1TxTypeTables.TxTypeIntraInvSet2"/> a hardcoded-true value would
        /// force, to match real aomenc's own default candidate set -- see every non-lossless tx-type search/
        /// write call site for the actual (reducedTxSet, txSzSqr)-dependent set selection, mirroring
        /// <see cref="Decoding.Av1.Av1TileDecoder.GetTxSet"/>'s own identical logic exactly (TX_16X16 always
        /// gets the reduced set regardless of this flag -- a real spec restriction, not a reduced_tx_set
        /// effect).
        /// </summary>
        public required bool ReducedTxSet;

        /// <summary>
        /// Real, content-based decision (<see cref="Av1ScreenContentEstimator"/>, a faithful port of libaom's
        /// own <c>estimate_screen_content</c>) for whether this frame's palette/IntraBC tooling is
        /// structurally present in the bitstream at all -- replaces the old "always on whenever lossless"
        /// shortcut. Only ever <see langword="true"/> when <see cref="Lossless"/> is (matching real AV1
        /// spec's own <c>allow_screen_content_tools</c> semantics: a lossy frame here never implements
        /// palette/IntraBC regardless of this flag). Every <c>*StructurallyPresent</c> local in
        /// <see cref="EncodeLeaf"/>/<see cref="EncodeRectangularLeaf"/>/<see cref="RdPickPartition"/>
        /// must AND this in alongside <see cref="Lossless"/> -- getting this wrong desyncs the bitstream
        /// (writing/reading a <c>use_intrabc</c>/<c>has_palette_y</c> symbol the other side doesn't expect),
        /// not just a search-quality regression.
        /// </summary>
        public required bool AllowScreenContentTools;

        /// <summary>
        /// <c>allow_intrabc</c> -- a real, decoupled decision from <see cref="AllowScreenContentTools"/> (spec
        /// only requires <c>allow_screen_content_tools</c> for palette; IntraBC needs both that AND its own
        /// bit). Confirmed via this project's own libaom byte-exact comparison harness that these two
        /// genuinely diverge in practice -- a 128x128 checkerboard test frame: real encoders enable palette
        /// (screen content tools) but leave IntraBC off, since <see cref="Av1ScreenContentEstimator"/>'s own
        /// separate, stricter high-variance threshold for IntraBC isn't met even though the coarser
        /// palette-worthiness threshold is.
        /// </summary>
        public required bool AllowIntrabc;

        /// <summary>
        /// libaom ALL_INTRA-mode speed-feature levels for this tile's configured <c>Effort</c>
        /// (<see cref="AvifEncoderOptions.Effort"/>) -- see <see cref="Av1SpeedFeatures"/>'s own remarks.
        ///
        /// <para><b>Not yet consulted by the search</b>: an initial attempt at wiring
        /// <c>TopIntraModelCountAllowed</c> into a cheap WHT-magnitude-SATD prescreen for
        /// <see cref="EncodeLeaf"/>/<see cref="EstimateLumaCost(TileState, int, int, int)"/> measurably regressed lossless output size
        /// on this project's own regression corpus (a plain WHT-coefficient-abs-sum proxy correlates too
        /// poorly with real bit cost on its own -- libaom pairs its own SATD ranking with HOG-based
        /// directional-mode pre-filtering, which this attempt didn't implement) and was reverted rather than
        /// shipped as a regression. This field is validated, unit-tested infrastructure ready for the next
        /// attempt, not yet load-bearing for any encoder decision -- see the project plan
        /// (compare-avif-encoding-to-lucky-clover.md) for the fuller libaom-parity work this is scoped for.</para>
        /// </summary>
        public required Av1SpeedFeatures SpeedFeatures;

        /// <summary>
        /// Spec's real, unpadded <c>MiCols</c>/<c>MiRows</c> (<see cref="TrueMiRows"/>) -- distinct from
        /// <see cref="MiCols"/>/<see cref="MiRows"/>, which describe this encoder's internal
        /// superblock-padded working canvas (unchanged; every buffer/array in this class stays sized to that
        /// padded grid, exactly as before this field existed). <see cref="EncodePartitionForced"/> and
        /// <see cref="RdPickPartition"/> use these two fields -- not <see cref="MiCols"/>/<see cref="MiRows"/>
        /// -- for the spec's out-of-bounds recursion stop (<c>r &gt;= MiRows || c &gt;= MiCols</c>, §5.11.4)
        /// and the <c>hasRows</c>/<c>hasCols</c> partition-legality restriction at a frame edge that falls
        /// mid-superblock, matching <see cref="Decoding.Av1.Av1TileDecoder.DecodePartition"/>'s identical
        /// logic exactly (that method's own <c>_miCols</c>/<c>_miRows</c> are this same "true" concept --
        /// real AV1 decoders never pad, so it never needed two separate fields the way this encoder's own
        /// padded-canvas traversal does). Equal to <see cref="MiCols"/>/<see cref="MiRows"/> whenever the
        /// frame's true size already happens to be a superblock multiple, which non-lossless always is (see
        /// <c>Av1FrameEncoder.Encode</c>'s own remarks) -- making every restriction below a guaranteed no-op
        /// there.
        /// </summary>
        public required int TrueMiCols;
        public required int TrueMiRows;

        /// <summary>
        /// The real, spec-correct <c>maxX</c>/<c>maxY</c> intra-prediction edge-clamp bound (<c>(MiCols * 4) - 1</c>,
        /// matching <see cref="Decoding.Av1.Av1TileDecoder"/>'s own identical <c>maxX - 1</c>/<c>maxY - 1</c>
        /// computation off its real, unpadded <c>_miCols</c>/<c>_miRows</c>) -- every <see cref="Av1IntraPrediction.BuildEdges"/>/
        /// <see cref="Av1IntraPrediction.Predict"/>/<c>Av1InterPrediction.PredictIntrabc</c> call site in this
        /// class must pass this (or <see cref="ChromaEdgeMaxX"/>/<see cref="ChromaEdgeMaxY"/> for a chroma
        /// plane), not <c>YWidth - 1</c>/<c>YHeight - 1</c>/<c>ChromaWidth - 1</c>/<c>ChromaHeight - 1</c> --
        /// those describe the padded working-buffer's own extent, which a real decoder never has and would
        /// clamp edge replication to the wrong (padding, not frame-true) boundary once <see cref="TrueMiCols"/>/
        /// <see cref="TrueMiRows"/> differ from <see cref="MiCols"/>/<see cref="MiRows"/>. Also the bound every
        /// leaf-commit function's own per-4x4-sub-block loop (<c>EncodeLeaf</c>/<c>EncodeRectangularLeaf</c>/
        /// <c>EncodeIntrabcResidual</c>/<c>EncodeChromaRegion</c>/<c>EncodeLosslessLumaResidual</c>/
        /// <c>EncodePaletteResidual</c>, and their cost-estimation counterparts) checks against to implement
        /// spec's own <c>transform_block()</c> per-sub-block edge skip (§5.11.35: "if (row &gt;= MiRows ||
        /// col &gt;= MiCols) return"), mirroring <see cref="Decoding.Av1.Av1TileDecoder.TransformBlock"/>'s
        /// identical bounds check exactly -- this is what lets <see cref="RdPickPartition"/> choose
        /// None/Horz/Vert for a node whose own footprint overhangs the true frame edge, instead of being
        /// forced to keep splitting down to an exact fit.
        /// </summary>
        public required int EdgeMaxX;
        public required int EdgeMaxY;

        /// <summary>Chroma-plane counterpart to <see cref="EdgeMaxX"/>/<see cref="EdgeMaxY"/>, already subsampling-adjusted (right-shifted by the real <c>subsampling_x</c>/<c>subsampling_y</c>, zero for this encoder's always-4:4:4 lossless chroma -- see <see cref="Av1TileEncoder.EncodeTile"/>'s own <c>chromaSubX</c>/<c>chromaSubY</c> remarks).</summary>
        public required int ChromaEdgeMaxX;
        public required int ChromaEdgeMaxY;

        /// <summary>
        /// Mi-unit mask for converting an absolute mi row/col into its position relative to the current
        /// top-level superblock (<c>r &amp; SbMiMask</c>) -- 31 (128x128, 32 mi units) when this frame uses
        /// 128x128 superblocks (always exactly <see cref="Lossless"/>, see <c>Av1TileEncoder.EncodeTile</c>'s
        /// top-level loop), 15 (64x64, 16 mi units) otherwise. <see cref="BlockDecodedStride"/> is already
        /// sized generously enough for either case.
        /// </summary>
        public required int SbMiMask;
        public required Av1CdfContext Cdf;

        /// <summary>
        /// A snapshot of <see cref="Cdf"/>'s own probabilities, refreshed exactly once per top-level
        /// superblock (see <c>Av1TileEncoder.EncodeTile</c>'s own top-level loop, immediately before each
        /// superblock's own <see cref="EncodePartitionForced"/> call) -- a real, literal port of libaom's own
        /// <c>av1_set_cost_upd_freq</c> (<c>av1/encoder/encodeframe_utils.c:1621</c>, called once per SB from
        /// the main encode loop, <c>encodeframe.c:1289</c>) and its default <c>INTERNAL_COST_UPD_SB</c> level
        /// for both <c>coeff_cost_upd_level</c>/<c>mode_cost_upd_level</c> (<c>speed_features.c:2385-2387</c>,
        /// never overridden away from this default for AV1's own ALL_INTRA usage at any cpu-used level this
        /// project targets) -- confirmed via direct source reading, not assumed.
        ///
        /// <para>Every cost-*estimation* call in this file (<see cref="Av1SymbolEncoder.EstimateSymbolCost"/>
        /// against a CDF, or <see cref="Av1CoefficientWriter.WriteCoeffs"/> called with a trial sink) reads
        /// from this snapshot, never <see cref="Cdf"/> directly -- matching libaom's own real architecture,
        /// where every RD candidate considered within one superblock (partition-type comparison AND each
        /// leaf's own real mode search alike -- libaom has no decision/commit split at all, see this
        /// project's own plan doc's "Research: confirmed libaom has no decision/commit split" section) is
        /// costed against the exact same frozen table, regardless of what's already been committed earlier
        /// within that same superblock. Every real bitstream *write*
        /// (<c>s.Symbols.WriteSymbol</c>/<c>WriteCoeffs(s.Symbols, s.Cdf, ...)</c>) still correctly reads and
        /// adapts <see cref="Cdf"/> itself, exactly as before -- only the RD-search cost model's own probability
        /// source changed, never the real, spec-mandated per-symbol bitstream adaptation.</para>
        ///
        /// <para>Before this snapshot existed, every cost estimate read <see cref="Cdf"/> directly -- for a
        /// leaf's own real mode search (<c>EncodeLeaf</c>/<c>EncodeRectangularLeaf</c>'s own <c>bestMode</c>
        /// comparison, <c>SearchUvMode</c>/<c>SearchRectUvMode</c>, palette RD, IntraBC MV cost, etc.), this
        /// meant the *second* leaf committed within a superblock scored its own candidates against a
        /// probability table already shifted by the *first* leaf's own just-written symbols -- a real,
        /// leaf-order-dependent bias no real libaom encoder has, since every leaf within one of its own
        /// superblocks shares the identical frozen table regardless of commit order. The partition-*type*
        /// decision itself (<see cref="RdPickPartition"/>'s own recursive cost comparison) was already
        /// effectively frozen for the whole superblock even before this change, since nothing is ever
        /// committed (nothing mutates <see cref="Cdf"/>) until <em>after</em> the entire superblock's own
        /// decision tree is memoized -- this snapshot makes that already-correct behavior explicit and
        /// uniform with every other cost estimate, rather than relying on that architectural coincidence.</para>
        /// </summary>
        public required Av1CdfContext CostCdf;

        /// <summary>
        /// Two-pass tile-encoder architecture, Stage 1b-i (project plan's own "Two-pass tile-encoder
        /// architecture" section): the real, load-bearing prerequisite a design-review pass identified
        /// before <see cref="CostCdf"/>'s per-superblock refresh can safely be sourced from anything other
        /// than <see cref="Cdf"/> itself. Today (this stage), <see cref="Cdf"/> and every real commit still
        /// happen together, per superblock, exactly as before -- so this field is deliberately kept an
        /// always-in-lockstep copy of <see cref="Cdf"/>, refreshed at the same point and frequency
        /// <see cref="CostCdf"/> already was (see the top-level superblock loop in <see cref="EncodeTile"/>),
        /// making this stage a genuine no-op relative to <see cref="Cdf"/> -- its only purpose right now is
        /// to introduce the real object and rewire <see cref="CostCdf"/>'s own refresh source onto it
        /// (<c>CostCdf.CopyFrom(DecideCdf)</c>, not <c>CopyFrom(Cdf)</c>), verified byte-identical to
        /// before this stage. A future Stage 1b-ii, once <c>DecideTile</c>/<c>EmitTile</c> genuinely split
        /// decide from real bitstream emission, changes what advances this field (a real, decide-time-only
        /// adaptation of each leaf's own decided symbol values, independent of <see cref="Cdf"/>, which by
        /// then no longer advances until the later Emit phase) without needing to touch
        /// <see cref="CostCdf"/>'s own refresh call site again.
        /// </summary>
        public required Av1CdfContext DecideCdf;

        /// <summary>
        /// Two-pass tile-encoder architecture, Stage 1b (Round N+57 correction/prerequisite): a real,
        /// independent deep copy of <see cref="CostCdf"/>, captured once per top-level superblock
        /// (immediately after that superblock's own <c>CostCdf.CopyFrom(DecideCdf)</c> refresh in
        /// <see cref="EncodeTile"/>'s top-level loop), in superblock visitation order.
        ///
        /// <para>Found necessary by direct source reading of <see cref="OptimizeCoeffTrellis"/>: contrary to
        /// this project's own plan doc's original claim that coefficient quantization has "no CDF-dependent
        /// RDO/trellis step," trellis's own per-candidate cost comparison (<c>GetCost</c>'s
        /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> call) reads <see cref="CostCdf"/> directly, and its
        /// result (a mutation of the real <c>levels[]</c> array that becomes the final committed bitstream
        /// content) is therefore a genuine function of whichever <see cref="CostCdf"/> snapshot was live at
        /// the time. Once a future Stage 1b-ii's real <c>DecideTile</c> completes for the WHOLE frame before
        /// <c>EmitTile</c> ever runs, <see cref="DecideCdf"/> (and therefore a freshly-rederived
        /// <see cref="CostCdf"/>) will hold its final, whole-frame-adapted state throughout all of
        /// <c>EmitTile</c> -- not the intermediate, per-superblock-in-time state Decide actually used for a
        /// given superblock. Naively re-deriving <see cref="CostCdf"/> from <see cref="DecideCdf"/> during
        /// Emit and re-running trellis against it could therefore silently pick a *different* final
        /// coefficient level than Decide did for the exact same leaf -- the same class of byte-exactness bug
        /// this file's own Stage-1b remarks already identified for the tx-depth comparison, just not
        /// previously checked against this specific function. This snapshot list is the fix: <c>EmitTile</c>
        /// re-derives <see cref="CostCdf"/> for a given superblock by loading
        /// <c>CostCdfSnapshots[supeblockIndex]</c> instead of copying from <see cref="DecideCdf"/>, making a
        /// recomputed trellis call during Emit read the exact same probabilities Decide's own trellis call
        /// did, and therefore reach the exact same answer.</para>
        ///
        /// <para>Purely additive today (Stage 1b prerequisite, not yet Stage 1b-ii's real cutover): populated
        /// by the existing fused per-superblock loop, not yet read anywhere -- a true no-op relative to
        /// today's still-fused single-pass encode, verified the same way as every other Stage 1a/1b
        /// prerequisite before it.</para>
        /// </summary>
        public required List<Av1CdfContext> CostCdfSnapshots;

        public required Av1SymbolEncoder Symbols;
        public required int[] YModes;
        public required int[] UvModes;
        public required int[] MiSizes;
        public required int[] LumaTxSizes;
        public required bool[] Skips;

        // Two-pass tile-encoder architecture, Stage 1a (project plan's own "Two-pass tile-encoder
        // architecture" section, for-the-attached-image-nifty-kahan.md): frame-wide persistence for the
        // per-leaf decisions that today are only ever local/scratch, computed and immediately consumed by
        // the fused commit-and-write call, then discarded. Populated at every real leaf commit (mirroring
        // exactly where YModes/UvModes/MiSizes above are populated -- same mi-addressing granularity,
        // same "real, final, already-override-applied value" contract), but NOT YET READ anywhere -- this
        // is Stage 1a's own deliberately additive, no-behavior-change scope; a future Stage 1b's real
        // Emit phase is what will read these. AngleDeltaYGrid/AngleDeltaUvGrid/FilterIntraGrid/
        // FilterIntraModeGrid store the real angle_delta_y/angle_delta_uv/use_filter_intra/filter_intra_mode
        // values actually written (0/false whenever IntraBC or palette forced them off, matching every real
        // write-site's own gate exactly -- see EncodeLeaf/EncodeRectangularLeaf's own commit-loop remarks).
        // TxTypeGrid stores the real per-transform-block tx_type (Av1TxType) -- only ever meaningful for a
        // non-lossless plain-residual leaf (lossless always uses WHT, palette/IntraBC all-zero-residual
        // leaves have no searched transform at all), so it is only ever populated by EncodeLeaf's own
        // non-lossless branch; every other leaf kind leaves it at its harmless DctDct (0) default. Unlike
        // LumaTxSizes (uniform across a leaf's whole mi footprint even when tx_depth == 1, since tx SIZE is
        // genuinely uniform across all 4 sub-blocks there), tx_type can differ PER SUB-BLOCK when
        // tx_depth == 1 (confirmed via direct source reading: EncodeLeaf's own tx_depth==1 branch writes 4
        // independent subTxTypes[k] symbols) -- so this grid is populated at true transform-block
        // granularity, not leaf granularity, to avoid silently collapsing 4 distinct real values into one.
        public required int[] AngleDeltaYGrid;
        public required int[] AngleDeltaUvGrid;
        public required bool[] FilterIntraGrid;
        public required int[] FilterIntraModeGrid;
        public required int[] TxTypeGrid;

        // Two-pass tile-encoder architecture, Stage 1a addendum: a genuine gap found only once a
        // dedicated variable-crossing audit of EncodeLeaf's own search-to-commit boundary was done (see
        // the project plan's own "Stage 1b-ii execution guide" for the full audit) -- CFL alpha_u/alpha_v
        // (only meaningful when the committed uv_mode is UV_CFL_PRED) were the one decided value with no
        // persisted counterpart anywhere, unlike every other per-leaf decision. Same mi-addressing
        // granularity as AngleDeltaYGrid/etc; harmless (never read) whenever the leaf's own UvModes[idx]
        // isn't UvCflPred.
        public required int[] CflAlphaUGrid;
        public required int[] CflAlphaVGrid;

        // Two-pass tile-encoder architecture, Stage 1a (see the grids just above for the full rationale):
        // frame-wide, per-pixel palette color-index maps -- only the up-to-8 palette COLORS persist
        // anywhere today (PaletteColorsYGrid/UGrid below); the color-map itself (which color index each
        // pixel actually uses) is single per-leaf scratch (PaletteColorMap/PaletteColorMapUv further below),
        // overwritten by every subsequent leaf. Stored directly (not re-derived later via a second nearest-
        // color/k-means assignment pass) since it's cheap -- one int per pixel, unlike coefficient levels --
        // and re-deriving risks tie-breaking non-determinism across two separate runs. Sized to the real,
        // padded working-canvas pixel extent (YWidth*YHeight / ChromaWidth*ChromaHeight), addressed by
        // global (not leaf-local) pixel row/col, exactly like ReconY/ReconU/ReconV -- populated at every
        // WriteColorMapTokens call site, copying that call's own leaf-local, onscreen-clipped region into
        // the matching global offset (see CopyColorMapToFrameGrid). Not yet read anywhere (Stage 1a only).
        public required int[] PaletteColorMapYGrid;
        public required int[] PaletteColorMapUvGrid;

        // IntraBC neighbor context (spec's IsInters/Mvs), write-side mirror of Av1TileDecoder's identically
        // named fields -- see FindMvStack's remarks for why IsInters is only ever true for an IntraBC leaf
        // in this encoder (there is no other way for is_inter to be true). Written mirrors "has this mi
        // position been written for this frame yet" (spec §7.10.2.4's scan point process) -- see ScanPoint's
        // remarks for why this encoder's own quad-split recursion order still needs it (a block deep in a
        // bottom-left quadrant's top-right scan point can land in the not-yet-encoded bottom-right quadrant,
        // despite both quadrants sharing the same row range).
        public required bool[] IsInters;
        public required int[] MvRowsGrid;
        public required int[] MvColsGrid;
        public required bool[] Written;

        // Real, whole-frame IntraBC exact-match index -- a literal port of libaom's own hash-table
        // construction (av1/encoder/hash_motion.c, see Av1IntrabcHashTable's own remarks), built once here
        // (see EncodeTile) before the main superblock loop starts, from the tile's own already-fully-known
        // source pixels. Null whenever IntraBC can never be used at all (non-lossless, or lossless with
        // AllowIntrabc false) -- FindIntrabcMatch always checks for null rather than building a table nobody
        // will ever query.
        public Av1IntrabcHashTable? IntrabcHashTable;

        // Phase D technique 5: every fully-encoded lossless leaf's position, by size, regardless of content
        // -- unlike IntrabcHashTable (exact-content lookup), this backs the *approximate*-match search
        // (FindApproximateIntrabcMatch), which needs real candidates to score by residual cost, not just
        // ones that already match exactly.
        public required Dictionary<int, List<(int R, int C)>> PositionsBySize;

        // Coarse content-signature bucket index (see ComputeCoarseSignature/FindApproximateIntrabcMatch's own
        // remarks): a visually-similar-but-not-byte-identical leaf (e.g. the same decorative element
        // re-rendered with different anti-aliasing) can be spaced anywhere in the frame, not just within a
        // small raster-order recency window -- this buckets every fully-encoded leaf by size + a coarse,
        // quantized-luma-average signature so the approximate-match search can reach those candidates
        // directly, at O(bucket size) rather than O(every leaf in the frame).
        public required Dictionary<(int SizePixels, int Signature), List<(int R, int C)>> IntrabcSignatureIndex;

        // Write-side mirror of Av1TileDecoder's BlockDecoded tracking (spec's BlockDecoded[][], §5.11.3) --
        // needed so haveAboveRight/haveBelowLeft (spec §7.11.2's edge-extension availability, which directional
        // prediction with a nonzero angle_delta can read past the block's own top-right/bottom-left corner)
        // match what a real decoder will independently compute at the same position, not a conservative
        // always-false guess. See EncodeLeaf/EncodeLosslessLumaResidual's remarks for why getting this wrong
        // is a real correctness bug (not just a missed optimization) once angle_delta is actually searched.
        public required bool[][] BlockDecoded;

        // Phase D "RD-optimal partition search": memoized (keep-as-leaf, cost) decision per (r, c, sizeMi)
        // node, computed by DecidePartition and consumed by EncodePartitionForced -- see DecidePartition's
        // remarks. Keyed by the full (r, c, sizeMi) triple, not just (r, c): the same top-left position is
        // visited at every size level on the way down (a node's leaf-vs-split choice at 32x32 and its
        // leaf-vs-split choice at 16x16 share a top-left corner but are different decisions).
        public required Dictionary<(int R, int C, int SizeMi), (int Type, long Cost)> PartitionDecisions;
        public required Av1CoefficientWriter.PlaneContext YCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? UCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? VCoeffCtx;

        // Two-pass tile-encoder architecture, Stage 1b (second load-bearing prerequisite, found while
        // auditing every piece of "must genuinely advance during Decide" state before attempting the real
        // DecideTile/EmitTile split -- see TileState.DecideCdf's own remarks for the first one): ScratchCoeffCtx
        // (below) is reseeded from the REAL YCoeffCtx/UCoeffCtx/VCoeffCtx before every single RD candidate's
        // own trial cost -- not once per superblock the way CostCdf is, but genuinely per candidate, since a
        // leaf's own coefficient signaling cost depends on its immediate above/left neighbor's real coefficient
        // pattern. Once a future Stage 1b-ii's real DecideTile stops calling the real, bit-emitting
        // Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, ..., s.YCoeffCtx, ...) (that becomes EmitTile's own
        // job), YCoeffCtx/UCoeffCtx/VCoeffCtx would silently freeze at their all-zero construction-time state for
        // the whole Decide pass -- degrading every leaf's own coefficient-context-dependent cost estimate after
        // the first, exactly the same failure class DecideCdf already fixes for Cdf/CostCdf. DecideYCoeffCtx/
        // DecideUCoeffCtx/DecideVCoeffCtx are the real, genuinely-advancing decide-time counterparts: every real
        // WriteCoeffs call site also calls Av1CoefficientWriter.WriteCoeffs a second time, same arguments,
        // targeting these grids via DecideCommitSink (see its own remarks) and DecideCdf instead of s.Symbols/
        // s.Cdf -- updateContext: true, so the real context-derivation logic already proven correct for real
        // commits also drives these. Not yet read by ScratchCoeffCtx's own reseed call sites (that reseed still
        // targets the real YCoeffCtx/etc, correct today since decide and commit are still fused) -- a future
        // Stage 1b-ii both switches ScratchCoeffCtx's reseed source to these fields AND makes the "real" calls
        // above conditional on an Emit phase instead of unconditional duplicates.
        public required Av1CoefficientWriter.PlaneContext DecideYCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? DecideUCoeffCtx;
        public required Av1CoefficientWriter.PlaneContext? DecideVCoeffCtx;

        /// <summary>
        /// Two-pass tile-encoder architecture, Stage 1b: the sink every parallel "also advance DecideCdf/
        /// DecideYCoeffCtx for real" <see cref="Av1CoefficientWriter.WriteCoeffs"/> call (see
        /// <see cref="DecideYCoeffCtx"/>'s own remarks) writes through -- an <see cref="Av1AdaptingTrialSymbolSink"/>
        /// so it genuinely adapts whichever <see cref="DecideCdf"/> coefficient CDF table it's handed (exactly
        /// like a real commit's own <see cref="Av1SymbolEncoder.WriteSymbol"/> would adapt <see cref="Cdf"/>),
        /// without ever emitting real bits or touching <see cref="Cdf"/> itself. Deliberately a separate
        /// instance from <see cref="AdaptingTrialSink"/> above (that one's own reset/reseed lifecycle is
        /// per-RD-candidate, a different purpose) -- this one is invoked once per real leaf commit, mirroring
        /// <see cref="DecideCdf"/>'s own per-leaf adaptation cadence, and never needs an explicit
        /// <c>Reset()</c> since nothing ever reads its accumulated cost total.
        /// </summary>
        public required Av1AdaptingTrialSymbolSink DecideCommitSink;

        // RD cost search scratch (Av1RdCost / ComputeCandidateCost): ScratchCoeffCtx is reseeded from the
        // real Y/U/V context before every candidate's trial cost, then freely mutated (never written back)
        // by that one candidate's own WriteCoeffs trial calls -- see PlaneContext.SeedFrom's remarks. TrialSink
        // is the one reused Av1TrialSymbolSink every trial WriteCoeffs call accumulates into (Reset() between
        // candidates), avoiding a fresh allocation on every one of a leaf's ~100+ candidate evaluations.
        // Lambda is this frame's qindex-derived RD weight (Av1RdCost.QIndexToLambda), computed once here
        // rather than per candidate.
        public required Av1CoefficientWriter.PlaneContext ScratchCoeffCtx;
        public required Av1TrialSymbolSink TrialSink;

        // Scratch, per-candidate-adapting coefficient-CDF state (see ComputeLosslessWholeLeafCostPerSubBlock's
        // own remarks): unlike TrialSink/Cdf above (a static, never-adapted-within-one-candidate snapshot --
        // the right choice for every OTHER cost estimate in this class, matching real AV1 encoders' own
        // architecture, confirmed by directly reading libaom's own RD-cost source), a candidate spanning many
        // (potentially 1024) 4x4 sub-blocks needs its own trial to reflect how quickly a real commit's CDF
        // would adapt across those same sub-blocks -- a literally-flat plane's true cost is dominated almost
        // entirely by that adaptation, which no amount of per-symbol cost precision alone can model. ScratchCdf
        // is reseeded (Av1CdfContext.CopyFrom) from the real, current Cdf once per candidate (never per
        // sub-block, and never written back) so this candidate's own trial starts from the tile's real,
        // already-adapted-by-everything-committed-so-far state, then freely adapts further within just this
        // one candidate's own sub-block loop via AdaptingTrialSink. Only ever used by
        // ComputeLosslessWholeLeafCostPerSubBlock -- every other cost estimate in this class keeps using
        // TrialSink/Cdf directly, since that rare, once-per-128x128-superblock call site is the only one this
        // adaptation gap was ever measured to matter for (see the project plan's own progress log).
        public required Av1CdfContext ScratchCdf;
        public required Av1AdaptingTrialSymbolSink AdaptingTrialSink;
        public required double Lambda;

        // Palette mode state (spec §5.11.46/§5.11.47) -- PaletteSizesY/UV and the two color grids are
        // frame-shared neighbor context (mirroring Av1TileDecoder's own identically-named fields exactly,
        // including why only Y and U colors are ever cached -- see Av1TileDecoder.GetPaletteCache's
        // remarks); PaletteColorsY/U/V and PaletteColorMap are per-leaf scratch, reused across every block
        // the same way Pred/BestPred/etc. are.
        public required int[] PaletteSizesY;
        public required int[] PaletteSizesUV;
        public required int[] PaletteColorsYGrid;
        public required int[] PaletteColorsUGrid;
        public required int[] PaletteColorsY;
        public required int[] PaletteColorsU;
        public required int[] PaletteColorsV;
        public required int[] PaletteColorMap;

        // Second color-map scratch buffer, used only by EncodeLeaf's approximate-palette residual
        // commit path -- see the remark at this state's construction site for why PaletteColorMap
        // alone isn't enough once both planes' maps must survive to be written before either residual.
        public required int[] PaletteColorMapUv;

        // Real multi-strategy palette search scratch (Av1PaletteSearch/SearchLosslessYPalette/
        // SearchLosslessUvPalette): PaletteTrialColorMap holds whichever candidate is currently under
        // trial during the sweep -- separate from PaletteColorMap/PaletteColorMapUv above, which only ever
        // hold the *winning* candidate found so far, copied over on every improvement. Reused sequentially
        // for both the luma and chroma searches (they never run concurrently, so one shared trial buffer is
        // enough). PaletteTrialColorsY/U/V are the equivalent per-candidate scratch for centroid values
        // (PaletteColorsY/U/V, like PaletteColorMap, hold only the winner). PaletteKMeansDataY/U/V are the
        // flattened raw-pixel arrays libaom's own av1_k_means operates on directly (fill_data_and_get_bounds,
        // palette.c) -- filled once per search, reused across every candidate K. PaletteTopColors/
        // PaletteCountBuf back Av1PaletteSearch.FindTopColors/CountColors.
        public required int[] PaletteTrialColorMap;
        public required int[] PaletteTrialColorsY;
        public required int[] PaletteTrialColorsU;
        public required int[] PaletteTrialColorsV;
        public required int[] PaletteKMeansDataY;
        public required int[] PaletteKMeansDataU;
        public required int[] PaletteKMeansDataV;
        public required int[] PaletteTopColors;
        public required int[] PaletteCountBuf;

        // Rented once per EncodeTile call and reused across every block -- see EncodeTile's remarks.
        public required int[] Pred;
        public required int[] BestPred;
        public required int[] Residual;
        public required int[] Coeff;
        public required int[] Levels;
        public required int[] ReconDequant;
        public required int[] ReconResidual;
        public required int[] CflLumaAc;
        public required int[] LumaLevels;

        /// <summary>
        /// Shared 4x4-sub-block edge-array scratch, reused across every per-4x4-sub-block prediction call in
        /// this file (<see cref="ComputeLosslessWholeLeafCostPerSubBlockPrecise"/> and its siblings --
        /// <see cref="EncodeLosslessLumaResidual"/>, <see cref="EncodeChromaRegion"/>, the palette residual
        /// writers, <c>SearchRectUvMode</c>'s own oversized branch, etc.) instead of each call allocating its
        /// own pair of <see cref="Av1EdgeArray"/> objects. A single 4x4 sub-block's own edge context is always
        /// fully overwritten by <see cref="Av1IntraPrediction.BuildEdges"/> before <see cref="Av1IntraPrediction.Predict"/>
        /// reads it and consumed synchronously (into <see cref="Pred"/>) before the next sub-block's own
        /// BuildEdges call -- the same "safe to share because never read across an intervening write" property
        /// <see cref="Pred"/>/<see cref="Residual"/>/<see cref="Coeff"/>/<see cref="Levels"/> above already
        /// rely on, just extended to this one additional pair of scratch objects. Sized 16 (4x4 plus
        /// <see cref="Av1EdgeArray"/>'s own fixed corner offset) -- never reused for a whole-leaf-sized edge
        /// build (those already allocate their own, larger, per-call-site array once per search, outside any
        /// sub-block loop, and stay untouched by this field).
        /// </summary>
        public required Av1EdgeArray SubBlockAbove;
        public required Av1EdgeArray SubBlockLeft;
    }

    private static int BlockSizeFromSizeMi(int sizeMi) => sizeMi switch
    {
        32 => Av1BlockSize.Block128x128,
        16 => Av1BlockSize.Block64x64,
        8 => Av1BlockSize.Block32x32,
        4 => Av1BlockSize.Block16x16,
        2 => Av1BlockSize.Block8x8,
        _ => Av1BlockSize.Block4x4,
    };

    /// <summary>
    /// Rectangular counterpart to <see cref="BlockSizeFromSizeMi"/>, for a HORZ/VERT-split leaf (<paramref
    /// name="wMi"/> != <paramref name="hMi"/>) -- falls back to the square mapping when they're equal.
    /// Covers every shape <see cref="RdPickPartition"/>'s Horz/Vert candidates can produce: 8x4/4x8 up
    /// to 64x32/32x64 from its own fully-interior <c>sizeMi &lt;= 16</c> branch, plus 64x128/128x64
    /// (<paramref name="wMi"/>/<paramref name="hMi"/> 16x32/32x16) from its boundary
    /// (<c>split_or_horz</c>/<c>split_or_vert</c>) branch's own <c>(half, sizeMi)</c>/<c>(sizeMi, half)</c>
    /// candidate at the top, <c>sizeMi == 32</c> level -- reachable only at a non-superblock-multiple frame's
    /// own true edge (<see cref="TileState.TrueMiRows"/>/<see cref="TileState.TrueMiCols"/>), but a real,
    /// live case, not a hypothetical one: found missing via a real round-trip failure once the real luma mode
    /// search (see <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>) made this boundary candidate's own estimated cost
    /// cheap enough to actually win for the first time. Before that fix, the wrong fallback below
    /// (<see cref="BlockSizeFromSizeMi"/>(wMi), collapsing this shape to <c>Block64x64</c>) was silently
    /// harmless: DC_PRED's own prediction math doesn't consult <c>bSize</c> at all, and <c>bSize</c>'s other
    /// consumers here (<c>paletteStructurallyPresent</c>'s own size gate in particular) happened to still
    /// agree with the correct answer for a leaf this large by coincidence. It stops being harmless the moment
    /// any bSize-keyed decision can differ between the wrong, too-small fallback and the real shape -- exactly
    /// what happened: <c>paletteStructurallyPresent</c> incorrectly stayed true for a real 64x128 leaf
    /// (<c>Block64x64</c>'s own height, 64, satisfies its own <c>&lt;= 64</c> check; the real leaf's true
    /// height, 128, does not), writing a real decoder never expected to read and silently desyncing the
    /// entropy stream from that point on.
    /// </summary>
    private static int BlockSizeFromWidthHeightMi(int wMi, int hMi) => (wMi, hMi) switch
    {
        (2, 1) => Av1BlockSize.Block8x4,
        (1, 2) => Av1BlockSize.Block4x8,
        (4, 2) => Av1BlockSize.Block16x8,
        (2, 4) => Av1BlockSize.Block8x16,
        (8, 4) => Av1BlockSize.Block32x16,
        (4, 8) => Av1BlockSize.Block16x32,
        (16, 8) => Av1BlockSize.Block64x32,
        (8, 16) => Av1BlockSize.Block32x64,
        (16, 32) => Av1BlockSize.Block64x128,
        (32, 16) => Av1BlockSize.Block128x64,

        // PARTITION_HORZ_4/VERT_4's own 4:1-aspect leaf shapes (see ComputeDecidePartition's own remarks) --
        // previously unreachable (no caller ever asked this method for one of these ratios before Horz4/Vert4
        // existed), so falling through to BlockSizeFromSizeMi(wMi) below would have silently returned a
        // *square* block size instead (e.g. wMi=4,hMi=1 -> Block16x16, not Block16x4) -- a real bug, not just
        // a missing optimization, since every size-dependent gate downstream (RectAngleDeltaAllowed, palette's
        // own AllowPalette-equivalent check, filter_intra's max(bw,bh)<=32 gate) needs the real rectangular
        // Av1BlockSize to evaluate correctly for these leaves.
        (4, 1) => Av1BlockSize.Block16x4,
        (1, 4) => Av1BlockSize.Block4x16,
        (8, 2) => Av1BlockSize.Block32x8,
        (2, 8) => Av1BlockSize.Block8x32,
        (16, 4) => Av1BlockSize.Block64x16,
        (4, 16) => Av1BlockSize.Block16x64,

        _ => BlockSizeFromSizeMi(wMi),
    };

    /// <summary><c>log2</c> of a leaf's pixel width/height, for <see cref="Av1IntraPrediction.Predict"/>'s <c>log2W</c>/<c>log2H</c> parameters -- e.g. sizeMi 16 (64 pixels) -&gt; 6.</summary>
    private static int PixelLog2(int sizeMi) => sizeMi switch
    {
        32 => 7,
        16 => 6,
        8 => 5,
        4 => 4,
        2 => 3,
        _ => 2,
    };

    private static void EncodePartitionForced(TileState s, int r, int c, int sizeMi)
    {
        // decode_partition()'s own top-of-function out-of-bounds stop (spec §5.11.4: "if (r >= MiRows ||
        // c >= MiCols) return"), against the frame's real, unpadded mi bounds (TileState.TrueMiCols's own
        // remarks) -- not MiCols/MiRows, this encoder's internal superblock-padded working-canvas bounds.
        // Only reachable when lossless: non-lossless's TrueMiCols/TrueMiRows always equal MiCols/MiRows (see
        // TrueMiCols's remarks), so every padded superblock the top-level loop in EncodeTile visits is
        // always in-bounds there.
        if (r >= s.TrueMiRows || c >= s.TrueMiCols)
        {
            return;
        }

        // decode_partition() never reads a partition symbol below 8x8 (spec §5.11.4: the read is gated on
        // bSize >= BLOCK_8X8) -- a 4x4 node is always a leaf, unconditionally, with no signaling of its own,
        // and (per that same gate) with no hasRows/hasCols restriction to apply either -- the out-of-bounds
        // check above already guarantees this node's own (r, c) is a real, in-frame position. Only reachable
        // at all when lossless (see the sizeMi == 2 case below): non-lossless never recurses this far since
        // it never calls DecidePartition.
        if (sizeMi == 1)
        {
            EncodeLeaf(s, r, c, sizeMi);
            return;
        }

        int bSize = BlockSizeFromSizeMi(sizeMi);

        // decode_partition() reads a partition symbol at every size down to and including 8x8 (only sizes
        // *below* 8x8 skip it, see the sizeMi == 1 case above).
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.Cdf.PartitionW8[ctx],
            2 => s.Cdf.PartitionW16[ctx],
            3 => s.Cdf.PartitionW32[ctx],
            4 => s.Cdf.PartitionW64[ctx],
            _ => s.Cdf.PartitionW128[ctx],
        };

        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks
        // (same rationale, applied to the partition-type symbol). Only the ordinary hasRows&&hasCols
        // branch below ever adapts a real, persistent CDF (BuildSplitOrHorzCdf/BuildSplitOrVertCdf's own
        // remarks: the two restricted-branch CDFs are freshly-built local arrays, never the real
        // persistent one, so those two branches need no decide-tracking counterpart).
        var decidePartitionCdf = bsl switch
        {
            1 => s.DecideCdf.PartitionW8[ctx],
            2 => s.DecideCdf.PartitionW16[ctx],
            3 => s.DecideCdf.PartitionW32[ctx],
            4 => s.DecideCdf.PartitionW64[ctx],
            _ => s.DecideCdf.PartitionW128[ctx],
        };

        int half = sizeMi / 2;

        // hasRows/hasCols (spec §5.11.4): whether this node's second half in each dimension actually falls
        // inside the frame's true bounds. Always both true for non-lossless (see TrueMiCols's remarks), so
        // the restricted branches below are only ever live for lossless.
        bool hasRows = r + half < s.TrueMiRows;
        bool hasCols = c + half < s.TrueMiCols;

        // Real RD partition search (DecidePartition, Phase D technique 6 originally, now live for
        // non-lossless too -- see the project plan's partition/TX-size RDO phase) picks split-vs-leaf at
        // every level down to each mode's own floor: lossless reaches spec's true 4x4 floor (sizeMi == 1,
        // see above) since DecidePartition's own remarks explain why going smaller matters for hard-edged/
        // screen-content-style graphics; non-lossless floors at sizeMi == 2 (8x8) instead -- DecidePartition
        // itself never recurses non-lossless below that (see its own sizeMi == 2 remarks), so this call
        // always terminates correctly without a separate forced-8x8 special case here anymore. This includes
        // leaves that end up using IntraBC's approximate-match residual path (EncodeIntrabcResidual, lossless
        // only), which predicts every plane fresh per 4x4 sub-block from progressively-reconstructed state,
        // matching Av1TileDecoder.TransformBlock's own per-sub-block PredictIntrabc call (spec §5.11.35) for
        // any leaf size -- see EncodeIntrabcResidual's remarks. That path used to be gated to single-sub-block
        // (sizeMi <= 2) leaves specifically because it predicted a merged coding block in one whole-block
        // PredictIntrabc call instead, which could desync from a real decoder's per-sub-block prediction for
        // a genuinely multi-sub-block IntraBC block; IntraBC's *exact*-match path (skip = 1, no residual,
        // verified byte-identical to source before use) never had this problem, since it carries no
        // per-sub-block prediction step at all. RdPickPartition applies this same hasRows/hasCols
        // restriction to its own candidate set, so decidedType is always legal for whichever signaling branch
        // below actually fires. Decisions for this whole superblock's own subtree were already computed and
        // written by DecideSuperblockPartition before this encode-phase walk began (see its own call site in
        // the superblock loop above) -- a direct dictionary read here, never a compute-on-miss.
        int decidedType = s.PartitionDecisions[(r, c, sizeMi)].Type;

        // Signaling (spec §5.11.4's own partition/split_or_horz/split_or_vert selection): a fully-interior
        // node (hasRows && hasCols, i.e. every non-lossless node, and most lossless ones) reads/writes the
        // ordinary 4-way symbol exactly as before this restriction existed. A boundary node restricts the
        // legal choice to a real-decoder-matching 2-outcome derived symbol (or, at a corner, no symbol at
        // all) -- see BuildSplitOrHorzCdf/BuildSplitOrVertCdf's own remarks for why this never adapts the
        // real, persistent partitionCdf the way the ordinary branch's WriteSymbol call does.
        if (hasRows && hasCols)
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(partitionCdf, decidedType);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(decidePartitionCdf, decidedType);
            }
        }
        else if (hasCols)
        {
            // Two-pass tile-encoder architecture, Stage 1b-ii -- see Av1EncodePhase's own remarks. A fresh,
            // non-persistent local CDF (BuildSplitOrHorzCdf's own remarks) -- real-only, no decide-tracking
            // counterpart needed (nothing persistent to adapt), but still a real bitstream write that must
            // not fire during a genuine Decide-only pass.
            if (s.Phase != Av1EncodePhase.Decide)
            {
                var splitOrHorzCdf = BuildSplitOrHorzCdf(partitionCdf, bSize);
                s.Symbols.WriteSymbol(splitOrHorzCdf, decidedType == Av1PartitionType.Split ? 1 : 0);
            }
        }
        else if (hasRows)
        {
            // Two-pass tile-encoder architecture, Stage 1b-ii -- see Av1EncodePhase's own remarks. Same
            // rationale as the split_or_horz branch above.
            if (s.Phase != Av1EncodePhase.Decide)
            {
                var splitOrVertCdf = BuildSplitOrVertCdf(partitionCdf, bSize);
                s.Symbols.WriteSymbol(splitOrVertCdf, decidedType == Av1PartitionType.Split ? 1 : 0);
            }
        }

        // else: neither hasRows nor hasCols -- partition is forced to Split with no symbol read/written at
        // all (spec's decode_partition final else-branch).
        switch (decidedType)
        {
            case Av1PartitionType.None:
                EncodeLeaf(s, r, c, sizeMi);
                return;

            // HORZ/VERT (first increment of full AV1 partition-type support, lossless only -- see
            // ComputeDecidePartition's own remarks): each produces exactly two final, non-recursing leaves
            // (spec's partition_subsize() gives the final block size directly), unlike SPLIT below. The
            // second half is only ever coded when it's actually in-bounds (hasRows/hasCols) -- always true
            // for a fully-interior node, and always false for the specific boundary branch that can legally
            // choose Horz/Vert at all (see decode_block()'s identical "if (hasRows)"/"if (hasCols)" guard on
            // its own second child).
            case Av1PartitionType.Horz:
                EncodeRectangularLeaf(s, r, c, sizeMi, half);
                if (hasRows)
                {
                    EncodeRectangularLeaf(s, r + half, c, sizeMi, half);
                }

                return;

            case Av1PartitionType.Vert:
                EncodeRectangularLeaf(s, r, c, half, sizeMi);
                if (hasCols)
                {
                    EncodeRectangularLeaf(s, r, c + half, half, sizeMi);
                }

                return;

            // HORZ_4/VERT_4 (spec's 4-way "extended" partitions, see ComputeDecidePartition's own remarks):
            // like HORZ/VERT, only ever decided from the fully-interior branch above (hasRows && hasCols both
            // already true whenever decidedType can be one of these -- BuildSplitOrHorzCdf/BuildSplitOrVertCdf
            // never offer them at a boundary node), so all 4 children are always emitted unconditionally, no
            // further in-bounds guard needed here (mirroring Av1TileDecoder.DecodePartition's own identical
            // unconditional 4-call Horz4/Vert4 case). A child leaf may still individually overhang the frame's
            // true edge exactly like Horz/Vert's own second half can -- safe for the same reason (every
            // leaf-commit function's own per-sub-block loop already implements spec's transform_block() edge
            // skip).
            // Horz4/Vert4's own 4th child is NOT unconditional -- unlike this method's own doc comment used to
            // claim (a real, confirmed-via-direct-source-comparison error: Av1TileDecoder.DecodePartition's
            // real Horz4/Vert4 cases guard *only* their own 4th DecodeBlock call with an explicit in-bounds
            // check, `r + quarterBlock4x4*3 < _miRows` / `c + quarterBlock4x4*3 < _miCols` -- the first 3
            // children are unconditional, matching this method's own first 3 calls below, but the encoder was
            // missing the exact same guard on its own 4th call). HORZ4/VERT4 is only ever offered from the
            // fully-interior branch (hasRows && hasCols both already true for the *parent* node -- see
            // RdPickPartition's own remarks), but that only guarantees the parent's own half-sized children
            // are in-bounds, not the quarter-sized 4th child specifically: a quarter-sized 4th child can still
            // extend past the frame's true edge for a non-superblock-multiple frame size, exactly the case
            // that exposed this (found via a real round-trip pixel-correctness failure, not just byte-count
            // -- writing this 4th child unconditionally means the decoder never reads those bits at all,
            // desyncing the entropy stream for everything after this node, not just misdrawing this one leaf).
            case Av1PartitionType.Horz4:
            {
                int quarter = sizeMi / 4;
                EncodeRectangularLeaf(s, r + (quarter * 0), c, sizeMi, quarter);
                EncodeRectangularLeaf(s, r + (quarter * 1), c, sizeMi, quarter);
                EncodeRectangularLeaf(s, r + (quarter * 2), c, sizeMi, quarter);
                if (r + (quarter * 3) < s.TrueMiRows)
                {
                    EncodeRectangularLeaf(s, r + (quarter * 3), c, sizeMi, quarter);
                }

                return;
            }

            case Av1PartitionType.Vert4:
            {
                int quarter = sizeMi / 4;
                EncodeRectangularLeaf(s, r, c + (quarter * 0), quarter, sizeMi);
                EncodeRectangularLeaf(s, r, c + (quarter * 1), quarter, sizeMi);
                EncodeRectangularLeaf(s, r, c + (quarter * 2), quarter, sizeMi);
                if (c + (quarter * 3) < s.TrueMiCols)
                {
                    EncodeRectangularLeaf(s, r, c + (quarter * 3), quarter, sizeMi);
                }

                return;
            }

            // HORZ_A/HORZ_B/VERT_A/VERT_B (Phase 2 AB partitions -- see RdPickPartition's own remarks for
            // the geometry derivation, confirmed against Av1TileDecoder.DecodePartition's own real cases:
            // "splitSize" -- the two square quarters -- resolves to Split's own half-x-half subsize, and
            // "subSize" -- the one rect half -- resolves to Horz's/Vert's own subsize for this same sizeMi.
            // Every one of the 3 leaf positions below is bounded by r+half/c+half, both already guaranteed
            // in-bounds by hasRows/hasCols (AB is only ever decided from the fully-interior branch, exactly
            // like Horz4/Vert4 -- see RdPickPartition's own remarks), unlike Horz4/Vert4's own real quarter-
            // sized 4th child, which reaches *past* r+half/c+half and genuinely needs its own guard -- so all
            // 3 calls here are unconditional, matching Av1TileDecoder.DecodePartition's own identical
            // unconditional 3-DecodeBlock-call HorzA/HorzB/VertA/VertB cases exactly (no bounds check there
            // either).
            case Av1PartitionType.HorzA:
                EncodeRectangularLeaf(s, r, c, half, half);
                EncodeRectangularLeaf(s, r, c + half, half, half);
                EncodeRectangularLeaf(s, r + half, c, sizeMi, half);
                return;

            case Av1PartitionType.HorzB:
                EncodeRectangularLeaf(s, r, c, sizeMi, half);
                EncodeRectangularLeaf(s, r + half, c, half, half);
                EncodeRectangularLeaf(s, r + half, c + half, half, half);
                return;

            case Av1PartitionType.VertA:
                EncodeRectangularLeaf(s, r, c, half, half);
                EncodeRectangularLeaf(s, r + half, c, half, half);
                EncodeRectangularLeaf(s, r, c + half, half, sizeMi);
                return;

            case Av1PartitionType.VertB:
                EncodeRectangularLeaf(s, r, c, half, sizeMi);
                EncodeRectangularLeaf(s, r, c + half, half, half);
                EncodeRectangularLeaf(s, r + half, c + half, half, half);
                return;

            default:
                EncodePartitionForced(s, r, c, half);
                EncodePartitionForced(s, r, c + half, half);
                EncodePartitionForced(s, r + half, c, half);
                EncodePartitionForced(s, r + half, c + half, half);
                return;
        }
    }

    /// <summary>
    /// <c>split_or_horz</c>'s derived, throwaway 2-outcome CDF (spec §8.3.2), built from the real,
    /// persistently-adapted 4-way <paramref name="partitionCdf"/> at this context -- ports
    /// <see cref="Decoding.Av1.Av1TileDecoder.ReadSplitOrHorz"/>'s exact psum formula (symbol 0 = Horz,
    /// symbol 1 = Split, matching that method's own <c>ReadSymbol(cdf) != 0</c> convention). The
    /// Horz4/Vert4 term is included only for a smaller-than-128x128 parent -- a real 128x128 block's own
    /// partition CDF (<see cref="Av1CdfContext.PartitionW128"/>) has no Horz4/Vert4 entries at all (spec
    /// never offers 4-way partitions at that size), so indexing them there would be out of range, exactly
    /// mirroring the decoder's own <c>bSize != BLOCK_128X128</c> guard. Never mutates
    /// <paramref name="partitionCdf"/> itself; the returned array is a fresh, one-shot local that
    /// <see cref="Av1SymbolEncoder.WriteSymbol"/>'s own adaptation step is free to mutate, since nothing else
    /// ever reads it again -- matching the decoder's identical "adapt a synthetic local, never the real
    /// persistent CDF" behavior for these two derived symbols.
    /// </summary>
    private static ushort[] BuildSplitOrHorzCdf(ushort[] partitionCdf, int bSize)
    {
        int psum =
            (partitionCdf[Av1PartitionType.Vert] - partitionCdf[Av1PartitionType.Vert - 1])
            + (partitionCdf[Av1PartitionType.Split] - partitionCdf[Av1PartitionType.Split - 1])
            + (partitionCdf[Av1PartitionType.HorzA] - partitionCdf[Av1PartitionType.HorzA - 1])
            + (partitionCdf[Av1PartitionType.VertA] - partitionCdf[Av1PartitionType.VertA - 1])
            + (partitionCdf[Av1PartitionType.VertB] - partitionCdf[Av1PartitionType.VertB - 1]);

        if (bSize != Av1BlockSize.Block128x128)
        {
            psum += partitionCdf[Av1PartitionType.Vert4] - partitionCdf[Av1PartitionType.Vert4 - 1];
        }

        return [(ushort)((1 << 15) - psum), 1 << 15, 0];
    }

    /// <summary><c>split_or_vert</c>'s derived CDF (spec §8.3.2), the mirror image of <see cref="BuildSplitOrHorzCdf"/> -- see its remarks (symbol 0 = Vert, symbol 1 = Split here instead).</summary>
    private static ushort[] BuildSplitOrVertCdf(ushort[] partitionCdf, int bSize)
    {
        int psum =
            (partitionCdf[Av1PartitionType.Horz] - partitionCdf[Av1PartitionType.Horz - 1])
            + (partitionCdf[Av1PartitionType.Split] - partitionCdf[Av1PartitionType.Split - 1])
            + (partitionCdf[Av1PartitionType.HorzA] - partitionCdf[Av1PartitionType.HorzA - 1])
            + (partitionCdf[Av1PartitionType.HorzB] - partitionCdf[Av1PartitionType.HorzB - 1])
            + (partitionCdf[Av1PartitionType.VertA] - partitionCdf[Av1PartitionType.VertA - 1]);

        if (bSize != Av1BlockSize.Block128x128)
        {
            psum += partitionCdf[Av1PartitionType.Horz4] - partitionCdf[Av1PartitionType.Horz4 - 1];
        }

        return [(ushort)((1 << 15) - psum), 1 << 15, 0];
    }

    // A split node's own signaling is now priced exactly (the real partition symbol's bit cost, computed in
    // DecidePartition itself via Av1SymbolEncoder.EstimateSymbolCost against the real, context-selected
    // partition CDF -- see PartitionContext) rather than this flat stand-in. A leaf's *partition* symbol is
    // priced the same exact way, but a leaf also pays signaling this cost function can't know yet at
    // DecidePartition time, before EncodeLeaf's own mode search has run: one skip bit, one yMode symbol,
    // (sometimes) an angle_delta, a uv_mode, and palette/filter-intra eligibility bits. This remaining
    // constant stands in for just that piece -- still tuned as a relative weight, not a real per-symbol cost
    // (pricing it for real, e.g. by running a cheap mode-search pass before the partition decision, is a
    // candidate for a later RD phase, not this one).
    //
    // Recalibrated from 16 to 1 alongside Av1SymbolEncoder.EstimateSymbolCost's own libaom av1_cost_symbol
    // port (see that method's remarks): that port makes every *real*, computed per-symbol cost this
    // constant sits alongside (noneBits/splitBits/lumaCost/chromaCost, all ultimately backed by
    // EstimateSymbolCost) substantially smaller and more accurate than the old renormalization-step-based
    // formula's systematic overestimate -- but this constant's own value was never re-derived from anything,
    // it was tuned by feel against the OLD formula's typically-larger magnitudes. Left at 16 once the real
    // costs around it shrank, it became a comparatively oversized fixed fee that a split's four children each
    // pay independently (roughly 4x this constant's total weight) versus a leaf's one payment -- biasing the
    // leaf-vs-split comparison toward keeping fewer, bigger leaves purely because of this stale calibration.
    // Verified via this project's own libaom byte-exact comparison harness and the existing
    // LosslessSizeRegressionTests.cs benchmark fixtures by sweeping this constant (16, 8, 4, 1, 0) at the new
    // formula: 1 is the empirically best value found across GraphicContentImage (all three sizes) and
    // FractalNoiseImage (all three sizes) -- notably non-monotonic right at 0, which regressed the smallest,
    // highest-edge-density GraphicContentImage fixture (128x128) relative to 1, while every larger fixture
    // kept marginally improving all the way to 0. This asymmetry is why 1, not 0, was kept: a real (if small)
    // per-leaf/per-child fee still discourages trivial splitting somewhere, and this was the smallest value
    // that didn't reverse that protection on the fixture most sensitive to losing it.
    private const long LeafOtherSignalingCost = 1;

    /// <summary>
    /// Real (if approximate) rate-distortion partition decision, replacing the old pure-variance flatness
    /// heuristic now that <see cref="ComputeCandidateCost"/> (Phase D technique 4) gives every leaf a real
    /// cost proxy to compare, not just "is this region exactly flat". Recursively compares the cost of
    /// keeping (<paramref name="r"/>, <paramref name="c"/>, <paramref name="sizeMi"/>) as one leaf against
    /// the summed cost of its best-decided 4 quadrants, memoized in <see cref="TileState.PartitionDecisions"/>
    /// so each node's cost is computed exactly once regardless of how many ancestors query it (a parent's
    /// own split-cost comparison, and this same node's real encode pass in
    /// <see cref="EncodePartitionForced"/>, both read the cached result rather than re-deriving it).
    ///
    /// <para>Lossless coding reconstructs bit-exactly regardless of this decision -- every 4x4
    /// Walsh-Hadamard sub-block reconstructs its own region independently of how many of them share one
    /// coding block -- so this only ever affects how many bits the coded output takes, never
    /// correctness.</para>
    ///
    /// <para><b>Known approximation</b>: this estimates every candidate leaf's cost from <see cref="TileState.SourceY"/>
    /// directly, not <see cref="TileState.ReconY"/> -- deliberately, not carelessly. For lossless content
    /// specifically, reconstructed pixels are always bit-identical to source pixels once a block is really
    /// encoded, so this sidesteps a real ordering problem: estimating a "what if we split" cost needs each
    /// of the 4 quadrants' own edge context, including from *sibling* quadrants that haven't actually been
    /// encoded yet at estimation time (only decided), so their true reconstruction doesn't exist yet to read
    /// -- their source pixels already equal what that reconstruction will be, so reading source instead is
    /// exact, not approximate, for the pixel data itself. The one real approximation left is availability
    /// flags (<c>haveAboveRight</c>/<c>haveBelowLeft</c>/<see cref="GetFilterType"/>'s smooth-neighbor check)
    /// for a not-yet-really-encoded sibling, which fall back to their safe "unavailable"/"not smooth"
    /// defaults during estimation even where the real encode will later find them true -- never wrong
    /// (BuildEdges' clamp/replicate fallback is always a legal prediction), just a slightly less-informed
    /// cost estimate for those specific candidates.</para>
    /// </summary>
    /// <summary>
    /// Top-level entry point for one superblock -- calls <see cref="RdPickPartition"/> unbounded
    /// (<see langword="long"/>.<see cref="long.MaxValue"/>), matching libaom's own real
    /// <c>av1_rd_pick_partition</c>'s own top-level caller, which always starts with an unbounded
    /// <c>RD_STATS best_rdc</c>.
    /// </summary>
    private static void DecideSuperblockPartition(TileState s, int r, int c, int sizeMi)
    {
        RdPickPartition(s, r, c, sizeMi, long.MaxValue, cnnCache: null, quadTreeIdx: 0);
    }

    /// <summary>
    /// A real, bound-threaded port of libaom's own <c>av1_rd_pick_partition</c>
    /// (<c>av1/encoder/partition_search.c:5690-6084</c>) -- replaces the old, unconditionally-exhaustive,
    /// context-free-memoized <c>ComputeDecidePartition</c>/<c>DecidePartition</c> pair (project plan's own
    /// "full libaom parity program", Phase 1). <paramref name="bestRdCost"/> is the caller's own remaining
    /// RD budget, passed <em>by value</em> exactly like libaom's own <c>RD_STATS best_rdc</c> parameter --
    /// never a shared mutable field -- and every candidate here (None, Split, Horz/Vert, Horz4/Vert4) is
    /// measured against it: a candidate only ever becomes this node's own winner if its real cost beats
    /// whatever the running best is at the moment it's tried (starting from <paramref name="bestRdCost"/>
    /// itself, then tightening as earlier candidates win). Returns <see langword="long"/>.<see cref="long.MaxValue"/>
    /// if nothing here beat the inbound bound (mirroring libaom's own <c>av1_invalid_rd_stats</c>/return-false
    /// convention) -- callers (SPLIT's own child loop below) treat that as "this whole candidate lost,
    /// stop trying to build on it further", exactly like a real bounded search would.
    ///
    /// <para><b>Phase 1's own explicit scope</b> (see the project plan's own roadmap): this threads the
    /// bound through the recursive <em>partition-type</em> search only -- SPLIT's own 4 children are
    /// evaluated one at a time with a real early-abort loop (mirroring <c>split_partition_search</c>'s own
    /// <c>for (idx = 0; idx &lt; 4 &amp;&amp; sum_rdc.rdcost &lt; best_rdc-&gt;rdcost; ++idx)</c> exactly), and
    /// <c>less_rectangular_check_level</c> (round 5's own disclosed, node-local <em>approximation</em> of
    /// this exact mechanism -- using this node's own NONE cost as a stand-in for a real inherited bound) is
    /// now retired entirely, superseded by the genuine early-abort behavior below. What this phase does
    /// <em>not</em> yet do: thread the bound into the leaf-cost estimators themselves
    /// (<c>EstimateLumaCost</c>/<c>EstimateChromaCost</c>/<c>EstimateRectLumaCostWithPalette</c>'s own
    /// internal mode/angle_delta search loops still run to completion unconditionally, matching libaom's
    /// own real behavior for whichever mode ends up winning -- see this method's own remarks at the SPLIT
    /// loop below for why a bound that only gates whether a <em>fully</em>-computed child's own cost gets
    /// used, without also truncating that child's own internal search, still exactly reproduces which
    /// candidate wins, just without libaom's own real compute savings) -- that's Phase 3's own explicitly
    /// scoped "mode-search-level bound threading" work, not attempted here.</para>
    ///
    /// <para><paramref name="cnnCache"/>/<paramref name="quadTreeIdx"/> (see
    /// <see cref="Av1IntraCnnPartitionPruner"/>) are threaded exactly as before -- see the original
    /// <c>ComputeDecidePartition</c>'s own historical remarks, preserved in spirit: freshly created once
    /// per in-frame 64x64 region, then passed down unchanged through this node's own 4 SPLIT children.</para>
    ///
    /// <para>Writes this node's own real, final decision to <see cref="TileState.PartitionDecisions"/> --
    /// but only when a candidate actually won (beat <paramref name="bestRdCost"/>); a node visited purely
    /// to find out "can you beat my caller's own bound" and failing never gets a stale/wrong record
    /// written, matching libaom's own real "no valid partition found, don't touch pc_tree" behavior. This
    /// is now a write-after-decide <em>record</em>, not a read-before-compute <em>cache</em> --
    /// <see cref="EncodePartitionForced"/>'s own later re-visit of the real winning path reads this record
    /// directly (a plain dictionary lookup, no compute-on-miss), safe because every real position on that
    /// winning path is visited by this method exactly once per superblock, always before
    /// <see cref="EncodePartitionForced"/>'s own re-visit -- see that method's own remarks.</para>
    /// </summary>
    private static long RdPickPartition(TileState s, int r, int c, int sizeMi, long bestRdCost, Av1IntraCnnPartitionPruner.Cache? cnnCache, int quadTreeIdx)
    {
        // decode_partition()'s own out-of-bounds stop (spec §5.11.4), mirroring EncodePartitionForced's
        // identical guard -- reached here whenever a parent's own split-cost candidate (below) recurses into
        // a child that falls outside the frame's real, unpadded bounds. Cost 0: nothing is ever actually
        // coded there (EncodePartitionForced's own matching guard skips writing anything for this (r, c)
        // too), so it must contribute nothing to the parent's split-cost comparison. Only reachable when
        // lossless -- see TileState.TrueMiCols's remarks.
        // A forced/floor position like this one has no real alternative to compete against, so it always
        // reports its own real cost unconditionally, regardless of bestRdCost -- exactly like libaom's own
        // equivalent early-outs, which never take a bound either.
        if (r >= s.TrueMiRows || c >= s.TrueMiCols)
        {
            s.PartitionDecisions[(r, c, sizeMi)] = (Av1PartitionType.None, 0);
            return 0;
        }

        // Spec's true 4x4 partition floor (see EncodePartitionForced's sizeMi == 1 case) -- lossless is the
        // only mode that ever reaches this (non-lossless's own floor is sizeMi == 2, below) -- nothing
        // smaller to compare against, and no partition symbol is read/written at this size at all (spec
        // §5.11.4 gates the read on bSize >= BLOCK_8X8), so unlike every case below, no partition-symbol bit
        // cost applies here either.
        if (sizeMi == 1)
        {
            long floorCost = EstimateLumaCost(s, r, c, sizeMi) + LeafOtherSignalingCost;
            s.PartitionDecisions[(r, c, sizeMi)] = (Av1PartitionType.None, floorCost);
            return floorCost;
        }

        // Real partition-symbol bit costs (Av1SymbolEncoder.EstimateSymbolCost), replacing the old flat
        // LeafSignalingCost/SplitSignalingCost stand-ins for this component specifically -- computed against
        // the same context-selected CDF EncodePartitionForced's real write uses for this same (r, c, sizeMi)
        // node (see PartitionContext), so unlike those old flat constants this actually reflects how skewed
        // this position's real, already-adapted partition CDF is (a region whose neighbors were mostly split
        // costs more to signal PARTITION_NONE here than one whose neighbors mostly weren't, and vice versa
        // for PARTITION_SPLIT) instead of charging every node the same amount regardless of context.
        int bSize = BlockSizeFromSizeMi(sizeMi);
        int ctx = PartitionContext(s, r, c, bSize, out int bsl);
        var partitionCdf = bsl switch
        {
            1 => s.CostCdf.PartitionW8[ctx],
            2 => s.CostCdf.PartitionW16[ctx],
            3 => s.CostCdf.PartitionW32[ctx],
            4 => s.CostCdf.PartitionW64[ctx],
            _ => s.CostCdf.PartitionW128[ctx],
        };

        // Signaling-bit terms (noneBits/splitBits/LeafOtherSignalingCost) are real bit *counts* -- for
        // lossless, EstimateLumaCost/EstimateChromaCost's own cost is likewise pure bits (Av1RdCost.CombineCost
        // called with lambda == 1.0 there, see ComputeCandidateCost's lossless branch), so adding them
        // directly is already apples-to-apples. Non-lossless is different: its per-candidate cost is
        // SSE + lambda*bits (the same Lagrangian ComputeCandidateCost's non-lossless branch uses), typically
        // dominated by SSE (a real image block's squared error commonly runs into the thousands, versus a
        // handful of signaling bits) -- adding raw, un-scaled bit counts into that sum makes them near-total
        // noise in the leaf-vs-split comparison, understating their real influence by roughly 1/lambda. This
        // was a real, measured bug (not just an approximation): non-lossless partition RDO first landed
        // without this scaling and made this project's benchmark image *larger*, because the comparison was
        // effectively blind to the very signaling savings it exists to weigh. Scaling by s.Lambda converts
        // these bit counts into the same SSE-equivalent units ComputeCandidateCost already uses for
        // everything else in the comparison.
        long ScaleSignalingBits(long bits) => s.Lossless ? bits : (long)Math.Round(bits * s.Lambda);

        // This encoder's own non-lossless leaf-size floor (see the project plan's partition/TX-size RDO
        // phase: EncodeLeaf's non-lossless write path only knows how to commit 8x8/16x16/32x32 leaves, never
        // smaller) -- unlike the sizeMi == 1 floor above, a real PARTITION_NONE symbol is still written at
        // 8x8 (spec reads a partition symbol at every size down to and including 8x8, only sizes *below* 8x8
        // skip it), so its bits are still charged; there is just no PARTITION_SPLIT alternative to compare
        // against here, since a non-lossless leaf can never go smaller than this.
        if (!s.Lossless && sizeMi == 2)
        {
            long floorNoneBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.None);
            long floorCost = EstimateLumaCost(s, r, c, sizeMi) + EstimateChromaCost(s, r, c, sizeMi) + ScaleSignalingBits(LeafOtherSignalingCost + floorNoneBits);
            s.PartitionDecisions[(r, c, sizeMi)] = (Av1PartitionType.None, floorCost);
            return floorCost;
        }

        int half = sizeMi / 2;

        // hasRows/hasCols (spec §5.11.4) -- see EncodePartitionForced's identical computation/remarks. Always
        // both true for non-lossless (TrueMiCols/TrueMiRows == MiCols/MiRows there), so this restriction is
        // only ever live for lossless. Computed here (moved up from just below costSplitChildren) because the
        // CNN partition pruner's own cache-creation gate below needs it first.
        bool hasRows = r + half < s.TrueMiRows;
        bool hasCols = c + half < s.TrueMiCols;

        // Av1IntraCnnPartitionPruner (IntraCnnBasedPartPruneLevel, effort >= 1 for non-screen-content): a
        // fresh per-64x64-region cache is created here, exactly once, the moment recursion enters a 64x64
        // node whose own full footprint is in-frame (mirroring libaom's own av1_is_whole_blk_in_frame gate at
        // the 64x64 root -- a boundary 64x64 region never gets a cache, even if a descendant node within it
        // would itself be fully in-frame, matching libaom's own behavior exactly rather than being more
        // permissive). Above this level (sizeMi == 32) cnnCache is always null; at this level and below, it's
        // either freshly created (sizeMi == 16) or the same object inherited from an ancestor 64x64 region
        // (sizeMi 8/4/2) -- see Av1IntraCnnPartitionPruner's own remarks for why passing it as an ordinary
        // recursive parameter, updated correctly for each of the 4 children below, needs no manual
        // save/restore the way libaom's own mutable per-thread state does.
        var childCnnCache = cnnCache;

        // libaom's own reset ("if (frame_is_intra_only(cm) && bsize == BLOCK_64X64) quad_tree_idx = 0",
        // partition_search.c:3346-3352) fires unconditionally at every 64x64 node's own entry, discarding
        // whatever quad_tree_idx would otherwise have been inherited from its own parent (sizeMi == 32 has no
        // quad-tree position of its own -- it's above the region this feature ever operates within). Mirrored
        // here: ownQuadTreeIdx is 0 at sizeMi == 16 regardless of the inherited quadTreeIdx parameter, and
        // only ever equal to the inherited value below that level (sizeMi 8/4/2, genuinely descending within
        // an already-entered 64x64 region).
        int ownQuadTreeIdx = sizeMi == 16 ? 0 : quadTreeIdx;
        if (sizeMi == 16 && s.Lossless && s.SpeedFeatures.IntraCnnBasedPartPruneLevel > 0 && hasRows && hasCols)
        {
            childCnnCache = new Av1IntraCnnPartitionPruner.Cache();
            Av1IntraCnnPartitionPruner.RunCnn(s.SourceY, s.YWidth, s.TrueMiCols * 4, s.TrueMiRows * 4, c * 4, r * 4, s.BaseQIdx, bitDepth: 8, childCnnCache);
        }

        long bestCost = bestRdCost;
        int bestType = Av1PartitionType.None;
        bool decided = false;

        // This encoder's own non-lossless leaf-size ceiling: EncodeLeaf's non-lossless write path (and
        // ComputeCandidateCost's own scratch buffers, sized for this encoder's actual largest non-lossless
        // transform, 32x32) never go above sizeMi == 8 -- see the project plan's partition/TX-size RDO
        // phase. A 64x64 node (sizeMi == 16) therefore always splits for non-lossless, without ever calling
        // EstimateLumaCost at this level at all -- that call would overflow those buffers, not just lose
        // the leaf-vs-split comparison, so it must never run here, not merely never win.
        bool noneStructurallyUnavailable = !s.Lossless && sizeMi == 16;

        // NONE evaluated first, exactly like libaom's own av1_rd_pick_partition order (none_partition_search
        // before split_partition_search) -- unlike the old unbounded ComputeDecidePartition (which computed
        // SPLIT's children first, since C#'s own field-declaration convenience made that the easiest place
        // to put the now-retired less_rectangular_check_level approximation's own childCost1-4 locals), this
        // order is now load-bearing: tightening bestCost with NONE's own real cost *before* running SPLIT's
        // own early-abort children loop below is what makes that loop's own bound threading actually prune
        // anything -- every recursive call in this whole tree ultimately traces its own inbound bestRdCost
        // back to the top-level DecideSuperblockPartition's long.MaxValue seed, so without a real
        // NONE-derived tightening at every single level, every level's own SPLIT children loop would inherit
        // an unbounded budget and never abort at all.
        bool noneAllowed = true;
        bool splitAllowed = true;
        bool rectAllowed = true;
        bool hasChroma = !s.MonoChrome;
        if (hasRows && hasCols && !noneStructurallyUnavailable)
        {
            // Non-lossless leaves reaching this point are always sizeMi 4 or 8 (16x16/32x32 --
            // noneStructurallyUnavailable already excludes sizeMi == 16, sizeMi == 2 was already handled by
            // the non-lossless floor above), i.e. exactly the sizes EncodeLeaf force-DC_PREDs chroma for --
            // see EstimateChromaCost's own remarks for why this term is necessary here. Lossless never needs
            // it (its chroma cost doesn't depend on leaf size), so EstimateChromaCost's own MonoChrome
            // short-circuit aside, this is skipped entirely there rather than adding a zero no-op term.
            long noneBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.None);

            // Lossless chroma cost is genuinely leaf-size-independent everywhere EXCEPT the one place a leaf
            // can cross the palette-eligibility boundary in one step: sizeMi > 16 (128x128) is the only
            // lossless leaf size with no palette alternative at all (real AV1 caps palette at 64x64, same
            // gate EncodeLeaf's paletteStructurallyPresent uses) whose SPLIT alternative (four 64x64
            // children) can regain it -- see EstimateLosslessChromaCost's own remarks for the full history of
            // this term (measured evidence, three earlier failed attempts, and why it's deliberately NOT
            // generalized to every lossless size).
            // lumaCost computed before chromaCost (project plan "lossless decision-phase real-cost parity"
            // section, Step 4b) -- a pure, side-effect-free reordering: EstimateLosslessChromaCost's own
            // real SearchUvMode-based search needs this leaf's own just-decided luma mode (lumaModeForChroma)
            // to select the real uv_mode CDF, the same way EncodeLeaf's real commit-time search already
            // computes luma before chroma.
            long lumaCost = EstimateLumaCost(s, r, c, sizeMi, out int lumaModeForChroma);
            long chromaCost = s.Lossless
                ? (!s.MonoChrome ? EstimateLosslessChromaCost(s, r, c, sizeMi, lumaModeForChroma) : 0)
                : EstimateChromaCost(s, r, c, sizeMi);

            // Palette (spec's own Block8x8-through-Block64x64 gate): a real alternative to regular-intra/
            // WHT-residual coding for lossless leaves -- see EstimateLosslessPaletteCost's own remarks.
            bool paletteWon = false;
            if (s.Lossless && s.AllowScreenContentTools && sizeMi <= 16)
            {
                long? paletteCost = EstimateLosslessPaletteCost(s, r, c, sizeMi, availU: r > 0, availL: c > 0);
                if (paletteCost is long pc && pc < lumaCost)
                {
                    lumaCost = pc;
                    paletteWon = true;
                }
            }

            // EstimateLosslessPaletteCost's own return value already covers Y+UV jointly (see its remarks) --
            // adding chromaCost on top once palette wins would double-charge chroma.
            long intraTotal = lumaCost + (paletteWon ? 0 : chromaCost);

            // IntraBC decision-phase awareness (project plan "lossless decision-phase real-cost parity"
            // Step 5): real libaom pays this same real per-candidate IntraBC search cost inside every
            // pick_sb_modes call, so a leaf's own partition-level cost was never actually complete without
            // it -- see EstimateIntrabcCost's own remarks for why its underlying search/cost primitives
            // (all reused verbatim from EncodeLeaf's own real, final IntraBC decision) are safe to call here,
            // speculatively, before this node's own partition choice is decided.
            if (s.Lossless && s.AllowIntrabc)
            {
                long intrabcTotal = EstimateIntrabcCost(s, r, c, sizeMi, hasChroma, intraTotal);
                if (intrabcTotal < intraTotal)
                {
                    intraTotal = intrabcTotal;
                }
            }

            long costLeaf = intraTotal + ScaleSignalingBits(LeafOtherSignalingCost + noneBits);

            // Av1IntraCnnPartitionPruner: consulted only within this fully-interior branch, matching
            // libaom's own av1_is_whole_blk_in_frame requirement applying per-node -- see this class's own
            // remarks for why {None, Split, Horz, Vert} always has at least one survivor regardless.
            if (s.Lossless && s.SpeedFeatures.IntraCnnBasedPartPruneLevel > 0 && cnnCache is not null)
            {
                int minFrameDimensionPixels = Math.Min(s.TrueMiCols, s.TrueMiRows) * 4;
                Av1IntraCnnPartitionPruner.GetPruneDecision(cnnCache, ownQuadTreeIdx, sizeMi, s.SpeedFeatures.IntraCnnBasedPartPruneLevel, minFrameDimensionPixels, out noneAllowed, out splitAllowed, out rectAllowed);
            }

            // Real, hardcoded ALLINTRA variance-forced-split override (partition_search.c:5828-5864) --
            // unconditional at every real ALLINTRA speed (not gated behind any sf.part_sf.* flag the way the
            // CNN/AB/4-way pruners above are), so evaluated unconditionally here too, after the CNN pruner so
            // it can override that decision -- exactly matching real libaom's own ordering (CNN-based pruning
            // runs inside av1_prune_partitions_before_search, called before this override's own real
            // BEGIN_PARTITION_SEARCH-time check). See ComputeLogSubBlockVariance's own remarks for the exact
            // real formula and why bsize >= BLOCK_16X16 is sizeMi >= 4 here.
            if (s.Lossless && sizeMi >= 4)
            {
                ComputeLogSubBlockVariance(s, r, c, sizeMi, out double varMin, out double varMax);
                if (varMin < 0.272 && (varMax - varMin) > 3.0)
                {
                    noneAllowed = false;
                    splitAllowed = true;
                }
            }

            if (noneAllowed && costLeaf < bestCost)
            {
                bestCost = costLeaf;
                bestType = Av1PartitionType.None;
                decided = true;
            }
        }

        // splitSignalingCost differs by which branch (fully-interior vs. one of the two boundary shapes)
        // this node turns out to be -- resolved before evaluating SPLIT's own children so the SAME
        // early-abort loop below can serve either branch (SPLIT's 4 children are the same regardless of
        // which signaling context this node is in; only the cost of choosing SPLIT *over* the alternative
        // changes).
        long splitSignalingCost;
        ushort[]? splitOrHorzCdf = null;
        ushort[]? splitOrVertCdf = null;
        if (!hasRows && !hasCols)
        {
            // Forced PARTITION_SPLIT, no symbol read/written at all -- see EncodePartitionForced's
            // identical corner case.
            splitSignalingCost = 0;
        }
        else if (!hasRows)
        {
            // hasCols true, hasRows false: split_or_horz.
            splitOrHorzCdf = BuildSplitOrHorzCdf(partitionCdf, bSize);
            splitSignalingCost = ScaleSignalingBits(Av1SymbolEncoder.EstimateSymbolCost(splitOrHorzCdf, 1));
        }
        else if (!hasCols)
        {
            // hasRows true, hasCols false: split_or_vert.
            splitOrVertCdf = BuildSplitOrVertCdf(partitionCdf, bSize);
            splitSignalingCost = ScaleSignalingBits(Av1SymbolEncoder.EstimateSymbolCost(splitOrVertCdf, 1));
        }
        else
        {
            splitSignalingCost = ScaleSignalingBits(Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Split));
        }

        // SPLIT's own 4 children, evaluated one at a time with a real early-abort loop -- a direct port of
        // libaom's own split_partition_search (partition_search.c:4570-4677): the running partial sum is
        // compared against bestCost (which at this point in this method's own order is still just
        // bestRdCost -- SPLIT is evaluated before NONE here, see the fully-interior branch below for why
        // that's still exactly right regardless of libaom's own real candidate order). Each child's OWN
        // internal search still runs to full completion unconditionally (Phase 1's own explicit scope, see
        // this method's own doc comment) -- a bounded/early-aborted computation of a child could only ever
        // report something *at least as expensive* as that same child's true unbounded minimum, so stopping
        // the LOOP early (never calling later siblings at all) never changes which candidate wins at this
        // level, only skips provably-wasted recursive work, exactly matching libaom's own real
        // correctness/performance split.
        Span<(int Row, int Col, int QuadTreeIdx)> splitChildren =
        [
            (r, c, (ownQuadTreeIdx * 4) + 1),
            (r, c + half, (ownQuadTreeIdx * 4) + 2),
            (r + half, c, (ownQuadTreeIdx * 4) + 3),
            (r + half, c + half, (ownQuadTreeIdx * 4) + 4),
        ];
        long splitPartialSum = splitSignalingCost;
        int splitChildrenEvaluated = 0;
        bool splitAborted = false;

        // Per-quadrant SPLIT costs (av1_prune_ab_partitions' own split_rd[0..3], see RdPickPartition's own
        // AB-partition candidate block below): zero-initialized and left at 0 for any quadrant this loop's
        // own early-abort never reaches, exactly matching libaom's own av1_zero-initialized RD_STATS array
        // that a short-circuited split_partition_search never overwrites (partition_search.c's own
        // split_rd[idx] assignment only runs for a quadrant actually evaluated).
        Span<long> splitChildCosts = stackalloc long[4];
        for (int i = 0; i < 4; i++)
        {
            // libaom's own av1_rd_stats_subtraction: how much budget is left for this child (and whatever
            // siblings remain) before the running sum would already lose to bestCost.
            long remainingBudget = bestCost == long.MaxValue ? long.MaxValue : Math.Max(0, bestCost - splitPartialSum);
            long childCost = RdPickPartition(s, splitChildren[i].Row, splitChildren[i].Col, half, remainingBudget, childCnnCache, splitChildren[i].QuadTreeIdx);

            // A child reporting long.MaxValue means it couldn't beat even its own remaining budget --
            // SPLIT as a whole has therefore already lost (its true cost can only be >= what's already
            // accumulated plus this now-unbounded child), so stop immediately, exactly like libaom's own
            // av1_invalid_rd_stats-then-break.
            if (childCost == long.MaxValue)
            {
                splitAborted = true;
                splitChildrenEvaluated = i + 1;
                break;
            }

            splitChildCosts[i] = childCost;
            splitPartialSum += childCost;
            splitChildrenEvaluated = i + 1;
            if (splitPartialSum >= bestCost)
            {
                splitAborted = true;
                break;
            }
        }

        if (!hasRows || !hasCols)
        {
            if (!hasRows && !hasCols)
            {
                if (!splitAborted && splitPartialSum < bestCost)
                {
                    bestCost = splitPartialSum;
                    bestType = Av1PartitionType.Split;
                    decided = true;
                }

                if (decided)
                {
                    s.PartitionDecisions[(r, c, sizeMi)] = (bestType, bestCost);
                    return bestCost;
                }

                return long.MaxValue;
            }

            if (hasCols)
            {
                // split_or_horz: spec's own two legal outcomes are recursing (Split) or taking just the
                // in-bounds top half as one final leaf (Horz) -- the bottom half falls entirely outside the
                // frame and is never coded at all (see EncodePartitionForced's hasRows-gated second-child
                // emission). Horz's own top half may itself still overhang the true frame edge (when
                // sizeMi > 2*(TrueMiRows - r)'s own half-boundary) -- safe now that every leaf-commit
                // function's own per-sub-block loop implements spec's transform_block() edge skip (see
                // EncodeLosslessLumaResidual's remarks), matching a real decoder's own identical tolerance
                // for a partially-out-of-bounds coding block exactly.
                if (!splitAborted && splitPartialSum < bestCost)
                {
                    bestCost = splitPartialSum;
                    bestType = Av1PartitionType.Split;
                    decided = true;
                }

                // Horz is only ever offered here at sizeMi <= 16, exactly mirroring the fully-interior
                // branch's own identical restriction below -- a boundary Horz candidate at sizeMi == 32
                // keeps the *other* dimension at its full, un-halved sizeMi (128x64 here), which can exceed
                // 64px in that dimension. Real AV1's own residual() (spec's own chunking, mirrored by
                // Av1TileDecoder.Residual's widthChunks/heightChunks) processes any coding block bigger than
                // 64px in either dimension as multiple independent 64x64 chunks, each with its own
                // interleaved Y/U/V transform-block loop -- a structurally different traversal order than
                // this encoder's own EncodeRectangularLeaf, which writes one plane fully across the whole
                // leaf before moving to the next. Found via a real round-trip failure (chroma decoded as
                // flat prediction with no residual, entropy-desynced from the very first sub-block) once the
                // real luma mode search made this boundary candidate's own estimated cost cheap enough to
                // actually win for the first time -- restricting to sizeMi <= 16 keeps every rectangular
                // leaf capped at 64px in both dimensions, matching EncodeRectangularLeaf's own single,
                // unchunked traversal exactly, rather than teaching it to replicate the decoder's own
                // 64x64-chunking scheme for a case only reachable at a non-superblock-multiple frame's own
                // true edge.
                if (sizeMi <= 16)
                {
                    long costHorzBoundary = ScaleSignalingBits(Av1SymbolEncoder.EstimateSymbolCost(splitOrHorzCdf!, 0) + LeafOtherSignalingCost)
                        + EstimateRectLumaCostWithPalette(s, r, c, sizeMi, half);
                    if (costHorzBoundary <= bestCost)
                    {
                        bestCost = costHorzBoundary;
                        bestType = Av1PartitionType.Horz;
                        decided = true;
                    }
                }

                if (decided)
                {
                    s.PartitionDecisions[(r, c, sizeMi)] = (bestType, bestCost);
                    return bestCost;
                }

                return long.MaxValue;
            }

            // hasRows true, hasCols false: split_or_vert, the mirror image of the hasCols branch above.
            if (!splitAborted && splitPartialSum < bestCost)
            {
                bestCost = splitPartialSum;
                bestType = Av1PartitionType.Split;
                decided = true;
            }

            // Vert restricted to sizeMi <= 16 too -- see the hasCols branch's own remarks above (the mirror
            // case: a boundary Vert candidate at sizeMi == 32 would keep height at the full, un-halved 32
            // (128px), the exact shape that exposed the chunking mismatch).
            if (sizeMi <= 16)
            {
                long costVertBoundary = ScaleSignalingBits(Av1SymbolEncoder.EstimateSymbolCost(splitOrVertCdf!, 0) + LeafOtherSignalingCost)
                    + EstimateRectLumaCostWithPalette(s, r, c, half, sizeMi);
                if (costVertBoundary <= bestCost)
                {
                    bestCost = costVertBoundary;
                    bestType = Av1PartitionType.Vert;
                    decided = true;
                }
            }

            if (decided)
            {
                s.PartitionDecisions[(r, c, sizeMi)] = (bestType, bestCost);
                return bestCost;
            }

            return long.MaxValue;
        }

        // noneStructurallyUnavailable (this method's own non-lossless leaf-size ceiling, see its own
        // declaration above): SPLIT is the only legal candidate at this forced size -- but still checked
        // against the caller's own inbound bound rather than returned unconditionally the way the old
        // unbounded ComputeDecidePartition did. An early-aborted splitPartialSum here means this whole
        // subtree already can't beat bestRdCost, and the caller needs long.MaxValue to know that, not a
        // fabricated "decision".
        if (noneStructurallyUnavailable)
        {
            if (!splitAborted && splitPartialSum < bestCost)
            {
                s.PartitionDecisions[(r, c, sizeMi)] = (Av1PartitionType.Split, splitPartialSum);
                return splitPartialSum;
            }

            return long.MaxValue;
        }

        if (splitAllowed && !splitAborted && splitPartialSum < bestCost)
        {
            bestCost = splitPartialSum;
            bestType = Av1PartitionType.Split;
            decided = true;
        }

        // less_rectangular_check_level (libaom's own speed feature, unconditionally 1 at every ALLINTRA
        // speed below 3 -- av1/encoder/speed_features.c's set_allintra_speed_features_framesize_independent
        // base init, never gated behind a speed check itself): real libaom evaluates SPLIT's own 4 children
        // one at a time and bails out of that loop early once the running partial sum already exceeds the
        // best cost found at this node so far (partition_search.c's split_partition_search, ~lines
        // 4570-4677) -- if that bail-out happens within the *first 1-2* children specifically (idx <= 2, the
        // level-1 case; level 2, active only at speed >= 3, checks more broadly and isn't relevant here),
        // do_rectangular_split gets disabled for this node -- confirmed (direct source reading) that this
        // single flag governs both HORZ/VERT and HORZ_4/VERT_4 together (partition_search.c:4183-4185's own
        // partition4_allowed derivation), matching this project's own single rectAllowed flag already
        // governing both candidate families from the Horz4/Vert4 work.
        //
        // This is now the REAL mechanism, not round 5's own disclosed, node-local approximation of it (which
        // stood this node's own NONE cost in for libaom's real caller-inherited best_rdc bound, because the
        // old ComputeDecidePartition had no genuine bound to check against at all): splitAborted and
        // splitChildrenEvaluated above come from the very same bound-threaded loop every other candidate in
        // this method now shares, seeded from a real best_rdc lineage tracing back through every recursive
        // call to the superblock root's own DecideSuperblockPartition, and tightened by this node's own
        // real NONE cost first -- exactly libaom's own idx <= 2 semantics, no stand-in left anywhere.
        bool lessRectangularDisable = splitAborted && splitChildrenEvaluated <= 2;
        rectAllowed &= !lessRectangularDisable;

        // Rectangular HORZ/VERT candidates (first increment of full AV1 partition-type support -- see
        // EncodeRectangularLeaf's own remarks for the scoping rationale): lossless only, and only for a
        // parent at or below 64x64 (sizeMi <= 16), so the resulting leaf is never bigger than 64x32/32x64 in
        // either dimension -- see EstimateRectLumaCost's remarks for why that keeps this out of the >64px
        // chunked residual path entirely. Unlike SPLIT, HORZ/VERT never recurse further: spec's own
        // partition_subsize() gives each half's *final* block size directly, so this only ever costs exactly
        // two leaves per candidate, not a recursive RdPickPartition call.
        if (s.Lossless && sizeMi <= 16 && rectAllowed)
        {
            // Individual per-half costs, named (not just summed), for av1_prune_ab_partitions' own
            // horz_rd[0]/horz_rd[1]/vert_rd[0]/vert_rd[1] inputs -- see the AB-partition candidate block
            // below, nested inside this same rectAllowed scope.
            long horzRd0 = EstimateRectLumaCostWithPalette(s, r, c, sizeMi, half);
            long horzRd1 = EstimateRectLumaCostWithPalette(s, r + half, c, sizeMi, half);
            long horzBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Horz);
            long costHorz = ScaleSignalingBits(horzBits + (2 * LeafOtherSignalingCost)) + horzRd0 + horzRd1;
            if (costHorz < bestCost)
            {
                bestCost = costHorz;
                bestType = Av1PartitionType.Horz;
                decided = true;
            }

            long vertRd0 = EstimateRectLumaCostWithPalette(s, r, c, half, sizeMi);
            long vertRd1 = EstimateRectLumaCostWithPalette(s, r, c + half, half, sizeMi);
            long vertBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Vert);
            long costVert = ScaleSignalingBits(vertBits + (2 * LeafOtherSignalingCost)) + vertRd0 + vertRd1;
            if (costVert < bestCost)
            {
                bestCost = costVert;
                bestType = Av1PartitionType.Vert;
                decided = true;
            }

            // PARTITION_HORZ_A/HORZ_B/VERT_A/VERT_B (Phase 2, "full libaom parity program": AB partitions --
            // spec's own remaining EXT_PARTITION_TYPES entries). Evaluated BEFORE Horz4/Vert4 below, matching
            // libaom's own real candidate order exactly (av1_rd_pick_partition: none -> split -> horz/vert ->
            // ab_partitions_search -> rd_pick_4partition) -- load-bearing here, not cosmetic: the pruning
            // gate below reads bestCost/bestType as they stand right after HORZ/VERT alone, and libaom's own
            // real av1_prune_ab_partitions is called (av1/encoder/partition_search.c:5940) strictly before
            // rd_pick_4partition ever runs, so best_rdcost at that call has never been tightened by a Horz4/
            // Vert4 win. Same CDF-context gate as Horz4/Vert4 (sizeMi 4/8/16 only, fully-interior-only) --
            // confirmed directly from Av1BlockTables.PartitionSubsize: every AB variant's own "subSize"
            // component (the single rect half) resolves to the exact same Block16x8/Block32x16/Block64x32-
            // family entry Horz's own subSize already uses for that sizeMi, and its "splitSize" component (the
            // two square quarters) resolves to Split's own half-x-half entry -- i.e. AB's own square
            // sub-leaves are sized and positioned identically to what a real SPLIT's own top-level quadrants
            // would be, never a smaller/deeper quarter the way Horz4/Vert4's own leaves are -- so every one of
            // an AB candidate's own 3 leaf positions is bounded by r+half/c+half, already guaranteed in-bounds
            // by this node's own hasRows/hasCols (established true for the whole fully-interior branch), with
            // no additional per-leaf bounds guard needed (unlike Horz4/Vert4's own real 4th-child guard -- see
            // EncodePartitionForced's identical remark at its own AB cases). Reuses rectAllowed for the same
            // reason Horz4/Vert4 already does. 3 leaves per candidate -- LeafOtherSignalingCost charged 3x,
            // matching Horz/Vert's own per-leaf accounting.
            if (sizeMi >= 4)
            {
                // av1_prune_ab_partitions (partition_strategy.c:1901-1991), level 1 -- unconditionally active
                // for every ALLINTRA speed (prune_ext_partition_types_search_level is never gated behind a
                // speed check in set_allintra_speed_features_framesize_independent, same precedent as
                // less_rectangular_check_level above). Two independent gates, both must pass for a given AB
                // type to even be costed:
                //
                // 1. Directional gate (horzab/vertab_partition_allowed): HORZ_A/HORZ_B only stay live when
                //    this node's own best candidate SO FAR (i.e. among just {None, Split, Horz, Vert} -- this
                //    block runs before Horz4/Vert4 ever touches bestType, see this block's own remarks above)
                //    is itself Horz, or Split, or None with a near-flat source (pbSourceVariance < 32,
                //    libaom's own untuned threshold, its own TODO comment intact). VERT_A/VERT_B mirror this
                //    with Vert in place of Horz. pbSourceVariance is computed unconditionally (not just when
                //    bestType == None) because ml_prune_ab_partition below needs it as a feature regardless of
                //    bestType -- libaom's own real x->source_variance is likewise always populated by this
                //    point (none_partition_search's own unconditional assignment whenever NONE is evaluated
                //    at all, which for this project's own fully-interior branch is always).
                // 2. RD-ratio gate (horz_a_rd/16*14 < best_rdcost, etc.): a cheap upper-bound estimate of each
                //    AB candidate's own final cost, built ONLY from costs this node has already computed for
                //    other candidates (horzRd0/1, vertRd0/1 from the HORZ/VERT block above; splitChildCosts
                //    from SPLIT's own 4-quadrant loop above) -- HORZ_A's own rect leaf is literally VERT's...
                //    no: HORZ_A's own bottom rect leaf is exactly HORZ's own bottom-half leaf (horzRd1), with
                //    its two square top leaves approximated by SPLIT's own top-left/top-right quadrant costs;
                //    HORZ_B mirrors this with horzRd0 (HORZ's own top half) plus SPLIT's bottom-left/
                //    bottom-right quadrants; VERT_A/VERT_B mirror both using vertRd0/vertRd1 and SPLIT's own
                //    left/right quadrant pairs. `/16*14` (not `*14/16`) is libaom's own exact operation order
                //    -- truncating integer division THEN multiply, not the algebraically-equivalent-in-reals
                //    but bit-different `rd*14/16`.
                //
                // best_rdcost for gate 2 is abPruneBestCost, snapshotted once right here (i.e. still exactly
                // the value bestCost holds coming out of the HORZ/VERT block above) and reused for all 4 AB
                // types' own gate checks -- matching libaom's own single av1_prune_ab_partitions call
                // computing all 4 ab_partitions_allowed[] flags from one best_rdc->rdcost snapshot before any
                // AB type is actually costed. The *live* bestCost (which each winning AB candidate below still
                // tightens for the next one, and for Horz4/Vert4 afterward) is deliberately a separate variable
                // from this snapshot.
                long abPruneBestCost = bestCost;
                int pbSourceVariance = EstimatePbSourceVariance(s, r, c, sizeMi);
                bool bestIsNone = bestType == Av1PartitionType.None;
                bool horzabAllowed = bestType == Av1PartitionType.Horz
                    || bestType == Av1PartitionType.Split
                    || (bestIsNone && pbSourceVariance < 32);
                bool vertabAllowed = bestType == Av1PartitionType.Vert
                    || bestType == Av1PartitionType.Split
                    || (bestIsNone && pbSourceVariance < 32);

                long horzARd = horzRd1 + splitChildCosts[0] + splitChildCosts[1];
                long horzBRd = horzRd0 + splitChildCosts[2] + splitChildCosts[3];
                long vertARd = vertRd1 + splitChildCosts[0] + splitChildCosts[2];
                long vertBRd = vertRd0 + splitChildCosts[1] + splitChildCosts[3];

                bool horzAAllowed = horzabAllowed && ((horzARd / 16 * 14) < abPruneBestCost);
                bool horzBAllowed = horzabAllowed && ((horzBRd / 16 * 14) < abPruneBestCost);
                bool vertAAllowed = vertabAllowed && ((vertARd / 16 * 14) < abPruneBestCost);
                bool vertBAllowed = vertabAllowed && ((vertBRd / 16 * 14) < abPruneBestCost);

                // ml_prune_partition's own AB half (partition_strategy.c:1995-2005), same unconditional-1
                // ALLINTRA status as the arithmetic gate above -- see Av1AbPartitionNnPruner's own remarks for
                // why this call, when it doesn't early-return (always true here: sizeMi is always 4/8/16 =
                // Block16x16/32x32/64x64, and this port's own lossless cost units never approach libaom's own
                // 1e9 "invalid" sentinel), REPLACES the 4 flags above wholesale rather than ANDing with them --
                // real libaom's own av1_zero_array-then-rebuild inside ml_prune_ab_partition, not an additional
                // narrowing pass.
                Span<long> splitRdForNn = [splitChildCosts[0], splitChildCosts[1], splitChildCosts[2], splitChildCosts[3]];
                int varCtx = pbSourceVariance > 0 ? System.Numerics.BitOperations.Log2((uint)pbSourceVariance) + 1 : 0;
                Av1AbPartitionNnPruner.Prune(bestType, varCtx, abPruneBestCost, horzRd0, horzRd1, vertRd0, vertRd1, splitRdForNn, sizeMi, ref horzAAllowed, ref horzBAllowed, ref vertAAllowed, ref vertBAllowed);

                if (horzAAllowed)
                {
                    long horzABits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.HorzA);
                    long costHorzA = ScaleSignalingBits(horzABits + (3 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r, c, half, half)
                        + EstimateRectLumaCostWithPalette(s, r, c + half, half, half)
                        + EstimateRectLumaCostWithPalette(s, r + half, c, sizeMi, half);
                    if (costHorzA < bestCost)
                    {
                        bestCost = costHorzA;
                        bestType = Av1PartitionType.HorzA;
                        decided = true;
                    }
                }

                if (horzBAllowed)
                {
                    long horzBBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.HorzB);
                    long costHorzB = ScaleSignalingBits(horzBBits + (3 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r, c, sizeMi, half)
                        + EstimateRectLumaCostWithPalette(s, r + half, c, half, half)
                        + EstimateRectLumaCostWithPalette(s, r + half, c + half, half, half);
                    if (costHorzB < bestCost)
                    {
                        bestCost = costHorzB;
                        bestType = Av1PartitionType.HorzB;
                        decided = true;
                    }
                }

                if (vertAAllowed)
                {
                    long vertABits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.VertA);
                    long costVertA = ScaleSignalingBits(vertABits + (3 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r, c, half, half)
                        + EstimateRectLumaCostWithPalette(s, r + half, c, half, half)
                        + EstimateRectLumaCostWithPalette(s, r, c + half, half, sizeMi);
                    if (costVertA < bestCost)
                    {
                        bestCost = costVertA;
                        bestType = Av1PartitionType.VertA;
                        decided = true;
                    }
                }

                if (vertBAllowed)
                {
                    long vertBBits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.VertB);
                    long costVertB = ScaleSignalingBits(vertBBits + (3 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r, c, half, sizeMi)
                        + EstimateRectLumaCostWithPalette(s, r, c + half, half, half)
                        + EstimateRectLumaCostWithPalette(s, r + half, c + half, half, half);
                    if (costVertB < bestCost)
                    {
                        bestCost = costVertB;
                        bestType = Av1PartitionType.VertB;
                        decided = true;
                    }
                }

                // PARTITION_HORZ_4/VERT_4 (spec's 4-way "extended" partitions): real AV1 restricts these to a
                // parent whose own partition CDF is one of PartitionW16/W32/W64 -- confirmed directly from
                // this project's own CDF tables (Av1CdfTables.DefaultPartitionW8 has only 4 entries, no
                // Horz4/Vert4 slot at all; DefaultPartitionW128 has 8, also no 4-way slot; only W16/W32/W64
                // have the full 10-entry EXT_PARTITION_TYPES set) -- i.e. sizeMi 4/8/16 (Block16x16/32x32/
                // 64x64) specifically, not sizeMi == 2 (Block8x8, too small) despite that size otherwise
                // qualifying for ordinary Horz/Vert above. Like Horz/Vert, only ever offered in this
                // fully-interior branch (hasRows && hasCols already established by the boundary-branch early
                // return above) -- BuildSplitOrHorzCdf/BuildSplitOrVertCdf's own psum already folds Horz4/
                // Vert4's probability mass into "must be Split" at a boundary node, matching
                // Av1TileDecoder.ReadSplitOrHorz/ReadSplitOrVert exactly, so a boundary node can never
                // legally choose either. Reuses rectAllowed (the CNN partition pruner's own
                // rectangular-candidate gate) rather than a separate allow flag: this project's real target
                // image evidence (aomenc's own real leaf-shape histogram) shows Horz4/Vert4 winning
                // specifically where an ordinary Horz/Vert would otherwise be the natural alternative, so
                // bucketing them under the same CNN-pruner permission this port already gives Horz/Vert is
                // the direct, minimal-diff extension rather than inventing a new prune-decision output this
                // port doesn't otherwise produce. 4 leaves per candidate, not 2 -- LeafOtherSignalingCost is
                // charged per leaf, matching Horz/Vert's own identical per-leaf accounting.
                //
                // ml_prune_partition's own 4-way half (av1_ml_prune_4_partition, partition_strategy.c:1326-1523,
                // same unconditional-1 ALLINTRA status as everywhere else this speed feature appears): unlike
                // the AB NN pruner above, this one does NOT zero-and-rebuild -- it only force-enables/disables
                // on a decisive softmax probability, otherwise leaving horz4Allowed/vert4Allowed at their
                // structural default (true, from this project's own established rectAllowed-governs-everything
                // simplification -- see Av1FourWayPartitionNnPruner's own remarks for why real libaom's own
                // narrower structural gate collapses to that same default in this port). real libaom's own
                // prune_4_partition_using_split_info (unconditional call, not level-gated) and
                // prune_ext_partition_types_search_level==2's own extra AND-gate (inactive at this port's own
                // level 1) remain real, disclosed follow-ups, not attempted this round.
                int quarter = sizeMi / 4;

                bool horz4Allowed = true;
                bool vert4Allowed = true;
                Span<int> horz4SourceVar =
                [
                    EstimatePbSourceVariance(s, r + (quarter * 0), c, sizeMi, quarter),
                    EstimatePbSourceVariance(s, r + (quarter * 1), c, sizeMi, quarter),
                    EstimatePbSourceVariance(s, r + (quarter * 2), c, sizeMi, quarter),
                    EstimatePbSourceVariance(s, r + (quarter * 3), c, sizeMi, quarter),
                ];
                Span<int> vert4SourceVar =
                [
                    EstimatePbSourceVariance(s, r, c + (quarter * 0), quarter, sizeMi),
                    EstimatePbSourceVariance(s, r, c + (quarter * 1), quarter, sizeMi),
                    EstimatePbSourceVariance(s, r, c + (quarter * 2), quarter, sizeMi),
                    EstimatePbSourceVariance(s, r, c + (quarter * 3), quarter, sizeMi),
                ];
                int minFrameDimension = Math.Min(s.YWidth, s.YHeight);
                int resIdx = (minFrameDimension >= 480 ? 1 : 0) + (minFrameDimension >= 720 ? 1 : 0);
                Av1FourWayPartitionNnPruner.Prune(bestType, varCtx, abPruneBestCost, horzRd0, horzRd1, vertRd0, vertRd1, splitRdForNn, horz4SourceVar, vert4SourceVar, pbSourceVariance, sizeMi, s.SpeedFeatures.Ml4PartitionSearchLevelIndex, resIdx, ref horz4Allowed, ref vert4Allowed);

                if (horz4Allowed)
                {
                    long horz4Bits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Horz4);
                    long costHorz4 = ScaleSignalingBits(horz4Bits + (4 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r + (quarter * 0), c, sizeMi, quarter)
                        + EstimateRectLumaCostWithPalette(s, r + (quarter * 1), c, sizeMi, quarter)
                        + EstimateRectLumaCostWithPalette(s, r + (quarter * 2), c, sizeMi, quarter)
                        + EstimateRectLumaCostWithPalette(s, r + (quarter * 3), c, sizeMi, quarter);
                    if (costHorz4 < bestCost)
                    {
                        bestCost = costHorz4;
                        bestType = Av1PartitionType.Horz4;
                        decided = true;
                    }
                }

                if (vert4Allowed)
                {
                    long vert4Bits = Av1SymbolEncoder.EstimateSymbolCost(partitionCdf, Av1PartitionType.Vert4);
                    long costVert4 = ScaleSignalingBits(vert4Bits + (4 * LeafOtherSignalingCost))
                        + EstimateRectLumaCostWithPalette(s, r, c + (quarter * 0), quarter, sizeMi)
                        + EstimateRectLumaCostWithPalette(s, r, c + (quarter * 1), quarter, sizeMi)
                        + EstimateRectLumaCostWithPalette(s, r, c + (quarter * 2), quarter, sizeMi)
                        + EstimateRectLumaCostWithPalette(s, r, c + (quarter * 3), quarter, sizeMi);
                    if (costVert4 < bestCost)
                    {
                        bestCost = costVert4;
                        bestType = Av1PartitionType.Vert4;
                        decided = true;
                    }
                }
            }
        }

        if (decided)
        {
            s.PartitionDecisions[(r, c, sizeMi)] = (bestType, bestCost);
            return bestCost;
        }

        return long.MaxValue;
    }

    /// <summary>
    /// libaom's <c>pb_source_variance</c> (as computed by <c>none_partition_search</c>'s own
    /// <c>x-&gt;source_variance</c> assignment, <c>av1/encoder/partition_search.c:2335-2338</c>, the value fed
    /// into <c>av1_prune_ab_partitions</c>'s own directional AB-partition gate) -- the Y-plane per-pixel
    /// variance of this node's own <paramref name="sizeMi"/>-sized source block, i.e. libaom's own
    /// <c>av1_get_perpixel_variance</c>: <c>ROUND_POWER_OF_TWO(sse - sum*sum/N, log2(N))</c> where <c>sum</c>/
    /// <c>sse</c> are this block's own pixel sum and sum-of-squares and <c>N</c> is its pixel count.
    ///
    /// <para>libaom's real <c>vf()</c> call compares against a constant mid-gray (128) reference rather than
    /// 0 -- algebraically irrelevant here (population variance, <c>sse - sum*sum/N</c>, is invariant under any
    /// additive constant, and this holds exactly under libaom's own truncating-integer-division arithmetic
    /// too, not just in real-number terms: shifting every sample by a constant c shifts sum by c*N and sse by
    /// a value that exactly cancels c*N's own contribution to sum*sum/N, leaving the pre-truncation real value
    /// identical, and since sum*sum's own value mod N is unchanged by any multiple-of-N shift, the truncated
    /// quotient -- and therefore the final integer result -- comes out identical too) -- so this reads
    /// <see cref="TileState.SourceY"/> directly with no offset, rather than replicating libaom's own
    /// 128-reference indirection.</para>
    /// </summary>
    private static int EstimatePbSourceVariance(TileState s, int r, int c, int sizeMi) => EstimatePbSourceVariance(s, r, c, sizeMi, sizeMi);

    /// <summary>
    /// Rectangular generalization of the square-only overload above, needed by
    /// <see cref="Av1FourWayPartitionNnPruner"/>'s own per-quarter variance features (<c>horz_4_source_var</c>/
    /// <c>vert_4_source_var</c>, each computed over a <c>sizeMi x quarter</c> or <c>quarter x sizeMi</c> strip,
    /// never a square) -- same formula, same offset-invariance argument, just with independently-sized width/
    /// height (both still always a power of two for every real block/strip shape this port ever offers, so
    /// <c>N = wPixels * hPixels</c> stays an exact power of two and <c>BitOperations.Log2</c> stays exact).
    /// </summary>
    private static int EstimatePbSourceVariance(TileState s, int r, int c, int wMi, int hMi)
    {
        int wPixels = wMi * 4;
        int hPixels = hMi * 4;
        int x = c * 4;
        int y = r * 4;

        long sum = 0;
        long sse = 0;
        for (int dy = 0; dy < hPixels; dy++)
        {
            int rowBase = ((y + dy) * s.YWidth) + x;
            for (int dx = 0; dx < wPixels; dx++)
            {
                int px = s.SourceY[rowBase + dx];
                sum += px;
                sse += (long)px * px;
            }
        }

        int n = wPixels * hPixels;
        long variance = sse - ((sum * sum) / n);

        int log2N = System.Numerics.BitOperations.Log2((uint)n);
        return (int)((variance + (1L << (log2N - 1))) >> log2N);
    }

    /// <summary>
    /// <c>log_sub_block_var</c> (<c>partition_search.c:5609-5643</c>): the minimum and maximum
    /// <c>log(1 + variance)</c> across every 4x4 luma sub-block of a <paramref name="sizeMi"/>-sized node at
    /// (<paramref name="r"/>, <paramref name="c"/>) -- feeds <see cref="RdPickPartition"/>'s own real,
    /// hardcoded ALLINTRA variance-forced-split override (project plan "lossless decision-phase real-cost
    /// parity" section, step 2). Each 4x4 sub-block's own raw variance is <c>sse - sum*sum/16</c> -- real
    /// libaom's own <c>av1_calc_normalized_variance</c> calls a real AV1 variance function against an
    /// all-zeros reference buffer, which is mathematically identical to computing the block's own true
    /// variance directly against its own mean (the same raw-sse-minus-normalized-sum-square form
    /// <see cref="EstimatePbSourceVariance(TileState, int, int, int)"/> already uses for a *whole* region);
    /// unlike that function, this does NOT divide again by the sub-block's own pixel count before the
    /// min/max accumulation -- real libaom's own <c>log_sub_block_var</c> only divides by 16 (<c>/16.0</c>)
    /// once, as part of the final <c>log1p</c> call below, after min/max have already been taken across the
    /// *raw* (undivided) per-sub-block values.
    ///
    /// <para>Deliberately does not replicate real libaom's own <c>right_overflow</c>/<c>bottom_overflow</c>
    /// pixel-clipping (real libaom calls this from a context where a node's own nominal footprint can
    /// genuinely extend past the frame edge) -- this project's own only call site
    /// (<see cref="RdPickPartition"/>'s <c>hasRows &amp;&amp; hasCols</c> branch) already only ever evaluates
    /// PARTITION_NONE, whose own <see cref="EstimateLumaCost(TileState, int, int, int)"/> cost likewise reads
    /// the full, unclipped <paramref name="sizeMi"/> footprint from <see cref="TileState.SourceY"/> with no
    /// special edge handling -- computing this variance over the identical, unclipped footprint keeps it
    /// consistent with the very NONE-candidate cost it's deciding whether to allow, rather than introducing a
    /// second, differently-scoped notion of "in frame" into the same decision.</para>
    /// </summary>
    private static void ComputeLogSubBlockVariance(TileState s, int r, int c, int sizeMi, out double varMin, out double varMax)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        long minVar4x4 = long.MaxValue;
        long maxVar4x4 = 0;

        for (int dy = 0; dy < sizePixels; dy += 4)
        {
            for (int dx = 0; dx < sizePixels; dx += 4)
            {
                long sum = 0;
                long sse = 0;
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((y + dy + i) * s.YWidth) + x + dx;
                    for (int j = 0; j < 4; j++)
                    {
                        int px = s.SourceY[rowBase + j];
                        sum += px;
                        sse += (long)px * px;
                    }
                }

                long var4x4 = sse - ((sum * sum) / 16);
                if (var4x4 < minVar4x4)
                {
                    minVar4x4 = var4x4;
                }

                if (var4x4 > maxVar4x4)
                {
                    maxVar4x4 = var4x4;
                }
            }
        }

        varMin = Math.Log(1.0 + (minVar4x4 / 16.0));
        varMax = Math.Log(1.0 + (maxVar4x4 / 16.0));
    }

    /// <summary>
    /// The best luma-only WHT-magnitude cost (<see cref="ComputeCandidateCost"/>) achievable for a
    /// <paramref name="sizeMi"/>-sized leaf at (<paramref name="r"/>, <paramref name="c"/>), searched over
    /// the same directional-mode/angle_delta candidates <see cref="EncodeLeaf"/>'s real search does, plus
    /// (project plan "lossless decision-phase real-cost parity" section, step 3) the same filter_intra
    /// candidate family -- see the filter_intra loop's own remarks below for why this one, unlike step 1,
    /// was kept. See <see cref="RdPickPartition"/>'s remarks for why this reads
    /// <see cref="TileState.SourceY"/> rather than <see cref="TileState.ReconY"/>.
    ///
    /// <para><b>Tried and reverted (project plan "lossless decision-phase real-cost parity" section, step
    /// 1):</b> adding real y_mode/angle_delta signaling cost here, via the exact same
    /// <c>Av1BlockTables.IntraModeContext[neighbor's own s.YModes[] entry]</c> formula
    /// <see cref="EncodeLeaf"/>'s own real write site uses. Measured, isolated result on this project's own
    /// real target image: luma-only, +2,743 B; combined with the analogous chroma fix in
    /// <see cref="EstimateLosslessChromaCost"/>, +5,460 B -- a real regression, not a wash. Root cause: this
    /// estimate's neighbor context for a sibling still inside the *current*, not-yet-committed superblock
    /// recursion always reads <see cref="TileState.YModes"/>'s zero-initialized (DC_PRED) default (see the
    /// availability-flag remarks immediately below, which accept the identical approximation for residual
    /// cost, where it's harmless since residual cost doesn't depend on neighbor context at all). Once a
    /// *context-dependent* signaling-cost term is added on top, that same stand-in becomes a real source of
    /// systematic error: the CDF context used here can differ from the one the real commit will actually use
    /// once its true left/above sibling has been decided, so the "more real" term ends up being priced
    /// against the wrong context more often than not. This is the exact same class of result
    /// <see cref="EstimateLosslessChromaCost"/>'s own remarks already document three separate times for
    /// chroma -- "naively making one piece of the estimate more accurate, in isolation" regresses here too,
    /// reinforcing that conclusion rather than being a one-off surprise. Not revisited until this
    /// architectural gap itself closes (the project plan's own later roadmap steps: a real, bound-threaded
    /// decision phase sharing genuinely committed neighbor state with the commit phase, not a partial-realism
    /// patch over the current speculative-recursion approximation).</para>
    /// </summary>
    private static long EstimateLumaCost(TileState s, int r, int c, int sizeMi) => EstimateLumaCost(s, r, c, sizeMi, out _);

    /// <summary>
    /// <see cref="EstimateLumaCost(TileState, int, int, int)"/>, additionally exposing the winning
    /// candidate's own final y_mode via <paramref name="bestMode"/> (spec's real *written* mode -- DcPred
    /// whenever filter_intra won, not just the plain-mode loop's own local winner) -- needed by
    /// <see cref="RdPickPartition"/>'s own chroma-cost call site (project plan "lossless decision-phase real-
    /// cost parity" section, step 4): <see cref="SearchUvMode"/>'s real <c>uv_mode</c> CDF is indexed by the
    /// luma mode that already won, matching that search's own identical <c>lumaMode</c> parameter --
    /// confirmed safe to expose (unlike Step 1's reverted luma/chroma signaling-cost attempt) since this is
    /// the *current block's own* just-decided mode, never a neighbor's committed state.
    /// </summary>
    private static long EstimateLumaCost(TileState s, int r, int c, int sizeMi, out int bestMode)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;
        bool availU = r > 0;
        bool availL = c > 0;

        // haveAboveRight/haveBelowLeft/filterTypeSmooth are deliberately NOT read from real BlockDecoded/
        // YModes state here, unlike EncodeLeaf's real search -- during DecidePartition's cost-only
        // recursion, a "what if we split" comparison estimates all 4 quadrants' costs before any of them
        // are actually committed, so a quadrant's true availability/neighbor-mode state doesn't exist yet
        // to read (see DecidePartition's remarks on the sibling-ordering approximation this already accepts
        // for pixel data via SourceY). Tried swapping in the real BlockDecoded-derived values (EncodeLeaf's
        // identical formula): passes every test (including RepeatedVerticalStripePattern_Lossless_
        // IntrabcRoundTripsExactlyAndStaysSmall, unlike an earlier, broader attempt bundling in real
        // filterTypeSmooth/YModes state too), but measured a net *loss* once both lossy and lossless are
        // weighed together on this project's benchmark image -- lossless improved by only 770 B (~0.04%)
        // while Quality=75/90 each grew (+568 B / +590 B) since this estimate is shared by both paths. Not
        // worth the added real/estimate divergence risk for a net-negative trade -- fixed, conservative
        // values keep the cost estimate a function of content and true structural position (availU/availL)
        // only, not of what else happened to be encoded first.
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;
        int log2Size = PixelLog2(sizeMi);
        int bSize = BlockSizeFromSizeMi(sizeMi);
        long bestCost = long.MaxValue;

        // Below Block8x8 (sizeMi == 1, the new 4x4 leaf floor), spec's intra_angle_info_y/_uv (§5.11.42/.43)
        // never reads an angle_delta symbol and always reconstructs with angleDelta == 0 regardless of mode
        // -- mirroring that here, not just at the write site (see EncodeLeaf's own AngleDelta write-site
        // remarks), is required for correctness, not just signaling economy: searching a nonzero angle_delta
        // this proxy or the real search could still "win" with, only to have the real decoder reconstruct
        // angleDelta == 0 instead, would desync this encoder's own recorded reconstruction from what a real
        // decoder produces for the same bitstream.
        bool angleDeltaAllowed = sizeMi >= 2;

        Span<bool> directionalModeSkipMask = stackalloc bool[13];
        ComputeLumaPruning(s, s.Lossless, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, directionalModeSkipMask, out bool skipSmoothVh, out bool skipSmoothPlain);

        Span<long> topIntraModelRd = stackalloc long[4];
        int topModelCount = s.Lossless ? s.SpeedFeatures.TopIntraModelCountAllowed : 0;
        topIntraModelRd[..Math.Max(topModelCount, 0)].Fill(long.MaxValue);
        long bestModelRd = long.MaxValue;

        // Tracks the plain-mode loop's own winner purely to key FILTER_INTRA's own submode mask below
        // (PruneFilterIntraLevel == 1's FilterIntraModeUsedFlag[bestPlainMode]) -- distinct from bestMode
        // (this function's own out parameter, tracking the real final *written* mode, DcPred whenever
        // filter_intra ends up winning).
        int bestPlainMode = Av1IntraMode.DcPred;
        bestMode = Av1IntraMode.DcPred;

        foreach (int mode in CandidateModes)
        {
            if (IsLumaModePruned(mode, directionalModeSkipMask, skipSmoothVh, skipSmoothPlain))
            {
                continue;
            }

            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                if (topModelCount > 0)
                {
                    long modelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, sizePixels, ptype: 0, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                    if (Av1IntraModelRdPruner.PruneIntraYMode(modelRd, ref bestModelRd, topIntraModelRd[..topModelCount]))
                    {
                        continue;
                    }
                }

                long cost;
                if (s.Lossless)
                {
                    // Real AV1 lossless intra prediction is genuinely per-4x4-sub-block (spec transform_block(),
                    // TX_4X4 forced regardless of coding-block size -- see ComputeLosslessWholeLeafCostPerSubBlock's
                    // own remarks) at every leaf size above 4x4, not just above 64x64. This branch used to be
                    // gated `sizePixels > 64` (only the 128x128 case, where a single whole-leaf Predict call is
                    // architecturally impossible -- PredictSmooth's own weight tables don't exist beyond 64x64),
                    // leaving every other lossless size on the whole-leaf-single-shot shortcut -- a real,
                    // previously-undocumented divergence from both real libaom's own architecture and this
                    // project's own real commit-time write (EncodeLosslessLumaResidual), both of which always
                    // re-predict fresh per sub-block using progressively-reconstructed neighbors. Widened to
                    // cover every lossless size uniformly: at exactly one sub-block (sizePixels == 4) the
                    // per-sub-block loop and the old whole-leaf shortcut compute identically, so this is a strict
                    // generalization, not a special case.
                    //
                    // Round N+40 (project plan progress log): widening this decision-phase estimate ALONE (this
                    // branch only) was tried first and measured, via the full corpus, to be a severe, universal
                    // regression (every file grew, +0.09% to +28.68%, none improved). Root-caused, not reverted
                    // for good: EncodeLeaf's own real commit-time luma mode SELECTION (further down in this
                    // file) still used the old whole-leaf shortcut at the time, while EncodeLosslessLumaResidual's
                    // own real residual WRITE has always used fresh per-sub-block prediction for whatever mode
                    // wins there, regardless of how it was selected. This estimate, once fixed alone, correctly
                    // promised "NONE will cost X once the real best mode is found" -- but EncodeLeaf's still-
                    // unfixed selection could pick a *different* mode than the one X assumed, whose real,
                    // per-sub-block-as-actually-written cost was higher than X. Round N+41 fixed EncodeLeaf's
                    // own mode selection to use this identical accurate primitive too (see its own remarks),
                    // closing that mismatch -- restoring this estimate here as part of that same fix, not a
                    // second, independent attempt. useAdaptiveCdf stays false here (the 128x128 case's own
                    // adaptive variant was tried and measured net-negative on its own, unrelated axis -- not
                    // re-tried for smaller sizes).
                    cost = ComputeLosslessWholeLeafCostPerSubBlock(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.EdgeMaxX, s.EdgeMaxY, x, y, bitDepth: 8);
                    cost = ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s));
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestPlainMode = mode;
                    bestMode = mode;
                }
            }
        }

        // FILTER_INTRA (spec §5.11.24) -- project plan's own "lossless decision-phase real-cost parity"
        // section, step 3: extends this decision-phase estimate to also consider the filter_intra
        // candidate family EncodeLeaf's own real commit-time search already does (see that method's own
        // remarks for the full real-libaom derivation this mirrors), closing the one real candidate
        // family previously missing from decision-phase luma costing entirely (this file's own prior
        // doc comment here claimed those "win rarely and by little" -- an unverified claim from before
        // Phase 3 made the real cost primitives this reuses bound-threaded and cheap to call
        // speculatively; not re-verified independently, simply ported in full per this round's own
        // standing "port the real mechanism, don't approximate" instruction).
        //
        // Unlike step 1's reverted y_mode/uv_mode signaling-cost attempt, neither CDF this candidate's
        // own signaling cost reads is indexed by any neighbor/committed-sibling state:
        // CostCdf.FilterIntra[bSize] keys only off this node's own real, always-correct bSize, and
        // CostCdf.FilterIntraMode is a single flat CDF with no context at all -- so this doesn't
        // reintroduce that round's own context-mismatch failure mode. filterTypeSmooth stays the same
        // conservative `false` constant the plain-mode loop above already uses (not GetFilterType's own
        // real neighbor-YModes read) for the identical reason the plain-mode loop's own remarks give.
        bool skipFilterIntraEntirely = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 2;
        if (sizePixels <= 32 && !skipFilterIntraEntirely)
        {
            int filterIntraModeMask = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 1 ? FilterIntraModeUsedFlag[bestPlainMode] : 0x1F;

            for (int filterMode = 0; filterMode < 5; filterMode++)
            {
                if ((filterIntraModeMask & (1 << filterMode)) == 0)
                {
                    continue;
                }

                if (topModelCount > 0)
                {
                    long filterModelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, sizePixels, ptype: 0, mode: Av1IntraMode.DcPred, angleDelta: 0, useFilterIntra: true, filterIntraMode: filterMode, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                    if (Av1IntraModelRdPruner.PruneFilterIntraModelRd(filterModelRd, ref bestModelRd))
                    {
                        continue;
                    }
                }

                long filterResidualUnits512;
                if (s.Lossless)
                {
                    // Same per-4x4-sub-block generalization as the plain-mode loop above -- see its own remarks
                    // (Round N+40/N+41). filterIntraMode's own submode search never reaches the 128x128-only
                    // oversized case (this whole block is gated sizePixels <= 32), so this just brings
                    // filter_intra's own decision-phase estimate onto the same real, per-sub-block primitive
                    // the plain-mode loop above now uses uniformly.
                    ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), mode: Av1IntraMode.DcPred, angleDelta: 0, useFilterIntra: true, filterIntraMode: filterMode, filterTypeSmooth: false, useRealBoundaryAvailability: false, out filterResidualUnits512);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: true, filterIntraMode: filterMode, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.EdgeMaxX, s.EdgeMaxY, x, y, bitDepth: 8);
                    ComputeCandidateCostPrecise(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), out filterResidualUnits512);
                }

                long filterSignalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntra[bSize], 1) + Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntraMode, filterMode);
                long filterCost = (long)Math.Round((filterResidualUnits512 + filterSignalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (filterCost < bestCost)
                {
                    bestCost = filterCost;
                    bestMode = Av1IntraMode.DcPred;
                }
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Cheap DC_PRED/PAETH_PRED-only 2-candidate chroma-cost proxy (adaptive-CDF trial via
    /// <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s <c>useAdaptiveCdf</c>) for a
    /// <paramref name="sizeMi"/>-sized square lossless leaf's real chroma cost, used specifically at the
    /// palette-eligibility boundary (see <see cref="RdPickPartition"/>'s own remarks: sizeMi &gt; 16 is
    /// the one lossless leaf size with no palette alternative at all, whose SPLIT alternative can regain it).
    ///
    /// <para>Two independent, real, measured fixes went into this: (1) DC_PRED alone badly overprices a
    /// smoothly-varying (non-flat) chroma plane -- adding PAETH_PRED as a second candidate and keeping
    /// whichever is cheaper dropped the harness's own gradient test leaf's estimate ~12x (45107 -> 3632 bits)
    /// with zero regressions. (2) A non-adapting per-symbol trial across many sub-blocks systematically
    /// overprices a flat/repetitive plane relative to what a real, CDF-adapting commit would achieve --
    /// <c>useAdaptiveCdf: true</c> (a scratch-cloned <see cref="TileState.ScratchCdf"/>, reseeded once per
    /// candidate, never per sub-block, never written back to the real <see cref="TileState.Cdf"/>) fixes
    /// this; see the project plan's "Intra-candidate CDF adaptation fix" section for the full investigation.
    /// </para>
    ///
    /// <para>Deliberately kept as a cheap proxy rather than upgraded to a real, full <c>SearchUvMode</c>-based
    /// search: a later investigation (project plan "Attempt to implement 'the correct changes that match
    /// libaom behavior'") tried exactly that -- giving every lossless leaf size a real, <c>SearchUvMode</c>-
    /// based chroma cost matching commit quality, confirmed via direct libaom source reading to be the
    /// architecturally faithful design (real libaom's own partition search uses the identical full chroma
    /// search for every partition-type candidate as it does for the real commit, with no separate cheap
    /// decision-phase proxy at all) -- but every concrete implementation tried (a per-candidate CDF-adaptation
    /// simulation; the plain, non-adaptive current-CDF read libaom's own static-table architecture actually
    /// uses; and a hybrid adding <c>useAdaptiveCdf</c> only to the oversized/128x128 branch) measurably
    /// regressed the real downloaded target image (+9.50% -&gt; +13.11%, then +12.29%) despite passing every
    /// synthetic test and the full regression suite, and was each fully reverted. See that section for the
    /// full, repeated negative result -- this proxy, scoped narrowly to the one boundary where it's a proven
    /// win, remains the shipped chroma-cost estimate for None/Split at sizeMi &gt; 16.</para>
    ///
    /// <para><b>Tried and reverted a fourth time (project plan "lossless decision-phase real-cost parity"
    /// section, step 1):</b> adding real uv_mode signaling cost (indexed by the real winning luma mode, the
    /// same real <c>UvModeCflAllowed</c>/<c>CflNotAllowed[lumaMode]</c> CDF selection
    /// <see cref="SearchUvMode"/>'s own real commit-time search uses), combining the previous two
    /// independent per-plane minimums into one per-shared-candidate-mode minimum to match the real single
    /// per-block <c>uv_mode</c> spec semantics. Measured, isolated result on this project's own real target
    /// image: chroma-only, +1,849 B -- a real regression, matching the identical negative-result pattern the
    /// three earlier attempts above already established, for the same underlying reason
    /// <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s own identical "tried and reverted" remarks give for the analogous
    /// luma fix: a genuinely real, context-dependent cost term bolted onto this estimate's still-approximate
    /// decision-phase framework (fixed availability/neighbor-context stand-ins, no real bound-threaded
    /// search) ends up priced against the wrong context often enough to net regress, even though the term
    /// itself is computed correctly. Left as a further, independent data point for the same eventual real fix
    /// (a genuinely real, bound-threaded decision phase, not another isolated accuracy patch).</para>
    ///
    /// <para><b>Fifth attempt (project plan "lossless decision-phase real-cost parity" section, Step 4b) --
    /// the DC/PAETH-only proxy above finally replaced with a real <see cref="SearchUvMode"/> call, kept.</b>
    /// A new <c>useRealBoundaryAvailability</c> parameter on that method substitutes the same per-4x4-
    /// sub-block, *source*-pixel primitive Step 4a's <see cref="SearchRectUvMode"/> already proved safe for
    /// the rectangular path, in place of its own common-size branch's Recon-based read -- exact, not
    /// approximate, for lossless (source == recon at zero distortion). This fixed the real,
    /// previously-undocumented cause common to all four earlier attempts (stale/garbage pixel data from
    /// <see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>, not merely an imprecise CDF context).
    ///
    /// <para><b>A real, separate bug found and fixed along the way, unrelated to this change's own logic:</b>
    /// this real chroma search initially exposed a genuine round-trip PIXEL correctness failure on one real
    /// corpus file (`colors_text_wcg_sdr_rec2020`) -- simply by choosing a different partition/mode structure
    /// than the old proxy did, not through any fault of its own. Root-caused via a direct, symbol-by-symbol
    /// entropy-coder range-state trace (encoder vs. a real decode of its own output, this investigation's own
    /// established technique, extended here to the whole bitstream rather than one CDF context) to
    /// <c>WriteColorMapTokens</c>: unlike every other residual-writing loop in this file, it scanned a
    /// palette leaf's full *nominal* width x height region with no clipping to the frame's TRUE (unpadded)
    /// edge, writing real entropy-coded color-map tokens for off-screen cells a real decoder's own
    /// <see cref="Av1TileDecoder.DecodeColorMapTokens"/> (spec §5.11.47) never reads at all (off-screen
    /// cells are filled by edge-replication afterward, not separately coded) -- a genuine, independently
    /// pre-existing gap (present since palette support was first added, simply never triggered by any
    /// partition structure any prior round happened to produce) that permanently desynced the entropy
    /// coder's own range/CDF-adaptation state for every symbol after the first affected leaf. Fixed at its
    /// own real source (see that method's own remarks) by clipping to the same onscreenWidth/onscreenHeight
    /// bound the decoder already uses -- confirmed via the same trace technique that self-round-trip goes
    /// fully clean once fixed, independent of this chroma-search change itself.</para>
    /// </para>
    /// </summary>
    private static long EstimateLosslessChromaCost(TileState s, int r, int c, int sizeMi, int lumaMode)
    {
        int x = c * 4;
        int y = r * 4;
        var result = SearchUvMode(s, r, c, x, y, sizeMi, availU: r > 0, availL: c > 0, lumaMode, useRealBoundaryAvailability: false);
        return result.BestCost;
    }

    /// <summary>
    /// Cheap DC_PRED/PAETH_PRED-only 2-candidate chroma-cost proxy for a rectangular (Horz/Vert) leaf,
    /// generalized to independent <paramref name="wMi"/>/<paramref name="hMi"/> via
    /// <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s own <c>heightPixels</c> parameter -- the same
    /// mechanism <see cref="EstimateLosslessChromaCost"/> uses for square leaves.
    ///
    /// <para>Added specifically for <see cref="EstimateRectLumaCostWithPalette"/>'s own cost, once the real
    /// luma mode search (see <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>'s own remarks) made a Horz/Vert candidate
    /// genuinely competitive against None/Split for the first time and exposed a real, measured regression
    /// (`GraphicContentImage` 256x256: 22197 -> 31253 bytes, +40%) traced to Horz/Vert's own chroma cost
    /// being entirely uncosted while its luma-only cost was now accurate enough to win unfairly often. This
    /// proxy fixed that specific regression.</para>
    ///
    /// <para><b>Upgraded to a real, full <c>SearchRectUvMode</c>-based search (project plan "lossless
    /// decision-phase real-cost parity" section, step 4):</b> the DC/PAETH-only proxy above described three
    /// prior attempts at this exact upgrade (for the analogous square-leaf case) that each regressed the real
    /// target image, plus Step 1's own reverted signaling-cost attempt for this same rectangular case --
    /// re-scoped this round after a direct source investigation (see the project plan's own "Step 4 scoping
    /// addendum") found the real, previously undocumented cause those three historical attempts most likely
    /// shared: unlike this rectangular leaf's own real commit-time search, the *square* leaf's real
    /// <see cref="SearchUvMode"/> reads genuinely real, commit-only state (<see cref="TileState.ReconU"/>/
    /// <see cref="TileState.ReconV"/>, real <see cref="GetBlockDecoded"/> availability) that does not exist
    /// yet during <see cref="RdPickPartition"/>'s own speculative recursion. This rectangular leaf's own
    /// <see cref="SearchRectUvMode"/> was already free of that specific problem (its own two per-plane trials
    /// already go through the same real, *source*-pixel <see cref="ComputeLosslessWholeLeafCostPerSubBlockPrecise"/>
    /// primitive this estimate's own predecessor proxy already used safely) -- only
    /// <see cref="GetChromaFilterType"/>'s own real neighbor-<see cref="TileState.UvModes"/> read and the
    /// hardcoded real <c>useRealBoundaryAvailability: true</c> needed a decision-phase-safe substitute, both
    /// added via <see cref="SearchRectUvMode"/>'s own new <c>useRealBoundaryAvailability</c> parameter
    /// (<see langword="false"/> here). <paramref name="lumaMode"/> is the real, current block's own
    /// already-decided luma mode (never a neighbor's), confirmed safe to thread into <c>uv_mode</c>'s own real
    /// CDF selection the identical way <see cref="SearchRectUvMode"/>'s own real commit-time call site already
    /// does.</para>
    /// </summary>
    private static long EstimateRectChromaCost(TileState s, int r, int c, int wMi, int hMi, bool availU, bool availL, int lumaMode)
    {
        int bSize = BlockSizeFromWidthHeightMi(wMi, hMi);
        bool cflAllowed = Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4;

        var (_, _, bestCost) = SearchRectUvMode(s, r, c, wMi, hMi, availU, availL, lumaMode, cflAllowed, useRealBoundaryAvailability: false);
        return bestCost;
    }

    /// <summary>
    /// Real per-4x4-sub-block WHT trial cost of one (<paramref name="mode"/>, <paramref name="angleDelta"/>)
    /// luma candidate for a <paramref name="wMi"/>x<paramref name="hMi"/> (mi units) rectangular (HORZ/VERT)
    /// leaf at (<paramref name="r"/>, <paramref name="c"/>) -- the Horz/Vert counterpart to
    /// <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s square-only per-sub-block trial, generalized to
    /// independent width/height rather than duplicating that function's own already-verified square path.
    /// Reused by both <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/> (the partition-decision estimate,
    /// <paramref name="useRealBoundaryAvailability"/>: <see langword="false"/>) and
    /// <see cref="EncodeRectangularLeaf"/>'s own real, commit-time mode search
    /// (<paramref name="useRealBoundaryAvailability"/>: <see langword="true"/>) -- both read
    /// <see cref="TileState.SourceY"/>, never <see cref="TileState.ReconY"/>, mirroring
    /// <see cref="EncodeLeaf"/>'s own identical oversized-leaf-branch precedent
    /// (<see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s callers): for lossless, a real commit's own
    /// reconstruction is always bit-identical to source, so reading source is exact (not approximate) for the
    /// pixel data itself even during a real, about-to-commit search, and avoids this leaf's own
    /// not-yet-written <see cref="TileState.ReconY"/> positions ever leaking a previous candidate's stale
    /// trial data into a later one's prediction.
    /// </summary>
    private static long ComputeRectLumaCostForMode(TileState s, int r, int c, int wMi, int hMi, int mode, int angleDelta, bool filterTypeSmooth, bool useRealBoundaryAvailability, bool useFilterIntra = false, int filterIntraMode = 0)
        => ComputeRectLumaCostForModePrecise(s, r, c, wMi, hMi, mode, angleDelta, filterTypeSmooth, useRealBoundaryAvailability, out _, useFilterIntra, filterIntraMode);

    /// <summary>
    /// <see cref="ComputeRectLumaCostForMode"/>, additionally exposing the raw, unrounded
    /// <see cref="Av1TrialSymbolSink.Units512"/> total via <paramref name="units512"/> -- see
    /// <see cref="ComputeCandidateCostPrecise"/>'s own identical remarks for the full rationale (this is the
    /// same precision propagation, applied to <see cref="EncodeRectangularLeaf"/>'s own real per-candidate
    /// mode search instead of <see cref="EncodeLeaf"/>'s).
    /// </summary>
    private static long ComputeRectLumaCostForModePrecise(TileState s, int r, int c, int wMi, int hMi, int mode, int angleDelta, bool filterTypeSmooth, bool useRealBoundaryAvailability, out long units512, bool useFilterIntra = false, int filterIntraMode = 0, long boundUnits512 = long.MaxValue)
    {
        int x = c * 4;
        int y = r * 4;
        var pred = s.Pred;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;
        scratch.SeedFrom(LiveYCoeffCtx(s), x >> 2, wMi, y >> 2, hMi);
        trial.Reset();

        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;
        int subBlockMiRowBase = r & s.SbMiMask;
        int subBlockMiColBase = c & s.SbMiMask;

        bool aborted = false;
        for (int dr = 0; dr < hMi && !aborted; dr++)
        {
            for (int dc = 0; dc < wMi; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > s.EdgeMaxX || subY > s.EdgeMaxY)
                {
                    continue;
                }

                bool availU = subR > 0;
                bool availL = subC > 0;

                bool haveAboveRight;
                bool haveBelowLeft;
                if (useRealBoundaryAvailability)
                {
                    int subBlockMiRow = subBlockMiRowBase + dr;
                    int subBlockMiCol = subBlockMiColBase + dc;
                    haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + 1);
                    haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + 1, subBlockMiCol - 1);
                }
                else
                {
                    // Same interior-raster-scan derivation ComputeLosslessWholeLeafCostPerSubBlock's own
                    // non-real branch uses, generalized from one shared n to independent wMi: this leaf's own
                    // above-right sub-block was already visited at raster index (dr-1)*wMi+(dc+1), always
                    // earlier than the current dr*wMi+dc, whenever it's interior (dr>0 && dc+1<wMi) --
                    // provably already available, matching what a real GetBlockDecoded read would show. A
                    // leaf's own below-left is never interior in a top-to-bottom, left-to-right raster scan
                    // (the row below is never visited yet), matching real AV1 decode order too.
                    haveAboveRight = dr > 0 && (dc + 1) < wMi;
                    haveBelowLeft = false;
                }

                Av1IntraPrediction.BuildEdges(s.SubBlockAbove, s.SubBlockLeft, s.SourceY, s.YWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, s.SubBlockAbove, s.SubBlockLeft, mode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, subX, subY, bitDepth: 8);

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, 4, ptype: 0, subX4, subY4, scratch, writeLumaTxType: null, blockSize: wMi * 4, blockHeight: hMi * 4, updateContext: true);

                // Same exact, provably-safe early abort as ComputeCandidateCostPrecise's own identical
                // mechanism (see its remarks) -- Phase 3's own bound-threading, extended here to
                // EncodeRectangularLeaf's own separate luma cost function. boundUnits512 defaults to
                // long.MaxValue, so the pre-existing decision-phase estimator call site (EstimateRectLumaCost,
                // useRealBoundaryAvailability: false) is completely unaffected.
                if (trial.Units512 >= boundUnits512)
                {
                    aborted = true;
                    break;
                }
            }
        }

        if (aborted)
        {
            // Same guaranteed-losing sentinel convention as ComputeCandidateCostPrecise's own identical abort
            // path -- see its remarks.
            units512 = long.MaxValue / 2;
            return long.MaxValue;
        }

        units512 = trial.Units512;
        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>Real spec gate for whether a rectangular leaf's own <c>intra_angle_info_y</c>/<c>_uv</c> (§5.11.42/.43) is structurally present -- <c>Av1TileDecoder</c>'s own <c>_miSize &gt;= Block8x8</c> check, generalized from a single square <c>sizeMi</c> to the leaf's real <see cref="Av1BlockSize"/>. Only ever <see langword="false"/> for the two smallest Horz/Vert shapes this project produces (2x1/1x2 mi, i.e. <c>BLOCK_8X4</c>/<c>BLOCK_4X8</c>, both below <c>BLOCK_8X8</c> in AV1's own block-size ordering) -- every larger rectangular shape (4x2 mi and up) already exceeds it.</summary>
    private static bool RectAngleDeltaAllowed(int bSize) => bSize >= Av1BlockSize.Block8x8;

    /// <summary>
    /// Rectangular (HORZ/VERT) counterpart to <see cref="SearchUvMode"/>: closes <see cref="EncodeRectangularLeaf"/>'s
    /// previously-disclosed "chroma stays DC_PRED-only" scope gap with a real chroma mode search for its
    /// commit, exactly mirroring how <see cref="ComputeRectLumaCostForMode"/> already closed the equivalent
    /// luma gap. Deliberately narrower than the square path's own <see cref="SearchUvMode"/>: no CFL (a
    /// separate, natural follow-up, matching this leaf's own already-disclosed scope decisions) and no
    /// HOG/SATD pruning -- this leaf is capped at 64x32/32x64 (at most 32 sub-blocks per plane), cheap enough
    /// for a plain exhaustive <see cref="CandidateModes"/> sweep, exactly the same rationale
    /// <see cref="ComputeRectLumaCostForMode"/>'s own call site already gives for skipping HOG/SATD on the
    /// luma side.
    ///
    /// <para>Both U and V share one <c>uv_mode</c>/<c>angle_delta_uv</c> (spec's own single per-block chroma
    /// mode), costed via <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/> (the same real, per-4x4-sub-block
    /// WHT trial <see cref="SearchUvMode"/>'s own oversized-chroma branch already uses, generalized to this
    /// leaf's true <paramref name="wMi"/>x<paramref name="hMi"/> footprint via that method's own
    /// <c>heightPixels</c> parameter -- already proven safe for a rectangular shape by
    /// <c>EstimateRectChromaCost</c>'s own decision-phase estimate) with <c>useRealBoundaryAvailability: true,
    /// useAdaptiveCdf: false</c> -- a real, non-simulated cost against the actual current <see cref="TileState.Cdf"/>,
    /// matching what this search's own winner will really be committed against, exactly like
    /// <see cref="ComputeRectLumaCostForMode"/>'s own identical choice for luma.</para>
    ///
    /// <para><paramref name="useRealBoundaryAvailability"/> (project plan "lossless decision-phase real-cost
    /// parity" section, step 4): defaults <see langword="true"/>, preserving <see cref="EncodeRectangularLeaf"/>'s
    /// own real, commit-time call site unchanged. Passing <see langword="false"/> (the new decision-phase
    /// call site, <see cref="EstimateRectChromaCost"/>) does two things together, never independently: skips
    /// the real <see cref="GetBlockDecoded"/>-based haveAboveRight/haveBelowLeft reads inside the shared
    /// per-sub-block primitive below (already a proven-safe substitution -- the exact same flag
    /// <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>'s own call to the luma sibling of
    /// that same primitive already uses), and skips <see cref="GetChromaFilterType"/>'s own real
    /// <see cref="TileState.UvModes"/> neighbor read in favor of the plain luma loop's own already-proven-safe
    /// <see langword="false"/> constant (see <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s own
    /// identical-purpose remarks for why a *committed*-neighbor-state read is unsafe from this function's own
    /// new, speculative decision-phase call site, unlike every other input here). Both reads are otherwise
    /// only ever populated by real commit paths (<see cref="EncodeChromaRegion"/> and friends) -- never by
    /// any <c>Estimate*</c>/<see cref="RdPickPartition"/> decision-phase function -- so reading either one
    /// from a not-yet-committed sibling inside <see cref="RdPickPartition"/>'s own speculative recursion would
    /// read stale/default data, not a mere approximation.</para>
    /// </summary>
    private static (int Mode, int AngleDelta, long BestCost) SearchRectUvMode(TileState s, int r, int c, int wMi, int hMi, bool availU, bool availL, int lumaMode, bool cflAllowedForCost, bool useRealBoundaryAvailability = true)
    {
        int x = c * 4;
        int y = r * 4;
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        bool angleDeltaAllowed = RectAngleDeltaAllowed(BlockSizeFromWidthHeightMi(wMi, hMi));
        bool filterTypeSmooth = useRealBoundaryAvailability && GetChromaFilterType(s, r, c, availU, availL, subsampled: false);

        // Indexed by the luma leaf's own real winning mode, matching SearchUvMode's identical uv_mode CDF
        // selection (spec's is_cfl_allowed()-gated UvModeCflAllowed/CflNotAllowed[lumaMode] pair) -- resolved
        // once per leaf, not per candidate, since it doesn't depend on which uv_mode candidate is being priced.
        var uvModeCostCdf = cflAllowedForCost ? s.CostCdf.UvModeCflAllowed[lumaMode] : s.CostCdf.UvModeCflNotAllowed[lumaMode];

        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        long bestCost = long.MaxValue;

        foreach (int mode in CandidateModes)
        {
            // Real signaling-cost-only early-reject -- see SearchUvMode's identical remarks (the real libaom
            // function this ports, av1_rd_pick_intra_sbuv_mode, intra_mode_search.c:922-926) for the full
            // rationale: a mathematically exact bound, checked once per mode before any real residual trial.
            if (Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, mode) > bestCost)
            {
                continue;
            }

            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                // Phase 3's own bound-threading (project plan) -- same U-then-V derivation as SearchUvMode's
                // own identical increment (see that method's own remarks for the full rationale: real
                // libaom's av1_txfm_uvrd shares one un-reduced bound across both planes for an intra block;
                // this project's own tighter-but-still-safe bound, treating the not-yet-computed plane as its
                // own provable 0 minimum, is used instead since matching libaom's own looser bound bit-for-bit
                // isn't required for byte-identical output, only "safe" is).
                long modeSignaling = Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, mode);
                long angleSignaling = directional ? Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.AngleDelta[mode - Av1IntraMode.VPred], angleDelta + MaxAngleDelta) : 0;

                long uBound = bestCost == long.MaxValue ? long.MaxValue : (bestCost - modeSignaling - angleSignaling) * 512 - 256;
                long costU = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceU!, s.ChromaWidth, s.ChromaHeight, r, c, x, y, widthPixels, ptype: 1, LiveUCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability, out _, heightPixels: heightPixels, boundUnits512: uBound);
                if (costU == long.MaxValue)
                {
                    continue;
                }

                long vBound = bestCost == long.MaxValue ? long.MaxValue : (bestCost - costU - modeSignaling - angleSignaling) * 512 - 256;
                long costV = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceV!, s.ChromaWidth, s.ChromaHeight, r, c, x, y, widthPixels, ptype: 1, LiveVCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability, out _, heightPixels: heightPixels, boundUnits512: vBound);
                if (costV == long.MaxValue)
                {
                    continue;
                }

                long cost = costU + costV + modeSignaling + angleSignaling;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;
                }
            }
        }

        return (bestMode, bestAngleDelta, bestCost);
    }

    /// <summary>
    /// Rectangular (HORZ/VERT) counterpart to <see cref="EstimateLumaCost(TileState, int, int, int)"/>: the real per-4x4-sub-block WHT
    /// trial cost (<see cref="ComputeRectLumaCostForMode"/>), now searched across the full
    /// <see cref="CandidateModes"/>/angle_delta set exactly like the square path, rather than a hardcoded
    /// DC_PRED-only candidate (see the project plan's "real-image gap" investigation: DC_PRED-only rectangular
    /// costing meant a Horz/Vert candidate could only ever win when DC_PRED genuinely was the region's best
    /// predictor, which real content data showed is the minority case -- aomenc committed ~50% of a real
    /// photo/graphic image's area as rectangular leaves against this project's own ~2.5%, precisely because of
    /// this gap). Lossless-only (see <see cref="RdPickPartition"/>'s Horz/Vert call site -- non-lossless
    /// never calls this).
    ///
    /// <para>Deliberately NOT ported here, unlike the square path's own <see cref="EstimateLumaCost(TileState, int, int, int)"/>: the
    /// HOG-based directional pruning (<see cref="ComputeLumaPruning"/>) and the SATD-based shortlist
    /// (<see cref="Av1IntraModelRdPruner"/>). A rectangular leaf is capped at 64x32/32x64 (at most 32
    /// sub-blocks, versus the square path's worst case of 1024 for a 128x128 superblock kept whole), so a
    /// plain exhaustive search over all ~61 mode/angle_delta combinations is cheap enough not to need pruning
    /// -- and, given this project's own stated priority of byte-parity/size over speed, exhaustive is strictly
    /// safer than risking a wrongly-pruned candidate the way the (reverted) SATD-only prescreen attempt
    /// elsewhere in this project's history already demonstrated can happen. A real follow-up could add pruning
    /// here purely for speed once this path's own correctness is well established.</para>
    ///
    /// <para>Real chroma *cost* is added separately by <see cref="EstimateRectLumaCostWithPalette"/> via
    /// <see cref="EstimateRectChromaCost"/> -- see that method's own remarks for why this call site
    /// specifically needed one even though <see cref="RdPickPartition"/>'s own None/Split candidates still
    /// don't at this same size (a real, measured regression found once this method's own luma search made
    /// Horz/Vert win far more often), and (project plan "lossless decision-phase real-cost parity" section,
    /// step 4) for why <see cref="EstimateRectChromaCost"/> now uses a real <see cref="SearchRectUvMode"/>-based
    /// chroma *mode* search too, not just DC_PRED-only cost.</para>
    ///
    /// <para><b>Tried and reverted (project plan "lossless decision-phase real-cost parity" section, step
    /// 1):</b> adding real y_mode/angle_delta signaling cost here too -- see
    /// <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s own identical "tried and reverted" remarks
    /// for the measured result and root-cause explanation, which apply here unchanged. (Exposing the winning
    /// candidate's own mode, tried again for step 4 below, is a different, confirmed-safe change -- see the
    /// <c>out int bestMode</c> overload's own remarks.)</para>
    /// </summary>
    private static long EstimateRectLumaCost(TileState s, int r, int c, int wMi, int hMi) => EstimateRectLumaCost(s, r, c, wMi, hMi, out _);

    /// <summary>
    /// <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>, additionally exposing the winning
    /// candidate's own final y_mode via <paramref name="bestMode"/> (spec's real *written* mode -- DcPred
    /// whenever filter_intra won, not just the plain-mode loop's own local winner) -- needed by
    /// <see cref="EstimateRectLumaCostWithPalette"/>'s own real chroma-cost call site (project plan "lossless
    /// decision-phase real-cost parity" section, step 4): <see cref="SearchRectUvMode"/>'s real <c>uv_mode</c>
    /// CDF is indexed by the luma mode that already won.
    /// </summary>
    private static long EstimateRectLumaCost(TileState s, int r, int c, int wMi, int hMi, out int bestMode)
    {
        int x = c * 4;
        int y = r * 4;
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        int bSize = BlockSizeFromWidthHeightMi(wMi, hMi);
        bool angleDeltaAllowed = RectAngleDeltaAllowed(bSize);
        long bestCost = long.MaxValue;
        int bestPlainMode = Av1IntraMode.DcPred;
        bestMode = Av1IntraMode.DcPred;

        // HOG-based directional-mode pruning + SATD/model-RD-based top-K pruning (project plan's own
        // architecture-unification follow-up to Step 6): real libaom's own handle_intra_mode
        // (intra_mode_search.c) applies both unconditionally to every intra candidate regardless of block
        // shape -- prune_intra_mode_with_hog/prune_intra_y_mode take a plain BLOCK_SIZE with no squareness
        // restriction at all -- so this rect path lacking them (its own prior "no HOG/SATD pruning,
        // exhaustive is cheap enough at this size" design) was a real, disclosed divergence from libaom's
        // actual behavior, not a neutral simplification. Both underlying primitives
        // (Av1IntraHogPruner.ComputeSkipMask, ComputeIntraModelRd) already took width/height independently;
        // only ComputeLumaPruning's own signature needed generalizing from a single sizePixels to match.
        Span<bool> directionalModeSkipMask = stackalloc bool[13];
        ComputeLumaPruning(s, s.Lossless, s.SourceY, s.YWidth, x, y, widthPixels, heightPixels, directionalModeSkipMask, out bool skipSmoothVh, out bool skipSmoothPlain);

        Span<long> topIntraModelRd = stackalloc long[4];
        int topModelCount = s.Lossless ? s.SpeedFeatures.TopIntraModelCountAllowed : 0;
        topIntraModelRd[..Math.Max(topModelCount, 0)].Fill(long.MaxValue);
        long bestModelRd = long.MaxValue;

        foreach (int mode in CandidateModes)
        {
            if (IsLumaModePruned(mode, directionalModeSkipMask, skipSmoothVh, skipSmoothPlain))
            {
                continue;
            }

            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                if (topModelCount > 0)
                {
                    long modelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, widthPixels, heightPixels, ptype: 0, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                    if (Av1IntraModelRdPruner.PruneIntraYMode(modelRd, ref bestModelRd, topIntraModelRd[..topModelCount]))
                    {
                        continue;
                    }
                }

                long cost = ComputeRectLumaCostForMode(s, r, c, wMi, hMi, mode, angleDelta, filterTypeSmooth: false, useRealBoundaryAvailability: false);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestPlainMode = mode;
                    bestMode = mode;
                }
            }
        }

        // FILTER_INTRA (spec §5.11.24) -- project plan's own "lossless decision-phase real-cost parity"
        // section, step 3, rectangular counterpart to the square path's own identical addition in
        // EstimateLumaCost (see that method's own remarks for the full rationale and why, unlike step 1,
        // this doesn't reintroduce the context-mismatch regression: neither signaling-cost CDF here is
        // neighbor-dependent). No *model-RD* pruning on this specific filter_intra sweep (only the plain-mode
        // loop above has it, matching EstimateLumaCost's own identical scope -- filter_intra's own real
        // libaom gate is PruneFilterIntraLevel's own submode mask instead, applied below). Eligibility is
        // real AV1's own max(bw, bh) <= 32 (not squareness), matching EncodeRectangularLeaf's own identical
        // real gate.
        bool skipFilterIntraEntirely = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 2;
        if (Math.Max(wMi, hMi) * 4 <= 32 && !skipFilterIntraEntirely)
        {
            int filterIntraModeMask = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 1 ? FilterIntraModeUsedFlag[bestPlainMode] : 0x1F;

            for (int filterMode = 0; filterMode < 5; filterMode++)
            {
                if ((filterIntraModeMask & (1 << filterMode)) == 0)
                {
                    continue;
                }

                long filterSignalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntra[bSize], 1) + Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntraMode, filterMode);

                ComputeRectLumaCostForModePrecise(s, r, c, wMi, hMi, mode: Av1IntraMode.DcPred, angleDelta: 0, filterTypeSmooth: false, useRealBoundaryAvailability: false, out long filterResidualUnits512, useFilterIntra: true, filterIntraMode: filterMode);

                long filterCost = (long)Math.Round((filterResidualUnits512 + filterSignalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (filterCost < bestCost)
                {
                    bestCost = filterCost;
                    bestMode = Av1IntraMode.DcPred;
                }
            }
        }

        return bestCost;
    }

    /// <summary>
    /// <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>, but also considering the rectangular palette candidate (mirroring
    /// how the square leaf-cost estimate in <see cref="RdPickPartition"/> already folds
    /// <c>EstimateLosslessPaletteCost</c> in against its own DC_PRED luma cost) -- without this,
    /// Horz/Vert always lost the RDO comparison against a square None/Split candidate on palette-heavy
    /// content, since only the square candidates had a palette alternative to fall back on (confirmed via
    /// direct libaom comparison: real AV1 gives every partition-type candidate, including HORZ/VERT, a full
    /// palette search on equal footing -- <c>av1_allow_palette</c> has no squareness restriction).
    /// </summary>
    private static long EstimateRectLumaCostWithPalette(TileState s, int r, int c, int wMi, int hMi)
    {
        long lumaCost = EstimateRectLumaCost(s, r, c, wMi, hMi, out int lumaMode);

        int bSize = BlockSizeFromWidthHeightMi(wMi, hMi);
        bool paletteStructurallyPresent = s.AllowScreenContentTools
            && Av1BlockTables.BlockWidth(bSize) <= 64
            && Av1BlockTables.BlockHeight(bSize) <= 64
            && bSize >= Av1BlockSize.Block8x8;

        // EncodeRectangularLeaf's own commit-time palette logic now uses the same real multi-strategy search
        // (SearchLosslessYPalette/SearchLosslessUvPalette) this estimate does, so there's no estimate/commit
        // approximate-vs-exact-only mismatch to guard against anymore.
        long? paletteCost = paletteStructurallyPresent
            ? EstimateLosslessPaletteCost(s, r, c, wMi, hMi, availU: r > 0, availL: c > 0)
            : null;

        // Real chroma cost (EstimateRectChromaCost's own remarks: added specifically here, once the real
        // luma mode search above made this candidate's own luma-only cost accurate enough to actually win
        // the None/Split comparison far more often -- a real, measured GraphicContentImage regression traced
        // to exactly this previously-negligible blind spot). Added only to the non-palette lumaCost, not to
        // paletteCost -- EstimateLosslessPaletteCost's own return value already covers Y+UV jointly (see its
        // own remarks), so adding this again there would double-count chroma whenever palette wins.
        if (!s.MonoChrome)
        {
            lumaCost += EstimateRectChromaCost(s, r, c, wMi, hMi, availU: r > 0, availL: c > 0, lumaMode);
        }

        if (paletteCost is long pc && pc < lumaCost)
        {
            lumaCost = pc;
        }

        return lumaCost;
    }

    /// <summary>
    /// Non-lossless-only chroma-cost counterpart to <see cref="EstimateLumaCost(TileState, int, int, int)"/>, folded into
    /// <see cref="RdPickPartition"/>'s leaf-cost estimate. Without this, the partition search only
    /// ever weighed luma's own signaling/residual savings from merging into a bigger leaf, completely blind
    /// to the real cost <see cref="EncodeLeaf"/>'s own forced-DC_PRED-chroma gate (<c>sizeMi &gt; 2</c>, see
    /// its remarks) imposes at 4:2:0 -- a real, measured regression (this project's benchmark image got
    /// larger, not smaller, once non-lossless partition/TX-size RDO first landed without this term). Lossless
    /// never needs this: its chroma always gets a real, unrestricted <see cref="SearchUvMode"/> search
    /// regardless of leaf size, so merging never costs it anything chroma-side.
    ///
    /// <para>Deliberately still just a single DC_PRED candidate whenever chromaN &gt; 1, even though
    /// <see cref="EncodeLeaf"/>'s own real chroma search (<see cref="SearchUvMode"/>) is no longer forced to
    /// DC_PRED there (now that <see cref="Av1ForwardTransform"/>'s forward ADST operators cover every chroma
    /// region size this encoder produces). Measured empirically: making this estimate run the same
    /// unrestricted mode/angle_delta sweep <see cref="SearchUvMode"/> does made the partition search
    /// systematically too optimistic about what merging into a bigger leaf would cost chroma-side --
    /// <see cref="SearchUvMode"/>'s real search picks whichever candidate wins by SSE-plus-rate against this
    /// leaf's own source pixels, but a mode that looks cheap in isolation here can still lose to what the
    /// actual encode achieves once real (not <see langword="false"/>-approximated) haveAboveRight/haveBelowLeft
    /// and real reconstructed neighbor context are available -- the mismatch measurably biased the search
    /// toward worse partitions (this project's benchmark image lost ~3 dB of PSNR at Quality=90 for no size
    /// win before this was reverted back to DC_PRED-only). A parent's leaf-vs-split comparison only needs this
    /// term to not systematically overstate what merging costs; DC_PRED is already a safe upper bound on that,
    /// since <see cref="SearchUvMode"/>'s real search can only ever do at least as well.</para>
    ///
    /// <para>Reads <see cref="TileState.SourceU"/>/<see cref="TileState.SourceV"/> for edge context, not
    /// <see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/> the way <see cref="SearchUvMode"/>'s real
    /// (post-commitment) search safely can -- mirroring <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s identical, already-
    /// proven choice and for the identical reason: during <see cref="RdPickPartition"/>'s speculative
    /// recursion, no sibling's real reconstruction exists yet to read.</para>
    /// </summary>
    private static long EstimateChromaCost(TileState s, int r, int c, int sizeMi)
    {
        if (s.MonoChrome)
        {
            return 0;
        }

        int chromaN = sizeMi / 2;
        int chromaSizePixels = chromaN * 4;
        int cx = (c * 4) / 2;
        int cy = (r * 4) / 2;
        int log2Size = Log2FromPixels(chromaSizePixels);
        bool availU = r > 0;
        bool availL = c > 0;

        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;

        long CandidateCost(int mode, int angleDelta)
        {
            Av1IntraPrediction.BuildEdges(above, left, s.SourceU!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
            long cost = ComputeCandidateCost(s, s.SourceU!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, LiveUCoeffCtx(s));

            Av1IntraPrediction.BuildEdges(above, left, s.SourceV!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight: false, haveBelowLeft: false, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth: false, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
            cost += ComputeCandidateCost(s, s.SourceV!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, LiveVCoeffCtx(s));
            return cost;
        }

        if (chromaN > 1)
        {
            return CandidateCost(Av1IntraMode.DcPred, 0);
        }

        long bestCost = long.MaxValue;
        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode);
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                long cost = CandidateCost(mode, angleDelta);
                if (cost < bestCost)
                {
                    bestCost = cost;
                }
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Mutable running-best state threaded through <see cref="PaletteRdY"/>/<see cref="PaletteRdUv"/>'s many
    /// candidate trials -- mirrors libaom's own <c>*best_rd</c>/<c>*best_mbmi</c>/<c>*best_palette_color_map</c>
    /// pointer-threading (<c>av1_rd_pick_palette_intra_sby</c>/<c>_sbuv</c>, <c>av1/encoder/palette.c</c>)
    /// without needing C's own multi-output-pointer-parameter style. <see cref="Rd"/> starts at the caller's
    /// own non-palette baseline and only ever shrinks, so <see cref="HasResult"/> becoming
    /// <see langword="true"/> is equivalent to "some real candidate beat that baseline" -- there is no
    /// distinct "found something, but it didn't win" state, matching libaom's own <c>beat_best_rd</c>
    /// semantics exactly.
    /// </summary>
    private sealed class PaletteTrialState
    {
        public long Rd;
        public bool HasResult;
        public int N;
        public bool AllZeroResidual;
    }

    /// <summary><c>start_n_lookup_table</c> (<c>av1/encoder/palette.c:643-645</c>), indexed by <c>max_n = min(colors, 8)</c>.</summary>
    private static readonly int[] PaletteStartNLookup = [0, 0, 0, 3, 3, 2, 3, 3, 2];

    /// <summary><c>step_size_lookup_table</c> (<c>av1/encoder/palette.c:646-648</c>), indexed the same way as <see cref="PaletteStartNLookup"/>.</summary>
    private static readonly int[] PaletteStepSizeLookup = [0, 0, 0, 3, 3, 3, 3, 3, 3];

    /// <summary><c>is_iter_over</c> (<c>palette.c:328-331</c>): whether a jump-search loop stepping by <paramref name="step"/> has reached or passed <paramref name="endExclusive"/>.</summary>
    private static bool IsIterOver(int n, int endExclusive, int step) => step > 0 ? n >= endExclusive : n <= endExclusive;

    /// <summary>
    /// <c>set_stage2_params</c> (<c>palette.c:432-446</c>): after a coarse jump-search pass finds
    /// <paramref name="winner"/> the best size in <c>[PaletteMinSize, endNInclusive]</c>, this narrows the
    /// range to its immediate, still-unexplored neighbors (<c>winner-1</c>/<c>winner+1</c>, clamped at either
    /// end of the coarse range) for a cheap refinement pass.
    /// </summary>
    private static void SetStage2Params(int winner, int endNInclusive, out int minN, out int maxN, out int step)
    {
        minN = winner == Av1PaletteSearch.PaletteMinSize ? Av1PaletteSearch.PaletteMinSize + 1 : Math.Max(winner - 1, Av1PaletteSearch.PaletteMinSize);
        maxN = winner == endNInclusive ? winner - 1 : Math.Min(winner + 1, Av1PaletteSearch.PaletteMaxSize);
        step = Math.Max(1, maxN - minN);
    }

    /// <summary>
    /// <c>palette_rd_y</c> (<c>av1/encoder/palette.c:229-324</c>): evaluates one luma palette candidate
    /// (<paramref name="candidateColors"/>, before deduplication) for real -- deduplicates, prices the real
    /// header cost (palette color values via the existing <see cref="EstimatePaletteColorBitsY"/>, size via
    /// <c>PaletteYSize</c>, the has_palette_y=1 bit via <c>PaletteYMode</c>, and the real, locally-adapted
    /// color-map entropy cost via the existing <see cref="EstimateColorMapBits"/>), and -- unless
    /// <paramref name="doHeaderGating"/>'s own zero-distortion lower bound already rules this candidate out
    /// against <paramref name="state"/>'s current best (libaom's own <c>do_header_rd_based_breakout</c>,
    /// gated by <see cref="Av1SpeedFeatures.PruneLumaPaletteSizeSearchLevel"/>) -- prices the real residual
    /// via the existing <see cref="ComputePaletteResidualCost"/> and updates <paramref name="state"/> (and
    /// commits the winning colors/map into <see cref="TileState.PaletteColorsY"/>/<see cref="TileState.PaletteColorMap"/>)
    /// on improvement. Returns <see langword="true"/> when the header-cost gate fired (the caller's own sweep
    /// must abort the rest of this search direction, matching libaom's own <c>last_n_searched = end_n; break;</c>).
    ///
    /// <para>Deliberately does not port libaom's own <c>optimize_palette_colors</c> (color-cache snapping) --
    /// see <see cref="Av1PaletteSearch"/>'s own remarks. Deliberately does not add the y_mode=DC_PRED
    /// signaling cost either -- real libaom's own <c>intra_mode_info_cost_y</c> does include it (as
    /// <c>mode_cost</c>), but it is the exact same value added to every candidate <em>and</em> already baked
    /// into <paramref name="state"/>'s own incoming seed (the caller's real DC_PRED <c>bestCost</c>) via the
    /// same convention -- so omitting it from every candidate here is equivalent to including it everywhere,
    /// without needing this method to re-derive a value the caller already computed.</para>
    /// </summary>
    private static bool PaletteRdY(TileState s, int x, int y, int w, int h, int r, int c, bool availU, bool availL, int bsizeCtx, int paletteModeCtx, ReadOnlySpan<int> candidateColors, int candN, bool doHeaderGating, int headerRdShift, PaletteTrialState state)
    {
        var trialColors = s.PaletteTrialColorsY;
        candidateColors[..candN].CopyTo(trialColors);

        Span<int> cache = stackalloc int[16];
        int nCache = GetPaletteCacheColors(s, 0, r, c, availU, availL, cache);
        Av1PaletteSearch.OptimizePaletteColors(cache[..nCache], nCache, trialColors.AsSpan(0, candN), candN);

        int numUnique = Av1PaletteSearch.RemoveDuplicates(trialColors.AsSpan(0, candN), candN);
        if (numUnique < Av1PaletteSearch.PaletteMinSize)
        {
            return false;
        }

        long colorBits = EstimatePaletteColorBitsY(s, trialColors, numUnique, r, c, availU, availL);
        long sizeCost = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.PaletteYSize[bsizeCtx], numUnique - 2);
        long modeCostOne = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.PaletteYMode[bsizeCtx][paletteModeCtx], 1);

        var trialMap = s.PaletteTrialColorMap;
        int nPixels = w * h;
        Av1PaletteSearch.CalcIndices1D(s.PaletteKMeansDataY.AsSpan(0, nPixels), trialColors.AsSpan(0, numUnique), trialMap.AsSpan(0, nPixels), nPixels, numUnique);
        long mapBits = EstimateColorMapBits(trialMap, w, h, numUnique, s.CostCdf.PaletteYColorIndex);
        long headerBits = colorBits + sizeCost + modeCostOne + mapBits;

        if (doHeaderGating)
        {
            long headerRd = Av1RdCost.CombineCost(0, headerBits, 1.0);
            if ((headerRd >> headerRdShift) > state.Rd)
            {
                return true;
            }
        }

        long residualBits = ComputePaletteResidualCost(s, s.SourceY, s.YWidth, ptype: 0, x, y, w, h, trialMap, w, trialColors, LiveYCoeffCtx(s), out bool allZero);
        long totalBits = headerBits + (allZero ? 0 : residualBits);
        long totalRd = Av1RdCost.CombineCost(0, totalBits, 1.0);

        if (totalRd < state.Rd)
        {
            state.Rd = totalRd;
            state.HasResult = true;
            state.N = numUnique;
            state.AllZeroResidual = allZero;
            trialColors.AsSpan(0, numUnique).CopyTo(s.PaletteColorsY);
            trialMap.AsSpan(0, nPixels).CopyTo(s.PaletteColorMap);
        }

        return false;
    }

    /// <summary>
    /// <c>perform_top_color_palette_search</c> (<c>palette.c:340-376</c>): evaluates <see cref="PaletteRdY"/>
    /// for a size range <c>[startN, endNExclusive)</c> stepping by <paramref name="stepSize"/> (negative for
    /// a descending sweep), using successive prefixes of <see cref="TileState.PaletteTopColors"/> as each
    /// candidate's raw (pre-dedup) color set. Returns the best size found (or <paramref name="endNExclusive"/>
    /// itself as a "no winner" sentinel, matching libaom's own <c>top_color_winner = end_n</c> initial value).
    /// </summary>
    private static int PerformTopColorPaletteSearch(TileState s, int x, int y, int w, int h, int r, int c, bool availU, bool availL, int bsizeCtx, int paletteModeCtx, int startN, int endNExclusive, int stepSize, bool doHeaderGating, int headerRdShift, PaletteTrialState state, out int lastNSearched)
    {
        int n = startN;
        int winner = endNExclusive;
        lastNSearched = startN;

        while (!IsIterOver(n, endNExclusive, stepSize))
        {
            long rdBefore = state.Rd;
            bool breakout = PaletteRdY(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, s.PaletteTopColors.AsSpan(0, n), n, doHeaderGating, headerRdShift, state);
            lastNSearched = n;
            if (breakout)
            {
                lastNSearched = endNExclusive;
                break;
            }

            if (state.Rd < rdBefore)
            {
                winner = n;
            }
            else if (s.SpeedFeatures.PrunePaletteSearchLevel == 2)
            {
                return winner;
            }

            n += stepSize;
        }

        return winner;
    }

    /// <summary>
    /// <c>perform_k_means_palette_search</c> (<c>palette.c:383-429</c>): the k-means counterpart to
    /// <see cref="PerformTopColorPaletteSearch"/> -- re-seeds fresh, range-bisected centroids
    /// (<c>lowerBound</c>/<c>upperBound</c>, matching libaom's own per-candidate re-seed exactly, never
    /// reusing a previous size's converged centroids) and runs <see cref="Av1PaletteSearch.KMeans1D"/> before
    /// evaluating each size.
    /// </summary>
    private static int PerformKMeansPaletteSearch(TileState s, int x, int y, int w, int h, int r, int c, bool availU, bool availL, int bsizeCtx, int paletteModeCtx, int lowerBound, int upperBound, int startN, int endNExclusive, int stepSize, bool doHeaderGating, int headerRdShift, PaletteTrialState state, out int lastNSearched)
    {
        const int maxItr = 50;
        int n = startN;
        int winner = endNExclusive;
        lastNSearched = startN;
        int nPixels = w * h;
        var data = s.PaletteKMeansDataY.AsSpan(0, nPixels);
        Span<int> centroids = stackalloc int[Av1PaletteSearch.PaletteMaxSize];
        Span<int> kmIndices = stackalloc int[Av1PaletteSearch.MaxPaletteBlockPixels];

        while (!IsIterOver(n, endNExclusive, stepSize))
        {
            for (int i = 0; i < n; i++)
            {
                centroids[i] = lowerBound + (((2 * i) + 1) * (upperBound - lowerBound) / n / 2);
            }

            Av1PaletteSearch.KMeans1D(data, centroids[..n], kmIndices[..nPixels], nPixels, n, maxItr);

            long rdBefore = state.Rd;
            bool breakout = PaletteRdY(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, centroids[..n], n, doHeaderGating, headerRdShift, state);
            lastNSearched = n;
            if (breakout)
            {
                lastNSearched = endNExclusive;
                break;
            }

            if (state.Rd < rdBefore)
            {
                winner = n;
            }
            else if (s.SpeedFeatures.PrunePaletteSearchLevel == 2)
            {
                return winner;
            }

            n += stepSize;
        }

        return winner;
    }

    /// <summary>
    /// Real, multi-strategy port of libaom's <c>av1_rd_pick_palette_intra_sby</c> (<c>av1/encoder/palette.c</c>,
    /// read directly from the local checkout at <c>C:\Sources\GoogleSource\aom</c> -- see
    /// <c>THIRD-PARTY-LICENSES.md</c> for the full attribution): searches both a top-color-frequency strategy
    /// (<see cref="Av1PaletteSearch.FindTopColors"/>) and a k-means strategy (<see cref="Av1PaletteSearch.KMeans1D"/>)
    /// across a real palette-size sweep, gated by <see cref="Av1SpeedFeatures.PrunePaletteSearchLevel"/> (0:
    /// full ascending-then-descending sweep; 1: a jump-search coarse pass per libaom's own
    /// <see cref="PaletteStartNLookup"/>/<see cref="PaletteStepSizeLookup"/>, plus a +-1 refine of whichever
    /// size wins; 2: a greedy hill-climb that stops the moment a size fails to improve) and
    /// <see cref="Av1SpeedFeatures.PruneLumaPaletteSizeSearchLevel"/> (a real, zero-distortion header-cost
    /// lower bound that -- when it alone can't beat the running best -- skips that size's real residual
    /// evaluation and aborts the rest of that sweep direction).
    ///
    /// <para>Returns <see langword="false"/> (leaving <paramref name="bestRdSoFar"/>'s own candidate the
    /// winner) when no palette candidate beats it; otherwise <see cref="TileState.PaletteColorsY"/>/
    /// <see cref="TileState.PaletteColorMap"/> already hold the winning candidate (built as a side effect of
    /// finding it -- no separate re-build call is needed at the caller's own commit site).</para>
    /// </summary>
    private static bool SearchLosslessYPalette(TileState s, int x, int y, int w, int h, long bestRdSoFar, int r, int c, bool availU, bool availL, out int n, out bool allZeroResidual, out long cost)
    {
        n = 0;
        allZeroResidual = false;
        cost = 0;

        int colors = Av1PaletteSearch.CountColors(s.SourceY, s.YWidth, x, y, w, h, s.PaletteCountBuf);
        if (colors <= 1 || colors > s.SpeedFeatures.ColorPaletteThresh)
        {
            return false;
        }

        var data = s.PaletteKMeansDataY;
        int lowerBound = s.SourceY[(y * s.YWidth) + x];
        int upperBound = lowerBound;
        for (int i = 0; i < h; i++)
        {
            int rowBase = ((y + i) * s.YWidth) + x;
            int outBase = i * w;
            for (int j = 0; j < w; j++)
            {
                int v = s.SourceY[rowBase + j];
                data[outBase + j] = v;
                if (v < lowerBound)
                {
                    lowerBound = v;
                }

                if (v > upperBound)
                {
                    upperBound = v;
                }
            }
        }

        int maxN = Math.Min(colors, Av1PaletteSearch.PaletteMaxSize);
        Av1PaletteSearch.FindTopColors(s.PaletteCountBuf, maxN, s.PaletteTopColors);

        int bSize = BlockSizeFromWidthHeightMi(w / 4, h / 4);
        int bsizeCtx = GetPaletteBsizeCtx(bSize);
        int paletteModeCtx = GetPaletteModeCtx(s, r, c, availU, availL);

        var state = new PaletteTrialState { Rd = bestRdSoFar };
        bool doHeaderGating = s.SpeedFeatures.PruneLumaPaletteSizeSearchLevel != 0;
        int headerRdShift = s.SpeedFeatures.PruneLumaPaletteSizeSearchLevel == 1 ? 1 : 0;

        if (s.SpeedFeatures.PrunePaletteSearchLevel == 1 && colors > Av1PaletteSearch.PaletteMinSize)
        {
            int minN = PaletteStartNLookup[maxN];
            int step = PaletteStepSizeLookup[maxN];

            int topWinner = PerformTopColorPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, minN, maxN + 1, step, doHeaderGating, headerRdShift, state, out _);
            if (topWinner <= maxN)
            {
                SetStage2Params(topWinner, maxN, out int s2min, out int s2max, out int s2step);
                PerformTopColorPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, s2min, s2max + 1, s2step, doHeaderGating: false, headerRdShift: 0, state, out _);
            }

            int kMeansWinner = PerformKMeansPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, lowerBound, upperBound, minN, maxN + 1, step, doHeaderGating, headerRdShift, state, out _);
            if (kMeansWinner <= maxN)
            {
                SetStage2Params(kMeansWinner, maxN, out int s2min, out int s2max, out int s2step);
                PerformKMeansPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, lowerBound, upperBound, s2min, s2max + 1, s2step, doHeaderGating: false, headerRdShift: 0, state, out _);
            }
        }
        else
        {
            int minN = Av1PaletteSearch.PaletteMinSize;

            PerformTopColorPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, minN, maxN + 1, 1, doHeaderGating, headerRdShift, state, out int lastNSearched);
            if (lastNSearched < maxN)
            {
                PerformTopColorPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, maxN, lastNSearched, -1, doHeaderGating: false, headerRdShift: 0, state, out _);
            }

            if (colors == Av1PaletteSearch.PaletteMinSize)
            {
                // "Special case: These colors automatically become the centroids." (palette.c:718-725) --
                // always evaluated with header-gating off, matching libaom's own literal `/*do_header_rd_
                // based_gating=*/false` at this specific call site regardless of PruneLumaPaletteSizeSearchLevel.
                Span<int> centroids = [lowerBound, upperBound];
                PaletteRdY(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, centroids, 2, doHeaderGating: false, headerRdShift: 0, state);
            }
            else
            {
                PerformKMeansPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, lowerBound, upperBound, minN, maxN + 1, 1, doHeaderGating, headerRdShift, state, out lastNSearched);
                if (lastNSearched < maxN)
                {
                    PerformKMeansPaletteSearch(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteModeCtx, lowerBound, upperBound, maxN, lastNSearched, -1, doHeaderGating: false, headerRdShift: 0, state, out _);
                }
            }
        }

        if (!state.HasResult)
        {
            return false;
        }

        n = state.N;
        allZeroResidual = state.AllZeroResidual;
        cost = state.Rd;
        return true;
    }

    /// <summary>
    /// <c>optimize_palette_colors</c>-adjacent canonicalization step from <c>av1_rd_pick_palette_intra_sbuv</c>
    /// (<c>palette.c:872-885</c>): re-sorts a chroma candidate's (U, V) centroid pairs ascending by U,
    /// dragging V along -- a pure label relabeling (which pixels get merged is unchanged, only which integer
    /// index each merged group is called), needed so the subsequent color-map recomputation
    /// (<see cref="Av1PaletteSearch.CalcIndices2D"/>) uses the same canonical label order real libaom does.
    /// A plain selection sort, matching the reference's own <c>O(n^2)</c> loop shape exactly (n &lt;= 8).
    /// </summary>
    private static void SortChromaCentroidsByU(Span<int> centroidU, Span<int> centroidV)
    {
        int n = centroidU.Length;
        for (int i = 0; i < n - 1; i++)
        {
            int minIdx = i;
            int minVal = centroidU[i];
            for (int j = i + 1; j < n; j++)
            {
                if (centroidU[j] < minVal)
                {
                    minVal = centroidU[j];
                    minIdx = j;
                }
            }

            if (minIdx != i)
            {
                (centroidU[i], centroidU[minIdx]) = (centroidU[minIdx], centroidU[i]);
                (centroidV[i], centroidV[minIdx]) = (centroidV[minIdx], centroidV[i]);
            }
        }
    }

    /// <summary>
    /// Chroma counterpart to <see cref="PaletteRdY"/>, mirroring the per-size loop body inside
    /// <c>av1_rd_pick_palette_intra_sbuv</c> (<c>palette.c:861-928</c>) -- unlike luma, chroma never
    /// deduplicates centroids after k-means (a real asymmetry in libaom's own source, not a simplification:
    /// confirmed by direct reading, <c>remove_duplicates</c> is never called on this path), and its own
    /// early-termination gate (<see cref="Av1SpeedFeatures.EarlyTermChromaPaletteSizeSearch"/>, unconditionally
    /// true at every effort level) uses a non-strict <c>&gt;=</c> comparison with no relaxed-shift variant,
    /// unlike luma's own two-level shift. Returns <see langword="true"/> when the early-term gate fired (the
    /// caller's single ascending sweep must stop entirely -- chroma has no separate descending pass).
    /// </summary>
    private static bool PaletteRdUv(TileState s, int x, int y, int w, int h, int r, int c, bool availU, bool availL, int bsizeCtx, int paletteUvModeCtx, ReadOnlySpan<int> centroidU, ReadOnlySpan<int> centroidV, int nn, bool earlyTerm, PaletteTrialState state)
    {
        var trialU = s.PaletteTrialColorsU;
        var trialV = s.PaletteTrialColorsV;
        centroidU.CopyTo(trialU);
        centroidV.CopyTo(trialV);

        long colorBits = EstimatePaletteColorBitsUv(s, trialU, trialV, nn, r, c, availU, availL);
        long sizeCost = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.PaletteUvSize[bsizeCtx], nn - 2);
        long modeCostOne = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.PaletteUvMode[paletteUvModeCtx], 1);
        long mapBits = EstimateColorMapBits(s.PaletteTrialColorMap, w, h, nn, s.CostCdf.PaletteUvColorIndex);
        long headerBits = colorBits + sizeCost + modeCostOne + mapBits;

        if (earlyTerm)
        {
            long headerRd = Av1RdCost.CombineCost(0, headerBits, 1.0);
            if (headerRd >= state.Rd)
            {
                return true;
            }
        }

        long residualBitsU = ComputePaletteResidualCost(s, s.SourceU!, s.ChromaWidth, ptype: 1, x, y, w, h, s.PaletteTrialColorMap, w, trialU, LiveUCoeffCtx(s), out bool allZeroU);
        long residualBitsV = ComputePaletteResidualCost(s, s.SourceV!, s.ChromaWidth, ptype: 1, x, y, w, h, s.PaletteTrialColorMap, w, trialV, LiveVCoeffCtx(s), out bool allZeroV);
        long totalBits = headerBits + (allZeroU ? 0 : residualBitsU) + (allZeroV ? 0 : residualBitsV);
        long totalRd = Av1RdCost.CombineCost(0, totalBits, 1.0);

        if (totalRd < state.Rd)
        {
            state.Rd = totalRd;
            state.HasResult = true;
            state.N = nn;
            state.AllZeroResidual = allZeroU && allZeroV;
            trialU.AsSpan(0, nn).CopyTo(s.PaletteColorsU);
            trialV.AsSpan(0, nn).CopyTo(s.PaletteColorsV);
            s.PaletteTrialColorMap.AsSpan(0, w * h).CopyTo(s.PaletteColorMapUv);
        }

        return false;
    }

    /// <summary>
    /// Real port of libaom's <c>av1_rd_pick_palette_intra_sbuv</c> (<c>av1/encoder/palette.c:762-936</c>):
    /// unlike luma, chroma has no top-color strategy at all (confirmed by direct source reading) -- just a
    /// single, plain ascending k-means size sweep (2D, jointly clustering (U, V) pairs), broken early by
    /// <see cref="Av1SpeedFeatures.EarlyTermChromaPaletteSizeSearch"/> the moment a size's own real header
    /// cost alone can no longer beat the running best. <paramref name="usedYPalette"/> selects which
    /// <c>PaletteUvMode</c> context row applies (spec's own <c>palette_uv_mode</c> CDF is conditioned on
    /// whether luma used palette) -- the one real coupling between the two searches; the actual chroma
    /// candidate search itself is otherwise fully independent of luma's own outcome, matching libaom exactly.
    ///
    /// <para>Returns <see langword="false"/> when no candidate beats <paramref name="bestRdSoFar"/>;
    /// otherwise <see cref="TileState.PaletteColorsU"/>/<see cref="TileState.PaletteColorsV"/>/
    /// <see cref="TileState.PaletteColorMapUv"/> already hold the winner.</para>
    ///
    /// <para><paramref name="bsizeCtx"/> (project plan Phase 4's own real fix): <c>PaletteUvSize</c>'s real
    /// CDF context (<c>PALETTE_BLOCK_SIZE_CONTEXTS</c>, spec's own <c>has_palette_uv</c> eligibility/context)
    /// is keyed on the CODING BLOCK's own (luma) size, not the chroma plane's own, separately-subsampled
    /// pixel dimensions -- this method's own <paramref name="w"/>/<paramref name="h"/> are real CHROMA-plane
    /// pixel dimensions (needed for <see cref="Av1PaletteSearch.CountColors"/>/k-means/residual-cost calls
    /// against <see cref="TileState.SourceU"/>/<see cref="TileState.SourceV"/>), which only happen to equal
    /// the luma block size too under lossless's own Chroma444-always invariant -- a real, confirmed bug
    /// (crashed inside <see cref="PaletteRdUv"/>'s own <c>PaletteUvSize</c> index) once a genuinely
    /// 4:2:0-subsampled non-lossless leaf reached this method for the first time, since one parameter can't
    /// correctly serve both a chroma-pixel-size role and a luma-block-size-context role once the two
    /// diverge. The caller must compute this the same way <see cref="SearchLosslessYPalette"/>'s own
    /// (correct-by-construction, luma-only) internal <c>bsizeCtx</c> already does -- from the real coding
    /// block's own mi size, not from this method's own chroma-scaled <paramref name="w"/>/<paramref name="h"/>.
    /// </para>
    /// </summary>
    private static bool SearchLosslessUvPalette(TileState s, int x, int y, int w, int h, long bestRdSoFar, int r, int c, bool availU, bool availL, bool usedYPalette, int bsizeCtx, out int n, out bool allZeroResidual, out long cost)
    {
        n = 0;
        allZeroResidual = false;
        cost = 0;

        int colorsU = Av1PaletteSearch.CountColors(s.SourceU!, s.ChromaWidth, x, y, w, h, s.PaletteCountBuf);
        int colorsV = Av1PaletteSearch.CountColors(s.SourceV!, s.ChromaWidth, x, y, w, h, s.PaletteCountBuf);
        int colorsThreshold = Math.Max(colorsU, colorsV);
        if (colorsThreshold <= 1 || colorsThreshold > s.SpeedFeatures.ColorPaletteThresh)
        {
            return false;
        }

        var dataU = s.PaletteKMeansDataU;
        var dataV = s.PaletteKMeansDataV;
        int lbU = s.SourceU![(y * s.ChromaWidth) + x];
        int ubU = lbU;
        int lbV = s.SourceV![(y * s.ChromaWidth) + x];
        int ubV = lbV;
        for (int i = 0; i < h; i++)
        {
            int rowBase = ((y + i) * s.ChromaWidth) + x;
            int outBase = i * w;
            for (int j = 0; j < w; j++)
            {
                int uu = s.SourceU[rowBase + j];
                int vv = s.SourceV[rowBase + j];
                dataU[outBase + j] = uu;
                dataV[outBase + j] = vv;
                if (uu < lbU)
                {
                    lbU = uu;
                }

                if (uu > ubU)
                {
                    ubU = uu;
                }

                if (vv < lbV)
                {
                    lbV = vv;
                }

                if (vv > ubV)
                {
                    ubV = vv;
                }
            }
        }

        int colors = colorsU > colorsV ? colorsU : colorsV;
        int maxN = Math.Min(colors, Av1PaletteSearch.PaletteMaxSize);

        int paletteUvModeCtx = usedYPalette ? 1 : 0;

        var state = new PaletteTrialState { Rd = bestRdSoFar };
        bool earlyTerm = s.SpeedFeatures.EarlyTermChromaPaletteSizeSearch;

        const int maxItr = 50;
        int nPixels = w * h;
        Span<int> centroidU = stackalloc int[Av1PaletteSearch.PaletteMaxSize];
        Span<int> centroidV = stackalloc int[Av1PaletteSearch.PaletteMaxSize];
        Span<int> kmIndices = stackalloc int[Av1PaletteSearch.MaxPaletteBlockPixels];
        Span<int> chromaCache = stackalloc int[16];
        int nChromaCache = GetPaletteCacheColors(s, 1, r, c, availU, availL, chromaCache);

        for (int nn = Av1PaletteSearch.PaletteMinSize; nn <= maxN; nn++)
        {
            for (int i = 0; i < nn; i++)
            {
                centroidU[i] = lbU + (((2 * i) + 1) * (ubU - lbU) / nn / 2);
                centroidV[i] = lbV + (((2 * i) + 1) * (ubV - lbV) / nn / 2);
            }

            Av1PaletteSearch.KMeans2D(dataU.AsSpan(0, nPixels), dataV.AsSpan(0, nPixels), centroidU[..nn], centroidV[..nn], kmIndices[..nPixels], nPixels, nn, maxItr);

            // optimize_palette_colors (palette.c:872): only ever snaps U -- confirmed by direct source
            // reading that V is never cached at all (Av1TileDecoder.ReadPaletteColorsUv's own remarks).
            Av1PaletteSearch.OptimizePaletteColors(chromaCache[..nChromaCache], nChromaCache, centroidU[..nn], nn);

            SortChromaCentroidsByU(centroidU[..nn], centroidV[..nn]);
            Av1PaletteSearch.CalcIndices2D(dataU.AsSpan(0, nPixels), dataV.AsSpan(0, nPixels), centroidU[..nn], centroidV[..nn], s.PaletteTrialColorMap.AsSpan(0, nPixels), nPixels, nn);

            if (PaletteRdUv(s, x, y, w, h, r, c, availU, availL, bsizeCtx, paletteUvModeCtx, centroidU[..nn], centroidV[..nn], nn, earlyTerm, state))
            {
                break;
            }
        }

        if (!state.HasResult)
        {
            return false;
        }

        n = state.N;
        allZeroResidual = state.AllZeroResidual;
        cost = state.Rd;
        return true;
    }

    /// <summary>
    /// Combined Y+UV real palette cost for one lossless leaf (Y always, plus UV when <c>!s.MonoChrome</c>),
    /// or <see langword="null"/> when neither plane's real search (<see cref="SearchLosslessYPalette"/>/
    /// <see cref="SearchLosslessUvPalette"/>, called with an unbounded baseline so they report the best
    /// candidate found regardless of whether it would beat any particular non-palette alternative) can
    /// construct a valid (&gt;= 2 colors, &lt;= <see cref="Av1SpeedFeatures.ColorPaletteThresh"/> distinct
    /// source colors) candidate. Feeds <see cref="RdPickPartition"/>'s leaf-vs-split comparison -- see
    /// that method's own remarks for why a bigger leaf can lose palette eligibility a smaller one wouldn't
    /// have, and why this estimate needs to see that coming.
    /// </summary>
    private static long? EstimateLosslessPaletteCost(TileState s, int r, int c, int sizeMi, bool availU, bool availL)
        => EstimateLosslessPaletteCost(s, r, c, sizeMi, sizeMi, availU, availL);

    /// <summary>
    /// Rectangular-generalized (wMi != hMi) form of the same estimate, used by
    /// <see cref="RdPickPartition"/>'s Horz/Vert candidates so those leaves get a palette
    /// alternative on the same footing as None/Split -- see <see cref="EstimateRectLumaCost(TileState, int, int, int, int)"/>'s own
    /// remarks for why Horz/Vert previously lost this comparison unfairly (no palette candidate at all)
    /// on palette-heavy content.
    /// </summary>
    private static long? EstimateLosslessPaletteCost(TileState s, int r, int c, int wMi, int hMi, bool availU, bool availL)
    {
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        int x = c * 4;
        int y = r * 4;

        if (!SearchLosslessYPalette(s, x, y, widthPixels, heightPixels, long.MaxValue, r, c, availU, availL, out _, out _, out long yCost))
        {
            return null;
        }

        long bits = yCost;
        if (!s.MonoChrome)
        {
            // SearchLosslessUvPalette indexes s.SourceU/V directly at (x, y)/(w, h) against s.ChromaWidth's
            // own stride -- real chroma-plane coordinates/size, not luma's (see that function's own remarks
            // for the real bug this fixes: passing luma-scale values here was only ever correct because
            // this estimate's only exercised caller was lossless content, always Chroma444). This estimate
            // itself is gated only on !s.MonoChrome, not Chroma444, so it was already live (and already
            // wrong) for non-lossless 4:2:0 content before this fix -- an independent, real correctness gap,
            // not just a prerequisite for enabling palette's own commit-time use for non-lossless.
            int chromaX = s.Chroma444 ? x : x / 2;
            int chromaY = s.Chroma444 ? y : y / 2;
            int chromaWidthPixels = s.Chroma444 ? widthPixels : widthPixels / 2;
            int chromaHeightPixels = s.Chroma444 ? heightPixels : heightPixels / 2;

            // bsizeCtx: real luma-block-size context (PaletteUvSize's own real CDF key), not derived from
            // the chroma-scaled dimensions above -- see SearchLosslessUvPalette's own remarks.
            int uvBsizeCtx = GetPaletteBsizeCtx(BlockSizeFromWidthHeightMi(wMi, hMi));
            if (!SearchLosslessUvPalette(s, chromaX, chromaY, chromaWidthPixels, chromaHeightPixels, long.MaxValue, r, c, availU, availL, usedYPalette: true, uvBsizeCtx, out _, out _, out long uvCost))
            {
                return null;
            }

            bits += uvCost;
        }

        return bits;
    }

    /// <summary>
    /// Real per-4x4-sub-block WHT residual trial cost of an approximate palette prediction: for each pixel,
    /// "prediction" is simply <c>colors[colorMap[localIdx]]</c> -- no spatial edge-based extrapolation at all
    /// (unlike every other intra mode candidate this class costs, a palette lookup already <em>is</em> the
    /// whole prediction) -- so this is simpler than <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s
    /// own per-sub-block loop: no BuildEdges/Predict/haveAboveRight/haveBelowLeft, just
    /// residual = source - palette lookup, WHT-transformed/quantized/trial-written exactly like any other
    /// candidate's residual (same non-mutating trial-sink/scratch-context pattern <c>ComputeLosslessWholeLeafCostPerSubBlock</c>'s
    /// own remarks describe). <paramref name="allZero"/> comes back <see langword="true"/> when every pixel
    /// happens to match its assigned centroid exactly -- callers should treat that leaf as free (skip = 1)
    /// rather than paying for a real (here, coincidentally zero) residual.
    /// </summary>
    private static long ComputePaletteResidualCost(TileState s, int[] source, int planeWidth, int ptype, int x, int y, int widthPixels, int heightPixels, int[] colorMap, int mapWidth, int[] colors, Av1CoefficientWriter.PlaneContext realCtx, out bool allZero)
    {
        int nW = widthPixels / 4;
        int nH = heightPixels / 4;
        int x4 = x >> 2;
        int y4 = y >> 2;
        var scratch = s.ScratchCoeffCtx;
        scratch.SeedFrom(realCtx, x4, nW, y4, nH);
        var trial = s.TrialSink;
        trial.Reset();

        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;
        bool anyNonZero = false;
        int edgeMaxX = ptype == 0 ? s.EdgeMaxX : s.ChromaEdgeMaxX;
        int edgeMaxY = ptype == 0 ? s.EdgeMaxY : s.ChromaEdgeMaxY;

        for (int dr = 0; dr < nH; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                // A skipped sub-block correctly never contributes to allZero either way, matching a real
                // decoder never coding it at all.
                if (subX > edgeMaxX || subY > edgeMaxY)
                {
                    continue;
                }

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * planeWidth) + subX;
                    int mapRowBase = (((dr * 4) + i) * mapWidth) + (dc * 4);
                    for (int j = 0; j < 4; j++)
                    {
                        int predicted = colors[colorMap[mapRowBase + j]];
                        int diff = source[rowBase + j] - predicted;
                        residual[(i * 4) + j] = diff;
                        if (diff != 0)
                        {
                            anyNonZero = true;
                        }
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels, updateContext: true);
            }
        }

        allZero = !anyNonZero;
        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>
    /// Real commit-time counterpart to <see cref="ComputePaletteResidualCost"/>: writes the real WHT residual
    /// for an approximate palette leaf to <see cref="TileState.Symbols"/> (updating <paramref name="realCtx"/>
    /// for real), and reconstructs <paramref name="recon"/> as palette-prediction-plus-dequantized-residual,
    /// mirroring <see cref="EncodeLosslessLumaResidual"/>/<see cref="EncodeRectangularLeaf"/>'s own per-sub-block
    /// predict-then-reconstruct shape exactly, just with a color-map lookup as the prediction source instead
    /// of spatial extrapolation or block-copy. Only ever called once a leaf's own residual is already known
    /// to be non-all-zero (see <c>EncodeLeaf</c>'s own call site) -- an all-zero-residual leaf uses the
    /// existing exact-match/skip=1 path instead, which is strictly cheaper (no coefficient symbols at all)
    /// for the case it already handles.
    /// </summary>
    private static void EncodePaletteResidual(TileState s, int[] source, int[] recon, int planeWidth, int ptype, int r, int c, int x, int y, int widthPixels, int heightPixels, int[] colorMap, int mapWidth, int[] colors, Av1CoefficientWriter.PlaneContext realCtx, int blockDecodedPlane, Av1CoefficientWriter.PlaneContext decideCtx)
    {
        int nW = widthPixels / 4;
        int nH = heightPixels / 4;
        var pred = s.Pred;
        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;
        int edgeMaxX = ptype == 0 ? s.EdgeMaxX : s.ChromaEdgeMaxX;
        int edgeMaxY = ptype == 0 ? s.EdgeMaxY : s.ChromaEdgeMaxY;

        for (int dr = 0; dr < nH; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > edgeMaxX || subY > edgeMaxY)
                {
                    continue;
                }

                for (int i = 0; i < 4; i++)
                {
                    int mapRowBase = (((dr * 4) + i) * mapWidth) + (dc * 4);
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        pred[predRowBase + j] = colors[colorMap[mapRowBase + j]];
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * planeWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, recon, ((subY + i) * planeWidth) + subX, 4);
                }

                if (s.Phase != Av1EncodePhase.Decide)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype, subC, subR, realCtx, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same arguments as the real commit just above, targeting the decide-time
                // counterparts instead -- levels/positions are identical since this is the same leaf's
                // own already-computed residual, not a separate re-derivation.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype, subC, subR, decideCtx, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels);
                }

                Av1LocalReconstructor.Reconstruct(recon, planeWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, blockDecodedPlane, subR & s.SbMiMask, subC & s.SbMiMask, true);
            }
        }
    }

    /// <summary>
    /// Real, cache-hit-aware bit cost for one color-cache-eligible palette color list (Y, or U within UV --
    /// V is never cached, see <see cref="WritePaletteColorsUv"/>'s own remarks), mirroring libaom's real
    /// <c>av1_palette_color_cost_y</c>/<c>_uv</c> (<c>av1/encoder/palette.c</c>) -- but using this project's
    /// own exact, already-decoder-verified adaptive-shrinking delta-width algorithm for the explicit
    /// (not-cached) colors' own cost, rather than libaom's own <c>delta_encode_cost</c> (a real, but looser,
    /// upfront-fixed-width RD-estimate approximation libaom's own real bitstream writer does not use either
    /// -- see <see cref="WriteCacheAwareColors"/>'s identical remark). <paramref name="minVal"/> is 1 for Y
    /// (<c>Av1TileDecoder.ReadPaletteColorsY</c>'s own <c>delta + 1</c>/<c>range - 1</c> convention) and 0
    /// for U (<c>ReadPaletteColorsUv</c>'s own unshifted convention).
    /// </summary>
    private static long EstimateCacheAwareColorBits(ReadOnlySpan<int> cache, int nCache, ReadOnlySpan<int> colors, int n, int minVal)
    {
        Span<bool> found = stackalloc bool[16];
        Span<int> explicitColors = stackalloc int[8];
        int nExplicit = Av1PaletteSearch.IndexColorCache(cache, nCache, colors, n, found, explicitColors, out int slotsChecked);

        long bits = slotsChecked;
        if (nExplicit > 0)
        {
            bits += 8;
            if (nExplicit > 1)
            {
                bits += 2;
                const int minBits = 5;
                const int extraBits = 3;
                int widthBits = minBits + extraBits;
                int range = 256 - explicitColors[0] - minVal;
                for (int idx = 1; idx < nExplicit; idx++)
                {
                    bits += widthBits;
                    range -= explicitColors[idx] - explicitColors[idx - 1];
                    widthBits = Math.Min(widthBits, Av1TileDecoder.CeilLog2(range));
                }
            }
        }

        return bits;
    }

    /// <summary>Pure bit-count mirror of <see cref="WritePaletteColorsY"/> -- see <see cref="EstimateCacheAwareColorBits"/>'s remarks -- without ever writing to <see cref="TileState.Symbols"/> (see <c>EstimateLosslessPaletteCost</c>'s remarks on why a speculative candidate must not).</summary>
    private static long EstimatePaletteColorBitsY(TileState s, int[] colors, int n, int r, int c, bool availU, bool availL)
    {
        Span<int> cache = stackalloc int[16];
        int nCache = GetPaletteCacheColors(s, 0, r, c, availU, availL, cache);
        return EstimateCacheAwareColorBits(cache[..nCache], nCache, colors.AsSpan(0, n), n, minVal: 1);
    }

    /// <summary>Pure bit-count mirror of <see cref="WritePaletteColorsUv"/> -- see <see cref="EstimateCacheAwareColorBits"/>'s remarks for U; V stays its own flat, never-cached, never-delta-coded cost (unchanged).</summary>
    private static long EstimatePaletteColorBitsUv(TileState s, int[] uColors, int[] vColors, int n, int r, int c, bool availU, bool availL)
    {
        Span<int> cache = stackalloc int[16];
        int nCache = GetPaletteCacheColors(s, 1, r, c, availU, availL, cache);
        long bits = EstimateCacheAwareColorBits(cache[..nCache], nCache, uColors.AsSpan(0, n), n, minVal: 0);
        bits += 1 + (n * 8);
        return bits;
    }

    /// <summary>
    /// Pure bit-count mirror of <see cref="WriteColorMapTokens(TileState, int[], int, int, int, int, int, ushort[][][])"/> -- identical NS-coded first index plus
    /// wavefront-ordered, context-selected per-pixel symbol costs, but via <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>
    /// against a <em>local, per-candidate scratch clone</em> of <paramref name="mapCdf"/>'s (small, fixed-size --
    /// spec's own <c>PALETTE_COLOR_INDEX_CONTEXTS</c> == 5) context rows, adapted in place via
    /// <see cref="Av1CdfAdaptation.AdaptCdf"/> after every pixel exactly like a real <see cref="TileState.Symbols"/>
    /// write would -- never touching <paramref name="mapCdf"/> itself.
    ///
    /// <para><b>This local adaptation is not optional.</b> A single palette leaf can carry thousands of pixels
    /// through the *same* handful of contexts (unlike coefficient coding's typically-sparse per-block symbol
    /// counts, where reading the frame's real, currently-adapted CDF once and reusing it for the whole trial is
    /// an acceptable approximation -- see <see cref="ComputeCandidateCost"/>'s remarks). Reading the same static,
    /// real CDF for every one of those pixels (this method's own first version) systematically overestimates a
    /// large uniform region's true cost by an order of magnitude or more: a real encoder's per-symbol cost
    /// collapses toward zero within the first few pixels as the CDF adapts to the region's dominant color, while
    /// a static read keeps charging close to <c>log2(n)</c> bits per pixel forever. That bug made every eligible
    /// palette candidate this method ever scored look far more expensive than regular intra/WHT residual coding
    /// -- confirmed by an instrumented run of <c>EstimateLosslessPaletteCost</c> on this project's
    /// benchmark image finding 2,946 structurally-eligible candidates across the partition search and zero
    /// wins, before this local-adaptation fix.</para>
    /// </summary>
    private static long EstimateColorMapBits(int[] colorMap, int width, int height, int n, ushort[][][] mapCdf)
    {
        var colorOrder = new int[8];
        var inverseColorOrder = new int[8];

        const int contexts = 5; // spec PALETTE_COLOR_INDEX_CONTEXTS
        var scratchCdf = new ushort[contexts][];
        var seeded = new bool[contexts];

        long bits = Av1SymbolEncoder.EstimateNsCost(colorMap[0], n);

        // Anti-diagonal (wavefront) scan generalized to width != height: for diagonal i (row + col == i),
        // col ranges from min(i, width-1) down to max(0, i-height+1) so both row and col stay in bounds --
        // collapses to the original square-only Math.Min/Max(..., size-1) bounds when width == height.
        for (int i = 1; i < width + height - 1; i++)
        {
            for (int col = Math.Min(i, width - 1); col >= Math.Max(0, i - height + 1); col--)
            {
                int row = i - col;
                int ctx = Av1TileDecoder.GetPaletteColorIndexContext(colorMap, width, row, col, n, colorOrder);
                for (int k = 0; k < n; k++)
                {
                    inverseColorOrder[colorOrder[k]] = k;
                }

                int trueIdx = colorMap[(row * width) + col];
                int symbol = inverseColorOrder[trueIdx];

                if (!seeded[ctx])
                {
                    scratchCdf[ctx] = (ushort[])mapCdf[n - 2][ctx].Clone();
                    seeded[ctx] = true;
                }

                var cdf = scratchCdf[ctx];
                bits += Av1SymbolEncoder.EstimateSymbolCost(cdf, symbol);
                Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
            }
        }

        return bits;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ClearBlockDecodedFlags</c> (spec's <c>clear_block_decoded_flags(r, c, sbSize4)</c>, §5.11.3) -- <paramref name="sbSize4"/> matches whichever superblock size this frame actually uses (see <c>EncodeTile</c>'s remarks), and this encoder is always single-tile (MiColEnd/MiRowEnd == MiCols/MiRows).</summary>
    private static void ClearBlockDecodedFlags(TileState s, int r, int c, int sbSize4)
    {
        int numPlanes = s.MonoChrome ? 1 : 3;
        for (int plane = 0; plane < numPlanes; plane++)
        {
            int subX = plane > 0 && !s.Chroma444 ? 1 : 0;
            int subY = plane > 0 && !s.Chroma444 ? 1 : 0;
            int sbWidth4 = (s.MiCols - c) >> subX;
            int sbHeight4 = (s.MiRows - r) >> subY;

            for (int y = -1; y <= sbSize4 >> subY; y++)
            {
                for (int x = -1; x <= sbSize4 >> subX; x++)
                {
                    bool value = (y < 0 && x < sbWidth4) || (x < 0 && y < sbHeight4);
                    SetBlockDecoded(s, plane, y, x, value);
                }
            }

            SetBlockDecoded(s, plane, sbSize4 >> subY, -1, false);
        }
    }

    private static bool GetBlockDecoded(TileState s, int plane, int y, int x) => s.BlockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1];

    private static void SetBlockDecoded(TileState s, int plane, int y, int x, bool value) => s.BlockDecoded[plane][((y + 1) * BlockDecodedStride) + x + 1] = value;

    /// <summary>
    /// Marks a <paramref name="bw4"/>x<paramref name="bh4"/> (in luma 4x4-mi units) region starting at
    /// absolute mi position (<paramref name="r"/>, <paramref name="c"/>) as decoded on the luma plane --
    /// the write-side equivalent of every <c>SetBlockDecoded</c> call <c>Av1TileDecoder.TransformBlock</c>
    /// would make while iterating that same footprint's real 4x4 transform blocks, collapsed into one call
    /// for the coding paths here (palette, IntraBC, non-lossless TX8X8) that predict/reconstruct a whole
    /// region at once instead of transform-block by transform-block.
    /// </summary>
    private static void MarkLumaBlockDecoded(TileState s, int r, int c, int bw4, int bh4)
    {
        int subBlockMiRow = r & s.SbMiMask;
        int subBlockMiCol = c & s.SbMiMask;
        for (int i = 0; i < bh4; i++)
        {
            for (int j = 0; j < bw4; j++)
            {
                SetBlockDecoded(s, 0, subBlockMiRow + i, subBlockMiCol + j, true);
            }
        }
    }

    /// <summary>
    /// Chroma counterpart of <see cref="MarkLumaBlockDecoded"/> -- marks the <paramref name="sizeMi"/>-leaf's
    /// whole chroma sub-block grid decoded on both chroma planes, for the prediction paths (palette, IntraBC)
    /// that reconstruct all of a leaf's chroma in one shot rather than transform-block by transform-block
    /// (<see cref="EncodeChromaRegion"/> marks its own per-sub-block progress directly as it goes). Needed
    /// now that chroma directional prediction (<see cref="SearchUvMode"/>) reads real haveAboveRight/
    /// haveBelowLeft state for later leaves -- previously chroma was always DC_PRED, which never read this
    /// state, so leaving it unmarked here was harmless.
    /// </summary>
    private static void MarkChromaBlockDecoded(TileState s, int r, int c, int sizeMi)
        => MarkChromaBlockDecoded(s, r, c, sizeMi, sizeMi);

    /// <summary>
    /// Rectangular-generalized (wMi != hMi) form of the same marking, for a HORZ/VERT-split leaf's palette
    /// commit -- see <see cref="EncodeRectangularLeaf"/>'s own remarks. Lossless is always chroma444 (see
    /// the class-level remarks on why this encoder never reaches the 4:2:0 <c>subX</c>/<c>mult</c> halving
    /// case at that call site), but this stays fully general rather than assuming it, matching the square
    /// overload's own defensive shape.
    /// </summary>
    private static void MarkChromaBlockDecoded(TileState s, int r, int c, int wMi, int hMi)
    {
        int chromaW = s.Chroma444 ? wMi : wMi / 2;
        int chromaH = s.Chroma444 ? hMi : hMi / 2;
        int subX = s.Chroma444 ? 0 : 1;
        int mult = s.Chroma444 ? 1 : 2;
        for (int dr = 0; dr < chromaH; dr++)
        {
            for (int dc = 0; dc < chromaW; dc++)
            {
                int row = ((r + (dr * mult)) & s.SbMiMask) >> subX;
                int col = ((c + (dc * mult)) & s.SbMiMask) >> subX;
                SetBlockDecoded(s, 1, row, col, true);
                SetBlockDecoded(s, 2, row, col, true);
            }
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.GetFilterType</c> (spec's <c>get_filter_type(plane)</c>,
    /// §7.11.2.8), restricted to luma (chroma stays hardcoded DC_PRED for now, which never reaches the
    /// intra-edge-filter/upsample path this value feeds, so it doesn't need this yet). Computed once per
    /// *coding block* from its own above/left neighbor -- not once per 4x4 transform sub-block -- exactly
    /// mirroring the decoder's own <c>_availU</c>/<c>_availL</c>/<c>_miRow</c>/<c>_miCol</c>-based fields,
    /// which are coding-block-scoped, not transform-block-scoped.
    /// </summary>
    private static bool GetFilterType(TileState s, int r, int c, bool availU, bool availL)
    {
        bool aboveSmooth = availU && IsSmoothMode(s, r - 1, c);
        bool leftSmooth = availL && IsSmoothMode(s, r, c - 1);
        return aboveSmooth || leftSmooth;
    }

    private static bool IsSmoothMode(TileState s, int row, int col)
    {
        row = Math.Clamp(row, 0, s.MiRows - 1);
        col = Math.Clamp(col, 0, s.MiCols - 1);
        int mode = s.YModes[(row * s.MiCols) + col];
        return mode is Av1IntraMode.SmoothPred or Av1IntraMode.SmoothVPred or Av1IntraMode.SmoothHPred;
    }

    /// <summary>
    /// Chroma counterpart of <see cref="GetFilterType"/> (spec's <c>get_filter_type(plane)</c> for plane &gt; 0).
    /// The decoder's general version nudges the above/left neighbor mi position by one unit under 4:2:0
    /// subsampling (<c>_availUChroma</c>/<c>_availLChroma</c>'s "shares chroma with the sibling" case) --
    /// that nudge only matters for a 1-mi-tall/wide luma block, and its direction otherwise depends on the
    /// current block's mi row/col parity. This encoder's leaves are always 8x8 or bigger and always
    /// superblock-aligned to their own size (a power of two &gt;= 2 mi units), so every leaf's (r, c) is
    /// always even -- collapsing the decoder's parity-conditional nudge to the fixed offsets below (above:
    /// column +1 when subsampled; left: row +1 when subsampled), and making <c>availUChroma</c>/
    /// <c>availLChroma</c> always equal <paramref name="availU"/>/<paramref name="availL"/> (the sub-8x8
    /// deferral case never triggers here either). Computed once per coding block, matching
    /// <see cref="GetFilterType"/>'s own coding-block-scoped semantics.
    /// </summary>
    private static bool GetChromaFilterType(TileState s, int r, int c, bool availU, bool availL, bool subsampled)
    {
        bool aboveSmooth = availU && IsSmoothUvMode(s, r - 1, subsampled ? c + 1 : c);
        bool leftSmooth = availL && IsSmoothUvMode(s, subsampled ? r + 1 : r, c - 1);
        return aboveSmooth || leftSmooth;
    }

    private static bool IsSmoothUvMode(TileState s, int row, int col)
    {
        row = Math.Clamp(row, 0, s.MiRows - 1);
        col = Math.Clamp(col, 0, s.MiCols - 1);
        int mode = s.UvModes[(row * s.MiCols) + col];
        return mode is Av1IntraMode.SmoothPred or Av1IntraMode.SmoothVPred or Av1IntraMode.SmoothHPred;
    }

    /// <summary><c>log2</c> of an arbitrary power-of-two pixel size (4/8/16/32/64) -- unlike <see cref="PixelLog2"/>
    /// (which maps a *luma leaf's* mi-count, and so has no representation for a 4-pixel chroma region), this
    /// covers every chroma coding-block size a 4:2:0 leaf can produce, down to 4.</summary>
    private static int Log2FromPixels(int sizePixels) => sizePixels switch
    {
        64 => 6,
        32 => 5,
        16 => 4,
        8 => 3,
        _ => 2,
    };

    /// <summary>
    /// Real <c>D + lambda*R</c> Lagrangian RD cost (<see cref="Av1RdCost"/>) for one mode/angle/filter-intra
    /// candidate, replacing two previous proxies: non-lossless candidates used raw SSE with no rate term at
    /// all, and lossless candidates a hand-tuned WHT-coefficient-magnitude/log2 proxy that only approximated
    /// real bit cost (no CDF/context modeling). Both now share one real bit count instead: this actually
    /// forward-transforms, quantizes, and trial-costs (<see cref="Av1TrialSymbolSink"/>, via
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> -- the exact same context-derivation code the real
    /// bitstream writer uses, so this is a real bit count, not an approximation of one) the residual this
    /// candidate's prediction would actually produce.
    ///
    /// <para>Non-lossless: forward DCT (<see cref="Av1ForwardTransform"/>) + quantize at
    /// <see cref="TileState.BaseQIdx"/>, cost = <c>sse + lambda*bits</c> (<see cref="Av1RdCost.CombineCost"/>).
    /// One caveat for chroma: this always transforms as DCT_DCT regardless of which <paramref name="ptype"/>
    /// 1 candidate <c>mode</c> is being scored, even though <c>EncodeChromaRegion</c>'s real (non-lossless)
    /// write later forward-transforms with a mode-dependent DCT/ADST-mixed type once a <c>uv_mode</c> is
    /// actually chosen (<c>Av1TxTypeTables.ModeToTxfm</c>, added for the real write path by PR #64) -- luma's
    /// tx_type is always DCT_DCT regardless of mode already (see <c>EncodeLeaf</c>'s <c>WriteLumaTxType</c>
    /// remarks), so this only approximates chroma. Real per-candidate transform-*type* search (this
    /// encoder's next RD-search phase, see the project plan) will let this also select the type each
    /// candidate's own mode would actually use; until then, a uniform DCT_DCT rate estimate still ranks
    /// candidates by real residual-energy-driven bit cost, just not necessarily the exact type each will
    /// finally use -- good enough for relative ranking, not for predicting the exact final byte count.</para>
    ///
    /// <para>Lossless: forward WHT (<see cref="Av1ForwardWht"/>, the real lossless transform -- see
    /// <see cref="EncodeLosslessLumaResidual"/>'s identical per-4x4-sub-block transform) + quantize at
    /// <c>baseQIdx == 0</c> (an exact identity, not a source of loss or extra bits), summing real bits across
    /// every 4x4 sub-block. Distortion is always exactly zero once a lossless candidate is really committed
    /// (every sub-block reconstructs bit-exactly), so the cost is bits alone (<see cref="Av1RdCost.CombineCost"/>
    /// called with <c>sse: 0, lambda: 1.0</c> -- not <see cref="TileState.Lambda"/>, which is 0 for lossless,
    /// meaning "don't weigh a meaningless zero-distortion term", the opposite of what's needed here).</para>
    ///
    /// <para>Both branches reseed <see cref="TileState.ScratchCoeffCtx"/> from the real, already-committed
    /// neighbor context (<paramref name="realCtx"/>) before trial-costing, mutate only the scratch copy
    /// (<c>updateContext: true</c> against scratch, matching real <c>WriteCoeffs</c> so a multi-sub-block
    /// lossless candidate's own later sub-blocks see its own earlier sub-blocks' context, exactly like a real
    /// encode would), and never write back to <paramref name="realCtx"/> itself -- a candidate that might not
    /// even be chosen must never leave a trace a later, real leaf's context lookup could read (see
    /// <see cref="Av1CoefficientWriter.PlaneContext.SeedFrom"/>'s remarks). This is the same "estimate from
    /// real, already-committed neighbors; not-yet-encoded siblings use safe defaults" posture
    /// <see cref="RdPickPartition"/>'s own remarks already document for the pixel data (<see cref="TileState.SourceY"/>
    /// vs <see cref="TileState.ReconY"/>) -- context estimation inherits the identical approximation for the
    /// identical reason.</para>
    ///
    /// <para>Takes an explicit <paramref name="source"/>/<paramref name="planeWidth"/> (rather than always
    /// reading <see cref="TileState.SourceY"/>/<see cref="TileState.YWidth"/>) and <paramref name="ptype"/>/<paramref name="realCtx"/>,
    /// so the same cost function scores luma (<c>ptype: 0</c>, <see cref="TileState.YCoeffCtx"/>) and chroma
    /// (<c>ptype: 1</c>, <see cref="TileState.UCoeffCtx"/>/<see cref="TileState.VCoeffCtx"/>) candidates alike
    /// during the UV mode search (see <see cref="SearchUvMode"/>).</para>
    /// </summary>
    private static long ComputeCandidateCost(TileState s, int[] source, int planeWidth, int[] pred, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx)
        => ComputeCandidateCostPrecise(s, source, planeWidth, pred, x, y, sizePixels, ptype, realCtx, out _);

    /// <summary>
    /// <see cref="ComputeCandidateCost"/>, additionally exposing the lossless branch's own raw, unrounded
    /// <see cref="Av1TrialSymbolSink.Units512"/> total via <paramref name="units512"/> -- needed only at a
    /// real per-candidate mode-search comparison that also adds further signaling-cost terms of its own
    /// (y_mode/angle_delta/filter_intra) on top of this residual cost, and wants to round the *combined*
    /// total once at the very end rather than rounding this residual cost and each signaling term separately
    /// (see <see cref="Av1TrialSymbolSink"/>'s own remarks for why summing several already-rounded costs
    /// compounds enough error to produce spurious exact ties). Every other, non-precision-sensitive caller
    /// keeps using <see cref="ComputeCandidateCost"/> itself, unchanged (a thin forwarding wrapper around this
    /// method, so there is only one real implementation). Non-lossless callers (the <c>!s.Lossless</c> branch
    /// below) get <c>units512 = 0</c> -- this project's own cost-precision investigation is scoped to the real
    /// lossless target image only; SSE-dominated non-lossless candidates were never the case this was measured
    /// to matter for. A distinct method name, not an overload of <see cref="ComputeCandidateCost"/> itself,
    /// specifically to avoid a bare <c>&lt;see cref="ComputeCandidateCost"/&gt;</c> becoming ambiguous at this
    /// method's own dozen-plus pre-existing doc-comment references elsewhere in this file.
    /// </summary>
    private static long ComputeCandidateCostPrecise(TileState s, int[] source, int planeWidth, int[] pred, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx, out long units512, long boundUnits512 = long.MaxValue)
    {
        units512 = 0;
        int x4 = x >> 2;
        int y4 = y >> 2;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;
        int[] coeff = s.Coeff;
        int[] levels = s.Levels;

        if (!s.Lossless)
        {
            long sse = 0;
            int[] residual = s.Residual;
            for (int i = 0; i < sizePixels; i++)
            {
                int rowBase = ((y + i) * planeWidth) + x;
                int predRowBase = i * sizePixels;
                for (int j = 0; j < sizePixels; j++)
                {
                    int diff = source[rowBase + j] - pred[predRowBase + j];
                    sse += (long)diff * diff;
                    residual[(i * sizePixels) + j] = diff;
                }
            }

            Av1ForwardTransform.Forward2D(residual, coeff, sizePixels);
            Av1ForwardQuantizer.Quantize(coeff, levels, sizePixels, s.BaseQIdx);

            int w4 = sizePixels >> 2;
            scratch.SeedFrom(realCtx, x4, w4, y4, w4);
            trial.Reset();
            Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true);

            return Av1RdCost.CombineCost(sse, trial.Bits, s.Lambda);
        }

        int leafW4 = sizePixels >> 2;
        scratch.SeedFrom(realCtx, x4, leafW4, y4, leafW4);
        trial.Reset();
        int[] subResidual = s.Residual;
        int edgeMaxX = ptype == 0 ? s.EdgeMaxX : s.ChromaEdgeMaxX;
        int edgeMaxY = ptype == 0 ? s.EdgeMaxY : s.ChromaEdgeMaxY;

        bool aborted = false;
        for (int by = 0; by < sizePixels && !aborted; by += 4)
        {
            for (int bx = 0; bx < sizePixels; bx += 4)
            {
                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping;
                // here it keeps this candidate's own estimated cost consistent with what the real commit
                // will actually write once it overhangs the true edge.
                if (x + bx > edgeMaxX || y + by > edgeMaxY)
                {
                    continue;
                }

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((y + by + i) * planeWidth) + x + bx;
                    int predRowBase = ((by + i) * sizePixels) + bx;
                    for (int j = 0; j < 4; j++)
                    {
                        subResidual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(subResidual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = (x + bx) >> 2;
                int subY4 = (y + by) >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: sizePixels, updateContext: true);

                // block_rd_txfm's own exact early-abort (av1/encoder/tx_search.c:3068-3140, ported per the
                // project plan's own Phase 3 scoping): trial.Units512 only ever grows (every WriteCoeffs call
                // adds a non-negative number of symbol-cost units), so once it already reaches boundUnits512 --
                // the caller's own precomputed "no further contribution could still let this candidate beat
                // bestCost" threshold, see EncodeLeaf's own call site remarks for the exact derivation -- no
                // amount of additional, non-negative cost from the remaining sub-blocks can undo that. Stopping
                // here is provably exact, not an approximation: it only ever discards a candidate that was
                // already guaranteed to lose, identical in spirit to av1_txfm_rd_in_plane's own
                // "current_rd > ref_best_rd" whole-candidate short-circuit. The caller reads only
                // units512/the (here-meaningless-once-aborted) return value at its own bound-aware call site,
                // never this method's own aborted/non-aborted control flow directly.
                if (trial.Units512 >= boundUnits512)
                {
                    aborted = true;
                    break;
                }
            }
        }

        if (aborted)
        {
            // Guaranteed-losing sentinel: large enough that EncodeLeaf's own combined
            // (residualUnits512 + signalingUnits512) total can never round below bestCost, but far short of
            // long.MaxValue so that combining it with a real, small signalingUnits512 term can never overflow.
            units512 = long.MaxValue / 2;
            return long.MaxValue;
        }

        units512 = trial.Units512;
        return Av1RdCost.CombineCost(0, trial.Bits, 1.0);
    }

    /// <summary>
    /// Real per-4x4-sub-block Lagrangian cost (<see cref="Av1RdCost.CombineCost"/>, <c>sse: 0, lambda: 1.0</c>)
    /// of a candidate (<paramref name="mode"/>, <paramref name="angleDelta"/>, filter-intra) across a lossless
    /// leaf bigger than 64x64 (128x128, the only size this ever runs at -- see <see cref="EstimateLumaCost(TileState, int, int, int)"/>/
    /// <c>EncodeLeaf</c>'s own call sites), by genuinely re-predicting fresh at each 4x4 sub-block instead of
    /// <see cref="ComputeCandidateCost"/>'s whole-leaf single-shot shortcut. That shortcut (build edges once
    /// from the leaf's own outer boundary, predict the whole leaf in one call) is safe up to 64x64 because
    /// real AV1 intra prediction is architecturally capped at 64x64 per prediction/transform unit regardless
    /// of coding-block size -- <see cref="Av1IntraPrediction.PredictSmooth"/>'s own <c>Sm_Weights</c> tables
    /// simply don't exist beyond that (spec never predicts a bigger unit in one shot). Every coding block
    /// above 64x64 -- which for lossless (always <c>TX_4X4</c> regardless of coding-block size) means every
    /// lossless leaf above 4x4 already, this method is only reached at 128x128 specifically because 64x64
    /// still fits the old shortcut -- is really predicted as a grid of smaller units, each with its own fresh
    /// neighbor context; <c>EncodeLosslessLumaResidual</c> already implements exactly this at 4x4 granularity
    /// for the real residual commit, and this mirrors it for candidate costing.
    ///
    /// <para>Reads <paramref name="source"/> for both prediction input and residual (safe, not approximate,
    /// for lossless -- source is bit-identical to final reconstruction once a candidate is really committed,
    /// the same substitution <see cref="RdPickPartition"/>'s own remarks already document). Never mutates
    /// <see cref="TileState.Symbols"/>, the real <see cref="TileState.Cdf"/>, or <see cref="TileState.BlockDecoded"/>
    /// -- interior sub-block-to-sub-block availability (has this leaf's own earlier sub-block, in raster
    /// order, already been virtually processed) is derived arithmetically from the sub-block's own
    /// <c>(dr, dc)</c> grid position alone (provably identical to what a real <see cref="GetBlockDecoded"/>
    /// read would give for a raster-order-earlier interior neighbor -- see this method's own remarks at the
    /// call site for the derivation), so no scratch/restore bookkeeping is needed the way coefficient
    /// contexts require. This candidate's own per-sub-block coefficient coding <em>does</em> mutate
    /// <see cref="TileState.ScratchCdf"/> (reseeded from the real <see cref="TileState.Cdf"/> once at this
    /// call's own start, via <see cref="TileState.AdaptingTrialSink"/> -- see both fields' own remarks for
    /// why this candidate specifically needs real intra-candidate CDF adaptation simulated, unlike every
    /// other cost estimate in this class), but that scratch copy is always discarded after this call returns,
    /// never written back to the real one.</para>
    ///
    /// <para>Boundary (leaf-external) neighbor availability is controlled by
    /// <paramref name="useRealBoundaryAvailability"/>: <see langword="false"/> (used by
    /// <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s speculative partition-size search) always treats a boundary-crossing
    /// neighbor as unavailable, matching every other whole-leaf estimate in this class and avoiding the
    /// measured, position-dependent IntraBC-repeat regression real boundary reads caused there (see
    /// <see cref="EstimateLumaCost(TileState, int, int, int)"/>'s own remarks); <see langword="true"/> (used by <c>EncodeLeaf</c>'s real,
    /// already-size-committed mode search) reads real <see cref="TileState.BlockDecoded"/> state instead,
    /// matching <c>EncodeLosslessLumaResidual</c>'s real behavior exactly so the mode this picks is scored the
    /// same way its real residual commit will be.</para>
    /// </summary>
    private static long ComputeLosslessWholeLeafCostPerSubBlock(TileState s, int[] source, int planeWidth, int planeHeight, int r, int c, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx, int mode, int angleDelta, bool useFilterIntra, int filterIntraMode, bool filterTypeSmooth, bool useRealBoundaryAvailability, bool useAdaptiveCdf = false, int heightPixels = 0)
        => ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, source, planeWidth, planeHeight, r, c, x, y, sizePixels, ptype, realCtx, mode, angleDelta, useFilterIntra, filterIntraMode, filterTypeSmooth, useRealBoundaryAvailability, out _, useAdaptiveCdf, heightPixels);

    /// <summary>
    /// <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>, additionally exposing the raw, unrounded
    /// <see cref="Av1TrialSymbolSink.Units512"/>/<see cref="Av1AdaptingTrialSymbolSink.Units512"/> total (from
    /// whichever sink <paramref name="useAdaptiveCdf"/> selects) via <paramref name="units512"/> -- see
    /// <see cref="ComputeCandidateCostPrecise"/>'s own identical remarks for the full rationale.
    /// </summary>
    private static long ComputeLosslessWholeLeafCostPerSubBlockPrecise(TileState s, int[] source, int planeWidth, int planeHeight, int r, int c, int x, int y, int sizePixels, int ptype, Av1CoefficientWriter.PlaneContext realCtx, int mode, int angleDelta, bool useFilterIntra, int filterIntraMode, bool filterTypeSmooth, bool useRealBoundaryAvailability, out long units512, bool useAdaptiveCdf = false, int heightPixels = 0, long boundUnits512 = long.MaxValue)
    {
        // heightPixels defaults to 0, meaning "same as sizePixels" (a square leaf -- every pre-existing call
        // site), mirroring Av1CoefficientWriter.WriteCoeffs's own blockHeight convention exactly. A nonzero
        // value generalizes this method to a rectangular (Horz/Vert) leaf -- see EstimateRectChromaCost's own
        // remarks for the one caller that needs this.
        int effectiveHeightPixels = heightPixels > 0 ? heightPixels : sizePixels;
        int nW = sizePixels / 4;
        int nH = effectiveHeightPixels / 4;
        int x4 = x >> 2;
        int y4 = y >> 2;
        var scratch = s.ScratchCoeffCtx;
        scratch.SeedFrom(realCtx, x4, nW, y4, nH);

        // useAdaptiveCdf (see TileState.ScratchCdf/AdaptingTrialSink's own remarks): reseeded from the real,
        // current Cdf once per candidate (this whole method call), never per sub-block and never written
        // back. AdaptingTrialSink then adapts it freely across this candidate's own (potentially 1024)
        // sub-blocks below, so later sub-blocks correctly see cheaper costs once earlier ones establish a
        // pattern, the way a real commit would -- but the next candidate's own call to this same method
        // starts over from the untouched real Cdf, discarding this one's simulated drift. Deliberately
        // scoped to only the specific callers that measurably need it (EstimateLosslessChromaCost's own
        // decision-phase estimate -- see its remarks): trying this for EncodeLeaf's own real luma mode
        // search too was measured, via the harness, to expose a *different*, pre-existing precision limit
        // instead (two structurally-similar modes -- H_PRED and PAETH_PRED, both reasonable predictors for
        // an axis-aligned gradient -- estimated as an exact tie even at full 1/512-bit precision, whose real,
        // committed entropy costs then differed by roughly 6x once the tie-break happened to pick the worse
        // one) -- a real, separate gap, not something this change should paper over by accident via a wider
        // blast radius than the diagnosed problem needed.
        IAv1SymbolSink trial;
        Av1CdfContext cdfForTrial;
        if (useAdaptiveCdf)
        {
            s.ScratchCdf.CopyFrom(s.CostCdf);
            s.AdaptingTrialSink.Reset();
            trial = s.AdaptingTrialSink;
            cdfForTrial = s.ScratchCdf;
        }
        else
        {
            s.TrialSink.Reset();
            trial = s.TrialSink;
            cdfForTrial = s.CostCdf;
        }

        int blockDecodedPlane = ptype == 0 ? 0 : 1;
        int subBlockMiRowBase = r & s.SbMiMask;
        int subBlockMiColBase = c & s.SbMiMask;

        // True (unpadded) edge bound for this plane -- NOT planeWidth-1/planeHeight-1 (the padded
        // working-buffer's own extent, safe only because the caller's own now-relaxed exact-fit restriction
        // used to guarantee they coincided). A real decoder's own edge-replication clamp, and its
        // transform_block() skip below, both key off the frame's true extent regardless of how this
        // encoder's own internal buffers happen to be sized.
        int edgeMaxX = ptype == 0 ? s.EdgeMaxX : s.ChromaEdgeMaxX;
        int edgeMaxY = ptype == 0 ? s.EdgeMaxY : s.ChromaEdgeMaxY;

        bool aborted = false;
        for (int dr = 0; dr < nH && !aborted; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > edgeMaxX || subY > edgeMaxY)
                {
                    continue;
                }

                bool availU = subR > 0;
                bool availL = subC > 0;
                int subBlockMiRow = subBlockMiRowBase + dr;
                int subBlockMiCol = subBlockMiColBase + dc;

                bool haveAboveRight;
                bool haveBelowLeft;
                if (useRealBoundaryAvailability)
                {
                    haveAboveRight = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow - 1, subBlockMiCol + 1);
                    haveBelowLeft = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow + 1, subBlockMiCol - 1);
                }
                else
                {
                    // Interior above-right (dr > 0 && dc + 1 < nW) was visited at raster index
                    // (dr-1)*nW+(dc+1), always < the current dr*nW+dc, so it's provably already available --
                    // exactly what a real GetBlockDecoded read would show. A leaf's own bottom-left is never
                    // interior in a raster (top-to-bottom, left-to-right) scan (the row below is never
                    // visited yet), matching real AV1 decode order too. Boundary (leaf-external) positions
                    // stay conservatively false -- see this method's own remarks on why.
                    haveAboveRight = dr > 0 && (dc + 1) < nW;
                    haveBelowLeft = false;
                }

                Av1IntraPrediction.BuildEdges(s.SubBlockAbove, s.SubBlockLeft, source, planeWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, edgeMaxX, edgeMaxY, bitDepth: 8);

                var pred = s.Pred;
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, s.SubBlockAbove, s.SubBlockLeft, mode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, edgeMaxX, edgeMaxY, subX, subY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 16; i++)
                {
                    residual[i] = source[((subY + (i / 4)) * planeWidth) + subX + (i % 4)] - pred[i];
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, cdfForTrial, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: sizePixels, updateContext: true, blockHeight: effectiveHeightPixels);

                // Same exact, provably-safe early abort as ComputeCandidateCostPrecise's own identical
                // mechanism (see its remarks) -- Phase 3's own bound-threading, extended here to the rare
                // (128px-oversized-leaf, or SearchRectUvMode's own rectangular-chroma) per-4x4-sub-block path.
                // boundUnits512 defaults to long.MaxValue, so every pre-existing (decision-phase-estimator)
                // caller that never passes it is completely unaffected.
                long currentUnits512 = useAdaptiveCdf ? s.AdaptingTrialSink.Units512 : s.TrialSink.Units512;
                if (currentUnits512 >= boundUnits512)
                {
                    aborted = true;
                    break;
                }
            }
        }

        if (aborted)
        {
            // Same guaranteed-losing sentinel convention as ComputeCandidateCostPrecise's own identical abort
            // path -- see its remarks.
            units512 = long.MaxValue / 2;
            return long.MaxValue;
        }

        long trialBits;
        if (useAdaptiveCdf)
        {
            trialBits = s.AdaptingTrialSink.Bits;
            units512 = s.AdaptingTrialSink.Units512;
        }
        else
        {
            trialBits = s.TrialSink.Bits;
            units512 = s.TrialSink.Units512;
        }

        return Av1RdCost.CombineCost(0, trialBits, 1.0);
    }

    /// <summary>
    /// <c>intra_model_rd</c> (<c>av1/encoder/intra_mode_search_utils.h</c>): a cheap SATD estimate of one
    /// (<paramref name="mode"/>, <paramref name="angleDelta"/>) candidate's residual, used only to decide
    /// (via <see cref="Av1IntraModelRdPruner.PruneIntraYMode"/>) whether this candidate is even worth this
    /// class's own real, far more expensive WHT+quantize+entropy-cost estimate
    /// (<see cref="ComputeCandidateCost"/>/<see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>) --
    /// deliberately no entropy modeling at all, just <c>sum(|Hadamard(residual)|)</c> per real AV1 tx block.
    ///
    /// <para>Mirrors <see cref="ComputeLosslessWholeLeafCostPerSubBlock"/>'s own per-4x4-sub-block prediction
    /// loop exactly (boundary-availability derivation included) rather than a separate whole-leaf-then-split
    /// path the way <see cref="ComputeCandidateCost"/>'s non-lossless branch needs for efficiency at larger
    /// sizes -- correct uniformly for any <paramref name="widthPixels"/>/<paramref name="heightPixels"/> since
    /// real AV1's own <c>intra_model_rd</c> is <em>always</em> a per-tx-block (4x4, forced by lossless) loop
    /// regardless of coding-block size or shape, so there's no size- or shape-dependent fast path to mirror
    /// here the way the real committed residual-coding path has. Independent width/height (rather than one
    /// shared <c>sizePixels</c>) needed once this method's own model-based FILTER_INTRA prune
    /// (<see cref="Av1IntraModelRdPruner.PruneFilterIntraModelRd"/>) started calling it from
    /// <see cref="EncodeRectangularLeaf"/> too, not just the square-leaf callers this method originally had --
    /// every existing square-leaf call site simply passes the same value for both.</para>
    /// </summary>
    private static long ComputeIntraModelRd(TileState s, int[] source, int planeWidth, int planeHeight, int r, int c, int x, int y, int widthPixels, int heightPixels, int ptype, int mode, int angleDelta, bool useFilterIntra, int filterIntraMode, bool filterTypeSmooth, bool useRealBoundaryAvailability)
    {
        int nCols = widthPixels / 4;
        int nRows = heightPixels / 4;
        int blockDecodedPlane = ptype == 0 ? 0 : 1;
        int subBlockMiRowBase = r & s.SbMiMask;
        int subBlockMiColBase = c & s.SbMiMask;
        long satdTotal = 0;

        // True (unpadded) edge bound for this plane -- see ComputeLosslessWholeLeafCostPerSubBlock's
        // identical remarks on why this must be the frame's true extent, not planeWidth-1/planeHeight-1.
        int edgeMaxX = ptype == 0 ? s.EdgeMaxX : s.ChromaEdgeMaxX;
        int edgeMaxY = ptype == 0 ? s.EdgeMaxY : s.ChromaEdgeMaxY;

        Span<int> residual = stackalloc int[16];
        Span<int> coeff = stackalloc int[16];

        for (int dr = 0; dr < nRows; dr++)
        {
            for (int dc = 0; dc < nCols; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > edgeMaxX || subY > edgeMaxY)
                {
                    continue;
                }

                bool availU = subR > 0;
                bool availL = subC > 0;
                int subBlockMiRow = subBlockMiRowBase + dr;
                int subBlockMiCol = subBlockMiColBase + dc;

                bool haveAboveRight;
                bool haveBelowLeft;
                if (useRealBoundaryAvailability)
                {
                    haveAboveRight = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow - 1, subBlockMiCol + 1);
                    haveBelowLeft = GetBlockDecoded(s, blockDecodedPlane, subBlockMiRow + 1, subBlockMiCol - 1);
                }
                else
                {
                    haveAboveRight = dr > 0 && (dc + 1) < nCols;
                    haveBelowLeft = false;
                }

                Av1IntraPrediction.BuildEdges(s.SubBlockAbove, s.SubBlockLeft, source, planeWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, edgeMaxX, edgeMaxY, bitDepth: 8);

                var pred = s.Pred;
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, s.SubBlockAbove, s.SubBlockLeft, mode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, edgeMaxX, edgeMaxY, subX, subY, bitDepth: 8);

                for (int i = 0; i < 16; i++)
                {
                    residual[i] = source[((subY + (i / 4)) * planeWidth) + subX + (i % 4)] - pred[i];
                }

                Av1IntraModelRdPruner.Hadamard4x4(residual, 4, coeff);
                satdTotal += Av1IntraModelRdPruner.Satd(coeff);
            }
        }

        return satdTotal;
    }

    /// <summary>
    /// Non-lossless tx_type-only candidate cost (Phase 4 of the project plan): same transform+quantize+
    /// entropy-trial pipeline <see cref="ComputeCandidateCost"/>'s non-lossless branch uses, but taking an
    /// already-computed <paramref name="residual"/>/<paramref name="sse"/> (constant across every candidate
    /// once the leaf's prediction is fixed, see the call site's remarks) and a <paramref name="txType"/> to
    /// try, instead of re-deriving the residual from a fresh prediction every time -- this is only ever
    /// called after mode/angle search has already picked a winning prediction, refining just the transform
    /// choice against it, so re-gathering the (unchanged) residual/SSE for each of the reduced set's 5
    /// candidates would be pure waste.
    /// </summary>
    private static long ComputeTxTypeCost(TileState s, int[] residual, long sse, int sizePixels, int ptype, int x, int y, Av1CoefficientWriter.PlaneContext realCtx, int txType)
    {
        int x4 = x >> 2;
        int y4 = y >> 2;
        int[] coeff = s.Coeff;
        int[] levels = s.Levels;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;

        Av1ForwardTransform.Forward2D(residual, coeff, sizePixels, txType);
        Av1ForwardQuantizer.Quantize(coeff, levels, sizePixels, s.BaseQIdx);

        int w4 = sizePixels >> 2;
        scratch.SeedFrom(realCtx, x4, w4, y4, w4);
        trial.Reset();
        Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true, txType: txType);

        return Av1RdCost.CombineCost(sse, trial.Bits, s.Lambda);
    }

    /// <summary>
    /// Phase 5 of the project plan: post-quantization rate-distortion refinement ("trellis" quantization,
    /// libaom's <c>av1_optimize_txb</c>/<c>optimize_txb</c>, <c>av1/encoder/encodetxb.c</c>) for one
    /// already-quantized, non-lossless transform block. Rounding each coefficient to its nearest
    /// reconstructible value (what <see cref="Av1ForwardQuantizer.Quantize"/> already does) minimizes each
    /// coefficient's own distortion in isolation, but ignores that a level's magnitude also drives its own
    /// entropy cost (coeff_base/coeff_br symbol size) and every later-coded coefficient's context -- so a
    /// small further reduction can sometimes trade a little distortion for enough bits to be a net win.
    ///
    /// <para><b>Distortion is measured in the coefficient (transform) domain, not the pixel domain</b> --
    /// <c>(coeff[pos] - dqCandidate)^2</c>, where <paramref name="coeff"/> is this block's real, pre-
    /// quantization forward-transform output (<see cref="Av1ForwardTransform.Forward2D"/>'s own output, the
    /// same array <see cref="Av1ForwardQuantizer.Quantize"/> just quantized <paramref name="levels"/> from)
    /// and <c>dqCandidate</c> is the trial level's dequantized value, computed the same way
    /// <see cref="Av1Dequantizer.Dequantize"/> computes it for real reconstruction (<c>level * q / dqDenom</c>,
    /// truncating -- <paramref name="coeff"/> and a real dequantized level are already the same "coefficient
    /// units" in this codebase's own forward/backward quantizer pair, by construction, so no extra libaom-style
    /// rescale is needed here the way it would be to compensate for libaom's own internal fixed-point
    /// convention). This mirrors libaom's own <c>get_coeff_dist</c> exactly in kind (coefficient-domain, not
    /// pixel-domain distortion) -- an earlier version of this method used real pixel-domain SSE (dequantize +
    /// full inverse-transform + compare to source per trial), which was both far more expensive per trial and,
    /// worse, measurably wrong: at the <em>same</em> <see cref="TileState.Lambda"/> this encoder's mode/tx_type
    /// search already uses, pixel-domain SSE at this pass's much finer per-coefficient granularity was
    /// systematically outweighed by the rate term, and the result was provably worse (bigger <em>and</em>
    /// lower PSNR) than simply picking a different qindex at the same output size -- measured on this
    /// project's own benchmark image before this coefficient-domain rewrite. Switching to a coefficient-domain
    /// distortion metric restores the same "units" convention libaom's own trellis lambda derivation assumes.
    /// </para>
    ///
    /// <para><paramref name="ptype"/> also selects libaom's own real, measured per-plane trellis rd-multiplier
    /// (<c>plane_rd_mult</c> in <c>av1/encoder/encodetxb.c</c>, intra row: luma 17, chroma 13, applied as
    /// <c>(rdmult * planeMult) &gt;&gt; 2</c>) layered on top of <see cref="TileState.Lambda"/> -- not an
    /// independently-guessed scale factor (the project's own established caution against empirically-guessed
    /// lambda scaling, see <see cref="Av1RdCost.QIndexToLambda"/>'s remarks, is about guessing a fudge factor
    /// with no reference basis; this is libaom's own real, shipped calibration for exactly this per-coefficient
    /// decision, deliberately distinct from the coarser mode/partition/tx_type lambda -- reusing that lambda
    /// unscaled for trellis was the bug the coefficient-domain rewrite above fixes, and this per-plane factor
    /// is the other half of libaom's own trellis-specific calibration).</para>
    ///
    /// <para>Only ever reduces a level's magnitude by one step, never increases it or explores further steps
    /// (nearest-rounding already chose the distortion-minimizing point in isolation, so only trading some of
    /// that away for fewer bits can help, never the reverse), processed from the last (highest-frequency,
    /// closest to eob) nonzero coefficient to the first -- the same scan order
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> itself serializes in, so dropping the block's current
    /// last nonzero coefficient to zero shrinks eob (and every bit <c>coeff_base_eob</c>/<c>eob_pt</c> would
    /// otherwise spend past it) exactly the way a real encode would. Rate is still measured exactly (a trial
    /// <see cref="Av1CoefficientWriter.WriteCoeffs"/> call against the real, already-committed neighbor
    /// context, the same mechanism <see cref="ComputeCandidateCost"/> already uses) -- only the distortion side
    /// changed; getting the rate side approximately right would reintroduce exactly the kind of
    /// hard-to-diagnose miscalibration this rewrite exists to fix.</para>
    ///
    /// <para>Only ever considers levels within <c>Av1CoeffTables.NumBaseLevels + 1</c> (the loop body's own
    /// remarks explain why) -- with that restriction in place plus the two fixes above, this project's own
    /// benchmark image measured a real, Pareto-improving trade at every tested Quality: smaller output at
    /// only a modest quality cost relative to simply picking a different qindex at the same size (e.g.
    /// Quality=90: ~8% smaller for ~0.45 dB, vs. ~1.9 dB for the same size reduction before this restriction
    /// existed) -- verified directly against real (not interpolated) same-size comparison points, not just
    /// this encoder's own <see cref="Av1RdCost"/> metric, since that metric is exactly what a miscalibrated
    /// lambda would silently agree with itself about.</para>
    /// </summary>
    private static void OptimizeCoeffTrellis(TileState s, int[] coeff, int[] levels, int sizePixels, int ptype, int x4, int y4, Av1CoefficientWriter.PlaneContext realCtx, int txType = Av1TxType.DctDct)
    {
        int txSz = Av1ForwardTransform.SizeToTxSz(sizePixels);
        int dcQ = Av1Dequantizer.DcQ(s.BaseQIdx, 8);
        int acQ = Av1Dequantizer.AcQ(s.BaseQIdx, 8);
        int dqDenom = txSz == Av1TxSize.Tx32x32 ? 2 : 1;
        int planeMult = ptype == 0 ? 17 : 13;
        double trellisLambda = s.Lambda * planeMult / 4.0;

        // txType: chroma's own callers never pass this (always Av1TxType.DctDct, this method's own default)
        // -- ModeToTxfm's own possible outputs (DctDct/AdstDct/DctAdst/AdstAdst) are all Av1TxClass.Class2D,
        // and AV1's real default scan order/coefficient-context derivation is identical across every
        // Class2D type (keyed only by tx_size and tx_class, not the specific type within the class), so the
        // default is exact there, not an approximation. Luma's own real, non-lossless caller passes the
        // actual searched bestTxType -- genuinely needed once that search can pick Av1TxType.VDct/HDct
        // (Av1TxClass.ClassVert/ClassHoriz, a real different scan/context) -- see Av1CoefficientWriter's own
        // class remarks for the full account of the real bug this closes (Round N+43).
        int[] scan = Av1ScanTables.GetScan(txSz, txType);
        int total = sizePixels * sizePixels;
        int w4 = sizePixels >> 2;
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;

        long GetCoeffDist(int pos)
        {
            int level = levels[pos];
            long q = pos == 0 ? dcQ : acQ;
            long dq = (Math.Abs((long)level) * q) / dqDenom;
            long diff = coeff[pos] - (level < 0 ? -dq : dq);
            return diff * diff;
        }

        long GetCost(long distortion)
        {
            scratch.SeedFrom(realCtx, x4, w4, y4, w4);
            trial.Reset();
            Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, sizePixels, ptype, x4, y4, scratch, writeLumaTxType: null, updateContext: true, txType: txType);
            return Av1RdCost.CombineCost(distortion, trial.Bits, trellisLambda);
        }

        long currentDist = 0;
        for (int c = 0; c < total; c++)
        {
            int pos = scan[c];
            if (levels[pos] != 0)
            {
                currentDist += GetCoeffDist(pos);
            }
        }

        long currentCost = GetCost(currentDist);

        for (int c = total - 1; c >= 0; c--)
        {
            int pos = scan[c];
            int level = levels[pos];

            // Restricted to levels the coeff_base/coeff_base_eob symbol alone already represents
            // (Av1CoeffTables.NumBaseLevels + 1 == 3, spec's own base-symbol ceiling before WriteCoeffs's
            // coeff_br loop kicks in -- see its cappedLevel > NumBaseLevels branch) -- measured, not assumed:
            // trying every nonzero level regardless of magnitude captured essentially the same size
            // reduction as this narrower search but at real, measurable extra distortion cost (~1.6 dB PSNR
            // at Quality=90 on this project's own benchmark image, for a difference in output size under
            // 0.1%). A one-step reduction on a level already past this boundary rarely changes which
            // coeff_br symbol gets written (the br loop's own granularity absorbs it), so it pays close to
            // the full quadratic distortion cost of the step for comparatively little of the rate benefit
            // that makes the trade worthwhile at smaller levels.
            if (level == 0 || Math.Abs(level) > Av1CoeffTables.NumBaseLevels + 1)
            {
                continue;
            }

            long originalDistContribution = GetCoeffDist(pos);
            int originalLevel = level;
            levels[pos] = level > 0 ? level - 1 : level + 1;

            long candidateDist = currentDist - originalDistContribution + GetCoeffDist(pos);
            long candidateCost = GetCost(candidateDist);
            if (candidateCost < currentCost)
            {
                currentCost = candidateCost;
                currentDist = candidateDist;
            }
            else
            {
                levels[pos] = originalLevel;
            }
        }
    }

    /// <summary>
    /// Real cost-based UV mode + angle_delta search, replacing the previous hardcoded DC_PRED -- mirrors
    /// <see cref="EncodeLeaf"/>'s own whole-leaf luma search (same <see cref="CandidateModes"/>/angle_delta
    /// sweep, same <see cref="ComputeCandidateCost"/> proxy), except a single <c>uv_mode</c> covers both U and
    /// V (spec §5.11.42: one mode/angle pair per coding block, not per chroma plane), so each candidate's cost
    /// is the sum of both planes' cost. Builds edges from <see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>
    /// (already-real neighbor reconstruction, exactly like the luma search reads <see cref="TileState.ReconY"/>)
    /// at the coding block's full chroma-region size -- <see cref="EncodeChromaRegion"/>'s real write later
    /// re-predicts per-4x4 sub-block from progressively reconstructed neighbors, same relationship the luma
    /// leaf search already has with <see cref="EncodeLosslessLumaResidual"/>.
    ///
    /// <para>haveAboveRight/haveBelowLeft are read from the real per-plane <see cref="TileState.BlockDecoded"/>
    /// state (previously only ever populated/queried for plane 0 -- chroma always used DC_PRED, which never
    /// reads past the block's own edges) at the *coding-block* granularity, the same approximation the luma
    /// whole-leaf search already makes for its own single edge build (see that search's remarks) -- U and V
    /// share identical geometry, so plane 1 (U)'s state stands in for both here; the real per-4x4-sub-block
    /// values <see cref="EncodeChromaRegion"/> uses are exact, not approximated.</para>
    /// </summary>
    private static (int Mode, int AngleDelta, int AlphaU, int AlphaV, long BestCost) SearchUvMode(TileState s, int r, int c, int x, int y, int sizeMi, bool availU, bool availL, int lumaMode, bool useRealBoundaryAvailability = true)
    {
        bool subsampled = !s.Chroma444;
        int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
        int cx = s.Chroma444 ? x : x / 2;
        int cy = s.Chroma444 ? y : y / 2;
        int chromaSizePixels = chromaN * 4;
        int log2Size = Log2FromPixels(chromaSizePixels);

        int subX = subsampled ? 1 : 0;
        int chromaRow = (r & s.SbMiMask) >> subX;
        int chromaCol = (c & s.SbMiMask) >> subX;

        // useRealBoundaryAvailability (project plan "lossless decision-phase real-cost parity" section,
        // Step 4's own revised scope): false for a decision-phase estimate call, where none of these three
        // reads are valid yet -- GetBlockDecoded/GetChromaFilterType both read genuinely real, commit-only
        // state (per-mi BlockDecoded flags and a real neighbor's own committed UvModes[] entry) that doesn't
        // exist for a sibling still inside RdPickPartition's own speculative, not-yet-decided recursion. The
        // real commit-time call site (EncodeLeaf, the default) is completely unaffected: this only ever
        // substitutes `false` in place of a real read via short-circuit, never changing that call's own
        // already-verified byte-exact behavior.
        bool haveAboveRight = useRealBoundaryAvailability && GetBlockDecoded(s, 1, chromaRow - 1, chromaCol + chromaN);
        bool haveBelowLeft = useRealBoundaryAvailability && GetBlockDecoded(s, 1, chromaRow + chromaN, chromaCol - 1);
        bool filterTypeSmooth = useRealBoundaryAvailability && GetChromaFilterType(s, r, c, availU, availL, subsampled);

        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;
        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        int bestAlphaU = 0;
        int bestAlphaV = 0;
        long bestCost = long.MaxValue;

        // Gated on the luma leaf's own sizeMi (matching intra_angle_info_uv()'s _miSize -- the coding
        // block's size, not the chroma plane's residual size) -- see EstimateLumaCost's identical
        // angleDeltaAllowed remarks for why this must match the real decoder's angleDelta == 0 default below
        // Block8x8, not just skip signaling for it.
        bool angleDeltaAllowed = sizeMi >= 2;

        // Chroma's own HOG-based directional pruning (Av1SpeedFeatures.ChromaIntraPruningWithHog, libaom's
        // Site A -- av1_rd_pick_intra_sbuv_mode) -- U-plane only (matching libaom's own is_chroma=1 call,
        // which never reads V for this), scaled by real 4:2:0 subsampling parity when this leaf isn't 4:4:4
        // (always a no-op for this encoder's own always-4:4:4 lossless chroma, kept for correctness/parity
        // with Av1IntraHogPruner's own general contract regardless). Deliberately luma's own DisableSmoothIntra/
        // PruneFilterIntraLevel are NOT applied here -- confirmed via direct source reading that
        // av1_rd_pick_intra_sbuv_mode consults neither (chroma has no filter_intra equivalent in AV1 at all).
        // PruneChromaModesUsingLumaWinner (effort >= 4, restricting chroma candidates by whichever luma mode
        // won) is applied separately below, inside the main candidate loop -- see
        // ChromaModeUsedFlagByLumaWinner's own remarks. Never simultaneously live with this HOG mask in this
        // project's own cascade (Av1SpeedFeatures.Compute already forces ChromaIntraPruningWithHog back to 0
        // whenever PruneChromaModesUsingLumaWinner is true, mirroring libaom's own real override), so the two
        // never do redundant work against each other.
        Span<bool> chromaDirectionalSkipMask = stackalloc bool[13];
        if (s.Lossless && s.SpeedFeatures.ChromaIntraPruningWithHog > 0)
        {
            int chromaSubsamplingScale = subsampled ? 4 : 1;
            Av1IntraHogPruner.ComputeSkipMask(s.SourceU!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, Av1IntraHogPruner.Thresh[s.SpeedFeatures.ChromaIntraPruningWithHog - 1], chromaSubsamplingScale, chromaDirectionalSkipMask);
        }

        // Real uv_mode/angle_delta_uv signaling cost (same missing-signaling-cost gap fixed for luma in
        // EncodeLeaf -- see that call site's own remarks): uv_mode's own CDF is indexed by the *luma* mode
        // that already won (spec's is_cfl_allowed()-gated UvModeCflAllowed/CflNotAllowed[lumaMode] pair,
        // matching the real write site's identical selection), not by neighbor context, so it only needs to
        // be resolved once per leaf, not per candidate.
        int uvBSize = BlockSizeFromSizeMi(sizeMi);
        bool cflAllowedForCost = s.Lossless
            ? Av1BlockTables.GetPlaneResidualSize(uvBSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4
            : true;
        var uvModeCostCdf = cflAllowedForCost ? s.CostCdf.UvModeCflAllowed[lumaMode] : s.CostCdf.UvModeCflNotAllowed[lumaMode];

        foreach (int mode in CandidateModes)
        {
            if (Av1IntraMode.IsDirectional(mode) && chromaDirectionalSkipMask[mode])
            {
                continue;
            }

            // PruneChromaModesUsingLumaWinner (av1_derived_chroma_intra_mode_used_flag, effort >= 4): unlike
            // the HOG mask above, this restricts every candidate, not just directional ones -- DC/SMOOTH/CFL
            // always survive it (see ChromaModeUsedFlagByLumaWinner's own remarks), but SMOOTH_V/SMOOTH_H/
            // Paeth are gated exactly like the directional modes are. Never actually live at the same time as
            // the HOG mask above in this project's own speed-feature cascade -- Av1SpeedFeatures.Compute
            // already forces ChromaIntraPruningWithHog back to 0 whenever this is true (mirroring libaom's own
            // speed_features.c override, since the HOG pruning becomes redundant once directional candidates
            // are already capped to one) -- so this is never redundant work with the check above, just a
            // second, independent gate for the effort levels where each is actually active.
            if (s.SpeedFeatures.PruneChromaModesUsingLumaWinner && (ChromaModeUsedFlagByLumaWinner[lumaMode] & (1 << mode)) == 0)
            {
                continue;
            }

            // Real signaling-cost-only early-reject (av1_rd_pick_intra_sbuv_mode, intra_mode_search.c:922-926):
            // real residual cost is always non-negative, so if this mode's own uv_mode signaling cost alone
            // already exceeds the running bestCost, no real residual trial could ever make it win -- a
            // mathematically exact bound, not an approximation (unlike the SATD-model shortlist elsewhere in
            // this file), so it can only skip candidates already provably unable to win, never change which
            // candidate wins. Checked once per mode (not per angle_delta): libaom's own mode_rate is the
            // uv_mode symbol's cost alone, independent of angle_delta. Lossless's own cost model is already
            // pure bits (see ScaleSignalingBits's identical s.Lossless short-circuit elsewhere in this file),
            // so no RDCOST/lambda wrapping is needed here the way libaom's own real macro applies one.
            if (Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, mode) > bestCost)
            {
                continue;
            }

            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                long cost;
                if (chromaSizePixels > 64)
                {
                    // See ComputeLosslessWholeLeafCostPerSubBlock's remarks (only reachable for a lossless
                    // 4:4:4 128x128 chroma region, paired 1:1 with a 128x128 luma leaf). Phase 3's own
                    // bound-threading, same U-then-V derivation as the common (non-oversized) branch below --
                    // see that branch's own remarks for the full rationale (av1_txfm_uvrd's real U/V bound
                    // sharing, the provable-safety argument for using this project's own tighter bound
                    // instead).
                    long oversizedModeSignaling = Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, mode);
                    long oversizedAngleSignaling = directional ? Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.AngleDelta[mode - Av1IntraMode.VPred], angleDelta + MaxAngleDelta) : 0;

                    // useRealBoundaryAvailability threaded through (was hardcoded `true` before Step 4b added
                    // a decision-phase caller of this whole method): before Step 4b, this oversized branch was
                    // only ever reached from the real commit-time call site, where useRealBoundaryAvailability
                    // is always true, so hardcoding it was equivalent. Now that a decision-phase estimate call
                    // (useRealBoundaryAvailability: false, sizeMi == 32, RdPickPartition's own top-level
                    // 128x128 superblock probe) can reach this same branch, a hardcoded `true` here would
                    // still read real, commit-only BlockDecoded state for a speculative node -- the exact bug
                    // class the rest of this method's own useRealBoundaryAvailability plumbing exists to
                    // avoid. useAdaptiveCdf: true only for the estimate call, matching the common (non-
                    // oversized) branch's own identical reasoning below.
                    long oversizedUBound = bestCost == long.MaxValue ? long.MaxValue : (bestCost - oversizedModeSignaling - oversizedAngleSignaling) * 512 - 256;
                    long oversizedCostU = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceU!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveUCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability, out _, useAdaptiveCdf: !useRealBoundaryAvailability, boundUnits512: oversizedUBound);
                    if (oversizedCostU == long.MaxValue)
                    {
                        continue;
                    }

                    long oversizedVBound = bestCost == long.MaxValue ? long.MaxValue : (bestCost - oversizedCostU - oversizedModeSignaling - oversizedAngleSignaling) * 512 - 256;
                    long oversizedCostV = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceV!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveVCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability, out _, useAdaptiveCdf: !useRealBoundaryAvailability, boundUnits512: oversizedVBound);
                    if (oversizedCostV == long.MaxValue)
                    {
                        continue;
                    }

                    cost = oversizedCostU + oversizedCostV + oversizedModeSignaling + oversizedAngleSignaling;
                }
                else
                {
                    // Real uv_mode/angle_delta_uv signaling cost -- computed here, BEFORE either plane's own
                    // residual trial (moved up from its own original position after both trials, mirroring
                    // EncodeLeaf's own identical reordering in Phase 3's first increment), purely so both
                    // values are available up front to derive the two per-plane bound-threading thresholds
                    // below. Pure reordering: neither call has any side effect, so moving them earlier changes
                    // nothing about their own value.
                    long modeSignaling = Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, mode);
                    long angleSignaling = directional ? Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.AngleDelta[mode - Av1IntraMode.VPred], angleDelta + MaxAngleDelta) : 0;

                    long costU;
                    long costV;
                    if (useRealBoundaryAvailability)
                    {
                        // Phase 3's second bound-threading increment (project plan): SearchUvMode's own
                        // two-plane (U then V) trial mirrors real libaom's av1_txfm_uvrd
                        // (av1/encoder/tx_search.c:3702-3755) -- confirmed via direct source reading that for
                        // an intra block (this project's only case), libaom passes the SAME, un-reduced outer
                        // best_rd bound to each plane's own av1_txfm_rd_in_plane call (chroma_ref_best_rd
                        // stays == ref_best_rd; the inter-only "subtract this_rd/skip_txfm_rd" refinement at
                        // :3731-3733 never fires for us), and short-circuits entirely -- never even attempting
                        // V -- the moment U's own accumulated rd already exceeds that bound (:3743-3746). This
                        // project's own cost model has no distortion term to make that exact match meaningful
                        // bit-for-bit (lossless is pure bits, unlike libaom's real RDCOST(rate, dist)), so
                        // rather than replicate libaom's own un-tightened bound verbatim, this derives the
                        // tightest SAFE bound directly: U's own final whole-bit cost is monotonic
                        // non-decreasing in its own accumulated raw units (see ComputeCandidateCostPrecise's
                        // own remarks for the exact AwayFromZero-rounding identity, (units + 256) / 512), so
                        // once no possible remaining contribution -- from U's own unfinished sub-blocks, from
                        // V (whose true minimum is a real, provable 0, entropy costs are never negative), or
                        // from this candidate's own already-known signaling -- could still let the combined
                        // total beat bestCost, aborting is exact, not approximate, for the same reason round
                        // N+23's own luma threshold is. bestCost == long.MaxValue (no real candidate found
                        // yet) is guarded explicitly to avoid overflow, matching that same precedent.
                        long uBoundUnits512 = bestCost == long.MaxValue ? long.MaxValue : ((bestCost - modeSignaling - angleSignaling) * 512) - 256;

                        // Round N+41 (project plan progress log): unified with the oversized (128x128) branch
                        // above and with EncodeLeaf's own identical luma fix this same round -- this common-size
                        // real-commit branch used to be the one place in this function still reading
                        // s.ReconU!/s.ReconV! via a single whole-leaf BuildEdges/Predict call
                        // (ComputeCandidateCostPrecise), while EncodeChromaRegion's own real residual WRITE
                        // (Av1TileEncoder.cs, separate function) has always used fresh per-4x4-sub-block
                        // prediction for whatever uv_mode wins here, regardless of how it was selected -- the
                        // same "search vs. write" mismatch found and fixed for luma. useAdaptiveCdf: false,
                        // matching the oversized branch's own already-proven `!useRealBoundaryAvailability`
                        // convention exactly (real-commit selection stays non-adaptive; only the decision-phase
                        // estimate below uses adaptive CDF, per its own load-bearing finding).
                        if (s.Lossless)
                        {
                            costU = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceU!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveUCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true, out _, useAdaptiveCdf: false, boundUnits512: uBoundUnits512);
                        }
                        else
                        {
                            Av1IntraPrediction.BuildEdges(above, left, s.ReconU!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
                            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
                            costU = ComputeCandidateCostPrecise(s, s.SourceU!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, LiveUCoeffCtx(s), out _, uBoundUnits512);
                        }

                        if (costU == long.MaxValue)
                        {
                            // U alone already guarantees a loss -- skip V entirely (matching av1_txfm_uvrd's
                            // own break-before-V exactly) and this whole mode/angle_delta candidate, avoiding
                            // both wasted work and a long.MaxValue-plus-costV overflow below.
                            continue;
                        }

                        // Same derivation as uBoundUnits512 above, now with U's own REAL (not worst-case-zero)
                        // cost folded in, since U has already finished without aborting.
                        long vBoundUnits512 = bestCost == long.MaxValue ? long.MaxValue : ((bestCost - costU - modeSignaling - angleSignaling) * 512) - 256;

                        if (s.Lossless)
                        {
                            costV = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceV!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveVCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true, out _, useAdaptiveCdf: false, boundUnits512: vBoundUnits512);
                        }
                        else
                        {
                            Av1IntraPrediction.BuildEdges(above, left, s.ReconV!, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
                            Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
                            costV = ComputeCandidateCostPrecise(s, s.SourceV!, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, LiveVCoeffCtx(s), out _, vBoundUnits512);
                        }

                        if (costV == long.MaxValue)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Decision-phase-safe path (project plan "lossless decision-phase real-cost parity"
                        // section, Step 4's own revised scope, item 2): the branch above reads s.ReconU!/
                        // s.ReconV! (real, commit-only reconstructed pixels) via ONE whole-leaf BuildEdges/
                        // Predict call -- invalid for a sibling still inside RdPickPartition's own speculative
                        // recursion (see this method's own useRealBoundaryAvailability remarks above). Reuses
                        // the same per-4x4-sub-block, *source*-pixel primitive the oversized branch above and
                        // SearchRectUvMode already use safely: exact, not approximate, for lossless (source ==
                        // recon at zero distortion), and it already replicates real per-transform-block
                        // prediction chaining (each 4x4 sub-block predicts from its own immediately-adjacent
                        // neighbor, not one shared outer edge context the way the real-commit branch above
                        // does for search purposes) -- a genuinely different, not merely faster, cost model
                        // from the real-commit branch above, which is exactly why it's gated behind
                        // useRealBoundaryAvailability rather than simply always used: that real-commit branch
                        // stays byte-for-byte unchanged from before this change.
                        // useAdaptiveCdf: true -- matching the DC/PAETH proxy this replaces (see
                        // EstimateLosslessChromaCost's own remarks: a non-adapting per-symbol trial across
                        // many sub-blocks systematically overprices a flat/repetitive plane relative to what a
                        // real, CDF-adapting commit would achieve). Confirmed load-bearing, not cosmetic: the
                        // corpus-wide measurement for this round (all 59 libavif-test-data files) showed one
                        // real-photo image (large flat/repetitive sky and water regions) regress catastrophically
                        // without it -- +48% on that one file alone, nearly the entire aggregate corpus
                        // regression this round's first attempt measured -- while every other, mostly small
                        // graphic/icon corpus file stayed near-neutral either way, isolating this specific gap
                        // rather than a broader structural problem with the estimate itself.
                        long uBound = bestCost == long.MaxValue ? long.MaxValue : ((bestCost - modeSignaling - angleSignaling) * 512) - 256;
                        costU = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceU!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveUCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: false, out _, useAdaptiveCdf: true, boundUnits512: uBound);
                        if (costU == long.MaxValue)
                        {
                            continue;
                        }

                        long vBound = bestCost == long.MaxValue ? long.MaxValue : ((bestCost - costU - modeSignaling - angleSignaling) * 512) - 256;
                        costV = ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceV!, s.ChromaWidth, s.ChromaHeight, r, c, cx, cy, chromaSizePixels, ptype: 1, LiveVCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: false, out _, useAdaptiveCdf: true, boundUnits512: vBound);
                        if (costV == long.MaxValue)
                        {
                            continue;
                        }
                    }

                    cost = costU + costV + modeSignaling + angleSignaling;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;
                }
            }
        }

        // CFL (Phase 6 backlog item of the project plan): tried whenever spec's own is_cfl_allowed() would
        // permit it -- unconditionally for non-lossless (cflAllowedForCost is always true there), and for
        // lossless only at the one leaf size spec's own Lossless branch of is_cfl_allowed() permits (chroma's
        // own residual size == BLOCK_4X4, exactly what cflAllowedForCost above already computes for the
        // regular candidates' own uv_mode CDF selection -- reused here as the real eligibility gate, not just
        // a cost-table choice). Competes fairly against every mode above via the same real ComputeCandidateCost
        // trial cost, just with a linear-model prediction (Av1IntraPrediction's own decode-side
        // PredictChromaFromLuma math, spec §7.11.5) instead of a fixed directional/smooth pattern.
        //
        // Not tried at all when !useRealBoundaryAvailability (a decision-phase estimate call): TryCflCandidate
        // reads s.ReconY!/s.ReconU!/s.ReconV! unconditionally (both for the luma AC source and each plane's
        // own edge context), the identical real-commit-only-state dependency the regular candidate loop above
        // needed a whole separate cost model to avoid -- CFL has no analogous source-pixel-only primitive
        // ported yet. Omitting it here is strictly no worse than the DC/PAETH-only proxy this whole method
        // replaces (which never considered CFL either): a genuinely un-ported candidate family for the
        // decision phase specifically, not a regression, and a real candidate for a future round once/if it's
        // shown to matter.
        if (useRealBoundaryAvailability && (!s.Lossless || cflAllowedForCost))
        {
            long cflCost = TryCflCandidate(s, x, y, cx, cy, chromaSizePixels, log2Size, subX, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, out int cflAlphaU, out int cflAlphaV);

            // Real uv_mode signaling cost for CFL itself -- every regular candidate in the loop above already
            // pays this (EstimateSymbolCost(uvModeCostCdf, mode)); CFL's own trial previously compared its
            // own residual+alpha cost directly against bestCost with no such term, an unfair (and, for
            // lossless once CFL is actually reachable there, more consequential) comparison that silently let
            // CFL win close ties purely by skipping a real cost every other candidate pays.
            if (cflCost < long.MaxValue)
            {
                cflCost += Av1SymbolEncoder.EstimateSymbolCost(uvModeCostCdf, Av1IntraMode.UvCflPred);
            }

            if (cflCost < bestCost)
            {
                bestCost = cflCost;
                bestMode = Av1IntraMode.UvCflPred;
                bestAngleDelta = 0;
                bestAlphaU = cflAlphaU;
                bestAlphaV = cflAlphaV;
            }
        }

        return (bestMode, bestAngleDelta, bestAlphaU, bestAlphaV, bestCost);
    }

    /// <summary>
    /// CFL (chroma-from-luma, spec §7.11.5) candidate for <see cref="SearchUvMode"/> -- tried for both
    /// non-lossless and lossless now (see that method's own <c>cflAllowedForCost</c> gate for lossless's real,
    /// spec-mandated restriction to the one leaf size <c>is_cfl_allowed()</c> permits it at). Reuses
    /// <see cref="ComputeCandidateCost"/>'s own already-lossless-aware WHT/pure-bits branch for
    /// <c>costU</c>/<c>costV</c> unchanged; only this method's own final alpha-signaling cost needed its own
    /// lossless fix (see its remarks) since it combines bits directly, not through that shared helper.
    ///
    /// <para>U and V are searched independently -- CFL's alpha_u/alpha_v each only affect their own plane's
    /// prediction -- around a fast least-squares estimate of the alpha that best explains this block's real
    /// chroma AC content from its own luma AC content (<c>alpha* = sum(lumaAc*chromaAc) / sum(lumaAc^2)</c>,
    /// the closed-form minimizer of squared prediction error before quantization/entropy cost is considered
    /// at all); a small window around that estimate is then trial-costed for real via the same
    /// <see cref="ComputeCandidateCost"/> every other candidate in <see cref="SearchUvMode"/> uses, so the
    /// actual accept/reject decision is never based on the (rate-blind) estimate itself. alpha_u/alpha_v
    /// share one bitstream sign symbol (spec's <c>cfl_alpha_signs</c> -- (0,0) isn't an encodable combination,
    /// at least one channel must be nonzero), so the two planes' independently-best alphas are combined and
    /// the real joint signaling cost (<see cref="WriteCflAlphas"/>, trial-costed exactly like the residual
    /// bits are) is added once here, not per-plane.</para>
    /// </summary>
    private static long TryCflCandidate(TileState s, int lumaX, int lumaY, int cx, int cy, int chromaSizePixels, int log2Size, int subX, bool availL, bool availU, bool haveAboveRight, bool haveBelowLeft, bool filterTypeSmooth, Av1EdgeArray above, Av1EdgeArray left, int[] pred, out int alphaU, out int alphaV)
    {
        int[] lumaAcU = s.CflLumaAc;
        long lumaAvgU = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaSizePixels, log2Size, subX, lumaAcU);

        long costU = TryCflPlane(s, s.ReconU!, s.SourceU!, LiveUCoeffCtx(s), cx, cy, chromaSizePixels, log2Size, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, lumaAcU, lumaAvgU, out alphaU);

        // U's own AC buffer is fully consumed (every read of it happens inside TryCflPlane's own
        // alpha-candidate loop, via ApplyCflAlpha) before V starts, so reusing the same TileState.CflLumaAc
        // scratch for both planes -- like SearchUvMode's own pred buffer -- is safe. This must stay a
        // dedicated buffer, not TileState.Residual: ComputeCandidateCost's own non-lossless branch (called
        // from inside TryCflPlane's loop, once per alpha candidate) clobbers Residual as its own scratch on
        // every call, which would silently feed ApplyCflAlpha stale pixel-residual data instead of real luma
        // AC values from the second alpha candidate onward.
        int[] lumaAcV = s.CflLumaAc;
        long lumaAvgV = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaSizePixels, log2Size, subX, lumaAcV);
        long costV = TryCflPlane(s, s.ReconV!, s.SourceV!, LiveVCoeffCtx(s), cx, cy, chromaSizePixels, log2Size, availL, availU, haveAboveRight, haveBelowLeft, filterTypeSmooth, above, left, pred, lumaAcV, lumaAvgV, out alphaV);

        if (alphaU == 0 && alphaV == 0)
        {
            // Not spec-encodable (cfl_alpha_signs has no (zero, zero) symbol -- ReadCflAlphas's own
            // signU/signV derivation never produces this pair) and not useful anyway: zero alpha on both
            // planes means CFL's AC term vanishes entirely, so this would just be a strictly more expensive
            // way to signal what DC_PRED already offers for free.
            return long.MaxValue;
        }

        var trial = s.TrialSink;
        trial.Reset();
        WriteCflAlphas(trial, s.CostCdf, alphaU, alphaV);

        // Lambda 0 for lossless means "don't weigh a meaningless zero-distortion term" (see
        // EncodeLosslessLumaResidual's identical CombineCost remarks) -- the opposite of what's needed for a
        // pure bit-count signaling cost like this one, which would otherwise silently price as free and bias
        // every lossless comparison this feeds into.
        long signalingCost = Av1RdCost.CombineCost(0, trial.Bits, s.Lossless ? 1.0 : s.Lambda);

        return costU + costV + signalingCost;
    }

    /// <summary>One plane's real, trial-costed CFL alpha search -- see <see cref="TryCflCandidate"/>'s remarks.</summary>
    private static long TryCflPlane(TileState s, int[] reconPlane, int[] source, Av1CoefficientWriter.PlaneContext ctx, int cx, int cy, int chromaSizePixels, int log2Size, bool availL, bool availU, bool haveAboveRight, bool haveBelowLeft, bool filterTypeSmooth, Av1EdgeArray above, Av1EdgeArray left, int[] pred, int[] lumaAc, long lumaAvg, out int bestAlpha)
    {
        Av1IntraPrediction.BuildEdges(above, left, reconPlane, s.ChromaWidth, cx, cy, chromaSizePixels, chromaSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
        Av1IntraPrediction.Predict(pred, chromaSizePixels, chromaSizePixels, log2Size, log2Size, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);

        // DC_PRED produces one constant value across the whole predicted block, so a single sample stands in
        // for the entire buffer -- avoids needing a second scratch buffer to remember the DC baseline across
        // ApplyCflAlpha's per-candidate in-place overwrites of `pred`.
        int dcConstant = pred[0];

        long numerator = 0;
        long denominator = 0;
        int total = chromaSizePixels * chromaSizePixels;
        for (int i = 0; i < chromaSizePixels; i++)
        {
            int rowBase = ((cy + i) * s.ChromaWidth) + cx;
            int localRowBase = i * chromaSizePixels;
            for (int j = 0; j < chromaSizePixels; j++)
            {
                long ac = lumaAc[localRowBase + j] - lumaAvg;
                numerator += ac * (source[rowBase + j] - dcConstant);
                denominator += ac * ac;
            }
        }

        bestAlpha = 0;
        if (denominator == 0)
        {
            // This block's luma has zero AC variance (perfectly flat) -- CFL has nothing to predict from,
            // regardless of what the chroma content looks like.
            return long.MaxValue;
        }

        int alphaEstimate = (int)Math.Clamp(Math.Round(numerator * 64.0 / denominator), -16, 16);
        int windowLo = Math.Max(-16, alphaEstimate - 1);
        int windowHi = Math.Min(16, alphaEstimate + 1);

        long bestCost = long.MaxValue;
        for (int alpha = windowLo; alpha <= windowHi; alpha++)
        {
            if (alpha == 0)
            {
                continue; // never spec-legal to signal alone (see TryCflCandidate) and never useful either
            }

            ApplyCflAlpha(pred, lumaAc, lumaAvg, total, dcConstant, alpha, bitDepth: 8);
            long cost = ComputeCandidateCost(s, source, s.ChromaWidth, pred, cx, cy, chromaSizePixels, ptype: 1, ctx);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestAlpha = alpha;
            }
        }

        return bestCost;
    }

    /// <summary>
    /// Luma AC term for CFL (spec §7.11.5's <c>predict_chroma_from_luma</c>, the part shared across every
    /// alpha candidate -- luma content doesn't depend on alpha, only how it's scaled and added to chroma's DC
    /// does). Ported from <see cref="Av1IntraPrediction.PredictChromaFromLuma"/>'s own identical loop, not a
    /// re-derivation: same subsample-and-average-then-Q3-scale computation, same rounding.
    ///
    /// <para><paramref name="lumaPlane"/> is always <see cref="TileState.ReconY"/> in practice, but taken as
    /// a parameter rather than hardcoded so this method doesn't need to know which caller it's serving. Real,
    /// already-reconstructed luma matters here: <see cref="SearchUvMode"/>'s CFL candidate only runs after
    /// <see cref="EncodeLeaf"/>'s early, pre-search luma reconstruction (see its remarks) has already written
    /// this leaf's own real <see cref="TileState.ReconY"/> data, and the real, final commit
    /// (<see cref="EncodeChromaRegion"/>/<see cref="EncodeNonLosslessLargeChromaRegion"/>) reads the same,
    /// by-then-unquestionably-real buffer -- both callers see identical data, eliminating the search/commit
    /// mismatch an earlier version of this method had when the search used <see cref="TileState.SourceY"/>
    /// as a stand-in instead.</para>
    /// </summary>
    private static long ComputeCflLumaAc(int[] lumaPlane, int lumaStride, int lumaX, int lumaY, int chromaSizePixels, int log2Size, int subX, int[] lumaAcOut)
    {
        int subY = subX; // this encoder's chroma subsampling is always symmetric (4:2:0 or 4:4:4)
        int maxLumaW = lumaX + (chromaSizePixels << subX);
        int maxLumaH = lumaY + (chromaSizePixels << subY);

        long lumaAvg = 0;
        for (int i = 0; i < chromaSizePixels; i++)
        {
            int lumaRow = Math.Min(lumaY + (i << subY), maxLumaH - (1 << subY));
            for (int j = 0; j < chromaSizePixels; j++)
            {
                int lumaCol = Math.Min(lumaX + (j << subX), maxLumaW - (1 << subX));

                int t = 0;
                for (int dy = 0; dy <= subY; dy++)
                {
                    for (int dx = 0; dx <= subX; dx++)
                    {
                        t += lumaPlane[((lumaRow + dy) * lumaStride) + lumaCol + dx];
                    }
                }

                int v = t << (3 - subX - subY);
                lumaAcOut[(i * chromaSizePixels) + j] = v;
                lumaAvg += v;
            }
        }

        return Round2(lumaAvg, log2Size + log2Size);
    }

    /// <summary>Applies a candidate CFL alpha on top of an already-DC-predicted <paramref name="pred"/>, matching <see cref="Av1IntraPrediction.PredictChromaFromLuma"/>'s exact formula (spec §7.11.5).</summary>
    private static void ApplyCflAlpha(int[] pred, int[] lumaAc, long lumaAvg, int total, int dcConstant, int alpha, int bitDepth)
    {
        for (int i = 0; i < total; i++)
        {
            long ac = lumaAc[i] - lumaAvg;
            int scaledLuma = Round2Signed(alpha * ac, 6);
            pred[i] = Clip1(dcConstant + scaledLuma, bitDepth);
        }
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadCflAlphas</c> (spec §5.11.45's write direction) -- an
    /// <see cref="IAv1SymbolSink"/> parameter so the same call trial-costs (<see cref="Av1TrialSymbolSink"/>,
    /// via <see cref="TryCflCandidate"/>) or really writes (<see cref="Av1SymbolEncoder"/>, via
    /// <see cref="EncodeLeaf"/>) identically, matching this file's existing <see cref="Av1CoefficientWriter.WriteCoeffs"/>
    /// convention. <paramref name="alphaU"/>/<paramref name="alphaV"/> are the real signed alpha values
    /// (spec range roughly ±1..16, never both zero -- see <see cref="TryCflCandidate"/>'s remarks), not the
    /// bitstream symbols themselves; this derives cflAlphaSigns/cflAlphaU/cflAlphaV the same way
    /// <c>ReadCflAlphas</c> reconstructs alphaU/alphaV from them, just in reverse.
    /// </summary>
    private static void WriteCflAlphas(IAv1SymbolSink sink, Av1CdfContext cdf, int alphaU, int alphaV)
    {
        int signU = alphaU == 0 ? 0 : alphaU < 0 ? 1 : 2;
        int signV = alphaV == 0 ? 0 : alphaV < 0 ? 1 : 2;

        // Inverse of ReadCflAlphas's `signU = (cflAlphaSigns+1)/3; signV = (cflAlphaSigns+1)%3` -- a bijection
        // over cflAlphaSigns in [0,7] onto every (signU, signV) pair except (0, 0), which TryCflCandidate's
        // caller never passes here.
        int cflAlphaSigns = (signU * 3) + signV - 1;
        sink.WriteSymbol(cdf.CflSign, cflAlphaSigns);

        if (signU != 0)
        {
            int ctx = ((signU - 1) * 3) + signV;
            sink.WriteSymbol(cdf.CflAlpha[ctx], Math.Abs(alphaU) - 1);
        }

        if (signV != 0)
        {
            int ctx = ((signV - 1) * 3) + signU;
            sink.WriteSymbol(cdf.CflAlpha[ctx], Math.Abs(alphaV) - 1);
        }
    }

    /// <summary><c>Round2</c> (spec §4.7). Duplicated from <see cref="Av1IntraPrediction"/>'s own private identically-named helper rather than widening that method's visibility, to keep this encoder-only CFL search self-contained.</summary>
    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);

    /// <summary><c>Round2Signed</c> (spec §4.7). See <see cref="Round2"/>'s remarks.</summary>
    private static int Round2Signed(long x, int n) => x >= 0 ? Round2(x, n) : -Round2(-x, n);

    /// <summary><c>Clip1</c> (spec §4.10.6). See <see cref="Round2"/>'s remarks.</summary>
    private static int Clip1(int x, int bitDepth) => Math.Clamp(x, 0, (1 << bitDepth) - 1);

    private static int PartitionContext(TileState s, int r, int c, int bSize, out int bsl)
    {
        bsl = Av1BlockTables.MiWidthLog2[bSize];
        bool above = r > 0 && Av1BlockTables.MiWidthLog2[s.MiSizes[((r - 1) * s.MiCols) + c]] < bsl;
        bool left = c > 0 && Av1BlockTables.MiHeightLog2[s.MiSizes[(r * s.MiCols) + c - 1]] < bsl;
        return ((left ? 1 : 0) * 2) + (above ? 1 : 0);
    }

    private static void EncodeLeaf(TileState s, int r, int c, int sizeMi)
    {
        int sizePixels = sizeMi * 4;
        int bSize = BlockSizeFromSizeMi(sizeMi);
        bool availU = r > 0;
        bool availL = c > 0;
        int x = c * 4;
        int y = r * 4;

        int aboveYMode = availU ? s.YModes[((r - 1) * s.MiCols) + c] : Av1IntraMode.DcPred;
        int leftYMode = availL ? s.YModes[(r * s.MiCols) + c - 1] : Av1IntraMode.DcPred;
        int yModeCtx0 = Av1BlockTables.IntraModeContext[aboveYMode];
        int yModeCtx1 = Av1BlockTables.IntraModeContext[leftYMode];

        // Sized to match Av1TileDecoder's own AboveRow/LeftCol buffers (528) rather than a leaf-size-derived
        // formula: the edge-upsample process (Av1IntraPrediction.EdgeUpsample, spec §7.11.2.11) writes at
        // index up to 2*numPx-2 where numPx can reach w+h (not just w or h), so a naive "2*sizePixels"
        // capacity is exactly half of what a 64px leaf's directional-mode search can need once angle_delta
        // is actually varied (Phase D) -- this bit this encoder before angle_delta search existed only
        // because DC/H/V/Smooth/Paeth never reach the upsample path at all.
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);

        // haveAboveRight/haveBelowLeft, computed from the real BlockDecoded state (mirroring
        // Av1TileDecoder.TransformBlock exactly, treating this whole leaf as one transform block -- the
        // literal case for the non-lossless 8x8 path, which uses bestPred as its final, actually-encoded
        // prediction with no re-derivation afterward). This matters for real correctness, not just search
        // quality, once angle_delta is actually varied (Phase D): a directional predictor can read samples
        // past the block's own top-right/bottom-left corner, and BuildEdges silently clamps/replicates
        // instead when told a neighbor isn't available -- if that disagrees with what a real decoder
        // independently computes for the same position, the two sides predict different pixels from the
        // same signaled mode/angle_delta, corrupting every pixel from there on.
        int subBlockMiRow = r & s.SbMiMask;
        int subBlockMiCol = c & s.SbMiMask;
        bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + sizeMi);
        bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + sizeMi, subBlockMiCol - 1);

        // Computed once per coding block (not per candidate, not per transform sub-block) -- see
        // GetFilterType's remarks for why this must match the decoder's own coding-block-scoped semantics.
        bool filterTypeSmooth = GetFilterType(s, r, c, availU, availL);

        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        long bestCost = long.MaxValue;
        var bestPred = s.BestPred;
        var pred = s.Pred;
        int log2Size = PixelLog2(sizeMi);
        int leafElements = sizePixels * sizePixels;

        // Below Block8x8 (sizeMi == 1), spec's intra_angle_info_y() (§5.11.42) never reads an angle_delta
        // symbol and the decoder always reconstructs with angleDelta == 0 regardless of mode -- see
        // EstimateLumaCost's identical angleDeltaAllowed remarks for why the search itself, not just the
        // write site below, has to respect this for real (not just estimated) leaves too.
        bool angleDeltaAllowed = sizeMi >= 2;

        Span<bool> directionalModeSkipMask = stackalloc bool[13];
        ComputeLumaPruning(s, s.Lossless, s.SourceY, s.YWidth, x, y, sizePixels, sizePixels, directionalModeSkipMask, out bool skipSmoothVh, out bool skipSmoothPlain);

        Span<long> topIntraModelRd = stackalloc long[4];
        int topModelCount = s.Lossless ? s.SpeedFeatures.TopIntraModelCountAllowed : 0;
        topIntraModelRd[..Math.Max(topModelCount, 0)].Fill(long.MaxValue);
        long bestModelRd = long.MaxValue;

        foreach (int mode in CandidateModes)
        {
            if (IsLumaModePruned(mode, directionalModeSkipMask, skipSmoothVh, skipSmoothPlain))
            {
                continue;
            }

            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                if (topModelCount > 0)
                {
                    long modelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, sizePixels, ptype: 0, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true);
                    if (Av1IntraModelRdPruner.PruneIntraYMode(modelRd, ref bestModelRd, topIntraModelRd[..topModelCount]))
                    {
                        continue;
                    }
                }

                // Rebuilt fresh for every candidate: directional prediction (Av1IntraPrediction.PredictDirectional)
                // filters/upsamples aboveRow/leftCol IN PLACE (spec §7.11.2.11/.12), so reusing one mutated
                // pair of edge arrays across multiple candidates would feed every candidate after the first
                // directional one increasingly corrupted edge data -- including the eventual "winning"
                // candidate, whose bestPred (used directly as the final, actually-encoded prediction for the
                // non-lossless path) would then disagree with what a real decoder's own single fresh
                // BuildEdges-then-Predict call independently reconstructs.
                // Real y_mode/angle_delta signaling cost (matching libaom's own av1_rd_pick_intra_sby_mode,
                // which adds bmode_costs[mbmi->mode] -- the real symbol cost of the y_mode choice itself --
                // into every candidate's total rate, confirmed by directly reading its source). Accumulated
                // here in the same raw, unrounded 1/512-bit units the residual cost below is already in (see
                // Av1TrialSymbolSink's own remarks for why summing several already-rounded costs compounds
                // enough error to produce spurious exact ties between genuinely different candidates -- this
                // project's own real per-position instrumented comparison against aomenc found exactly that
                // at an ordinary 4x8px leaf's own y_mode comparison), rounding the *combined* residual+
                // signaling total only once, right before this candidate's own bestCost comparison. Computed
                // BEFORE the residual cost below (moved up from its own original position after that call)
                // specifically so its own value is available to derive boundUnits512 -- pure reordering, no
                // value computed here depends on the residual cost itself.
                long signalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.IntraFrameYMode[yModeCtx0][yModeCtx1], mode);

                // Real angle_delta signaling cost (spec's intra_angle_info_y(), §5.11.42) -- the same
                // missing-signaling-cost gap as y_mode above, just for the angle_delta symbol: a
                // directional mode choosing a nonzero angle_delta pays a real symbol cost non-directional
                // modes (which never write this symbol at all -- PAETH_PRED, SMOOTH*, DC_PRED) or
                // angleDelta == 0 never do, and that cost genuinely varies by delta value (the AngleDelta
                // CDF isn't uniform). Without this, a directional candidate's own angleDelta sweep could
                // win a tie against angleDelta == 0 (or against a non-directional mode) purely because it
                // was evaluated first, not because it's actually cheaper once real signaling is included.
                if (directional)
                {
                    signalingUnits512 += Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.AngleDelta[mode - Av1IntraMode.VPred], angleDelta + MaxAngleDelta);
                }

                // Phase 3's own first bound-threading increment (project plan): a direct port of
                // av1_txfm_rd_in_plane's own "current_rd > ref_best_rd" early abort
                // (av1/encoder/tx_search.c:3068-3140), applied to this candidate's own residual-only cost.
                // Derivation of the exact, non-approximate threshold: this candidate wins iff
                // round((residualUnits512 + signalingUnits512) / 512, AwayFromZero) &lt; bestCost. For any
                // non-negative total x, AwayFromZero rounding is round-half-up, i.e. the rounded result
                // equals the truncating integer division (x + 256) / 512 -- so the win condition is exactly
                // (residualUnits512 + signalingUnits512 + 256) / 512 &lt; bestCost, i.e.
                // residualUnits512 &lt; bestCost*512 - 256 - signalingUnits512. Once the residual trial's own
                // raw units (which only ever grow) reach or exceed that threshold, the candidate is
                // guaranteed to lose no matter what the remaining sub-blocks contribute -- both
                // ComputeCandidateCostPrecise's and ComputeLosslessWholeLeafCostPerSubBlockPrecise's own
                // abort paths (see their remarks) are safe to trigger. bestCost == long.MaxValue (no real
                // candidate found yet anywhere in this leaf) is guarded explicitly rather than multiplied,
                // to avoid overflow -- an unbounded threshold there is exactly correct anyway, since nothing
                // has ever beaten "no bound" yet.
                long boundUnits512 = bestCost == long.MaxValue ? long.MaxValue : (bestCost * 512) - 256 - signalingUnits512;

                // Round N+40/N+41 (project plan progress log): unified with EstimateLumaCost's own identical
                // generalization -- real AV1 lossless intra prediction is genuinely per-4x4-sub-block (spec
                // transform_block(), TX_4X4 forced regardless of coding-block size) at every leaf size above
                // 4x4, not just above 64x64 (previously the only size ComputeLosslessWholeLeafCostPerSubBlock
                // was used for here, since a single whole-leaf Predict call is architecturally impossible for
                // a 128x128 leaf -- PredictSmooth's own weight tables don't exist beyond 64x64). This half of
                // the unification -- the REAL commit-time mode SELECTION, not just the decision-phase cost
                // ESTIMATE Round N+40 first tried alone -- turned out to be load-bearing, not optional:
                // EncodeLosslessLumaResidual's own real residual WRITE has always used fresh per-sub-block
                // prediction for whatever mode wins here, regardless of how that mode was selected. Unifying
                // only the decision-phase estimate (Round N+40) left this search still picking its winner
                // under the *less* accurate whole-leaf-shortcut metric -- so the estimate could correctly
                // predict "NONE will cost X once the real best mode is found", while this search, still using
                // the old metric, could pick a *different* mode whose real (per-sub-block, as actually
                // written) cost was higher than X -- a genuine, newly-introduced mismatch between what the
                // partition search was promised and what commit actually delivered, and the real root cause
                // of Round N+40's own universal corpus regression (not a flaw in the more-accurate estimate
                // itself). Fixing only this side without also fixing the decision-phase estimate would
                // reintroduce the original, pre-N+40 divergence in the other direction, so both must move
                // together -- see the round log for the full derivation.
                long residualUnits512;
                if (s.Lossless)
                {
                    ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true, out residualUnits512, boundUnits512: boundUnits512);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, mode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, x, y, bitDepth: 8);
                    ComputeCandidateCostPrecise(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), out residualUnits512, boundUnits512);
                }

                long cost = (long)Math.Round((residualUnits512 + signalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;

                    // bestPred only matters for the non-lossless whole-leaf commit below (gated
                    // !s.Lossless) -- never read for lossless (see EncodeLosslessLumaResidual's own real,
                    // separate per-4x4 prediction), and the lossless branch above never fills a real
                    // whole-leaf pred buffer to copy (each sub-block's own prediction lives only inside
                    // ComputeLosslessWholeLeafCostPerSubBlockPrecise's own internal loop).
                    if (!s.Lossless)
                    {
                        Array.Copy(pred, bestPred, leafElements);
                    }
                }
            }
        }

        // Whether screen-content palette mode (spec §5.11.46/§5.11.47) is even structurally present in the
        // bitstream for this leaf -- true whenever the frame declared allow_screen_content_tools and this
        // leaf's size is palette-eligible (Av1TileDecoder.AllowPalette: Block8x8 through Block64x64 -- real
        // AV1 simply doesn't define palette's block-size-context CDF (PaletteYMode/PaletteYSize) beyond
        // 64x64, spec's own PALETTE_BLOCK_SIZE_CONTEXTS == 7 covering exactly that range, so a coding block
        // above 64x64 -- reachable now via 128x128 superblocks -- must never read palette_mode_info() at
        // all). Every leaf this encoder produced before the partition floor reached 4x4 (sizeMi == 1)
        // satisfied the lower size gate unconditionally (floor was 8x8), but a genuine 4x4 leaf does not, and
        // every leaf before 128x128 superblocks satisfied the upper gate unconditionally too -- this must
        // exactly match the decoder's own gate: getting it wrong doesn't just miss a compression opportunity,
        // it either desyncs every bit after it (reading/writing a symbol the other side doesn't) or, as found
        // here, indexes PaletteYMode/PaletteYSize out of bounds outright. Hoisted here (was previously
        // computed later, right before this same search) purely so Y-palette can move earlier -- see below.
        //
        // Deliberately NOT lossless-gated (unlike paletteSearchEnabled below): real
        // Av1TileDecoder.AllowPalette has no lossless check at all -- has_palette_y/has_palette_uv are
        // structurally present (must be written, even as "not used") whenever allow_screen_content_tools is
        // true and this leaf's size qualifies, regardless of losslessness. This distinction only started
        // mattering once allow_screen_content_tools could become true for a non-lossless frame too (see
        // Av1FrameEncoder.Encode's own non-lossless IntraBC estimator, which needs this same frame flag) --
        // before that, allow_screen_content_tools was only ever true when lossless also was, so the two
        // conditions happened to coincide. Conflating them again here would silently omit a real,
        // decoder-expected has_palette_y/has_palette_uv bit for any non-lossless leaf that reaches
        // palette_mode_info() at all, permanently desyncing every entropy-coded bit after it -- the exact
        // same class of bug this project's own IntraBC-for-non-lossless audit found in
        // Av1FrameHeaderWriter's loop-filter/CDEF gates (see that fix's own remarks).
        bool paletteBitsPresent = s.AllowScreenContentTools && sizeMi is >= 2 and <= 16;

        // Whether this encoder actually SEARCHES for palette usage at all -- now also enabled for non-lossless
        // (project plan Phase 4, exact-match only -- mirroring non-lossless IntraBC's own identical scoping),
        // third attempt at closing this real, previously twice-reverted bug (see the round log's own
        // "palette-for-lossy" entries for the first two attempts' full history and the still-open 6th-bug
        // symptom, a genuine reconstruction corruption found after all five previously-known bugs were fixed).
        bool paletteSearchEnabled = paletteBitsPresent;

        // Y-PALETTE (spec §5.11.46), moved to run here -- real libaom's own av1_rd_pick_intra_sby_mode order
        // (intra_mode_search.c:1490-1785, confirmed by direct source reading) is plain modes -> palette ->
        // filter_intra, in that order, with av1_rd_pick_palette_intra_sby (:1708-1714) tightening best_rd BY
        // POINTER before filter_intra's own beat_best_rd gate ever reads it -- this project's own palette
        // search used to run much later (after IntraBC and chroma), a real, disclosed ordering divergence
        // from round N+23's own Phase 3 research, now closed. Unlike this project's own previous gate
        // (`!usedIntrabc && paletteStructurallyPresent`), real libaom's own palette search has no concept of
        // IntraBC at this point at all -- IntraBC is an entirely separate, later top-level RD comparison in
        // real libaom (rd_pick_intrabc_mode_sb, compared against the FULL resolved Y+UV(+palette) cost, see
        // that function's own real call site) -- so this search now runs unconditionally on
        // paletteStructurallyPresent alone, exactly like real libaom's own unconditional call. `usedIntrabc`
        // itself isn't known yet at this point in this project's own still-unrestructured control flow (that
        // remaining IntraBC-vs-chroma ordering divergence is its own separate, larger item -- see the project
        // plan), so the actual `!usedIntrabc` gate is applied later, at usedPalette's own real computation
        // below, which reuses yPaletteEligible/nY/yAllZero from this one search rather than re-running it.
        //
        // bestCost is tightened here exactly like libaom's own real best_rd pointer-tightening, so
        // filter_intra's own subsequent bound (and, after it, IntraBC's own bestCost-based approximate-match
        // search) correctly reflects palette as a real, already-resolved alternative -- matching libaom's own
        // real search order's actual effect, not just its call order.
        //
        // Y-palette is tried regardless of which mode won the raw residual-only comparison above --
        // deliberately NOT gated on bestMode == DC_PRED. Spec's own palette-eligibility gate (has_palette_y
        // is only ever read when y_mode == DC_PRED) is a constraint on what gets WRITTEN, not on which
        // candidate is allowed to reach this point: choosing palette means writing y_mode = DC_PRED as an
        // override, exactly like usedIntrabc already overrides the written y_mode unconditionally (see
        // writtenYMode below) -- it never requires DC_PRED to have first won an unrelated comparison. A
        // genuinely flat (single-value) leaf's SSE-optimal mode is DC_PRED anyway, but a *periodic*,
        // few-distinct-color leaf (a checkerboard, screen-content graphics) is exactly the case this
        // mattered for: DC_PRED's flat single-value prediction genuinely produces the largest raw residual
        // of any candidate for such content, so directional/PAETH modes routinely won the earlier loop on
        // raw residual cost alone -- silently making palette structurally unreachable for that leaf even
        // though palette's own real cost (an exact per-pixel index map, no prediction-quality dependence at
        // all) could be an order of magnitude cheaper. SearchLosslessYPalette's own RD gate (bestRdSoFar)
        // already safely rejects palette whenever it doesn't actually win, so trying it unconditionally
        // costs nothing but a wasted search when it doesn't apply.
        int nY = 0;
        bool yAllZero = false;
        long yPaletteCost = long.MaxValue;
        // Search bound: bestCost directly for lossless (already the real, exact bit cost there -- no
        // separate quantization stage to under-estimate it, see realLumaCost's own remarks below). For
        // non-lossless, bestCost at this point is only the cheap, pre-quantization SSE+lambda*bits proxy
        // from the plain-mode loop above -- the real, post-quantization cost doesn't exist yet (it's only
        // computed later, by the tx-size-RDO block) -- so bounding the search by it here would reject a
        // genuinely winning palette candidate simply for not beating an artificially cheap number, exactly
        // the same class of bug already found and fixed for the IntraBC decision (see realLumaCost's own
        // remarks). Use an unbounded search instead and defer the real accept/reject decision to after
        // realLumaCost is known (see its own use below).
        long yPaletteSearchBound = s.Lossless ? bestCost : long.MaxValue;
        bool yPaletteEligible = paletteSearchEnabled && SearchLosslessYPalette(s, x, y, sizePixels, sizePixels, yPaletteSearchBound, r, c, availU, availL, out nY, out yAllZero, out yPaletteCost);
        if (yPaletteEligible && yPaletteCost < bestCost)
        {
            bestCost = yPaletteCost;
        }

        // FILTER_INTRA (spec §5.11.24): real libaom (av1_rd_pick_intra_sby_mode, intra_mode_search.c) tries
        // this as a genuine competing candidate for every bsize-eligible leaf once *any* plain mode has beaten
        // the search's initial RD bound -- regardless of *which* plain mode currently holds bestMode -- and
        // overwrites the plain-mode winner outright if a filter_intra submode comes out ahead (mode becomes
        // DC_PRED with use_filter_intra=1, since filter_intra_mode_info() is only ever read when
        // YMode == DC_PRED -- that's a *bitstream signaling* restriction, not a search-entry precondition, and
        // this used to conflate the two by gating entry on bestMode == DcPred already, which silently excluded
        // every leaf where a directional/smooth/paeth mode merely edged out DC_PRED in the plain-mode loop).
        // Also gated on PaletteSizeY == 0 per spec -- deferred until usedPalette is known (see the actual
        // use_filter_intra write site) since this encoder always ties Y and UV palette eligibility together
        // (see usedPalette's own remarks), so it isn't needed for the search itself.
        //
        // PruneFilterIntraLevel (libaom's own prune_filter_intra_level, av1/encoder/intra_mode_search.c):
        // level 2 skips filter_intra entirely; level 1 restricts the 5 sub-modes tried to
        // FilterIntraModeUsedFlag[bestMode]'s bitmask, keyed off whatever plain mode is winning so far (used
        // only to pick the submode mask, same as libaom's own best_mode_so_far -- not a precondition on it).
        bool skipFilterIntraEntirely = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 2;
        int filterIntraModeMask = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 1 ? FilterIntraModeUsedFlag[bestMode] : 0x1F;

        bool bestUseFilterIntra = false;
        int bestFilterIntraMode = 0;
        if (sizePixels <= 32 && !skipFilterIntraEntirely)
        {
            for (int filterMode = 0; filterMode < 5; filterMode++)
            {
                if ((filterIntraModeMask & (1 << filterMode)) == 0)
                {
                    continue;
                }

                // model_intra_yrd_and_prune (intra_mode_search_utils.h:671-686, called from
                // rd_pick_filter_intra_sby, intra_mode_search.c:275): a distinct, simpler single-threshold
                // sibling of prune_intra_y_mode (the plain-mode loop's own PruneIntraYMode call above) --
                // prunes whenever this submode's model rd exceeds 1.25x the SAME running bestModelRd the
                // plain-mode loop already tracked (libaom's own best_model_rd is one value threaded across
                // both loops, intra_mode_search.c:1610 -> :1677), not the top-K array PruneIntraYMode uses.
                // Unconditional at PruneFilterIntraLevel 1 (that level only trims which *submodes* are tried,
                // via filterIntraModeMask above; it does not disable this separate prune). Missing this let
                // every unmasked submode's real residual cost win comparisons libaom's own search would have
                // rejected before ever costing them for real -- confirmed via a direct real-image measurement
                // (FilterIntra area overshooting aomenc's own 9.68% by 1.5x, 14.81%, directionally: 3,200
                // leaves winning filter_intra where aomenc doesn't, vs. only 1,716 the other way) once the
                // search-entry gate above was fixed to stop conflating "not DC_PRED yet" with "not eligible".
                if (topModelCount > 0)
                {
                    long filterModelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, sizePixels, ptype: 0, mode: Av1IntraMode.DcPred, angleDelta: 0, useFilterIntra: true, filterIntraMode: filterMode, filterTypeSmooth, useRealBoundaryAvailability: true);
                    if (Av1IntraModelRdPruner.PruneFilterIntraModelRd(filterModelRd, ref bestModelRd))
                    {
                        continue;
                    }
                }

                // Real use_filter_intra + filter_intra_mode signaling cost -- the same missing-signaling-cost
                // gap fixed for y_mode/angle_delta above, applied here too: previously a filter_intra
                // candidate was compared purely on residual cost against bestCost (which, for the plain
                // DC_PRED baseline it's replacing, never had to pay this leaf's own use_filter_intra bit
                // either), so filter_intra could win a close comparison based only on a marginally smaller
                // residual while ignoring its own real extra signaling cost. Deliberately one-sided (doesn't
                // also charge the DC_PRED baseline for its own "use_filter_intra = 0" bit, since usedPalette
                // -- which can retroactively make this bit not written at all -- isn't known yet at this
                // point in the search): a real, if incomplete, improvement over charging filter_intra nothing
                // at all, and safe either way since it only ever makes filter_intra a harder sell, never
                // artificially cheaper. Accumulated in raw units alongside the residual cost below (see this
                // method's own plain-mode-loop precision-propagation remarks), rounded once at the very end.
                // Computed BEFORE the residual call below (moved up, same pure-reordering pattern as the
                // plain mode/angle-delta loop's own Phase 3 change) so it's available to derive
                // filterBoundUnits512.
                long filterSignalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntra[bSize], 1) + Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntraMode, filterMode);

                // Phase 3's own bound-threading (project plan), same exact derivation as the plain mode/
                // angle-delta loop's own identical comment above -- see ComputeCandidateCostPrecise's remarks
                // for the general mechanism and EncodeLeaf's own plain-loop call site for the full rounding-
                // identity derivation this reuses verbatim, just against this candidate's own
                // filterSignalingUnits512 in place of the plain loop's signalingUnits512.
                long filterBoundUnits512 = bestCost == long.MaxValue ? long.MaxValue : (bestCost * 512) - 256 - filterSignalingUnits512;

                // Same per-4x4-sub-block generalization as the plain mode/angle-delta loop above -- see its
                // own remarks (Round N+40/N+41). filterIntraMode's own submode search never reaches the
                // 128x128-only oversized case (this whole block is gated sizePixels <= 32), so this branch
                // brings filter_intra's own real mode selection onto the same accurate primitive the plain-
                // mode loop above now uses uniformly, for the identical reason: EncodeLosslessLumaResidual's
                // own real write always re-predicts fresh per sub-block regardless of which candidate wins.
                long filterResidualUnits512;
                if (s.Lossless)
                {
                    ComputeLosslessWholeLeafCostPerSubBlockPrecise(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), mode: Av1IntraMode.DcPred, angleDelta: 0, useFilterIntra: true, filterIntraMode: filterMode, filterTypeSmooth, useRealBoundaryAvailability: true, out filterResidualUnits512, boundUnits512: filterBoundUnits512);
                }
                else
                {
                    Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                    Av1IntraPrediction.Predict(pred, sizePixels, sizePixels, log2Size, log2Size, above, left, Av1IntraMode.DcPred, availL, availU, useFilterIntra: true, filterIntraMode: filterMode, angleDelta: 0, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, x, y, bitDepth: 8);
                    ComputeCandidateCostPrecise(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s), out filterResidualUnits512, filterBoundUnits512);
                }

                long cost = (long)Math.Round((filterResidualUnits512 + filterSignalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = Av1IntraMode.DcPred;
                    bestAngleDelta = 0;
                    bestUseFilterIntra = true;
                    bestFilterIntraMode = filterMode;
                    Array.Copy(pred, bestPred, leafElements);
                }
            }
        }

        bool hasChroma = !s.MonoChrome;

        // paletteStructurallyPresent is now computed earlier (before the FILTER_INTRA loop above), so
        // Y-palette can run before it -- see that computation's own remarks.

        // Whether IntraBC's own use_intrabc bit is structurally present -- real decoder gate
        // (Av1TileDecoder.IntraFrameModeInfo: `_frame.AllowIntrabc && ReadSymbol(...)`) has no lossless check
        // at all, so this must not have one either: getting it wrong desyncs every bit after it, the same
        // class of bug paletteBitsPresent's own remarks (above) describe for palette. Non-lossless IntraBC is
        // now supported, exact-match only (skip=1 leaves, see intrabcApproxCandidate's own lossless gate
        // below) -- the decoder's own matching restriction lives in Av1TileDecoder.IntraFrameModeInfo's
        // `!_lossless && !_skip` throw guard.
        bool intrabcStructurallyPresent = s.AllowIntrabc;

        // Trial-search for an exact-match IntraBC copy source *before* writing anything -- like palette,
        // skip's value depends on whether IntraBC will end up used (see FindIntrabcMatch's remarks), and
        // unlike palette, using IntraBC also means the entire yMode/uv_mode/palette signaling this leaf
        // would otherwise carry is skipped altogether (spec's use_intrabc branch replaces it, not layers on
        // top of it) -- so this has to be decided before the mode search's result is committed to the
        // bitstream at all, not just before the skip bit.
        // intrabcExactCandidate/intrabcApproxCandidate are cheap-search RESULTS (a candidate MV was found by
        // FindIntrabcMatch/FindApproximateIntrabcMatch's own proxy-based search/prefilter, unchanged from
        // before) -- NOT yet a final decision. Project plan Phase 3's own IntraBC-vs-chroma ordering fix:
        // real libaom's own rd_pick_intrabc_mode_sb compares a genuine, complete RD cost against the fully
        // resolved Y+UV(+palette) total, evaluated once both are known -- this project's own previous
        // architecture instead finalized `usedIntrabc` right here, before chroma/palette even ran, using it
        // to skip them outright. The real, final decision (a new `usedIntrabc`, computed after chroma/
        // palette resolve below) now lives further down, once bestCost/bestUvCost are both genuinely
        // complete -- see that computation's own remarks for the full rationale, including why the naive
        // "just widen FindApproximateIntrabcMatch's own bound" fix is unsound (round N+28's own finding).
        int intrabcMvRow = 0;
        int intrabcMvCol = 0;
        bool intrabcExactCandidate = intrabcStructurallyPresent && FindIntrabcMatch(s, r, c, sizeMi, hasChroma, out intrabcMvRow, out intrabcMvCol);

        // Phase D technique 5: when no byte-exact copy source exists, fall back to a bounded approximate
        // search -- unlike the exact-match path, this leaf still carries a real WHT-coded residual on top
        // of the block-copy prediction (skip = 0), which the decoder already supports unconditionally (see
        // FindApproximateIntrabcMatch's remarks). Only tried when it wasn't already an exact match and the
        // real intra search above found something to beat.
        //
        // This used to be gated to sizeMi <= 2 (single-sub-block leaves only): EncodeIntrabcResidual
        // predicted a merged coding block's whole region in one Av1InterPrediction.PredictIntrabc call and
        // then sliced the result per 4x4 sub-block, which could desync from a real decoder's transform_block()
        // (spec §5.11.35, which calls PredictIntrabc fresh per sub-block from progressively-reconstructed
        // state) for a genuinely multi-sub-block IntraBC block. EncodeIntrabcResidual now predicts per
        // sub-block the same way -- see its remarks for detail. IntraBC's *exact*-match path
        // (intrabcExactCandidate above) never had this problem, since it has no per-sub-block prediction
        // step at all.
        //
        // s.Lossless && ...: non-lossless IntraBC support (project plan Phase 4) is exact-match only -- an
        // approximate match still needs a real WHT/quantize/entropy residual trial (EncodeIntrabcResidual),
        // and the decoder's own IntraBC support for a non-lossless segment only implements the skip=1 path
        // (Av1TileDecoder.IntraFrameModeInfo's own `!_lossless && !_skip` throw guard; the variable-transform-
        // partition-tree and inter tx_type machinery a residual-bearing non-lossless IntraBC block would need
        // are real, separately-scoped, unimplemented decoder work -- see this project's own IntraBC-for-
        // non-lossless audit). Gating the search itself (rather than only the eventual write) means this
        // encoder never even considers a candidate its own paired decoder couldn't read back.
        int approxMvRow = 0;
        int approxMvCol = 0;
        bool intrabcApproxCandidate = s.Lossless && !intrabcExactCandidate && intrabcStructurallyPresent
            && FindApproximateIntrabcMatch(s, r, c, sizeMi, bSize, bestCost, out approxMvRow, out approxMvCol);
        if (intrabcApproxCandidate)
        {
            intrabcMvRow = approxMvRow;
            intrabcMvCol = approxMvCol;
        }

        // Real cost-based uv_mode/uv_angle_delta search (see SearchUvMode's remarks) -- replaces the old
        // hardcoded DC_PRED, for both lossless and non-lossless chroma, at every leaf size. Non-lossless
        // chroma's transform type is mode-dependent (spec's Mode_To_Txfm, Av1TxTypeTables.ModeToTxfm --
        // Av1TileDecoder.ComputeTxType derives ADST/mixed transforms for every uv_mode except DC_PRED), which
        // used to make a real search unsafe here whenever the chroma region grew past one 4x4 transform
        // (sizeMi > 2 -- a 16x16/32x32 luma leaf's 4:2:0 chroma region, 8x8/16x16): Av1ForwardTransform's
        // forward ADST operators only existed at size 4 until this encoder's tx_type search phase generalized
        // them to 4/8/16 (every chroma region size this encoder ever produces -- 4:2:0's largest, from a
        // 32x32 luma leaf, is 16x16), so any uv_mode whose ModeToTxfm entry wasn't DCT_DCT would have had
        // nothing to forward-transform with at those larger sizes. Every ModeToTxfm entry is one of
        // DCT_DCT/AdstDct/DctAdst/AdstAdst (never IDTX or a 1D-only type), so once those operators covered
        // every real chroma size, nothing about the search itself needed to change to become safe at every
        // size -- see EncodeChromaRegion's own remarks for the write-side half of this. Lossless never had
        // this constraint: AV1 forces TX_4X4/WHT unconditionally at coded-lossless regardless of prediction
        // mode (ComputeTxType's own lossless short-circuit), so uv_mode has no bearing on transform choice
        // there.
        //
        // Still skipped when neither neighbor is available (the frame's very first leaf) or !hasChroma/
        // usedIntrabc (spec's use_intrabc branch replaces yMode/uv_mode signaling entirely -- see the
        // intrabc branch below, which leaves uv_mode at its DC_PRED default instead). With no real edge
        // data on either side, every candidate predicts from the same synthetic default fill, so a
        // directional mode/angle_delta search there is pure overhead with no possible benefit -- lossless
        // still reconstructs bit-exactly regardless (residual always corrects the rest of the way), but
        // choosing DC_PRED avoids gambling extra angle_delta signaling bits on a block with no real content
        // to base a directional choice on.
        // Non-lossless luma's real transform/quantize/trellis/reconstruct, computed here -- before
        // SearchUvMode runs, not at this leaf's later bitstream-order commit position below -- so CFL's
        // search (TryCflCandidate) has this leaf's own real reconstructed luma to read from
        // TileState.ReconY, matching what a real decoder will actually have available by the equivalent
        // point in its own decode. uv_mode/cfl_alphas are decided (and, per spec, their bitstream position
        // precedes) before luma's own residual is ever written, but nothing requires the ENCODER's internal
        // pixel reconstruction to wait that long too -- only WriteCoeffs (the actual bitstream symbol write,
        // which must stay at its original position to preserve mode_info()-before-residual() ordering) is
        // deferred; TileState.LumaLevels and bestTxType (declared here, at a scope enclosing both this block
        // and the later commit site) carry the result forward for that deferred write to reuse instead of
        // recomputing.
        //
        // Always run when reached (not conditioned on whether this leaf's Y palette will end up used --
        // that isn't decided until after this point, see the palette eligibility checks below), so it can't
        // avoid the tx_type search/transform/quantize/trellis cost purely by knowing palette will win --
        // real, if occasionally wasted, work is far simpler and safer than also hoisting palette's own
        // decision earlier just to skip it. A leaf whose Y palette does win later overwrites TileState.ReconY
        // with the palette's own (exact, source-copied) reconstruction instead -- see that branch, unchanged,
        // below.
        int bestTxType = Av1TxType.DctDct;

        // chosenTxDepth/subTxTypes: this leaf's own real tx-size-RDO decision (project plan Phase 4), read
        // back by the later, deferred commit-write section below -- declared at this outer scope (like
        // bestTxType itself, just above) rather than inside the `if (!s.Lossless)` block that actually
        // computes them, for the exact same reason: the real WriteTxDepth/WriteCoeffs calls happen much
        // later in this same method, after mode_info()'s own bitstream position, per spec ordering (see
        // that commit site's own remarks). 0/DctDct are safe, inert defaults for lossless (never read) and
        // for a genuine 4x4 leaf (tx_depth is never signalled there at all, see WriteTxDepth's own remarks).
        int chosenTxDepth = 0;
        Span<int> subTxTypes = stackalloc int[4];

        // realLumaCost: the genuine, post-quantization luma cost for this leaf, used ONLY by the IntraBC
        // decision below (intraAlternativeTotal) -- NOT a general replacement for bestCost, which keeps its
        // own existing meaning (a cheap, pre-quantization SSE+lambda*bits proxy) for every other comparison
        // in this method (palette, filter_intra). For lossless, bestCost already IS the real, exact bit cost
        // (WHT reconstruction has no separate quantization stage to under-estimate), so this is simply
        // bestCost there. For non-lossless, the real cost only exists once the tx-size-RDO block below
        // resolves costA/costB (real transform+quantize+entropy costs, unlike bestCost's own pre-quantization
        // model) -- initialized here to bestCost as a placeholder, overwritten with the real
        // Math.Min(costA, costB) once that block runs. Getting this wrong (leaving it as the stale,
        // pre-quantization bestCost) was a real, confirmed bug found via a real round-trip test: it silently
        // makes IntraBC's own real MV-signaling-only cost look artificially expensive by comparison against
        // an alternative whose own cost was never actually priced at its real, post-quantization value --
        // this was invisible before non-lossless IntraBC existed (bestCost's non-lossless proxy nature was
        // never actually compared against anything real), but would otherwise make IntraBC structurally
        // unable to ever win for non-lossless content, regardless of how well it would have actually
        // compressed.
        long realLumaCost = bestCost;

        // Condition is `!s.Lossless` alone, not `!s.Lossless && !usedIntrabc`: `usedIntrabc`'s own real,
        // final value isn't known yet this early (it's decided later, once bestUvCost is also known -- see
        // that computation's own remarks), so this speculative tx-size-RDO search runs unconditionally
        // whenever !s.Lossless, even for a leaf that will end up choosing IntraBC instead. This used to be a
        // provably dead case (IntraBC was only ever structurally present under lossless, so !s.Lossless
        // always implied usedIntrabc could never become true) -- now that non-lossless IntraBC exists
        // (exact-match only, see intrabcStructurallyPresent's own remarks), a leaf whose real IntraBC exact
        // match wins still runs this whole block first, purely wastefully: chosenTxDepth/bestTxType/
        // s.Residual end up computed and then silently discarded, since IntraBC's own commit path (skip=1,
        // straight source-to-recon copy) never reads any of them. Wasted compute only, not a correctness
        // issue -- an IntraBC-winning leaf's real committed bitstream content never depends on what this
        // block computed, and ordinary RD search architectures (including real libaom's own) routinely
        // compute and discard a losing candidate's own state this way. Left as a known, disclosed
        // inefficiency rather than an early-exit optimization, since an early exit here would need to predict
        // whether IntraBC will actually win before chroma's own cost is known -- not available this early.
        if (!s.Lossless)
        {
            int[] earlyResidual = s.Residual;
            int leafElementCount = sizePixels * sizePixels;
            long earlySse = 0;
            for (int i = 0; i < leafElementCount; i++)
            {
                int diff = s.SourceY[((y + (i / sizePixels)) * s.YWidth) + x + (i % sizePixels)] - bestPred[i];
                earlyResidual[i] = diff;
                earlySse += (long)diff * diff;
            }

            // Candidate A (tx_depth 0, no split -- this leaf's own natural tx size): the search this
            // method already had, just renamed costA/bestTxType to make room for candidate B below.
            long costA;
            if (sizePixels < 32)
            {
                // get_tx_set() (spec §5.11.48, mirrored from Av1TileDecoder.GetTxSet exactly): TX_16X16
                // always gets the reduced (5-symbol) intra set regardless of ReducedTxSet -- a real spec
                // restriction (TX_SET_INTRA_1 is only ever legal at TX_4X4/TX_8X8), not a reduced_tx_set
                // effect -- so only TX_4X4/TX_8X8 (sizePixels 4 or 8) actually widen to the full 7-symbol
                // Av1TxTypeTables.TxTypeIntraInvSet1 when !s.ReducedTxSet (see TileState.ReducedTxSet's own
                // remarks for why that's now real aomenc's own observed default).
                bool useFullTxSet = !s.ReducedTxSet && sizePixels != 16;
                int[] txTypeCandidates = useFullTxSet ? Av1TxTypeTables.TxTypeIntraInvSet1 : Av1TxTypeTables.TxTypeIntraInvSet2;

                long bestTxTypeCost = long.MaxValue;
                foreach (int candidateTxType in txTypeCandidates)
                {
                    long txTypeCost = ComputeTxTypeCost(s, earlyResidual, earlySse, sizePixels, ptype: 0, x, y, LiveYCoeffCtx(s), candidateTxType);
                    if (txTypeCost < bestTxTypeCost)
                    {
                        bestTxTypeCost = txTypeCost;
                        bestTxType = candidateTxType;
                    }
                }

                costA = bestTxTypeCost;
            }
            else
            {
                // sizePixels == 32: TX_SET_DCTONLY forces DctDct with no real search (see the tx-type
                // symbol-write remarks below) -- costA still needs computing directly (not skipped) so the
                // tx-depth comparison against candidate B remains a real, comparable cost.
                costA = ComputeTxTypeCost(s, earlyResidual, earlySse, sizePixels, ptype: 0, x, y, LiveYCoeffCtx(s), Av1TxType.DctDct);
            }

            // Candidate B (tx_depth 1, one uniform quad-split down -- project plan Phase 4's own
            // transform-size RDO, the real per-leaf search libaom's own choose_tx_size_type_from_rd
            // performs at real aomenc's tested cpu-used=2 all-intra settings: confirmed directly from
            // libaom source that TX_MODE_SELECT applies there and the real search only ever evaluates
            // exactly two uniform candidates -- this leaf's own natural size, and one recursive split down
            // -- before its own speed-feature-driven early exit, never a deeper multi-level recursive
            // quadtree the way inter content's own var-tx search works). Only reachable for sizePixels >= 8
            // (a genuine 4x4 leaf has no smaller uniform tx size to split into, matching real spec's own
            // MiSize > BLOCK_4X4 gate on whether tx_depth is even signalled at all).
            //
            // Each sub-block's own real tx-type search reuses ComputeTxTypeCost exactly like candidate A's
            // own does -- real per-transform-block costing, matching libaom's own uniform_txfm_yrd. The one
            // real, disclosed approximation here: each sub-block's trial residual is still measured against
            // bestPred's own whole-leaf-sized prediction (a quarter-slice of it), not a fresh, progressively
            // re-predicted per-sub-block prediction reading this leaf's OWN earlier-committed sub-blocks as
            // neighbor context -- real AV1 intra prediction is genuinely per-transform-block, so a real,
            // maximally-accurate comparison would re-predict here too. This mirrors the same class of
            // decision-phase-estimate-vs-commit-phase-real approximation this file's own Estimate* family
            // already uses throughout (see their own remarks) -- it only needs to get the WINNING candidate
            // right often enough to be a real net improvement, not compute a bit-exact cost for the
            // candidate that loses; the actual COMMITTED reconstruction below is always fully real,
            // regardless of which candidate wins.
            long costB = long.MaxValue;
            int halfSize = sizePixels / 2;
            if (sizePixels >= 8)
            {
                costB = 0;
                for (int k = 0; k < 4; k++)
                {
                    int dr = k / 2;
                    int dc = k % 2;
                    int subX = dc * halfSize;
                    int subY = dr * halfSize;

                    // CflLumaAc, not s.Residual: candidate A's own real commit (below, once the depth
                    // decision is made) reads earlyResidual -- which IS s.Residual, aliased at this
                    // method's own top -- so this trial loop must not clobber it while estimating candidate
                    // B's cost. CflLumaAc is timing-safe scratch here for the exact same reason TileState's
                    // own declaration of it already documents (kept separate from Residual so a later,
                    // different trial loop -- CFL's own -- can't clobber a still-needed buffer); CFL's own
                    // real use of it happens later still, during SearchUvMode, well after this leaf's real
                    // luma commit (below) has already consumed the real earlyResidual it needs.
                    int[] subResidual = s.CflLumaAc;
                    long subSse = 0;
                    for (int i = 0; i < halfSize * halfSize; i++)
                    {
                        int px = subX + (i % halfSize);
                        int py = subY + (i / halfSize);
                        int diff = s.SourceY[((y + py) * s.YWidth) + x + px] - bestPred[(py * sizePixels) + px];
                        subResidual[i] = diff;
                        subSse += (long)diff * diff;
                    }

                    bool useFullTxSetSub = !s.ReducedTxSet && halfSize != 16;
                    int[] subTxTypeCandidates = halfSize < 32
                        ? (useFullTxSetSub ? Av1TxTypeTables.TxTypeIntraInvSet1 : Av1TxTypeTables.TxTypeIntraInvSet2)
                        : [Av1TxType.DctDct];

                    long bestSubCost = long.MaxValue;
                    int bestSubTxType = Av1TxType.DctDct;
                    foreach (int candidateTxType in subTxTypeCandidates)
                    {
                        long subCost = ComputeTxTypeCost(s, subResidual, subSse, halfSize, ptype: 0, x + subX, y + subY, LiveYCoeffCtx(s), candidateTxType);
                        if (subCost < bestSubCost)
                        {
                            bestSubCost = subCost;
                            bestSubTxType = candidateTxType;
                        }
                    }

                    subTxTypes[k] = bestSubTxType;
                    costB += bestSubCost;
                }
            }

            chosenTxDepth = costB < costA ? 1 : 0;
            realLumaCost = Math.Min(costA, costB);

            if (chosenTxDepth == 0)
            {
                int[] earlyCoeff = s.Coeff;
                Av1ForwardTransform.Forward2D(earlyResidual, earlyCoeff, sizePixels, bestTxType);
                Av1ForwardQuantizer.Quantize(earlyCoeff, s.LumaLevels, sizePixels, s.BaseQIdx);
                OptimizeCoeffTrellis(s, earlyCoeff, s.LumaLevels, sizePixels, ptype: 0, c, r, LiveYCoeffCtx(s), txType: bestTxType);

                // Write the prediction into the reconstruction buffer before Reconstruct() adds the
                // residual -- matches Av1TileDecoder's own predict-then-reconstruct-in-place ordering.
                for (int i = 0; i < sizePixels; i++)
                {
                    Array.Copy(bestPred, i * sizePixels, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
                }

                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, x, y, sizePixels, s.LumaLevels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: false, bestTxType);
            }
            else
            {
                // Real, progressive per-sub-block predict-then-reconstruct -- unlike candidate B's own
                // decision-phase cost estimate above, this is the actual committed data, so it uses a real,
                // fresh per-sub-block prediction (not a bestPred slice), reading this same leaf's own
                // already-reconstructed earlier sub-blocks as neighbor context exactly like
                // EncodeLosslessLumaResidual's own established pattern (generalized here from a fixed 4x4
                // WHT sub-block to a real halfSize DCT/ADST one).
                int halfSizeMi = halfSize / 4;
                int halfArea = halfSize * halfSize;
                for (int k = 0; k < 4; k++)
                {
                    int dr = k / 2;
                    int dc = k % 2;
                    int subX = x + (dc * halfSize);
                    int subY = y + (dr * halfSize);
                    int subR = r + (dr * halfSizeMi);
                    int subC = c + (dc * halfSizeMi);

                    bool subAvailU = subR > 0;
                    bool subAvailL = subC > 0;

                    int subMiRow = subR & s.SbMiMask;
                    int subMiCol = subC & s.SbMiMask;
                    bool subHaveAboveRight = GetBlockDecoded(s, 0, subMiRow - 1, subMiCol + halfSizeMi);
                    bool subHaveBelowLeft = GetBlockDecoded(s, 0, subMiRow + halfSizeMi, subMiCol - 1);

                    Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, subX, subY, halfSize, halfSize, subAvailL, subAvailU, subHaveAboveRight, subHaveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);

                    var subPred = s.Pred;
                    int subLog2 = Log2FromPixels(halfSize);
                    Av1IntraPrediction.Predict(subPred, halfSize, halfSize, subLog2, subLog2, above, left, bestMode, subAvailL, subAvailU, bestUseFilterIntra, bestFilterIntraMode, bestAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, subX, subY, bitDepth: 8);

                    var subResidualCommit = s.Residual;
                    for (int i = 0; i < halfArea; i++)
                    {
                        subResidualCommit[i] = s.SourceY[((subY + (i / halfSize)) * s.YWidth) + subX + (i % halfSize)] - subPred[i];
                    }

                    int subTxType = subTxTypes[k];
                    Av1ForwardTransform.Forward2D(subResidualCommit, s.Coeff, halfSize, subTxType);
                    Av1ForwardQuantizer.Quantize(s.Coeff, s.Levels, halfSize, s.BaseQIdx);
                    OptimizeCoeffTrellis(s, s.Coeff, s.Levels, halfSize, ptype: 0, subC, subR, LiveYCoeffCtx(s), txType: subTxType);
                    Array.Copy(s.Levels, 0, s.LumaLevels, k * halfArea, halfArea);

                    for (int i = 0; i < halfSize; i++)
                    {
                        Array.Copy(subPred, i * halfSize, s.ReconY, ((subY + i) * s.YWidth) + subX, halfSize);
                    }

                    Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, halfSize, s.Levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: false, subTxType);
                    SetBlockDecoded(s, 0, subMiRow, subMiCol, true);
                }
            }
        }

        int bestUvMode = Av1IntraMode.DcPred;
        int bestUvAngleDelta = 0;
        int bestAlphaU = 0;
        int bestAlphaV = 0;

        // Sentinel for "the search below never ran" -- see the approximate-palette RD gate below, which
        // treats this as "no real non-palette cost to compare against" rather than risking an overflowing
        // sum with a real bestCost. Previously also gated on (availU || availL), skipping the search entirely
        // (defaulting to DC_PRED) for the very first leaf(es) of a frame -- confirmed via this project's own
        // harness that this was a real, correctable gap, not a harmless optimization: BuildEdges already
        // handles "neither neighbor available" gracefully (spec's own fixed default edge values), the exact
        // same case the LUMA mode search (this method's own loop above) has never gated on, and real content
        // can genuinely have a non-DC_PRED optimum even with no real edge context yet (PAETH_PRED's nonlinear
        // combination of the same default edge values doesn't degenerate to DC_PRED's own result).
        // Unconditional whenever hasChroma -- real libaom always searches chroma (av1_rd_pick_intra_sbuv_mode)
        // independent of IntraBC, which is an entirely separate, later top-level RD comparison in its own
        // real architecture (rd_pick_intrabc_mode_sb). Previously gated on `!usedIntrabc` here, before that
        // decision even existed this early any more -- see the real IntraBC decision below for the full
        // rationale (project plan Phase 3's own IntraBC-vs-chroma ordering fix).
        long bestUvCost = long.MaxValue;
        if (hasChroma)
        {
            (bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV, bestUvCost) = SearchUvMode(s, r, c, x, y, sizeMi, availU, availL, bestMode);
        }

        // The real, deferred accept/reject decision for the Y-palette candidate found earlier (see
        // yPaletteSearchBound's own remarks): now that realLumaCost is known (computed by the tx-size-RDO
        // block above, which runs after the Y-palette search), reject a candidate that doesn't actually
        // beat the real, post-quantization luma alternative. Lossless never reaches here with a non-final
        // yPaletteEligible=true that this would need to revisit (bestCost there already was the real cost,
        // so the search itself already gated correctly), so this is a genuine no-op for lossless.
        if (!s.Lossless && yPaletteEligible && yPaletteCost >= realLumaCost)
        {
            yPaletteEligible = false;
        }

        // Trial-search this leaf's UV-palette *before* writing anything -- skip's value depends on whether
        // palette will end up used (skip = 1 only when it does, covering every plane at once; see the loop
        // below), so that decision has to be made up front, even though the actual has_palette_y/
        // has_palette_uv/colors/tokens aren't written until after yMode and uv_mode are (spec order).
        // Y-palette itself was already searched earlier (before FILTER_INTRA, see that search's own
        // remarks for why) -- yPaletteEligible/nY/yAllZero below are its own results, reused rather than
        // recomputed. Like Y-palette's own search, UV-palette's own search below is now unconditional on
        // paletteStructurallyPresent/hasChroma alone (not `!usedIntrabc`, not known yet) -- real libaom's
        // own search order has no such gate on palette's own *search* either, only on whether the result is
        // actually applied, which is what yPaletteEligibleFinal below (after the real IntraBC decision)
        // captures. bestUvCost is tightened here exactly like Y-palette's own bestCost tightening, so the
        // real IntraBC decision below compares against the true, fully-resolved chroma total.
        //
        // UV-palette eligibility is checked independently of Y's, matching spec's own independent gate
        // (palette_mode_info()'s UV branch only depends on uv_mode == DC_PRED) -- not gated on bestUvMode ==
        // DC_PRED either, same reasoning as Y-palette's own search. This encoder still only ever *uses*
        // palette all-or-nothing (both Y and, when hasChroma, UV must win their own real RD search, so it
        // can always pair the palette prediction with skip = 1), but the *bits establishing that* --
        // has_palette_y when eligible, has_palette_uv when eligible -- are structurally required regardless
        // of that all-or-nothing outcome, and must be written even on leaves that end up not using palette
        // at all.
        //
        // SearchLosslessYPalette/SearchLosslessUvPalette (real, multi-strategy ports of libaom's own
        // av1_rd_pick_palette_intra_sby/_sbuv, av1/encoder/palette.c) each gate internally against their own
        // plane's real non-palette cost (bestCost/bestUvCost) -- unlike libaom's own true per-plane
        // independence, this project's own all-or-nothing architecture (skip=1 needs every plane exact)
        // means a leaf where Y alone doesn't quite beat bestCost, but Y+UV jointly would have, is not found
        // by this combination -- a small, disclosed limitation of preserving that pre-existing invariant
        // rather than reworking it, not a regression from any prior session's own behavior (which had no
        // per-plane RD gate at all, just the same joint-comparison intent enforced after the fact).
        bool uvBasePaletteGate = paletteSearchEnabled && hasChroma;
        int nUv = 0;
        bool uvAllZero = false;
        long uvPaletteCost = long.MaxValue;

        // SearchLosslessUvPalette indexes s.SourceU/V directly at (x, y)/(w, h) against s.ChromaWidth's own
        // stride -- real chroma-plane coordinates/size, not luma's. Lossless is always Chroma444 (chroma ==
        // luma resolution), so passing the leaf's own luma x/y/sizePixels straight through was correct for
        // every call site this method ever had before project plan Phase 4 -- a real, disclosed bug once
        // non-lossless (commonly 4:2:0) reaches this same call, confirmed via a real crash the first time
        // this round attempted it (Av1PaletteSearch.CountColors), then audited end-to-end (see the round
        // log) to find and fix every other internal call site with the identical assumption, not just this
        // one. Real chroma-scaling, matching every other chroma call site in this class (e.g. SearchUvMode's
        // own identical cx/cy pattern) -- chromaX/chromaY/chromaSizePixels are reused below by this leaf's
        // own all-zero commit branch too, once palette wins.
        int chromaX = s.Chroma444 ? x : x / 2;
        int chromaY = s.Chroma444 ? y : y / 2;
        int chromaSizePixels = s.Chroma444 ? sizePixels : sizePixels / 2;

        // bsizeCtx: the real coding-block (luma) size context, NOT derived from the chroma-scaled
        // dimensions above -- see SearchLosslessUvPalette's own remarks for the real bug this fixes (a
        // second, distinct one from the chroma pixel-array scaling: PaletteUvSize's own CDF context is
        // spec-keyed on the luma block size, matching bSize, this leaf's own already-computed top-level
        // block size).
        int uvBsizeCtx = GetPaletteBsizeCtx(bSize);
        bool uvPaletteEligible = uvBasePaletteGate && SearchLosslessUvPalette(s, chromaX, chromaY, chromaSizePixels, chromaSizePixels, bestUvCost, r, c, availU, availL, usedYPalette: yPaletteEligible, uvBsizeCtx, out nUv, out uvAllZero, out uvPaletteCost);
        if (uvPaletteEligible && uvPaletteCost < bestUvCost)
        {
            bestUvCost = uvPaletteCost;
        }

        // skipCtx (spec's own skip_ctx, based purely on neighbor Skips[] state -- no dependency on this
        // leaf's own decision): hoisted here (was previously computed later, right before the real skip-bit
        // write) so the real IntraBC decision below -- which needs to price each alternative's own skip bit
        // -- can use it too, without duplicating the tiny computation.
        int skipCtx = 0;
        if (availU)
        {
            skipCtx += s.Skips[((r - 1) * s.MiCols) + c] ? 1 : 0;
        }

        if (availL)
        {
            skipCtx += s.Skips[(r * s.MiCols) + c - 1] ? 1 : 0;
        }

        // Real, final IntraBC decision (project plan Phase 3's own IntraBC-vs-chroma ordering fix, round
        // N+28's own research made precise here): real libaom's rd_pick_intrabc_mode_sb compares a genuine,
        // complete RD cost -- real signaling AND, for the approximate case, a real per-sub-block WHT/
        // quantize/entropy residual trial across every plane -- against the fully resolved Y+UV(+palette)
        // total, evaluated once both are actually known. This project's own previous architecture instead
        // decided `usedIntrabc` immediately after the cheap candidate search above, using a luma-only bound
        // and skipping chroma/palette outright once it won -- a real, disclosed divergence. The naive fix
        // (just widen FindApproximateIntrabcMatch's own bound to bestCost+bestUvCost) was found to be
        // UNSOUND: that search's own candidate scoring is a deliberate luma-only WHT proxy (see its own
        // remarks), so comparing it directly against a real Y+UV total would systematically under-select
        // IntraBC in a new way. The fix here instead keeps that proxy exactly as it was -- a legitimate cheap
        // prefilter for *finding* a candidate MV, the same "cheap model, real cost only for the survivor"
        // pattern this file already uses elsewhere (Av1IntraModelRdPruner) -- and adds a SEPARATE, real,
        // non-proxy cost computation for whichever single candidate the prefilter already found, used as the
        // actual, final, dimensionally-sound decision.
        //
        // intraAlternativeTotal is the real, complete cost of NOT using IntraBC: realLumaCost (luma -- the
        // real, post-quantization cost for non-lossless, since bestCost's own non-lossless value is only a
        // cheap pre-quantization proxy never otherwise compared against a real alternative like IntraBC's
        // own exact MV-signaling cost; already palette-tightened and exact for lossless, where the two
        // coincide) + bestUvCost (chroma, now also palette-tightened, only when hasChroma) + the skip bit
        // this leaf would really write in that case. paletteWouldWinIfNoIntrabc/paletteAllZeroIfNoIntrabc use
        // the RAW (not yet `!usedIntrabc`-narrowed) eligibility flags deliberately -- they ask "if IntraBC
        // does NOT win, would palette," which is exactly the hypothetical this comparison needs and has no
        // circular dependency on usedIntrabc itself.
        bool paletteWouldWinIfNoIntrabc = yPaletteEligible && (!hasChroma || uvPaletteEligible);
        bool paletteAllZeroIfNoIntrabc = paletteWouldWinIfNoIntrabc && yAllZero && (!hasChroma || uvAllZero);
        long intraAlternativeTotal = realLumaCost + (hasChroma ? bestUvCost : 0)
            + Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.Skip[skipCtx], paletteAllZeroIfNoIntrabc ? 1 : 0);

        var (predMvRow, predMvCol) = FindMvStackAndPredict(s, r, c, bSize);
        bool usedIntrabc;
        if (intrabcExactCandidate)
        {
            // Zero residual by construction (FindIntrabcMatch only ever returns a source already verified
            // byte-identical, luma and chroma alike -- see its own remarks), so the real total is pure,
            // exactly-computable signaling: no proxy, no approximation, a fully sound comparison already.
            long exactTotal = EstimateMvCost(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol)
                + Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.Skip[skipCtx], 1);
            usedIntrabc = exactTotal < intraAlternativeTotal;
        }
        else if (intrabcApproxCandidate)
        {
            // IsSourceFootprintWritten (checked at candidate-search time inside FindApproximateIntrabcMatch's
            // own callees) already guarantees this candidate's own source footprint is entirely OUTSIDE this
            // leaf's own not-yet-committed destination -- this leaf can never reference its own still-
            // undecided pixels -- so ComputeIntrabcRealResidualCost below safely reads TileState.ReconY/
            // ReconU/ReconV directly, real and unmutated, with no scratch/overlap handling needed at all.
            long knownSignaling = EstimateMvCost(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol)
                + Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.Skip[skipCtx], 0);

            // Phase 3's own bound-threading derivation (see ComputeCandidateCostPrecise's own remarks for the
            // general mechanism), applied here too: once the real residual trial's own accumulated cost
            // proves the combined total can no longer beat intraAlternativeTotal, abort early -- exact, not
            // approximate, for the identical reason every other Phase 3 bound derivation is.
            long residualBoundUnits512 = intraAlternativeTotal == long.MaxValue
                ? long.MaxValue
                : ((intraAlternativeTotal - knownSignaling) * 512) - 256;
            long residualCost = ComputeIntrabcRealResidualCost(s, x, y, intrabcMvRow, intrabcMvCol, hasChroma, sizePixels, residualBoundUnits512);
            usedIntrabc = residualCost != long.MaxValue && (knownSignaling + residualCost) < intraAlternativeTotal;
        }
        else
        {
            usedIntrabc = false;
        }

        // yPaletteEligibleFinal (not the raw yPaletteEligible) is what actually gets used from here on --
        // real eligibility, from the earlier search, narrowed by the now-real, final !usedIntrabc.
        bool yPaletteEligibleFinal = yPaletteEligible && !usedIntrabc;

        bool usedPalette = yPaletteEligibleFinal && (!hasChroma || uvPaletteEligible);
        bool paletteAllZeroResidual = usedPalette && yAllZero && (!hasChroma || uvAllZero);

        // Non-lossless palette stays exact-match-only (project plan Phase 4, same scoping non-lossless
        // IntraBC uses) -- a real, previously-approximate (residual-bearing) palette candidate would still
        // need this leaf's own committed reconstruction to differ from a plain WHT/DCT residual candidate's
        // in ways not yet fully re-audited after this round's own fixes; restricting to the exact-match case
        // (paletteAllZeroResidual) keeps this round's own scope bounded to what's already been re-verified.
        if (!s.Lossless && usedPalette && !paletteAllZeroResidual)
        {
            usedPalette = false;
            paletteAllZeroResidual = false;
        }

        // The actual y_mode/uv_mode values written to the bitstream: DC_PRED whenever palette overrides them
        // (matching spec's own requirement that has_palette_y/uv can only be 1 when y_mode/uv_mode ==
        // DC_PRED), the raw search winner otherwise. Every downstream symbol write below that used to read
        // bestMode/bestUvMode directly now reads these instead -- getting this wrong would desync a real
        // decoder, which derives its own context/CDF selection from whatever mode value actually got written,
        // not from this encoder's own internal, pre-palette-decision bestMode/bestUvMode.
        int writtenYMode = usedPalette ? Av1IntraMode.DcPred : bestMode;
        int writtenUvMode = usedPalette ? Av1IntraMode.DcPred : bestUvMode;

        int paletteSizeY = 0;
        int paletteSizeUV = 0;

        // skip: only ever true for a fully palette-covered leaf (every plane predicted exactly, nothing
        // left to correct -- see the block below) or a winning IntraBC-exact leaf (zero residual by
        // construction). skipCtx itself was already computed earlier (before the real IntraBC decision,
        // which needed it too -- see that computation's own remarks); `usedIntrabc && intrabcExactCandidate`
        // (not the bare candidate flag) is what actually matters here now that a found exact-match candidate
        // isn't an automatic, unconditional win any more.
        // Two-pass tile-encoder architecture, Stage 1b-ii -- see Av1EncodePhase's own remarks.
        if (s.Phase != Av1EncodePhase.Decide)
        {
            s.Symbols.WriteSymbol(s.Cdf.Skip[skipCtx], (paletteAllZeroResidual || (usedIntrabc && intrabcExactCandidate)) ? 1 : 0);
        }

        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks
        // (same rationale, applied to every mode-info symbol below).
        if (s.Phase != Av1EncodePhase.Emit)
        {
            s.DecideCommitSink.WriteSymbol(s.DecideCdf.Skip[skipCtx], (paletteAllZeroResidual || (usedIntrabc && intrabcExactCandidate)) ? 1 : 0);
        }

        // use_intrabc (spec §5.11.7): structurally present whenever this frame allows it (tied to lossless
        // -- see Av1FrameHeaderWriter), read/written unconditionally for every leaf regardless of outcome,
        // exactly like paletteStructurallyPresent's has_palette_y/has_palette_uv bits.
        if (intrabcStructurallyPresent)
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.Intrabc, usedIntrabc ? 1 : 0);
            }

            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.Intrabc, usedIntrabc ? 1 : 0);
            }
        }

        if (usedIntrabc)
        {
            // use_intrabc's branch (spec §5.11.7) completely replaces yMode/uv_mode/palette signaling --
            // find_mv_stack(0) + assign_mv(0)'s PredMv derivation must exactly match what a real decoder
            // independently computes (see FindMvStackAndPredict's remarks), so the diffMv this writes lands
            // on the same Mv the decoder reconstructs from PredMv + diffMv. predMvRow/predMvCol were already
            // computed earlier (needed by the real IntraBC decision above too) -- reused here, not
            // recomputed (FindMvStackAndPredict is a pure read of neighbor state, so this is just avoiding a
            // second identical call, not a behavior change).
            WriteMv(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol);

            if (intrabcExactCandidate)
            {
                // reset_block_context(bw4, bh4) (spec §5.11.5): this leaf is skip = 1 (an exact match has
                // zero residual by construction), so none of the coefficient-writing paths run.
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.YCoeffCtx.Reset(c, sizeMi, r, sizeMi);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same reset as the real one just above.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideYCoeffCtx.Reset(c, sizeMi, r, sizeMi);
                }

                // Reconstruction is exact by construction (FindIntrabcMatch only ever returns a source
                // whose pixels -- luma and, when hasChroma, chroma -- already verified byte-identical to
                // this leaf's own source), so copying straight from source is simpler than -- and produces
                // identical results to -- running the decoder's own block-copy/subpel-blend prediction here
                // too.
                for (int i = 0; i < sizePixels; i++)
                {
                    Array.Copy(s.SourceY, ((y + i) * s.YWidth) + x, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
                }

                if (hasChroma)
                {
                    // Real chroma-scaled coordinates, not luma's -- see paletteBitsPresent's own remarks
                    // above for the identical bug class (chroma-pixel-scale vs luma-pixel-scale conflation)
                    // this project's palette-for-lossy investigation already found and fixed at several other
                    // call sites; this one was never reachable until non-lossless (4:2:0) IntraBC existed,
                    // since lossless is always Chroma444.
                    int intrabcChromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
                    int intrabcChromaR4Base = s.Chroma444 ? r : r / 2;
                    int intrabcChromaC4Base = s.Chroma444 ? c : c / 2;
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        s.UCoeffCtx!.Reset(intrabcChromaC4Base, intrabcChromaN, intrabcChromaR4Base, intrabcChromaN);
                        s.VCoeffCtx!.Reset(intrabcChromaC4Base, intrabcChromaN, intrabcChromaR4Base, intrabcChromaN);
                    }

                    // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                    // remarks. Same resets as the real ones just above.
                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        s.DecideUCoeffCtx!.Reset(intrabcChromaC4Base, intrabcChromaN, intrabcChromaR4Base, intrabcChromaN);
                        s.DecideVCoeffCtx!.Reset(intrabcChromaC4Base, intrabcChromaN, intrabcChromaR4Base, intrabcChromaN);
                    }

                    int intrabcChromaX = s.Chroma444 ? x : x / 2;
                    int intrabcChromaY = s.Chroma444 ? y : y / 2;
                    int intrabcChromaSizePixels = intrabcChromaN * 4;
                    for (int i = 0; i < intrabcChromaSizePixels; i++)
                    {
                        int rowOffset = ((intrabcChromaY + i) * s.ChromaWidth) + intrabcChromaX;
                        Array.Copy(s.SourceU!, rowOffset, s.ReconU!, rowOffset, intrabcChromaSizePixels);
                        Array.Copy(s.SourceV!, rowOffset, s.ReconV!, rowOffset, intrabcChromaSizePixels);
                    }
                }
            }
            else
            {
                // Approximate match (skip = 0): the block-copy prediction isn't pixel-exact, so a real WHT
                // residual is coded on top, exactly like EncodeLosslessLumaResidual's intra case -- see
                // EncodeIntrabcResidual's remarks for why no decoder changes were needed for this.
                EncodeIntrabcResidual(s, r, c, x, y, intrabcMvRow, intrabcMvCol, hasChroma, sizePixels);
            }

            MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
            if (hasChroma)
            {
                MarkChromaBlockDecoded(s, r, c, sizeMi);
            }
        }
        else
        {
        if (s.Phase != Av1EncodePhase.Decide)
        {
            s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], writtenYMode);
        }

        if (s.Phase != Av1EncodePhase.Emit)
        {
            s.DecideCommitSink.WriteSymbol(s.DecideCdf.IntraFrameYMode[yModeCtx0][yModeCtx1], writtenYMode);
        }

        // intra_angle_info_y() (spec §5.11.42): structurally present only when this leaf's size is >= 8x8
        // (Av1TileDecoder.IntraAngleInfoY's own _miSize >= Block8x8 gate) -- no longer always true now that
        // the partition floor reaches 4x4 (sizeMi == 1), so this has to check for real; the search above
        // already never picks a nonzero bestAngleDelta for such a leaf (angleDeltaAllowed), so this is a
        // structural-presence match, not a search-quality change. Gated on writtenYMode, not bestMode: never
        // fires when palette overrides y_mode to DC_PRED (never directional), regardless of what the raw
        // search's own bestMode was.
        if (sizeMi >= 2 && Av1IntraMode.IsDirectional(writtenYMode))
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.AngleDelta[writtenYMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
            }

            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.AngleDelta[writtenYMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
            }
        }

        if (hasChroma)
        {
            // uv_mode is always signalled when hasChroma, now a real cost-searched mode (see
            // SearchUvMode) instead of hardcoded DC_PRED -- but which CDF table depends on cflAllowed
            // (spec §8.3.2, mirroring Av1TileDecoder.ReadUvMode exactly): non-lossless, this encoder's
            // leaf (always 8x8, forced -- see EncodePartitionForced) always has cflAllowed == true (block
            // size <= 32). Lossless is size-dependent instead: GetPlaneResidualSize(bSize, plane:1, ...)
            // is Block4x4 at 4:2:0 (chroma always coded as one 4x4 sub-block per luma 4x4, matching luma
            // 1:1 -- see EncodeChromaRegion) for any leaf size, but equals the leaf's own luma block size
            // at 4:4:4 (chroma matches luma's leaf size 1:1 there too), so cflAllowed flips to false for
            // lossless + 4:4:4 -- except at the 4x4 leaf floor itself, where the leaf's own luma block size
            // already equals Block4x4, so this same condition flips back to true there. Getting this CDF
            // wrong doesn't just compress worse -- it silently desyncs the entropy decoder against any real
            // AV1 decoder, since CFL-allowed-ness
            // picks which adaptive probability table the very next symbol is read from. Chroma444 and
            // non-lossless never co-occur in this encoder (see Av1FrameEncoder.Encode's chroma444 gate),
            // so the non-lossless branch never needs to consult it. This condition was already exactly right
            // before CFL (Phase 6 backlog item) was real-searched -- SearchUvMode never picked
            // Av1IntraMode.UvCflPred before, but the CDF-selection logic here has to match the decoder's
            // is_cfl_allowed() regardless of whether CFL is ever actually chosen, since it's read
            // unconditionally whenever hasChroma.
            bool cflAllowed = s.Lossless
                ? Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4
                : true;
            // Indexed by writtenYMode (the actual, possibly palette-overridden y_mode just written above),
            // not raw bestMode: a real decoder derives this same CDF-selection context from whatever y_mode
            // value it just decoded, which is DC_PRED whenever palette overrode it here, regardless of what
            // this encoder's own pre-palette-decision bestMode search happened to prefer.
            var uvModeCdf = cflAllowed ? s.Cdf.UvModeCflAllowed[writtenYMode] : s.Cdf.UvModeCflNotAllowed[writtenYMode];
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(uvModeCdf, writtenUvMode);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            var decideUvModeCdf = cflAllowed ? s.DecideCdf.UvModeCflAllowed[writtenYMode] : s.DecideCdf.UvModeCflNotAllowed[writtenYMode];
            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(decideUvModeCdf, writtenUvMode);
            }

            // read_cfl_alphas() (spec §5.11.45): structurally present, immediately after uv_mode and before
            // intra_angle_info_uv(), whenever uv_mode == UV_CFL_PRED (Av1TileDecoder's own read order at
            // ReadUvMode()/ReadCflAlphas()/IntraAngleInfoUv -- bitstream position matters here, not just
            // logical presence). SearchUvMode never returns UvCflPred unless cflAllowed was true for this
            // leaf (see its own remarks), so this can't fire from a CDF table that didn't structurally offer
            // the symbol in the first place. Gated on writtenUvMode: never fires when palette overrides
            // uv_mode to DC_PRED, regardless of what bestUvMode the raw search preferred.
            if (writtenUvMode == Av1IntraMode.UvCflPred)
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    WriteCflAlphas(s.Symbols, s.Cdf, bestAlphaU, bestAlphaV);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    WriteCflAlphas(s.DecideCommitSink, s.DecideCdf, bestAlphaU, bestAlphaV);
                }
            }

            // intra_angle_info_uv() (spec §5.11.43): structurally present whenever this leaf's size is
            // >= 8x8 (no longer always true now that the partition floor reaches 4x4, sizeMi == 1) and the
            // searched uv_mode is directional, mirroring Av1TileDecoder.IntraAngleInfoUv exactly, including
            // its shared AngleDelta CDF table (indexed by mode class, not by plane). SearchUvMode's own
            // angleDeltaAllowed gate already never picks a nonzero bestUvAngleDelta for such a leaf.
            // UV_CFL_PRED is never directional (Av1IntraMode.IsDirectional(UvCflPred) is false), so this
            // never fires for a CFL leaf -- no angle_delta symbol competes with cfl_alphas for this leaf.
            // Gated on writtenUvMode for the same palette-override reason as above.
            if (sizeMi >= 2 && Av1IntraMode.IsDirectional(writtenUvMode))
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.AngleDelta[writtenUvMode - Av1IntraMode.VPred], bestUvAngleDelta + MaxAngleDelta);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.AngleDelta[writtenUvMode - Av1IntraMode.VPred], bestUvAngleDelta + MaxAngleDelta);
                }
            }
        }

        if (paletteBitsPresent)
        {
            int bsizeCtx = GetPaletteBsizeCtx(bSize);

            // Gated on writtenYMode, not bestMode: has_palette_y is structurally present whenever the
            // actual written y_mode is DC_PRED, whether that's because bestMode was naturally DC_PRED or
            // because palette overrode it -- see writtenYMode's own remarks above.
            if (writtenYMode == Av1IntraMode.DcPred)
            {
                int paletteModeCtx = GetPaletteModeCtx(s, r, c, availU, availL);
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteYMode[bsizeCtx][paletteModeCtx], usedPalette ? 1 : 0);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteYMode[bsizeCtx][paletteModeCtx], usedPalette ? 1 : 0);
                }

                if (usedPalette)
                {
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        s.Symbols.WriteSymbol(s.Cdf.PaletteYSize[bsizeCtx], nY - 2);
                        WritePaletteColorsY(s, s.PaletteColorsY, nY, r, c, availU, availL);
                    }

                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteYSize[bsizeCtx], nY - 2);
                    }

                    paletteSizeY = nY;
                }
            }

            // has_palette_uv is only structurally present when uv_mode == DC_PRED (spec's
            // palette_mode_info(), mirrored from Av1TileDecoder.PaletteModeInfo's identical
            // `_hasChroma && _uvMode == Av1IntraMode.DcPred` gate) -- now that uv_mode is really searched,
            // this can no longer assume it's always DC_PRED. Gated on writtenUvMode for the same
            // palette-override reason as writtenYMode above.
            if (hasChroma && writtenUvMode == Av1IntraMode.DcPred)
            {
                // paletteUvModeCtx = (this leaf's own Y palette size > 0) -- matches
                // Av1TileDecoder.PaletteModeInfo's `_paletteSizeY > 0 ? 1 : 0` exactly: usedPalette already
                // implies bestMode == DC_PRED and a Y palette was just written above whenever it's true, and
                // is false whenever no Y palette was written (paletteSizeY stays 0), so this is equivalent
                // without needing to re-derive it from paletteSizeY.
                int paletteUvModeCtx = usedPalette ? 1 : 0;
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteUvMode[paletteUvModeCtx], usedPalette ? 1 : 0);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteUvMode[paletteUvModeCtx], usedPalette ? 1 : 0);
                }

                if (usedPalette)
                {
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        s.Symbols.WriteSymbol(s.Cdf.PaletteUvSize[bsizeCtx], nUv - 2);
                        WritePaletteColorsUv(s, s.PaletteColorsU, s.PaletteColorsV, nUv, r, c, availU, availL);
                    }

                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteUvSize[bsizeCtx], nUv - 2);
                    }

                    paletteSizeUV = nUv;
                }
            }
        }

        // filter_intra_mode_info() (spec §5.11.24): structurally present exactly when the decoder's own
        // gate (enable_filter_intra && YMode == DC_PRED && PaletteSizeY == 0 && max(bw,bh) <= 32) holds --
        // enable_filter_intra is unconditionally on (see Av1SequenceHeaderWriter), and PaletteSizeY == 0 is
        // exactly !usedPalette here (this encoder never partially uses Y-only palette -- see usedPalette's
        // own remarks). bestUseFilterIntra can only be true when bestMode == DcPred and sizePixels <= 32
        // already (see the search above), so no separate re-check of those two conditions is needed here.
        if (!usedPalette && bestMode == Av1IntraMode.DcPred && sizePixels <= 32)
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.FilterIntra[bSize], bestUseFilterIntra ? 1 : 0);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.FilterIntra[bSize], bestUseFilterIntra ? 1 : 0);
            }

            if (bestUseFilterIntra)
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.FilterIntraMode, bestFilterIntraMode);
                }

                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.FilterIntraMode, bestFilterIntraMode);
                }
            }
        }

        if (usedPalette && paletteAllZeroResidual)
        {
            // SearchLosslessYPalette/SearchLosslessUvPalette already built the winning candidate's own
            // color map directly into PaletteColorMap/PaletteColorMapUv as a side effect of finding it (see
            // their own remarks) -- nothing between that search and this commit touches those buffers again,
            // so no rebuild is needed here.
            // onscreenWidthMi/onscreenHeightMi (project plan Round N+36's own root-cause fix): this leaf's own
            // nominal footprint can overhang the frame's TRUE (unpadded) edge -- see WriteColorMapTokens' own
            // remarks for why only the on-screen portion is ever really entropy-coded. Lossless is always
            // chroma444 (see TileState.Lossless's remarks), so luma and chroma share the same true mi bounds
            // and thus the same onscreen pixel extent here -- no separate chroma computation needed.
            int onscreenWidth = Math.Min(sizePixels, (s.TrueMiCols - c) * 4);
            int onscreenHeight = Math.Min(sizePixels, (s.TrueMiRows - r) * 4);

            var colorMap = s.PaletteColorMap;
            if (s.Phase != Av1EncodePhase.Decide)
            {
                WriteColorMapTokens(s, colorMap, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nY, s.Cdf.PaletteYColorIndex);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                WriteColorMapTokens(s.DecideCommitSink, colorMap, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nY, s.DecideCdf.PaletteYColorIndex);
            }

            CopyColorMapToFrameGrid(s.PaletteColorMapYGrid, s.YWidth, colorMap, sizePixels, x, y, onscreenWidth, onscreenHeight);

            // Reconstruction is exact by construction (paletteAllZeroResidual means every source sample in
            // this leaf mapped to its palette color with zero error), so copying straight from source is
            // simpler than -- and produces identical results to -- looking each pixel back up through the
            // palette + color map just written.
            for (int i = 0; i < sizePixels; i++)
            {
                Array.Copy(s.SourceY, ((y + i) * s.YWidth) + x, s.ReconY, ((y + i) * s.YWidth) + x, sizePixels);
            }

            if (hasChroma)
            {
                // Real chroma-scaled coordinates/size (chromaX/chromaY/chromaSizePixels, computed earlier
                // for the UV palette search call and reused here per that computation's own remarks) --
                // correct for non-lossless 4:2:0 too, not just lossless's own always-Chroma444 case. This
                // branch was unreachable for non-lossless until paletteSearchEnabled's lossless gate was
                // lifted (project plan Phase 4, exact-match-only palette-for-lossy, third attempt); fixed
                // here at the same time as that gate, mirroring the identical fix already applied to the
                // IntraBC exact-match commit path just above. Onscreen-bound formula matches
                // Av1TileDecoder.DecodeColorMapTokens's own chroma call site exactly (subX/subY right-shift
                // of the real luma-mi remaining-count, floored at 1) -- always a no-op for the only case that
                // reaches this today (non-lossless never has a real frame-edge overhang leaf), but matching
                // it exactly avoids relying on that invariant silently.
                int chromaSubX = s.Chroma444 ? 0 : 1;
                int chromaOnscreenWidth = Math.Max(1, Math.Min(chromaSizePixels, ((s.TrueMiCols - c) * 4) >> chromaSubX));
                int chromaOnscreenHeight = Math.Max(1, Math.Min(chromaSizePixels, ((s.TrueMiRows - r) * 4) >> chromaSubX));
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    WriteColorMapTokens(s, s.PaletteColorMapUv, chromaSizePixels, chromaSizePixels, chromaOnscreenWidth, chromaOnscreenHeight, nUv, s.Cdf.PaletteUvColorIndex);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    WriteColorMapTokens(s.DecideCommitSink, s.PaletteColorMapUv, chromaSizePixels, chromaSizePixels, chromaOnscreenWidth, chromaOnscreenHeight, nUv, s.DecideCdf.PaletteUvColorIndex);
                }

                CopyColorMapToFrameGrid(s.PaletteColorMapUvGrid, s.ChromaWidth, s.PaletteColorMapUv, chromaSizePixels, chromaX, chromaY, chromaOnscreenWidth, chromaOnscreenHeight);

                for (int i = 0; i < chromaSizePixels; i++)
                {
                    int rowOffset = ((chromaY + i) * s.ChromaWidth) + chromaX;
                    Array.Copy(s.SourceU!, rowOffset, s.ReconU!, rowOffset, chromaSizePixels);
                    Array.Copy(s.SourceV!, rowOffset, s.ReconV!, rowOffset, chromaSizePixels);
                }
            }

            // read_block_tx_size() (spec §5.11.16): real bitstream order is palette_tokens() (the color-map
            // writes just above) THEN read_block_tx_size() THEN reset_block_context() -- confirmed directly
            // against Av1TileDecoder's own real call order (DecodeBlock: IntraFrameModeInfo() ->
            // PaletteTokens() -> ReadBlockTxSize() -> ResetBlockContext() when skip). allowSelect is
            // `!is_inter || !skip` -- for a palette leaf (is_inter is always false; palette never sets it),
            // allowSelect is unconditionally true regardless of skip, so a real tx_depth symbol is still
            // read/written here whenever MiSize > BLOCK_4X4 and TxMode == SELECT, exactly like the plain
            // (non-palette) commit branch below -- skip=1 only means no actual coefficients are read
            // afterward, not that tx_size itself stops being signalled. Missing this entirely, and then
            // writing it in the wrong position relative to the color-map tokens, were both real, confirmed
            // bugs found this round (see the round log's own "palette-for-lossy" entries) -- getting the
            // relative order wrong corrupts the entropy stream just as surely as omitting the symbol
            // outright, since the decoder reads these same two things in this exact order too. txDepth 0
            // (the whole leaf as one uniform transform, matching leafTxSz below) is the only sensible choice
            // -- there's no real residual to size a smaller transform for either way.
            if (!s.Lossless && sizeMi > 1)
            {
                WriteTxDepth(s, r, c, availU, availL, bSize, txDepth: 0);
            }

            if (!s.Lossless)
            {
                int leafTxSz = Av1ForwardTransform.SizeToTxSz(sizePixels);
                for (int dy = 0; dy < sizeMi; dy++)
                {
                    int rowIdx = (r + dy) * s.MiCols;
                    for (int dx = 0; dx < sizeMi; dx++)
                    {
                        s.LumaTxSizes[rowIdx + c + dx] = leafTxSz;
                    }
                }
            }

            // reset_block_context(bw4, bh4) (spec §5.11.5): this leaf is skip = 1, so none of the
            // WriteCoeffs calls below run -- without this, YCoeffCtx/UCoeffCtx/VCoeffCtx would keep
            // whatever an earlier, unrelated leaf last left in this leaf's own above/left slots, feeding a
            // real decoder's matching reset a stale context it never sees on this side.
            s.YCoeffCtx.Reset(c, sizeMi, r, sizeMi);

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            // Same reset as the real one just above.
            s.DecideYCoeffCtx.Reset(c, sizeMi, r, sizeMi);

            if (hasChroma)
            {
                int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
                int chromaR4Base = s.Chroma444 ? r : r / 2;
                int chromaC4Base = s.Chroma444 ? c : c / 2;
                s.UCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
                s.VCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same resets as the real ones just above.
                s.DecideUCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
                s.DecideVCoeffCtx!.Reset(chromaC4Base, chromaN, chromaR4Base, chromaN);
            }

            MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);
            if (hasChroma)
            {
                MarkChromaBlockDecoded(s, r, c, sizeMi);
            }
        }
        else if (usedPalette)
        {
            // Non-zero-residual palette: skip = 0, so unlike the all-zero branch above, this leaf carries a
            // real per-plane WHT residual on top of the palette prediction -- exactly like
            // Av1TileDecoder.TransformBlock's own unconditional post-prediction residual() step, just with a
            // color-map lookup as the prediction source instead of spatial extrapolation or block-copy. No
            // manual coefficient-context reset needed here (unlike the all-zero branch): WriteCoeffs runs
            // for real below and updates YCoeffCtx/UCoeffCtx/VCoeffCtx in place, the same way any other
            // non-skip leaf's residual commit already does.
            // Real palette_tokens() (spec §5.11.47) reads BOTH planes' color-index maps before
            // read_block_tx_size()/residual() touches either plane's coefficients -- so both maps must have
            // their tokens written here before EITHER plane's residual is encoded. Y's map lives in
            // PaletteColorMap and UV's in the separate PaletteColorMapUv (see their declarations), both
            // already built by the search above -- no rebuild needed (see the all-zero branch's identical
            // remark).
            // See the all-zero-residual branch's identical onscreenWidth/onscreenHeight remarks above.
            int onscreenWidth = Math.Min(sizePixels, (s.TrueMiCols - c) * 4);
            int onscreenHeight = Math.Min(sizePixels, (s.TrueMiRows - r) * 4);

            var colorMapY = s.PaletteColorMap;
            if (s.Phase != Av1EncodePhase.Decide)
            {
                WriteColorMapTokens(s, colorMapY, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nY, s.Cdf.PaletteYColorIndex);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                WriteColorMapTokens(s.DecideCommitSink, colorMapY, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nY, s.DecideCdf.PaletteYColorIndex);
            }

            CopyColorMapToFrameGrid(s.PaletteColorMapYGrid, s.YWidth, colorMapY, sizePixels, x, y, onscreenWidth, onscreenHeight);

            int[]? colorMapUv = null;
            if (hasChroma)
            {
                colorMapUv = s.PaletteColorMapUv;
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    WriteColorMapTokens(s, colorMapUv, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nUv, s.Cdf.PaletteUvColorIndex);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    WriteColorMapTokens(s.DecideCommitSink, colorMapUv, sizePixels, sizePixels, onscreenWidth, onscreenHeight, nUv, s.DecideCdf.PaletteUvColorIndex);
                }
                CopyColorMapToFrameGrid(s.PaletteColorMapUvGrid, s.ChromaWidth, colorMapUv, sizePixels, x, y, onscreenWidth, onscreenHeight);
            }

            EncodePaletteResidual(s, s.SourceY, s.ReconY, s.YWidth, ptype: 0, r, c, x, y, sizePixels, sizePixels, colorMapY, sizePixels, s.PaletteColorsY, s.YCoeffCtx, blockDecodedPlane: 0, s.DecideYCoeffCtx);

            if (hasChroma)
            {
                // U and V are two independent single-channel planes sharing the same color map index (see
                // EstimateLosslessPaletteCost's identical remark) -- reuses EncodePaletteResidual once per
                // plane rather than a combined UV variant, exactly mirroring how every other residual path
                // in this class already treats chroma.
                EncodePaletteResidual(s, s.SourceU!, s.ReconU!, s.ChromaWidth, ptype: 1, r, c, x, y, sizePixels, sizePixels, colorMapUv!, sizePixels, s.PaletteColorsU, s.UCoeffCtx!, blockDecodedPlane: 1, s.DecideUCoeffCtx!);
                EncodePaletteResidual(s, s.SourceV!, s.ReconV!, s.ChromaWidth, ptype: 1, r, c, x, y, sizePixels, sizePixels, colorMapUv!, sizePixels, s.PaletteColorsV, s.VCoeffCtx!, blockDecodedPlane: 2, s.DecideVCoeffCtx!);
            }
        }
        else
        {
            if (s.Lossless && sizePixels > 64)
            {
                // Spec's real residual() (§5.11.34) never iterates a coding block bigger than 64x64 as one
                // flat raster scan of transform blocks -- it chunks it into 64x64-or-smaller pieces (Av1TileDecoder.Residual's
                // widthChunks/heightChunks) and, for EACH chunk, writes every plane (luma, then U, then V)
                // before moving to the next chunk. Only reachable for a lossless 128x128 leaf (this
                // encoder's non-lossless leaves never exceed 32x32), so this only ever chunks into exactly 4
                // 64x64 quadrants (2x2). Confirmed by direct encoder/decoder cross-instrumentation: the
                // decoder's own x4/y4 sequence wraps at 16 (64 pixels), not 32, proving this chunk-major,
                // plane-minor order is what a real decoder actually expects -- the previous flat "all luma
                // sub-blocks for the whole leaf, then all chroma" order desynced the entropy stream the
                // moment a genuine >64x64 leaf was ever chosen.
                int chunksPerSide = sizePixels / 64;
                for (int chunkY = 0; chunkY < chunksPerSide; chunkY++)
                {
                    for (int chunkX = 0; chunkX < chunksPerSide; chunkX++)
                    {
                        int chunkR = r + (chunkY * 16);
                        int chunkC = c + (chunkX * 16);
                        int chunkX_px = x + (chunkX * 64);
                        int chunkY_px = y + (chunkY * 64);

                        EncodeLosslessLumaResidual(s, chunkR, chunkC, chunkX_px, chunkY_px, bestMode, bestAngleDelta, filterTypeSmooth, bestUseFilterIntra, bestFilterIntraMode, blockSize: 64);

                        if (hasChroma && !usedIntrabc)
                        {
                            EncodeChromaRegion(s, chunkR, chunkC, chunkX_px, chunkY_px, sizeMi: 16, bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV);
                        }
                    }
                }
            }
            else if (s.Lossless)
            {
                // AV1 forces TX_4X4 for every block when lossless -- the leaf's transform splits into
                // (sizePixels/4)^2 4x4 sub-blocks, each with its own predict-then-reconstruct pass (see the
                // method's remarks on why this can't just reuse bestPred/the whole-leaf residual the
                // non-lossless path below does).
                EncodeLosslessLumaResidual(s, r, c, x, y, bestMode, bestAngleDelta, filterTypeSmooth, bestUseFilterIntra, bestFilterIntraMode, sizePixels);
            }
            else
            {
                // sizePixels is 8, 16, or 32 -- EncodePartitionForced/DecidePartition's non-lossless floor is
                // 8x8 (sizeMi == 2), and this encoder never keeps a non-lossless leaf above 32x32 as one
                // (EncodePartitionForced's superblock traversal always splits a 64x64 node at least once
                // before DecidePartition's floor logic ever runs on it -- see the project plan's
                // partition/TX-size RDO phase).
                //
                // The real transform/quantize/trellis/reconstruct work (Phase 4's tx_type search AND, now,
                // tx-size search both included) already ran early, before SearchUvMode -- see that call
                // site's remarks for why (CFL needs this leaf's own real reconstructed luma before it can be
                // searched at all, which is earlier than this leaf's residual would otherwise be
                // committed). chosenTxDepth/subTxTypes/bestTxType and TileState.LumaLevels (all from that
                // earlier scope) are this branch's only remaining inputs; only the real bitstream writes
                // (tx_depth, then WriteCoeffs per real transform block) wait until here, matching spec's
                // mode_info()-then-read_block_tx_size()-then-residual() ordering.
                //
                // tx_depth itself (spec's read_tx_size/tx_depth, §5.11.15/§8.3.2) is only ever signalled for
                // MiSize > BLOCK_4X4 -- a genuine 4x4 leaf (sizeMi == 1) has no smaller uniform tx size to
                // split into, so chosenTxDepth is always its inert 0 default there and no symbol is written,
                // matching WriteTxDepth's own remarks. Also gated on s.LumaTxSizes' own real per-leaf state
                // being current for every leaf's OWN above/left context (updated below regardless of depth).
                if (sizeMi > 1)
                {
                    WriteTxDepth(s, r, c, availU, availL, bSize, chosenTxDepth);
                }

                int leafTxSz = chosenTxDepth == 1 ? Av1ForwardTransform.SizeToTxSz(sizePixels / 2) : Av1ForwardTransform.SizeToTxSz(sizePixels);
                for (int dy = 0; dy < sizeMi; dy++)
                {
                    int rowIdx = (r + dy) * s.MiCols;
                    for (int dx = 0; dx < sizeMi; dx++)
                    {
                        s.LumaTxSizes[rowIdx + c + dx] = leafTxSz;
                    }
                }

                if (chosenTxDepth == 0)
                {
                    int[] levels = s.LumaLevels;

                    int txSz = Av1ForwardTransform.SizeToTxSz(sizePixels);
                    int txSzSqr = Av1CoeffTables.TxSizeSqr[txSz];

                    // intraDir: FilterIntraModeToIntraDir[filterIntraMode] when this leaf used FILTER_INTRA
                    // (spec's transform_type() context derivation, mirrored from Av1TileDecoder.TransformType --
                    // bestMode is always DC_PRED whenever bestUseFilterIntra is true, per the search above, but
                    // that's not the same context index unless filterIntraMode itself also happens to map back
                    // to DC_PRED).
                    int intraDir = bestUseFilterIntra ? Av1TxTypeTables.FilterIntraModeToIntraDir[bestFilterIntraMode] : bestMode;

                    // Y transform type symbol: writes whichever type the search above actually picked (the
                    // CDF-inverse lookup mirrors Av1TileDecoder.TransformType's own TxTypeIntraInvSet1/Set2 read),
                    // via whichever set the search above actually searched (see TileState.ReducedTxSet's own
                    // remarks -- TX_16X16 always the reduced set, TX_4X4/TX_8X8 the full set unless
                    // s.ReducedTxSet) -- but Av1TileDecoder.GetTxSet forces TX_SET_DCTONLY (no tx_type symbol
                    // read at all) whenever txSzSqrUp == TX_32X32, i.e. exactly a 32x32 leaf here (this encoder's
                    // leaves are always square, so txSzSqrUp == txSz) -- bestTxType is left at its DctDct default
                    // there (the search above never ran) and, correctly, no symbol is written for it either.
                    // Passing null for writeLumaTxType at that size (rather than writing a symbol no real decoder
                    // expects) is required for correctness, not just an optimization -- writing it would desync
                    // the entropy stream from here on. Only actually invoked by WriteCoeffs when the block turns
                    // out non-all-zero either way -- see its remarks.
                    bool useFullTxSetForWrite = !s.ReducedTxSet && sizePixels != 16;
                    int txTypeSymbol = useFullTxSetForWrite
                        ? Array.IndexOf(Av1TxTypeTables.TxTypeIntraInvSet1, bestTxType)
                        : Array.IndexOf(Av1TxTypeTables.TxTypeIntraInvSet2, bestTxType);
                    void WriteLumaTxType() => s.Symbols.WriteSymbol(useFullTxSetForWrite ? s.Cdf.IntraTxTypeSet1[txSzSqr][intraDir] : s.Cdf.IntraTxTypeSet2[txSzSqr][intraDir], txTypeSymbol);
                    Action? writeLumaTxType = sizePixels < 32 ? WriteLumaTxType : null;

                    // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                    // remarks. Parallel closure mirroring writeLumaTxType above exactly, but adapting
                    // DecideCdf via DecideCommitSink instead of the real Cdf/Symbols.
                    void DecideWriteLumaTxType() => s.DecideCommitSink.WriteSymbol(useFullTxSetForWrite ? s.DecideCdf.IntraTxTypeSet1[txSzSqr][intraDir] : s.DecideCdf.IntraTxTypeSet2[txSzSqr][intraDir], txTypeSymbol);
                    Action? decideWriteLumaTxType = sizePixels < 32 ? DecideWriteLumaTxType : null;

                    // WriteCoeffs takes (x4, y4) -- AV1's convention is x4 = column, y4 = row -- so this is
                    // (c, r), not (r, c). Passing them backwards is silently unobservable on any square coding
                    // block grid (miCols == miRows, e.g. every image up to 64x64 after padding), since
                    // PlaneContext's MaxX4/MaxY4 bounds are then identical too; it only breaks on a genuinely
                    // non-square, multi-superblock frame, where the above/left context bookkeeping silently
                    // stops updating past whichever axis is shorter in mi-units -- desyncing every block's
                    // entropy context (and the whole rest of the tile with it) from exactly that point onward.
                    // txType: bestTxType -- Round N+43's own real root-caused fix. This call site's own
                    // hardcoded default (Av1TxType.DctDct) was harmless for every type this search could
                    // previously pick (AdstDct/DctAdst/AdstAdst/Idtx are all Av1TxClass.Class2D too, and AV1's
                    // real default scan order/coefficient-context derivation is identical across every Class2D
                    // type, keyed only by tx_size and tx_class -- not by which specific type within the class),
                    // but is genuinely wrong once VDct/HDct (Av1TxClass.ClassVert/ClassHoriz) become reachable --
                    // see Av1CoefficientWriter's own class remarks for the full account of the resulting
                    // corruption this fixes.
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, sizePixels, ptype: 0, c, r, s.YCoeffCtx, writeLumaTxType, txType: bestTxType);
                    }

                    // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                    // remarks. Same arguments as the real commit just above.
                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, sizePixels, ptype: 0, c, r, s.DecideYCoeffCtx, decideWriteLumaTxType, txType: bestTxType);
                    }

                    MarkLumaBlockDecoded(s, r, c, sizeMi, sizeMi);

                    // Two-pass tile-encoder architecture, Stage 1a -- see TileState.TxTypeGrid's own
                    // remarks. chosenTxDepth == 0: one real tx_type for the whole leaf's mi footprint.
                    for (int dy = 0; dy < sizeMi; dy++)
                    {
                        int rowIdx = (r + dy) * s.MiCols;
                        for (int dx = 0; dx < sizeMi; dx++)
                        {
                            s.TxTypeGrid[rowIdx + c + dx] = bestTxType;
                        }
                    }
                }
                else
                {
                    // tx_depth == 1: real spec residual() now iterates 4 real transform blocks (spec's own
                    // transform_block() traversal, raster order) instead of one -- each with its own real
                    // tx_type symbol (transform_type() is called per transform block, not per coding block,
                    // see Av1TileDecoder.ComputeTxType/TransformType's own plane==0 call site inside Coeffs()'s
                    // own per-block loop) and its own WriteCoeffs commit, using the real per-sub-block levels
                    // already computed (and this leaf's own ReconY already reconstructed) early above.
                    int halfSizeWrite = sizePixels / 2;
                    int halfSizeMiWrite = halfSizeWrite / 4;
                    int halfAreaWrite = halfSizeWrite * halfSizeWrite;
                    int subTxSz = Av1ForwardTransform.SizeToTxSz(halfSizeWrite);
                    int subTxSzSqr = Av1CoeffTables.TxSizeSqr[subTxSz];
                    bool useFullTxSetForWriteSub = !s.ReducedTxSet && halfSizeWrite != 16;
                    int subIntraDir = bestUseFilterIntra ? Av1TxTypeTables.FilterIntraModeToIntraDir[bestFilterIntraMode] : bestMode;

                    for (int k = 0; k < 4; k++)
                    {
                        int dr = k / 2;
                        int dc = k % 2;
                        int subR = r + (dr * halfSizeMiWrite);
                        int subC = c + (dc * halfSizeMiWrite);

                        Array.Copy(s.LumaLevels, k * halfAreaWrite, s.Levels, 0, halfAreaWrite);
                        int subTxType = subTxTypes[k];

                        int subTxTypeSymbol = useFullTxSetForWriteSub
                            ? Array.IndexOf(Av1TxTypeTables.TxTypeIntraInvSet1, subTxType)
                            : Array.IndexOf(Av1TxTypeTables.TxTypeIntraInvSet2, subTxType);
                        void WriteSubLumaTxType() => s.Symbols.WriteSymbol(useFullTxSetForWriteSub ? s.Cdf.IntraTxTypeSet1[subTxSzSqr][subIntraDir] : s.Cdf.IntraTxTypeSet2[subTxSzSqr][subIntraDir], subTxTypeSymbol);
                        Action? writeSubLumaTxType = halfSizeWrite < 32 ? WriteSubLumaTxType : null;

                        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's
                        // own remarks. Parallel closure mirroring writeSubLumaTxType above.
                        void DecideWriteSubLumaTxType() => s.DecideCommitSink.WriteSymbol(useFullTxSetForWriteSub ? s.DecideCdf.IntraTxTypeSet1[subTxSzSqr][subIntraDir] : s.DecideCdf.IntraTxTypeSet2[subTxSzSqr][subIntraDir], subTxTypeSymbol);
                        Action? decideWriteSubLumaTxType = halfSizeWrite < 32 ? DecideWriteSubLumaTxType : null;

                        // blockSize: sizePixels (the coding block's own real size, bigger than this
                        // transform), selecting Av1CoefficientWriter's own non-shortcut all_zero context
                        // path -- exactly the same blockSize-vs-size distinction EncodeLosslessLumaResidual's
                        // own identical call already relies on, see its remarks. (BlockDecoded for each
                        // sub-block was already marked true during the early real-reconstruction loop above
                        // -- required there for sub-block 1-3's own progressive neighbor prediction within
                        // this same leaf -- so it isn't repeated here.)
                        if (s.Phase != Av1EncodePhase.Decide)
                        {
                            Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, s.Levels, halfSizeWrite, ptype: 0, subC, subR, s.YCoeffCtx, writeSubLumaTxType, blockSize: sizePixels, txType: subTxType);
                        }

                        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's
                        // own remarks. Same arguments as the real commit just above.
                        if (s.Phase != Av1EncodePhase.Emit)
                        {
                            Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, s.Levels, halfSizeWrite, ptype: 0, subC, subR, s.DecideYCoeffCtx, decideWriteSubLumaTxType, blockSize: sizePixels, txType: subTxType);
                        }

                        // Two-pass tile-encoder architecture, Stage 1a -- see TileState.TxTypeGrid's own
                        // remarks. chosenTxDepth == 1: tx_type genuinely differs per sub-block, so this
                        // fills only this sub-block's own halfSizeMiWrite-square mi region, not the whole
                        // leaf -- filling the whole leaf here (like LumaTxSizes' own uniform fill, correct
                        // there since tx SIZE really is uniform across all 4 sub-blocks) would silently
                        // collapse up to 4 distinct real tx_type values into whichever one wrote last.
                        for (int dy = 0; dy < halfSizeMiWrite; dy++)
                        {
                            int rowIdx = (subR + dy) * s.MiCols;
                            for (int dx = 0; dx < halfSizeMiWrite; dx++)
                            {
                                s.TxTypeGrid[rowIdx + subC + dx] = subTxType;
                            }
                        }
                    }
                }
            }
        }
        }

        int leafYMode = usedIntrabc ? Av1IntraMode.DcPred : writtenYMode;
        int leafUvMode = usedIntrabc ? Av1IntraMode.DcPred : writtenUvMode;

        // Mirrors the real bitstream semantics of every field here, not just the search's own
        // pre-override intent: usedIntrabc (spec §5.11.7) and usedPalette both force filter_intra/
        // angle_delta off regardless of what the earlier search stages computed before either was
        // decided -- see Av1TileDecoder.IntraFrameModeInfo's own use_intrabc early-return (sets
        // UseFilterIntra/AngleDelta* to their DC_PRED/no-angle defaults unconditionally) and this
        // method's own filter_intra write-site guard (!usedPalette && ...) for the real gates this
        // mirrors. Computed once here (not just at the OnLeafCommitted diagnostic site below, which
        // reuses these same locals) so the two-pass-architecture grids just below store the same real,
        // already-override-applied values the diagnostic record always has.
        bool leafUseFilterIntra = !usedPalette && !usedIntrabc && bestUseFilterIntra;
        int leafFilterIntraMode = leafUseFilterIntra ? bestFilterIntraMode : 0;
        int leafAngleDeltaY = (usedIntrabc || usedPalette) ? 0 : bestAngleDelta;
        int leafAngleDeltaUv = (usedIntrabc || usedPalette) ? 0 : bestUvAngleDelta;

        for (int dy = 0; dy < sizeMi; dy++)
        {
            int rowIdx = (r + dy) * s.MiCols;
            for (int dx = 0; dx < sizeMi; dx++)
            {
                int idx = rowIdx + c + dx;
                s.YModes[idx] = leafYMode;
                s.UvModes[idx] = leafUvMode;
                s.MiSizes[idx] = bSize;
                s.AngleDeltaYGrid[idx] = leafAngleDeltaY;
                s.AngleDeltaUvGrid[idx] = leafAngleDeltaUv;
                s.FilterIntraGrid[idx] = leafUseFilterIntra;
                s.FilterIntraModeGrid[idx] = leafFilterIntraMode;

                // Two-pass tile-encoder architecture, Stage 1a addendum -- see TileState.CflAlphaUGrid's
                // own remarks. Stored unconditionally (harmless whenever leafUvMode isn't UvCflPred).
                s.CflAlphaUGrid[idx] = bestAlphaU;
                s.CflAlphaVGrid[idx] = bestAlphaV;

                // Must mirror the actual written skip bit (paletteAllZeroResidual || (usedIntrabc &&
                // intrabcExactCandidate), see above -- not usedPalette or bare usedIntrabc), which is 0 for
                // an approximate-match IntraBC leaf (it carries a real residual) and now also 0 for an
                // approximate (k-means-clustered, non-exact) palette leaf, which likewise carries a real
                // residual despite usedPalette being true. Av1TileDecoder stores its own Skips-equivalent
                // grid from the literally decoded skip bit (_skips[idx] = _skip), so using a broader flag
                // here diverges from the decoder's neighbor-skip context for any later leaf bordering this
                // one -- silently latent while approximate leaves of either kind were rare, but real once
                // they're not.
                s.Skips[idx] = paletteAllZeroResidual || (usedIntrabc && intrabcExactCandidate);
                s.PaletteSizesY[idx] = usedPalette ? paletteSizeY : 0;
                s.PaletteSizesUV[idx] = usedPalette ? paletteSizeUV : 0;
                int colorBase = idx * 8;
                for (int k = 0; k < 8; k++)
                {
                    s.PaletteColorsYGrid[colorBase + k] = s.PaletteColorsY[k];
                    s.PaletteColorsUGrid[colorBase + k] = s.PaletteColorsU[k];
                }

                s.IsInters[idx] = usedIntrabc;
                s.MvRowsGrid[idx] = usedIntrabc ? intrabcMvRow : 0;
                s.MvColsGrid[idx] = usedIntrabc ? intrabcMvCol : 0;
                s.Written[idx] = true;
            }
        }

        if (s.OnLeafCommitted is not null)
        {
            s.OnLeafCommitted(new Av1BlockDecisionRecord
            {
                R = r,
                C = c,
                WidthMi = sizeMi,
                HeightMi = sizeMi,
                YMode = leafYMode,
                AngleDeltaY = leafAngleDeltaY,
                UvMode = leafUvMode,
                AngleDeltaUv = leafAngleDeltaUv,
                Skip = paletteAllZeroResidual || (usedIntrabc && intrabcExactCandidate),
                PaletteSizeY = usedPalette ? paletteSizeY : 0,
                PaletteSizeUV = usedPalette ? paletteSizeUV : 0,
                PaletteColorsY = usedPalette ? string.Join(',', s.PaletteColorsY[..paletteSizeY]) : string.Empty,
                PaletteColorsUV = usedPalette && paletteSizeUV > 0 ? string.Join(',', s.PaletteColorsU[..paletteSizeUV]) : string.Empty,
                UsedIntrabc = usedIntrabc,
                MvRow = usedIntrabc ? intrabcMvRow : 0,
                MvCol = usedIntrabc ? intrabcMvCol : 0,
                UseFilterIntra = leafUseFilterIntra,
                FilterIntraMode = leafFilterIntraMode,
                EstimatedCost = s.PartitionDecisions.TryGetValue((r, c, sizeMi), out var decision) ? decision.Cost : null,
            });
        }

        // sizePixels > 64 already wrote chroma inline, interleaved per-64x64-chunk with luma above (see that
        // branch's remarks on why real AV1 requires that interleaving, not a separate whole-leaf pass here).
        if (hasChroma && !usedPalette && !usedIntrabc && sizePixels <= 64)
        {
            EncodeChromaRegion(s, r, c, x, y, sizeMi, bestUvMode, bestUvAngleDelta, bestAlphaU, bestAlphaV);
        }

        if (s.AllowIntrabc)
        {
            RecordIntrabcHashEntry(s, r, c, sizeMi);
        }
    }

    /// <summary>
    /// Commits a rectangular (HORZ/VERT-split) coding block: <paramref name="wMi"/>x<paramref name="hMi"/>
    /// mi units, <paramref name="wMi"/> != <paramref name="hMi"/>. First increment of full AV1 partition-type
    /// support (spec §5.11.4 defines 10 types -- NONE/HORZ/VERT/SPLIT/HORZ_A/HORZ_B/VERT_A/VERT_B/HORZ_4/
    /// VERT_4; this project's encoder previously only ever wrote NONE/SPLIT, a pure square quadtree -- see
    /// <see cref="RdPickPartition"/>'s Horz/Vert cost candidates for the RDO half of this feature).
    /// <see cref="Av1TileDecoder"/> already handles every partition type generally (it must, to decode
    /// bitstreams from any real encoder), so this needed no decoder changes at all -- confirmed by reading
    /// <c>Av1TileDecoder.DecodePartition</c>/<c>Av1BlockTables.PartitionSubsize</c>, both already fully
    /// populated for rectangular sizes.
    ///
    /// <para><b>Scope of this first increment</b> (deliberately narrower than <see cref="EncodeLeaf"/>'s
    /// square path, to land a correct, real win without the risk of generalizing that ~700-line function's
    /// every helper in one pass):</para>
    /// <list type="bullet">
    /// <item>Lossless only -- <see cref="RdPickPartition"/> never offers Horz/Vert for non-lossless.</item>
    /// <item>Real luma intra-mode search (<see cref="CandidateModes"/> + angle_delta, exactly mirroring
    /// <see cref="EncodeLeaf"/>'s own real search shape via <see cref="ComputeRectLumaCostForMode"/> --
    /// see the project plan's "real-image gap" investigation for why this replaced an earlier, narrower
    /// DC_PRED-only first increment: real content data showed DC_PRED-only rectangular costing meant a
    /// Horz/Vert candidate could only ever win when DC_PRED genuinely was the best predictor for that region,
    /// which is the minority case on real photo/graphic content). Chroma now gets a real, if narrower,
    /// commit-time mode search too (<see cref="SearchRectUvMode"/> -- <see cref="CandidateModes"/>/angle_delta,
    /// no CFL, mirroring <see cref="ComputeRectLumaCostForMode"/>'s own scope decisions), closing this
    /// method's previously-disclosed DC_PRED-only chroma gap; CFL for a rectangular leaf remains a separate,
    /// natural follow-up. Note this does introduce one new, small decision/commit asymmetry of its own:
    /// <see cref="EstimateRectChromaCost"/> (the decision-phase estimate <see cref="RdPickPartition"/>
    /// uses to cost a Horz/Vert candidate) still only trials the 2-candidate DC/PAETH proxy
    /// <see cref="EstimateLosslessChromaCost"/> uses, not this method's own full search -- so a rectangular
    /// leaf whose real commit benefits from a directional/smooth chroma mode is still costed as if it
    /// couldn't, at decision time. Deliberately left alone here (see the project plan's own repeated,
    /// hard-won lesson on decision-phase chroma-cost changes: several broader attempts at closing an
    /// analogous None/Split gap this same way regressed real-image output despite passing every synthetic
    /// test) -- this method's own real commit is still correct and never worse than before, only sometimes
    /// under-selected by the partition search that decides whether it gets to run at all. A wide-short or
    /// tall-narrow region (e.g. a text stroke or a
    /// flat border -- exactly the shapes a square-only quadtree can't express directly) can now code with
    /// whichever predictor genuinely suits it, not just DC_PRED, and <see cref="RdPickPartition"/>'s
    /// real cost comparison against None/Split means this is never a forced regression, only sometimes a
    /// missed win relative to a hypothetical, even-more-exhaustive search (e.g. HOG/SATD-pruned like the
    /// square path's own, or a real chroma search).</item>
    /// <item>Real palette support (Y and UV, coupled all-or-nothing exactly like <see cref="EncodeLeaf"/>'s
    /// own <c>usedPalette</c> -- see its remarks on why an independently-decoupled Y/UV attempt regressed
    /// graphic-content benchmarks and was reverted), added so Horz/Vert competes fairly against None/Split in
    /// <see cref="RdPickPartition"/>'s cost comparison instead of always losing to a square candidate
    /// that already had palette folded in. No IntraBC, though -- that remains square-leaf-only for now. The
    /// use_intrabc *bit* is still spec-required and written here (see below), just always with a false value;
    /// a rectangular leaf's content is also never recorded as a future IntraBC copy source
    /// (<see cref="RecordIntrabcHashEntry"/> is deliberately not called here), since that indexing assumes a
    /// square source region throughout (<see cref="IsValidIntrabcSourcePixels"/>'s own square <c>sizePixels</c>
    /// footprint).</item>
    /// <item>Never reached for a leaf bigger than 64x64 in either dimension -- <see cref="RdPickPartition"/>
    /// only offers Horz/Vert for a parent at or below sizeMi 16 (its own boundary <c>split_or_horz</c>/
    /// <c>split_or_vert</c> branch is restricted to this same bound too, for exactly this reason -- see its
    /// own remarks: a boundary Horz/Vert candidate at the top, sizeMi == 32 level would keep its *other*
    /// dimension at the full, un-halved 32 mi (128px), a real, found-via-round-trip-failure case where real
    /// AV1's own residual() chunks any &gt;64px-in-either-dimension coding block into independent 64x64
    /// pieces with an interleaved per-chunk Y/U/V order -- structurally different from this function's own
    /// single, unchunked whole-leaf traversal), so this never needs the &gt;64px chunked
    /// residual shape <see cref="EncodeLeaf"/>'s square path handles separately, and never hits the 4:2:0
    /// <c>bw4==1</c>/<c>bh4==1</c> shared-chroma edge case either (lossless is always 4:4:4 -- see
    /// <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate -- so this never needs
    /// <see cref="Av1TileDecoder"/>'s equivalent logic for that).</item>
    /// </list>
    ///
    /// <para>Bit order mirrors <see cref="EncodeLeaf"/>'s exactly (spec's own <c>mode_info()</c>/<c>residual()</c>
    /// ordering): skip (=1 only when fully palette-covered), use_intrabc(=0), y_mode(=<c>writtenYMode</c> --
    /// the real search winner, or DC_PRED whenever palette overrides it, mirroring <see cref="EncodeLeaf"/>'s
    /// own identical <c>writtenYMode</c> pattern), intra_angle_info_y() (structurally present per
    /// <see cref="RectAngleDeltaAllowed"/>, real angle_delta value when directional), uv_mode (=DC_PRED, no
    /// cfl_alphas/angle_delta -- chroma search out of scope, see this method's own scope remarks above),
    /// palette_mode_info() (has_palette_y/uv, real values now, using the real spec <c>AllowPalette</c> gate
    /// generalized to a rectangular <c>bSize</c> -- see its own remarks below for why this can't reuse
    /// <see cref="EncodeLeaf"/>'s old sizeMi-based shortcut; has_palette_y is itself only structurally present
    /// when <c>writtenYMode == DC_PRED</c>, per <see cref="Av1TileDecoder"/>'s own real gate, now that
    /// <c>writtenYMode</c> isn't unconditionally DC_PRED anymore), filter_intra_mode_info() (a real search
    /// winner now -- <c>bestUseFilterIntra</c>/<c>bestFilterIntraMode</c>, see the real search above -- gated
    /// on the real spec <c>max(bw,bh) &lt;= 32 &amp;&amp; YMode == DC_PRED &amp;&amp; PaletteSizeY == 0</c>,
    /// not the old square-only <c>sizePixels &lt;= 32</c> shortcut; real AV1's own gate never required
    /// squareness, only every rect shape this project can produce bigger than 32x16/16x32 was structurally
    /// excluded anyway), then either the
    /// palette color-map commit (skip=1 leaf) or real per-4x4-sub-block WHT residual for Y, U, V (predicted
    /// fresh per sub-block from progressively-updated <see cref="TileState.ReconY"/>/ReconU/ReconV, exactly
    /// like <see cref="EncodeLosslessLumaResidual"/>/<see cref="EncodeChromaRegion"/>'s own lossless paths --
    /// required for correctness, not just style: an interior sub-block's own local DC average depends on its
    /// immediate neighbors, not the whole leaf's edges, so this can't be a single whole-region prediction).</para>
    ///
    /// <para>Uses <c>bSize</c> internally (the coding block's own <see cref="Av1BlockSize"/>, derived from
    /// <paramref name="wMi"/>/<paramref name="hMi"/> via <see cref="BlockSizeFromWidthHeightMi"/>) for every
    /// spec-defined size-dependent gate below.</para>
    /// </summary>
    private static void EncodeRectangularLeaf(TileState s, int r, int c, int wMi, int hMi)
    {
        int widthPixels = wMi * 4;
        int heightPixels = hMi * 4;
        int bSize = BlockSizeFromWidthHeightMi(wMi, hMi);
        bool availU = r > 0;
        bool availL = c > 0;
        int x = c * 4;
        int y = r * 4;
        bool hasChroma = !s.MonoChrome;

        int aboveYMode = availU ? s.YModes[((r - 1) * s.MiCols) + c] : Av1IntraMode.DcPred;
        int leftYMode = availL ? s.YModes[(r * s.MiCols) + c - 1] : Av1IntraMode.DcPred;
        int yModeCtx0 = Av1BlockTables.IntraModeContext[aboveYMode];
        int yModeCtx1 = Av1BlockTables.IntraModeContext[leftYMode];

        int skipCtx = 0;
        if (availU)
        {
            skipCtx += s.Skips[((r - 1) * s.MiCols) + c] ? 1 : 0;
        }

        if (availL)
        {
            skipCtx += s.Skips[(r * s.MiCols) + c - 1] ? 1 : 0;
        }

        // Real spec gate for whether intra_angle_info_y/uv is structurally present at this rectangular size
        // -- see RectAngleDeltaAllowed's own remarks.
        bool angleDeltaAllowed = RectAngleDeltaAllowed(bSize);

        // Real luma intra-mode search: exhaustive over CandidateModes/angle_delta, deliberately still no
        // HOG/SATD pruning here even after EstimateRectLumaCost's own decision-phase counterpart gained it
        // (project plan's own architecture-unification follow-up to Step 6) -- this is the real, final,
        // commit-time search whose own output gets written to the bitstream, not a cost estimate feeding a
        // partition-level comparison, so staying exhaustive here can only ever find an equally-good-or-better
        // candidate than a pruned search would, never a worse one; narrowing it to match would be a genuine,
        // separate, higher-risk change (real libaom's own commit-time search IS pruned, so this remains a
        // real, disclosed divergence -- just one that can only bias towards a *better* real mode, not a
        // wrong one). Computed once, for real, before the palette
        // search below (which needs bestCost as its own real RD-gate baseline, exactly mirroring EncodeLeaf's
        // own bestCost -> SearchLosslessYPalette wiring) -- unlike this function's own previous DC_PRED-only
        // increment, there is now a genuine non-palette alternative to compare palette against.
        bool filterTypeSmooth = GetFilterType(s, r, c, availU, availL);
        int bestMode = Av1IntraMode.DcPred;
        int bestAngleDelta = 0;
        long bestCost = long.MaxValue;

        // prune_intra_y_mode/PruneIntraYMode (intra_mode_search.c:467-491, called from the plain-mode loop
        // at intra_mode_search.c:1610): applied here for real now, not just tracked -- a real per-position
        // instrumented comparison against aomenc's own real search (a second per-candidate SATD-model trace
        // layer added directly to libaom, logging every candidate this method's own real loop couldn't see)
        // found this exact gap directly, not by inference: at this project's own real target image's very
        // first encoder-vs-aomenc divergence (a 4x8px leaf, produced via EncodeRectangularLeaf, not
        // EncodeLeaf), aomenc's real search computes PAETH_PRED's model rd as 28 -- identical to H_PRED's own
        // 28 -- yet PRUNES PAETH_PRED outright (never gives it a real cost trial) while keeping H_PRED,
        // because by the time PAETH_PRED (5th in real search order) is evaluated, the top-3 shortlist is
        // already fully populated with 3 candidates <=24, while H_PRED (2nd) was evaluated before that
        // shortlist filled up -- exactly the order-dependent behavior this project's own PruneIntraYMode port
        // already reproduces faithfully for the *square* leaf path (EncodeLeaf). This method's own loop
        // previously never pruned anything at all (a pre-existing, disclosed "cheap enough not to need it"
        // scope decision, kept even after this session's own FILTER_INTRA-overshoot fix started tracking
        // bestModelRd here, deliberately without also skipping) -- meaning every rectangular leaf's own
        // PAETH_PRED (or any other candidate a real aomenc search would have thrown away) always got a real
        // cost trial and could win comparisons libaom's own real search never lets it compete in. Now a real,
        // full port: same topIntraModelRd/topModelCount/PruneIntraYMode machinery EncodeLeaf's own loop
        // already uses, generalized to this leaf's true widthPixels/heightPixels via ComputeIntraModelRd's own
        // now-rect-capable signature (see that method's own remarks).
        Span<long> topIntraModelRd = stackalloc long[4];
        int topModelCount = s.Lossless ? s.SpeedFeatures.TopIntraModelCountAllowed : 0;
        topIntraModelRd[..Math.Max(topModelCount, 0)].Fill(long.MaxValue);
        long bestModelRd = long.MaxValue;

        foreach (int mode in CandidateModes)
        {
            bool directional = Av1IntraMode.IsDirectional(mode) && angleDeltaAllowed;
            int minDelta = directional ? -MaxAngleDelta : 0;
            int maxDelta = directional ? MaxAngleDelta : 0;

            for (int angleDelta = minDelta; angleDelta <= maxDelta; angleDelta++)
            {
                if (topModelCount > 0)
                {
                    long modelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, widthPixels, heightPixels, ptype: 0, mode, angleDelta, useFilterIntra: false, filterIntraMode: 0, filterTypeSmooth, useRealBoundaryAvailability: true);
                    if (Av1IntraModelRdPruner.PruneIntraYMode(modelRd, ref bestModelRd, topIntraModelRd[..topModelCount]))
                    {
                        continue;
                    }
                }

                // Real y_mode/angle_delta signaling cost, exactly mirroring EncodeLeaf's own identical fix
                // (see its own remarks: without this, two structurally-similar modes with near-identical
                // residual cost fall to CandidateModes' own iteration order rather than to which one a real,
                // signaling-cost-aware search would actually prefer). Accumulated in raw, unrounded 1/512-bit
                // units alongside the residual cost below -- see EncodeLeaf's own identical precision-
                // propagation remarks for the full rationale -- rounding the combined total only once, right
                // before this candidate's own bestCost comparison. Computed BEFORE the residual call below
                // (same pure-reordering pattern as EncodeLeaf's own Phase 3 change) so it's available to
                // derive rectBoundUnits512.
                long signalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.IntraFrameYMode[yModeCtx0][yModeCtx1], mode);
                if (directional)
                {
                    signalingUnits512 += Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.AngleDelta[mode - Av1IntraMode.VPred], angleDelta + MaxAngleDelta);
                }

                // Phase 3's own bound-threading (project plan), same exact derivation as EncodeLeaf's own
                // plain mode/angle-delta loop -- see ComputeCandidateCostPrecise's remarks for the general
                // mechanism and that call site for the full rounding-identity derivation this reuses verbatim.
                long rectBoundUnits512 = bestCost == long.MaxValue ? long.MaxValue : (bestCost * 512) - 256 - signalingUnits512;

                ComputeRectLumaCostForModePrecise(s, r, c, wMi, hMi, mode, angleDelta, filterTypeSmooth, useRealBoundaryAvailability: true, out long residualUnits512, boundUnits512: rectBoundUnits512);

                long cost = (long)Math.Round((residualUnits512 + signalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = mode;
                    bestAngleDelta = angleDelta;
                }
            }
        }

        // palette_mode_info() (spec §5.11.46): real spec gate (AllowPalette, generalized to a rectangular
        // bSize -- this method has no sizeMi to reuse EncodeLeaf's own square-leaf shortcut with). Hoisted
        // here (was previously computed later, right before this same search) purely so Y-palette can move
        // before FILTER_INTRA -- see that search's own remarks, mirroring EncodeLeaf's own identical Phase 3
        // reordering (round N+23's own research: real libaom's av1_rd_pick_intra_sby_mode order is plain
        // modes -> palette -> filter_intra, tightening best_rd by pointer before filter_intra's own gate
        // reads it). Simpler here than in EncodeLeaf -- this method has no IntraBC of its own at all, so
        // there's no later `!usedIntrabc` re-gate needed; yPaletteEligible below is already the real, final
        // eligibility.
        bool paletteStructurallyPresent = s.Lossless
            && s.AllowScreenContentTools
            && Av1BlockTables.BlockWidth(bSize) <= 64
            && Av1BlockTables.BlockHeight(bSize) <= 64
            && bSize >= Av1BlockSize.Block8x8;

        // Y-PALETTE (spec §5.11.46), moved to run here -- see EncodeLeaf's own identical reordering and its
        // remarks for the full rationale. bestCost is tightened here exactly like libaom's own real best_rd
        // pointer-tightening, so the filter_intra loop below correctly treats palette as a real,
        // already-resolved alternative.
        int nY = 0;
        bool yAllZero = false;
        long yPaletteCost = long.MaxValue;
        bool yPaletteEligible = paletteStructurallyPresent && SearchLosslessYPalette(s, x, y, widthPixels, heightPixels, bestCost, r, c, availU, availL, out nY, out yAllZero, out yPaletteCost);
        if (yPaletteEligible && yPaletteCost < bestCost)
        {
            bestCost = yPaletteCost;
        }

        // FILTER_INTRA (spec §5.11.24) for a rectangular leaf -- previously always unattempted here (this
        // method's own doc comment used to disclose it as a real, deliberate follow-up gap): real AV1's own
        // eligibility gate is max(bw, bh) <= 32 (confirmed directly against Av1TileDecoder.FilterIntraModeInfo's
        // own identical check), which does NOT require squareness -- a 32x16/16x32/16x8/8x16/8x4/4x8
        // rectangular leaf is just as eligible as any square one of the same max dimension, only a
        // 64x32/32x64 leaf (this project's own largest rect shape) is excluded, exactly like a real 64x64
        // square leaf would be. Real aomenc's own filter_intra usage on this project's real target image
        // (9.68% area) versus this project's own pre-fix 1.85%-3.17% range was traced precisely to this gap:
        // once rectangular leaves started winning ~51% of the frame's own area (see the CostCdf/symmetric-
        // chroma-cost fix above), every one of those leaves structurally could not ever use filter_intra at
        // all, regardless of content -- not a search-quality gap, a real missing candidate. Mirrors
        // EncodeLeaf's own identical search exactly (same PruneFilterIntraLevel gating, same 5-mode sweep,
        // same real use_filter_intra/filter_intra_mode signaling-cost addition), via
        // ComputeRectLumaCostForMode's own new useFilterIntra/filterIntraMode parameters (generalizing its
        // existing per-4x4-sub-block trial to a filter_intra candidate exactly like it already does for
        // every regular CandidateModes entry).
        // See EncodeLeaf's identical remarks: real libaom tries filter_intra as a genuine competing
        // candidate regardless of which plain mode currently holds bestMode, not only when bestMode is
        // already DcPred -- FilterIntraModeUsedFlag[bestMode] below still keys off whatever plain mode is
        // winning so far (libaom's own best_mode_so_far), same as before, just no longer also gating entry.
        bool skipFilterIntraEntirely = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 2;
        int filterIntraModeMask = s.Lossless && s.SpeedFeatures.PruneFilterIntraLevel == 1 ? FilterIntraModeUsedFlag[bestMode] : 0x1F;

        bool bestUseFilterIntra = false;
        int bestFilterIntraMode = 0;
        if (Math.Max(widthPixels, heightPixels) <= 32 && !skipFilterIntraEntirely)
        {
            for (int filterMode = 0; filterMode < 5; filterMode++)
            {
                if ((filterIntraModeMask & (1 << filterMode)) == 0)
                {
                    continue;
                }

                // model_intra_yrd_and_prune -- see EncodeLeaf's identical remarks (PruneFilterIntraModelRd's
                // own doc comment) for the real libaom function/threshold this ports. Shares bestModelRd with
                // this method's own plain-mode loop above (tracked there, not pruned there -- see that loop's
                // own remarks).
                if (topModelCount > 0)
                {
                    long filterModelRd = ComputeIntraModelRd(s, s.SourceY, s.YWidth, s.YHeight, r, c, x, y, widthPixels, heightPixels, ptype: 0, mode: Av1IntraMode.DcPred, angleDelta: 0, useFilterIntra: true, filterIntraMode: filterMode, filterTypeSmooth, useRealBoundaryAvailability: true);
                    if (Av1IntraModelRdPruner.PruneFilterIntraModelRd(filterModelRd, ref bestModelRd))
                    {
                        continue;
                    }
                }

                // Real use_filter_intra + filter_intra_mode signaling cost -- see EncodeLeaf's own identical
                // precision-propagation remarks; accumulated in raw units alongside the residual cost below,
                // rounded once at the very end. Computed BEFORE the residual call below (same pure-reordering
                // pattern as EncodeLeaf's own Phase 3 change) so it's available to derive filterBoundUnits512.
                long filterSignalingUnits512 = Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntra[bSize], 1) + Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(s.CostCdf.FilterIntraMode, filterMode);

                // Phase 3's own bound-threading (project plan), same exact derivation as EncodeLeaf's own
                // filter_intra loop -- see ComputeCandidateCostPrecise's remarks for the general mechanism.
                long filterBoundUnits512 = bestCost == long.MaxValue ? long.MaxValue : (bestCost * 512) - 256 - filterSignalingUnits512;

                ComputeRectLumaCostForModePrecise(s, r, c, wMi, hMi, mode: Av1IntraMode.DcPred, angleDelta: 0, filterTypeSmooth, useRealBoundaryAvailability: true, out long filterResidualUnits512, useFilterIntra: true, filterIntraMode: filterMode, boundUnits512: filterBoundUnits512);

                long cost = (long)Math.Round((filterResidualUnits512 + filterSignalingUnits512) / 512.0, MidpointRounding.AwayFromZero);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = Av1IntraMode.DcPred;
                    bestAngleDelta = 0;
                    bestUseFilterIntra = true;
                    bestFilterIntraMode = filterMode;
                }
            }
        }

        // Real cost-based uv_mode/angle_delta_uv search (SearchRectUvMode -- see its own remarks): closes
        // this method's previously-disclosed "chroma stays DC_PRED-only" scope gap, exactly mirroring
        // EncodeLeaf's own SearchUvMode wiring. cflAllowed is computed here (not just at the later write
        // site) since SearchRectUvMode's own uv_mode CDF selection needs it for a real signaling-cost term,
        // matching SearchUvMode's identical cflAllowedForCost usage.
        bool cflAllowed = Av1BlockTables.GetPlaneResidualSize(bSize, 1, !s.Chroma444, !s.Chroma444) == Av1BlockSize.Block4x4;
        int bestUvMode = Av1IntraMode.DcPred;
        int bestUvAngleDelta = 0;
        long bestUvCost = long.MaxValue;
        if (hasChroma)
        {
            (bestUvMode, bestUvAngleDelta, bestUvCost) = SearchRectUvMode(s, r, c, wMi, hMi, availU, availL, bestMode, cflAllowed);
        }

        // paletteStructurallyPresent and Y-palette itself are now computed earlier (before the FILTER_INTRA
        // loop above), so Y-palette can run before it -- see that computation's own remarks. Eligibility is
        // still decided before skip, because a fully palette-covered leaf (both Y and UV independently <= 8
        // distinct values, same coupled all-or-nothing choice EncodeLeaf's own usedPalette makes -- see its
        // remarks on why a decoupled Y/UV attempt regressed graphic-content benchmarks) writes skip=1 and
        // never reaches the residual path below at all.
        //
        // SearchLosslessUvPalette (the same real, multi-strategy search EncodeLeaf's own square leaves use --
        // see their own remarks) is RD-gated against bestUvCost -- the real chroma mode search's own best
        // result above -- exactly mirroring EncodeLeaf's own bestUvCost -> SearchLosslessUvPalette wiring.
        int nUv = 0;
        bool uvAllZero = false;

        // Real chroma-scaling + real luma-block-size bsizeCtx -- see EncodeLeaf's own identical fix and
        // SearchLosslessUvPalette's own remarks for the two real, distinct bugs this addresses (chroma
        // pixel-array coordinates, and PaletteUvSize's own CDF context). Currently unreachable for
        // non-lossless regardless (rectangular partitions are lossless-only today), but fixed here too for
        // consistency and so this function is genuinely ready, not silently still broken, whenever that
        // separate restriction is lifted.
        int chromaX = s.Chroma444 ? x : x / 2;
        int chromaY = s.Chroma444 ? y : y / 2;
        int chromaWidthPixels = s.Chroma444 ? widthPixels : widthPixels / 2;
        int chromaHeightPixels = s.Chroma444 ? heightPixels : heightPixels / 2;
        int uvBsizeCtx = GetPaletteBsizeCtx(bSize);
        bool uvPaletteEligible = paletteStructurallyPresent && hasChroma && SearchLosslessUvPalette(s, chromaX, chromaY, chromaWidthPixels, chromaHeightPixels, bestUvCost, r, c, availU, availL, usedYPalette: yPaletteEligible, uvBsizeCtx, out nUv, out uvAllZero, out _);
        bool usedPalette = yPaletteEligible && (!hasChroma || uvPaletteEligible);
        int writtenYMode = usedPalette ? Av1IntraMode.DcPred : bestMode;
        int writtenUvMode = usedPalette ? Av1IntraMode.DcPred : bestUvMode;

        // Unlike this function's own previous, exact-match-only palette support, the new multi-strategy
        // search can find a genuinely cheaper candidate that still needs a real (if small) residual --
        // skip=1 is only legal when every plane's own residual is truly zero, mirroring EncodeLeaf's own
        // paletteAllZeroResidual exactly.
        bool paletteAllZeroResidual = usedPalette && yAllZero && (!hasChroma || uvAllZero);

        // skip: true only for a fully palette-covered, zero-residual leaf, exactly mirroring EncodeLeaf's
        // own Skip[skipCtx] write.
        if (s.Phase != Av1EncodePhase.Decide)
        {
            s.Symbols.WriteSymbol(s.Cdf.Skip[skipCtx], paletteAllZeroResidual ? 1 : 0);
        }

        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
        if (s.Phase != Av1EncodePhase.Emit)
        {
            s.DecideCommitSink.WriteSymbol(s.DecideCdf.Skip[skipCtx], paletteAllZeroResidual ? 1 : 0);
        }

        // use_intrabc (spec §5.11.7): structurally present whenever the frame allows it (tied to lossless AND
        // real content-based IntraBC detection, same as EncodeLeaf's intrabcStructurallyPresent), regardless
        // of this leaf's shape -- always 0 here.
        if (s.Lossless && s.AllowIntrabc)
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.Intrabc, 0);
            }

            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.Intrabc, 0);
            }
        }

        if (s.Phase != Av1EncodePhase.Decide)
        {
            s.Symbols.WriteSymbol(s.Cdf.IntraFrameYMode[yModeCtx0][yModeCtx1], writtenYMode);
        }

        if (s.Phase != Av1EncodePhase.Emit)
        {
            s.DecideCommitSink.WriteSymbol(s.DecideCdf.IntraFrameYMode[yModeCtx0][yModeCtx1], writtenYMode);
        }

        // intra_angle_info_y() (spec §5.11.42): gated on writtenYMode, not bestMode -- never fires when
        // palette overrides y_mode to DC_PRED, regardless of what the raw search's own bestMode was,
        // mirroring EncodeLeaf's own identical writtenYMode-gated write exactly.
        if (angleDeltaAllowed && Av1IntraMode.IsDirectional(writtenYMode))
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.AngleDelta[writtenYMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
            }

            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.AngleDelta[writtenYMode - Av1IntraMode.VPred], bestAngleDelta + MaxAngleDelta);
            }
        }

        if (hasChroma)
        {
            // Indexed by writtenYMode, not a hardcoded DC_PRED -- a real decoder derives this same
            // CDF-selection context from whatever y_mode value it just decoded (see EncodeLeaf's identical
            // remark on this same computation). cflAllowed was already computed above, before
            // SearchRectUvMode ran (which needs it too, for its own uv_mode signaling-cost term) -- reused
            // here rather than recomputed, since bSize/Chroma444 can't have changed in between.
            var uvModeCdf = cflAllowed ? s.Cdf.UvModeCflAllowed[writtenYMode] : s.Cdf.UvModeCflNotAllowed[writtenYMode];
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(uvModeCdf, writtenUvMode);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            var decideUvModeCdf = cflAllowed ? s.DecideCdf.UvModeCflAllowed[writtenYMode] : s.DecideCdf.UvModeCflNotAllowed[writtenYMode];
            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(decideUvModeCdf, writtenUvMode);
            }

            // intra_angle_info_uv() (spec §5.11.43): structurally present whenever this leaf's angle-delta
            // gate allows it and the searched uv_mode is directional, mirroring EncodeLeaf's own identical
            // writtenUvMode-gated write. Gated on writtenUvMode, not bestUvMode, for the same palette-override
            // reason as writtenYMode's own intra_angle_info_y write above.
            if (angleDeltaAllowed && Av1IntraMode.IsDirectional(writtenUvMode))
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.AngleDelta[writtenUvMode - Av1IntraMode.VPred], bestUvAngleDelta + MaxAngleDelta);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.AngleDelta[writtenUvMode - Av1IntraMode.VPred], bestUvAngleDelta + MaxAngleDelta);
                }
            }
        }

        // palette_mode_info() (spec §5.11.46): structurally present using the *real* spec gate
        // (AllowPalette -- Av1TileDecoder.AllowPalette, generalized to any block shape, not the old
        // square-only "sizeMi is >= 2 and <= 16" shortcut EncodeLeaf's own paletteStructurallyPresent still
        // uses, which has no meaning for a leaf with no single sizeMi). Getting this gate wrong wouldn't
        // just miss a compression opportunity -- it would desync the entropy stream by omitting/adding a
        // bit a real decoder does the opposite of. usedPalette/nY/nUv were already decided above (before the
        // skip bit), so this only has to write the real bits/colors now.
        if (paletteStructurallyPresent)
        {
            int bsizeCtx = GetPaletteBsizeCtx(bSize);

            // has_palette_y is structurally present whenever the actual written y_mode is DC_PRED, whether
            // that's because bestMode was naturally DC_PRED or because palette overrode it -- mirroring
            // EncodeLeaf's own identical writtenYMode-gated write exactly. Now a real, sometimes-false
            // condition: bestMode isn't unconditionally DC_PRED anymore (see the real mode search above), so
            // a non-palette directional/smooth/paeth winner correctly makes this bit structurally absent.
            if (writtenYMode == Av1IntraMode.DcPred)
            {
                int paletteModeCtx = GetPaletteModeCtx(s, r, c, availU, availL);
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteYMode[bsizeCtx][paletteModeCtx], usedPalette ? 1 : 0);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteYMode[bsizeCtx][paletteModeCtx], usedPalette ? 1 : 0);
                }

                if (usedPalette)
                {
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        s.Symbols.WriteSymbol(s.Cdf.PaletteYSize[bsizeCtx], nY - 2);
                        WritePaletteColorsY(s, s.PaletteColorsY, nY, r, c, availU, availL);
                    }

                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteYSize[bsizeCtx], nY - 2);
                    }
                }
            }

            // has_palette_uv is only structurally present when uv_mode == DC_PRED (spec §5.11.46) -- no
            // longer unconditionally true now that writtenUvMode is a real search winner (SearchRectUvMode)
            // rather than an unconditional DC_PRED: a leaf whose chroma search picks a directional/smooth
            // mode (writtenUvMode != DcPred, usedPalette false) must NOT write this bit at all, mirroring
            // Av1TileDecoder.PaletteModeInfo's own identical `_uvMode == Av1IntraMode.DcPred` gate exactly
            // (a real, found-via-round-trip-failure desync bug: the old unconditional `if (hasChroma)` gate
            // was only ever correct back when writtenUvMode really was always DC_PRED here).
            if (hasChroma && writtenUvMode == Av1IntraMode.DcPred)
            {
                int paletteUvModeCtx = usedPalette ? 1 : 0;
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.PaletteUvMode[paletteUvModeCtx], usedPalette ? 1 : 0);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteUvMode[paletteUvModeCtx], usedPalette ? 1 : 0);
                }

                if (usedPalette)
                {
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        s.Symbols.WriteSymbol(s.Cdf.PaletteUvSize[bsizeCtx], nUv - 2);
                        WritePaletteColorsUv(s, s.PaletteColorsU, s.PaletteColorsV, nUv, r, c, availU, availL);
                    }

                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        s.DecideCommitSink.WriteSymbol(s.DecideCdf.PaletteUvSize[bsizeCtx], nUv - 2);
                    }
                }
            }
        }

        // filter_intra_mode_info() (spec §5.11.24): real spec gate is
        // max(bw, bh) <= 32 && YMode == DC_PRED && PaletteSizeY == 0 -- see
        // Av1TileDecoder.FilterIntraModeInfo's identical check, mirroring EncodeLeaf's own
        // !usedPalette && bestMode == DcPred && sizePixels <= 32 gate exactly (bestMode, not writtenYMode:
        // when !usedPalette, the two are identical anyway -- see EncodeLeaf's own remark). PaletteSizeY == 0
        // is exactly !usedPalette (this leaf never partially uses Y-only palette -- see usedPalette's own
        // remarks above). filter_intra is now a real search winner (bestUseFilterIntra/bestFilterIntraMode,
        // see the real search above) rather than an unconditional 0.
        if (!usedPalette && bestMode == Av1IntraMode.DcPred && Math.Max(widthPixels, heightPixels) <= 32)
        {
            if (s.Phase != Av1EncodePhase.Decide)
            {
                s.Symbols.WriteSymbol(s.Cdf.FilterIntra[bSize], bestUseFilterIntra ? 1 : 0);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                s.DecideCommitSink.WriteSymbol(s.DecideCdf.FilterIntra[bSize], bestUseFilterIntra ? 1 : 0);
            }

            if (bestUseFilterIntra)
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    s.Symbols.WriteSymbol(s.Cdf.FilterIntraMode, bestFilterIntraMode);
                }

                if (s.Phase != Av1EncodePhase.Emit)
                {
                    s.DecideCommitSink.WriteSymbol(s.DecideCdf.FilterIntraMode, bestFilterIntraMode);
                }
            }
        }

        // A single coding block's own mode_info() governs every one of its transform_block() sub-blocks
        // (spec §5.11.35) -- computed once here (rather than inside the non-palette residual branch alone)
        // so both that branch's own per-sub-block Predict calls and the OnLeafCommitted diagnostic record
        // below see the same value; always 0 when usedPalette (writtenYMode is DC_PRED there, never
        // directional).
        int residualAngleDelta = angleDeltaAllowed && Av1IntraMode.IsDirectional(writtenYMode) ? bestAngleDelta : 0;

        // filter_intra counterpart to residualAngleDelta above: bestUseFilterIntra can only be true when
        // bestMode == DcPred already (see the real search above), but a palette-covered leaf (usedPalette)
        // never uses filter_intra prediction at all regardless -- it's real reconstruction is a direct
        // palette-color copy, not any real intra prediction -- so this must also gate on !usedPalette,
        // exactly mirroring the later filter_intra_mode_info() write site's own identical condition.
        bool residualUseFilterIntra = !usedPalette && bestUseFilterIntra;
        int residualFilterIntraMode = residualUseFilterIntra ? bestFilterIntraMode : 0;

        // Chroma counterpart to residualAngleDelta above, and the real chroma filter type (GetChromaFilterType,
        // subsampled: false -- lossless is always 4:4:4) -- both feed the real chroma residual commit loop
        // below, replacing that loop's previous hardcoded Av1IntraMode.DcPred/angleDelta: 0/filterTypeSmooth:
        // false now that writtenUvMode is a real search winner, not an unconditional DC_PRED.
        int residualUvAngleDelta = angleDeltaAllowed && Av1IntraMode.IsDirectional(writtenUvMode) ? bestUvAngleDelta : 0;
        bool chromaFilterTypeSmooth = hasChroma && GetChromaFilterType(s, r, c, availU, availL, subsampled: false);

        if (usedPalette && paletteAllZeroResidual)
        {
            // reset_block_context(bw4, bh4) (spec §5.11.5): this leaf is skip = 1, so none of the
            // WriteCoeffs calls below run -- without this, YCoeffCtx/UCoeffCtx/VCoeffCtx would keep
            // whatever an earlier, unrelated leaf last left in this leaf's own above/left slots, feeding a
            // real decoder's matching reset a stale context it never sees on this side. Mirrors EncodeLeaf's
            // identical reset, generalized from a single sizeMi to wMi/hMi.
            s.YCoeffCtx.Reset(c, wMi, r, hMi);

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            // Same reset as the real one just above.
            s.DecideYCoeffCtx.Reset(c, wMi, r, hMi);

            if (hasChroma)
            {
                s.UCoeffCtx!.Reset(c, wMi, r, hMi);
                s.VCoeffCtx!.Reset(c, wMi, r, hMi);

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same resets as the real ones just above.
                s.DecideUCoeffCtx!.Reset(c, wMi, r, hMi);
                s.DecideVCoeffCtx!.Reset(c, wMi, r, hMi);
            }

            // onscreenWidth/onscreenHeight (project plan Round N+36's own root-cause fix): see
            // WriteColorMapTokens' own remarks -- this leaf's own nominal footprint can overhang the frame's
            // TRUE (unpadded) edge (confirmed the real trigger for a genuine bitstream-desync bug this round,
            // via a leaf exactly like this one). Lossless is always chroma444, so luma/chroma share bounds.
            int onscreenWidth = Math.Min(widthPixels, (s.TrueMiCols - c) * 4);
            int onscreenHeight = Math.Min(heightPixels, (s.TrueMiRows - r) * 4);

            // SearchLosslessYPalette/SearchLosslessUvPalette already built the winning candidate's own color
            // map directly into PaletteColorMap/PaletteColorMapUv as a side effect of finding it -- no
            // rebuild needed (see EncodeLeaf's identical remark).
            if (s.Phase != Av1EncodePhase.Decide)
            {
                WriteColorMapTokens(s, s.PaletteColorMap, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nY, s.Cdf.PaletteYColorIndex);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                WriteColorMapTokens(s.DecideCommitSink, s.PaletteColorMap, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nY, s.DecideCdf.PaletteYColorIndex);
            }

            CopyColorMapToFrameGrid(s.PaletteColorMapYGrid, s.YWidth, s.PaletteColorMap, widthPixels, x, y, onscreenWidth, onscreenHeight);

            // Reconstruction is exact by construction (paletteAllZeroResidual means every source sample in
            // this leaf mapped to its palette color with zero error), so copying straight from source is
            // simpler than -- and produces identical results to -- looking each pixel back up through the
            // palette + color map just written.
            for (int i = 0; i < heightPixels; i++)
            {
                Array.Copy(s.SourceY, ((y + i) * s.YWidth) + x, s.ReconY, ((y + i) * s.YWidth) + x, widthPixels);
            }

            if (hasChroma)
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    WriteColorMapTokens(s, s.PaletteColorMapUv, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nUv, s.Cdf.PaletteUvColorIndex);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    WriteColorMapTokens(s.DecideCommitSink, s.PaletteColorMapUv, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nUv, s.DecideCdf.PaletteUvColorIndex);
                }

                CopyColorMapToFrameGrid(s.PaletteColorMapUvGrid, s.ChromaWidth, s.PaletteColorMapUv, widthPixels, x, y, onscreenWidth, onscreenHeight);

                for (int i = 0; i < heightPixels; i++)
                {
                    int rowOffset = ((y + i) * s.ChromaWidth) + x;
                    Array.Copy(s.SourceU!, rowOffset, s.ReconU!, rowOffset, widthPixels);
                    Array.Copy(s.SourceV!, rowOffset, s.ReconV!, rowOffset, widthPixels);
                }
            }

            MarkLumaBlockDecoded(s, r, c, wMi, hMi);
            if (hasChroma)
            {
                MarkChromaBlockDecoded(s, r, c, wMi, hMi);
            }
        }
        else if (usedPalette)
        {
            // Non-zero-residual palette (see the multi-strategy search's own remarks on this new capability
            // for a rectangular leaf): skip = 0, so this carries a real per-plane WHT residual on top of the
            // palette prediction, exactly mirroring EncodeLeaf's own identical branch. No manual
            // coefficient-context reset needed (WriteCoeffs runs for real below).
            // See the all-zero-residual branch's identical onscreenWidth/onscreenHeight remarks above.
            int onscreenWidth = Math.Min(widthPixels, (s.TrueMiCols - c) * 4);
            int onscreenHeight = Math.Min(heightPixels, (s.TrueMiRows - r) * 4);

            if (s.Phase != Av1EncodePhase.Decide)
            {
                WriteColorMapTokens(s, s.PaletteColorMap, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nY, s.Cdf.PaletteYColorIndex);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                WriteColorMapTokens(s.DecideCommitSink, s.PaletteColorMap, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nY, s.DecideCdf.PaletteYColorIndex);
            }

            CopyColorMapToFrameGrid(s.PaletteColorMapYGrid, s.YWidth, s.PaletteColorMap, widthPixels, x, y, onscreenWidth, onscreenHeight);

            if (hasChroma)
            {
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    WriteColorMapTokens(s, s.PaletteColorMapUv, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nUv, s.Cdf.PaletteUvColorIndex);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    WriteColorMapTokens(s.DecideCommitSink, s.PaletteColorMapUv, widthPixels, heightPixels, onscreenWidth, onscreenHeight, nUv, s.DecideCdf.PaletteUvColorIndex);
                }

                CopyColorMapToFrameGrid(s.PaletteColorMapUvGrid, s.ChromaWidth, s.PaletteColorMapUv, widthPixels, x, y, onscreenWidth, onscreenHeight);
            }

            EncodePaletteResidual(s, s.SourceY, s.ReconY, s.YWidth, ptype: 0, r, c, x, y, widthPixels, heightPixels, s.PaletteColorMap, widthPixels, s.PaletteColorsY, s.YCoeffCtx, blockDecodedPlane: 0, s.DecideYCoeffCtx);

            if (hasChroma)
            {
                // U and V are two independent single-channel planes sharing the same color map index --
                // reuses EncodePaletteResidual once per plane rather than a combined UV variant, exactly
                // mirroring EncodeLeaf's identical chroma handling.
                EncodePaletteResidual(s, s.SourceU!, s.ReconU!, s.ChromaWidth, ptype: 1, r, c, x, y, widthPixels, heightPixels, s.PaletteColorMapUv, widthPixels, s.PaletteColorsU, s.UCoeffCtx!, blockDecodedPlane: 1, s.DecideUCoeffCtx!);
                EncodePaletteResidual(s, s.SourceV!, s.ReconV!, s.ChromaWidth, ptype: 1, r, c, x, y, widthPixels, heightPixels, s.PaletteColorMapUv, widthPixels, s.PaletteColorsV, s.VCoeffCtx!, blockDecodedPlane: 2, s.DecideVCoeffCtx!);
            }
        }
        else
        {
        // residual() (spec §5.11.34): real per-4x4 WHT residual, Y then U then V (plane-major, matching
        // spec's own residual() loop and EncodeChromaRegion's identical ordering) -- skip is always 0 here,
        // so this always runs for every plane, never the reset_block_context() shortcut.
        int nW = wMi;
        int nH = hMi;
        var above = s.SubBlockAbove;
        var left = s.SubBlockLeft;
        var pred = s.Pred;
        var residual = s.Residual;
        var coeff = s.Coeff;
        var levels = s.Levels;

        for (int dr = 0; dr < nH; dr++)
        {
            for (int dc = 0; dc < nW; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > s.EdgeMaxX || subY > s.EdgeMaxY)
                {
                    continue;
                }

                bool subAvailU = subR > 0;
                bool subAvailL = subC > 0;

                int subBlockMiRow = subR & s.SbMiMask;
                int subBlockMiCol = subC & s.SbMiMask;
                bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + 1);
                bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + 1, subBlockMiCol - 1);

                Av1IntraPrediction.BuildEdges(above, left, s.ReconY, s.YWidth, subX, subY, 4, 4, subAvailL, subAvailU, haveAboveRight, haveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, writtenYMode, subAvailL, subAvailU, residualUseFilterIntra, residualFilterIntraMode, angleDelta: residualAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, subX, subY, bitDepth: 8);

                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                if (s.Phase != Av1EncodePhase.Decide)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same arguments as the real commit just above.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 0, subC, subR, s.DecideYCoeffCtx, writeLumaTxType: null, blockSize: widthPixels, blockHeight: heightPixels);
                }

                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, 0, subBlockMiRow, subBlockMiCol, true);
            }
        }

        if (hasChroma)
        {
            // 4:4:4 always, for lossless (see the class-level remarks) -- chroma shares luma's exact
            // position/footprint 1:1, no subsampling shift anywhere in this loop.
            //
            // planeIndex (1 for U's own pass, 2 for V's own pass -- real per-plane BlockDecoded tracking,
            // matching EncodeChromaRegion's own identical planeIndex pattern, the square-leaf path's real,
            // already-correct implementation of this same thing) -- NOT the single hardcoded plane-1 this
            // loop used to query/set for BOTH U and V regardless of which one was actually running. That
            // was a real, confirmed round-trip correctness bug (found via a genuine pixel-corruption
            // investigation, not inference): U's own pass, run to completion first, would call
            // SetBlockDecoded for *both* plane 1 and plane 2 at every sub-block it touched -- so by the time
            // V's own pass reached a sub-block whose own haveBelowLeft/haveAboveRight query looked at a
            // position U's pass had already visited (later in this leaf's own row-major order, e.g. a
            // below-left neighbor on the leaf's own next row), it would incorrectly read "already decoded"
            // even though V's own reconstruction for that spatial position hadn't happened yet. A real
            // decoder tracks each of the three planes' own BlockDecoded state completely independently
            // (Av1TileDecoder.TransformBlock's own per-plane SetBlockDecoded call, one call per plane per
            // sub-block, never two together) -- so encode-time edge availability for V could silently differ
            // from what a real decode of the same bitstream computes, changing V's own predicted samples
            // (and hence its final reconstructed pixels) even though every entropy symbol -- mode, angle
            // delta, and even the coefficient levels themselves -- decodes back byte-for-byte correctly.
            int planeIndex = 0;
            foreach (var (source, recon, ctx, decideCtx) in new[]
            {
                (s.SourceU!, s.ReconU!, s.UCoeffCtx!, s.DecideUCoeffCtx!),
                (s.SourceV!, s.ReconV!, s.VCoeffCtx!, s.DecideVCoeffCtx!),
            })
            {
                planeIndex++;

                for (int dr = 0; dr < nH; dr++)
                {
                    for (int dc = 0; dc < nW; dc++)
                    {
                        int subX = x + (dc * 4);
                        int subY = y + (dr * 4);
                        int subR = r + dr;
                        int subC = c + dc;

                        // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                        // EncodeLosslessLumaResidual's identical remarks on why this needs no other
                        // bookkeeping (chroma's own true edge bound, ChromaEdgeMaxX/Y).
                        if (subX > s.ChromaEdgeMaxX || subY > s.ChromaEdgeMaxY)
                        {
                            continue;
                        }

                        bool subAvailU = subR > 0;
                        bool subAvailL = subC > 0;

                        int subBlockMiRow = subR & s.SbMiMask;
                        int subBlockMiCol = subC & s.SbMiMask;
                        bool haveAboveRight = GetBlockDecoded(s, planeIndex, subBlockMiRow - 1, subBlockMiCol + 1);
                        bool haveBelowLeft = GetBlockDecoded(s, planeIndex, subBlockMiRow + 1, subBlockMiCol - 1);

                        Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, subX, subY, 4, 4, subAvailL, subAvailU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);
                        Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, writtenUvMode, subAvailL, subAvailU, useFilterIntra: false, filterIntraMode: 0, angleDelta: residualUvAngleDelta, enableIntraEdgeFilter: true, chromaFilterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, subX, subY, bitDepth: 8);

                        for (int i = 0; i < 4; i++)
                        {
                            int rowBase = ((subY + i) * s.ChromaWidth) + subX;
                            int predRowBase = i * 4;
                            for (int j = 0; j < 4; j++)
                            {
                                residual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                            }
                        }

                        Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                        Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                        for (int i = 0; i < 4; i++)
                        {
                            Array.Copy(pred, i * 4, recon, ((subY + i) * s.ChromaWidth) + subX, 4);
                        }

                        int chromaBlockSizeArg = (nW * nH > 1) ? widthPixels : 0;
                        int chromaBlockHeightArg = (nW * nH > 1) ? heightPixels : 0;
                        if (s.Phase != Av1EncodePhase.Decide)
                        {
                            Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, subC, subR, ctx, writeLumaTxType: null, blockSize: chromaBlockSizeArg, blockHeight: chromaBlockHeightArg);
                        }

                        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's
                        // own remarks. Same arguments as the real commit just above.
                        if (s.Phase != Av1EncodePhase.Emit)
                        {
                            Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 1, subC, subR, decideCtx, writeLumaTxType: null, blockSize: chromaBlockSizeArg, blockHeight: chromaBlockHeightArg);
                        }

                        Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                        SetBlockDecoded(s, planeIndex, subBlockMiRow, subBlockMiCol, true);
                    }
                }
            }
        }
        }

        // Leaf-state bookkeeping, mirroring EncodeLeaf's own identical loop -- neighbor context (yMode,
        // skip, palette sizes/colors, mv/is_inter) that later leaves' own writes read.
        int paletteSizeY = usedPalette ? nY : 0;
        int paletteSizeUV = usedPalette && hasChroma ? nUv : 0;
        for (int dy = 0; dy < hMi; dy++)
        {
            int rowIdx = (r + dy) * s.MiCols;
            for (int dx = 0; dx < wMi; dx++)
            {
                int idx = rowIdx + c + dx;
                s.YModes[idx] = writtenYMode;
                s.UvModes[idx] = writtenUvMode;
                s.MiSizes[idx] = bSize;

                // Two-pass tile-encoder architecture, Stage 1a -- see TileState's own grid remarks.
                // residualAngleDelta/residualUvAngleDelta/residualUseFilterIntra/residualFilterIntraMode
                // (computed above, this leaf's own real, already-override-applied final values) mirror
                // EncodeLeaf's identically-purposed leafAngleDeltaY/leafAngleDeltaUv/leafUseFilterIntra/
                // leafFilterIntraMode exactly. No TxTypeGrid write here: this function's own residual path
                // is always WHT (lossless-only, see its own remarks), so tx_type is never searched or
                // signalled for a rectangular leaf -- TxTypeGrid stays at its harmless DctDct default.
                s.AngleDeltaYGrid[idx] = residualAngleDelta;
                s.AngleDeltaUvGrid[idx] = residualUvAngleDelta;
                s.FilterIntraGrid[idx] = residualUseFilterIntra;
                s.FilterIntraModeGrid[idx] = residualFilterIntraMode;

                // paletteAllZeroResidual, not usedPalette: this must mirror the *real* skip bit this leaf
                // wrote (s.Symbols.WriteSymbol(Skip[skipCtx], paletteAllZeroResidual ? 1 : 0) above) -- the
                // two were identical back when this function's own palette support was exact-match-only
                // (usedPalette implied zero residual unconditionally), but the multi-strategy search can now
                // make usedPalette true with a real nonzero residual (skip=0) too. Writing usedPalette here
                // unconditionally recorded a stale "skip=1" for every later leaf's own skipCtx derivation
                // whenever a non-zero-residual palette leaf was used, desyncing the entropy stream for
                // everything that followed even though this leaf's own symbols decoded correctly (skipCtx
                // only affects which leaf's own skip bit gets read next, not this leaf's own bits) -- a real,
                // found-via-round-trip-test bug, not a latent/theoretical one.
                s.Skips[idx] = paletteAllZeroResidual;
                s.PaletteSizesY[idx] = paletteSizeY;
                s.PaletteSizesUV[idx] = paletteSizeUV;
                int colorBase = idx * 8;
                for (int k = 0; k < 8; k++)
                {
                    s.PaletteColorsYGrid[colorBase + k] = s.PaletteColorsY[k];
                    s.PaletteColorsUGrid[colorBase + k] = s.PaletteColorsU[k];
                }

                s.IsInters[idx] = false;
                s.MvRowsGrid[idx] = 0;
                s.MvColsGrid[idx] = 0;
                s.Written[idx] = true;
            }
        }

        if (s.OnLeafCommitted is not null)
        {
            // EstimatedCost is deliberately omitted (left null) here: TileState.PartitionDecisions is keyed
            // by the *square parent's* own (r, c, sizeMi) that chose Horz/Vert, not by either individual
            // child -- its Cost is the combined total for both halves together, not this one child's own
            // share, so surfacing it here under this child's own record would misattribute the parent's
            // whole cost as if it were this leaf's alone.
            s.OnLeafCommitted(new Av1BlockDecisionRecord
            {
                R = r,
                C = c,
                WidthMi = wMi,
                HeightMi = hMi,
                YMode = writtenYMode,
                AngleDeltaY = residualAngleDelta,
                UvMode = writtenUvMode,
                AngleDeltaUv = residualUvAngleDelta,
                Skip = paletteAllZeroResidual,
                PaletteSizeY = paletteSizeY,
                PaletteSizeUV = paletteSizeUV,
                PaletteColorsY = paletteSizeY > 0 ? string.Join(',', s.PaletteColorsY[..paletteSizeY]) : string.Empty,
                PaletteColorsUV = paletteSizeUV > 0 ? string.Join(',', s.PaletteColorsU[..paletteSizeUV]) : string.Empty,
                UsedIntrabc = false,
                MvRow = 0,
                MvCol = 0,
                UseFilterIntra = residualUseFilterIntra,
                FilterIntraMode = residualFilterIntraMode,
                EstimatedCost = null,
            });
        }

        // Deliberately no RecordIntrabcHashEntry call -- see the class-level remarks on why a rectangular
        // leaf is never offered as a future IntraBC copy source in this first increment.
    }

    // ---- IntraBC (spec §5.11.7's use_intrabc branch / §7.10.2 find_mv_stack / §5.11.31 MV syntax) ----
    //
    // Write-side mirror of Av1TileDecoder's own IntraBC subsystem -- see that type's FindMvStack/ReadMv/
    // ReadMvComponent remarks for the general reasoning (GlobalMvs always zero, no temporal scan, extra
    // search degenerates to a zero-fill, force_integer_mv always 1 so mv_class0_fr/hp/fr/hp are never
    // written). The DV *search* itself (deciding WHICH source position to copy from, if any) has no decoder
    // equivalent -- see FindIntrabcMatch's remarks.

    private const int MaxRefMvStackSize = 8;
    private const int RefCatLevel = 640;
    private const int MvBorder = 128;
    private const int IntrabcDelayPixels = 256;
    private const int IntrabcDelaySb64 = 4;
    private const int MvJointHzvnz = 2;
    private const int MvJointHnzvz = 1;
    private const int MvJointHnzvnz = 3;
    private const int MvClass0 = 0;

    private sealed class MvSearchState
    {
        public int NumMvFound;
        public readonly int[,] RefStackMv = new int[MaxRefMvStackSize, 2];
        public readonly int[] WeightStack = new int[MaxRefMvStackSize];
    }

    private static bool IsInsideEnc(TileState s, int row, int col) => row >= 0 && row < s.MiRows && col >= 0 && col < s.MiCols;

    /// <summary>
    /// Finds an exact-pixel-match copy source for the <paramref name="sizeMi"/>-sized leaf at (r, c), if one
    /// exists among positions <see cref="IsValidIntrabcSourcePixels"/> allows as a copy source (see that method's
    /// own remarks -- the same wavefront-reachability rule that already made incremental "was this position
    /// really encoded before me" tracking unnecessary; a whole-frame-upfront table is exactly as safe a
    /// source of candidates as the old incrementally-recorded one). Backed by <see cref="TileState.IntrabcHashTable"/>,
    /// a literal port of libaom's own whole-frame hash-table construction (<see cref="Av1IntrabcHashTable"/>'s
    /// own remarks) -- unlike that table's predecessor (a same-size-only content hash populated leaf-by-leaf
    /// as this encoder's own traversal committed each one), this indexes every valid position at every one of
    /// AV1's 6 square IntraBC sizes up front, so a match can be found against a differently-partitioned
    /// region of an already-encoded area, not just a leaf whose own chosen size happened to equal this one's.
    ///
    /// Among every verified candidate, picks the one with the lowest real DV-signaling cost
    /// (<see cref="EstimateMvCost"/> against this leaf's own real DV predictor,
    /// <see cref="FindMvStackAndPredict"/>) -- <see cref="ComputeIntrabcLumaCost"/> can't be reused for this
    /// ranking, since its residual-only cost is near-zero for any exact match regardless of how far away the
    /// source is. The scan itself is capped to the first <see cref="Av1SpeedFeatures.PruneIntrabcCandidateBlockHashSearch"/>-gated
    /// 64 candidates in the table's own insertion (raster) order, mirroring libaom's own real query-time
    /// bound (<c>AOMMIN(64, count)</c>, <c>mcomp.c</c>) -- see <see cref="Av1IntrabcHashTable"/>'s own remarks
    /// for why this substitutes for libaom's dispersed-insertion/256-cap machinery instead of porting it too.
    ///
    /// Only ever returns a match with an even luma displacement in both axes when this frame has subsampled
    /// chroma (4:2:0): this guarantees the chroma DV (which is the luma DV divided by 2, spec §7.11.3.3)
    /// lands on a whole chroma pixel with zero fractional phase, so the decoder's block-copy prediction
    /// (<see cref="Av1InterPrediction.PredictIntrabc"/>) is a pure copy on every plane -- letting this method
    /// verify the match with a direct pixel comparison instead of having to replicate the decoder's bilinear
    /// blend logic here too. 4:4:4/monochrome have no such restriction (no subsampling to create a
    /// fractional chroma phase in the first place).
    /// </summary>
    /// <summary>
    /// Decision-phase IntraBC search (project plan "lossless decision-phase real-cost parity" Step 5),
    /// called from <see cref="RdPickPartition"/>'s own fully-interior NONE candidate: tries the exact same
    /// real search <see cref="EncodeLeaf"/>'s own final, commit-time IntraBC decision uses
    /// (<see cref="FindIntrabcMatch"/>/<see cref="FindApproximateIntrabcMatch"/>), and if a candidate is
    /// found, returns its real total cost -- MV signaling plus either zero (exact match, byte-identical by
    /// construction) or <see cref="ComputeIntrabcRealResidualCost"/>'s own real per-4x4-sub-block WHT/
    /// quantize/entropy residual trial (approximate match), the identical primitive <see cref="EncodeLeaf"/>
    /// uses for its own final decision. Returns <see cref="long.MaxValue"/> when no candidate is found or
    /// none beats <paramref name="intraAlternativeTotal"/>, the real, already-computed luma+chroma(+palette)
    /// total this candidate is competing against (mirroring <see cref="EncodeLeaf"/>'s own
    /// <c>intraAlternativeTotal</c>).
    ///
    /// <para><b>Why this is safe to call speculatively, before this node's own partition choice (or any of
    /// its ancestors') is decided</b> -- confirmed by direct tracing of every real state read along this
    /// whole call chain, the same "confirm a term's real dependencies before reusing it" discipline Step 4's
    /// own scoping addendum used to find the analogous <c>ReconU</c>/<c>ReconV</c> risk: every read reachable
    /// from here is either (a) <see cref="TileState.SourceY"/>/<c>SourceU</c>/<c>SourceV</c> (a whole-frame,
    /// always-available buffer, never gated on commit progress -- <see cref="BlockPixelsEqual"/>,
    /// <see cref="ComputeIntrabcRealPlaneUnits512"/>'s own <c>source</c> parameter), (b)
    /// <see cref="TileState.ReconY"/>/<c>ReconU</c>/<c>ReconV</c> read <em>only</em> at a candidate source
    /// position already proven <see cref="TileState.Written"/> by <see cref="IsSourceFootprintWritten"/>
    /// before any pixel read happens -- this leaf's own (not-yet-decided) destination footprint can provably
    /// never be among them, so unlike <c>SearchUvMode</c>'s own former common-size branch, there is no
    /// not-yet-committed-sibling risk here at all, or (c) <see cref="TileState.Written"/>/<c>IsInters</c>/
    /// <c>MiSizes</c> (<see cref="FindMvStackAndPredict"/>'s own neighbor scan, <see cref="ScanRow"/>/
    /// <see cref="ScanCol"/>/<see cref="ScanPoint"/>/<see cref="AddRefMvCandidate"/>), each a fresh
    /// <c>new[]</c>-allocated, per-mi-position, write-once-by-whichever-leaf-ultimately-owns-it array: for a
    /// not-yet-committed sibling these simply read their own true "never written yet" default (<c>false</c>/
    /// <c>0</c>), which <see cref="AddRefMvCandidate"/>'s own <c>IsInters</c> gate and
    /// <see cref="IsSourceFootprintWritten"/> both already treat as "not a real candidate" -- a real but
    /// bounded completeness loss (a not-yet-committed sibling's own IntraBC usage, or a same-superblock
    /// approximate-match source, simply can't be found yet), never a correctness risk, since a default value
    /// can never be mistaken for genuine committed content the way a reused pixel buffer's own stale write
    /// could. <see cref="TileState.PositionsBySize"/>/<c>IntrabcSignatureIndex</c> (the approximate search's
    /// own candidate lists) are populated only by <see cref="RecordIntrabcHashEntry"/>, called only for a
    /// "just-fully-encoded" real commit, so they too can only ever contain genuinely already-committed
    /// positions from prior superblocks (this encoder decides and commits one whole superblock at a time --
    /// see <see cref="DecideSuperblockPartition"/>'s own call site). Deliberately omits the real skip-bit
    /// cost term <see cref="EncodeLeaf"/>'s own final decision includes (<c>EstimateSymbolCost(Skip[skipCtx], ...)</c>):
    /// unlike everything above, <c>skipCtx</c> reads <see cref="TileState.Skips"/> for the above/left
    /// neighbor directly, unconditionally -- for a not-yet-committed sibling that genuinely would be a
    /// stale-default read masquerading as real context (the same class of risk <see cref="GetFilterType"/>/
    /// <c>GetChromaFilterType</c> were already found and deliberately excluded from the decision phase for),
    /// so this comparison treats both alternatives' own skip-bit cost as a wash, exactly like
    /// <see cref="LeafOtherSignalingCost"/>'s own existing flat-epsilon treatment of it for the intra
    /// candidate already.</para>
    /// </summary>
    private static long EstimateIntrabcCost(TileState s, int r, int c, int sizeMi, bool hasChroma, long intraAlternativeTotal)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;
        int bSize = BlockSizeFromSizeMi(sizeMi);

        bool intrabcExactCandidate = FindIntrabcMatch(s, r, c, sizeMi, hasChroma, out int intrabcMvRow, out int intrabcMvCol);
        bool intrabcApproxCandidate = false;
        if (!intrabcExactCandidate)
        {
            intrabcApproxCandidate = FindApproximateIntrabcMatch(s, r, c, sizeMi, bSize, intraAlternativeTotal, out intrabcMvRow, out intrabcMvCol);
        }

        if (!intrabcExactCandidate && !intrabcApproxCandidate)
        {
            return long.MaxValue;
        }

        var (predMvRow, predMvCol) = FindMvStackAndPredict(s, r, c, bSize);

        if (intrabcExactCandidate)
        {
            // Zero residual by construction (FindIntrabcMatch only ever returns a source already verified
            // byte-identical, luma and chroma alike -- see its own remarks), so the real total is pure,
            // exactly-computable signaling, exactly like EncodeLeaf's own identical exact-match case.
            return EstimateMvCost(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol);
        }

        long knownSignaling = EstimateMvCost(s, intrabcMvRow, intrabcMvCol, predMvRow, predMvCol);

        // Same bound-threading derivation as EncodeLeaf's own identical approximate-match case (see
        // ComputeCandidateCostPrecise's own remarks for the general mechanism).
        long residualBoundUnits512 = intraAlternativeTotal == long.MaxValue
            ? long.MaxValue
            : ((intraAlternativeTotal - knownSignaling) * 512) - 256;
        long residualCost = ComputeIntrabcRealResidualCost(s, x, y, intrabcMvRow, intrabcMvCol, hasChroma, sizePixels, residualBoundUnits512);
        return residualCost == long.MaxValue ? long.MaxValue : knownSignaling + residualCost;
    }

    private static bool FindIntrabcMatch(TileState s, int r, int c, int sizeMi, bool hasChroma, out int mvRow, out int mvCol)
    {
        mvRow = 0;
        mvCol = 0;
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        var candidates = s.IntrabcHashTable?.GetCandidates(sizePixels, x, y);
        if (candidates is null || candidates.Count == 0)
        {
            return false;
        }

        int bSize = BlockSizeFromSizeMi(sizeMi);
        var (predMvRow, predMvCol) = FindMvStackAndPredict(s, r, c, bSize);

        int scanCount = s.SpeedFeatures.PruneIntrabcCandidateBlockHashSearch ? Math.Min(64, candidates.Count) : candidates.Count;
        bool found = false;
        long bestCost = 0;

        for (int i = 0; i < scanCount; i++)
        {
            var (srcX, srcY) = candidates[i];
            int deltaRow = srcY - y;
            int deltaCol = srcX - x;
            if (deltaRow == 0 && deltaCol == 0)
            {
                continue;
            }

            if (hasChroma && !s.Chroma444 && ((deltaRow & 1) != 0 || (deltaCol & 1) != 0))
            {
                continue;
            }

            // IsValidIntrabcSourcePixels directly, NOT the mi-unit IsValidIntrabcSource wrapper: unlike the
            // old leaf-only index (every candidate necessarily mi-aligned, since it only ever recorded a
            // committed leaf's own top-left corner), Av1IntrabcHashTable indexes every raw pixel offset --
            // real AV1 DVs have no alignment requirement (see IsValidIntrabcSourcePixels's own remarks).
            // Rounding srcX/srcY down to mi units first (srcX / 4, srcY / 4) would silently validate a
            // different, truncated position than the one actually used as the copy source whenever either
            // isn't already a multiple of 4 -- confirmed as a real, reproducible decode-corruption bug via
            // this project's own round-trip tests (GraphicContentImage_LosslessAvif at 256x256/512x512:
            // decoded pixels diverged from source, reading zeroed/not-yet-reconstructed memory, exactly the
            // signature of a wrongly-accepted "future" source position slipping past a validity check that
            // was quietly checking a different, earlier-rounded position instead).
            if (!IsValidIntrabcSourcePixels(s, x, y, sizePixels, srcX, srcY))
            {
                continue;
            }

            if (!IsSourceFootprintWritten(s, srcX, srcY, sizePixels))
            {
                // The geometric wavefront check above proves a *real, spec-conformant* decoder would already
                // have this position decoded by the time it reaches the current one -- but it doesn't know
                // anything about this *specific* encoder's own already-made partition choices, only the
                // superblock-level structure every AV1 bitstream shares. This encoder's own traversal can
                // leave a geometrically-wavefront-legal region not yet actually committed (e.g. a later
                // sibling in the current superblock's own partition recursion) -- Written is this encoder's
                // own ground truth for "has this exact leaf already been encoded," and is authoritative where
                // the geometric rule is only a conformance-shaped approximation. Skipping a candidate here
                // costs nothing but a missed match; accepting one that fails this would corrupt the decode
                // (confirmed via a real round-trip regression on GraphicContentImage_LosslessAvif before this
                // check existed -- decoded pixels read zeroed, not-yet-reconstructed memory).
                continue;
            }

            if (!BlockPixelsEqual(s, x, y, srcX, srcY, sizePixels, hasChroma))
            {
                // Hash collision, not a real match.
                continue;
            }

            int candMvRow = deltaRow * 8;
            int candMvCol = deltaCol * 8;
            long cost = EstimateMvCost(s, candMvRow, candMvCol, predMvRow, predMvCol);
            if (!found || cost < bestCost)
            {
                found = true;
                bestCost = cost;
                mvRow = candMvRow;
                mvCol = candMvCol;
            }
        }

        return found;
    }

    /// <summary>
    /// <c>is_mv_valid</c>'s IntraBC region-reachability check (spec §6.10.25 / libaom's <c>av1_is_dv_valid</c>),
    /// specialized: this encoder is always single-tile (MiRowStart/MiColStart are always 0, MiRowEnd/MiColEnd
    /// are always MiRows/MiCols). The spec's <c>bw &lt; 8 &amp;&amp; subsampling_x</c> / <c>bh &lt; 8 &amp;&amp;
    /// subsampling_y</c> edge adjustments are omitted: every leaf this encoder ever produces is at least 8x8
    /// (see the class-level remarks), so they can never apply. Getting this wrong wouldn't break
    /// round-tripping through this project's own decoder (which doesn't enforce it at read time -- see
    /// Av1TileDecoder.ReadMv's remarks), only real-world conformance against a third-party decoder (dav1d,
    /// libaom) that does -- an AVIF whose only purpose is to round-trip through this project's own codec
    /// wouldn't need this at all, but real interop is the actual point of writing AVIF files, so it's
    /// implemented in full rather than approximated. IntraBC now runs under both lossless (always 128x128
    /// superblocks, since PR #80) and non-lossless (project plan Phase 4, exact-match only -- always 64x64
    /// superblocks) -- see the <c>gradient</c> computation below, which derives the real superblock-size
    /// adjustment from <see cref="TileState.SbMiMask"/> rather than losslessness directly.
    ///
    /// <para>Takes raw pixel coordinates, not mi units: real AV1 DVs are <em>not</em> constrained to another
    /// coding block's own top-left corner (confirmed against libaom's own <c>av1_is_dv_valid</c>,
    /// <c>av1/common/mvref_common.h</c>: the only alignment requirement is whole-pixel, <c>(dv.row | dv.col) &amp; 7
    /// == 0</c> in spec's 1/8-luma-sample units, matching <c>force_integer_mv</c> -- there's no further
    /// requirement that the referenced position be a multiple of 4 pixels, let alone line up with some other
    /// leaf's own boundary). Every call site must pass <paramref name="srcX"/>/<paramref name="srcY"/> as
    /// received, never rounded to an mi/4-pixel grid first: <see cref="Av1IntrabcHashTable"/> indexes every
    /// raw pixel offset, not just leaf-aligned ones, so truncating first would validate a different, rounded
    /// position than the one actually used as the copy source -- exactly the bug this method's own remarks
    /// on <see cref="FindIntrabcMatch"/>'s call site describe finding and fixing.
    /// </para>
    /// </summary>
    private static bool IsValidIntrabcSourcePixels(TileState s, int x, int y, int sizePixels, int srcX, int srcY)
    {
        int srcTopEdge = srcY;
        int srcLeftEdge = srcX;
        int srcBottomEdge = srcTopEdge + sizePixels;
        int srcRightEdge = srcLeftEdge + sizePixels;

        if (srcTopEdge < 0 || srcLeftEdge < 0 || srcBottomEdge > s.MiRows * 4 || srcRightEdge > s.MiCols * 4)
        {
            return false;
        }

        const int sbH = 64;
        int activeSbRow = y / sbH;
        int activeSb64Col = x >> 6;
        int srcSbRow = (srcBottomEdge - 1) / sbH;
        int srcSb64Col = (srcRightEdge - 1) >> 6;
        int totalSb64PerRow = ((s.MiCols - 1) >> 4) + 1;
        int activeSb64 = (activeSbRow * totalSb64PerRow) + activeSb64Col;
        int srcSb64 = (srcSbRow * totalSb64PerRow) + srcSb64Col;
        if (srcSb64 >= activeSb64 - IntrabcDelaySb64)
        {
            return false;
        }

        // Real AV1 (libaom's av1_is_dv_valid) adds a further +1 to this gradient when the frame uses
        // 128x128 superblocks. Derived from s.SbMiMask (31 for 128x128 superblocks, 15 for 64x64 -- see
        // TileState.SbMiMask's own remarks) rather than s.Lossless directly: those two used to always
        // coincide (IntraBC only ever ran under lossless, and lossless always uses 128x128 superblocks), but
        // non-lossless IntraBC now exists too (project plan Phase 4, exact-match only), and non-lossless
        // always uses 64x64 superblocks regardless -- reading the real superblock-size flag instead of the
        // losslessness flag they used to happen to track keeps this correct regardless of which combination
        // a future round might introduce (e.g. if non-lossless ever adopted 128x128 superblocks too).
        int gradient = 1 + IntrabcDelaySb64 + (s.SbMiMask == 31 ? 1 : 0);
        int wfOffset = gradient * (activeSbRow - srcSbRow);
        return srcSbRow <= activeSbRow && srcSb64Col < activeSb64Col - IntrabcDelaySb64 + wfOffset;
    }

    /// <summary>
    /// Ground-truth causality check, complementing <see cref="IsValidIntrabcSourcePixels"/>'s geometric
    /// wavefront rule: true only when every mi cell this encoder's own real traversal would need to have
    /// already committed to make (<paramref name="srcX"/>, <paramref name="srcY"/>)'s <paramref name="sizePixels"/>-sized
    /// footprint a real, already-reconstructed copy source is actually marked <see cref="TileState.Written"/>.
    /// Needed because the geometric rule only encodes the superblock-level structure every AV1 bitstream
    /// shares (real-decoder conformance), not this specific encoder's own already-made partition choices --
    /// it can (and, confirmed via a real round-trip regression on this project's own
    /// <c>GraphicContentImage_LosslessAvif</c> fixture before this check existed, does) call a
    /// geometrically-wavefront-legal-but-not-yet-actually-committed region (e.g. a later sibling in the
    /// current superblock's own recursion) "valid," which would corrupt the decode if used as a copy source.
    /// <see cref="TileState.Written"/> is this encoder's own exact record of what it has really committed so
    /// far, so checking it directly is authoritative where the geometric rule is only an approximation.
    /// </summary>
    private static bool IsSourceFootprintWritten(TileState s, int srcX, int srcY, int sizePixels)
    {
        int minR = srcY / 4;
        int maxR = (srcY + sizePixels - 1) / 4;
        int minC = srcX / 4;
        int maxC = (srcX + sizePixels - 1) / 4;

        for (int mr = minR; mr <= maxR; mr++)
        {
            int rowBase = mr * s.MiCols;
            for (int mc = minC; mc <= maxC; mc++)
            {
                if (!s.Written[rowBase + mc])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private const int ApproxSearchWindow = 64;

    // Widened from the original 256: measured on this project's own real target photo (1054x1492,
    // communityyardsale0926.png), a wider bucket window keeps finding genuinely better matches beyond the
    // old cutoff (256 -> 2,076,474 bytes; 1024 -> 2,075,755; 4096 -> 2,075,734 -- diminishing fast past 1024,
    // capturing ~97% of the 4096 ceiling's gain), at a modest ~9% wall-time cost (this window is scanned once
    // per leaf that reaches this fallback). 1024 is the practical knee of that curve, not evidence the
    // underlying content-similarity signal stops mattering past it. Measured before
    // FindApproximateIntrabcMatchViaMotionSearch's own real-diamond-search rewrite -- worth re-measuring
    // against that, since a real motion search covering the whole legal region may make this bucket source
    // partly or wholly redundant.
    private const int ApproxSignatureBucketWindow = 1024;
    private const long ApproxDvSignalingMargin = 64;

    /// <summary>
    /// Phase D technique 5: a bounded approximate-match search, tried only when <see cref="FindIntrabcMatch"/>
    /// found no byte-exact source. Unlike that search (a hash lookup, effectively free), there is no way to
    /// index "close enough" content cheaply by *exact* value, so this scores real candidates directly, drawn
    /// from two sources: the last <see cref="ApproxSearchWindow"/> same-size leaves already encoded
    /// (<see cref="TileState.PositionsBySize"/>, scanned newest-first -- recent/local content is a common
    /// source of real repetition in screen-content-style graphics: repeated UI chrome, tiled elements), and
    /// the last <see cref="ApproxSignatureBucketWindow"/> entries of whichever <see cref="TileState.IntrabcSignatureIndex"/>
    /// bucket this leaf's own <see cref="ComputeCoarseSignature"/> falls into (a visually-similar-but-not-
    /// byte-identical leaf -- e.g. the same decorative element re-rendered with different anti-aliasing --
    /// can be spaced anywhere in the frame, not just within the recency window; measured near-zero real
    /// IntraBC usage on this project's own real target photo, despite it having exactly this kind of spread-
    /// out repeated content, before this bucket source was added). Both are bounded rather than exhaustive
    /// over every same-size leaf in the frame -- an exhaustive search would be O(leaves) per leaf,
    /// O(leaves^2) overall, too slow for a general-purpose encoder on a large image.
    /// Scored with the same WHT-magnitude proxy <see cref="ComputeCandidateCost"/> uses (luma only -- chroma
    /// cost is real but a second-order term for this comparison, and estimating it here would double the
    /// per-candidate cost for a proxy that's already approximate), then only accepted if it beats
    /// <paramref name="bestIntraCost"/> by more than a fixed margin standing in for DV signaling overhead
    /// this proxy doesn't otherwise account for (mv_joint/class/sign/magnitude bits the intra candidates'
    /// own cost never had to pay).
    /// </summary>
    private static bool FindApproximateIntrabcMatch(TileState s, int r, int c, int sizeMi, int bSize, long bestIntraCost, out int mvRow, out int mvCol)
    {
        mvRow = 0;
        mvCol = 0;

        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        long bestCost = long.MaxValue;
        int bestMvRow = 0;
        int bestMvCol = 0;
        bool found = false;

        void ConsiderCandidatePixels(int srcX, int srcY)
        {
            int deltaRow = srcY - y;
            int deltaCol = srcX - x;

            if ((deltaRow == 0 && deltaCol == 0) || !IsValidIntrabcSourcePixels(s, x, y, sizePixels, srcX, srcY))
            {
                return;
            }

            if (!s.MonoChrome && !s.Chroma444 && ((deltaRow & 1) != 0 || (deltaCol & 1) != 0))
            {
                return;
            }

            // IsSourceFootprintWritten -- see FindIntrabcMatch's own identical check remarks for why the
            // geometric wavefront rule IsValidIntrabcSourcePixels already applied above isn't sufficient on
            // its own: it proves a real, spec-conformant decoder would already have this position decoded by
            // now, not that THIS encoder's own specific partition traversal has actually committed it yet. A
            // real, pre-existing gap found this round (this whole approximate-match path never had this check
            // at all, unlike the exact-match path above it, despite a since-corrected doc comment elsewhere in
            // this file claiming it did) -- surfaced by, but independent of, the recon-buffer seeding
            // investigation: reachable any time this candidate's own footprint happens to fall on a
            // not-yet-committed sibling (most likely at a superblock boundary, where a later superblock's own
            // geometrically-reachable positions haven't been visited by this trial's own raster order yet).
            if (!IsSourceFootprintWritten(s, srcX, srcY, sizePixels))
            {
                return;
            }

            long cost = ComputeIntrabcLumaCost(s, x, y, deltaRow * 8, deltaCol * 8, sizePixels);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestMvRow = deltaRow * 8;
                bestMvCol = deltaCol * 8;
                found = true;
            }
        }

        void ConsiderCandidate(int srcR, int srcC) => ConsiderCandidatePixels(srcC * 4, srcR * 4);

        if (s.PositionsBySize.TryGetValue(sizePixels, out var positions))
        {
            int start = Math.Max(0, positions.Count - ApproxSearchWindow);
            for (int idx = positions.Count - 1; idx >= start; idx--)
            {
                var (srcR, srcC) = positions[idx];
                ConsiderCandidate(srcR, srcC);
            }
        }

        int signature = ComputeCoarseSignature(s, x, y, sizePixels);
        if (s.IntrabcSignatureIndex.TryGetValue((sizePixels, signature), out var sameSignature))
        {
            int start = Math.Max(0, sameSignature.Count - ApproxSignatureBucketWindow);
            for (int idx = sameSignature.Count - 1; idx >= start; idx--)
            {
                var (srcR, srcC) = sameSignature[idx];
                ConsiderCandidate(srcR, srcC);
            }
        }

        if (FindApproximateIntrabcMatchViaMotionSearch(s, r, c, bSize, x, y, sizePixels, out int motionSrcX, out int motionSrcY))
        {
            ConsiderCandidatePixels(motionSrcX, motionSrcY);
        }

        // libaom's own av1_full_pixel_search doesn't stop at the diamond search above when its result still
        // looks weak (its own run_mesh_search/force_mesh_thresh escape hatch, av1/encoder/mcomp.c) -- it falls
        // back to a real exhaustive/mesh search over a wider area first. A predictor-seeded diamond search is
        // a greedy local search: when FindMvStackAndPredict's own predictor falls through to its fixed
        // "directly left"/"directly above" default (the common case on a frame with little existing IntraBC
        // usage for neighbors to have contributed a real DV to the stack from), the diamond search above never
        // explores anywhere else in the frame at all -- confirmed by measurement: without this, real IntraBC
        // usage on this project's own target photo *dropped* from 721 to 153 leaves and the file grew, despite
        // the diamond search itself being a more faithful match to libaom's real starting-point/step logic.
        // This mirrors that escape hatch with this project's own prior coarse-grid scan (bounded so total
        // candidate count stays roughly constant regardless of frame size, same shape as libaom's own
        // candidate-count-bounded mesh patterns) instead of reimplementing libaom's exact mesh pattern.
        if (FindApproximateIntrabcMatchViaMeshSearch(s, x, y, sizePixels, out int meshSrcX, out int meshSrcY))
        {
            ConsiderCandidatePixels(meshSrcX, meshSrcY);
        }

        if (!found || bestCost + ApproxDvSignalingMargin >= bestIntraCost)
        {
            return false;
        }

        mvRow = bestMvRow;
        mvCol = bestMvCol;
        return true;
    }

    /// <summary>
    /// Real diamond-pattern full-pixel motion search for an IntraBC copy source -- rewritten to mirror
    /// libaom's own <c>av1_full_pixel_search</c> (<c>av1/encoder/mcomp.c</c>), read directly to settle a real
    /// discrepancy: profiling this project's own real target photo (1054x1492) found libaom using approximate
    /// IntraBC on 5,022 leaves (12.8% of the frame) versus this project's prior fixed-budget-coarse-grid
    /// version finding only 721 (1.8% of pixels) -- a ~7x gap traced to two structural differences from
    /// libaom's real search, not a tuning gap:
    /// <list type="bullet">
    /// <item>libaom's <c>rd_pick_intrabc_mode_sb</c> seeds its search from a real spatial DV predictor
    /// (<c>dv_ref</c>, from <c>av1_find_mv_refs</c>/<c>av1_find_best_ref_mvs_from_stack</c> -- the same
    /// nearest/near-neighbor MV stack machinery real inter prediction uses), not an arbitrary fixed point. This
    /// project already has the exact write-side mirror of that (<see cref="FindMvStackAndPredict"/>, previously
    /// only used at the final MV-diff-signaling site) -- reused here as this search's own starting point.</item>
    /// <item>From that seed, libaom runs a real adaptive step-halving diamond search (its <c>NSTEP</c>/
    /// <c>DIAMOND</c> family, dispatched from <c>av1_full_pixel_search</c>) over the *whole* legal already-coded
    /// region, not a fixed-budget uniform grid sampled at whatever spacing keeps the total candidate count
    /// around a constant. A uniform grid can step clean over a real match sitting between its sample points,
    /// especially for a small block where the useful search radius is itself small; a step-halving diamond
    /// search starting from a spatially-informed predictor reaches the same fine positions with far fewer
    /// candidates and without ever risking stepping over them.</item>
    /// </list>
    /// Legality is still gated the same way as before, by <see cref="IsValidIntrabcSourcePixels"/> (this
    /// project's own conservative wavefront-reachability check, mirroring libaom's <c>av1_is_dv_valid</c>) --
    /// libaom instead pre-clips its search to two separate "above"/"left" mv_limits boxes before searching, an
    /// implementation-level optimization this project doesn't need since its own legality check is already a
    /// single, more general predicate any candidate position can be tested against directly.
    /// </summary>
    private static bool FindApproximateIntrabcMatchViaMotionSearch(TileState s, int r, int c, int bSize, int x, int y, int sizePixels, out int bestSrcX, out int bestSrcY)
    {
        bestSrcX = 0;
        bestSrcY = 0;

        int regionWidth = s.MiCols * 4;
        int regionHeight = y + sizePixels;
        if (regionWidth < sizePixels || regionHeight < sizePixels)
        {
            return false;
        }

        long bestSad = long.MaxValue;
        bool found = false;
        int bestX = 0;
        int bestY = 0;

        void Consider(int candX, int candY)
        {
            if (candX < 0 || candY < 0 || candX > regionWidth - sizePixels || candY > regionHeight - sizePixels)
            {
                return;
            }

            if (candX == x && candY == y)
            {
                return;
            }

            if (!IsValidIntrabcSourcePixels(s, x, y, sizePixels, candX, candY))
            {
                return;
            }

            // IsSourceFootprintWritten -- see ConsiderCandidatePixels's own identical check remarks (project
            // plan's own investigation, this round): without this, a not-yet-committed position can score a
            // deceptively perfect SAD (its own Recon content, whatever it happens to hold this trial) and pull
            // the whole diamond search's own convergence toward a candidate that ConsiderCandidatePixels will
            // ultimately reject anyway -- correct in the end (never accepted), but wasting this search's own
            // real result on a doomed candidate instead of finding a genuinely usable nearby one, measurably
            // costing real IntraBC hit rate (and so real compression) on real photo content.
            if (!IsSourceFootprintWritten(s, candX, candY, sizePixels))
            {
                return;
            }

            long sad = ComputeIntrabcLumaSad(s, x, y, candX, candY, sizePixels);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestX = candX;
                bestY = candY;
                found = true;
            }
        }

        // Real spatial DV predictor (see this method's own remarks) in 1/8-pel MV units -- DVs are always
        // whole-pixel (spec's force_integer_mv for intrabc), so dividing by 8 is exact, never truncating a
        // real fractional offset away.
        var (predMvRow, predMvCol) = FindMvStackAndPredict(s, r, c, bSize);
        int startX = Math.Clamp(x + (predMvCol / 8), 0, regionWidth - sizePixels);
        int startY = Math.Clamp(y + (predMvRow / 8), 0, regionHeight - sizePixels);
        Consider(startX, startY);

        int maxDim = Math.Max(regionWidth, regionHeight);
        int step = 1;
        while (step * 2 <= maxDim)
        {
            step *= 2;
        }

        Span<(int Dy, int Dx)> neighbors = stackalloc (int Dy, int Dx)[8];
        while (step >= 1)
        {
            neighbors[0] = (-step, 0);
            neighbors[1] = (step, 0);
            neighbors[2] = (0, -step);
            neighbors[3] = (0, step);
            neighbors[4] = (-step, -step);
            neighbors[5] = (-step, step);
            neighbors[6] = (step, -step);
            neighbors[7] = (step, step);

            // Keep probing the current step size around whatever is currently best, re-centering after every
            // improvement (the real diamond-search shape: a big win can chain into another big win at the same
            // step size), only halving once a full round finds nothing better.
            bool improved;
            do
            {
                improved = false;
                int baseX = bestX;
                int baseY = bestY;
                foreach (var (dy, dx) in neighbors)
                {
                    long before = bestSad;
                    Consider(baseX + dx, baseY + dy);
                    if (bestSad < before)
                    {
                        improved = true;
                    }
                }
            }
            while (improved);

            step /= 2;
        }

        bestSrcX = bestX;
        bestSrcY = bestY;
        return found;
    }

    // Tuned empirically against this project's own real target photo (1054x1492): the coarse candidate count
    // only meaningfully affects encode time for a genuinely large frame -- Math.Sqrt-derived step already
    // floors to 1 (fully exhaustive at whatever pixel granularity the region allows) for small/synthetic test
    // fixtures well under this budget's own area threshold, confirmed by the full test suite's own runtime
    // staying flat (~24s) all the way from budget 200 up through this value. Measured tradeoff curve on that
    // real photo (bytes / wall time, this mesh search as the only motion-search-family source, i.e. before
    // FindApproximateIntrabcMatchViaMotionSearch's own predictor-seeded diamond search existed): 200 ->
    // 2,088,959 / ~68s; 2,000 -> 2,086,902 / ~74s; 8,000 -> 2,083,557 / ~86s; 32,000 -> 2,075,361 / ~162s;
    // 128,000 -> 2,063,154 / ~362s. Gains kept growing, not plateauing, all the way up -- this value is a
    // deliberate practicality cutoff for a lossless mode that already accepts a real RDO cost for real
    // compression, not evidence that the underlying search itself stops improving further out; revisit if this
    // project ever wants to trade more encode time for a smaller lossless file.
    private const int MotionSearchCoarseCandidateBudget = 32000;

    /// <summary>
    /// Fallback exhaustive-ish coarse-grid scan, tried alongside <see cref="FindApproximateIntrabcMatchViaMotionSearch"/>'s
    /// own predictor-seeded diamond search -- mirrors libaom's own <c>run_mesh_search</c>/<c>force_mesh_thresh</c>
    /// escape hatch (<c>av1/encoder/mcomp.c</c>'s <c>av1_full_pixel_search</c>), which falls back to a real
    /// exhaustive/mesh search whenever a predictor-seeded diamond search's own result still looks weak. A
    /// diamond search is a greedy local search: when the spatial DV predictor it starts from isn't actually
    /// informative (the common case when few of this leaf's neighbors have used IntraBC themselves, so
    /// <see cref="FindMvStackAndPredict"/> falls through to its own fixed "directly left"/"directly above"
    /// default), the diamond search alone never looks anywhere else in the frame at all -- confirmed by
    /// measurement: removing this source entirely (keeping only the predictor-seeded diamond search) dropped
    /// real IntraBC usage on this project's own target photo from 721 to 153 leaves and grew the file, despite
    /// the diamond search itself being a more faithful match to libaom's real starting-point/step logic. This
    /// restores the original coarse-grid-then-diamond-refine scan (spaced so the total candidate count stays
    /// roughly constant regardless of frame size) as that same kind of broader safety net, using cheap SAD as
    /// the coarse metric (a full WHT-based <see cref="ComputeIntrabcLumaCost"/> per coarse candidate would be
    /// far too slow at this candidate count). Only the single final winner is ever priced with the real
    /// WHT-based cost, by <see cref="FindApproximateIntrabcMatch"/>'s own <c>ConsiderCandidatePixels</c> after
    /// this returns.
    /// </summary>
    private static bool FindApproximateIntrabcMatchViaMeshSearch(TileState s, int x, int y, int sizePixels, out int bestSrcX, out int bestSrcY)
    {
        bestSrcX = 0;
        bestSrcY = 0;

        int regionWidth = s.MiCols * 4;
        int regionHeight = y + sizePixels;
        if (regionWidth < sizePixels || regionHeight < sizePixels)
        {
            return false;
        }

        long area = (long)(regionWidth - sizePixels + 1) * (regionHeight - sizePixels + 1);
        int step = Math.Max(1, (int)Math.Sqrt((double)area / MotionSearchCoarseCandidateBudget));

        long bestSad = long.MaxValue;
        bool found = false;

        for (int srcY = 0; srcY <= regionHeight - sizePixels; srcY += step)
        {
            for (int srcX = 0; srcX <= regionWidth - sizePixels; srcX += step)
            {
                if (srcX == x && srcY == y)
                {
                    continue;
                }

                if (!IsValidIntrabcSourcePixels(s, x, y, sizePixels, srcX, srcY))
                {
                    continue;
                }

                // IsSourceFootprintWritten -- see ConsiderCandidatePixels's own identical check remarks.
                if (!IsSourceFootprintWritten(s, srcX, srcY, sizePixels))
                {
                    continue;
                }

                long sad = ComputeIntrabcLumaSad(s, x, y, srcX, srcY, sizePixels);
                if (sad < bestSad)
                {
                    bestSad = sad;
                    bestSrcX = srcX;
                    bestSrcY = srcY;
                    found = true;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        int refineStep = Math.Max(1, step / 2);
        while (refineStep >= 1)
        {
            bool improved = false;
            Span<(int Dy, int Dx)> neighbors =
            [
                (-refineStep, 0), (refineStep, 0), (0, -refineStep), (0, refineStep),
                (-refineStep, -refineStep), (-refineStep, refineStep), (refineStep, -refineStep), (refineStep, refineStep),
            ];

            foreach (var (dy, dx) in neighbors)
            {
                int candX = bestSrcX + dx;
                int candY = bestSrcY + dy;
                if (candX < 0 || candY < 0 || candX > regionWidth - sizePixels || candY > regionHeight - sizePixels)
                {
                    continue;
                }

                if (candX == x && candY == y)
                {
                    continue;
                }

                if (!IsValidIntrabcSourcePixels(s, x, y, sizePixels, candX, candY))
                {
                    continue;
                }

                // IsSourceFootprintWritten -- see ConsiderCandidatePixels's own identical check remarks.
                if (!IsSourceFootprintWritten(s, candX, candY, sizePixels))
                {
                    continue;
                }

                long sad = ComputeIntrabcLumaSad(s, x, y, candX, candY, sizePixels);
                if (sad < bestSad)
                {
                    bestSad = sad;
                    bestSrcX = candX;
                    bestSrcY = candY;
                    improved = true;
                }
            }

            if (!improved)
            {
                refineStep /= 2;
            }
        }

        return true;
    }

    /// <summary>Cheap sum-of-absolute-differences between this leaf's own source luma and a candidate copy source's already-reconstructed luma -- the coarse-search metric <see cref="FindApproximateIntrabcMatchViaMotionSearch"/> uses instead of a full WHT-based cost, exactly the SAD-then-refine shape real motion search algorithms use to keep the expensive metric off the hot path.</summary>
    private static long ComputeIntrabcLumaSad(TileState s, int x, int y, int srcX, int srcY, int sizePixels)
    {
        long sad = 0;
        for (int i = 0; i < sizePixels; i++)
        {
            int rowBase = ((y + i) * s.YWidth) + x;
            int srcRowBase = ((srcY + i) * s.YWidth) + srcX;
            for (int j = 0; j < sizePixels; j++)
            {
                sad += Math.Abs(s.SourceY[rowBase + j] - s.ReconY[srcRowBase + j]);
            }
        }

        return sad;
    }

    /// <summary>Luma-only WHT-magnitude cost of block-copying from (<paramref name="mvRow"/>, <paramref name="mvCol"/>) (spec 1/8th-luma-sample units) instead of the leaf's own source pixels -- see <see cref="ComputeCandidateCost"/>'s remarks for why this proxy, not SSE.</summary>
    private static long ComputeIntrabcLumaCost(TileState s, int x, int y, int mvRow, int mvCol, int sizePixels)
    {
        var pred = s.BestPred;
        Av1InterPrediction.PredictIntrabc(pred, s.ReconY, s.YWidth, x, y, sizePixels, sizePixels, mvRow, mvCol, subX: 0, subY: 0, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);
        return ComputeCandidateCost(s, s.SourceY, s.YWidth, pred, x, y, sizePixels, ptype: 0, LiveYCoeffCtx(s));
    }

    /// <summary>
    /// The REAL (not <see cref="ComputeIntrabcLumaCost"/>'s own deliberate luma-only proxy) total residual
    /// cost of a candidate approximate-match IntraBC leaf -- every plane (luma, and when
    /// <paramref name="hasChroma"/>, both chroma planes), real per-4x4-sub-block WHT/quantize/entropy trial,
    /// exactly mirroring <see cref="EncodeIntrabcResidual"/>'s own real commit-path loop but against scratch
    /// coefficient context + <see cref="TileState.TrialSink"/> instead of real state, so a candidate that
    /// might not even win never has a visible side effect. Used only for the final, real accept/reject
    /// decision in <see cref="EncodeLeaf"/> (project plan Phase 3's own IntraBC-vs-chroma ordering fix,
    /// round N+28's own research) -- <see cref="FindApproximateIntrabcMatch"/>'s own luma-only proxy search
    /// stays exactly as it was, a legitimate cheap prefilter for finding a candidate MV in the first place,
    /// not a replacement for a real decision.
    ///
    /// <para>Safe to read <see cref="TileState.ReconY"/>/<see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>
    /// directly and unconditionally, with no scratch reconstruction buffer or self-overlap handling needed at
    /// all: every candidate reaching this point already passed <see cref="IsSourceFootprintWritten"/> at
    /// search time, which proves its own source footprint is entirely composed of pixels this encoder has
    /// already really, permanently committed -- this leaf's own (not-yet-decided) destination footprint can
    /// never be among them.</para>
    ///
    /// <para>Bound-threaded the same way every other real per-candidate cost function in this file is (see
    /// <see cref="ComputeCandidateCostPrecise"/>'s own remarks for the general mechanism): <paramref name="boundUnits512"/>
    /// is a raw, unrounded 1/512-bit threshold, checked against each plane's own running total in turn (luma,
    /// then U, then V when <paramref name="hasChroma"/>), each plane's own remaining budget derived from what
    /// the previous plane(s) already spent -- exactly SearchUvMode's/SearchRectUvMode's own U-then-V budget
    /// derivation, generalized to three planes here. Returns <see cref="long.MaxValue"/> the instant any
    /// plane's own running total proves the whole candidate can no longer beat the bound (skipping every
    /// remaining plane entirely), or the real, once-rounded combined cost otherwise.
    /// </para>
    /// </summary>
    private static long ComputeIntrabcRealResidualCost(TileState s, int x, int y, int mvRow, int mvCol, bool hasChroma, int sizePixels, long boundUnits512)
    {
        long lumaUnits512 = ComputeIntrabcRealPlaneUnits512(s, s.SourceY, s.ReconY, s.YWidth, LiveYCoeffCtx(s), ptype: 0, x, y, sizePixels, sizePixels, mvRow, mvCol, subsamplingX: 0, subsamplingY: 0, s.EdgeMaxX, s.EdgeMaxY, boundUnits512);
        if (lumaUnits512 == long.MaxValue)
        {
            return long.MaxValue;
        }

        if (!hasChroma)
        {
            return Av1RdCost.CombineCost(0, (long)Math.Round(lumaUnits512 / 512.0, MidpointRounding.AwayFromZero), 1.0);
        }

        int chromaSize = s.Chroma444 ? sizePixels : sizePixels / 2;
        int chromaSub = s.Chroma444 ? 0 : 1;
        int cx = s.Chroma444 ? x : x / 2;
        int cy = s.Chroma444 ? y : y / 2;

        long uBound = boundUnits512 == long.MaxValue ? long.MaxValue : boundUnits512 - lumaUnits512;
        long uUnits512 = ComputeIntrabcRealPlaneUnits512(s, s.SourceU!, s.ReconU!, s.ChromaWidth, LiveUCoeffCtx(s), ptype: 1, cx, cy, chromaSize, chromaSize, mvRow, mvCol, chromaSub, chromaSub, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, uBound);
        if (uUnits512 == long.MaxValue)
        {
            return long.MaxValue;
        }

        long vBound = boundUnits512 == long.MaxValue ? long.MaxValue : boundUnits512 - lumaUnits512 - uUnits512;
        long vUnits512 = ComputeIntrabcRealPlaneUnits512(s, s.SourceV!, s.ReconV!, s.ChromaWidth, LiveVCoeffCtx(s), ptype: 1, cx, cy, chromaSize, chromaSize, mvRow, mvCol, chromaSub, chromaSub, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, vBound);
        if (vUnits512 == long.MaxValue)
        {
            return long.MaxValue;
        }

        long totalUnits512 = lumaUnits512 + uUnits512 + vUnits512;
        return Av1RdCost.CombineCost(0, (long)Math.Round(totalUnits512 / 512.0, MidpointRounding.AwayFromZero), 1.0);
    }

    /// <summary>
    /// One plane's own real per-4x4-sub-block WHT/quantize/entropy trial for <see cref="ComputeIntrabcRealResidualCost"/>
    /// -- structurally identical to <see cref="EncodeIntrabcResidual"/>'s own per-plane loop (same
    /// <see cref="Av1InterPrediction.PredictIntrabc"/> prediction, same WHT/quantize), but against
    /// <see cref="TileState.ScratchCoeffCtx"/>/<see cref="TileState.TrialSink"/> instead of real
    /// state/<see cref="TileState.Symbols"/>, and returning the raw, unrounded <see cref="Av1TrialSymbolSink.Units512"/>
    /// total (or <see cref="long.MaxValue"/> on an early abort) rather than committing anything or rounding.
    /// Never writes back into <paramref name="recon"/> -- unlike <see cref="EncodeIntrabcResidual"/>, which
    /// must (a later sub-block of a genuinely oversized real commit could depend on an earlier one's own
    /// reconstruction), this trial's own candidate is proven (see <see cref="ComputeIntrabcRealResidualCost"/>'s
    /// own remarks) to never reference its own destination footprint at all, so every sub-block's own
    /// prediction reads real, unmutated, already-committed pixels regardless of this trial's own progress.
    /// </summary>
    private static long ComputeIntrabcRealPlaneUnits512(TileState s, int[] source, int[] recon, int stride, Av1CoefficientWriter.PlaneContext realCtx, int ptype, int destX, int destY, int wPixels, int hPixels, int mvRow, int mvCol, int subsamplingX, int subsamplingY, int edgeMaxX, int edgeMaxY, long boundUnits512)
    {
        var scratch = s.ScratchCoeffCtx;
        var trial = s.TrialSink;
        int destX4 = destX >> 2;
        int destY4 = destY >> 2;
        int w4 = wPixels >> 2;
        int h4 = hPixels >> 2;
        scratch.SeedFrom(realCtx, destX4, w4, destY4, h4);
        trial.Reset();

        var pred = s.Pred;
        bool aborted = false;
        for (int dr = 0; dr < h4 && !aborted; dr++)
        {
            for (int dc = 0; dc < w4; dc++)
            {
                int subX = destX + (dc * 4);
                int subY = destY + (dr * 4);

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > edgeMaxX || subY > edgeMaxY)
                {
                    continue;
                }

                Av1InterPrediction.PredictIntrabc(pred, recon, stride, subX, subY, 4, 4, mvRow, mvCol, subsamplingX, subsamplingY, edgeMaxX, edgeMaxY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * stride) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = source[rowBase + j] - pred[predRowBase + j];
                    }
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                int subX4 = subX >> 2;
                int subY4 = subY >> 2;
                Av1CoefficientWriter.WriteCoeffs(trial, s.CostCdf, levels, 4, ptype, subX4, subY4, scratch, writeLumaTxType: null, blockSize: wPixels, updateContext: true);

                // Same exact, provably-safe early abort as ComputeCandidateCostPrecise's own identical
                // mechanism (see its remarks).
                if (trial.Units512 >= boundUnits512)
                {
                    aborted = true;
                    break;
                }
            }
        }

        return aborted ? long.MaxValue : trial.Units512;
    }

    /// <summary>
    /// Encodes an approximate-match IntraBC leaf's real residual (skip = 0): predicts every plane via the
    /// same shared block-copy predictor the decoder uses (<see cref="Av1InterPrediction.PredictIntrabc"/>),
    /// then WHT-transforms/quantizes/writes/reconstructs each 4x4 sub-block exactly like
    /// <see cref="EncodeLosslessLumaResidual"/>'s intra case does. No decoder changes were needed for this
    /// at all: <c>Av1TileDecoder.TransformBlock</c>'s <c>if (!_skip) { Coeffs(); Reconstruct(); }</c> already
    /// runs unconditionally after any prediction branch, intrabc included, and lossless's own
    /// <c>qindex &lt;= 0</c> short-circuit in <c>TransformType</c> means no <c>tx_type</c> symbol is ever
    /// read either way (matching <c>writeLumaTxType: null</c> below) -- this leaf looks, to the decoder, like
    /// any other lossless coding block with a nonzero residual, just with a different prediction source.
    /// Unlike whole-leaf intra prediction's own *search/estimate* step (which legitimately approximates by
    /// reading <see cref="TileState.SourceY"/> up front -- see <see cref="RdPickPartition"/>'s remarks),
    /// this real residual encode predicts every plane fresh per 4x4 sub-block from the progressively-updated
    /// <see cref="TileState.ReconY"/>/<see cref="TileState.ReconU"/>/<see cref="TileState.ReconV"/>, exactly
    /// mirroring <c>Av1TileDecoder.TransformBlock</c>'s own per-sub-block <c>PredictIntrabc</c> call (spec
    /// §5.11.35) -- this is what makes the path correct for a merged (&gt;8x8) leaf, not just a
    /// single-sub-block one (see the now-removed <c>sizeMi &lt;= 2</c> gate's history at the call site below
    /// for why this mattered).
    /// </summary>
    private static void EncodeIntrabcResidual(TileState s, int r, int c, int x, int y, int mvRow, int mvCol, bool hasChroma, int sizePixels)
    {
        var pred = s.Pred;

        int n = sizePixels / 4;
        for (int dr = 0; dr < n; dr++)
        {
            for (int dc = 0; dc < n; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                if (subX > s.EdgeMaxX || subY > s.EdgeMaxY)
                {
                    continue;
                }

                // Predicted fresh per 4x4 sub-block from s.ReconY, which by now already carries this same
                // leaf's own earlier (raster-order) sub-blocks' reconstructed pixels -- matching
                // Av1TileDecoder.TransformBlock's per-sub-block PredictIntrabc call exactly (spec §5.11.35),
                // not a stale whole-leaf snapshot taken before any of this leaf's own pixels existed.
                // mvRow/mvCol are unchanged per call (the coding block's one DV); only startX/startY move.
                // The trailing 0, 0 are PredictIntrabc's own chroma-subsampling-shift parameters (always 0
                // for luma) -- passed positionally here, not as subX:/subY:, since this loop already has
                // locals named subX/subY for the sub-block's pixel position.
                Av1InterPrediction.PredictIntrabc(pred, s.ReconY, s.YWidth, subX, subY, 4, 4, mvRow, mvCol, 0, 0, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 4; i++)
                {
                    int rowBase = ((subY + i) * s.YWidth) + subX;
                    int predRowBase = i * 4;
                    for (int j = 0; j < 4; j++)
                    {
                        residual[(i * 4) + j] = s.SourceY[rowBase + j] - pred[predRowBase + j];
                    }
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                if (s.Phase != Av1EncodePhase.Decide)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: sizePixels);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same arguments as the real commit just above.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 0, subC, subR, s.DecideYCoeffCtx, writeLumaTxType: null, blockSize: sizePixels);
                }

                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);

                SetBlockDecoded(s, 0, subR & s.SbMiMask, subC & s.SbMiMask, true);
            }
        }

        if (!hasChroma)
        {
            return;
        }

        int chromaSize = s.Chroma444 ? sizePixels : sizePixels / 2;
        // subXc/subYc: genuinely independent axes -- this encoder only ever produces 4:2:0 or 4:4:4 (never
        // 4:2:2), where the two always coincide, but keeping them as separate values (rather than reusing
        // subXc for both PredictIntrabc parameters below) avoids a landmine for any future 4:2:2 support,
        // which would need them to differ.
        int subXc = s.Chroma444 ? 0 : 1;
        int subYc = s.Chroma444 ? 0 : 1;
        int cx = s.Chroma444 ? x : x / 2;
        int cy = s.Chroma444 ? y : y / 2;
        int chromaN = chromaSize / 4;

        foreach (var (source, recon, ctx, decideCtx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!, s.DecideUCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!, s.DecideVCoeffCtx!),
        })
        {
            var cpred = s.BestPred;

            for (int dr = 0; dr < chromaN; dr++)
            {
                for (int dc = 0; dc < chromaN; dc++)
                {
                    int subCx = cx + (dc * 4);
                    int subCy = cy + (dr * 4);
                    int chromaR4 = (s.Chroma444 ? r : r / 2) + dr;
                    int chromaC4 = (s.Chroma444 ? c : c / 2) + dc;

                    // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                    // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                    if (subCx > s.ChromaEdgeMaxX || subCy > s.ChromaEdgeMaxY)
                    {
                        continue;
                    }

                    // Same fix as the luma loop above: predicted fresh per 4x4 sub-block from recon
                    // (progressively updated by this same leaf's own earlier sub-blocks), not once for the
                    // whole chroma region.
                    Av1InterPrediction.PredictIntrabc(cpred, recon, s.ChromaWidth, subCx, subCy, 4, 4, mvRow, mvCol, subXc, subYc, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);

                    var residual = s.Residual;
                    for (int i = 0; i < 4; i++)
                    {
                        int rowBase = ((subCy + i) * s.ChromaWidth) + subCx;
                        int predRowBase = i * 4;
                        for (int j = 0; j < 4; j++)
                        {
                            residual[(i * 4) + j] = source[rowBase + j] - cpred[predRowBase + j];
                        }
                    }

                    var coeff = s.Coeff;
                    Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                    var levels = s.Levels;
                    Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                    for (int i = 0; i < 4; i++)
                    {
                        Array.Copy(cpred, i * 4, recon, ((subCy + i) * s.ChromaWidth) + subCx, 4);
                    }

                    int chromaBlockSizeArg = chromaN > 1 ? chromaSize : 0;
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null, blockSize: chromaBlockSizeArg);
                    }

                    // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                    // remarks. Same arguments as the real commit just above.
                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 1, chromaC4, chromaR4, decideCtx, writeLumaTxType: null, blockSize: chromaBlockSizeArg);
                    }

                    Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, subCx, subCy, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                }
            }
        }
    }

    private static bool BlockPixelsEqual(TileState s, int x1, int y1, int x2, int y2, int size, bool hasChroma)
    {
        for (int i = 0; i < size; i++)
        {
            int row1 = ((y1 + i) * s.YWidth) + x1;
            int row2 = ((y2 + i) * s.YWidth) + x2;
            for (int j = 0; j < size; j++)
            {
                if (s.SourceY[row1 + j] != s.SourceY[row2 + j])
                {
                    return false;
                }
            }
        }

        if (hasChroma)
        {
            int c1x = s.Chroma444 ? x1 : x1 / 2;
            int c1y = s.Chroma444 ? y1 : y1 / 2;
            int c2x = s.Chroma444 ? x2 : x2 / 2;
            int c2y = s.Chroma444 ? y2 : y2 / 2;
            int chromaSize = s.Chroma444 ? size : size / 2;
            for (int i = 0; i < chromaSize; i++)
            {
                int row1 = ((c1y + i) * s.ChromaWidth) + c1x;
                int row2 = ((c2y + i) * s.ChromaWidth) + c2x;
                for (int j = 0; j < chromaSize; j++)
                {
                    if (s.SourceU![row1 + j] != s.SourceU![row2 + j] || s.SourceV![row1 + j] != s.SourceV![row2 + j])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Records a just-fully-encoded leaf's position into <see cref="TileState.PositionsBySize"/>/
    /// <see cref="TileState.IntrabcSignatureIndex"/> for the *approximate*-match search
    /// (<see cref="FindApproximateIntrabcMatch"/>). Only ever called for lossless tiles (see
    /// <see cref="EncodeLeaf"/>) -- indexing a non-lossless leaf would be pointless, since this encoder never
    /// enables IntraBC outside lossless mode. Exact-match candidates no longer come from here at all --
    /// <see cref="FindIntrabcMatch"/> queries <see cref="TileState.IntrabcHashTable"/> instead, a whole-frame
    /// table built once up front (see <see cref="Av1IntrabcHashTable"/>'s own remarks).
    /// </summary>
    private static void RecordIntrabcHashEntry(TileState s, int r, int c, int sizeMi)
    {
        int sizePixels = sizeMi * 4;
        int x = c * 4;
        int y = r * 4;

        if (!s.PositionsBySize.TryGetValue(sizePixels, out var positions))
        {
            positions = [];
            s.PositionsBySize[sizePixels] = positions;
        }

        positions.Add((r, c));

        int signature = ComputeCoarseSignature(s, x, y, sizePixels);
        var sigKey = (sizePixels, signature);
        if (!s.IntrabcSignatureIndex.TryGetValue(sigKey, out var sigList))
        {
            sigList = [];
            s.IntrabcSignatureIndex[sigKey] = sigList;
        }

        sigList.Add((r, c));
    }

    /// <summary>
    /// Coarse, quantized-luma-average signature for a <paramref name="size"/>x<paramref name="size"/> leaf at
    /// (<paramref name="x"/>, <paramref name="y"/>), used only to bucket visually-similar leaves for
    /// <see cref="FindApproximateIntrabcMatch"/> -- unlike <see cref="Av1IntrabcHashTable"/> (exact content,
    /// used for byte-identical matches), this deliberately throws away most of the content so a re-rendered copy of
    /// the same decorative element (different anti-aliasing, a few source pixels off) still lands in the same
    /// bucket as its near-duplicates. Splits the leaf into a fixed 2x2 grid of quadrants regardless of its
    /// own size (so a signature is comparable only against same-size leaves, matching
    /// <see cref="TileState.IntrabcSignatureIndex"/>'s own size-keyed buckets, but the quantization coarseness
    /// stays constant across leaf sizes), averages each quadrant's luma, and quantizes each average to 16
    /// levels (4 bits) -- 4 quadrants x 4 bits packs into a 16-bit signature, deliberately coarse (not a
    /// perceptual hash proper) so genuinely similar content reliably collides into the same bucket rather than
    /// being split across many by noise-level differences.
    /// </summary>
    private static int ComputeCoarseSignature(TileState s, int x, int y, int size)
    {
        int half = Math.Max(1, size / 2);
        int signature = 0;
        for (int qr = 0; qr < 2; qr++)
        {
            for (int qc = 0; qc < 2; qc++)
            {
                int startY = y + (qr * half);
                int startX = x + (qc * half);
                int endY = Math.Min(startY + half, y + size);
                int endX = Math.Min(startX + half, x + size);

                long sum = 0;
                int count = 0;
                for (int py = startY; py < endY; py++)
                {
                    int rowBase = py * s.YWidth;
                    for (int px = startX; px < endX; px++)
                    {
                        sum += s.SourceY[rowBase + px];
                        count++;
                    }
                }

                int avg = count > 0 ? (int)(sum / count) : 0;
                int bucket = avg >> 4;
                signature = (signature << 4) | (bucket & 0xF);
            }
        }

        return signature;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.FindMvStack</c> + <c>AssignMv</c>'s PredMv derivation, isCompound=0 -- see that method's remarks for the full reasoning (identical here, just reading/writing <see cref="TileState"/>'s grids instead of instance fields).</summary>
    private static (int Row, int Col) FindMvStackAndPredict(TileState s, int r, int c, int bSize)
    {
        int bw4 = Av1BlockTables.Num4x4BlocksWide[bSize];
        int bh4 = Av1BlockTables.Num4x4BlocksHigh[bSize];
        var mv = new MvSearchState();

        ScanRow(s, mv, r, c, bw4, -1);
        ScanCol(s, mv, r, c, bh4, -1);
        if (Math.Max(bw4, bh4) <= 16)
        {
            ScanPoint(s, mv, r, c, -1, bw4);
        }

        int numNearest = mv.NumMvFound;
        for (int idx = 0; idx < numNearest; idx++)
        {
            mv.WeightStack[idx] += RefCatLevel;
        }

        ScanPoint(s, mv, r, c, -1, -1);
        ScanRow(s, mv, r, c, bw4, -3);
        ScanCol(s, mv, r, c, bh4, -3);
        if (bh4 > 1)
        {
            ScanRow(s, mv, r, c, bw4, -5);
        }

        if (bw4 > 1)
        {
            ScanCol(s, mv, r, c, bh4, -5);
        }

        SortStack(mv, 0, numNearest);
        SortStack(mv, numNearest, mv.NumMvFound);

        if (mv.NumMvFound < 2)
        {
            for (int idx = mv.NumMvFound; idx < 2; idx++)
            {
                mv.RefStackMv[idx, 0] = 0;
                mv.RefStackMv[idx, 1] = 0;
            }
        }

        int bw = Av1BlockTables.BlockWidth(bSize);
        int bh = Av1BlockTables.BlockHeight(bSize);
        for (int idx = 0; idx < mv.NumMvFound; idx++)
        {
            mv.RefStackMv[idx, 0] = ClampMvRow(mv.RefStackMv[idx, 0], MvBorder + (bh * 8), r, bSize, s.MiRows);
            mv.RefStackMv[idx, 1] = ClampMvCol(mv.RefStackMv[idx, 1], MvBorder + (bw * 8), c, bSize, s.MiCols);
        }

        int predMvRow = mv.RefStackMv[0, 0];
        int predMvCol = mv.RefStackMv[0, 1];
        if (predMvRow == 0 && predMvCol == 0)
        {
            predMvRow = mv.RefStackMv[1, 0];
            predMvCol = mv.RefStackMv[1, 1];
        }

        if (predMvRow == 0 && predMvCol == 0)
        {
            // Num_4x4_Blocks_High[BLOCK_128X128 or BLOCK_64X64] (spec's own sbSize4 in this exact fallback,
            // mirrored from Av1TileDecoder.AssignMv's identical branch, which derives it from the real
            // Use128x128Superblock sequence-header flag, not losslessness). Derived here from s.SbMiMask (31
            // for 128x128 superblocks, 15 for 64x64 -- see TileState.SbMiMask's own remarks) rather than
            // s.Lossless directly, for the identical reason IsValidIntrabcSourcePixels's own gradient
            // computation now does the same: the two flags used to always coincide (IntraBC only ever ran
            // under lossless, and lossless has used 128x128 superblocks since PR #80), but non-lossless
            // IntraBC now exists too (project plan Phase 4) and always uses 64x64 superblocks regardless.
            // Getting this wrong doesn't just compress worse: since this fallback is the actual PredMv the
            // decoder independently derives too, a mismatched constant here silently predicts a *different*
            // Mv on each side once diffMv is added back, corrupting every pixel this leaf's IntraBC
            // block-copy reads from -- confirmed via direct encoder/decoder cross-instrumentation (encoder
            // computed predMvRow=-512, decoder independently computed predMvRow=-1024 for the same leaf) the
            // first time this class of bug was found, back when this used the stale pre-PR-#80 value.
            int sbSize4 = s.SbMiMask == 31 ? 32 : 16;
            if (r - sbSize4 < 0)
            {
                predMvRow = 0;
                predMvCol = -((sbSize4 * 4) + IntrabcDelayPixels) * 8;
            }
            else
            {
                predMvRow = -(sbSize4 * 4 * 8);
                predMvCol = 0;
            }
        }

        return (predMvRow, predMvCol);
    }

    private static void ScanRow(TileState s, MvSearchState mv, int r, int c, int bw4, int deltaRow)
    {
        int end4 = Math.Min(Math.Min(bw4, s.MiCols - c), 16);
        int deltaCol = 0;
        bool useStep16 = bw4 >= 16;
        if (Math.Abs(deltaRow) > 1)
        {
            deltaRow += r & 1;
            deltaCol = 1 - (c & 1);
        }

        int i = 0;
        while (i < end4)
        {
            int mvRow = r + deltaRow;
            int mvCol = c + deltaCol + i;
            if (!IsInsideEnc(s, mvRow, mvCol))
            {
                break;
            }

            int len = Math.Min(bw4, Av1BlockTables.Num4x4BlocksWide[s.MiSizes[(mvRow * s.MiCols) + mvCol]]);
            if (Math.Abs(deltaRow) > 1)
            {
                len = Math.Max(2, len);
            }

            if (useStep16)
            {
                len = Math.Max(4, len);
            }

            AddRefMvCandidate(s, mv, mvRow, mvCol, len * 2);
            i += len;
        }
    }

    private static void ScanCol(TileState s, MvSearchState mv, int r, int c, int bh4, int deltaCol)
    {
        int end4 = Math.Min(Math.Min(bh4, s.MiRows - r), 16);
        int deltaRow = 0;
        bool useStep16 = bh4 >= 16;
        if (Math.Abs(deltaCol) > 1)
        {
            deltaRow = 1 - (r & 1);
            deltaCol += c & 1;
        }

        int i = 0;
        while (i < end4)
        {
            int mvRow = r + deltaRow + i;
            int mvCol = c + deltaCol;
            if (!IsInsideEnc(s, mvRow, mvCol))
            {
                break;
            }

            int len = Math.Min(bh4, Av1BlockTables.Num4x4BlocksHigh[s.MiSizes[(mvRow * s.MiCols) + mvCol]]);
            if (Math.Abs(deltaCol) > 1)
            {
                len = Math.Max(2, len);
            }

            if (useStep16)
            {
                len = Math.Max(4, len);
            }

            AddRefMvCandidate(s, mv, mvRow, mvCol, len * 2);
            i += len;
        }
    }

    private static void ScanPoint(TileState s, MvSearchState mv, int r, int c, int deltaRow, int deltaCol)
    {
        int mvRow = r + deltaRow;
        int mvCol = c + deltaCol;
        if (IsInsideEnc(s, mvRow, mvCol) && s.Written[(mvRow * s.MiCols) + mvCol])
        {
            AddRefMvCandidate(s, mv, mvRow, mvCol, 4);
        }
    }

    private static void AddRefMvCandidate(TileState s, MvSearchState mv, int mvRow, int mvCol, int weight)
    {
        int idx = (mvRow * s.MiCols) + mvCol;
        if (!s.IsInters[idx])
        {
            return;
        }

        SearchStack(s, mv, mvRow, mvCol, weight);
    }

    private static void SearchStack(TileState s, MvSearchState mv, int mvRow, int mvCol, int weight)
    {
        int idx = (mvRow * s.MiCols) + mvCol;
        int candMvRow = s.MvRowsGrid[idx];
        int candMvCol = s.MvColsGrid[idx];

        for (int i = 0; i < mv.NumMvFound; i++)
        {
            if (mv.RefStackMv[i, 0] == candMvRow && mv.RefStackMv[i, 1] == candMvCol)
            {
                mv.WeightStack[i] += weight;
                return;
            }
        }

        if (mv.NumMvFound < MaxRefMvStackSize)
        {
            mv.RefStackMv[mv.NumMvFound, 0] = candMvRow;
            mv.RefStackMv[mv.NumMvFound, 1] = candMvCol;
            mv.WeightStack[mv.NumMvFound] = weight;
            mv.NumMvFound++;
        }
    }

    private static void SortStack(MvSearchState mv, int start, int end)
    {
        while (end > start)
        {
            int newEnd = start;
            for (int idx = start + 1; idx < end; idx++)
            {
                if (mv.WeightStack[idx - 1] < mv.WeightStack[idx])
                {
                    (mv.WeightStack[idx - 1], mv.WeightStack[idx]) = (mv.WeightStack[idx], mv.WeightStack[idx - 1]);
                    (mv.RefStackMv[idx - 1, 0], mv.RefStackMv[idx, 0]) = (mv.RefStackMv[idx, 0], mv.RefStackMv[idx - 1, 0]);
                    (mv.RefStackMv[idx - 1, 1], mv.RefStackMv[idx, 1]) = (mv.RefStackMv[idx, 1], mv.RefStackMv[idx - 1, 1]);
                    newEnd = idx;
                }
            }

            end = newEnd;
        }
    }

    private static int ClampMvRow(int mvec, int border, int r, int bSize, int miRows)
    {
        int bh4 = Av1BlockTables.Num4x4BlocksHigh[bSize];
        int mbToTopEdge = -(r * 4 * 8);
        int mbToBottomEdge = (miRows - bh4 - r) * 4 * 8;
        return Math.Clamp(mvec, mbToTopEdge - border, mbToBottomEdge + border);
    }

    private static int ClampMvCol(int mvec, int border, int c, int bSize, int miCols)
    {
        int bw4 = Av1BlockTables.Num4x4BlocksWide[bSize];
        int mbToLeftEdge = -(c * 4 * 8);
        int mbToRightEdge = (miCols - bw4 - c) * 4 * 8;
        return Math.Clamp(mvec, mbToLeftEdge - border, mbToRightEdge + border);
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ReadMv</c>.</summary>
    private static void WriteMv(TileState s, int mvRow, int mvCol, int predMvRow, int predMvCol)
    {
        // Two-pass tile-encoder architecture, Stage 1b-ii -- see Av1EncodePhase's own remarks.
        if (s.Phase != Av1EncodePhase.Decide)
        {
            WriteMv(s, s.Symbols, s.Cdf, mvRow, mvCol, predMvRow, predMvCol);
        }

        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
        if (s.Phase != Av1EncodePhase.Emit)
        {
            WriteMv(s, s.DecideCommitSink, s.DecideCdf, mvRow, mvCol, predMvRow, predMvCol);
        }
    }

    /// <summary>
    /// Real symbol-writing body of <see cref="WriteMv(TileState, int, int, int, int)"/>, parameterized by
    /// (<paramref name="sink"/>, <paramref name="cdf"/>) so the two-argument overload above can call it
    /// once for real (<see cref="TileState.Symbols"/>/<see cref="TileState.Cdf"/>) and once for the
    /// parallel decide-time tracking call (<see cref="TileState.DecideCommitSink"/>/
    /// <see cref="TileState.DecideCdf"/>) -- see <see cref="TileState.DecideYCoeffCtx"/>'s own remarks.
    /// </summary>
    private static void WriteMv(TileState s, IAv1SymbolSink sink, Av1CdfContext cdf, int mvRow, int mvCol, int predMvRow, int predMvCol)
    {
        int diffRow = mvRow - predMvRow;
        int diffCol = mvCol - predMvCol;
        int mvJoint = (diffRow != 0 ? 2 : 0) | (diffCol != 0 ? 1 : 0);

        sink.WriteSymbol(cdf.MvJoint, mvJoint);
        if (mvJoint == MvJointHzvnz || mvJoint == MvJointHnzvnz)
        {
            WriteMvComponent(sink, cdf, 0, diffRow);
        }

        if (mvJoint == MvJointHnzvz || mvJoint == MvJointHnzvnz)
        {
            WriteMvComponent(sink, cdf, 1, diffCol);
        }
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ReadMvComponent</c>.</summary>
    private static void WriteMvComponent(IAv1SymbolSink sink, Av1CdfContext cdf, int comp, int diff)
    {
        int sign = diff < 0 ? 1 : 0;
        int absMv = Math.Abs(diff);

        // absMv is always a positive multiple of 8 (force_integer_mv is unconditionally 1 -- see
        // Av1TileDecoder.ReadMvComponent's remarks): k = absMv/8 - 1 inverts read_mv_component's own
        // (class0_bit or mv_class+d) -> magnitude packing exactly (mag-1's low 3 bits are always the
        // forced 111 from mv_class0_fr/hp or mv_fr/hp, so dividing by 8 and subtracting 1 recovers the
        // class-selecting index cleanly).
        int k = (absMv / 8) - 1;
        int mvClass = k <= 1 ? MvClass0 : Av1CdfAdaptation.FloorLog2((uint)k);

        sink.WriteSymbol(cdf.MvSign[comp], sign);
        sink.WriteSymbol(cdf.MvClass[comp], mvClass);
        if (mvClass == MvClass0)
        {
            sink.WriteSymbol(cdf.MvClass0Bit[comp], k);
        }
        else
        {
            int d = k - (1 << mvClass);
            for (int i = 0; i < mvClass; i++)
            {
                sink.WriteSymbol(cdf.MvBit[comp][i], (d >> i) & 1);
            }
        }
    }

    /// <summary>
    /// Cost-estimate mirror of <see cref="WriteMv(TileState, int, int, int, int)"/> -- same symbol structure, but priced via
    /// <see cref="Av1SymbolEncoder.EstimateSymbolCost"/> against the tile's real, current
    /// <see cref="TileState.Cdf"/> instead of actually writing any bits. Needed by
    /// <see cref="FindIntrabcMatch"/> to rank multiple exact-pixel-match candidates against each other:
    /// <see cref="ComputeIntrabcLumaCost"/> can't do this (its residual-only cost is near-zero for any exact
    /// match, regardless of DV magnitude), but the real DV-signaling cost this estimates varies directly with
    /// how far the source is from <paramref name="predMvRow"/>/<paramref name="predMvCol"/> -- exactly the
    /// dimension that distinguishes one exact match from another.
    /// </summary>
    private static long EstimateMvCost(TileState s, int mvRow, int mvCol, int predMvRow, int predMvCol)
    {
        int diffRow = mvRow - predMvRow;
        int diffCol = mvCol - predMvCol;
        int mvJoint = (diffRow != 0 ? 2 : 0) | (diffCol != 0 ? 1 : 0);

        long cost = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.MvJoint, mvJoint);
        if (mvJoint == MvJointHzvnz || mvJoint == MvJointHnzvnz)
        {
            cost += EstimateMvComponentCost(s, 0, diffRow);
        }

        if (mvJoint == MvJointHnzvz || mvJoint == MvJointHnzvnz)
        {
            cost += EstimateMvComponentCost(s, 1, diffCol);
        }

        return cost;
    }

    /// <summary>Cost-estimate mirror of <see cref="WriteMvComponent"/> -- see <see cref="EstimateMvCost"/>'s own remarks.</summary>
    private static long EstimateMvComponentCost(TileState s, int comp, int diff)
    {
        int sign = diff < 0 ? 1 : 0;
        int absMv = Math.Abs(diff);
        int k = (absMv / 8) - 1;
        int mvClass = k <= 1 ? MvClass0 : Av1CdfAdaptation.FloorLog2((uint)k);

        long cost = Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.MvSign[comp], sign);
        cost += Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.MvClass[comp], mvClass);
        if (mvClass == MvClass0)
        {
            cost += Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.MvClass0Bit[comp], k);
        }
        else
        {
            int d = k - (1 << mvClass);
            for (int i = 0; i < mvClass; i++)
            {
                cost += Av1SymbolEncoder.EstimateSymbolCost(s.CostCdf.MvBit[comp][i], (d >> i) & 1);
            }
        }

        return cost;
    }

    /// <summary><c>get_palette_bsize_ctx</c> (spec §8.3.2's palette context derivation): <c>FloorLog2(pixel count) - FloorLog2(64)</c> -- 0 for an 8x8 leaf up to 6 for 64x64.</summary>
    private static int GetPaletteBsizeCtx(int bSize)
    {
        int numPels = Av1BlockTables.BlockWidth(bSize) * Av1BlockTables.BlockHeight(bSize);
        return Av1CdfAdaptation.FloorLog2((uint)numPels) - 6;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.GetPaletteModeCtx</c>: count of {above, left} neighbors that themselves used a luma palette.</summary>
    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.GetTxDepthCtx</c>'s own context derivation for <c>tx_depth</c>
    /// (spec §8.3.2) -- called once per non-lossless leaf with <c>sizePixels &gt; 4</c>, before the tx_depth
    /// symbol is written, to select the same above/left-based context a real decoder independently derives
    /// via its own <c>_interTxSizes</c> neighbor state.
    ///
    /// <para>Matches the decoder's own <c>GetAboveTxWidth</c>/<c>GetLeftTxHeight</c> exactly, including their
    /// <c>is_inter &amp;&amp; skip</c> special case (spec's own tx-size-defaults-to-coding-block-size rule for
    /// such a neighbor, rather than reading its real signalled tx size): this used to be safely skippable,
    /// since IntraBC (the only way a non-lossless neighbor could ever have <c>is_inter</c> true) was
    /// lossless-only, and lossless never reaches this method at all (tx_depth is never signalled there) --
    /// so every neighbor this method could ever read really was a plain, non-IntraBC leaf. That's no longer
    /// true now that non-lossless IntraBC exists (project plan Phase 4, exact-match only): an IntraBC
    /// neighbor writes <see cref="TileState.IsInters"/>[idx]=true (see <c>EncodeLeaf</c>'s own per-mi commit
    /// loop) but never writes <see cref="TileState.LumaTxSizes"/> for its own mi positions at all (that array
    /// is only ever updated by the plain, non-IntraBC commit branch) -- reading it unconditionally for such a
    /// neighbor would silently use a stale/default value instead of the real coding-block-size-derived one,
    /// picking the wrong CDF context and desyncing the entropy coder for every leaf whose above or left
    /// neighbor is an IntraBC leaf. Checks <c>IsInters</c> alone, matching real
    /// <c>Av1TileDecoder.ReadTxDepth</c>'s own condition exactly (its inline <c>aboveW</c>/<c>leftH</c>
    /// derivation, not <c>GetAboveTxWidth</c>/<c>GetLeftTxHeight</c>'s own internal <c>Skips &amp;&amp;
    /// IsInters</c> check -- that check is provably dead when called from <c>ReadTxDepth</c>, since it's only
    /// reached in the already-known-not-inter branch) -- equivalent to also requiring <c>Skips</c> only
    /// because every leaf <em>this</em> method can ever read is itself non-lossless, where an IntraBC
    /// neighbor can only ever be skip=1 (exact-match; approximate/residual IntraBC stays lossless-only, see
    /// <c>intrabcApproxCandidate</c>'s own remarks), but written to match the decoder's real condition
    /// directly rather than relying on that invariant staying true. Found via direct comparison against the
    /// decoder's own logic while auditing this same change, not a round-trip failure -- the one test built
    /// for this feature (<c>Rgb24TiledPattern_NonLossless_UsesIntrabcAndRoundTrips</c>) happened not to
    /// trigger it in its own specific partition layout, underscoring why this kind of neighbor-context logic
    /// needs a direct side-by-side check against the decoder, not just a passing round-trip test.</para>
    /// </summary>
    private static int GetTxDepthCtx(TileState s, int r, int c, bool availU, bool availL, int maxRectTxSize)
    {
        int maxTxWidth = Av1TxDimensions.Width[maxRectTxSize];
        int maxTxHeight = Av1TxDimensions.Height[maxRectTxSize];

        int aboveW = 0;
        if (availU)
        {
            int aboveIdx = ((r - 1) * s.MiCols) + c;
            aboveW = s.IsInters[aboveIdx]
                ? Av1BlockTables.BlockWidth(s.MiSizes[aboveIdx])
                : Av1TxDimensions.Width[s.LumaTxSizes[aboveIdx]];
        }

        int leftH = 0;
        if (availL)
        {
            int leftIdx = (r * s.MiCols) + c - 1;
            leftH = s.IsInters[leftIdx]
                ? Av1BlockTables.BlockHeight(s.MiSizes[leftIdx])
                : Av1TxDimensions.Height[s.LumaTxSizes[leftIdx]];
        }

        return (aboveW >= maxTxWidth ? 1 : 0) + (leftH >= maxTxHeight ? 1 : 0);
    }

    /// <summary>
    /// Write-side of <c>read_tx_size</c>/<c>tx_depth</c> (spec §5.11.15/§8.3.2) for one non-lossless leaf --
    /// writes the real <paramref name="txDepth"/> the search in <see cref="EncodeLeaf"/> already decided
    /// (0 = no split, this leaf's own natural tx size; 1 = one uniform quad-split down -- see that method's
    /// own tx-size-RDO remarks for why real aomenc's own cpu-used=2 speed features never search beyond
    /// depth 1), via the same <c>maxTxDepth</c>-selected CDF (<see cref="Av1CdfContext.Tx8x8"/>/
    /// <see cref="Av1CdfContext.Tx16x16"/>/<see cref="Av1CdfContext.Tx32x32"/>) <c>Av1TileDecoder.ReadTxDepth</c>
    /// reads from, mirrored exactly via <see cref="Av1MaxTxDepth"/> and <paramref name="bSize"/>. Only ever
    /// called for <c>sizeMi &gt; 1</c> (sizePixels &gt; 4, matching the decoder's own <c>MiSize &gt;
    /// BLOCK_4X4</c> gate) -- a genuine 4x4 leaf never has a tx_depth symbol at all, real or written.
    /// </summary>
    private static void WriteTxDepth(TileState s, int r, int c, bool availU, bool availL, int bSize, int txDepth)
    {
        int maxRectTxSize = Av1BlockTables.MaxTxSizeRect[bSize];
        int ctx = GetTxDepthCtx(s, r, c, availU, availL, maxRectTxSize);
        int maxTxDepth = Av1MaxTxDepth.Values[bSize];

        var cdf = maxTxDepth switch
        {
            4 => s.Cdf.Tx64x64[ctx],
            3 => s.Cdf.Tx32x32[ctx],
            2 => s.Cdf.Tx16x16[ctx],
            _ => s.Cdf.Tx8x8[ctx],
        };

        if (s.Phase != Av1EncodePhase.Decide)
        {
            s.Symbols.WriteSymbol(cdf, txDepth);
        }

        // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
        if (s.Phase != Av1EncodePhase.Emit)
        {
            var decideCdf = maxTxDepth switch
            {
                4 => s.DecideCdf.Tx64x64[ctx],
                3 => s.DecideCdf.Tx32x32[ctx],
                2 => s.DecideCdf.Tx16x16[ctx],
                _ => s.DecideCdf.Tx8x8[ctx],
            };
            s.DecideCommitSink.WriteSymbol(decideCdf, txDepth);
        }
    }

    private static int GetPaletteModeCtx(TileState s, int r, int c, bool availU, bool availL)
    {
        int ctx = 0;
        if (availU && s.PaletteSizesY[((r - 1) * s.MiCols) + c] > 0)
        {
            ctx++;
        }

        if (availL && s.PaletteSizesY[(r * s.MiCols) + c - 1] > 0)
        {
            ctx++;
        }

        return ctx;
    }

    /// <summary>
    /// The write-side count-only mirror of <c>Av1TileDecoder.GetPaletteCache</c>: how many "is this cache
    /// color used" bits <see cref="WritePaletteColorsY"/>/<see cref="WritePaletteColorsUv"/> must write
    /// before their literal colors (this method's caller always answers every one of them "no" -- see
    /// EncodeLeaf's remarks -- but the *count* itself is spec-normative, computed from
    /// the same frame-shared above/left neighbor palette state a real decoder independently derives, so it
    /// has to match exactly even though none of the actual cache values end up used).
    /// </summary>
    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.GetPaletteCache</c>: the actual sorted, deduplicated neighbor
    /// palette color <em>values</em> (not just the count -- real bit cost/writing now needs to know which
    /// specific colors are cache-adjacent, for <see cref="Av1PaletteSearch.OptimizePaletteColors"/>'s own
    /// centroid-snapping and the real cache-hit-aware cost/write path below), computed from the same
    /// frame-shared above/left neighbor palette state a real decoder independently derives.
    /// <paramref name="cache"/> must be at least 16 entries (2x <c>PALETTE_MAX_SIZE</c>, matching the
    /// decoder's own sizing).
    /// </summary>
    private static int GetPaletteCacheColors(TileState s, int plane, int r, int c, bool availU, bool availL, Span<int> cache)
    {

        bool aboveExcluded = r % 16 == 0;
        bool useAbove = availU && !aboveExcluded;
        bool useLeft = availL;

        int[] sizes = plane == 0 ? s.PaletteSizesY : s.PaletteSizesUV;
        int[] grid = plane == 0 ? s.PaletteColorsYGrid : s.PaletteColorsUGrid;

        int aboveN = 0, leftN = 0, aboveBase = 0, leftBase = 0;
        if (useAbove)
        {
            int idx = ((r - 1) * s.MiCols) + c;
            aboveN = sizes[idx];
            aboveBase = idx * 8;
        }

        if (useLeft)
        {
            int idx = (r * s.MiCols) + c - 1;
            leftN = sizes[idx];
            leftBase = idx * 8;
        }

        if (aboveN == 0 && leftN == 0)
        {
            return 0;
        }

        int n = 0;
        int aboveIdx = 0, leftIdx = 0;
        while (aboveIdx < aboveN && leftIdx < leftN)
        {
            int vAbove = grid[aboveBase + aboveIdx];
            int vLeft = grid[leftBase + leftIdx];
            if (vLeft < vAbove)
            {
                AddToPaletteCache(cache, ref n, vLeft);
                leftIdx++;
            }
            else
            {
                AddToPaletteCache(cache, ref n, vAbove);
                aboveIdx++;
                if (vLeft == vAbove)
                {
                    leftIdx++;
                }
            }
        }

        while (aboveIdx < aboveN)
        {
            AddToPaletteCache(cache, ref n, grid[aboveBase + aboveIdx++]);
        }

        while (leftIdx < leftN)
        {
            AddToPaletteCache(cache, ref n, grid[leftBase + leftIdx++]);
        }

        return n;
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.AddToPaletteCache</c> -- skips a value equal to the cache's own current last entry, keeping the merged cache ascending and duplicate-free.</summary>
    private static void AddToPaletteCache(Span<int> cache, ref int n, int value)
    {
        if (n > 0 && value == cache[n - 1])
        {
            return;
        }

        cache[n++] = value;
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadPaletteColorsY</c>/<c>Uv</c>: writes one bypass ("is this
    /// cache color used") bit per checked cache slot (see <see cref="Av1PaletteSearch.IndexColorCache"/>'s
    /// own remarks on the real early-stop protocol -- not every cache slot always gets a bit), then the
    /// explicit (not-cached) colors -- the first as a raw 8-bit literal, the rest as ascending
    /// <c>delta - minVal</c> values (skipped entirely when every chosen color came from the cache).
    ///
    /// <para>The delta bit-width is <em>not</em> a free encoder choice held constant across every delta --
    /// the decoder recomputes it after every color from the shrinking "remaining value range" (<c>range -=
    /// colors[idx] - colors[idx - 1]</c>, then <c>bits = min(bits, CeilLog2(range))</c>), so this has to
    /// replicate that exact shrinking (this project's own exact algorithm, verified against the real
    /// decoder -- not libaom's own looser <c>delta_encode_cost</c> RD-estimate approximation, which the real
    /// bitstream writer doesn't use either) over the explicit colors' own independent ascending run, not
    /// just pick one width generously large enough for the first delta and reuse it.</para>
    /// </summary>
    private static void WriteCacheAwareColors(TileState s, ReadOnlySpan<int> cache, int nCache, ReadOnlySpan<int> colors, int n, int minVal)
    {
        Span<bool> found = stackalloc bool[16];
        Span<int> explicitColors = stackalloc int[8];
        int nExplicit = Av1PaletteSearch.IndexColorCache(cache, nCache, colors, n, found, explicitColors, out int slotsChecked);

        for (int i = 0; i < slotsChecked; i++)
        {
            s.Symbols.WriteLiteral(found[i] ? 1u : 0u, 1);
        }

        if (nExplicit > 0)
        {
            s.Symbols.WriteLiteral((uint)explicitColors[0], 8);
            if (nExplicit > 1)
            {
                const int minBits = 5; // bitDepth(8) - 3
                const int extraBits = 3; // always the maximum -- see WritePaletteColorsY's own remarks on why this is always safe as the *starting* width; it only ever shrinks from here, identically on both sides.
                s.Symbols.WriteLiteral(extraBits, 2);
                int bits = minBits + extraBits;
                int range = 256 - explicitColors[0] - minVal;
                for (int idx = 1; idx < nExplicit; idx++)
                {
                    s.Symbols.WriteLiteral((uint)(explicitColors[idx] - explicitColors[idx - 1] - minVal), bits);
                    range -= explicitColors[idx] - explicitColors[idx - 1];
                    bits = Math.Min(bits, Av1TileDecoder.CeilLog2(range));
                }
            }
        }
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.ReadPaletteColorsY</c> -- see <see cref="WriteCacheAwareColors"/>'s remarks for the real cache-hit-aware protocol this now uses.</summary>
    private static void WritePaletteColorsY(TileState s, int[] colors, int n, int r, int c, bool availU, bool availL)
    {
        Span<int> cache = stackalloc int[16];
        int nCache = GetPaletteCacheColors(s, 0, r, c, availU, availL, cache);
        WriteCacheAwareColors(s, cache[..nCache], nCache, colors.AsSpan(0, n), n, minVal: 1);
    }

    /// <summary>
    /// Write-side mirror of <c>Av1TileDecoder.ReadPaletteColorsUv</c>: U follows <see cref="WritePaletteColorsY"/>'s
    /// exact cache-hit-aware shape (against the U-specific cache, and U's own unshifted-by-1 range/delta
    /// convention -- see <see cref="WriteCacheAwareColors"/>'s remarks). V is never cached (confirmed by
    /// direct decoder-side reading) and is always written in its own flat, non-delta form (spec's
    /// <c>palette_colors_v_contain_extra_bit</c>-style bit = 0), a plain 8-bit literal per color in
    /// palette-index order -- simpler and always correct, unlike the delta form, which would need
    /// modular-wraparound reasoning to guarantee every delta fits its bit budget -- so V's own writing here
    /// stays completely unchanged.
    /// </summary>
    private static void WritePaletteColorsUv(TileState s, int[] uColors, int[] vColors, int n, int r, int c, bool availU, bool availL)
    {
        Span<int> cache = stackalloc int[16];
        int nCache = GetPaletteCacheColors(s, 1, r, c, availU, availL, cache);
        WriteCacheAwareColors(s, cache[..nCache], nCache, uColors.AsSpan(0, n), n, minVal: 0);

        s.Symbols.WriteBool(0); // not delta-encoded
        for (int i = 0; i < n; i++)
        {
            s.Symbols.WriteLiteral((uint)vColors[i], 8);
        }
    }

    /// <summary>
    /// Two-pass tile-encoder architecture, Stage 1a: copies a leaf-local, onscreen-clipped palette
    /// color-map region (<paramref name="localMap"/>, row-major at <paramref name="localWidth"/> stride,
    /// exactly the same region <see cref="WriteColorMapTokens(TileState, int[], int, int, int, int, int, ushort[][][])"/> was just called with) into its matching
    /// position in a frame-wide grid (<see cref="TileState.PaletteColorMapYGrid"/>/
    /// <see cref="TileState.PaletteColorMapUvGrid"/>), addressed by global pixel row/col -- mirroring
    /// exactly how every other per-leaf reconstruction copy in this file (e.g. the all-zero-residual
    /// palette branch's own <c>Array.Copy(s.SourceY, ...)</c> loop) already writes into a global-offset
    /// buffer from a leaf-local one. Called once per <see cref="WriteColorMapTokens(TileState, int[], int, int, int, int, int, ushort[][][])"/> call site, right
    /// after it, never on its own.
    /// </summary>
    private static void CopyColorMapToFrameGrid(int[] frameGrid, int frameStride, int[] localMap, int localWidth, int globalX, int globalY, int onscreenWidth, int onscreenHeight)
    {
        for (int row = 0; row < onscreenHeight; row++)
        {
            Array.Copy(localMap, row * localWidth, frameGrid, ((globalY + row) * frameStride) + globalX, onscreenWidth);
        }
    }

    /// <summary>Write-side mirror of <c>Av1TileDecoder.DecodeColorMapTokens</c>, restricted to this encoder's always-fully-on-screen leaves (no off-screen edge extension needed -- see the class-level remarks on padding). Writes the first index via <see cref="Av1SymbolEncoder.WriteNs"/>, then every later position in the same anti-diagonal ("wavefront") order the decoder reads in, so <see cref="Av1TileDecoder.GetPaletteColorIndexContext"/>'s left/above-left/above neighbors are always already-written -- reusing that exact method (rather than a separate write-side copy) guarantees the context this writes against can never drift from what a real decoder derives.</summary>
    /// <summary>
    /// <paramref name="onscreenWidth"/>/<paramref name="onscreenHeight"/> (project plan Round N+36's own
    /// root-cause investigation): a real, independently-existing bitstream-desync bug found and fixed this
    /// round, not present before this method gained real rectangular-leaf/true-edge-overhang callers.
    /// <see cref="Av1TileDecoder.DecodeColorMapTokens"/> (spec §5.11.47/libaom's own
    /// <c>decode_color_map_tokens</c>) only ever entropy-codes the color-map's own ON-SCREEN region -- a
    /// coding block whose nominal footprint overhangs the frame's TRUE (unpadded) edge never has tokens
    /// coded for the off-screen portion at all; those cells are filled afterward by real edge-replication,
    /// exactly like every other off-screen prediction/residual read in this whole codec. This method used to
    /// unconditionally scan the FULL nominal <paramref name="width"/> x <paramref name="height"/> region
    /// regardless, writing real entropy-coded tokens for cells no real decoder -- including this project's
    /// own -- ever reads back, permanently desyncing the entropy coder's own range/CDF-adaptation state for
    /// every single symbol written after the affected leaf (found via a direct, symbol-by-symbol range-state
    /// trace comparison against a real decode, this investigation's own established technique, isolating the
    /// exact leaf and exact mechanism -- see the project plan's own round log for the full trace). Confined
    /// to this method specifically because every OTHER residual-writing loop in this file already correctly
    /// clips to <see cref="TileState.EdgeMaxX"/>/<see cref="TileState.EdgeMaxY"/>/
    /// <see cref="TileState.ChromaEdgeMaxX"/>/<see cref="TileState.ChromaEdgeMaxY"/> per sub-block -- only
    /// this one, real-per-pixel-not-per-4x4-sub-block scan lacked the equivalent check.
    /// </summary>
    private static void WriteColorMapTokens(TileState s, int[] colorMap, int width, int height, int onscreenWidth, int onscreenHeight, int n, ushort[][][] mapCdf)
        => WriteColorMapTokens(s.Symbols, colorMap, width, height, onscreenWidth, onscreenHeight, n, mapCdf);

    /// <summary>
    /// Real symbol-writing body of <see cref="WriteColorMapTokens(TileState, int[], int, int, int, int, int, ushort[][][])"/>,
    /// parameterized by <paramref name="sink"/> so the two-argument overload above can be called once for
    /// real and once more for the parallel decide-time tracking call (passing
    /// <see cref="TileState.DecideCommitSink"/> and the matching <see cref="TileState.DecideCdf"/> table) --
    /// see <see cref="TileState.DecideYCoeffCtx"/>'s own remarks. <see cref="IAv1SymbolSink.WriteLiteral"/>
    /// (which <see cref="Av1SymbolEncoder.WriteNs"/> is built from) never adapts any CDF (a real,
    /// non-adaptive bypass code -- confirmed via direct source reading of <see cref="Av1SymbolEncoder.WriteBool"/>'s
    /// own fresh-50/50-CDF-every-call implementation), so only the per-pixel <c>WriteSymbol</c> call below
    /// actually needs a decide-tracking counterpart -- <paramref name="mapCdf"/> itself is already threaded
    /// in by the caller (real <c>s.Cdf.PaletteYColorIndex</c>/<c>UvColorIndex</c> or, for the decide-tracking
    /// call, <c>s.DecideCdf</c>'s own matching table), so this method needs no direct <see cref="TileState"/>
    /// reference of its own.
    /// </summary>
    private static void WriteColorMapTokens(IAv1SymbolSink sink, int[] colorMap, int width, int height, int onscreenWidth, int onscreenHeight, int n, ushort[][][] mapCdf)
    {
        var colorOrder = new int[8];
        var inverseColorOrder = new int[8];

        // IAv1SymbolSink has no WriteNs (only Av1SymbolEncoder itself does, used by the real single-sink
        // TileState.Symbols call path elsewhere) -- inlined here as the exact same NS(n) logic in terms of
        // WriteLiteral (which IS on the interface, and which -- like WriteNs itself -- never adapts any
        // CDF, see this method's own class remarks), rather than widening the interface for one caller.
        int nsW = Av1CdfAdaptation.FloorLog2((uint)n) + 1;
        int nsM = (1 << nsW) - n;
        if (colorMap[0] < nsM)
        {
            sink.WriteLiteral((uint)colorMap[0], nsW - 1);
        }
        else
        {
            int nsT = colorMap[0] + nsM;
            sink.WriteLiteral((uint)(nsT >> 1), nsW - 1);
            sink.WriteLiteral((uint)(nsT & 1), 1);
        }

        // Same width/height-generalized anti-diagonal scan as EstimateColorMapBits -- must stay in lockstep
        // with it (and with the decoder's DecodeColorMapTokens) so contexts never drift. Bounded by
        // onscreenWidth/onscreenHeight, not width/height -- see this method's own remarks above.
        for (int i = 1; i < onscreenHeight + onscreenWidth - 1; i++)
        {
            for (int col = Math.Min(i, onscreenWidth - 1); col >= Math.Max(0, i - onscreenHeight + 1); col--)
            {
                int row = i - col;
                int ctx = Av1TileDecoder.GetPaletteColorIndexContext(colorMap, width, row, col, n, colorOrder);
                for (int k = 0; k < n; k++)
                {
                    inverseColorOrder[colorOrder[k]] = k;
                }

                int trueIdx = colorMap[(row * width) + col];
                int symbol = inverseColorOrder[trueIdx];
                sink.WriteSymbol(mapCdf[n - 2][ctx], symbol);
            }
        }
    }

    /// <summary>
    /// Encodes the chroma (U then V, matching spec <c>residual()</c>'s plane-outer loop order -- see the
    /// remarks below) region matching one luma leaf: at 4:2:0, a <c>(sizeMi/2)</c>-square grid of 4x4
    /// sub-blocks (half the luma leaf's mi extent in each dimension); at 4:4:4 (only ever paired with
    /// <see cref="TileState.Lossless"/> -- see <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate),
    /// a <c>sizeMi</c>-square grid at luma-identical (unhalved) coordinates. Uses <paramref name="uvMode"/>/
    /// <paramref name="uvAngleDelta"/> (real cost-searched, see <see cref="SearchUvMode"/> -- CFL isn't
    /// implemented, so <paramref name="uvMode"/> is never <see cref="Av1IntraMode.UvCflPred"/>), and follows
    /// <see cref="TileState.Lossless"/> per sub-block for WHT vs. DCT/ADST exactly like the single-sub-block
    /// case this generalizes did.
    ///
    /// <para>Non-lossless forward-transforms with <c>Av1TxTypeTables.ModeToTxfm[uvMode]</c> -- DCT_DCT for
    /// DC_PRED, one of Av1ForwardTransform's AdstDct/DctAdst/AdstAdst operators for every other mode -- the
    /// exact same table <c>Av1TileDecoder.ComputeTxType</c> uses to pick its inverse transform for a chroma
    /// block from <c>_uvMode</c> alone (chroma's <c>tx_type</c> is never itself bitstream-signalled, unlike
    /// luma's; see <c>writeLumaTxType: null</c> below). Getting this wrong -- forward-transforming with a
    /// different type than <c>ModeToTxfm[uvMode]</c> implies -- wouldn't just compress worse, it would
    /// silently desync the decoder's reconstruction from this leaf onward, since the decoder derives its
    /// inverse transform from the signalled <c>uv_mode</c> with no way to learn otherwise. Lossless instead
    /// always uses WHT (<see cref="Av1ForwardWht"/>), matching <c>ComputeTxType</c>'s own <c>_lossless</c>
    /// short-circuit to DCT_DCT/WHT regardless of <c>uv_mode</c> -- see <see cref="TileState.Lossless"/>'s
    /// remarks.</para>
    ///
    /// <para>Plane must be the outer loop and sub-block position the inner loop -- not the reverse -- to
    /// match spec §5.11.34 <c>residual()</c>'s own <c>for (plane ...) { for (y...) for (x...)
    /// transform_block() } }</c> nesting: a real decoder reads every one of U's transform blocks in this
    /// coding block before reading any of V's, so writing them interleaved by position would silently
    /// desync the entropy stream the moment there's more than one sub-block per plane (any leaf bigger than
    /// the previous fixed 8x8 grid).</para>
    /// </summary>
    private static void EncodeChromaRegion(TileState s, int r, int c, int x, int y, int sizeMi, int uvMode, int uvAngleDelta, int alphaU, int alphaV)
    {
        int uvTxType = Av1TxTypeTables.ModeToTxfm[uvMode];
        int chromaN = s.Chroma444 ? sizeMi : sizeMi / 2;
        int chromaR4Base = s.Chroma444 ? r : r / 2;
        int chromaC4Base = s.Chroma444 ? c : c / 2;
        int cxBase = s.Chroma444 ? x : x / 2;
        int cyBase = s.Chroma444 ? y : y / 2;

        // The chroma coding block's own pixel width/height, passed to WriteCoeffs as blockSize whenever it
        // differs from the 4x4 transform (chromaN > 1) -- matches EncodeLosslessLumaResidual's identical
        // blockSize reasoning, just applied to the (possibly smaller, 4:2:0) chroma region instead of luma's.
        int chromaBlockSizePixels = chromaN * 4;
        int blockSizeArg = chromaN > 1 ? chromaBlockSizePixels : 0;

        // haveAboveRight/haveBelowLeft/filterTypeSmooth now need to be real (see SearchUvMode's remarks on
        // why the previous hardcoded false/false and filterTypeSmooth: false were only ever safe while
        // uvMode was always DC_PRED): mult converts a chroma 4x4-unit offset back to luma mi units (each
        // chroma unit spans 2 luma mi units at 4:2:0, 1 at 4:4:4) so the per-sub-block position can be
        // masked into the same superblock-relative space GetBlockDecoded/SetBlockDecoded already use for
        // luma, then shifted down by subX to land in this plane's own (possibly halved) index space --
        // mirrors Av1TileDecoder.TransformBlock's row/col/subX derivation exactly (see that method's remarks).
        bool subsampled = !s.Chroma444;
        int subX = subsampled ? 1 : 0;
        int mult = subsampled ? 2 : 1;
        bool filterTypeSmooth = GetChromaFilterType(s, r, c, r > 0, c > 0, subsampled);

        // Non-lossless with more than one 4x4 worth of chroma (chromaN > 1, i.e. every 16x16/32x32 luma leaf
        // -- see the project plan's partition/TX-size RDO phase) codes ONE transform sized to the whole
        // chroma region instead of looping a grid of 4x4 sub-blocks: a real decoder's GetTxSizeForPlane
        // always derives chroma's transform as the single largest size that fits the residual region outside
        // lossless's own spec-forced TX_4X4, so looping 4x4 sub-blocks here (as the lossless path below
        // still correctly does -- WHT is genuinely forced to 4x4 regardless of leaf size) would write the
        // wrong number of symbols against the wrong scan table for anything a real decoder expects.
        // uvTxType (already resolved above from the real, searched uvMode -- see EncodeLeaf's remarks on why
        // that search is safe at every size now) is passed straight through to the one larger transform this
        // codes: every Av1TxTypeTables.ModeToTxfm entry is one of DCT_DCT/AdstDct/DctAdst/AdstAdst, and
        // Av1ForwardTransform supports all four at every size this branch ever sees (4:2:0's largest chroma
        // region, from a 32x32 luma leaf, is 16x16), so there's no size/type combination left here that
        // forward-transforming can't handle.
        if (!s.Lossless && chromaN > 1)
        {
            EncodeNonLosslessLargeChromaRegion(s, r, c, chromaR4Base, chromaC4Base, cxBase, cyBase, chromaBlockSizePixels, subX, filterTypeSmooth, uvMode, uvAngleDelta, uvTxType, alphaU, alphaV);
            return;
        }

        // Non-lossless (this branch never reaches here with s.Lossless -- see EncodeLeaf's remarks on why
        // CFL is scoped to the lossy path), chromaN == 1 always means this loop's single (dr, dc) == (0, 0)
        // iteration covers exactly this leaf's own luma extent (sizeMi == 2, an 8x8 luma leaf at 4:2:0's
        // fixed non-lossless partition floor -- see the class remarks), so (x, y) -- this leaf's own base
        // luma pixel position, already this method's own parameters -- is CFL's luma base directly, with no
        // per-sub-block offset to add.
        bool isCfl = uvMode == Av1IntraMode.UvCflPred;

        int planeIndex = 0;
        foreach (var (source, recon, ctx, decideCtx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!, s.DecideUCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!, s.DecideVCoeffCtx!),
        })
        {
            planeIndex++;

            for (int dr = 0; dr < chromaN; dr++)
            {
                for (int dc = 0; dc < chromaN; dc++)
                {
                    int chromaR4 = chromaR4Base + dr;
                    int chromaC4 = chromaC4Base + dc;
                    int cx = cxBase + (dc * 4);
                    int cy = cyBase + (dr * 4);

                    // transform_block() (spec §5.11.35) per-sub-block edge skip -- see
                    // EncodeLosslessLumaResidual's identical remarks on why this needs no other bookkeeping.
                    // A guaranteed no-op for non-lossless (ChromaEdgeMaxX/Y == ChromaWidth/Height - 1 there,
                    // see TrueMiCols's own remarks), only ever live for lossless.
                    if (cx > s.ChromaEdgeMaxX || cy > s.ChromaEdgeMaxY)
                    {
                        continue;
                    }

                    bool availU = chromaR4 > 0;
                    bool availL = chromaC4 > 0;

                    int subBlockChromaRow = ((r + (dr * mult)) & s.SbMiMask) >> subX;
                    int subBlockChromaCol = ((c + (dc * mult)) & s.SbMiMask) >> subX;
                    bool haveAboveRight = GetBlockDecoded(s, planeIndex, subBlockChromaRow - 1, subBlockChromaCol + 1);
                    bool haveBelowLeft = GetBlockDecoded(s, planeIndex, subBlockChromaRow + 1, subBlockChromaCol - 1);

                    var above = s.SubBlockAbove;
                    var left = s.SubBlockLeft;
                    Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);

                    // Reuses the same tile-wide scratch buffers luma just finished using for this leaf --
                    // safe because luma's use of them is already fully consumed (WriteCoeffs/Reconstruct
                    // called for every luma sub-block) before this method runs.
                    var pred = s.Pred;

                    // CFL predicts through an ordinary DC_PRED baseline (predict_intra's own DC-with-
                    // UV_CFL_PRED-mapped-to-DC_PRED call, spec §7.11.2 -- mirrored from Av1TileDecoder's
                    // identical `mode = isCfl ? DcPred : uvMode` substitution), then adds the luma-derived AC
                    // term on top -- Predict() itself has no UV_CFL_PRED case to dispatch to.
                    Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, above, left, isCfl ? Av1IntraMode.DcPred : uvMode, availL, availU, useFilterIntra: false, filterIntraMode: 0, uvAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
                    if (isCfl)
                    {
                        int dcConstant = pred[0];
                        int alpha = planeIndex == 1 ? alphaU : alphaV;
                        long lumaAvg = ComputeCflLumaAc(s.ReconY, s.YWidth, x, y, 4, log2Size: 2, subX, s.CflLumaAc);
                        ApplyCflAlpha(pred, s.CflLumaAc, lumaAvg, total: 16, dcConstant, alpha, bitDepth: 8);
                    }

                    var residual = s.Residual;
                    for (int i = 0; i < 16; i++)
                    {
                        residual[i] = source[((cy + (i / 4)) * s.ChromaWidth) + cx + (i % 4)] - pred[i];
                    }

                    var coeff = s.Coeff;
                    if (s.Lossless)
                    {
                        Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                    }
                    else
                    {
                        Av1ForwardTransform.Forward2D(residual.AsSpan(0, 16), coeff.AsSpan(0, 16), 4, uvTxType);
                    }

                    var levels = s.Levels;
                    Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);
                    if (!s.Lossless)
                    {
                        // Two-pass tile-encoder architecture, Stage 1b-ii (Round N+60 root-cause fix) -- see
                        // LiveYCoeffCtx's own remarks: this is a SEARCH call (trellis optimization), not a
                        // commit, so it must read whichever context is actually "live" for the current phase
                        // (decideCtx during Decide, since real ctx never advances then) -- not always the real
                        // ctx this loop's own tuple happens to destructure per plane.
                        OptimizeCoeffTrellis(s, coeff, levels, 4, ptype: 1, chromaC4, chromaR4, s.Phase == Av1EncodePhase.Decide ? decideCtx : ctx);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        Array.Copy(pred, i * 4, recon, ((cy + i) * s.ChromaWidth) + cx, 4);
                    }

                    // (x4, y4) = (chromaC4, chromaR4), not (chromaR4, chromaC4) -- see EncodeLeaf's luma call
                    // site for why the argument order matters here (x4 = column, y4 = row) even though it's
                    // unobservable on any square/single-superblock chroma grid.
                    if (s.Phase != Av1EncodePhase.Decide)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null, blockSize: blockSizeArg);
                    }

                    // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                    // remarks. Same arguments as the real commit just above.
                    if (s.Phase != Av1EncodePhase.Emit)
                    {
                        Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 1, chromaC4, chromaR4, decideCtx, writeLumaTxType: null, blockSize: blockSizeArg);
                    }

                    Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, s.Lossless, uvTxType);
                    SetBlockDecoded(s, planeIndex, subBlockChromaRow, subBlockChromaCol, true);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="EncodeChromaRegion"/>'s non-lossless, chromaN &gt; 1 branch: one DCT_DCT transform sized to
    /// <paramref name="chromaBlockSizePixels"/> per plane (U then V, matching spec's plane-outer
    /// <c>residual()</c> order -- see <see cref="EncodeChromaRegion"/>'s own remarks on why that nesting
    /// matters) instead of a grid of 4x4 sub-blocks -- structurally the same single-whole-block predict/
    /// transform/quantize/write/reconstruct sequence <see cref="EncodeLeaf"/>'s own (now size-generic)
    /// non-lossless luma path uses, just applied to one chroma plane at a time. <paramref name="uvMode"/>/
    /// <paramref name="uvAngleDelta"/> come from the same real mode/angle search <see cref="EncodeLeaf"/> now
    /// always runs for chroma (Phase 4's generalized <see cref="Av1ForwardTransform"/> made real ADST-mixed
    /// search safe at every chroma size this encoder produces, removing the old forced-DC_PRED restriction);
    /// unlike <see cref="EncodeLeaf"/>'s luma path there's still no tx_type symbol to conditionally write here,
    /// since chroma's tx_type is never itself bitstream-signalled (always derived from <c>uv_mode</c> via
    /// <c>Av1TxTypeTables.ModeToTxfm</c>, see <see cref="EncodeChromaRegion"/>'s remarks) -- <paramref
    /// name="uvTxType"/> is passed through purely to drive the actual transform math. No <c>blockSize</c>
    /// override for <see cref="Av1CoefficientWriter.WriteCoeffs"/> either, since the transform now always
    /// exactly equals the chroma coding block (the same "transform == coding block" shortcut
    /// <see cref="EncodeLeaf"/>'s own non-lossless call already relies on).
    /// </summary>
    private static void EncodeNonLosslessLargeChromaRegion(TileState s, int r, int c, int chromaR4, int chromaC4, int cx, int cy, int chromaBlockSizePixels, int subX, bool filterTypeSmooth, int uvMode, int uvAngleDelta, int uvTxType, int alphaU, int alphaV)
    {
        bool availU = chromaR4 > 0;
        bool availL = chromaC4 > 0;
        bool isCfl = uvMode == Av1IntraMode.UvCflPred;

        // Real luma pixel coords this region's local (0, 0) corresponds to -- cx/cy are already real
        // full-resolution chroma-plane coords (this encoder's mi-grid alignment guarantees cx << subX == the
        // exact real luma x with no rounding loss, the same invariant SearchUvMode's own TryCflCandidate call
        // relies on for its (x, y) luma base).
        int lumaX = cx << subX;
        int lumaY = cy << subX;

        // Unlike EncodeChromaRegion's own 4x4-sub-block loop (whose subBlockChromaRow/Col shifts per
        // sub-block via `mult`), there is exactly one sub-block here -- the whole chroma region -- so its
        // BlockDecoded position is just (r, c)'s own masked-and-shifted position, no per-iteration offset.
        int subBlockChromaRow = (r & s.SbMiMask) >> subX;
        int subBlockChromaCol = (c & s.SbMiMask) >> subX;

        // Scaled by the region's own width/height in chroma 4x4 units (matching SearchUvMode's identical
        // `chromaCol + chromaN`/`chromaRow + chromaN` computation) -- a fixed +-1 offset only happens to be
        // correct at chromaN == 1 (4x4 chroma), which never reaches this method (that size takes
        // EncodeChromaRegion's own 4x4 sub-block loop instead; this method only ever runs for chromaN > 1).
        // Getting this wrong doesn't matter for DC_PRED (which never reads the above-right/below-left corner
        // samples these flags gate), so it went unnoticed while uv_mode was forced to DC_PRED here -- but a
        // real directional/smooth mode reads those corners, and BuildEdges silently clamps/replicates instead
        // of reading real neighbor pixels whenever told a neighbor isn't available, so a wrong flag corrupts
        // the whole block's prediction, not just a few edge pixels.
        int chromaN = chromaBlockSizePixels / 4;

        int log2Size = Log2FromPixels(chromaBlockSizePixels);
        var above = new Av1EdgeArray(528);
        var left = new Av1EdgeArray(528);
        var pred = s.Pred;

        int planeIndex = 0;
        foreach (var (source, recon, ctx, decideCtx) in new[]
        {
            (s.SourceU!, s.ReconU!, s.UCoeffCtx!, s.DecideUCoeffCtx!),
            (s.SourceV!, s.ReconV!, s.VCoeffCtx!, s.DecideVCoeffCtx!),
        })
        {
            planeIndex++;

            bool haveAboveRight = GetBlockDecoded(s, planeIndex, subBlockChromaRow - 1, subBlockChromaCol + chromaN);
            bool haveBelowLeft = GetBlockDecoded(s, planeIndex, subBlockChromaRow + chromaN, subBlockChromaCol - 1);

            Av1IntraPrediction.BuildEdges(above, left, recon, s.ChromaWidth, cx, cy, chromaBlockSizePixels, chromaBlockSizePixels, availL, availU, haveAboveRight, haveBelowLeft, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, bitDepth: 8);

            // CFL predicts through an ordinary DC_PRED baseline (spec §7.11.2's DC-with-UV_CFL_PRED-mapped-
            // to-DC_PRED call), then adds the luma-derived AC term on top -- Predict() itself has no
            // UV_CFL_PRED case to dispatch to (see EncodeChromaRegion's identical substitution).
            Av1IntraPrediction.Predict(pred, chromaBlockSizePixels, chromaBlockSizePixels, log2Size, log2Size, above, left, isCfl ? Av1IntraMode.DcPred : uvMode, availL, availU, useFilterIntra: false, filterIntraMode: 0, angleDelta: uvAngleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.ChromaEdgeMaxX, s.ChromaEdgeMaxY, cx, cy, bitDepth: 8);
            if (isCfl)
            {
                int dcConstant = pred[0];
                int alpha = planeIndex == 1 ? alphaU : alphaV;
                long lumaAvg = ComputeCflLumaAc(s.ReconY, s.YWidth, lumaX, lumaY, chromaBlockSizePixels, log2Size, subX, s.CflLumaAc);
                ApplyCflAlpha(pred, s.CflLumaAc, lumaAvg, total: chromaBlockSizePixels * chromaBlockSizePixels, dcConstant, alpha, bitDepth: 8);
            }

            var residual = s.Residual;
            for (int i = 0; i < chromaBlockSizePixels; i++)
            {
                int rowBase = ((cy + i) * s.ChromaWidth) + cx;
                int predRowBase = i * chromaBlockSizePixels;
                for (int j = 0; j < chromaBlockSizePixels; j++)
                {
                    residual[predRowBase + j] = source[rowBase + j] - pred[predRowBase + j];
                }
            }

            var coeff = s.Coeff;
            Av1ForwardTransform.Forward2D(residual, coeff, chromaBlockSizePixels, uvTxType);
            var levels = s.Levels;
            Av1ForwardQuantizer.Quantize(coeff, levels, chromaBlockSizePixels, s.BaseQIdx);

            // Two-pass tile-encoder architecture, Stage 1b-ii (Round N+60 root-cause fix) -- see
            // LiveYCoeffCtx's own remarks: a SEARCH call, must read whichever context is actually live for
            // the current phase, not always the real ctx this loop's own tuple happens to destructure.
            OptimizeCoeffTrellis(s, coeff, levels, chromaBlockSizePixels, ptype: 1, chromaC4, chromaR4, s.Phase == Av1EncodePhase.Decide ? decideCtx : ctx);

            for (int i = 0; i < chromaBlockSizePixels; i++)
            {
                Array.Copy(pred, i * chromaBlockSizePixels, recon, ((cy + i) * s.ChromaWidth) + cx, chromaBlockSizePixels);
            }

            if (s.Phase != Av1EncodePhase.Decide)
            {
                Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, chromaBlockSizePixels, ptype: 1, chromaC4, chromaR4, ctx, writeLumaTxType: null);
            }

            // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own remarks.
            // Same arguments as the real commit just above.
            if (s.Phase != Av1EncodePhase.Emit)
            {
                Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, chromaBlockSizePixels, ptype: 1, chromaC4, chromaR4, decideCtx, writeLumaTxType: null);
            }

            Av1LocalReconstructor.Reconstruct(recon, s.ChromaWidth, cx, cy, chromaBlockSizePixels, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: false, uvTxType);

            // Marks this whole chromaN x chromaN sub-block footprint decoded -- not just its own (top-left)
            // position -- mirroring Av1TileDecoder.TransformBlock's own `for (i < stepY) for (j < stepX))
            // SetBlockDecoded(...)` loop exactly (stepX/stepY there are this same transform's width/height in
            // chroma 4x4 units, i.e. chromaN here, since tx_mode is always TX_MODE_LARGEST -- see this
            // method's own remarks). A single-position mark left every OTHER position this region actually
            // covers (chromaN > 1 means more than one) permanently "not yet decoded" from a later leaf's own
            // haveAboveRight/haveBelowLeft query's point of view, even after this region's real reconstruction
            // finished -- silently disagreeing with a real decoder (which, per the loop above, correctly marks
            // the whole footprint) about whether a neighbor is available. Getting this wrong doesn't matter
            // for DC_PRED (which never reads the above-right/below-left corner samples these flags gate, see
            // this method's own remarks above) -- which is exactly why it went unnoticed through PR #75's own
            // real chroma mode search landing -- but a real directional/smooth (or CFL) mode reads those
            // corners, and BuildEdges silently clamps/replicates instead of reading the real neighbor pixel
            // whenever told a neighbor isn't available, corrupting the whole block's prediction from a stale
            // "not decoded yet" flag, not just a few edge pixels -- confirmed via direct encoder/decoder
            // instrumentation cross-check, not just inference: at one queried position, the encoder's own
            // BlockDecoded state said false while the real decoder, given the identical bitstream, said true
            // for the identical (plane, row, col) query.
            for (int i = 0; i < chromaN; i++)
            {
                for (int j = 0; j < chromaN; j++)
                {
                    SetBlockDecoded(s, planeIndex, subBlockChromaRow + i, subBlockChromaCol + j, true);
                }
            }
        }
    }

    /// <summary>
    /// Encodes a leaf's luma residual when lossless: AV1 forces <c>TX_4X4</c> for every block at
    /// coded-lossless, so unlike the non-lossless path (one whole-leaf DCT_DCT transform over a single
    /// whole-block prediction, only ever an 8x8 leaf here), this predicts, transforms, and reconstructs each
    /// of the <c>(blockSize/4)^2</c> 4x4 sub-blocks in raster order individually -- later sub-blocks'
    /// predictions read the just-reconstructed pixels of earlier sub-blocks in this same coding block as
    /// edge context (spec <c>predict_intra()</c> operates at the transform-block level, not the coding-block
    /// level, whenever <c>TxSize &lt; block size</c>), using <paramref name="bestMode"/> (chosen once for the
    /// whole leaf, via the SSE search in <see cref="EncodeLeaf"/> -- the encoder is free to use whatever mode-
    /// decision heuristic it likes, only the actual prediction executed here has to be spec-correct) instead
    /// of always DC_PRED, and <see cref="Av1ForwardWht"/> instead of DCT. <c>tx_type</c> is never signalled
    /// here (matching <c>Av1TileDecoder.TransformType</c>'s own <c>qindex &lt;= 0</c> short-circuit, which
    /// never reads a tx_type symbol at coded-lossless either).
    /// </summary>
    private static void EncodeLosslessLumaResidual(TileState s, int r, int c, int x, int y, int bestMode, int angleDelta, bool filterTypeSmooth, bool useFilterIntra, int filterIntraMode, int blockSize)
    {
        int n = blockSize / 4;
        for (int dr = 0; dr < n; dr++)
        {
            for (int dc = 0; dc < n; dc++)
            {
                int subX = x + (dc * 4);
                int subY = y + (dr * 4);
                int subR = r + dr;
                int subC = c + dc;

                // transform_block() (spec §5.11.35): "if (row >= MiRows || col >= MiCols) return" -- skips
                // prediction AND residual coding entirely for a sub-block whose pixel position falls outside
                // the frame's true (unpadded) extent, exactly mirroring Av1TileDecoder.TransformBlock's own
                // identical early return. Safe with no other bookkeeping: the skipped region is always a
                // rectangular edge strip relative to this leaf's own in-bounds footprint, so no later
                // sub-block in this same leaf and no future leaf (which can never itself be positioned past
                // TrueMiRows/TrueMiCols) ever needs this position's context/reconstruction state as its own
                // neighbor -- leaving YCoeffCtx/ReconY/BlockDecoded untouched here is exactly what the
                // decoder's own unconditional early return does too.
                if (subX > s.EdgeMaxX || subY > s.EdgeMaxY)
                {
                    continue;
                }

                bool availU = subR > 0;
                bool availL = subC > 0;

                // haveAboveRight/haveBelowLeft from the real BlockDecoded state (spec-accurate, mirroring
                // Av1TileDecoder.TransformBlock exactly at this same 4x4-transform-block granularity) --
                // required for correctness, not just search quality, once angle_delta is actually nonzero:
                // see EncodeLeaf's identical remarks on this same point for the whole-block search buffer.
                int subBlockMiRow = subR & s.SbMiMask;
                int subBlockMiCol = subC & s.SbMiMask;
                bool haveAboveRight = GetBlockDecoded(s, 0, subBlockMiRow - 1, subBlockMiCol + 1);
                bool haveBelowLeft = GetBlockDecoded(s, 0, subBlockMiRow + 1, subBlockMiCol - 1);

                Av1IntraPrediction.BuildEdges(s.SubBlockAbove, s.SubBlockLeft, s.ReconY, s.YWidth, subX, subY, 4, 4, availL, availU, haveAboveRight, haveBelowLeft, s.EdgeMaxX, s.EdgeMaxY, bitDepth: 8);

                var pred = s.Pred;
                Av1IntraPrediction.Predict(pred, 4, 4, 2, 2, s.SubBlockAbove, s.SubBlockLeft, bestMode, availL, availU, useFilterIntra, filterIntraMode, angleDelta, enableIntraEdgeFilter: true, filterTypeSmooth, s.EdgeMaxX, s.EdgeMaxY, subX, subY, bitDepth: 8);

                var residual = s.Residual;
                for (int i = 0; i < 16; i++)
                {
                    residual[i] = s.SourceY[((subY + (i / 4)) * s.YWidth) + subX + (i % 4)] - pred[i];
                }

                var coeff = s.Coeff;
                Av1ForwardWht.Forward4x4(residual.AsSpan(0, 16), coeff.AsSpan(0, 16));
                var levels = s.Levels;
                Av1ForwardQuantizer.Quantize(coeff, levels, 4, s.BaseQIdx);

                for (int i = 0; i < 4; i++)
                {
                    Array.Copy(pred, i * 4, s.ReconY, ((subY + i) * s.YWidth) + subX, 4);
                }

                // blockSize: the coding block's real pixel size -- >= 8 for every leaf bigger than the 4x4
                // floor (n > 1 sub-blocks, so the all_zero context can't take the transform-equals-block
                // shortcut, see WriteCoeffs's remarks), but exactly 4 for a genuine 4x4 leaf (n == 1), where
                // it deliberately *does* take that shortcut -- WriteCoeffs's own blockSize == size check
                // handles both correctly without this call site needing to special-case n == 1 itself.
                if (s.Phase != Av1EncodePhase.Decide)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.Symbols, s.Cdf, levels, 4, ptype: 0, subC, subR, s.YCoeffCtx, writeLumaTxType: null, blockSize: blockSize);
                }

                // Two-pass tile-encoder architecture, Stage 1b -- see TileState.DecideYCoeffCtx's own
                // remarks. Same arguments as the real commit just above.
                if (s.Phase != Av1EncodePhase.Emit)
                {
                    Av1CoefficientWriter.WriteCoeffs(s.DecideCommitSink, s.DecideCdf, levels, 4, ptype: 0, subC, subR, s.DecideYCoeffCtx, writeLumaTxType: null, blockSize: blockSize);
                }

                Av1LocalReconstructor.Reconstruct(s.ReconY, s.YWidth, subX, subY, 4, levels, s.BaseQIdx, s.ReconDequant, s.ReconResidual, lossless: true);
                SetBlockDecoded(s, 0, subBlockMiRow, subBlockMiCol, true);
            }
        }
    }
}
