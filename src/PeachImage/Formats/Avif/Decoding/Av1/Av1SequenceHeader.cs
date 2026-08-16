namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// A parsed <c>sequence_header_obu()</c> (spec §5.5), restricted to the <c>reduced_still_picture_header
/// == 1</c> form -- the only form AVIF permits (AVIF spec Annex A requires it). This prunes away
/// essentially all of the video-oriented machinery the full form carries (timing info, decoder model
/// info, multiple operating points, temporal/spatial scalability) since none of it is reachable when
/// this flag is set; a file that sets it to 0 is rejected as <see cref="AvifUnsupportedFeatureException"/>
/// rather than attempting to parse (and immediately discard) an entire subsystem AVIF still images never
/// use.
/// </summary>
internal sealed class Av1SequenceHeader
{
    public required int SeqProfile { get; init; }

    public required int SeqLevelIdx0 { get; init; }

    public required int FrameWidthBits { get; init; }

    public required int FrameHeightBits { get; init; }

    public required int MaxFrameWidth { get; init; }

    public required int MaxFrameHeight { get; init; }

    public required bool Use128x128Superblock { get; init; }

    public required bool EnableFilterIntra { get; init; }

    public required bool EnableIntraEdgeFilter { get; init; }

    public required bool EnableSuperres { get; init; }

    public required bool EnableCdef { get; init; }

    public required bool EnableRestoration { get; init; }

    public required int BitDepth { get; init; }

    public required bool MonoChrome { get; init; }

    public int NumPlanes => MonoChrome ? 1 : 3;

    public required int ColorPrimaries { get; init; }

    public required int TransferCharacteristics { get; init; }

    public required int MatrixCoefficients { get; init; }

    public required bool ColorRange { get; init; }

    public required bool SubsamplingX { get; init; }

    public required bool SubsamplingY { get; init; }

    public required int ChromaSamplePosition { get; init; }

    public required bool SeparateUvDeltaQ { get; init; }

    public required bool FilmGrainParamsPresent { get; init; }

    /// <summary>Reduced-still-picture-header AV1 forces this to <c>SELECT_SCREEN_CONTENT_TOOLS</c> (2) rather than reading it from the bitstream.</summary>
    public const int SeqForceScreenContentTools = 2;

    /// <summary>Reduced-still-picture-header AV1 forces this to <c>SELECT_INTEGER_MV</c> (2) rather than reading it from the bitstream.</summary>
    public const int SeqForceIntegerMv = 2;

    private const int CpBt709 = 1;
    private const int TcSrgb = 13;
    private const int McIdentity = 0;
    private const int CpUnspecified = 2;
    private const int TcUnspecified = 2;
    private const int McUnspecified = 2;

    public static Av1SequenceHeader Parse(Av1BitReader reader)
    {
        int seqProfile = (int)reader.ReadBits(3);
        reader.ReadFlag(); // still_picture -- not consulted; AVIF's own ftyp brand is authoritative for "is this a still image"
        bool reducedStillPictureHeader = reader.ReadFlag();

        if (!reducedStillPictureHeader)
        {
            throw new AvifUnsupportedFeatureException("AV1 sequence headers with reduced_still_picture_header == 0 are not supported; AVIF requires it to be 1.");
        }

        int seqLevelIdx0 = (int)reader.ReadBits(5);

        int frameWidthBits = (int)reader.ReadBits(4) + 1;
        int frameHeightBits = (int)reader.ReadBits(4) + 1;
        int maxFrameWidth = (int)reader.ReadBits(frameWidthBits) + 1;
        int maxFrameHeight = (int)reader.ReadBits(frameHeightBits) + 1;

        // frame_id_numbers_present_flag is forced to 0 when reduced_still_picture_header == 1 -- no bits read.
        bool use128x128Superblock = reader.ReadFlag();
        bool enableFilterIntra = reader.ReadFlag();
        bool enableIntraEdgeFilter = reader.ReadFlag();

        // enable_interintra_compound/enable_masked_compound/enable_warped_motion/enable_dual_filter/
        // enable_order_hint/enable_jnt_comp/enable_ref_frame_mvs are all forced to 0 (and
        // seq_force_screen_content_tools/seq_force_integer_mv forced to SELECT_*) when
        // reduced_still_picture_header == 1 -- none of them are read from the bitstream.
        bool enableSuperres = reader.ReadFlag();
        bool enableCdef = reader.ReadFlag();
        bool enableRestoration = reader.ReadFlag();

        ParseColorConfig(
            reader,
            seqProfile,
            out int bitDepth,
            out bool monoChrome,
            out int colorPrimaries,
            out int transferCharacteristics,
            out int matrixCoefficients,
            out bool colorRange,
            out bool subsamplingX,
            out bool subsamplingY,
            out int chromaSamplePosition,
            out bool separateUvDeltaQ);

        bool filmGrainParamsPresent = reader.ReadFlag();

        return new Av1SequenceHeader
        {
            SeqProfile = seqProfile,
            SeqLevelIdx0 = seqLevelIdx0,
            FrameWidthBits = frameWidthBits,
            FrameHeightBits = frameHeightBits,
            MaxFrameWidth = maxFrameWidth,
            MaxFrameHeight = maxFrameHeight,
            Use128x128Superblock = use128x128Superblock,
            EnableFilterIntra = enableFilterIntra,
            EnableIntraEdgeFilter = enableIntraEdgeFilter,
            EnableSuperres = enableSuperres,
            EnableCdef = enableCdef,
            EnableRestoration = enableRestoration,
            BitDepth = bitDepth,
            MonoChrome = monoChrome,
            ColorPrimaries = colorPrimaries,
            TransferCharacteristics = transferCharacteristics,
            MatrixCoefficients = matrixCoefficients,
            ColorRange = colorRange,
            SubsamplingX = subsamplingX,
            SubsamplingY = subsamplingY,
            ChromaSamplePosition = chromaSamplePosition,
            SeparateUvDeltaQ = separateUvDeltaQ,
            FilmGrainParamsPresent = filmGrainParamsPresent,
        };
    }

    /// <summary><c>color_config()</c> (spec §5.5.2).</summary>
    private static void ParseColorConfig(
        Av1BitReader reader,
        int seqProfile,
        out int bitDepth,
        out bool monoChrome,
        out int colorPrimaries,
        out int transferCharacteristics,
        out int matrixCoefficients,
        out bool colorRange,
        out bool subsamplingX,
        out bool subsamplingY,
        out int chromaSamplePosition,
        out bool separateUvDeltaQ)
    {
        bool highBitdepth = reader.ReadFlag();
        if (seqProfile == 2 && highBitdepth)
        {
            bool twelveBit = reader.ReadFlag();
            bitDepth = twelveBit ? 12 : 10;
        }
        else
        {
            bitDepth = highBitdepth ? 10 : 8;
        }

        if (bitDepth == 12)
        {
            throw new AvifUnsupportedFeatureException("12-bit AVIF is not supported.");
        }

        monoChrome = seqProfile != 1 && reader.ReadFlag();

        bool colorDescriptionPresent = reader.ReadFlag();
        if (colorDescriptionPresent)
        {
            colorPrimaries = (int)reader.ReadBits(8);
            transferCharacteristics = (int)reader.ReadBits(8);
            matrixCoefficients = (int)reader.ReadBits(8);
        }
        else
        {
            colorPrimaries = CpUnspecified;
            transferCharacteristics = TcUnspecified;
            matrixCoefficients = McUnspecified;
        }

        if (monoChrome)
        {
            colorRange = reader.ReadFlag();
            subsamplingX = true;
            subsamplingY = true;
            chromaSamplePosition = 0;
            separateUvDeltaQ = false;
            return;
        }

        if (colorPrimaries == CpBt709 && transferCharacteristics == TcSrgb && matrixCoefficients == McIdentity)
        {
            colorRange = true;
            subsamplingX = false;
            subsamplingY = false;
            // Spec leaves chroma_sample_position unassigned on this path (subsampling is 4:4:4, where it's
            // meaningless) -- 0 (CSP_UNKNOWN) matches the same "not applicable" value used elsewhere.
            chromaSamplePosition = 0;
        }
        else
        {
            colorRange = reader.ReadFlag();

            if (seqProfile == 0)
            {
                subsamplingX = true;
                subsamplingY = true;
            }
            else if (seqProfile == 1)
            {
                subsamplingX = false;
                subsamplingY = false;
            }
            else if (bitDepth == 12)
            {
                subsamplingX = reader.ReadFlag();
                subsamplingY = subsamplingX && reader.ReadFlag();
            }
            else
            {
                subsamplingX = true;
                subsamplingY = false;
            }

            chromaSamplePosition = subsamplingX && subsamplingY ? (int)reader.ReadBits(2) : 0;
        }

        separateUvDeltaQ = reader.ReadFlag();
    }
}
