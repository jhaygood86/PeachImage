using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes a <c>sequence_header_obu()</c> (spec §5.5) in the <c>reduced_still_picture_header == 1</c> form
/// -- the write-side mirror of <see cref="Av1SequenceHeader"/>. This encoder always writes the same fixed
/// v1 configuration: profile 0 (8-bit, 4:2:0), no 128x128 superblocks, no filter-intra, superres/CDEF/loop
/// restoration disabled at the sequence level (not just unused per frame -- this is what lets
/// <see cref="Av1FrameHeaderWriter"/> skip their per-frame syntax entirely, since the parser only reads
/// those fields when the corresponding <c>enable_*</c> flag is set), and no film grain. Only image
/// dimensions and monochrome-vs-color vary between calls.
/// </summary>
internal static class Av1SequenceHeaderWriter
{
    public const int SeqProfile = 0;

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
    // Av1RgbToYuvConverter's forward matrix exactly, and must not be 0 (identity) -- see WriteColorConfig.
    public const int ColorPrimaries = 1; // CP_BT_709
    public const int TransferCharacteristics = 13; // TC_SRGB
    public const int MatrixCoefficients = 6; // MC_BT_601 / SMPTE170M
    public const bool ColorRangeFull = true;
    public const int ChromaSamplePosition = 0; // CSP_UNKNOWN -- box-filter 4:2:0 doesn't claim a specific siting convention

    /// <summary>Writes the sequence header OBU payload for a <paramref name="width"/> x <paramref name="height"/> still image.</summary>
    public static byte[] Write(int width, int height, bool monoChrome)
    {
        var writer = new Av1BitWriter();

        writer.WriteBits(SeqProfile, 3);
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
        writer.WriteFlag(false); // enable_filter_intra
        writer.WriteFlag(EnableIntraEdgeFilter);
        writer.WriteFlag(false); // enable_superres
        writer.WriteFlag(false); // enable_cdef
        writer.WriteFlag(false); // enable_restoration

        WriteColorConfig(writer, monoChrome);

        writer.WriteFlag(false); // film_grain_params_present

        // trailing_bits() (spec §5.5.1's own final call) -- see Av1BitWriter.WriteTrailingBits's remarks for
        // why this is not optional (it matters even when the preceding content already reaches a byte
        // boundary, not just to "pad" a partial byte).
        writer.WriteTrailingBits();

        return writer.ToArray();
    }

    /// <summary><c>color_config()</c> (spec §5.5.2), write-side mirror of the private method in <see cref="Av1SequenceHeader"/>.</summary>
    private static void WriteColorConfig(Av1BitWriter writer, bool monoChrome)
    {
        writer.WriteFlag(false); // high_bitdepth (8-bit only in v1)
        writer.WriteFlag(monoChrome); // mono_chrome (seq_profile == 0 != 1, so this bit is always read/written)
        writer.WriteFlag(true); // color_description_present_flag
        writer.WriteBits(ColorPrimaries, 8);
        writer.WriteBits(TransferCharacteristics, 8);
        writer.WriteBits(MatrixCoefficients, 8);

        writer.WriteFlag(ColorRangeFull); // color_range

        if (monoChrome)
        {
            // subsampling_x/y forced true, chroma_sample_position=CSP_UNKNOWN, separate_uv_delta_q=false --
            // none of these are read from the bitstream in the monochrome path.
            return;
        }

        // seq_profile == 0 forces subsampling_x = subsampling_y = true; no bits read for them here, unlike
        // the general form Av1SequenceHeader.ParseColorConfig handles for other profiles.
        writer.WriteBits(ChromaSamplePosition, 2);
        writer.WriteFlag(false); // separate_uv_delta_q
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
