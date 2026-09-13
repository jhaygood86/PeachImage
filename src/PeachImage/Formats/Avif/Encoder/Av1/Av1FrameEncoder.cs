namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Top-level AV1 encode entry point: converts a source image to YUV, pads it (edge-replicated -- see
/// <see cref="Av1TileEncoder"/>'s remarks on why this is required for its simplified, edge-case-free
/// superblock traversal) to a coded canvas that's a multiple of the actual superblock size -- 128 pixels for
/// lossless (128x128 superblocks), 64 for everything else -- writes the sequence/frame headers and the single tile,
/// and assembles the final OBU byte stream (temporal delimiter + sequence header + frame header + tile
/// group). The true (unpadded) source dimensions are carried separately in the returned
/// <see cref="Av1EncodedFrame"/> for the container writer's <c>ispe</c> box -- the AVIF container crops the
/// padded coded frame back down to the true size at decode time (see <c>AvifDecoder.Decode</c>'s use of the
/// container's own width/height, not the AV1 bitstream's <c>RenderWidth</c>/<c>RenderHeight</c>).
/// </summary>
internal static class Av1FrameEncoder
{
    /// <summary>
    /// Encodes an opaque 8-bit RGB24 (or, if <paramref name="monoChrome"/>, Gray8) source into a full AV1
    /// OBU byte stream. When <paramref name="lossless"/> is <see langword="true"/>, <paramref name="quality"/>
    /// is ignored entirely and every block is coded via AV1's lossless Walsh-Hadamard path instead of DCT_DCT
    /// quantization (<c>base_q_idx</c> forced to 0, AV1's coded-lossless trigger, rather than derived from
    /// <paramref name="quality"/>).
    ///
    /// <para><paramref name="colorPrimaries"/>/<paramref name="transferCharacteristics"/>/
    /// <paramref name="chromaSamplePosition"/> are pure CICP (H.273) bitstream tagging -- see
    /// <see cref="Av1SequenceHeaderWriter.ColorPrimaries"/>'s own remarks for why these are safe to vary
    /// independently of actual pixel conversion, which always stays BT.601 (non-lossless) or the identity
    /// matrix (lossless).</para>
    ///
    /// <para><paramref name="enableScreenContentTools"/>/<paramref name="enableIntrabc"/> are a real,
    /// caller-settable override of the internal content-based auto-detection (<see cref="Av1ScreenContentEstimator"/>),
    /// mirroring real aomenc's own <c>--enable-palette</c>/<c>--enable-intrabc</c> -- both default to
    /// <see langword="true"/> (auto-detect, unchanged from this method's own pre-existing behavior). Setting
    /// <paramref name="enableScreenContentTools"/> to <see langword="false"/> disables both palette and
    /// IntraBC together (this encoder's own screen-content-tools frame flag structurally gates both --
    /// spec requires it for <c>allow_intrabc</c> to be read at all, and it's palette's own real, direct gate
    /// too), skipping the estimator and any real trial-encode cost entirely. <paramref name="enableIntrabc"/>
    /// set to <see langword="false"/> disables only IntraBC specifically, leaving palette's own real
    /// content-based decision untouched -- there is no equivalent independent "palette only" override in the
    /// other direction, since this encoder's own palette search has no gate separate from the shared
    /// screen-content-tools frame flag IntraBC also needs.</para>
    /// </summary>
    public static Av1EncodedFrame Encode(ReadOnlySpan<byte> pixels, int width, int height, bool monoChrome, int quality, bool lossless = false, int effort = 2, Action<Av1BlockDecisionRecord>? onLeafCommitted = null, int colorPrimaries = Av1SequenceHeaderWriter.ColorPrimaries, int transferCharacteristics = Av1SequenceHeaderWriter.TransferCharacteristics, int chromaSamplePosition = Av1SequenceHeaderWriter.ChromaSamplePosition, bool enableScreenContentTools = true, bool enableIntrabc = true)
    {
        // Lossless uses 128x128 superblocks (Av1SequenceHeaderWriter/Av1TileEncoder), so the coded canvas
        // must pad to a 128-pixel multiple instead of 64 -- same "every superblock fully in-bounds" reason
        // the 64-pixel padding exists for the non-lossless/64x64-superblock path (see this class's own
        // remarks and Av1TileEncoder's).
        int sbPixels = lossless ? 128 : 64;
        int paddedWidth = ((width + sbPixels - 1) / sbPixels) * sbPixels;
        int paddedHeight = ((height + sbPixels - 1) / sbPixels) * sbPixels;

        // The one gate for genuinely lossless RGB: lossless + real chroma planes means 4:4:4 with an
        // identity color matrix (Av1RgbToYuvIdentityConverter) instead of 4:2:0 BT.601 -- see that class's
        // remarks for why only the identity matrix is exactly invertible. Always false for monoChrome (no
        // chroma planes to subsample either way), so this can never change monoChrome/alpha item output.
        bool chroma444 = lossless && !monoChrome;

        int[] yPlane;
        int[]? uPlane = null;
        int[]? vPlane = null;
        int chromaWidth = 0;
        int chromaHeight = 0;
        int paddedChromaWidth = 0;
        int paddedChromaHeight = 0;

        if (monoChrome)
        {
            int[] y = Av1RgbToYuvConverter.ConvertMonoChrome(pixels, width, height);
            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
        }
        else if (chroma444)
        {
            var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(pixels, width, height);
            chromaWidth = width;
            chromaHeight = height;
            paddedChromaWidth = paddedWidth;
            paddedChromaHeight = paddedHeight;

            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
            uPlane = PadPlane(u, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
            vPlane = PadPlane(v, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
        }
        else
        {
            var (y, u, v, cw, ch) = Av1RgbToYuvConverter.Convert(pixels, width, height);
            chromaWidth = cw;
            chromaHeight = ch;
            paddedChromaWidth = paddedWidth / 2;
            paddedChromaHeight = paddedHeight / 2;

            yPlane = PadPlane(y, width, height, paddedWidth, paddedHeight);
            uPlane = PadPlane(u, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
            vPlane = PadPlane(v, chromaWidth, chromaHeight, paddedChromaWidth, paddedChromaHeight);
        }

        int baseQIdx = lossless ? 0 : Av1ForwardQuantizer.QualityToBaseQIdx(quality);

        // Real, content-based decision (Av1ScreenContentEstimator) for whether palette/IntraBC are worth
        // structurally enabling at all -- only ever computed for lossless (this encoder's only mode that
        // implements either tool). Sampled directly off the padded yPlane at the TRUE (width, height) bounds:
        // padding only ever replicates the last real row/column past those bounds, so this never samples a
        // padding pixel regardless of stride. AllowIntrabc is a real, separately-computed decision from
        // AllowScreenContentTools (spec requires both for IntraBC; a stricter, higher-variance threshold than
        // palette's own -- see Av1ScreenContentEstimator's remarks), never true unless lossless also is.
        //
        // This static heuristic is only ever a coarse, cheap-to-compute PROXY for whether enabling these
        // tools structurally actually pays for itself -- confirmed via this project's own libaom byte-exact
        // comparison harness that real encoders (aomenc's own av1_determine_sc_tools_with_encoding) don't
        // trust an equivalent heuristic either: they run an actual trial sub-encode with and without screen
        // content tools and measure the real result. This project's own equivalent (below) is simpler (a
        // full-fidelity trial via the real encoder, not aomenc's own reduced-fidelity fixed-partition one --
        // this project's stated priority is size over speed, and full fidelity means the trial's own real
        // byte count is exactly what would actually ship, not an approximation of it) but the same idea: the
        // heuristic only decides which candidates are even worth *trying*, never the final answer by itself.
        bool heuristicScreenContentTools = false;
        bool heuristicIntrabc = false;
        if (enableScreenContentTools)
        {
            if (lossless)
            {
                (heuristicScreenContentTools, heuristicIntrabc) = Av1ScreenContentEstimator.Estimate(yPlane, paddedWidth, width, height);
            }
            else
            {
                // Non-lossless screen-content support (project plan Phase 4): both palette (exact-match only,
                // Av1TileEncoder.EncodeLeaf's own paletteSearchEnabled/paletteAllZeroResidual restriction) and
                // IntraBC (exact-match only, that same method's intrabcStructurallyPresent/intrabcApproxCandidate
                // remarks) now reuse this same real, content-based estimator -- unlike the lossless case, which
                // only ever needed one Estimate() call since both tools share identical eligibility there,
                // heuristicScreenContentTools here must reflect EITHER tool wanting it (spec requires
                // allow_screen_content_tools for allow_intrabc to be read at all, and it's palette's own real,
                // direct gate too -- Av1TileDecoder.AllowPalette/IntraFrameModeInfo), not just IntraBC's own
                // stricter threshold.
                (bool estimatorScreenContentTools, heuristicIntrabc) = Av1ScreenContentEstimator.Estimate(yPlane, paddedWidth, width, height);
                heuristicScreenContentTools = estimatorScreenContentTools || heuristicIntrabc;
            }

            if (!enableIntrabc)
            {
                heuristicIntrabc = false;
            }
        }

        // The bitstream's own signaled frame size: TRUE (unpadded) dimensions for lossless -- confirmed via
        // this project's own libaom byte-exact comparison harness that real encoders never round
        // FrameWidth/FrameHeight/MiCols/MiRows up to a superblock multiple (AV1 partitioning natively
        // supports a coding block whose nominal extent overhangs past the frame's true bounds -- see
        // Av1TileEncoder.EncodeTile's own remarks on TrueMiCols/TrueMiRows and the hasRows/hasCols-restricted
        // partition-type legality this requires). Deliberately still the PADDED dimensions for non-lossless:
        // that path's own partition/mode-info commit logic was never updated to handle a real frame-edge
        // overhang the way the lossless path now is, so signaling true dimensions there without also fixing
        // that logic would desync a real decoder rather than just being suboptimal. Passing paddedWidth/
        // paddedHeight here for non-lossless makes TrueMiCols/TrueMiRows equal the already-superblock-aligned
        // MiCols/MiRows exactly, so hasRows/hasCols are always true and the new restriction logic is a
        // guaranteed no-op there -- this one conditional is the only place non-lossless's existing,
        // already-tested behavior needs to be preserved explicitly.
        int headerWidth = lossless ? width : paddedWidth;
        int headerHeight = lossless ? height : paddedHeight;

        // onLeafCommitted's own records are captured PER TRIAL (a local list per call, never the caller's own
        // hook passed straight through to EncodeTile) and only the records belonging to whichever trial's
        // BYTES actually win get kept/forwarded to the caller at the very end. This used to instead run one
        // extra, separate "diagnostic-only" re-encode after the real winning trial was already decided, on
        // the assumption that an identically-configured re-encode is deterministic and would exactly
        // reproduce the winning trial's own real decisions -- confirmed FALSE via a real round-trip
        // investigation (project plan's own "round N+10 through N+14" history): two separate EncodeTile
        // calls with identical settings and identical source pixels measurably diverged partway through
        // (matching for the first several dozen leaf-level symbol writes, then genuinely disagreeing),
        // meaning the discarded "diagnostic" re-encode's own captured records did NOT reliably describe what
        // was actually in the real, shipped bitstream -- exactly the kind of bug this project's own
        // structural decision-log tooling exists to catch, just turned against its own data source instead
        // of against aomenc's.
        //
        // Real root cause, found and fixed this round: reconY/U/V used to be allocated ONCE, outside this
        // closure, and shared (mutated in place) across every trial below -- IntraBC's own copy-source search
        // (FindIntrabcMatch/FindApproximateIntrabcMatch) reads ReconY/U/V directly for causality/match-finding,
        // and a leaf whose own current trial hadn't actually reconstructed a given position yet could still
        // read whatever an EARLIER, DIFFERENT trial's own reconstruction had left behind there (screen-
        // content-tools/IntraBC on vs. off trials don't necessarily commit every leaf in the same shape or
        // order, so "this trial's own raster order already passed this position" and "this trial's own
        // reconstruction actually wrote real content there" are not the same guarantee once a stale array is
        // shared). That let a real IntraBC candidate get chosen based on pixel data that wasn't actually part
        // of ITS OWN trial's real, self-consistent reconstruction -- reproducing byte-for-byte (a valid, self-
        // consistent bitstream by construction, since WriteMv only ever writes real diff bits against a real
        // predictor) but decoding to a genuinely different MV/pixel content than what the encoder's own
        // decision was actually based on, a real encoder/decoder desync confirmed via direct instrumented
        // encoder-side/decoder-side MV tracing (temporary, removed after use) on a `checkerboard 256x256`
        // repro. Fixed by giving every trial its own fresh, zero-initialized reconY/U/V, allocated here
        // (trivially cheap relative to a real full-image encode) instead of sharing one mutable set across
        // trials -- each trial is now fully self-contained, with no possible cross-trial leakage, matching the
        // same "only ever run the hook against the SAME trial whose bytes are kept" principle the comment
        // above already established for onLeafCommitted itself.
        (byte[] Bytes, List<Av1BlockDecisionRecord>? Leaves, int[] ReconY, int[]? ReconU, int[]? ReconV, int LoopFilterLevel0, int LoopFilterLevel1, int LoopFilterLevelU, int LoopFilterLevelV, Av1CdefSearchResult Cdef, Av1LoopRestorationSearchResult Lr) EncodeTileTrial(bool trialScreenContentTools, bool trialIntrabc)
        {
            // Seeded from the real (already edge-replicated, via PadPlane) source planes, not zero-filled --
            // a real gap found and fixed this round: a lossless leaf whose own coding-block node falls
            // entirely beyond the frame's TRUE (unpadded) mi bounds is never visited by EncodeTile's own real
            // per-leaf commit loop at all (TileState's own TrueMiCols/TrueMiRows gate, `r >= s.TrueMiRows ||
            // c >= s.TrueMiCols` -- see its own remarks), so a zero-filled buffer left that true-edge-overhang
            // padding region at literal 0 forever -- wrong real content for any neighbor read (BuildEdges' own
            // edge-replication clamp, SearchUvMode's boundary context, palette's own neighbor search) that
            // legitimately reaches into it. libaom's own real encoder replicates the same edge pixels into its
            // own padded working buffer up front for the identical reason -- not a PeachImage-specific choice.
            //
            // Full-plane, not padding-only: a first attempt seeded only the padding region specifically,
            // reasoning that pre-filling the *interior* (not-yet-committed-by-this-trial) region with real
            // source content -- content no real decoder could ever have, since decoders never see source
            // pixels -- risked breaking encoder/decoder symmetry for any interior neighbor read that
            // legitimately (by real AV1's own progressive-reconstruction order) or illegitimately (a latent
            // bug) reaches a position before this trial's own raster commit order has actually reached it.
            // That reasoning was correct in kind but wrong in scope: direct investigation (this round) found
            // the actual illegitimate reader responsible for round-tripping pixel corruption on a real corpus
            // file (`colors_text_wcg_sdr_rec2020`) was IntraBC's own approximate-match search family
            // (FindApproximateIntrabcMatchViaMotionSearch/ViaMeshSearch, and ConsiderCandidatePixels in
            // FindApproximateIntrabcMatch itself) -- unlike FindIntrabcMatch's own exact-match search, none of
            // these validated IsSourceFootprintWritten before scoring/accepting a candidate (a real,
            // independently-existing gap, not introduced by this fix -- a stale doc comment elsewhere in this
            // file incorrectly claimed this check already existed here). Now fixed at its own real source (see
            // those methods' own remarks) -- every IntraBC candidate is validated as genuinely already
            // committed by this trial before its content is ever read for scoring or acceptance, regardless of
            // what a not-yet-committed position's own Recon content happens to hold. With that real gap closed,
            // full-plane source-seeding is safe: every position a real commit touches gets overwritten with the
            // exact same value regardless (lossless is zero-distortion -- every real leaf-commit write already
            // `Array.Copy`s straight from SourceY/U/V into ReconY/U/V, confirmed by direct source reading, not
            // assumed), and every other real reader of not-yet-committed Recon content is already properly
            // gated by explicit availability flags (availU/availL/haveAboveRight/haveBelowLeft/GetBlockDecoded),
            // never reading raw Recon content for a position those flags mark unavailable.
            var trialReconY = (int[])yPlane.Clone();
            int[]? trialReconU = monoChrome ? null : (int[])uPlane!.Clone();
            int[]? trialReconV = monoChrome ? null : (int[])vPlane!.Clone();

            // Two-pass tile-encoder architecture, Stage 1b-ii (Round N+61): switched from the fused
            // Av1TileEncoder.EncodeTile to the verified-byte-identical EncodeTileTwoPass (Round N+60's own
            // 17-case parity suite, plus a full corpus/lossy-parity run, both green) -- per the plan's own
            // "build as a parallel path, verify, then make it the only one" discipline. EncodeTile itself is
            // kept, unused by any production call site, as the reference implementation Stage 1b-ii's own
            // parity tests still check against.
            //
            // Stage 2 prerequisite (Round N+62): deblocking/CDEF search now runs INSIDE this per-trial call,
            // between Decide and Emit (EncodeTileTwoPassWithInLoopFilters), rather than once after the fact on
            // whichever trial happens to win -- the real ordering a genuine per-64x64-unit adaptive cdef_idx
            // literal (written inside the tile bitstream during Emit) requires. Gated by trialIntrabc, not the
            // eventual winner's own allowIntrabc, exactly mirroring spec's real "no loop_filter_params()/
            // cdef_params() whenever this frame uses IntraBC" rule for THIS trial's own configuration -- real
            // aomenc's own per-trial cost accounting would do the same, and it costs nothing extra to be exact
            // here rather than deferring to a post-hoc approximation.
            int trialLf0 = 0, trialLf1 = 0, trialLfU = 0, trialLfV = 0;
            var trialCdef = Av1CdefSearchResult.Off;
            var trialLr = Av1LoopRestorationSearchResult.Off;
            List<Av1BlockDecisionRecord>? leaves = onLeafCommitted is null ? null : [];
            byte[] bytes = Av1TileEncoder.EncodeTileTwoPassWithInLoopFilters(
                yPlane, paddedWidth, paddedHeight,
                uPlane, vPlane, paddedChromaWidth, paddedChromaHeight,
                trialReconY, trialReconU, trialReconV,
                monoChrome, baseQIdx, lossless, chroma444, effort, trialScreenContentTools, trialIntrabc,
                trueWidth: headerWidth, trueHeight: headerHeight, onLeafCommitted: leaves is null ? null : leaves.Add,
                applyInLoopFilters: () =>
                {
                    if (!lossless && !trialIntrabc)
                    {
                        (trialLf0, trialLf1, trialLfU, trialLfV) = Av1InLoopFilterSearch.SearchAndApply(
                            trialReconY, trialReconU, trialReconV,
                            yPlane, uPlane, vPlane,
                            paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                            monoChrome, baseQIdx);

                        // Real per-unit loop-restoration search (Stage 3) needs BOTH the pre-CDEF (deblocked-
                        // only) and post-CDEF reconstructions -- spec's own stripe-boundary blend (§7.17.1)
                        // reads the pre-CDEF version near each 64-row stripe edge, exactly mirroring
                        // Av1LoopRestoration.Apply's own two-buffer decode-side contract. Snapshotted here,
                        // right after deblocking and before CDEF mutates trialReconY/U/V in place.
                        int[] preCdefY = (int[])trialReconY.Clone();
                        int[]? preCdefU = monoChrome ? null : (int[])trialReconU!.Clone();
                        int[]? preCdefV = monoChrome ? null : (int[])trialReconV!.Clone();

                        // CDEF (spec §7.15) runs after deblocking, per spec's own filter ordering
                        // (Av1FrameDecoder.DecodeTileGroup applies them in exactly this order) -- reconY/U/V
                        // already reflect the chosen deblocking levels at this point. Real per-64x64-unit
                        // adaptive search (Stage 2, Round N+63) -- the returned result flows into
                        // EncodeTileTwoPassWithInLoopFilters's own state.CdefResult before Emit runs, so its
                        // per-unit cdef_idx literals land at the right bitstream position.
                        trialCdef = Av1CdefSearch.SearchAndApply(
                            trialReconY, trialReconU, trialReconV,
                            yPlane, uPlane, vPlane,
                            paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                            monoChrome, baseQIdx, trialLf0, trialLf1, trialLfU, trialLfV);

                        // Loop restoration (spec §7.17) runs last, per spec's own filter ordering -- reconY/U/V
                        // already reflect the chosen deblocking level AND CDEF strengths at this point (Stage 3,
                        // Round N+64). Mutates trialReconY/U/V in place with the real, winning per-unit filter
                        // choices' actual output (via the genuine, stripe-aware decoder filter -- see
                        // Av1LoopRestorationSearch's own class remarks).
                        trialLr = Av1LoopRestorationSearch.SearchAndApply(
                            trialReconY, trialReconU, trialReconV,
                            preCdefY, preCdefU, preCdefV,
                            yPlane, uPlane, vPlane,
                            paddedWidth, paddedHeight, paddedChromaWidth, paddedChromaHeight,
                            monoChrome, baseQIdx);
                    }

                    return (trialCdef, trialLr);
                });
            return (bytes, leaves, trialReconY, trialReconU, trialReconV, trialLf0, trialLf1, trialLfU, trialLfV, trialCdef, trialLr);
        }

        bool allowScreenContentTools = heuristicScreenContentTools;
        bool allowIntrabc = heuristicIntrabc;
        var trial = EncodeTileTrial(allowScreenContentTools, allowIntrabc);
        byte[] tileBytes = trial.Bytes;
        List<Av1BlockDecisionRecord>? winningLeaves = trial.Leaves;
        int[] reconY = trial.ReconY;
        int[]? reconU = trial.ReconU;
        int[]? reconV = trial.ReconV;
        int loopFilterLevel0 = trial.LoopFilterLevel0, loopFilterLevel1 = trial.LoopFilterLevel1, loopFilterLevelU = trial.LoopFilterLevelU, loopFilterLevelV = trial.LoopFilterLevelV;
        var cdefChoice = trial.Cdef;
        var lrChoice = trial.Lr;

        // The real trial itself: the heuristic above only gates whether this ever runs at all (an image the
        // heuristic already ruled out as screen-content-like never pays this extra encode cost) -- once it
        // says yes, don't just trust it. Try genuinely turning the tools back off (and, separately, IntraBC
        // off while keeping palette on) and keep whichever REAL committed byte count is actually smaller.
        // Each trial gets its own fresh reconY/U/V (see EncodeTileTrial's own remarks) -- reconY/U/V here are
        // reassigned to whichever trial's own arrays actually won, in lockstep with tileBytes/winningLeaves;
        // loopFilterLevel*/cdefChoice are reassigned the same way, now that each trial computes its own
        // deblocking/CDEF search in place of a single post-hoc search on the eventual winner (Round N+62).
        // Applies equally to lossless and non-lossless now: both palette (exact-match only for non-lossless)
        // and IntraBC (exact-match only for non-lossless) share this same real trial-comparison structure.
        if (heuristicScreenContentTools)
        {
            var withoutTools = EncodeTileTrial(false, false);
            if (withoutTools.Bytes.Length < tileBytes.Length)
            {
                tileBytes = withoutTools.Bytes;
                winningLeaves = withoutTools.Leaves;
                reconY = withoutTools.ReconY;
                reconU = withoutTools.ReconU;
                reconV = withoutTools.ReconV;
                loopFilterLevel0 = withoutTools.LoopFilterLevel0;
                loopFilterLevel1 = withoutTools.LoopFilterLevel1;
                loopFilterLevelU = withoutTools.LoopFilterLevelU;
                loopFilterLevelV = withoutTools.LoopFilterLevelV;
                cdefChoice = withoutTools.Cdef;
                lrChoice = withoutTools.Lr;
                allowScreenContentTools = false;
                allowIntrabc = false;
            }

            if (heuristicIntrabc)
            {
                var withoutIntrabc = EncodeTileTrial(true, false);
                if (withoutIntrabc.Bytes.Length < tileBytes.Length)
                {
                    tileBytes = withoutIntrabc.Bytes;
                    winningLeaves = withoutIntrabc.Leaves;
                    reconY = withoutIntrabc.ReconY;
                    reconU = withoutIntrabc.ReconU;
                    reconV = withoutIntrabc.ReconV;
                    loopFilterLevel0 = withoutIntrabc.LoopFilterLevel0;
                    loopFilterLevel1 = withoutIntrabc.LoopFilterLevel1;
                    loopFilterLevelU = withoutIntrabc.LoopFilterLevelU;
                    loopFilterLevelV = withoutIntrabc.LoopFilterLevelV;
                    cdefChoice = withoutIntrabc.Cdef;
                    lrChoice = withoutIntrabc.Lr;
                    allowScreenContentTools = true;
                    allowIntrabc = false;
                }
            }
        }

        if (onLeafCommitted is not null && winningLeaves is not null)
        {
            foreach (var leaf in winningLeaves)
            {
                onLeafCommitted(leaf);
            }
        }

        // Deblocking (spec §7.14) and CDEF (spec §7.15) were already searched and applied to reconY/U/V in
        // place, per-trial (Round N+62 -- see EncodeTileTrial's own applyInLoopFilters remarks) rather than
        // once here on whichever trial happened to win; loopFilterLevel*/cdefChoice above already carry the
        // winning trial's own real result. Both are lossy-only (codedLossless's own short-circuit means
        // loop_filter_params()/cdef_params() never reach the bitstream at lossless) and skipped whenever
        // allowIntrabc (real AV1 forbids both on any IntraBC frame, regardless of losslessness).
        // enableRestoration: !lossless, matching enableCdef's own identical rule immediately above -- loop
        // restoration is a lossy-only tool (spec's own coded-lossless short-circuit means lr_params() never
        // reaches the bitstream at lossless either way, Av1FrameHeaderWriter.Write's own lrParamsPresent
        // gate). Previously hardcoded to `lossless` (the reverse) -- harmless before Stage 3 (loop restoration
        // was never implemented on either side of that flag), but a real fix as of Stage 3's own real search.
        byte[] seqHeaderPayload = Av1SequenceHeaderWriter.Write(headerWidth, headerHeight, monoChrome, chroma444, enableCdef: !lossless, use128x128Superblock: lossless, enableRestoration: !lossless, colorPrimaries, transferCharacteristics, chromaSamplePosition);

        var frameHeaderWriter = new Av1BitWriter();
        // `false` unconditionally, matching real aomenc's own observed default -- see
        // Av1TileEncoder.TileState.ReducedTxSet's own remarks. Inert for lossless either way.
        Av1FrameHeaderWriter.Write(frameHeaderWriter, headerWidth, headerHeight, monoChrome, baseQIdx, lossless, loopFilterLevel0, enableCdef: !lossless, cdefChoice.FrameParams, allowScreenContentTools, allowIntrabc, reducedTxSet: false, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV, enableRestoration: !lossless, lrChoice);
        byte[] frameHeaderPayload = frameHeaderWriter.ToArray();

        // A single combined OBU_FRAME (spec's frame_obu(): frame_header_obu() + byte_alignment() +
        // tile_group_obu(sz), all in one OBU payload) rather than separate FrameHeader/TileGroup OBUs --
        // matches what real encoders (confirmed against aomenc's own --obu output) actually emit for a
        // single-tile-group frame, saving one OBU header+size field's worth of bytes. frameHeaderPayload is
        // already byte-aligned (Av1BitWriter.ToArray() rounds up to a whole byte with the same zero-padding
        // ByteAlign() would write), so it can be concatenated directly with tileBytes with no extra
        // alignment step. Av1FrameDecoder.Decode already parses this OBU_FRAME form (needed for real-world
        // AVIF interop regardless of what this encoder itself used to emit), so this needs no decoder change.
        byte[] frameObuPayload = new byte[frameHeaderPayload.Length + tileBytes.Length];
        Array.Copy(frameHeaderPayload, frameObuPayload, frameHeaderPayload.Length);
        Array.Copy(tileBytes, 0, frameObuPayload, frameHeaderPayload.Length, tileBytes.Length);

        var output = new List<byte>();
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.SequenceHeader, seqHeaderPayload);
        Av1ObuWriter.WriteObu(output, Decoding.Av1.Av1ObuType.Frame, frameObuPayload);

        int seqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(headerWidth, headerHeight);
        return new Av1EncodedFrame(output.ToArray(), width, height, monoChrome, chroma444, seqLevelIdx);
    }

    /// <summary>Pads a <paramref name="srcWidth"/> x <paramref name="srcHeight"/> plane up to <paramref name="paddedWidth"/> x <paramref name="paddedHeight"/> by replicating the last real row/column into the padding region.</summary>
    private static int[] PadPlane(int[] src, int srcWidth, int srcHeight, int paddedWidth, int paddedHeight)
    {
        if (srcWidth == paddedWidth && srcHeight == paddedHeight)
        {
            return src;
        }

        var padded = new int[paddedWidth * paddedHeight];
        for (int row = 0; row < paddedHeight; row++)
        {
            int srcRow = Math.Min(row, srcHeight - 1);
            int srcRowBase = srcRow * srcWidth;
            int destRowBase = row * paddedWidth;
            for (int col = 0; col < paddedWidth; col++)
            {
                int srcCol = Math.Min(col, srcWidth - 1);
                padded[destRowBase + col] = src[srcRowBase + srcCol];
            }
        }

        return padded;
    }
}
