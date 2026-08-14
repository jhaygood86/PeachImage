using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>
/// A JPEG Huffman decoding table, built per ITU-T.81 Annex C/F.2.2.3 from a bit-length-count array plus the
/// symbol values in code order. Most real-world Huffman codes are short (the whole point of Huffman coding
/// is that common symbols get short codes), so decoding first tries an O(1) lookup against every code of
/// length &lt;= <see cref="FastTableBits"/> via a lookahead table (the standard "fast Huffman" technique used
/// by essentially every performance-oriented JPEG decoder); only longer codes fall back to the bit-by-bit
/// walk that exactly follows the spec's reference decode procedure. <see cref="FastTableBits"/> is 10, not
/// the more commonly-cited 8: benchmarked against real decode workloads, 10 measurably beat 8 (fewer codes
/// fall through to the slow path) while 12 measured no further improvement over 10 — codes needing more
/// than 10 bits of lookahead are rare enough in practice that widening past that point mostly just grows
/// the table (and its one-time per-scan build cost) without resolving any more symbols in the fast path.
/// </summary>
internal sealed class HuffmanDecodingTable
{
    private const int FastTableBits = 10;
    private const int FastTableSize = 1 << FastTableBits;

    private readonly int[] _minCode = new int[17];
    private readonly int[] _maxCode = new int[17];
    private readonly int[] _valPtr = new int[17];
    private readonly byte[] _values;
    private readonly short[] _fastSymbol = new short[FastTableSize];
    private readonly byte[] _fastLength = new byte[FastTableSize];

    private HuffmanDecodingTable(int[] minCode, int[] maxCode, int[] valPtr, byte[] values)
    {
        _minCode = minCode;
        _maxCode = maxCode;
        _valPtr = valPtr;
        _values = values;

        Array.Fill(_fastSymbol, (short)-1);
        BuildFastTable();
    }

    /// <summary>Builds a decoding table from a parsed DHT table specification.</summary>
    public static HuffmanDecodingTable Build(JpegHuffmanTableSpec spec) => Build(spec.Counts, spec.Values);

    /// <summary>Builds a decoding table from raw bit-length counts (16 entries, for lengths 1-16) and symbol values in code order.</summary>
    public static HuffmanDecodingTable Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        var minCode = new int[17];
        var maxCode = new int[17];
        var valPtr = new int[17];
        Array.Fill(maxCode, -1);

        int code = 0;
        int valueIndex = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            if (count > 0)
            {
                valPtr[length] = valueIndex;
                minCode[length] = code;
                code += count - 1;
                maxCode[length] = code;

                // A valid Huffman code of this length must fit in `length` bits (the Kraft inequality: no
                // bit-length may claim more codes than the binary tree has room for at that depth). A
                // malformed/adversarial DHT segment can violate this, which would otherwise silently produce
                // an internally-inconsistent min/max-code table that indexes out of bounds later.
                if (maxCode[length] >= (1 << length))
                {
                    throw new JpegDecodingException($"Invalid Huffman table: bit-length {length} claims more codes than fit in {length} bits.");
                }

                valueIndex += count;
                code++;
            }

            code <<= 1;
        }

        return new HuffmanDecodingTable(minCode, maxCode, valPtr, values.ToArray());
    }

    /// <summary>Decodes the next Huffman-coded symbol from <paramref name="reader"/>.</summary>
    public int Decode(JpegEntropyReader reader)
    {
        int peeked = reader.PeekBits(FastTableBits);
        int fastLength = _fastLength[peeked];
        if (fastLength != 0)
        {
            reader.GetBits(fastLength);
            return _fastSymbol[peeked];
        }

        return DecodeSlow(reader);
    }

    private int DecodeSlow(JpegEntropyReader reader)
    {
        int code = reader.GetBits(1);
        int length = 1;

        while (code > _maxCode[length])
        {
            length++;
            if (length > 16)
            {
                throw new JpegDecodingException("Invalid Huffman code encountered while decoding entropy-coded data.");
            }

            code = (code << 1) | reader.GetBits(1);
        }

        return _values[_valPtr[length] + (code - _minCode[length])];
    }

    private void BuildFastTable()
    {
        for (int length = 1; length <= FastTableBits; length++)
        {
            if (_maxCode[length] == -1)
            {
                continue;
            }

            int shift = FastTableBits - length;
            for (int code = _minCode[length]; code <= _maxCode[length]; code++)
            {
                byte symbol = _values[_valPtr[length] + (code - _minCode[length])];
                int baseIndex = code << shift;
                int fillCount = 1 << shift;
                for (int fill = 0; fill < fillCount; fill++)
                {
                    _fastSymbol[baseIndex | fill] = symbol;
                    _fastLength[baseIndex | fill] = (byte)length;
                }
            }
        }
    }
}
