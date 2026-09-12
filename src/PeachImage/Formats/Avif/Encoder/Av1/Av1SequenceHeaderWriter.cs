using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes a <c>sequence_header_obu()</c> (spec §5.5) in the <c>reduced_still_picture_header == 1</c> form
/// -- the write-side mirror of <see cref="Av1SequenceHeader"/>. This encoder always writes the same fixed
/// v1 configuration (8-bit, no 128x128 superblocks, no filter-intra, superres/CDEF/loop restoration
/// disabled at the sequence level -- not just unused per frame, this is what lets
/// <see cref="Av1FrameHeaderWriter"/> skip their per-frame syntax entirely, since the parser only reads
/// those fields when the corresponding <c>enable_*</c> flag is set -- and no film grain), except profile
/// and chroma subsampling, which vary with <c>chroma444</c> -- see <see cref="Write"/>'s remarks.
/// </summary>
internal static class Av1SequenceHeaderWriter
{
    /// <summary>
    /// The profile this encoder writes whenever it is not signaling 4:4:4 (i.e. whenever <c>chroma444</c> is
    /// <see langword="false"/>): mono/alpha items always, and non-lossless color items. Kept public because
    /// <see cref="Container.AvifContainerWriter"/>'s <c>av1C</c> box must mirror whichever profile the
    /// bitstream actually used.
    /// </summary>
    public const int SeqProfile = 0;

    /// <summary>
    /// The profile this encoder writes whenever <c>chroma444</c> is <see langword="true"/>. AV1's per-profile
    /// conformance constraints (spec Annex A) fix profile 0's <c>subsampling_x</c>/<c>subsampling_y</c> at
    /// 1/1 unconditionally -- the identity-matrix special case in <c>color_config()</c> can *syntactically*
    /// still force them to 0/0 at profile 0 (this repo's own decoder, which doesn't cross-check profile
    /// conformance, accepts that), but a spec-conformant decoder correctly rejects it as non-conformant, since
    /// profile 0 never legally carries 4:4:4 regardless of how the bits got there. Profile 1 requires
    /// <c>mono_chrome = 0</c> (matching <c>chroma444</c> only ever being true for a non-monochrome frame) and
    /// natively allows 4:4:4, so it's the only spec-conformant way to combine 4:4:4 with the identity matrix
    /// -- confirmed empirically: this encoder's own output was rejected by an independent decoder (dav1d) at
    /// profile 0 despite parsing and round-tripping correctly through this repo's own decoder, and passed
    /// once switched to profile 1.
    /// </summary>
    public const int SeqProfileChroma444 = 1;

    /// <summary>
    /// Spec-reserved "no level constraint" value -- kept only for <see cref="Av1InLoopFilterSearch"/>/
    /// <see cref="Av1CdefSearch"/>'s own internal RD-search scratch <see cref="Av1SequenceHeader"/>
    /// construction (a decode-side type those searches reuse purely to drive shared reconstruction code, not
    /// anything that reaches the real bitstream), where the level is inert. The real bitstream now writes a
    /// genuinely computed level (<see cref="ComputeSeqLevelIdx"/>) instead of this constant -- see
    /// <see cref="Write"/>.
    /// </summary>
    public const int SeqLevelIdx0 = 31;

    /// <summary>
    /// AV1 spec Annex A.3's level table (<c>SEQ_LEVEL_2_0</c> through <c>SEQ_LEVEL_6_3</c>), restricted to the
    /// picture-size columns (<c>MaxPicSize</c>, <c>MaxHSize</c>, <c>MaxVSize</c>) -- the level index values
    /// and thresholds themselves are drawn directly from the publicly-specified AV1 Level Definitions table
    /// (not a libaom implementation detail), cross-checked against libaom's own <c>av1_level_defs</c>
    /// (<c>av1/encoder/level.c</c>) for the exact numeric values. Indices 2, 3, 6, 7, 10, 11 are reserved
    /// (spec's own table leaves them undefined; there is no level "2.2"/"2.3" etc.) and are simply skipped by
    /// <see cref="ComputeSeqLevelIdx"/>'s search, matching how libaom's own <c>is_valid_seq_level_idx</c>
    /// treats them.
    /// </summary>
    private static readonly (int LevelIdx, long MaxPicSize, int MaxHSize, int MaxVSize)[] LevelDefs =
    [
        (0, 147_456, 2048, 1152), // 2.0
        (1, 278_784, 2816, 1584), // 2.1
        (4, 665_856, 4352, 2448), // 3.0
        (5, 1_065_024, 5504, 3096), // 3.1
        (8, 2_359_296, 6144, 3456), // 4.0
        (9, 2_359_296, 6144, 3456), // 4.1
        (12, 8_912_896, 8192, 4352), // 5.0
        (13, 8_912_896, 8192, 4352), // 5.1
        (14, 8_912_896, 8192, 4352), // 5.2
        (15, 8_912_896, 8192, 4352), // 5.3
        (16, 35_651_584, 16384, 8704), // 6.0
        (17, 35_651_584, 16384, 8704), // 6.1
        (18, 35_651_584, 16384, 8704), // 6.2
        (19, 35_651_584, 16384, 8704), // 6.3
    ];

    /// <summary>
    /// Picks the lowest AV1 level whose picture-size constraints (<see cref="LevelDefs"/>) this frame fits
    /// within -- the same picture-size-dominated logic real encoders' level selection reduces to for a
    /// single-frame still picture (libaom's own <c>av1_get_seq_level_idx</c>, <c>av1/encoder/level.c</c>, also
    /// checks decode/display-rate and compression-ratio constraints accumulated across an encoded sequence,
    /// but for a genuine one-frame still image those are essentially always satisfied at whatever level the
    /// picture size alone would already require -- rate constraints are trivially met by a single frame, and
    /// the still-picture compression-ratio floor is a generous fixed 0.8x). Falls back to
    /// <see cref="SeqLevelIdx0"/> (spec's own "no level constraint" reserved value) for a frame too large for
    /// even the highest defined level (6.3) -- exactly what real encoders do at that point too.
    /// </summary>
    public static int ComputeSeqLevelIdx(int width, int height)
    {
        long pictureSize = (long)width * height;
        foreach (var level in LevelDefs)
        {
            if (pictureSize <= level.MaxPicSize && width <= level.MaxHSize && height <= level.MaxVSize)
            {
                return level.LevelIdx;
            }
        }

        return SeqLevelIdx0;
    }

    /// <summary>
    /// <see cref="Av1IntraPrediction"/>'s directional/smooth predictors already implement edge filtering
    /// unconditionally, so this is enabled at the sequence level to keep what a real decoder reconstructs
    /// consistent with what this encoder's own local reconstruction (used for RDO and neighbor context)
    /// produces.
    /// </summary>
    public const bool EnableIntraEdgeFilter = true;

    // Informational tagging only -- Av1YuvToRgbConverter (and its forward-direction counterpart,
    // Av1RgbToYuvConverter) only ever consult MatrixCoefficients/ColorRange for actual pixel math, never
    // ColorPrimaries/TransferCharacteristics/ChromaSamplePosition, so those three are cosmetic and safe to
    // expose as caller-settable (see AvifEncoderOptions.ColorPrimaries/TransferCharacteristics/
    // ChromaSamplePosition) -- these defaults are what every caller got before that surface existed.
    // MatrixCoefficients is NOT independently settable: it must match Av1RgbToYuvConverter's forward matrix
    // exactly (this encoder only ever implements the BT.601 and identity matrices below), so it stays a
    // fixed, derived-from-chroma444 value, not a public option.
    public const int ColorPrimaries = 1; // CP_BT_709
    public const int TransferCharacteristics = 13; // TC_SRGB
    public const int MatrixCoefficients = 6; // MC_BT_601 / SMPTE170M
    public const bool ColorRangeFull = true;
    public const int ChromaSamplePosition = 0; // CSP_UNKNOWN -- box-filter 4:2:0 doesn't claim a specific siting convention

    /// <summary>
    /// AV1's identity color matrix -- <c>Y=G, Cb=B, Cr=R</c>, no cross-channel math (see
    /// <see cref="Av1RgbToYuvIdentityConverter"/>). Combined with <see cref="ColorPrimaries"/>==CP_BT_709 and
    /// <see cref="TransferCharacteristics"/>==TC_SRGB (both already this encoder's fixed values), AV1 spec's
    /// <c>color_config()</c> forces <c>subsampling_x=subsampling_y=0</c> (4:4:4) and skips reading both
    /// <c>color_range</c> and <c>chroma_sample_position</c> -- see <see cref="WriteColorConfig"/>.
    /// </summary>
    public const int MatrixCoefficientsIdentity = 0; // MC_IDENTITY

    /// <summary>Writes the sequence header OBU payload for a <paramref name="width"/> x <paramref name="height"/> still image.</summary>
    /// <param name="width">Padded frame width in pixels.</param>
    /// <param name="height">Padded frame height in pixels.</param>
    /// <param name="monoChrome">Whether this frame is monochrome (matches the frame header's <c>mono_chrome</c>).</param>
    /// <param name="chroma444">
    /// When <see langword="true"/> (only ever paired with a non-monochrome, lossless encode -- see
    /// <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate), signals AV1's identity color matrix and
    /// implicit 4:4:4 instead of this encoder's usual BT.601/4:2:0. Ignored when <paramref name="monoChrome"/>.
    /// </param>
    /// <param name="enableCdef">
    /// <c>enable_cdef</c> -- whether <see cref="Av1FrameHeaderWriter"/> may signal a real (non-<see cref="Av1CdefChoice.Off"/>)
    /// CDEF strength combo for this frame (see <see cref="Av1CdefSearch"/>). Tied to <c>!lossless</c> at the
    /// one caller (<see cref="Av1FrameEncoder.Encode"/>): CDEF is a lossy-only tool (spec's <c>codedLossless</c>
    /// short-circuit means <c>cdef_params()</c> would never be read at coded-lossless regardless of this
    /// flag), and enabling it for a lossless frame would only cost bits (the sequence-level flag itself, plus
    /// the header's <c>cdef_params()</c> bits every frame this sequence header covers would then have to
    /// carry) for a tool that can never actually run there.
    /// </param>
    /// <param name="use128x128Superblock">
    /// <c>use_128x128_superblock</c> -- tied to <c>lossless</c> at the one caller
    /// (<see cref="Av1FrameEncoder.Encode"/>): matches libaom's own default superblock-size choice for
    /// non-tiny images (see <c>Av1TileEncoder</c>'s remarks), and this encoder's own non-lossless path never
    /// produces a leaf bigger than 32x32 anyway (see the project plan's partition/TX-size RDO phase), so a
    /// bigger superblock there would only add signaling overhead for zero benefit.
    /// </param>
    /// <param name="enableRestoration">
    /// <c>enable_restoration</c> -- <see cref="Av1FrameEncoder.Encode"/>'s one call site passes this as
    /// <c>lossless</c> itself, since it's provably inert there (see this method's own remarks at the write
    /// site). Must stay <see langword="false"/> for non-lossless, where it would make <c>lr_params()</c> a
    /// real, decoder-read syntax element this encoder doesn't implement writing.
    /// </param>
    /// <param name="colorPrimaries">CICP <c>color_primaries</c> (H.273), from <see cref="AvifEncoderOptions.ColorPrimaries"/>. Cosmetic tagging only -- see <see cref="ColorPrimaries"/>'s own remarks.</param>
    /// <param name="transferCharacteristics">CICP <c>transfer_characteristics</c> (H.273), from <see cref="AvifEncoderOptions.TransferCharacteristics"/>. Cosmetic tagging only -- see <see cref="ColorPrimaries"/>'s own remarks.</param>
    /// <param name="chromaSamplePosition">
    /// <c>chroma_sample_position</c>, from <see cref="AvifEncoderOptions.ChromaSamplePosition"/>. Only ever
    /// actually written to the bitstream for the non-chroma444 (4:2:0) case -- see <see cref="WriteColorConfig"/>.
    /// </param>
    public static byte[] Write(int width, int height, bool monoChrome, bool chroma444 = false, bool enableCdef = false, bool use128x128Superblock = false, bool enableRestoration = false, int colorPrimaries = ColorPrimaries, int transferCharacteristics = TransferCharacteristics, int chromaSamplePosition = ChromaSamplePosition)
    {
        var writer = new Av1BitWriter();

        int seqProfile = chroma444 ? SeqProfileChroma444 : SeqProfile;
        writer.WriteBits((uint)seqProfile, 3);
        writer.WriteFlag(true); // still_picture
        writer.WriteFlag(true); // reduced_still_picture_header
        writer.WriteBits((uint)ComputeSeqLevelIdx(width, height), 5);

        int frameWidthBits = BitsToRepresentMinusOne(width);
        int frameHeightBits = BitsToRepresentMinusOne(height);
        if (frameWidthBits > 16 || frameHeightBits > 16)
        {
            throw new AvifEncodingException($"Image dimensions {width}x{height} exceed what this encoder's sequence header can represent (max 65536 per side).");
        }

        writer.WriteBits((uint)(frameWidthBits - 1), 4);
        writer.WriteBits((uint)(frameHeightBits - 1), 4);
        writer.WriteBits((uint)(width - 1), frameWidthBits);
        writer.WriteBits((uint)(height - 1), frameHeightBits);

        writer.WriteFlag(use128x128Superblock);

        // enable_filter_intra: tied to "always on, let per-leaf RDO decide" the same way
        // allow_screen_content_tools/allow_intrabc are (see Av1FrameHeaderWriter) -- Av1TileEncoder.EncodeLeaf
        // only actually signals use_filter_intra=1 on the leaves where a filter-intra candidate wins the
        // search (Phase D technique 3), so a frame that never uses it just pays this one header bit plus a
        // per-eligible-leaf use_filter_intra bit, matching every other "structurally present" gate here.
        writer.WriteFlag(true); // enable_filter_intra
        writer.WriteFlag(EnableIntraEdgeFilter);
        writer.WriteFlag(false); // enable_superres
        writer.WriteFlag(enableCdef); // enable_cdef

        // enable_restoration: real encoders (confirmed against aomenc's own --obu output) signal this true
        // for a lossless frame even though it never actually does anything there -- lr_params()'s own gating
        // (spec's `(!AllLossless || allow_superres) && seq.enable_restoration && !allow_intrabc`) already
        // evaluates false for every lossless frame this encoder ever produces regardless of this bit, since
        // AllLossless is always true and allow_superres is always false here (superres isn't implemented),
        // making `!AllLossless || allow_superres` false on its own. So flipping this bit for lossless costs
        // nothing (no lr_params() bits are ever read either way) and matches real encoder output; it must
        // stay false for non-lossless, where `!AllLossless` is true and would make lr_params() a real,
        // decoder-read syntax element this encoder doesn't implement writing.
        writer.WriteFlag(enableRestoration); // enable_restoration

        WriteColorConfig(writer, monoChrome, chroma444, colorPrimaries, transferCharacteristics, chromaSamplePosition);

        writer.WriteFlag(false); // film_grain_params_present

        // trailing_bits() (spec §5.5.1's own final call) -- see Av1BitWriter.WriteTrailingBits's remarks for
        // why this is not optional (it matters even when the preceding content already reaches a byte
        // boundary, not just to "pad" a partial byte).
        writer.WriteTrailingBits();

        return writer.ToArray();
    }

    /// <summary>
    /// <c>color_config()</c> (spec §5.5.2), write-side mirror of the private method in
    /// <see cref="Av1SequenceHeader"/>. Mirrors that method's exact branch order and, since
    /// <paramref name="colorPrimaries"/>/<paramref name="transferCharacteristics"/> are caller-settable (see
    /// <see cref="AvifEncoderOptions"/>), its exact three-way identity-matrix condition too -- the identity
    /// path is only reachable when <em>all three</em> of colorPrimaries/transferCharacteristics/
    /// matrixCoefficients match (CP_BT_709/TC_SRGB/MC_IDENTITY exactly), not merely whenever
    /// matrixCoefficients happens to be identity, the way this collapsed when those two were still fixed
    /// constants. Getting this branch choice wrong for a caller-supplied non-default primaries/transfer pair
    /// would desync any real decoder (including this project's own <see cref="Av1SequenceHeader"/>), since
    /// the identity branch skips <c>color_range</c>/<c>chroma_sample_position</c> bits the general branch
    /// always writes.
    /// </summary>
    private static void WriteColorConfig(Av1BitWriter writer, bool monoChrome, bool chroma444, int colorPrimaries, int transferCharacteristics, int chromaSamplePosition)
    {
        writer.WriteFlag(false); // high_bitdepth (8-bit only in v1)

        // mono_chrome is read as `seq_profile != 1 && reader.ReadFlag()` -- at seq_profile == 1 (only ever
        // used when chroma444, which is only ever true for a non-monochrome frame) the bit is short-circuited
        // away entirely and mono_chrome is implicitly false, so it must not be written; every other profile
        // this encoder writes (always 0) always reads/writes the bit.
        if (!chroma444)
        {
            writer.WriteFlag(monoChrome);
        }

        writer.WriteFlag(true); // color_description_present_flag

        // chroma444 is never true when monoChrome (see Av1FrameEncoder.Encode's chroma444 gate), so the
        // monochrome path below always keeps writing MatrixCoefficients (BT.601) -- the identity path is
        // reachable only for a real, non-monochrome 4:4:4-lossless encode.
        int matrixCoefficients = (!monoChrome && chroma444) ? MatrixCoefficientsIdentity : MatrixCoefficients;
        writer.WriteBits((uint)colorPrimaries, 8);
        writer.WriteBits((uint)transferCharacteristics, 8);
        writer.WriteBits((uint)matrixCoefficients, 8);

        if (monoChrome)
        {
            writer.WriteFlag(ColorRangeFull); // color_range -- always read for monochrome, matrix irrelevant
            // subsampling_x/y forced true, chroma_sample_position=CSP_UNKNOWN, separate_uv_delta_q=false --
            // none of these are read from the bitstream in the monochrome path.
            return;
        }

        // Real three-way check, matching Av1SequenceHeader.ParseColorConfig exactly -- see this method's own
        // remarks above for why this can no longer be collapsed to a matrix-only comparison.
        bool identityBranch = colorPrimaries == 1 && transferCharacteristics == 13 && matrixCoefficients == MatrixCoefficientsIdentity;
        if (identityBranch)
        {
            // Decoder's identity-matrix special case (spec §5.5.2): color_range is implicitly full-range and
            // NOT read; subsampling_x/y are implicitly false (4:4:4); chroma_sample_position is not read
            // either. This is only correct because ColorRangeFull is (and must stay) true -- the bitstream
            // has no way to signal anything else once this branch is taken.
        }
        else
        {
            writer.WriteFlag(ColorRangeFull); // color_range
            // seq_profile == 0 forces subsampling_x = subsampling_y = true; seq_profile == 1 (chroma444)
            // forces both false. chroma_sample_position is only read when both are true (never true for
            // chroma444/profile 1), matching Av1SequenceHeader.ParseColorConfig's own subsamplingX &&
            // subsamplingY gate exactly.
            if (!chroma444)
            {
                writer.WriteBits((uint)chromaSamplePosition, 2);
            }
        }

        writer.WriteFlag(false); // separate_uv_delta_q -- read unconditionally for non-monochrome either way
    }

    /// <summary>Bits needed to represent <paramref name="value"/> - 1 as an unsigned integer, minimum 1 (matches the decoder's <c>frame_width_bits_minus_1</c>/<c>frame_height_bits_minus_1</c> semantics).</summary>
    private static int BitsToRepresentMinusOne(int value)
    {
        uint x = (uint)(value - 1);
        if (x == 0)
        {
            return 1;
        }

        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }

        return s;
    }
}
