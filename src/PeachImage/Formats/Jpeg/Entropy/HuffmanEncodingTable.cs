namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>A JPEG Huffman encoding table: a symbol-to-(code,length) lookup built from bit-length counts and symbol values in code order.</summary>
internal sealed class HuffmanEncodingTable
{
    private readonly ushort[] _codes = new ushort[256];
    private readonly byte[] _lengths = new byte[256];

    private HuffmanEncodingTable(byte[] counts, byte[] values)
    {
        Counts = counts;
        Values = values;

        int code = 0;
        int valueIndex = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            for (int i = 0; i < count; i++)
            {
                byte symbol = values[valueIndex++];
                _codes[symbol] = (ushort)code;
                _lengths[symbol] = (byte)length;
                code++;
            }

            code <<= 1;
        }
    }

    /// <summary>The number of codes of each bit length 1-16 (BITS, 16 entries), as written to a DHT segment.</summary>
    public byte[] Counts { get; }

    /// <summary>The symbol values in code order (HUFFVAL), as written to a DHT segment.</summary>
    public byte[] Values { get; }

    /// <summary>Builds an encoding table from bit-length counts and symbol values in code order.</summary>
    public static HuffmanEncodingTable Build(byte[] counts, byte[] values) => new(counts, values);

    /// <summary>Writes the Huffman code for <paramref name="symbol"/> to <paramref name="writer"/>.</summary>
    public void Encode(JpegEntropyWriter writer, byte symbol) => writer.WriteBits(_codes[symbol], _lengths[symbol]);
}
