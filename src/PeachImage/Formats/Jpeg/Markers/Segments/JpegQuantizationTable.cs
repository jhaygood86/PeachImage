using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>A parsed quantization table (one of possibly several packed into a single DQT segment), in natural (row-major) order.</summary>
internal sealed class JpegQuantizationTable
{
    /// <summary>The table id (0-3), as referenced by frame headers.</summary>
    public required byte Id { get; init; }

    /// <summary>The 64 quantization values, in natural (row-major, not zigzag) order.</summary>
    public required ushort[] Values { get; init; }

    /// <summary>Parses every table packed into a DQT segment's payload.</summary>
    public static List<JpegQuantizationTable> ParseAll(ReadOnlySpan<byte> payload)
    {
        var tables = new List<JpegQuantizationTable>();
        int offset = 0;

        while (offset < payload.Length)
        {
            byte pqTq = payload[offset++];
            int precision = pqTq >> 4;
            int id = pqTq & 0x0F;

            var values = new ushort[64];
            for (int zigzagIndex = 0; zigzagIndex < 64; zigzagIndex++)
            {
                ushort value;
                if (precision == 0)
                {
                    if (offset >= payload.Length)
                    {
                        throw new JpegDecodingException("Truncated DQT segment.");
                    }

                    value = payload[offset++];
                }
                else
                {
                    if (offset + 1 >= payload.Length)
                    {
                        throw new JpegDecodingException("Truncated DQT segment.");
                    }

                    value = (ushort)((payload[offset] << 8) | payload[offset + 1]);
                    offset += 2;
                }

                values[ZigZag.ToNaturalOrder[zigzagIndex]] = value;
            }

            tables.Add(new JpegQuantizationTable { Id = (byte)id, Values = values });
        }

        return tables;
    }
}
