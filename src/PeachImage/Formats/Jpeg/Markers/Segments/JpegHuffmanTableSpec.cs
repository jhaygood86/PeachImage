namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>A parsed Huffman table specification (one of possibly several packed into a single DHT segment), as delivered by the bitstream.</summary>
internal sealed class JpegHuffmanTableSpec
{
    /// <summary>0 for a DC table, 1 for an AC table.</summary>
    public required int TableClass { get; init; }

    /// <summary>The table id (0-3), as referenced by scan headers.</summary>
    public required byte Id { get; init; }

    /// <summary>The number of codes of each bit length 1-16 (BITS, 16 entries).</summary>
    public required byte[] Counts { get; init; }

    /// <summary>The symbol values in code order (HUFFVAL).</summary>
    public required byte[] Values { get; init; }

    /// <summary>Parses every table specification packed into a DHT segment's payload.</summary>
    public static List<JpegHuffmanTableSpec> ParseAll(ReadOnlySpan<byte> payload)
    {
        var specs = new List<JpegHuffmanTableSpec>();
        int offset = 0;

        while (offset < payload.Length)
        {
            if (offset + 17 > payload.Length)
            {
                throw new JpegDecodingException("Truncated DHT segment.");
            }

            byte tcTh = payload[offset++];
            int tableClass = tcTh >> 4;
            int id = tcTh & 0x0F;

            var counts = payload.Slice(offset, 16).ToArray();
            offset += 16;

            int total = 0;
            foreach (byte count in counts)
            {
                total += count;
            }

            if (offset + total > payload.Length)
            {
                throw new JpegDecodingException("Truncated DHT segment.");
            }

            var values = payload.Slice(offset, total).ToArray();
            offset += total;

            specs.Add(new JpegHuffmanTableSpec
            {
                TableClass = tableClass,
                Id = (byte)id,
                Counts = counts,
                Values = values,
            });
        }

        return specs;
    }
}
