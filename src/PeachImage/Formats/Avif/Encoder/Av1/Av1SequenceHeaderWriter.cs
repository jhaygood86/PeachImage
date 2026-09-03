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

    /// <summary>Spec-reserved "no level constraint" value; PeachImage's own decoder never validates it.</summary>
    public const int SeqLevelIdx0 = 31;

    /// <summary>
    /// <see cref="Av1IntraPrediction"/>'s directional/smooth predictors already implement edge filtering
    /// unconditionally, so this is enabled at the sequence level to keep what a real decoder reconstructs
    /// consistent with what this encoder's own local reconstruction (used for RDO and neighbor context)
    /// produces.
    /// </summary>
    public const bool EnableIntraEdgeFilter = true;

    // Informational tagging only -- Av1YuvToRgbConverter (and its forward-direction counterpart,
    // Av1RgbToYuvConverter) only ever consult MatrixCoefficients/ColorRange for actual pixel math, never
    // ColorPrimaries/TransferCharacteristics, so those two are cosmetic. MatrixCoefficients must match
    // Av1RgbToYuvConverter's forward matrix exactly when not using the identity path below.
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
    public static byte[] Write(int width, int height, bool monoChrome, bool chroma444 = false)
    {
        var writer = new Av1BitWriter();

        int seqProfile = chroma444 ? SeqProfileChroma444 : SeqProfile;
        writer.WriteBits((uint)seqProfile, 3);
        writer.WriteFlag(true); // still_picture
        writer.WriteFlag(true); // reduced_still_picture_header
        writer.WriteBits(SeqLevelIdx0, 5);

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

        writer.WriteFlag(false); // use_128x128_superblock

        // enable_filter_intra: tied to "always on, let per-leaf RDO decide" the same way
        // allow_screen_content_tools/allow_intrabc are (see Av1FrameHeaderWriter) -- Av1TileEncoder.EncodeLeaf
        // only actually signals use_filter_intra=1 on the leaves where a filter-intra candidate wins the
        // search (Phase D technique 3), so a frame that never uses it just pays this one header bit plus a
        // per-eligible-leaf use_filter_intra bit, matching every other "structurally present" gate here.
        writer.WriteFlag(true); // enable_filter_intra
        writer.WriteFlag(EnableIntraEdgeFilter);
        writer.WriteFlag(false); // enable_superres
        writer.WriteFlag(false); // enable_cdef
        writer.WriteFlag(false); // enable_restoration

        WriteColorConfig(writer, monoChrome, chroma444);

        writer.WriteFlag(false); // film_grain_params_present

        // trailing_bits() (spec §5.5.1's own final call) -- see Av1BitWriter.WriteTrailingBits's remarks for
        // why this is not optional (it matters even when the preceding content already reaches a byte
        // boundary, not just to "pad" a partial byte).
        writer.WriteTrailingBits();

        return writer.ToArray();
    }

    /// <summary>
    /// <c>color_config()</c> (spec §5.5.2), write-side mirror of the private method in
    /// <see cref="Av1SequenceHeader"/>. Mirrors that method's exact branch order, not just "skip 2 bits for
    /// 4:4:4" -- the identity-matrix branch skips <c>color_range</c> too, and skips it *before* any
    /// profile/subsampling branching would otherwise happen, per spec.
    /// </summary>
    private static void WriteColorConfig(Av1BitWriter writer, bool monoChrome, bool chroma444)
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
        writer.WriteBits(ColorPrimaries, 8);
        writer.WriteBits(TransferCharacteristics, 8);
        writer.WriteBits((uint)matrixCoefficients, 8);

        if (monoChrome)
        {
            writer.WriteFlag(ColorRangeFull); // color_range -- always read for monochrome, matrix irrelevant
            // subsampling_x/y forced true, chroma_sample_position=CSP_UNKNOWN, separate_uv_delta_q=false --
            // none of these are read from the bitstream in the monochrome path.
            return;
        }

        // ColorPrimaries/TransferCharacteristics are the fixed CP_BT_709/TC_SRGB constants above, so the
        // decoder's three-way check (colorPrimaries==CpBt709 && transferCharacteristics==TcSrgb &&
        // matrixCoefficients==McIdentity) collapses to just the matrix-coefficients comparison here.
        if (matrixCoefficients == MatrixCoefficientsIdentity)
        {
            // Decoder's identity-matrix special case (spec §5.5.2): color_range is implicitly full-range and
            // NOT read; subsampling_x/y are implicitly false (4:4:4); chroma_sample_position is not read
            // either. This is only correct because ColorRangeFull is (and must stay) true -- the bitstream
            // has no way to signal anything else once this branch is taken.
        }
        else
        {
            writer.WriteFlag(ColorRangeFull); // color_range
            // seq_profile == 0 forces subsampling_x = subsampling_y = true; no bits read for them here, unlike
            // the general form Av1SequenceHeader.ParseColorConfig handles for other profiles.
            writer.WriteBits(ChromaSamplePosition, 2);
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
