namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Reads one Huffman code definition (a "simple" code — 1-2 symbols given directly, no canonical-code
/// machinery — or a "normal" code — a full canonical tree built via a DEFLATE-style nested code-length
/// encoding) and returns the resulting decoding <see cref="Vp8LHuffmanTable"/>. VP8L calls this once per tree
/// per <see cref="Vp8LHuffmanGroup"/> (five alphabets: green, red, blue, alpha, distance), plus once more for
/// each meta-Huffman sub-image and transform parameter sub-image.
/// </summary>
internal static class Vp8LCodeLengthReader
{
    private const int NumCodeLengthCodes = 19;

    /// <summary>Fixed transmission order of the 19 code-length-code lengths — verified against libwebp's <c>kCodeLengthCodeOrder</c> (<c>src/dec/vp8l_dec.c</c>).</summary>
    private static readonly int[] CodeLengthCodeOrder =
        [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private const int DefaultCodeLength = 8;

    /// <summary>Symbols &lt; 16 are literal code lengths; 16/17/18 are the DEFLATE-style repeat codes handled below.</summary>
    private const int CodeLengthLiterals = 16;

    /// <summary>Extra bits read for repeat codes 16/17/18, indexed by <c>symbol - 16</c>.</summary>
    private static readonly int[] RepeatExtraBits = [2, 3, 7];

    /// <summary>Base repeat count added to the extra bits for repeat codes 16/17/18, indexed by <c>symbol - 16</c>.</summary>
    private static readonly int[] RepeatOffsets = [3, 3, 11];

    public static Vp8LHuffmanTable ReadHuffmanCode(Vp8LBitReader reader, int alphabetSize)
    {
        var codeLengths = new int[alphabetSize];
        bool simpleCode = reader.ReadBits(1) != 0;

        if (simpleCode)
        {
            int numSymbols = (int)reader.ReadBits(1) + 1;
            bool firstSymbolIs8Bit = reader.ReadBits(1) != 0;
            int symbol = (int)reader.ReadBits(firstSymbolIs8Bit ? 8 : 1);
            RequireInRange(symbol, alphabetSize);
            codeLengths[symbol] = 1;

            if (numSymbols == 2)
            {
                int symbol2 = (int)reader.ReadBits(8);
                RequireInRange(symbol2, alphabetSize);
                codeLengths[symbol2] = 1;
            }
        }
        else
        {
            Span<int> codeLengthCodeLengths = stackalloc int[NumCodeLengthCodes];
            int numCodes = (int)reader.ReadBits(4) + 4;
            for (int i = 0; i < numCodes; i++)
            {
                codeLengthCodeLengths[CodeLengthCodeOrder[i]] = (int)reader.ReadBits(3);
            }

            var lengthsTable = Vp8LHuffmanTableBuilder.Build(codeLengthCodeLengths.ToArray(), Vp8LHuffmanTableBuilder.LengthsRootBits);
            ReadCodeLengthsWithTable(reader, lengthsTable, alphabetSize, codeLengths);
        }

        return Vp8LHuffmanTableBuilder.Build(codeLengths, Vp8LHuffmanTableBuilder.MainRootBits);
    }

    /// <summary>The "normal" case's second half: uses the just-built 19-symbol code-length table to decode <paramref name="numSymbols"/> real code lengths, expanding DEFLATE-style repeat codes 16/17/18 along the way.</summary>
    private static void ReadCodeLengthsWithTable(Vp8LBitReader reader, Vp8LHuffmanTable lengthsTable, int numSymbols, int[] codeLengths)
    {
        int previousCodeLength = DefaultCodeLength;
        int maxSymbol;

        if (reader.ReadBits(1) != 0)
        {
            int lengthNBits = 2 + (2 * (int)reader.ReadBits(3));
            maxSymbol = 2 + (int)reader.ReadBits(lengthNBits);
            if (maxSymbol > numSymbols)
            {
                throw new WebpDecodingException("Invalid VP8L Huffman code length stream: declared max symbol exceeds the alphabet size.");
            }
        }
        else
        {
            maxSymbol = numSymbols;
        }

        int symbolIndex = 0;
        while (symbolIndex < numSymbols)
        {
            if (maxSymbol-- == 0)
            {
                break;
            }

            int codeLength = lengthsTable.Decode(reader);

            if (codeLength < CodeLengthLiterals)
            {
                codeLengths[symbolIndex++] = codeLength;
                if (codeLength != 0)
                {
                    previousCodeLength = codeLength;
                }
            }
            else
            {
                bool useRepeatOfPrevious = codeLength == CodeLengthLiterals;
                int slot = codeLength - CodeLengthLiterals;
                int extraBits = RepeatExtraBits[slot];
                int repeatOffset = RepeatOffsets[slot];
                int repeat = (int)reader.ReadBits(extraBits) + repeatOffset;

                if (symbolIndex + repeat > numSymbols)
                {
                    throw new WebpDecodingException("Invalid VP8L Huffman code length stream: repeat run overruns the alphabet.");
                }

                int fillLength = useRepeatOfPrevious ? previousCodeLength : 0;
                while (repeat-- > 0)
                {
                    codeLengths[symbolIndex++] = fillLength;
                }
            }
        }
    }

    private static void RequireInRange(int symbol, int alphabetSize)
    {
        if ((uint)symbol >= (uint)alphabetSize)
        {
            throw new WebpDecodingException("Invalid VP8L simple Huffman code: symbol index out of range for its alphabet.");
        }
    }
}
