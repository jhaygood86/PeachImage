using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes <c>frame_header_obu()</c> / <c>uncompressed_header()</c> (spec §5.9.1-§5.9.2) for the fixed v1
/// encoder configuration -- the write-side mirror of <see cref="Av1FrameHeader"/>, restricted to exactly
/// the bits that configuration requires. Because the sequence header always disables superres/CDEF/loop
/// restoration (see <see cref="Av1SequenceHeaderWriter"/>), the parser's own short-circuit logic
/// (<c>seq.EnableCdef &amp;&amp; ...</c>-style conditions) means several whole per-frame syntax elements
/// are never read at all -- this writer mirrors that by simply never writing them, rather than writing
/// "off" values for fields that don't exist in the bitstream. In-loop filters are signalled off via
/// zero-valued (not absent) fields where the non-lossless syntax still requires them (loop filter levels);
/// at coded-lossless (see <see cref="Write"/>'s <c>lossless</c> parameter) those same fields become entirely
/// absent from the bitstream instead, per AV1's own <c>codedLossless</c> short-circuit -- writing zero bits
/// there would desync a real decoder rather than merely being redundant.
/// </summary>
internal static class Av1FrameHeaderWriter
{
    /// <summary>
    /// Writes the frame header and returns the resolved <see cref="Av1FrameHeader"/> (the same type
    /// <see cref="Av1TileDecoder"/> consumes) plus the <see cref="Av1TileInfo"/> written alongside it.
    /// </summary>
    /// <param name="writer">The bit writer to append the header to.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="monoChrome">Whether this frame is monochrome (matches the sequence header's <c>mono_chrome</c>).</param>
    /// <param name="baseQIdx">
    /// The base quantizer index, 0-255. Must be exactly 0 when <paramref name="lossless"/> is
    /// <see langword="true"/> (AV1's coded-lossless trigger -- forces 4x4-only transforms and skips
    /// in-loop-filter/<c>delta_q_present</c> signalling entirely, both mirrored below) and 1-255 otherwise
    /// (0 would silently trigger coded-lossless without <paramref name="lossless"/> also being set, desyncing
    /// this writer from what it claims to be encoding). The quality-to-quantizer mapping is responsible for
    /// keeping non-lossless callers in the 1-255 range.
    /// </param>
    /// <param name="lossless">
    /// When <see langword="true"/>, writes AV1's coded-lossless configuration: <paramref name="baseQIdx"/>
    /// must be 0, <c>tx_mode</c> is implicitly <see cref="Av1FrameHeader.OnlyTx4x4"/> (no <c>tx_mode_select</c>
    /// bit is written -- the decoder never reads one either, see <see cref="Av1FrameHeader"/>'s own
    /// <c>codedLossless</c> branch), and neither <c>delta_q_present</c> nor any <c>loop_filter_params()</c>
    /// bits are written (both are unconditionally absent from the bitstream at coded-lossless, not merely
    /// zero-valued, again mirroring the decoder's short-circuit).
    /// </param>
    /// <param name="loopFilterLevel0">
    /// <c>loop_filter_level[0]</c> (Y, vertical edges). Real aomenc genuinely searches this independently of
    /// <paramref name="loopFilterLevel1"/> at this project's own tested settings (confirmed directly from
    /// libaom source, <c>av1/encoder/picklpf.c</c>'s own real <c>av1_pick_filter_level</c>: four separate
    /// <c>search_filter_level</c> calls -- Y-vertical, Y-horizontal, U, V -- not one shared value), which is
    /// why <see cref="Av1InLoopFilterSearch"/>'s own search now produces four independent levels instead of
    /// one. 0 (the default) reproduces this method's previous always-off behavior exactly -- all four levels
    /// are silently ignored (never written) when <paramref name="lossless"/> is <see langword="true"/>, since
    /// <c>loop_filter_params()</c> is entirely absent from the bitstream at coded-lossless regardless of what
    /// value would otherwise have been chosen (see <see cref="Write"/>'s own <paramref name="lossless"/>
    /// remarks).
    /// </param>
    /// <param name="loopFilterLevel1"><c>loop_filter_level[1]</c> (Y, horizontal edges) -- see <paramref name="loopFilterLevel0"/>'s remarks.</param>
    /// <param name="loopFilterLevelU"><c>loop_filter_level[2]</c> (U) -- see <paramref name="loopFilterLevel0"/>'s remarks. Only ever written when this frame has chroma and (<paramref name="loopFilterLevel0"/> or <paramref name="loopFilterLevel1"/>) is nonzero (spec's own real <c>loop_filter_params()</c> condition, not simply "chroma present").</param>
    /// <param name="loopFilterLevelV"><c>loop_filter_level[3]</c> (V) -- see <paramref name="loopFilterLevelU"/>'s remarks.</param>
    /// <param name="enableCdef">
    /// Must match whatever <c>enable_cdef</c> value the sequence header covering this frame actually wrote
    /// (<see cref="Av1SequenceHeaderWriter.Write"/>'s own <c>enableCdef</c> parameter) -- <c>cdef_params()</c>
    /// is only present in the bitstream when the sequence header enabled it (spec's <c>ParseCdefParams</c>
    /// short-circuits on <c>!seq.EnableCdef</c> regardless of what this method would otherwise write), so
    /// this can't be derived from <paramref name="lossless"/> alone the way <paramref name="loopFilterLevel0"/>'s
    /// gating can: a caller is free to build a sequence header with CDEF disabled even for a non-lossless
    /// frame (every default/positional-only call site does exactly that), and writing <c>cdef_params()</c>
    /// bits in that case would desync the very next syntax element a real decoder reads. Defaults to
    /// <see langword="false"/>, matching this method's pre-CDEF-support behavior for every caller that
    /// doesn't pass it explicitly.
    /// </param>
    /// <param name="cdef">
    /// CDEF (spec §7.15) strength choice from <see cref="Av1CdefSearch"/>'s RD search, or
    /// <see cref="Av1CdefChoice.Off"/> (the default) to signal CDEF as a no-op strength-0/0 combo. This
    /// encoder always writes exactly one strength combo (<c>cdef_bits = 0</c>) rather than the up to 8 a real
    /// per-64x64-unit-adaptive encoder would use -- see <see cref="Av1CdefSearch"/>'s remarks. Ignored
    /// (never written) whenever <paramref name="enableCdef"/> is <see langword="false"/> or
    /// <paramref name="lossless"/> is <see langword="true"/>.
    /// </param>
    /// <param name="allowScreenContentTools">
    /// <c>allow_screen_content_tools</c> -- real, content-based decision
    /// (<see cref="Av1ScreenContentEstimator"/>) for whether palette mode is structurally present in this
    /// frame's bitstream. The one caller only ever computes a real estimate when <paramref name="lossless"/>
    /// is <see langword="true"/> (this encoder's palette support is lossless-only); defaults to
    /// <see langword="false"/>, matching this method's pre-estimator behavior.
    /// </param>
    /// <param name="allowIntrabc">
    /// <c>allow_intrabc</c> -- a real decision computed separately from
    /// <paramref name="allowScreenContentTools"/> (<c>Av1TileEncoder.TileState.AllowIntrabc</c>'s remarks
    /// explain why these genuinely decouple in practice). Only meaningful (read at all) when
    /// <paramref name="allowScreenContentTools"/> is also <see langword="true"/>.
    /// </param>
    /// <param name="reducedTxSet">
    /// <c>reduced_tx_set</c> -- spec-provably inert whenever <paramref name="lossless"/> (tx_type is never
    /// read from the bitstream at coded-lossless at all). The one caller now passes <see langword="false"/>
    /// unconditionally, matching real aomenc's own observed default (confirmed directly from libaom source,
    /// <c>av1/encoder/encodeframe.c</c>'s own unconditional <c>features-&gt;reduced_tx_set_used =
    /// oxcf-&gt;txfm_cfg.reduced_tx_type_set</c> pass-through of a config value that itself defaults to 0 --
    /// a static default, not a lossless-dependent or per-frame decision). Defaults to <see langword="true"/>
    /// here only for this method's own pre-existing signature compatibility; the real, load-bearing value
    /// always comes from the caller. See <c>Av1TileEncoder.TileState.ReducedTxSet</c>'s own remarks for the
    /// matching non-lossless tx-type search/write side of this same change.
    /// </param>
    public static Av1FrameHeader Write(Av1BitWriter writer, int width, int height, bool monoChrome, int baseQIdx, bool lossless = false, int loopFilterLevel0 = 0, bool enableCdef = false, Av1CdefChoice? cdef = null, bool allowScreenContentTools = false, bool allowIntrabc = false, bool reducedTxSet = true, int loopFilterLevel1 = 0, int loopFilterLevelU = 0, int loopFilterLevelV = 0)
    {
        var cdefChoice = cdef ?? Av1CdefChoice.Off;
        if (lossless)
        {
            if (baseQIdx != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baseQIdx), baseQIdx, "base_q_idx must be exactly 0 when lossless is true.");
            }
        }
        else if (baseQIdx is <= 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQIdx), baseQIdx, "base_q_idx must be in [1, 255] when lossless is false -- 0 would silently trigger AV1's coded-lossless path.");
        }

        // allow_screen_content_tools: the single frame-level gate that lets a decoder's palette_mode_info()/
        // use_intrabc read anything at all. Caller (Av1FrameEncoder.Encode) computes this from real content
        // analysis (Av1ScreenContentEstimator, a faithful port of libaom's own estimate_screen_content) rather
        // than the old "always on whenever lossless" shortcut -- confirmed via this project's own libaom
        // byte-exact comparison harness that real encoders make this a genuine per-frame content decision,
        // not an unconditional lossless-implies-on rule (a 128x128 solid-color test frame: aomenc leaves both
        // this and allow_intrabc off entirely). Always false when !lossless (the one caller never computes a
        // real estimate otherwise), matching this encoder's palette/IntraBC support being lossless-only.
        writer.WriteFlag(false); // disable_cdf_update -- CDF adaptation is active during encode
        writer.WriteFlag(allowScreenContentTools); // allow_screen_content_tools

        if (allowScreenContentTools)
        {
            // force_integer_mv's value is irrelevant: FrameIsIntra (always true for AVIF) unconditionally
            // forces it to 1 afterward regardless of what's read here (see Av1FrameHeader.Parse's own
            // remarks) -- written true for clarity, but the decoder never actually branches on it.
            writer.WriteFlag(true); // force_integer_mv
        }

        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);

        // superres_params(): seq.EnableSuperres == false short-circuits use_superres -- no bit read/written.
        writer.WriteFlag(false); // render_and_frame_size_different -- render size == frame size

        if (allowScreenContentTools)
        {
            // allow_intrabc is only read when allowScreenContentTools && upscaledWidth == frameWidth (always
            // true here -- this encoder never uses superres). A real, separately-computed decision
            // (Av1ScreenContentEstimator), not automatically true whenever allowScreenContentTools is --
            // confirmed via this project's own libaom byte-exact comparison harness that these genuinely
            // decouple in practice (a checkerboard test frame: real encoders enable palette but leave IntraBC
            // off, since its own stricter high-variance threshold isn't met).
            writer.WriteFlag(allowIntrabc);
        }

        var tileInfo = Av1TileInfoWriter.Write(writer, miCols, miRows, use128x128Superblock: lossless);

        writer.WriteBits((uint)baseQIdx, 8);
        writer.WriteFlag(false); // delta_q_y_dc coded flag -> DeltaQYDc = 0

        int numPlanes = monoChrome ? 1 : 3;
        if (numPlanes > 1)
        {
            // seq.SeparateUvDeltaQ == false short-circuits diff_uv_delta -- no bit read/written.
            writer.WriteFlag(false); // delta_q_u_dc coded flag -> DeltaQUDc = 0
            writer.WriteFlag(false); // delta_q_u_ac coded flag -> DeltaQUAc = 0
        }

        writer.WriteFlag(false); // using_qmatrix
        writer.WriteFlag(false); // segmentation_enabled

        // delta_q_present is read as `baseQIdx > 0 && reader.ReadFlag()` -- at baseQIdx == 0 (lossless) the
        // bit is short-circuited away entirely, not merely defaulted to false, so it must not be written.
        // codedLossless is otherwise always false here since no segmentation is ever signalled either.
        if (!lossless)
        {
            writer.WriteFlag(false); // delta_q_present
        }

        // delta_lf_present is only read when delta_q_present -- not reached here either way.

        // loop_filter_params() is entirely absent from the bitstream when codedLossless OR allowIntrabc (see
        // Av1FrameHeader.ParseLoopFilterParams's `codedLossless || allowIntrabc` short-circuit) -- not just
        // zero-valued, so these bits must be skipped, not merely written as zero, in either case. Real AV1
        // forbids in-loop filtering on any frame using IntraBC regardless of losslessness (an IntraBC frame's
        // own reconstructed samples must stay exactly what the copy-prediction produced, for later same-frame
        // IntraBC references to remain valid copy sources) -- this was previously written as `if (!lossless)`
        // alone, correct only because allowIntrabc was, at the time, never true for a non-lossless frame; once
        // that's no longer guaranteed (see Av1TileEncoder's own exact-match-only non-lossless IntraBC support)
        // omitting `&& !allowIntrabc` here would write real loop-filter-level bits a real decoder's own parser
        // never reads at all, permanently desyncing every entropy-coded bit for the rest of the frame.
        int writtenLevel0 = 0, writtenLevel1 = 0, writtenLevel2 = 0, writtenLevel3 = 0;
        if (!lossless && !allowIntrabc)
        {
            (writtenLevel0, writtenLevel1, writtenLevel2, writtenLevel3) = WriteLoopFilterParams(writer, loopFilterLevel0, loopFilterLevel1, loopFilterLevelU, loopFilterLevelV, numPlanes);
        }

        // cdef_params() is read whenever seq.EnableCdef && !codedLossless && !allowIntrabc (see
        // Av1FrameHeader.ParseCdefParams) -- the same real "no post-filtering on an IntraBC frame" spec rule
        // loop_filter_params() follows above, and the same fix applies here for the identical reason: this
        // used to rely on allowIntrabc never being true for a non-lossless frame, which is no longer assumed.
        bool cdefParamsPresent = !lossless && !allowIntrabc && enableCdef;
        var writtenCdef = cdefParamsPresent ? cdefChoice : Av1CdefChoice.Off;
        if (cdefParamsPresent)
        {
            WriteCdefParams(writer, cdefChoice);
        }

        // lr_params(): seq.EnableRestoration == false short-circuits it entirely regardless of losslessness
        // -- no bits read/written (loop restoration isn't implemented by this encoder yet).

        // tx_mode_select is only read when !codedLossless (tx_mode is otherwise implicitly OnlyTx4x4) --
        // see Av1FrameHeader's own codedLossless branch. Project plan Phase 4's own transform-size RDO:
        // real aomenc always signals tx_mode_select for non-lossless all-intra content (confirmed directly
        // from libaom source, av1/encoder/rdopt_utils.h's own select_tx_mode: TX_MODE_SELECT unless
        // coded_lossless or tx_size_search_method == USE_LARGESTALL, and this project's own tested settings
        // never hit that USE_LARGESTALL case -- see EncodeLeaf's own non-lossless tx-size search remarks
        // for the real per-leaf search this bit now genuinely needs) -- so this is unconditionally true
        // whenever !lossless, not a separate knob.
        bool txModeSelect = !lossless;
        if (!lossless)
        {
            writer.WriteFlag(txModeSelect); // tx_mode_select
        }

        // reduced_tx_set is spec-provably inert for lossless: tx_type is never read from the bitstream at
        // coded-lossless at all (spec forces WHT_WHT unconditionally, see Av1TileDecoder's own lossless
        // branch), so this bit can never affect how any symbol is interpreted there -- confirmed via this
        // project's own libaom byte-exact comparison harness that real encoders signal it false for lossless
        // (both a solid-color and a checkerboard test frame). Now false for non-lossless too, matching real
        // aomenc's own actual default (see this parameter's own remarks) -- the encoder's own tx-type
        // search/write now consults TileState.ReducedTxSet at every relevant call site (see its own remarks),
        // so this is no longer the "flipping it alone would desync the decoder" risk it used to be.
        writer.WriteFlag(reducedTxSet); // reduced_tx_set
        // film_grain_params_present == false short-circuits the apply_grain bit -- no bit read/written.

        // byte_alignment() (spec §5.3.5, plain zero-bit padding to the next byte boundary -- writes NOTHING
        // when already aligned), not trailing_bits() (§5.3.4, which always writes at least one bit, a full
        // spurious extra 0x80 byte when already aligned). This frame header is always embedded in a combined
        // OBU_FRAME (Av1FrameEncoder.Encode: frame_header_obu() + byte_alignment() + tile_group_obu(sz), all
        // in one OBU, matching real encoders' own --obu output) rather than written as its own standalone
        // OBU_FRAME_HEADER -- per spec's frame_obu(), the step between the header and the tile group data is
        // explicitly byte_alignment(), not trailing_bits() (that generic OBU-wrapper-level call only applies
        // to a standalone OBU_FRAME_HEADER/OBU_REDUNDANT_FRAME_HEADER, which this encoder no longer emits).
        // Using trailing_bits() here after switching to the combined OBU_FRAME form silently wrote one
        // spurious extra byte whenever the header happened to land already byte-aligned, shifting the tile
        // group's real start by a full byte and corrupting every entropy-coded bit read after it -- exactly
        // the kind of divergence real interop depends on getting right, not just this project's own decoder
        // (which -- like the header's own Av1FrameHeader.Parse -- reads a plain byte_alignment() here too).
        writer.ByteAlign();

        return new Av1FrameHeader
        {
            FrameWidth = width,
            FrameHeight = height,
            UpscaledWidth = width,
            RenderWidth = width,
            RenderHeight = height,
            MiCols = miCols,
            MiRows = miRows,
            AllowScreenContentTools = allowScreenContentTools,
            AllowIntrabc = allowIntrabc,
            BaseQIdx = baseQIdx,
            DeltaQYDc = 0,
            DeltaQUDc = 0,
            DeltaQUAc = 0,
            DeltaQVDc = 0,
            DeltaQVAc = 0,
            UsingQMatrix = false,
            QmY = 0,
            QmU = 0,
            QmV = 0,
            Segmentation = new Av1SegmentationParams
            {
                Enabled = false,
                FeatureEnabled = new bool[Av1SegmentationParams.MaxSegments, Av1SegmentationParams.SegLvlMax],
                FeatureData = new int[Av1SegmentationParams.MaxSegments, Av1SegmentationParams.SegLvlMax],
                SegIdPreSkip = false,
                LastActiveSegId = 0,
            },
            DeltaQPresent = false,
            DeltaQRes = 0,
            DeltaLfPresent = false,
            DeltaLfRes = 0,
            DeltaLfMulti = false,
            CodedLossless = lossless,

            // AllLossless additionally requires frameWidth == upscaledWidth -- always true here, since this
            // encoder never uses superres (see the sequence header's enable_superres == false).
            AllLossless = lossless,
            LoopFilter = new Av1LoopFilterParams
            {
                Level = [writtenLevel0, writtenLevel1, writtenLevel2, writtenLevel3],
                Sharpness = 0,
                DeltaEnabled = false,
                RefDeltas = [1, 0, 0, 0, -1, 0, -1, -1],
                ModeDeltas = [0, 0],
            },
            Cdef = new Av1CdefParams
            {
                Damping = writtenCdef.Damping,
                Bits = 0,
                YPriStrength = [writtenCdef.YPriStrength],
                YSecStrength = [writtenCdef.YSecStrength],
                UvPriStrength = [writtenCdef.UvPriStrength],
                UvSecStrength = [writtenCdef.UvSecStrength],
            },
            LoopRestoration = new Av1LoopRestorationParams
            {
                FrameRestorationType = [Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone],
                UsesLr = false,
                UnitSize = [0, 0, 0],
            },
            TxMode = lossless ? Av1FrameHeader.OnlyTx4x4 : (txModeSelect ? Av1FrameHeader.TxModeSelect : Av1FrameHeader.TxModeLargest),
            ReducedTxSet = reducedTxSet,
            TileInfo = tileInfo,
            DisableCdfUpdate = false,
        };
    }

    /// <summary>
    /// <c>loop_filter_params()</c> (spec §5.9.11) write-side, now with four genuinely independent levels
    /// (project plan Phase 4's own deblock-per-plane RDO -- see <see cref="Av1InLoopFilterSearch"/>'s own
    /// remarks for why this matches real aomenc's own default behavior). The real spec condition for
    /// writing <paramref name="level2"/>/<paramref name="level3"/> is <c>NumPlanes &gt; 1 &amp;&amp;
    /// (loop_filter_level[0] || loop_filter_level[1])</c> -- a disjunction over the two <em>luma</em> levels,
    /// not "any level is nonzero" -- since with independent levels there's no single shared value left to
    /// test.
    /// </summary>
    private static (int Level0, int Level1, int Level2, int Level3) WriteLoopFilterParams(Av1BitWriter writer, int level0, int level1, int level2, int level3, int numPlanes)
    {
        writer.WriteBits((uint)level0, 6); // loop_filter_level[0]
        writer.WriteBits((uint)level1, 6); // loop_filter_level[1]

        int writtenLevel2 = 0, writtenLevel3 = 0;
        if (numPlanes > 1 && (level0 != 0 || level1 != 0))
        {
            writer.WriteBits((uint)level2, 6); // loop_filter_level[2]
            writer.WriteBits((uint)level3, 6); // loop_filter_level[3]
            writtenLevel2 = level2;
            writtenLevel3 = level3;
        }

        writer.WriteBits(0, 3); // loop_filter_sharpness
        writer.WriteFlag(false); // loop_filter_delta_enabled
        return (level0, level1, writtenLevel2, writtenLevel3);
    }

    /// <summary>
    /// <c>cdef_params()</c> (spec §5.9.19) write-side, restricted to this encoder's v1 scope: always
    /// <c>cdef_bits = 0</c> (exactly one strength combo, applied to every 64x64 unit in the frame -- a real
    /// per-unit-adaptive encoder would search and signal up to <c>CDEF_MAX_STRENGTHS</c> = 8), and one shared
    /// (<paramref name="cdef"/>'s <c>YPriStrength</c>/<c>UvPriStrength</c>) primary and
    /// (<c>YSecStrength</c>/<c>UvSecStrength</c>) secondary strength rather than separately tuned per plane
    /// beyond that. See <see cref="Av1CdefSearch"/> for how <paramref name="cdef"/> is chosen.
    ///
    /// <para>The secondary-strength field only has 4 legal values from its 2-bit code (spec's remap: raw code
    /// 3 means strength 4, not 3 -- strength 3 is simply unreachable) -- <see cref="Av1CdefChoice"/>'s own
    /// remarks document why its constructor only accepts <c>{0, 1, 2, 4}</c> for the secondary strengths, so
    /// this method can invert that remap unconditionally (<c>value == 4 ? 3 : value</c>) without a validity
    /// check here duplicating that one.</para>
    /// </summary>
    private static void WriteCdefParams(Av1BitWriter writer, Av1CdefChoice cdef)
    {
        writer.WriteBits((uint)(cdef.Damping - 3), 2); // cdef_damping_minus_3
        writer.WriteBits(0, 2); // cdef_bits -> exactly 1 strength combo

        writer.WriteBits((uint)cdef.YPriStrength, 4); // cdef_y_pri_strength[0]
        writer.WriteBits((uint)(cdef.YSecStrength == 4 ? 3 : cdef.YSecStrength), 2); // cdef_y_sec_strength[0]
        writer.WriteBits((uint)cdef.UvPriStrength, 4); // cdef_uv_pri_strength[0]
        writer.WriteBits((uint)(cdef.UvSecStrength == 4 ? 3 : cdef.UvSecStrength), 2); // cdef_uv_sec_strength[0]
    }
}

/// <summary>
/// One AV1 CDEF (spec §7.15) strength combo -- this encoder's v1 scope always signals exactly one such combo
/// per frame (<c>cdef_bits = 0</c>, see <see cref="Av1FrameHeaderWriter.WriteCdefParams"/>), applied
/// uniformly to every 64x64 unit, rather than the up to 8 a real per-unit-adaptive encoder would choose among.
/// </summary>
/// <param name="Damping">Spec's <c>cdef_damping_minus_3 + 3</c>, so 3-6.</param>
/// <param name="YPriStrength">0-15 (spec's 4-bit <c>cdef_y_pri_strength</c> field, written directly).</param>
/// <param name="YSecStrength">
/// One of <c>{0, 1, 2, 4}</c> -- the only 4 values spec's 2-bit secondary-strength field can represent (raw
/// code 3 remaps to strength 4 on read, per spec §5.9.19; strength 3 itself is unreachable). Not validated
/// here: <see cref="Av1CdefSearch"/>'s own candidate list is the only caller and never proposes 3.
/// </param>
/// <param name="UvPriStrength">0-15, chroma's primary strength.</param>
/// <param name="UvSecStrength">One of <c>{0, 1, 2, 4}</c>, chroma's secondary strength -- see <see cref="YSecStrength"/>'s remarks.</param>
internal readonly record struct Av1CdefChoice(int Damping, int YPriStrength, int YSecStrength, int UvPriStrength, int UvSecStrength)
{
    /// <summary>A strength-0/0 combo -- CDEF filters nothing (spec: strength 0 disables both the primary and secondary filter passes), so this is a true "off" equivalent to this encoder's previous always-disabled behavior, just with <c>cdef_params()</c> still present in the bitstream (a few extra header bits -- see <see cref="Av1FrameHeaderWriter.WriteCdefParams"/>'s remarks).</summary>
    public static readonly Av1CdefChoice Off = new(Damping: 3, YPriStrength: 0, YSecStrength: 0, UvPriStrength: 0, UvSecStrength: 0);
}
