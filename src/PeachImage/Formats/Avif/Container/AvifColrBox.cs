namespace PeachImage.Formats.Avif.Container;

/// <summary>
/// The <c>colr</c> property: either an <c>nclx</c> (ITU-T H.273 CICP) numeric color description, or an
/// embedded ICC profile (<c>rICC</c>/<c>prof</c>). A plain <c>Box</c>, not a <c>FullBox</c>. Not yet wired
/// into pixel decode/conversion (Phase 1 doesn't decode pixels) — Phase 2's YUV-&gt;RGB converter must
/// honor the AV1 bitstream's own <c>color_config()</c> matrix/range regardless, per the plan; this
/// container-level box mainly matters for ICC profile passthrough into <see cref="ImageMetadata"/>.
/// </summary>
internal sealed class AvifColr
{
    public required string ColourType { get; init; }

    public int ColourPrimaries { get; init; }

    public int TransferCharacteristics { get; init; }

    public int MatrixCoefficients { get; init; }

    public bool FullRange { get; init; }

    public byte[]? IccProfile { get; init; }
}

internal static class AvifColrBox
{
    public static AvifColr Parse(byte[] data, AvifBox box)
    {
        if (box.PayloadLength < 4)
        {
            throw new AvifDecodingException("Truncated 'colr' box.");
        }

        int offset = box.PayloadOffset;
        string colourType = AvifBinaryReader.ReadFourCc(data, offset);
        offset += 4;

        if (colourType == "nclx")
        {
            if (box.PayloadOffset + box.PayloadLength < offset + 7)
            {
                throw new AvifDecodingException("Truncated 'colr' (nclx) box.");
            }

            int primaries = AvifBinaryReader.ReadUInt16(data, ref offset);
            int transfer = AvifBinaryReader.ReadUInt16(data, ref offset);
            int matrix = AvifBinaryReader.ReadUInt16(data, ref offset);
            bool fullRange = (data[offset] & 0x80) != 0;

            return new AvifColr
            {
                ColourType = colourType,
                ColourPrimaries = primaries,
                TransferCharacteristics = transfer,
                MatrixCoefficients = matrix,
                FullRange = fullRange,
            };
        }

        if (colourType is "rICC" or "prof")
        {
            int iccLength = box.PayloadOffset + box.PayloadLength - offset;
            byte[] icc = data.AsSpan(offset, iccLength).ToArray();
            return new AvifColr { ColourType = colourType, IccProfile = icc };
        }

        return new AvifColr { ColourType = colourType };
    }
}
