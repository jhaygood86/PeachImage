namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>A JBIG2 Huffman table (T.88 Annex B): the standard tables B.1-B.15, custom tables from table segments, and symbol-ID code tables.</summary>
internal sealed class Jbig2HuffmanTable
{
    private enum LineKind : byte
    {
        Normal,
        Lower,
        Upper,
        OutOfBand,
    }

    private readonly struct Line
    {
        public Line(int rangeLow, int prefixLength, int rangeLength, LineKind kind)
        {
            RangeLow = rangeLow;
            PrefixLength = prefixLength;
            RangeLength = rangeLength;
            Kind = kind;
        }

        public int RangeLow { get; }

        public int PrefixLength { get; }

        public int RangeLength { get; }

        public LineKind Kind { get; }
    }

    private static readonly Jbig2HuffmanTable?[] StandardTables = new Jbig2HuffmanTable?[16];

    private readonly Line[] _lines;
    private readonly int[] _sortedLines;
    private readonly int[] _firstCode;
    private readonly int[] _firstIndex;
    private readonly int[] _count;
    private readonly int _maxLength;
    private readonly uint[] _codes;

    private Jbig2HuffmanTable(Line[] lines)
    {
        _lines = lines;
        _codes = new uint[lines.Length];
        int maxLength = 0;
        foreach (var line in lines)
        {
            maxLength = Math.Max(maxLength, line.PrefixLength);
        }

        _maxLength = maxLength;
        _count = new int[maxLength + 2];
        foreach (var line in lines)
        {
            if (line.PrefixLength > 0)
            {
                _count[line.PrefixLength]++;
            }
        }

        _firstCode = new int[maxLength + 2];
        _firstIndex = new int[maxLength + 2];
        _sortedLines = new int[lines.Length];

        // T.88 B.3: codes are assigned in increasing prefix length, then in line order.
        int code = 0;
        int previousCount = 0;
        int index = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            code = (code + previousCount) << 1;
            previousCount = _count[length];

            _firstCode[length] = code;
            _firstIndex[length] = index;
            int assigned = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].PrefixLength == length)
                {
                    _sortedLines[index++] = i;
                    _codes[i] = (uint)(code + assigned);
                    assigned++;
                }
            }
        }
    }

    /// <summary>Returns standard table B.<paramref name="number"/> (1 to 15).</summary>
    public static Jbig2HuffmanTable Standard(int number)
    {
        if (number is < 1 or > 15)
        {
            throw new Jbig2DecodingException($"JBIG2 standard Huffman table {number} does not exist.");
        }

        return StandardTables[number] ??= new Jbig2HuffmanTable(BuildStandard(number));
    }

    /// <summary>Builds a table from a table segment (T.88 B.2).</summary>
    public static Jbig2HuffmanTable FromSegment(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
        {
            throw new Jbig2DecodingException("JBIG2 table segment is truncated.");
        }

        int flags = data[0];
        bool hasOutOfBand = (flags & 1) != 0;
        int prefixSize = ((flags >> 1) & 7) + 1;
        int rangeSize = ((flags >> 4) & 7) + 1;
        int low = ReadInt32(data[1..]);
        int high = ReadInt32(data[5..]);
        if (high < low)
        {
            throw new Jbig2DecodingException("JBIG2 table segment has an invalid range.");
        }

        var reader = new Jbig2BitReader(data[9..]);
        var lines = new List<Line>();
        long current = low;
        while (current < high)
        {
            int prefixLength = (int)reader.ReadBits(prefixSize);
            int rangeLength = (int)reader.ReadBits(rangeSize);
            lines.Add(new Line((int)current, prefixLength, rangeLength, LineKind.Normal));
            current += 1L << rangeLength;
            if (lines.Count > 1 << 16)
            {
                throw new Jbig2DecodingException("JBIG2 table segment declares too many lines.");
            }
        }

        lines.Add(new Line(low - 1, (int)reader.ReadBits(prefixSize), 32, LineKind.Lower));
        lines.Add(new Line(high, (int)reader.ReadBits(prefixSize), 32, LineKind.Upper));
        if (hasOutOfBand)
        {
            lines.Add(new Line(0, (int)reader.ReadBits(prefixSize), 0, LineKind.OutOfBand));
        }

        return new Jbig2HuffmanTable([.. lines]);
    }

    /// <summary>Builds a table that maps each code in turn to its own index from per-symbol prefix lengths (symbol-ID tables, T.88 7.4.3.1.7).</summary>
    public static Jbig2HuffmanTable FromPrefixLengths(ReadOnlySpan<int> lengths)
    {
        var lines = new Line[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            lines[i] = new Line(i, lengths[i], 0, LineKind.Normal);
        }

        return new Jbig2HuffmanTable(lines);
    }

    /// <summary>Exposes (rangeLow, prefix length, prefix code) of the table's lines so tests can compare against the code values printed in T.88 Annex B.</summary>
    internal IEnumerable<(int RangeLow, int PrefixLength, uint Code)> Describe()
    {
        for (int i = 0; i < _lines.Length; i++)
        {
            yield return (_lines[i].RangeLow, _lines[i].PrefixLength, _codes[i]);
        }
    }

    /// <summary>Decodes one value. Returns false for the out-of-band value.</summary>
    public bool Decode(ref Jbig2BitReader reader, out int value)
    {
        int code = 0;
        for (int length = 1; length <= _maxLength; length++)
        {
            code = (code << 1) | reader.ReadBit();
            int offset = code - _firstCode[length];
            if (_count[length] > 0 && offset >= 0 && offset < _count[length])
            {
                var line = _lines[_sortedLines[_firstIndex[length] + offset]];
                switch (line.Kind)
                {
                    case LineKind.OutOfBand:
                        value = 0;
                        return false;
                    case LineKind.Lower:
                        value = (int)(line.RangeLow - (long)reader.ReadBits(32));
                        return true;
                    case LineKind.Upper:
                        value = (int)(line.RangeLow + (long)reader.ReadBits(32));
                        return true;
                    default:
                        value = (int)(line.RangeLow + (long)reader.ReadBits(line.RangeLength));
                        return true;
                }
            }
        }

        throw new Jbig2DecodingException("JBIG2 data contains an invalid Huffman code.");
    }

    /// <summary>Decodes a value that must not be out-of-band.</summary>
    public int DecodeRequired(ref Jbig2BitReader reader)
    {
        if (!Decode(ref reader, out int value))
        {
            throw new Jbig2DecodingException("JBIG2 data contains an unexpected out-of-band value.");
        }

        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data) =>
        (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];

    private static Line[] BuildStandard(int number)
    {
        // Each row: rangeLow, prefixLength, rangeLength; lower-range, upper-range and out-of-band lines follow the normal lines.
        static Line N(int low, int prefix, int range) => new(low, prefix, range, LineKind.Normal);
        static Line L(int low, int prefix) => new(low, prefix, 32, LineKind.Lower);
        static Line U(int low, int prefix) => new(low, prefix, 32, LineKind.Upper);
        static Line O(int prefix) => new(0, prefix, 0, LineKind.OutOfBand);

        return number switch
        {
            1 => [N(0, 1, 4), N(16, 2, 8), N(272, 3, 16), U(65808, 3)],
            2 => [N(0, 1, 0), N(1, 2, 0), N(2, 3, 0), N(3, 4, 3), N(11, 5, 6), U(75, 6), O(6)],
            3 => [N(-256, 8, 8), N(0, 1, 0), N(1, 2, 0), N(2, 3, 0), N(3, 4, 3), N(11, 5, 6), L(-257, 8), U(75, 7), O(6)],
            4 => [N(1, 1, 0), N(2, 2, 0), N(3, 3, 0), N(4, 4, 3), N(12, 5, 6), U(76, 5)],
            5 => [N(-255, 7, 8), N(1, 1, 0), N(2, 2, 0), N(3, 3, 0), N(4, 4, 3), N(12, 5, 6), L(-256, 7), U(76, 6)],
            6 =>
            [
                N(-2048, 5, 10), N(-1024, 4, 9), N(-512, 4, 8), N(-256, 4, 7), N(-128, 5, 6), N(-64, 5, 5), N(-32, 4, 5),
                N(0, 2, 7), N(128, 3, 7), N(256, 3, 8), N(512, 4, 9), N(1024, 4, 10), L(-2049, 6), U(2048, 6),
            ],
            7 =>
            [
                N(-1024, 4, 9), N(-512, 3, 8), N(-256, 4, 7), N(-128, 5, 6), N(-64, 5, 5), N(-32, 4, 5), N(0, 4, 5),
                N(32, 5, 5), N(64, 5, 6), N(128, 4, 7), N(256, 3, 8), N(512, 3, 9), N(1024, 3, 10), L(-1025, 5), U(2048, 5),
            ],
            8 =>
            [
                N(-15, 8, 3), N(-7, 9, 1), N(-5, 8, 1), N(-3, 9, 0), N(-2, 7, 0), N(-1, 4, 0), N(0, 2, 1), N(2, 5, 0),
                N(3, 6, 0), N(4, 3, 4), N(20, 6, 1), N(22, 4, 4), N(38, 4, 5), N(70, 5, 6), N(134, 5, 7), N(262, 6, 7),
                N(390, 7, 8), N(646, 6, 10), L(-16, 9), U(1670, 9), O(2),
            ],
            9 =>
            [
                N(-31, 8, 4), N(-15, 9, 2), N(-11, 8, 2), N(-7, 9, 1), N(-5, 7, 1), N(-3, 4, 1), N(-1, 3, 1), N(1, 3, 1),
                N(3, 5, 1), N(5, 6, 1), N(7, 3, 5), N(39, 6, 2), N(43, 4, 5), N(75, 4, 6), N(139, 5, 7), N(267, 5, 8),
                N(523, 6, 8), N(779, 7, 9), N(1291, 6, 11), L(-32, 9), U(3339, 9), O(2),
            ],
            10 =>
            [
                N(-21, 7, 4), N(-5, 8, 0), N(-4, 7, 0), N(-3, 5, 0), N(-2, 2, 2), N(2, 5, 0), N(3, 6, 0), N(4, 7, 0),
                N(5, 8, 0), N(6, 2, 6), N(70, 5, 5), N(102, 6, 5), N(134, 6, 6), N(198, 6, 7), N(326, 6, 8), N(582, 6, 9),
                N(1094, 6, 10), N(2118, 7, 11), L(-22, 8), U(4166, 8), O(2),
            ],
            11 =>
            [
                N(1, 1, 0), N(2, 2, 1), N(4, 4, 0), N(5, 4, 1), N(7, 5, 1), N(9, 5, 2), N(13, 6, 2), N(17, 7, 2),
                N(21, 7, 3), N(29, 7, 4), N(45, 7, 5), N(77, 7, 6), U(141, 7),
            ],
            12 =>
            [
                N(1, 1, 0), N(2, 2, 0), N(3, 3, 1), N(5, 5, 0), N(6, 5, 1), N(8, 6, 1), N(10, 7, 0), N(11, 7, 1),
                N(13, 7, 2), N(17, 7, 3), N(25, 7, 4), N(41, 8, 5), U(73, 8),
            ],
            13 =>
            [
                N(1, 1, 0), N(2, 3, 0), N(3, 4, 0), N(4, 5, 0), N(5, 4, 1), N(7, 3, 3), N(15, 6, 1), N(17, 6, 2),
                N(21, 6, 3), N(29, 6, 4), N(45, 6, 5), N(77, 7, 6), U(141, 7),
            ],
            14 => [N(-2, 3, 0), N(-1, 3, 0), N(0, 1, 0), N(1, 3, 0), N(2, 3, 0)],
            _ =>
            [
                N(-24, 7, 4), N(-8, 6, 2), N(-4, 5, 1), N(-2, 4, 0), N(-1, 3, 0), N(0, 1, 0), N(1, 3, 0), N(2, 4, 0),
                N(3, 5, 1), N(5, 6, 2), N(9, 7, 4), L(-25, 7), U(25, 7),
            ],
        };
    }
}
