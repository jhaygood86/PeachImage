using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Writes one Huffman code definition — the write-side mirror of
/// <see cref="Decoding.Vp8L.Vp8LCodeLengthReader"/>: either a "simple" code (1-2 symbols given directly) or
/// a "normal" code (a full canonical tree transmitted via a DEFLATE-style nested code-length encoding).
/// Called once per alphabet (green/red/blue/alpha/distance) by <see cref="Vp8LImageStreamWriter"/>, plus
/// once more for each transform parameter sub-image.
/// </summary>
internal static class Vp8LCodeLengthWriter
{
    private const int NumCodeLengthCodes = 19;

    /// <summary>Fixed transmission order of the 19 code-length-code lengths — must match <see cref="Decoding.Vp8L.Vp8LCodeLengthReader"/>'s <c>CodeLengthCodeOrder</c> exactly.</summary>
    private static readonly int[] CodeLengthCodeOrder =
        [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private const int RepeatPrevious = 16;
    private const int RepeatZeroShort = 17;
    private const int RepeatZeroLong = 18;

    /// <summary>
    /// Builds code lengths for <paramref name="freq"/> via <see cref="Vp8LHuffmanCodeBuilder"/>, writes the
    /// resulting Huffman code definition to <paramref name="writer"/>, and fills <paramref name="codes"/>
    /// with each used symbol's LSB-first code (and, for the degenerate single-symbol case, overwrites
    /// <paramref name="codeLengths"/>'s entry to 0 — a lone used symbol's decode-table lookup always consumes
    /// zero bits regardless of what length value was nominally assigned to it, per
    /// <see cref="Decoding.Vp8L.Vp8LHuffmanTableBuilder"/>'s single-symbol special case).
    /// </summary>
    public static void WriteHuffmanCode(Vp8LBitWriter writer, ReadOnlySpan<int> freq, Span<int> codeLengths, Span<uint> codes)
    {
        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq, codeLengths, WebpDecodingLimits.MaxHuffmanCodeLength);

        int usedCount = 0;
        int firstSymbol = -1;
        int secondSymbol = -1;
        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            if (codeLengths[symbol] == 0)
            {
                continue;
            }

            usedCount++;
            if (firstSymbol < 0)
            {
                firstSymbol = symbol;
            }
            else if (secondSymbol < 0)
            {
                secondSymbol = symbol;
            }
        }

        bool canUseSimple = usedCount switch
        {
            1 => firstSymbol <= 255,
            2 => firstSymbol <= 255 && secondSymbol <= 255,
            _ => false,
        };

        if (canUseSimple)
        {
            WriteSimpleForm(writer, usedCount, firstSymbol, secondSymbol);
        }
        else
        {
            WriteNormalForm(writer, codeLengths);
        }

        codes.Clear();
        if (usedCount == 1)
        {
            codeLengths[firstSymbol] = 0;
            codes[firstSymbol] = 0;
        }
        else
        {
            Vp8LHuffmanCodeBuilder.AssignCanonicalCodes(codeLengths, codes);
        }
    }

    private static void WriteSimpleForm(Vp8LBitWriter writer, int usedCount, int firstSymbol, int secondSymbol)
    {
        writer.WriteBits(1, 1); // Simple-code flag.
        writer.WriteBits((uint)(usedCount - 1), 1);

        bool firstIs8Bit = firstSymbol > 1;
        writer.WriteBits(firstIs8Bit ? 1u : 0u, 1);
        writer.WriteBits((uint)firstSymbol, firstIs8Bit ? 8 : 1);

        if (usedCount == 2)
        {
            writer.WriteBits((uint)secondSymbol, 8);
        }
    }

    private static void WriteNormalForm(Vp8LBitWriter writer, ReadOnlySpan<int> codeLengths)
    {
        var tokens = new List<Vp8LCodeLengthToken>();
        var freq19 = new int[NumCodeLengthCodes];
        BuildRleTokens(codeLengths, tokens, freq19);

        var codeLengthCodeLengths = new int[NumCodeLengthCodes];
        Vp8LHuffmanCodeBuilder.BuildCodeLengths(freq19, codeLengthCodeLengths, Vp8LEncodingLimits.MaxCodeLengthCodeLength);

        var codeLengthCodes = new uint[NumCodeLengthCodes];
        Vp8LHuffmanCodeBuilder.AssignCanonicalCodes(codeLengthCodeLengths, codeLengthCodes);

        writer.WriteBits(0, 1); // Normal-code flag.

        int numCodes = 4;
        for (int k = NumCodeLengthCodes - 1; k >= 0; k--)
        {
            if (codeLengthCodeLengths[CodeLengthCodeOrder[k]] != 0)
            {
                numCodes = Math.Max(4, k + 1);
                break;
            }
        }

        writer.WriteBits((uint)(numCodes - 4), 4);
        for (int k = 0; k < numCodes; k++)
        {
            writer.WriteBits((uint)codeLengthCodeLengths[CodeLengthCodeOrder[k]], 3);
        }

        writer.WriteBits(0, 1); // "Use bounded max symbol" -- always skipped in this encoder.

        foreach (var token in tokens)
        {
            writer.WriteBits(codeLengthCodes[token.Symbol], codeLengthCodeLengths[token.Symbol]);
            if (token.ExtraBits > 0)
            {
                writer.WriteBits(token.Extra, token.ExtraBits);
            }
        }
    }

    /// <summary>
    /// RLE-encodes <paramref name="codeLengths"/> into the 19-symbol code-length-code token stream, mirroring
    /// <see cref="Decoding.Vp8L.Vp8LCodeLengthReader"/>'s repeat semantics in reverse. A repeat-previous
    /// (16) token is only ever emitted immediately after a literal of the same value, within the same run --
    /// so, unlike the reader, no explicit "previous code length" state needs tracking here; the adjacency
    /// itself guarantees the decoder's own <c>previousCodeLength</c> will already equal the right value.
    /// </summary>
    private static void BuildRleTokens(ReadOnlySpan<int> codeLengths, List<Vp8LCodeLengthToken> tokens, int[] freq19)
    {
        int i = 0;
        int n = codeLengths.Length;

        while (i < n)
        {
            int value = codeLengths[i];
            int runLength = 1;
            while (i + runLength < n && codeLengths[i + runLength] == value)
            {
                runLength++;
            }

            if (value == 0)
            {
                int remaining = runLength;
                while (remaining > 0)
                {
                    if (remaining >= 11)
                    {
                        int take = Math.Min(remaining, 138);
                        Emit(RepeatZeroLong, (uint)(take - 11), 7, tokens, freq19);
                        remaining -= take;
                    }
                    else if (remaining >= 3)
                    {
                        int take = Math.Min(remaining, 10);
                        Emit(RepeatZeroShort, (uint)(take - 3), 3, tokens, freq19);
                        remaining -= take;
                    }
                    else
                    {
                        Emit(0, 0, 0, tokens, freq19);
                        remaining--;
                    }
                }
            }
            else
            {
                Emit(value, 0, 0, tokens, freq19);
                int remaining = runLength - 1;
                while (remaining > 0)
                {
                    if (remaining >= 3)
                    {
                        int take = Math.Min(remaining, 6);
                        Emit(RepeatPrevious, (uint)(take - 3), 2, tokens, freq19);
                        remaining -= take;
                    }
                    else
                    {
                        Emit(value, 0, 0, tokens, freq19);
                        remaining--;
                    }
                }
            }

            i += runLength;
        }
    }

    private static void Emit(int symbol, uint extra, int extraBits, List<Vp8LCodeLengthToken> tokens, int[] freq19)
    {
        tokens.Add(new Vp8LCodeLengthToken(symbol, extra, extraBits));
        freq19[symbol]++;
    }

    private readonly record struct Vp8LCodeLengthToken(int Symbol, uint Extra, int ExtraBits);
}
