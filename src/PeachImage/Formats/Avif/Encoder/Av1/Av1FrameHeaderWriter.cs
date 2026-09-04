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
    /// <param name="loopFilterLevel">
    /// The deblocking filter level (spec's <c>loop_filter_level[0]</c>/<c>[1]</c>, and, when this frame has
    /// chroma, also <c>[2]</c>/<c>[3]</c> -- this encoder searches and signals one shared level across all
    /// four rather than tuning luma/chroma independently, a v1 simplification real encoders typically refine
    /// further) chosen by <see cref="Av1InLoopFilterSearch"/>'s RD search over the encoder's own local
    /// reconstruction. 0 (the default) reproduces this method's previous always-off behavior exactly --
    /// <paramref name="loopFilterLevel"/> is silently ignored (never written) when <paramref name="lossless"/>
    /// is <see langword="true"/>, since <c>loop_filter_params()</c> is entirely absent from the bitstream at
    /// coded-lossless regardless of what value would otherwise have been chosen (see <see cref="Write"/>'s own
    /// <paramref name="lossless"/> remarks).
    /// </param>
    public static Av1FrameHeader Write(Av1BitWriter writer, int width, int height, bool monoChrome, int baseQIdx, bool lossless = false, int loopFilterLevel = 0)
    {
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

        // allow_screen_content_tools is set whenever (and only whenever) this frame is lossless -- palette
        // mode (Av1TileEncoder.TryEncodePalette) is only ever attempted in lossless leaves, and this is the
        // single frame-level gate that lets a decoder's palette_mode_info() read anything at all. Tying it
        // to lossless rather than running a real two-pass "did any leaf actually use palette" check costs
        // at most one wasted header bit (and, per leaf, one always-false has_palette_y/has_palette_uv
        // symbol) on a lossless frame that ends up not using palette anywhere -- negligible next to the
        // savings on the frames this is actually for.
        bool allowScreenContentTools = lossless;

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
            // true here -- this encoder never uses superres). Tied to lossless the same "always on, let
            // per-leaf RDO decide" way allowScreenContentTools itself is (see its own remarks above) --
            // Av1TileEncoder.EncodeLeaf only ever actually uses IntraBC when it finds an exact-pixel-match
            // copy source (see FindIntrabcMatch), so a lossless frame with no such match anywhere just pays
            // the same one-header-bit-plus-per-leaf-use_intrabc-bit cost allowScreenContentTools already
            // does for palette.
            writer.WriteFlag(true); // allow_intrabc
        }

        var tileInfo = Av1TileInfoWriter.Write(writer, miCols, miRows);

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

        // loop_filter_params() is entirely absent from the bitstream when codedLossless (see
        // Av1FrameHeader.ParseLoopFilterParams's `codedLossless || allowIntrabc` short-circuit) -- not just
        // zero-valued, so these bits must be skipped, not merely written as zero, when lossless.
        int writtenLevel0 = 0, writtenLevel1 = 0, writtenLevel2 = 0, writtenLevel3 = 0;
        if (!lossless)
        {
            (writtenLevel0, writtenLevel1, writtenLevel2, writtenLevel3) = WriteLoopFilterParams(writer, loopFilterLevel, numPlanes);
        }

        // cdef_params()/lr_params(): seq.EnableCdef == false / seq.EnableRestoration == false short-circuit
        // both entirely regardless of losslessness -- no bits read/written for either.

        // tx_mode_select is only read when !codedLossless (tx_mode is otherwise implicitly OnlyTx4x4) --
        // see Av1FrameHeader's own codedLossless branch.
        if (!lossless)
        {
            writer.WriteFlag(false); // tx_mode_select -> TX_MODE_LARGEST
        }

        writer.WriteFlag(true); // reduced_tx_set
        // film_grain_params_present == false short-circuits the apply_grain bit -- no bit read/written.

        // trailing_bits() -- mandatory OBU padding (a stop bit, then zero bits out to the byte boundary),
        // matching Av1SequenceHeaderWriter.Write's own call and its remarks on why this is required even
        // when the preceding content already lands on a byte boundary: trailing_bits() always writes at
        // least one bit, so skipping it isn't just "redundant padding" whenever there happens to be zero
        // bits' worth of slack left -- it desyncs a real decoder by exactly the bits this OBU's declared
        // size then falls short of. This previously went unnoticed because every prior configuration
        // (lossy, or any frame with chroma) always had a few bits of incidental padding entropy to absorb
        // the gap; a monochrome lossless frame header is short enough to land exactly byte-aligned with
        // none, which is what exposed the missing call (real decoders overran the OBU trying to read it).
        writer.WriteTrailingBits();

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
            AllowIntrabc = allowScreenContentTools,
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
                Damping = 3,
                Bits = 0,
                YPriStrength = [0],
                YSecStrength = [0],
                UvPriStrength = [0],
                UvSecStrength = [0],
            },
            LoopRestoration = new Av1LoopRestorationParams
            {
                FrameRestorationType = [Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone, Av1LoopRestorationParams.RestoreNone],
                UsesLr = false,
                UnitSize = [0, 0, 0],
            },
            TxMode = lossless ? Av1FrameHeader.OnlyTx4x4 : Av1FrameHeader.TxModeLargest,
            ReducedTxSet = true,
            TileInfo = tileInfo,
            DisableCdfUpdate = false,
        };
    }

    /// <summary>
    /// <c>loop_filter_params()</c> (spec §5.9.11) write-side. <paramref name="level"/> is written identically
    /// into all four <c>loop_filter_level</c> slots (Y-vertical, Y-horizontal, U, V) -- this encoder's RD
    /// search (<see cref="Av1InLoopFilterSearch"/>) picks one shared level rather than tuning luma/chroma
    /// independently, a v1 simplification. <c>loop_filter_delta_enabled</c> is always written
    /// <see langword="false"/>: this encoder's <c>RefDeltas</c>/<c>ModeDeltas</c> never deviate from the spec
    /// defaults <see cref="Write"/> always returns, so there's nothing for delta signaling to express.
    /// Returns the four written levels for <see cref="Write"/>'s returned <see cref="Av1FrameHeader"/> to
    /// mirror exactly (level[2]/level[3] are 0, matching what's actually on the wire, whenever
    /// <paramref name="numPlanes"/> == 1 or <paramref name="level"/> == 0 -- the same
    /// <c>numPlanes &gt; 1 &amp;&amp; (level0 != 0 || level1 != 0)</c> condition <c>Av1FrameHeader.ParseLoopFilterParams</c>
    /// gates that read on).
    /// </summary>
    private static (int Level0, int Level1, int Level2, int Level3) WriteLoopFilterParams(Av1BitWriter writer, int level, int numPlanes)
    {
        writer.WriteBits((uint)level, 6); // loop_filter_level[0]
        writer.WriteBits((uint)level, 6); // loop_filter_level[1]

        int level2 = 0, level3 = 0;
        if (numPlanes > 1 && level != 0)
        {
            writer.WriteBits((uint)level, 6); // loop_filter_level[2]
            writer.WriteBits((uint)level, 6); // loop_filter_level[3]
            level2 = level;
            level3 = level;
        }

        writer.WriteBits(0, 3); // loop_filter_sharpness
        writer.WriteFlag(false); // loop_filter_delta_enabled
        return (level, level, level2, level3);
    }
}
