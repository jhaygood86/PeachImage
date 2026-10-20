using PeachImage.Formats.Jbig2.Internal;
using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>A decoded symbol dictionary: the exported symbols plus the arithmetic contexts that may be retained for the next dictionary.</summary>
internal sealed class Jbig2SymbolDictionary
{
    public required Jbig2Bitmap[] Exported { get; init; }

    public byte[]? GenericContexts { get; init; }

    public byte[]? RefinementContexts { get; init; }
}

/// <summary>The fields of a symbol dictionary segment's header (T.88 7.4.2.1).</summary>
internal sealed class Jbig2SymbolDictionaryParameters
{
    public bool Huffman { get; init; }

    public bool RefineOrAggregate { get; init; }

    public int Template { get; init; }

    public int RefinementTemplate { get; init; }

    public sbyte[] Adaptive { get; init; } = [];

    public sbyte[] RefinementAdaptive { get; init; } = [];

    public int ExportedCount { get; init; }

    public int NewCount { get; init; }

    public bool RetainContexts { get; init; }

    public Jbig2HuffmanTable? DeltaHeightTable { get; init; }

    public Jbig2HuffmanTable? DeltaWidthTable { get; init; }

    public Jbig2HuffmanTable? BitmapSizeTable { get; init; }

    public Jbig2HuffmanTable? AggregateInstancesTable { get; init; }
}

/// <summary>Symbol dictionary decoding (T.88 6.5).</summary>
internal static class Jbig2SymbolDictionaryDecoder
{
    public static Jbig2SymbolDictionary Decode(
        Jbig2SymbolDictionaryParameters p,
        IReadOnlyList<Jbig2Bitmap> inputSymbols,
        ReadOnlySpan<byte> data,
        byte[]? inheritedGenericContexts,
        byte[]? inheritedRefinementContexts)
    {
        if (p.NewCount < 0 || p.NewCount > Jbig2DecodingLimits.MaxSymbols || p.ExportedCount < 0)
        {
            throw new Jbig2DecodingException("JBIG2 symbol dictionary declares too many symbols.");
        }

        int inputCount = inputSymbols.Count;
        long total = (long)inputCount + p.NewCount;
        if (total > Jbig2DecodingLimits.MaxSymbols)
        {
            throw new Jbig2DecodingException("JBIG2 symbol dictionary declares too many symbols.");
        }

        var symbols = new Jbig2Bitmap[total];
        for (int i = 0; i < inputCount; i++)
        {
            symbols[i] = inputSymbols[i];
        }

        int codeLength = CeilLog2((int)total);
        if (p.Huffman)
        {
            codeLength = Math.Max(codeLength, 1);
        }

        int genericSize = Jbig2GenericRegion.ContextSize(p.Template);
        int refinementSize = Jbig2RefinementRegion.ContextSize(p.RefinementTemplate);
        byte[] genericContexts = Jbig2ArithmeticContexts.Rent(genericSize);
        byte[] refinementContexts = Jbig2ArithmeticContexts.Rent(refinementSize);
        try
        {
            if (inheritedGenericContexts is not null)
            {
                inheritedGenericContexts.AsSpan(0, Math.Min(genericSize, inheritedGenericContexts.Length)).CopyTo(genericContexts);
            }

            if (inheritedRefinementContexts is not null)
            {
                inheritedRefinementContexts.AsSpan(0, Math.Min(refinementSize, inheritedRefinementContexts.Length)).CopyTo(refinementContexts);
            }

            using var arithmetic = new Jbig2ArithmeticContexts(codeLength);
            var decoder = p.Huffman ? default : new MqDecoder(data);
            var reader = p.Huffman ? new Jbig2BitReader(data) : default;

            var exportFlags = DecodeSymbols(p, symbols, inputCount, codeLength, arithmetic, ref decoder, ref reader, genericContexts.AsSpan(0, genericSize), refinementContexts.AsSpan(0, refinementSize));

            var exported = new List<Jbig2Bitmap>(p.ExportedCount);
            for (int i = 0; i < symbols.Length; i++)
            {
                if (exportFlags[i])
                {
                    exported.Add(symbols[i]);
                }
            }

            if (exported.Count != p.ExportedCount)
            {
                throw new Jbig2DecodingException("JBIG2 symbol dictionary exports a different number of symbols than declared.");
            }

            return new Jbig2SymbolDictionary
            {
                Exported = [.. exported],
                GenericContexts = p.RetainContexts ? genericContexts.AsSpan(0, genericSize).ToArray() : null,
                RefinementContexts = p.RetainContexts ? refinementContexts.AsSpan(0, refinementSize).ToArray() : null,
            };
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(genericContexts);
            System.Buffers.ArrayPool<byte>.Shared.Return(refinementContexts);
        }
    }

    private static bool[] DecodeSymbols(
        Jbig2SymbolDictionaryParameters p,
        Jbig2Bitmap[] symbols,
        int inputCount,
        int codeLength,
        Jbig2ArithmeticContexts arithmetic,
        ref MqDecoder decoder,
        ref Jbig2BitReader reader,
        Span<byte> genericContexts,
        Span<byte> refinementContexts)
    {
        bool huffman = p.Huffman;
        int[]? widths = huffman && !p.RefineOrAggregate ? new int[p.NewCount] : null;
        int height = 0;
        int decoded = 0;

        while (decoded < p.NewCount)
        {
            int deltaHeight = huffman ? p.DeltaHeightTable!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, arithmetic.DeltaHeight);
            height += deltaHeight;
            if (height < 0 || height > 1 << 24)
            {
                throw new Jbig2DecodingException("JBIG2 symbol dictionary has an invalid symbol height.");
            }

            int width = 0;
            int totalWidth = 0;
            int firstInClass = decoded;
            while (true)
            {
                bool inBand = huffman
                    ? p.DeltaWidthTable!.Decode(ref reader, out int deltaWidth)
                    : Jbig2ArithmeticContexts.DecodeInteger(ref decoder, arithmetic.DeltaWidth, out deltaWidth);
                if (!inBand)
                {
                    break;
                }

                if (decoded >= p.NewCount)
                {
                    throw new Jbig2DecodingException("JBIG2 symbol dictionary contains more symbols than declared.");
                }

                width += deltaWidth;
                if (width < 0 || width > 1 << 24)
                {
                    throw new Jbig2DecodingException("JBIG2 symbol dictionary has an invalid symbol width.");
                }

                totalWidth += width;
                if (!huffman || p.RefineOrAggregate)
                {
                    int index = inputCount + decoded;
                    if (!p.RefineOrAggregate)
                    {
                        symbols[index] = Jbig2GenericRegion.DecodeArithmetic(ref decoder, genericContexts, p.Template, false, p.Adaptive, width, height, null);
                    }
                    else
                    {
                        symbols[index] = DecodeAggregate(p, symbols, index, codeLength, width, height, arithmetic, ref decoder, ref reader, refinementContexts);
                    }
                }
                else
                {
                    widths![decoded] = width;
                }

                decoded++;
            }

            if (huffman && !p.RefineOrAggregate)
            {
                int bitmapSize = p.BitmapSizeTable!.DecodeRequired(ref reader);
                reader.AlignToByte();
                int start = reader.BytePosition;
                Jbig2Bitmap collective;
                if (bitmapSize == 0)
                {
                    collective = new Jbig2Bitmap(totalWidth, height);
                    int stride = (totalWidth + 7) / 8;
                    if (start + ((long)stride * height) > reader.Data.Length)
                    {
                        throw new Jbig2DecodingException("JBIG2 uncompressed symbol bitmap exceeds the segment.");
                    }

                    for (int y = 0; y < height; y++)
                    {
                        var row = collective.Row(y);
                        var packed = reader.Data.Slice(start + (y * stride), stride);
                        for (int x = 0; x < totalWidth; x++)
                        {
                            row[x] = (byte)((packed[x >> 3] >> (7 - (x & 7))) & 1);
                        }
                    }

                    reader.SkipBytes(stride * height);
                }
                else
                {
                    if (bitmapSize < 0 || start + bitmapSize > reader.Data.Length)
                    {
                        throw new Jbig2DecodingException("JBIG2 MMR symbol bitmap exceeds the segment.");
                    }

                    collective = Jbig2GenericRegion.DecodeMmr(reader.Data.Slice(start, bitmapSize), totalWidth, height, out _);
                    reader.SkipBytes(bitmapSize);
                }

                int x0 = 0;
                for (int i = firstInClass; i < decoded; i++)
                {
                    symbols[inputCount + i] = collective.Extract(x0, 0, widths![i], height);
                    x0 += widths[i];
                }
            }
        }

        return DecodeExportFlags(p, symbols.Length, arithmetic, ref decoder, ref reader);
    }

    private static Jbig2Bitmap DecodeAggregate(
        Jbig2SymbolDictionaryParameters p,
        Jbig2Bitmap[] symbols,
        int index,
        int codeLength,
        int width,
        int height,
        Jbig2ArithmeticContexts arithmetic,
        ref MqDecoder decoder,
        ref Jbig2BitReader reader,
        Span<byte> refinementContexts)
    {
        bool huffman = p.Huffman;
        int instances = huffman ? p.AggregateInstancesTable!.DecodeRequired(ref reader) : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, arithmetic.AggregateInstances);
        if (instances > 1)
        {
            var parameters = new Jbig2TextRegionParameters
            {
                Huffman = huffman,
                Refine = true,
                Width = width,
                Height = height,
                InstanceCount = instances,
                Strips = 1,
                LogStrips = 0,
                Symbols = new ArraySegment<Jbig2Bitmap>(symbols, 0, index),
                SymbolCodeLength = codeLength,
                DefaultPixel = 0,
                CombinationOperator = 0,
                Transposed = false,
                ReferenceCorner = 1,
                DeltaSOffset = 0,
                RefinementTemplate = p.RefinementTemplate,
                RefinementAdaptive = p.RefinementAdaptive,
                FirstS = huffman ? Jbig2HuffmanTable.Standard(6) : null,
                DeltaS = huffman ? Jbig2HuffmanTable.Standard(8) : null,
                DeltaT = huffman ? Jbig2HuffmanTable.Standard(11) : null,
                RefinementDeltaWidth = huffman ? Jbig2HuffmanTable.Standard(15) : null,
                RefinementDeltaHeight = huffman ? Jbig2HuffmanTable.Standard(15) : null,
                RefinementDeltaX = huffman ? Jbig2HuffmanTable.Standard(15) : null,
                RefinementDeltaY = huffman ? Jbig2HuffmanTable.Standard(15) : null,
                RefinementSize = huffman ? Jbig2HuffmanTable.Standard(1) : null,
            };

            return Jbig2TextRegion.Decode(parameters, ref decoder, ref reader, arithmetic, refinementContexts);
        }

        int id;
        int deltaX;
        int deltaY;
        if (huffman)
        {
            id = (int)reader.ReadBits(codeLength);
            deltaX = Jbig2HuffmanTable.Standard(15).DecodeRequired(ref reader);
            deltaY = Jbig2HuffmanTable.Standard(15).DecodeRequired(ref reader);
        }
        else
        {
            id = arithmetic.DecodeSymbolId(ref decoder);
            deltaX = Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, arithmetic.RefinementDeltaX);
            deltaY = Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, arithmetic.RefinementDeltaY);
        }

        if ((uint)id >= (uint)index)
        {
            throw new Jbig2DecodingException("JBIG2 refinement/aggregate symbol refers to a symbol that does not exist.");
        }

        var original = symbols[id];
        if (!huffman)
        {
            return Jbig2RefinementRegion.Decode(ref decoder, refinementContexts, p.RefinementTemplate, false, p.RefinementAdaptive, width, height, original, deltaX, deltaY);
        }

        int size = Jbig2HuffmanTable.Standard(1).DecodeRequired(ref reader);
        reader.AlignToByte();
        int start = reader.BytePosition;
        if (size < 0 || start + size > reader.Data.Length)
        {
            throw new Jbig2DecodingException("JBIG2 refinement data exceeds the segment.");
        }

        var refinementDecoder = new MqDecoder(reader.Data.Slice(start, size));
        var result = Jbig2RefinementRegion.Decode(ref refinementDecoder, refinementContexts, p.RefinementTemplate, false, p.RefinementAdaptive, width, height, original, deltaX, deltaY);
        reader.SkipBytes(size);
        return result;
    }

    private static bool[] DecodeExportFlags(
        Jbig2SymbolDictionaryParameters p,
        int total,
        Jbig2ArithmeticContexts arithmetic,
        ref MqDecoder decoder,
        ref Jbig2BitReader reader)
    {
        var flags = new bool[total];
        int index = 0;
        bool current = false;
        while (index < total)
        {
            int run = p.Huffman
                ? Jbig2HuffmanTable.Standard(1).DecodeRequired(ref reader)
                : Jbig2ArithmeticContexts.DecodeRequiredInteger(ref decoder, arithmetic.ExportRun);
            if (run < 0 || run > total - index)
            {
                throw new Jbig2DecodingException("JBIG2 symbol export run is invalid.");
            }

            if (current)
            {
                Array.Fill(flags, true, index, run);
            }

            index += run;
            current = !current;
        }

        return flags;
    }

    private static int CeilLog2(int value)
    {
        int bits = 0;
        while ((1L << bits) < value)
        {
            bits++;
        }

        return bits;
    }
}
