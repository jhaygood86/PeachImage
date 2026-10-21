using PeachImage.Formats.Jbig2.Internal;
using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>Everything the text region decoding procedure needs (T.88 Table 9).</summary>
internal sealed class Jbig2TextRegionParameters
{
    public bool Huffman { get; init; }

    public bool Refine { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int InstanceCount { get; init; }

    public int Strips { get; init; } = 1;

    public int LogStrips { get; init; }

    public required IReadOnlyList<Jbig2Bitmap> Symbols { get; init; }

    public int SymbolCodeLength { get; init; }

    /// <summary>For Huffman coding, the table mapping codes to symbol IDs; when null the ID is a plain <see cref="SymbolCodeLength"/>-bit number.</summary>
    public Jbig2HuffmanTable? SymbolCodes { get; init; }

    public int DefaultPixel { get; init; }

    public int CombinationOperator { get; init; }

    public bool Transposed { get; init; }

    /// <summary>0 bottom-left, 1 top-left, 2 bottom-right, 3 top-right.</summary>
    public int ReferenceCorner { get; init; } = 1;

    public int DeltaSOffset { get; init; }

    public int RefinementTemplate { get; init; }

    public sbyte[] RefinementAdaptive { get; init; } = [];

    public Jbig2HuffmanTable? FirstS { get; init; }

    public Jbig2HuffmanTable? DeltaS { get; init; }

    public Jbig2HuffmanTable? DeltaT { get; init; }

    public Jbig2HuffmanTable? RefinementDeltaWidth { get; init; }

    public Jbig2HuffmanTable? RefinementDeltaHeight { get; init; }

    public Jbig2HuffmanTable? RefinementDeltaX { get; init; }

    public Jbig2HuffmanTable? RefinementDeltaY { get; init; }

    public Jbig2HuffmanTable? RefinementSize { get; init; }
}

/// <summary>Text region decoding (T.88 6.4), arithmetic or Huffman coded.</summary>
internal static class Jbig2TextRegion
{
    /// <summary>Decodes a text region. Arithmetic coding uses <paramref name="decoder"/> and <paramref name="contexts"/>; Huffman coding uses <paramref name="reader"/>.</summary>
    public static Jbig2Bitmap Decode(
        Jbig2TextRegionParameters p,
        ref MqDecoder decoder,
        ref Jbig2BitReader reader,
        Jbig2ArithmeticContexts? contexts,
        Span<byte> refinementContexts)
    {
        if (p.InstanceCount > Jbig2DecodingLimits.MaxInstances)
        {
            throw new Jbig2DecodingException("JBIG2 text region declares too many instances.");
        }

        var region = new Jbig2Bitmap(p.Width, p.Height, (byte)p.DefaultPixel);
        bool huffman = p.Huffman;

        int stripT = -(huffman ? p.DeltaT!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.DeltaT)) * p.Strips;
        int firstS = 0;
        int instances = 0;
        while (instances < p.InstanceCount)
        {
            int deltaT = huffman ? p.DeltaT!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.DeltaT);
            stripT += deltaT * p.Strips;

            bool first = true;
            int currentS = 0;
            while (true)
            {
                if (first)
                {
                    int deltaFirst = huffman ? p.FirstS!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.FirstS);
                    firstS += deltaFirst;
                    currentS = firstS;
                    first = false;
                }
                else
                {
                    bool inBand = huffman
                        ? p.DeltaS!.Decode(ref reader, out int deltaS)
                        : Jbig2ArithmeticContexts.DecodeInteger(ref decoder, contexts!.DeltaS, out deltaS);
                    if (!inBand)
                    {
                        break;
                    }

                    currentS += deltaS + p.DeltaSOffset;
                }

                if (instances >= p.InstanceCount)
                {
                    throw new Jbig2DecodingException("JBIG2 text region contains more instances than declared.");
                }

                int currentT = 0;
                if (p.Strips > 1)
                {
                    currentT = huffman
                        ? (int)reader.ReadBits(p.LogStrips)
                        : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.CurrentT);
                }

                int t = stripT + currentT;

                int id;
                if (huffman)
                {
                    id = p.SymbolCodes is not null ? p.SymbolCodes.DecodeRequired(ref reader) : (int)reader.ReadBits(p.SymbolCodeLength);
                }
                else
                {
                    id = contexts!.DecodeSymbolId(ref decoder);
                }

                if ((uint)id >= (uint)p.Symbols.Count)
                {
                    throw new Jbig2DecodingException("JBIG2 text region refers to a symbol that does not exist.");
                }

                var symbol = p.Symbols[id];
                bool refined = false;
                if (p.Refine)
                {
                    refined = (huffman ? reader.ReadBit() : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.RefinementIndicator)) != 0;
                }

                if (refined)
                {
                    symbol = DecodeRefinedSymbol(p, symbol, ref decoder, ref reader, contexts, refinementContexts);
                }

                int symbolWidth = symbol.Width;
                int symbolHeight = symbol.Height;
                bool rightCorner = p.ReferenceCorner is 2 or 3;
                bool bottomCorner = p.ReferenceCorner is 0 or 2;
                if (!p.Transposed && rightCorner)
                {
                    currentS += symbolWidth - 1;
                }
                else if (p.Transposed && bottomCorner)
                {
                    currentS += symbolHeight - 1;
                }

                int s = currentS;
                int left;
                int top;
                if (!p.Transposed)
                {
                    left = rightCorner ? s - symbolWidth + 1 : s;
                    top = bottomCorner ? t - symbolHeight + 1 : t;
                }
                else
                {
                    left = rightCorner ? t - symbolWidth + 1 : t;
                    top = bottomCorner ? s - symbolHeight + 1 : s;
                }

                region.Combine(symbol, left, top, p.CombinationOperator);
                if (refined)
                {
                    symbol.Dispose();
                }

                if (!p.Transposed && !rightCorner)
                {
                    currentS += symbolWidth - 1;
                }
                else if (p.Transposed && !bottomCorner)
                {
                    currentS += symbolHeight - 1;
                }

                instances++;
            }
        }

        return region;
    }

    private static Jbig2Bitmap DecodeRefinedSymbol(
        Jbig2TextRegionParameters p,
        Jbig2Bitmap original,
        ref MqDecoder decoder,
        ref Jbig2BitReader reader,
        Jbig2ArithmeticContexts? contexts,
        Span<byte> refinementContexts)
    {
        bool huffman = p.Huffman;
        int deltaWidth = huffman ? p.RefinementDeltaWidth!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.RefinementDeltaWidth);
        int deltaHeight = huffman ? p.RefinementDeltaHeight!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.RefinementDeltaHeight);
        int deltaX = huffman ? p.RefinementDeltaX!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.RefinementDeltaX);
        int deltaY = huffman ? p.RefinementDeltaY!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, contexts!.RefinementDeltaY);

        int width = original.Width + deltaWidth;
        int height = original.Height + deltaHeight;
        if (width < 0 || height < 0)
        {
            throw new Jbig2DecodingException("JBIG2 refined symbol has a negative size.");
        }

        int referenceDx = FloorDiv2(deltaWidth) + deltaX;
        int referenceDy = FloorDiv2(deltaHeight) + deltaY;

        if (!huffman)
        {
            return Jbig2RefinementRegion.Decode(ref decoder, refinementContexts, p.RefinementTemplate, false, p.RefinementAdaptive, width, height, original, referenceDx, referenceDy);
        }

        int size = p.RefinementSize!.DecodeRequired(ref reader);
        reader.AlignToByte();
        int start = reader.BytePosition;
        if (size < 0 || start + size > reader.Data.Length)
        {
            throw new Jbig2DecodingException("JBIG2 refinement data exceeds the segment.");
        }

        var refinementDecoder = new MqDecoder(reader.Data.Slice(start, size));
        var result = Jbig2RefinementRegion.Decode(ref refinementDecoder, refinementContexts, p.RefinementTemplate, false, p.RefinementAdaptive, width, height, original, referenceDx, referenceDy);
        reader.SkipBytes(size);
        return result;
    }

    private static int FloorDiv2(int value) => value >> 1;
}
