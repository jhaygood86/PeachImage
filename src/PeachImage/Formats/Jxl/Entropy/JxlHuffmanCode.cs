using System.Runtime.CompilerServices;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Entropy;

/// <summary>One entry of a Brotli-style two-level prefix-code decoding table.</summary>
internal struct HuffmanEntry
{
    /// <summary>Bits to consume; for a root-table pointer entry, root bits plus the second-level table's bit width.</summary>
    public byte Bits;

    /// <summary>The decoded symbol, or for a root pointer entry the offset to the second-level table.</summary>
    public ushort Value;
}

/// <summary>
/// A canonical prefix code as used by JPEG XL's entropy coding when <c>use_prefix_code</c> is set: read from the
/// bitstream as either a "simple" code (up to 5 symbols) or code lengths, then expanded into a two-level lookup
/// table (an 8-bit root table plus second-level tables).
/// </summary>
internal sealed class JxlHuffmanCode
{
    internal const int RootBits = 8;
    internal const int MaxBits = 15;

    private const int CodeLengthCodes = 18;
    private const byte DefaultCodeLength = 8;
    private const int CodeLengthRepeatCode = 16;

    private static readonly byte[] CodeLengthCodeOrder = [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // Static prefix code (bits, value) for the code length code lengths, indexed by 4 peeked bits.
    private static readonly (byte Bits, byte Value)[] CodeLengthCodeLengthTable =
    [
        (2, 0), (2, 4), (2, 3), (3, 2), (2, 0), (2, 4), (2, 3), (4, 1),
        (2, 0), (2, 4), (2, 3), (3, 2), (2, 0), (2, 4), (2, 3), (4, 5),
    ];

    private HuffmanEntry[] _table = [];

    /// <summary>A code over an alphabet of one symbol: consumes no bits and always yields 0.</summary>
    public static JxlHuffmanCode SingleSymbol() => new() { _table = new HuffmanEntry[1 << RootBits] };

    /// <summary>Reads a prefix code over <paramref name="alphabetSize"/> symbols. Returns null if the code is invalid.</summary>
    public static JxlHuffmanCode? Read(int alphabetSize, ref JxlBitReader br)
    {
        if (alphabetSize > (1 << MaxBits))
        {
            return null;
        }

        uint simpleCodeOrSkip = br.ReadBits(2);
        if (simpleCodeOrSkip == 1)
        {
            var simple = new HuffmanEntry[1 << RootBits];
            return ReadSimpleCode(alphabetSize, ref br, simple) ? new JxlHuffmanCode { _table = simple } : null;
        }

        byte[] codeLengths = new byte[alphabetSize];
        Span<byte> codeLengthCodeLengths = stackalloc byte[CodeLengthCodes];
        codeLengthCodeLengths.Clear();
        int space = 32;
        int numCodes = 0;
        for (int i = (int)simpleCodeOrSkip; i < CodeLengthCodes && space > 0; i++)
        {
            int codeLengthIndex = CodeLengthCodeOrder[i];
            var (bits, value) = CodeLengthCodeLengthTable[br.PeekBits(4)];
            br.Consume(bits);
            codeLengthCodeLengths[codeLengthIndex] = value;
            if (value != 0)
            {
                space -= 32 >> value;
                numCodes++;
            }
        }

        if (!(numCodes == 1 || space == 0) || !ReadCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths, ref br))
        {
            return null;
        }

        Span<ushort> counts = stackalloc ushort[MaxBits + 1];
        counts.Clear();
        for (int i = 0; i < alphabetSize; i++)
        {
            counts[codeLengths[i]]++;
        }

        var table = new HuffmanEntry[alphabetSize + 376];
        int size = BuildTable(table, RootBits, codeLengths, alphabetSize, counts);
        if (size == 0)
        {
            return null;
        }

        Array.Resize(ref table, size);
        return new JxlHuffmanCode { _table = table };
    }

    /// <summary>Decodes the next symbol.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSymbol(ref JxlBitReader br)
    {
        var table = _table;
        int index = (int)br.PeekBits(RootBits);
        var entry = table[index];
        int bits = entry.Bits;
        if (bits > RootBits)
        {
            br.Consume(RootBits);
            bits -= RootBits;
            index += entry.Value;
            index += (int)br.PeekBits(bits);
            entry = table[index];
        }

        br.Consume(entry.Bits);
        return entry.Value;
    }

    private static bool ReadCodeLengths(scoped ReadOnlySpan<byte> codeLengthCodeLengths, int numSymbols, Span<byte> codeLengths, ref JxlBitReader br)
    {
        int symbol = 0;
        byte prevCodeLength = DefaultCodeLength;
        int repeat = 0;
        byte repeatCodeLength = 0;
        int space = 32768;

        Span<ushort> counts = stackalloc ushort[MaxBits + 1];
        counts.Clear();
        for (int i = 0; i < CodeLengthCodes; i++)
        {
            counts[codeLengthCodeLengths[i]]++;
        }

        var table = new HuffmanEntry[32];
        if (BuildTable(table, 5, codeLengthCodeLengths, CodeLengthCodes, counts) == 0)
        {
            return false;
        }

        while (symbol < numSymbols && space > 0)
        {
            var entry = table[br.PeekBits(5)];
            br.Consume(entry.Bits);
            byte codeLength = (byte)entry.Value;
            if (codeLength < CodeLengthRepeatCode)
            {
                repeat = 0;
                codeLengths[symbol++] = codeLength;
                if (codeLength != 0)
                {
                    prevCodeLength = codeLength;
                    space -= 32768 >> codeLength;
                }
            }
            else
            {
                int extraBits = codeLength - 14;
                byte newLength = codeLength == CodeLengthRepeatCode ? prevCodeLength : (byte)0;
                if (repeatCodeLength != newLength)
                {
                    repeat = 0;
                    repeatCodeLength = newLength;
                }

                int oldRepeat = repeat;
                if (repeat > 0)
                {
                    repeat -= 2;
                    repeat <<= extraBits;
                }

                repeat += (int)br.ReadBits(extraBits) + 3;
                int repeatDelta = repeat - oldRepeat;
                if (symbol + repeatDelta > numSymbols)
                {
                    return false;
                }

                codeLengths.Slice(symbol, repeatDelta).Fill(repeatCodeLength);
                symbol += repeatDelta;
                if (repeatCodeLength != 0)
                {
                    space -= repeatDelta << (15 - repeatCodeLength);
                }
            }
        }

        if (space != 0)
        {
            return false;
        }

        codeLengths[symbol..numSymbols].Clear();
        return !br.IsOverrun;
    }

    private static bool ReadSimpleCode(int alphabetSize, ref JxlBitReader br, HuffmanEntry[] table)
    {
        int maxBits = alphabetSize > 1 ? System.Numerics.BitOperations.Log2((uint)(alphabetSize - 1)) + 1 : 0;
        int numSymbols = (int)br.ReadBits(2) + 1;

        Span<ushort> symbols = stackalloc ushort[4];
        symbols.Clear();
        for (int i = 0; i < numSymbols; i++)
        {
            ushort symbol = (ushort)br.ReadBits(maxBits);
            if (symbol >= alphabetSize)
            {
                return false;
            }

            symbols[i] = symbol;
        }

        for (int i = 0; i < numSymbols - 1; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (symbols[i] == symbols[j])
                {
                    return false;
                }
            }
        }

        // Four symbols have the option of a second tree shape.
        if (numSymbols == 4)
        {
            numSymbols += (int)br.ReadBits(1);
        }

        int tableSize = 1;
        switch (numSymbols)
        {
            case 1:
                table[0] = new HuffmanEntry { Bits = 0, Value = symbols[0] };
                break;
            case 2:
                if (symbols[0] > symbols[1])
                {
                    (symbols[0], symbols[1]) = (symbols[1], symbols[0]);
                }

                table[0] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[1] = new HuffmanEntry { Bits = 1, Value = symbols[1] };
                tableSize = 2;
                break;
            case 3:
                if (symbols[1] > symbols[2])
                {
                    (symbols[1], symbols[2]) = (symbols[2], symbols[1]);
                }

                table[0] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[2] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[1] = new HuffmanEntry { Bits = 2, Value = symbols[1] };
                table[3] = new HuffmanEntry { Bits = 2, Value = symbols[2] };
                tableSize = 4;
                break;
            case 4:
                for (int i = 0; i < 3; i++)
                {
                    for (int j = i + 1; j < 4; j++)
                    {
                        if (symbols[i] > symbols[j])
                        {
                            (symbols[i], symbols[j]) = (symbols[j], symbols[i]);
                        }
                    }
                }

                table[0] = new HuffmanEntry { Bits = 2, Value = symbols[0] };
                table[2] = new HuffmanEntry { Bits = 2, Value = symbols[1] };
                table[1] = new HuffmanEntry { Bits = 2, Value = symbols[2] };
                table[3] = new HuffmanEntry { Bits = 2, Value = symbols[3] };
                tableSize = 4;
                break;
            case 5:
                if (symbols[2] > symbols[3])
                {
                    (symbols[2], symbols[3]) = (symbols[3], symbols[2]);
                }

                table[0] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[1] = new HuffmanEntry { Bits = 2, Value = symbols[1] };
                table[2] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[3] = new HuffmanEntry { Bits = 3, Value = symbols[2] };
                table[4] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[5] = new HuffmanEntry { Bits = 2, Value = symbols[1] };
                table[6] = new HuffmanEntry { Bits = 1, Value = symbols[0] };
                table[7] = new HuffmanEntry { Bits = 3, Value = symbols[3] };
                tableSize = 8;
                break;
            default:
                return false;
        }

        int goal = 1 << RootBits;
        while (tableSize != goal)
        {
            Array.Copy(table, 0, table, tableSize, tableSize);
            tableSize <<= 1;
        }

        return !br.IsOverrun;
    }

    // Returns reverse(reverse(key, len) + 1, len), where reverse reverses the low len bits.
    private static int NextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (key & (step - 1)) + step;
    }

    private static void Replicate(HuffmanEntry[] table, int start, int step, int end, HuffmanEntry code)
    {
        do
        {
            end -= step;
            table[start + end] = code;
        }
        while (end > 0);
    }

    // The width of the next second-level table, given the histogram of remaining code lengths.
    private static int NextTableBitSize(ReadOnlySpan<ushort> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxBits)
        {
            if (left <= count[len])
            {
                break;
            }

            left -= count[len];
            len++;
            left <<= 1;
        }

        return len - rootBits;
    }

    /// <summary>Builds the two-level decoding table; returns its total size, or 0 if the code lengths are not a valid prefix code. <paramref name="count"/> is consumed.</summary>
    internal static int BuildTable(HuffmanEntry[] root, int rootBits, ReadOnlySpan<byte> codeLengths, int codeLengthsSize, Span<ushort> count)
    {
        if (codeLengthsSize > (1 << MaxBits))
        {
            return 0;
        }

        Span<ushort> offset = stackalloc ushort[MaxBits + 1];
        offset.Clear();
        Span<ushort> sorted = codeLengthsSize <= 1024 ? stackalloc ushort[codeLengthsSize] : new ushort[codeLengthsSize];

        int maxLength = 1;
        ushort sum = 0;
        for (int len = 1; len <= MaxBits; len++)
        {
            offset[len] = sum;
            if (count[len] != 0)
            {
                sum = (ushort)(sum + count[len]);
                maxLength = len;
            }
        }

        for (int symbol = 0; symbol < codeLengthsSize; symbol++)
        {
            if (codeLengths[symbol] != 0)
            {
                sorted[offset[codeLengths[symbol]]++] = (ushort)symbol;
            }
        }

        int tableBase = 0;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;

        // A code with a single value consumes no bits.
        if (offset[MaxBits] == 1)
        {
            var single = new HuffmanEntry { Bits = 0, Value = sorted[0] };
            for (int key = 0; key < totalSize; key++)
            {
                root[key] = single;
            }

            return totalSize;
        }

        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }

        int keyBits = 0;
        int symbolIndex = 0;
        var code = new HuffmanEntry { Bits = 1 };
        int step = 2;
        do
        {
            for (; count[code.Bits] != 0; --count[code.Bits])
            {
                code.Value = sorted[symbolIndex++];
                Replicate(root, tableBase + keyBits, step, tableSize, code);
                keyBits = NextKey(keyBits, code.Bits);
            }

            step <<= 1;
        }
        while (++code.Bits <= tableBits);

        // If rootBits != tableBits only a fraction of the table was built; replicate it.
        while (totalSize != tableSize)
        {
            Array.Copy(root, 0, root, tableSize, tableSize);
            tableSize <<= 1;
        }

        // Second-level tables, pointed to from the root table.
        int mask = totalSize - 1;
        int low = -1;
        step = 2;
        for (int len = rootBits + 1; len <= maxLength; len++, step <<= 1)
        {
            for (; count[len] != 0; --count[len])
            {
                if ((keyBits & mask) != low)
                {
                    tableBase += tableSize;
                    tableBits = NextTableBitSize(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = keyBits & mask;
                    root[low].Bits = (byte)(tableBits + rootBits);
                    root[low].Value = (ushort)(tableBase - low);
                }

                code.Bits = (byte)(len - rootBits);
                code.Value = sorted[symbolIndex++];
                Replicate(root, tableBase + (keyBits >> rootBits), step, tableSize, code);
                keyBits = NextKey(keyBits, len);
            }
        }

        return totalSize;
    }
}
