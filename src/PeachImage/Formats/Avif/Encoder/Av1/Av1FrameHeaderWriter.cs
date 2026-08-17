using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes <c>frame_header_obu()</c> / <c>uncompressed_header()</c> (spec §5.9.1-§5.9.2) for the fixed v1
/// encoder configuration -- the write-side mirror of <see cref="Av1FrameHeader"/>, restricted to exactly
/// the bits that configuration requires. Because the sequence header always disables superres/CDEF/loop
/// restoration (see <see cref="Av1SequenceHeaderWriter"/>), the parser's own short-circuit logic
/// (<c>seq.EnableCdef &amp;&amp; ...</c>-style conditions) means several whole per-frame syntax elements
/// are never read at all -- this writer mirrors that by simply never writing them, rather than writing
/// "off" values for fields that don't exist in the bitstream. In-loop filters are always signalled off via
/// zero-valued (not absent) fields where the syntax does still require them (loop filter levels).
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
    /// <param name="baseQIdx">The base quantizer index, 1-255. Must be non-zero -- 0 would trigger AV1's
    /// coded-lossless path (forces 4x4-only transforms and skips in-loop-filter signalling entirely), which
    /// this v1 encoder does not implement; the quality-to-quantizer mapping (added alongside the forward
    /// transform/quantization layer) is responsible for keeping this true.</param>
    public static Av1FrameHeader Write(Av1BitWriter writer, int width, int height, bool monoChrome, int baseQIdx)
    {
        if (baseQIdx is <= 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQIdx), baseQIdx, "base_q_idx must be in [1, 255] -- 0 would trigger AV1's coded-lossless path, which this encoder does not implement.");
        }

        writer.WriteFlag(false); // disable_cdf_update -- CDF adaptation is active during encode
        writer.WriteFlag(false); // allow_screen_content_tools -- seq_force_integer_mv bit short-circuited off

        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);

        // superres_params(): seq.EnableSuperres == false short-circuits use_superres -- no bit read/written.
        writer.WriteFlag(false); // render_and_frame_size_different -- render size == frame size
        // allow_intrabc: allow_screen_content_tools == false short-circuits it -- no bit read/written.

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

        // codedLossless is always false here since baseQIdx > 0 and no delta-q/segmentation is ever signalled.
        writer.WriteFlag(false); // delta_q_present (baseQIdx > 0, so this bit IS read/written)
        // delta_lf_present is only read when delta_q_present -- not reached here.

        WriteLoopFilterParams(writer);
        // cdef_params()/lr_params(): seq.EnableCdef == false / seq.EnableRestoration == false short-circuit
        // both entirely -- no bits read/written for either.

        writer.WriteFlag(false); // tx_mode_select -> TX_MODE_LARGEST
        writer.WriteFlag(true); // reduced_tx_set
        // film_grain_params_present == false short-circuits the apply_grain bit -- no bit read/written.

        return new Av1FrameHeader
        {
            FrameWidth = width,
            FrameHeight = height,
            UpscaledWidth = width,
            RenderWidth = width,
            RenderHeight = height,
            MiCols = miCols,
            MiRows = miRows,
            AllowScreenContentTools = false,
            AllowIntrabc = false,
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
            CodedLossless = false,
            AllLossless = false,
            LoopFilter = new Av1LoopFilterParams
            {
                Level = [0, 0, 0, 0],
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
            TxMode = Av1FrameHeader.TxModeLargest,
            ReducedTxSet = true,
            TileInfo = tileInfo,
            DisableCdfUpdate = false,
        };
    }

    /// <summary><c>loop_filter_params()</c> (spec §5.9.11) write-side, always signalling every filter level off.</summary>
    private static void WriteLoopFilterParams(Av1BitWriter writer)
    {
        writer.WriteBits(0, 6); // loop_filter_level[0]
        writer.WriteBits(0, 6); // loop_filter_level[1]
        // Both levels are 0, so the level[2]/level[3] read is never reached regardless of plane count.
        writer.WriteBits(0, 3); // loop_filter_sharpness
        writer.WriteFlag(false); // loop_filter_delta_enabled
    }
}
