using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// A second hand-built end-to-end fixture, this one exercising the pieces
/// <see cref="Vp8LDecoderTests"/>'s pure-literal fixture cannot reach: a backward (LZ77) reference and its
/// overlapping copy, via a "normal" (DEFLATE-style repeat-coded) Huffman tree for the green channel — a
/// length code (green symbol &gt;= 256) can only ever be represented that way, since VP8L's "simple code"
/// shorthand tops out at an 8-bit (0-255) symbol value.
/// </summary>
public class Vp8LDecoderBackwardReferenceTests
{
    private static readonly int[] CodeLengthCodeOrder =
        [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    [Fact]
    public void Decode_BackwardReference_OverlappingCopy_RepeatsTheSourcePixelThroughout()
    {
        // 4x1 image. Pixel 0 is a literal (green=99); pixels 1-3 are one backward reference of length 3 at
        // distance 1 -- the classic overlapping/flat-run LZ77 case -- so all four pixels end up identical.
        var writer = new BitWriter();

        writer.WriteBits(3, 14); // width - 1 = 3 -> width = 4
        writer.WriteBits(0, 14); // height - 1 = 0 -> height = 1
        writer.WriteBits(0, 1); // alpha_is_used = false
        writer.WriteBits(0, 3); // version

        writer.WriteBits(0, 1); // transform_present
        writer.WriteBits(0, 1); // color_cache_present
        writer.WriteBits(0, 1); // use_meta_huffman

        // GREEN: symbols 99 (literal) and 258 (length code -> length symbol 2 -> DecodePrefixCodeValue = 3,
        // no extra bits since 2 < 4). Symbol 258 exceeds a "simple code"'s 8-bit ceiling, so this needs a
        // real (repeat-coded) code-length declaration.
        int[] greenLengths = new int[280];
        greenLengths[99] = 1;
        greenLengths[258] = 1;
        WriteNormalCode(writer, greenLengths);

        WriteSimpleOneSymbolCode(writer, 10, use8Bit: true); // RED: constant.
        WriteSimpleOneSymbolCode(writer, 30, use8Bit: true); // BLUE: constant.
        WriteSimpleOneSymbolCode(writer, 0, use8Bit: false); // ALPHA: constant, unused by Rgb24 output.

        // DISTANCE: a single symbol, 1. DecodePrefixCodeValue(1) = 2 directly (symbol < 4, no extra bits),
        // and PlaneCodeToDistance maps plane code 2 -> CodeToPlane[1]=0x07 -> yOffset=0, xOffset=1 ->
        // distance = 1, independent of image width.
        WriteSimpleOneSymbolCode(writer, 1, use8Bit: false);

        // Pixel stream: pixel 0 is the literal green=99 (green tree code "0", the shorter of its two codes --
        // see the canonical assignment inside WriteNormalCode). Pixels 1-3 come from one backward reference:
        // green symbol 258 (the length code), then the (bitless) distance symbol.
        WriteGreenSymbol(writer, greenLengths, symbol: 99);
        WriteGreenSymbol(writer, greenLengths, symbol: 258);
        // No bits for the distance symbol -- its tree is a trivial, zero-bit single-symbol table.

        byte[] chunk = BuildChunk(writer);

        var image = Vp8LDecoder.Decode(chunk);

        Assert.Equal(4, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);

        var pixels = image.GetPixelSpan();
        for (int i = 0; i < 4; i++)
        {
            int offset = i * 3;
            Assert.Equal(10, pixels[offset]);
            Assert.Equal(99, pixels[offset + 1]);
            Assert.Equal(30, pixels[offset + 2]);
        }
    }

    private static void WriteSimpleOneSymbolCode(BitWriter writer, int symbol, bool use8Bit)
    {
        writer.WriteBits(1, 1); // simple_code = true
        writer.WriteBits(0, 1); // num_symbols - 1 = 0 -> 1 symbol
        writer.WriteBits(use8Bit ? 1u : 0u, 1);
        writer.WriteBits((uint)symbol, use8Bit ? 8 : 1);
    }

    /// <summary>
    /// Encodes <paramref name="codeLengths"/> (one length per alphabet symbol; 0 = unused) as a "normal"
    /// Huffman code declaration: a 19-symbol code-length-code tree (built here via a simple, independent
    /// greedy run-length encoder — zero runs of 11 or more become a single repeat-zero-long (18) token,
    /// everything else a literal code-length token), then the resulting token stream.
    /// </summary>
    private static void WriteNormalCode(BitWriter writer, int[] codeLengths)
    {
        writer.WriteBits(0, 1); // simple_code = false

        var tokens = new List<(int ClcSymbol, int ExtraValue, int ExtraBits)>();
        int i = 0;
        while (i < codeLengths.Length)
        {
            if (codeLengths[i] == 0)
            {
                int run = 0;
                while (i + run < codeLengths.Length && codeLengths[i + run] == 0 && run < 138)
                {
                    run++;
                }

                if (run >= 11)
                {
                    tokens.Add((18, run - 11, 7)); // repeat-zero-long: extra 7 bits, base offset 11.
                    i += run;
                    continue;
                }

                tokens.Add((0, -1, 0)); // literal code-length 0.
                i++;
                continue;
            }

            tokens.Add((codeLengths[i], -1, 0)); // literal code-length (our test only ever uses 1).
            i++;
        }

        var usedSymbols = tokens.Select(t => t.ClcSymbol).Distinct().OrderBy(s => s).ToList();
        int[] clcLengths = AssignSimpleLengths(usedSymbols.Count);

        int[] codeLengthCodeLengths = new int[19];
        for (int s = 0; s < usedSymbols.Count; s++)
        {
            codeLengthCodeLengths[usedSymbols[s]] = clcLengths[s];
        }

        writer.WriteBits(15, 4); // numCodes - 4 = 15 -> numCodes = 19 (transmit every position).
        foreach (int orderSymbol in CodeLengthCodeOrder)
        {
            writer.WriteBits((uint)codeLengthCodeLengths[orderSymbol], 3);
        }

        writer.WriteBits(0, 1); // "use length" flag = false.

        var canonicalCodes = AssignCanonicalCodes(usedSymbols, clcLengths);
        foreach (var (clcSymbol, extraValue, extraBits) in tokens)
        {
            var (key, length) = canonicalCodes[clcSymbol];
            writer.WriteBits(key, length);
            if (extraBits > 0)
            {
                writer.WriteBits((uint)extraValue, extraBits);
            }
        }
    }

    /// <summary>Writes the pixel-stream code for a single green-alphabet <paramref name="symbol"/>, using the same canonical assignment <see cref="WriteNormalCode"/> declared for it.</summary>
    private static void WriteGreenSymbol(BitWriter writer, int[] greenCodeLengths, int symbol)
    {
        var usedSymbols = new List<int>();
        for (int s = 0; s < greenCodeLengths.Length; s++)
        {
            if (greenCodeLengths[s] > 0)
            {
                usedSymbols.Add(s);
            }
        }

        int[] lengths = new int[usedSymbols.Count];
        for (int s = 0; s < usedSymbols.Count; s++)
        {
            lengths[s] = greenCodeLengths[usedSymbols[s]];
        }

        var codes = AssignCanonicalCodes(usedSymbols, lengths);
        var (key, length) = codes[symbol];
        writer.WriteBits(key, length);
    }

    /// <summary>A simple, always-valid (Kraft-exact) length assignment for a small number of equally-weighted symbols.</summary>
    private static int[] AssignSimpleLengths(int symbolCount) => symbolCount switch
    {
        2 => [1, 1],
        3 => [1, 2, 2],
        4 => [2, 2, 2, 2],
        _ => throw new NotSupportedException($"Test helper does not support {symbolCount} distinct code-length-code symbols."),
    };

    /// <summary>Standard canonical Huffman code assignment (ascending length, then ascending symbol), bit-reversed per symbol for LSB-first transmission -- the same construction independently cross-checked in <see cref="Vp8LHuffmanTableBuilderTests"/>.</summary>
    private static Dictionary<int, (uint Key, int Length)> AssignCanonicalCodes(List<int> symbols, int[] symbolLengths)
    {
        int maxLength = symbolLengths.Max();
        var byLength = new List<int>[maxLength + 1];
        for (int len = 0; len <= maxLength; len++)
        {
            byLength[len] = [];
        }

        for (int s = 0; s < symbols.Count; s++)
        {
            byLength[symbolLengths[s]].Add(symbols[s]);
        }

        var result = new Dictionary<int, (uint, int)>();
        uint code = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            foreach (int symbol in byLength[length].OrderBy(s => s))
            {
                result[symbol] = (ReverseBits(code, length), length);
                code++;
            }

            code <<= 1;
        }

        return result;
    }

    private static uint ReverseBits(uint value, int bitCount)
    {
        uint result = 0;
        for (int i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }

    private static byte[] BuildChunk(BitWriter writer)
    {
        byte[] body = writer.ToBytes();
        byte[] chunk = new byte[1 + body.Length];
        chunk[0] = 0x2F;
        body.CopyTo(chunk, 1);
        return chunk;
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public void WriteBits(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes()
        {
            int byteCount = (_bits.Count + 7) / 8;
            var bytes = new byte[byteCount];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return bytes;
        }
    }
}
