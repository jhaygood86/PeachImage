namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>Reads a TIFF file's 8-byte header: the 'II'/'MM' byte-order mark, the magic number (42 for classic TIFF; 43 means BigTIFF, which is rejected here rather than misparsed as classic TIFF), and the first IFD's offset.</summary>
internal static class TiffHeaderReader
{
    private const ushort ClassicTiffMagic = 42;
    private const ushort BigTiffMagic = 43;

    public static TiffHeader Read(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new TiffDecodingException("Not a TIFF file: too short to contain an 8-byte header.");
        }

        TiffByteOrder byteOrder;
        if (data[0] == (byte)'I' && data[1] == (byte)'I')
        {
            byteOrder = TiffByteOrder.LittleEndian;
        }
        else if (data[0] == (byte)'M' && data[1] == (byte)'M')
        {
            byteOrder = TiffByteOrder.BigEndian;
        }
        else
        {
            throw new TiffDecodingException("Not a TIFF file: missing 'II'/'MM' byte-order mark.");
        }

        var reader = new TiffReader(data, byteOrder);
        ushort magic = reader.ReadUInt16(2);

        if (magic == BigTiffMagic)
        {
            throw new TiffUnsupportedFeatureException("BigTIFF is not supported.");
        }

        if (magic != ClassicTiffMagic)
        {
            throw new TiffDecodingException($"Not a TIFF file: unexpected magic number {magic} (expected 42).");
        }

        uint firstIfdOffset = reader.ReadUInt32(4);
        return new TiffHeader(byteOrder, firstIfdOffset);
    }
}
