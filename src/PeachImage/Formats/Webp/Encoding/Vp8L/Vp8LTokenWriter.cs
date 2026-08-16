namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>Writes a tokenized VP8L pixel stream through its five already-built Huffman code tables — the write-side mirror of <see cref="Decoding.Vp8L.Vp8LPixelDecoder"/>'s per-pixel decode loop.</summary>
internal static class Vp8LTokenWriter
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int LengthCodeLimit = NumLiteralCodes + NumLengthCodes;

    public static void WriteTokens(
        Vp8LBitWriter writer,
        ReadOnlySpan<Vp8LToken> tokens,
        Vp8LBuiltHuffmanCode green,
        Vp8LBuiltHuffmanCode red,
        Vp8LBuiltHuffmanCode blue,
        Vp8LBuiltHuffmanCode alpha,
        Vp8LBuiltHuffmanCode distance)
    {
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case Vp8LTokenKind.Literal:
                    WriteLiteral(writer, token.Argb, green, red, blue, alpha);
                    break;

                case Vp8LTokenKind.BackwardReference:
                    WriteBackwardReference(writer, token.Length, token.PlaneCode, green, distance);
                    break;

                case Vp8LTokenKind.CacheIndex:
                    WriteCacheIndex(writer, token.CacheIndex, green);
                    break;
            }
        }
    }

    private static void WriteLiteral(Vp8LBitWriter writer, uint argb, Vp8LBuiltHuffmanCode green, Vp8LBuiltHuffmanCode red, Vp8LBuiltHuffmanCode blue, Vp8LBuiltHuffmanCode alpha)
    {
        int a = (int)(argb >> 24) & 0xFF;
        int r = (int)(argb >> 16) & 0xFF;
        int g = (int)(argb >> 8) & 0xFF;
        int b = (int)argb & 0xFF;

        // Order matches Vp8LPixelDecoder's decode loop exactly (green signals "literal", then red/blue/alpha)
        // -- swapping it would silently corrupt colors rather than fail to decode at all.
        writer.WriteBits(green.Codes[g], green.Lengths[g]);
        writer.WriteBits(red.Codes[r], red.Lengths[r]);
        writer.WriteBits(blue.Codes[b], blue.Lengths[b]);
        writer.WriteBits(alpha.Codes[a], alpha.Lengths[a]);
    }

    private static void WriteBackwardReference(Vp8LBitWriter writer, int length, int planeCode, Vp8LBuiltHuffmanCode green, Vp8LBuiltHuffmanCode distance)
    {
        var (lengthSymbol, lengthExtra, lengthExtraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(length);
        int greenSymbol = NumLiteralCodes + lengthSymbol;
        writer.WriteBits(green.Codes[greenSymbol], green.Lengths[greenSymbol]);
        if (lengthExtraBits > 0)
        {
            writer.WriteBits(lengthExtra, lengthExtraBits);
        }

        var (distanceSymbol, distanceExtra, distanceExtraBits) = Vp8LPrefixCodeEncoder.EncodePrefixCodeValue(planeCode);
        writer.WriteBits(distance.Codes[distanceSymbol], distance.Lengths[distanceSymbol]);
        if (distanceExtraBits > 0)
        {
            writer.WriteBits(distanceExtra, distanceExtraBits);
        }
    }

    private static void WriteCacheIndex(Vp8LBitWriter writer, int cacheIndex, Vp8LBuiltHuffmanCode green)
    {
        int symbol = LengthCodeLimit + cacheIndex;
        writer.WriteBits(green.Codes[symbol], green.Lengths[symbol]);
    }
}
