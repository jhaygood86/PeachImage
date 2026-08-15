namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// The five Huffman trees VP8L needs to decode one pixel: <see cref="Green"/> is a combined
/// literal+length+color-cache-index alphabet (256 literals + 24 length codes + the color cache size, if any)
/// decoded first for every pixel; <see cref="Red"/>/<see cref="Blue"/>/<see cref="Alpha"/> are plain 256-symbol
/// literal alphabets, only consulted when <see cref="Green"/> decodes a literal (&lt;256); <see cref="Distance"/>
/// is the 40-symbol distance-code alphabet, only consulted for a backward reference (green in [256,280)).
/// A meta-Huffman image (see <see cref="Vp8LMetaHuffmanImage"/>) can select a different group of these five
/// per image tile; without one, every pixel uses the single group at index 0.
/// </summary>
internal sealed class Vp8LHuffmanGroup
{
    public required Vp8LHuffmanTable Green { get; init; }

    public required Vp8LHuffmanTable Red { get; init; }

    public required Vp8LHuffmanTable Blue { get; init; }

    public required Vp8LHuffmanTable Alpha { get; init; }

    public required Vp8LHuffmanTable Distance { get; init; }

    /// <summary>Reads one group's worth of five Huffman code definitions from the bitstream, in green/red/blue/alpha/distance order.</summary>
    public static Vp8LHuffmanGroup Read(Vp8LBitReader reader, int colorCacheBits)
    {
        int cacheSize = colorCacheBits > 0 ? 1 << colorCacheBits : 0;
        int greenAlphabetSize = 256 + 24 + cacheSize;

        var green = Vp8LCodeLengthReader.ReadHuffmanCode(reader, greenAlphabetSize);
        var red = Vp8LCodeLengthReader.ReadHuffmanCode(reader, 256);
        var blue = Vp8LCodeLengthReader.ReadHuffmanCode(reader, 256);
        var alpha = Vp8LCodeLengthReader.ReadHuffmanCode(reader, 256);
        var distance = Vp8LCodeLengthReader.ReadHuffmanCode(reader, Internal.WebpDecodingLimits.DistanceAlphabetSize);

        return new Vp8LHuffmanGroup { Green = green, Red = red, Blue = blue, Alpha = alpha, Distance = distance };
    }
}
