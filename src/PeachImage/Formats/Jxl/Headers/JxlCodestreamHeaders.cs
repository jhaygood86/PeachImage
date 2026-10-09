using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl.Headers;

/// <summary>
/// Everything in a codestream before the (optional) ICC profile and the first frame: signature, size header,
/// image metadata and custom transform data.
/// </summary>
internal sealed class JxlCodestreamHeaders
{
    private JxlCodestreamHeaders(JxlSize size, JxlImageMetadata metadata, JxlCustomTransformData transformData, byte[]? iccProfile, int frameOffset)
    {
        Size = size;
        Metadata = metadata;
        TransformData = transformData;
        IccProfile = iccProfile;
        FrameOffset = frameOffset;
    }

    public JxlSize Size { get; }

    public JxlImageMetadata Metadata { get; }

    public JxlCustomTransformData TransformData { get; }

    /// <summary>The embedded ICC profile, when the color encoding asks for one and it was read.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>The byte offset from the start of the codestream at which the first frame begins, or -1 when the ICC stream was skipped.</summary>
    public int FrameOffset { get; }

    /// <summary>Whether an entropy-coded ICC profile follows the headers.</summary>
    public bool WantIcc => Metadata.ColorEncoding.WantIcc;

    /// <summary>
    /// Reads the headers. With <paramref name="readIcc"/> false the ICC stream is not decoded, so
    /// <see cref="FrameOffset"/> is -1 (enough for identification).
    /// </summary>
    public static JxlCodestreamHeaders Read(ReadOnlySpan<byte> codestream, bool readIcc = true)
    {
        if (codestream.Length < 2 || codestream[0] != 0xFF || codestream[1] != 0x0A)
        {
            throw new JxlDecodingException("Missing JPEG XL codestream signature.");
        }

        var reader = new JxlBitReader(codestream);
        reader.Skip(16);

        var size = JxlSize.ReadSizeHeader(ref reader);
        if (size.Width == 0 || size.Height == 0)
        {
            throw new JxlDecodingException("The image has an empty dimension.");
        }

        if ((long)size.Width * size.Height > JxlDecodingLimits.MaxPixelCount)
        {
            throw new JxlDecodingException("The image is too large.");
        }

        var metadata = JxlImageMetadata.Read(ref reader);
        var transformData = JxlCustomTransformData.Read(ref reader, metadata.XybEncoded);
        reader.ThrowIfOverrun();

        byte[]? icc = null;
        if (metadata.ColorEncoding.WantIcc && readIcc)
        {
            icc = JxlIccDecoder.Read(ref reader);
        }

        if (metadata.ColorEncoding.WantIcc && !readIcc)
        {
            // The ICC stream was skipped, so the frame position is unknown.
            return new JxlCodestreamHeaders(size, metadata, transformData, iccProfile: null, frameOffset: -1);
        }

        // The first frame starts on a byte boundary.
        reader.ZeroPadToByte();
        reader.ThrowIfOverrun();
        return new JxlCodestreamHeaders(size, metadata, transformData, icc, checked((int)(reader.BitPosition >> 3)));
    }
}
