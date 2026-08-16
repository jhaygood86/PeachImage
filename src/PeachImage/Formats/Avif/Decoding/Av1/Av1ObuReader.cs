namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>AV1 Open Bitstream Unit types (spec §6.2.2) relevant to intra-only still-image decode.</summary>
internal static class Av1ObuType
{
    public const int SequenceHeader = 1;
    public const int TemporalDelimiter = 2;
    public const int FrameHeader = 3;
    public const int TileGroup = 4;
    public const int Metadata = 5;
    public const int Frame = 6;
    public const int RedundantFrameHeader = 7;
    public const int TileList = 8;
    public const int Padding = 15;
}

/// <summary>One parsed OBU: its type and where its payload lives in the buffered item bytes.</summary>
internal readonly record struct Av1Obu(int Type, int TemporalId, int SpatialId, int PayloadOffset, int PayloadLength);

/// <summary>Enumerates the OBUs in an AV1 item's byte stream (spec §5.3 <c>open_bitstream_unit()</c>). Byte-level framing only -- does not interpret any OBU's payload.</summary>
internal static class Av1ObuReader
{
    public static List<Av1Obu> ReadObus(byte[] data, int start, int length)
    {
        var result = new List<Av1Obu>();
        int offset = start;
        int end = start + length;

        while (offset < end)
        {
            byte headerByte = data[offset];

            // bit7 = obu_forbidden_bit (must be 0, but tolerated rather than hard-rejected -- matches this
            // codebase's general leniency toward reserved/forward-compatibility bits).
            int obuType = (headerByte >> 3) & 0xF;
            bool extensionFlag = ((headerByte >> 2) & 1) != 0;
            bool hasSizeField = ((headerByte >> 1) & 1) != 0;

            int cursor = offset + 1;
            int temporalId = 0;
            int spatialId = 0;

            if (extensionFlag)
            {
                if (cursor >= end)
                {
                    throw new AvifDecodingException("Truncated AV1 OBU extension header.");
                }

                byte extensionByte = data[cursor];
                temporalId = (extensionByte >> 5) & 0x7;
                spatialId = (extensionByte >> 3) & 0x3;
                cursor++;
            }

            long payloadLength;
            if (hasSizeField)
            {
                payloadLength = ReadLeb128(data, ref cursor, end);
            }
            else
            {
                // Only legal for the very last OBU in the stream; AVIF item data always sets has_size, but
                // handled per spec rather than rejected.
                payloadLength = end - cursor;
            }

            if (payloadLength < 0 || cursor + payloadLength > end)
            {
                throw new AvifDecodingException("AV1 OBU size extends past its containing data.");
            }

            result.Add(new Av1Obu(obuType, temporalId, spatialId, cursor, (int)payloadLength));
            offset = cursor + (int)payloadLength;
        }

        return result;
    }

    private static long ReadLeb128(byte[] data, ref int offset, int end)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (offset >= end)
            {
                throw new AvifDecodingException("Truncated AV1 leb128 value.");
            }

            byte b = data[offset];
            offset++;
            value |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new AvifDecodingException("AV1 leb128 value exceeds the 8-byte limit.");
    }
}
