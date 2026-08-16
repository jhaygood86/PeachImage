namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Composes one full VP8L image stream: the color-cache declaration, the "no entropy sub-image" bit (this
/// encoder always emits a single global Huffman group — <see cref="Decoding.Vp8L.Vp8LMetaHuffmanImage.ReadGroups"/>
/// treats that as the ordinary, ever-supported path, not a special case), the five Huffman code definitions
/// in green/red/blue/alpha/distance order (matching <see cref="Decoding.Vp8L.Vp8LHuffmanGroup.Read"/>'s fixed
/// order exactly), then the token stream. Shaped to match
/// <see cref="Decoding.Vp8L.Vp8LPixelDecoder.DecodeImageStream"/>'s signature so it is directly reusable for
/// the small transform-parameter sub-images (predictor tile modes, palette) with <c>allowColorCache: false</c>.
/// </summary>
internal static class Vp8LImageStreamWriter
{
    /// <summary>
    /// <paramref name="allowRecursion"/> must mirror the <c>allowRecursion</c> the matching decode call will
    /// use: <see langword="true"/> only for the top-level main image stream, <see langword="false"/> for every
    /// transform-parameter sub-image. <see cref="Decoding.Vp8L.Vp8LMetaHuffmanImage.ReadGroups"/> only reads
    /// the entropy-sub-image presence bit at all when its own <c>allowRecursion</c> is true — writing that bit
    /// unconditionally would misalign every sub-image's stream by one bit.
    /// </summary>
    public static void WriteImageStream(Vp8LBitWriter writer, uint[] pixels, int pixelCount, int width, WebpEncoderOptions options, bool allowColorCache, bool allowRecursion)
    {
        int cacheBits = allowColorCache
            ? Vp8LColorCachePlanner.ChooseCacheBits(pixels.AsSpan(0, pixelCount), options.UseColorCache)
            : 0;

        if (cacheBits > 0)
        {
            writer.WriteBits(1, 1);
            writer.WriteBits((uint)cacheBits, 4);
        }
        else
        {
            writer.WriteBits(0, 1);
        }

        if (allowRecursion)
        {
            writer.WriteBits(0, 1); // No entropy (meta-Huffman) sub-image -- always a single global group.
        }

        var (hashBits, maxChainLength, giveUpAfterFruitlessProbes) = GetMatchFinderSettings(options.CompressionLevel, pixelCount);
        var tokens = Vp8LTokenizer.Tokenize(pixels, pixelCount, width, cacheBits, hashBits, maxChainLength, giveUpAfterFruitlessProbes, out int tokenCount, out var histograms);

        try
        {
            var green = BuildAndWriteCode(writer, histograms.Green);
            var red = BuildAndWriteCode(writer, histograms.Red);
            var blue = BuildAndWriteCode(writer, histograms.Blue);
            var alpha = BuildAndWriteCode(writer, histograms.Alpha);
            var distance = BuildAndWriteCode(writer, histograms.Distance);

            Vp8LTokenWriter.WriteTokens(writer, tokens.AsSpan(0, tokenCount), green, red, blue, alpha, distance);
        }
        finally
        {
            Vp8LTokenPool.Shared.Return(tokens);
        }
    }

    private static Vp8LBuiltHuffmanCode BuildAndWriteCode(Vp8LBitWriter writer, int[] freq)
    {
        var lengths = new int[freq.Length];
        var codes = new uint[freq.Length];
        Vp8LCodeLengthWriter.WriteHuffmanCode(writer, freq, lengths, codes);
        return new Vp8LBuiltHuffmanCode { Lengths = lengths, Codes = codes };
    }

    private static (int HashBits, int MaxChainLength, int GiveUpAfterFruitlessProbes) GetMatchFinderSettings(WebpCompressionLevel level, int pixelCount)
    {
        int hashBits = Vp8LMatchFinder.ChooseHashBits(pixelCount);
        int maxChainLength = level switch
        {
            WebpCompressionLevel.Fastest => 16,
            WebpCompressionLevel.SmallestSize => 256,
            _ => 64,
        };

        // Measured on real photographic content: 99.96% of positions walk the entire configured chain length
        // and find nothing at all, so giving up after a handful of fruitless probes -- rather than always
        // walking to maxChainLength -- eliminates the overwhelming majority of that wasted search for a small
        // compression cost. SmallestSize search the full configured depth regardless, since it exists
        // specifically to prioritize compression over speed.
        int giveUpAfterFruitlessProbes = level switch
        {
            WebpCompressionLevel.Fastest => 4,
            WebpCompressionLevel.SmallestSize => maxChainLength,
            _ => 8,
        };

        return (hashBits, maxChainLength, giveUpAfterFruitlessProbes);
    }
}
