using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl.Headers;

/// <summary>The codestream's <c>ImageMetadata</c> bundle: orientation, bit depth, extra channels, color encoding, tone mapping, optional preview and animation.</summary>
internal sealed class JxlImageMetadata
{
    /// <summary>EXIF-style orientation, 1 to 8 (1 = identity).</summary>
    public int Orientation { get; private init; } = 1;

    public JxlSize? IntrinsicSize { get; private init; }

    public JxlSize? PreviewSize { get; private init; }

    public JxlAnimationHeader? Animation { get; private init; }

    public JxlBitDepth BitDepth { get; private init; } = JxlBitDepth.Default;

    public bool Modular16BitBufferSufficient { get; private init; } = true;

    public IReadOnlyList<JxlExtraChannelInfo> ExtraChannels { get; private init; } = [];

    /// <summary>Whether the color channels are stored in the XYB opsin space (VarDCT, and lossy Modular) rather than the original color space.</summary>
    public bool XybEncoded { get; private init; } = true;

    public JxlColorEncoding ColorEncoding { get; private init; } = JxlColorEncoding.Default;

    public JxlToneMapping ToneMapping { get; private init; } = JxlToneMapping.Default;

    /// <summary>Gets the index of the first alpha extra channel, or -1.</summary>
    public int AlphaChannelIndex
    {
        get
        {
            for (int i = 0; i < ExtraChannels.Count; i++)
            {
                if (ExtraChannels[i].Type == JxlExtraChannelType.Alpha)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public static JxlImageMetadata Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return new JxlImageMetadata();
        }

        int orientation = 1;
        JxlSize? intrinsic = null;
        JxlSize? preview = null;
        JxlAnimationHeader? animation = null;
        var toneMapping = JxlToneMapping.Default;

        bool extraFields = reader.ReadBool();
        if (extraFields)
        {
            orientation = (int)reader.ReadBits(3) + 1;
            if (reader.ReadBool())
            {
                intrinsic = JxlSize.ReadSizeHeader(ref reader);
            }

            if (reader.ReadBool())
            {
                preview = JxlSize.ReadPreviewHeader(ref reader);
            }

            if (reader.ReadBool())
            {
                animation = JxlAnimationHeader.Read(ref reader);
            }
        }

        var bitDepth = JxlBitDepth.Read(ref reader);
        bool modular16 = reader.ReadBool();

        uint extraCount = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(12, 1));
        reader.ThrowIfOverrun();
        if (extraCount > JxlDecodingLimits.MaxExtraChannels)
        {
            throw new JxlDecodingException("Too many extra channels.");
        }

        var extras = new JxlExtraChannelInfo[extraCount];
        for (int i = 0; i < extras.Length; i++)
        {
            extras[i] = JxlExtraChannelInfo.Read(ref reader);
        }

        bool xyb = reader.ReadBool();
        var colorEncoding = JxlColorEncoding.Read(ref reader);
        if (extraFields)
        {
            toneMapping = JxlToneMapping.Read(ref reader);
        }

        JxlFieldReader.SkipExtensions(ref reader);
        reader.ThrowIfOverrun();

        return new JxlImageMetadata
        {
            Orientation = orientation,
            IntrinsicSize = intrinsic,
            PreviewSize = preview,
            Animation = animation,
            BitDepth = bitDepth,
            Modular16BitBufferSufficient = modular16,
            ExtraChannels = extras,
            XybEncoded = xyb,
            ColorEncoding = colorEncoding,
            ToneMapping = toneMapping,
        };
    }
}
