namespace PeachImage.Formats.Webp.Internal;

/// <summary>Sanity-check limits applied while decoding, to reject hostile/corrupt input before large allocations. Mirrors Gif's/Bmp's/Jpeg's DecodingLimits.</summary>
internal static class WebpDecodingLimits
{
    /// <summary>
    /// The largest single canvas dimension a decode will attempt to allocate for. VP8L's own width/height
    /// header fields are 14 bits each (stored value + 1), so 16384 is the hard ceiling regardless of what
    /// VP8X's 24-bit canvas-size fields could theoretically encode.
    /// </summary>
    public const int MaxDimension = 16_384;

    /// <summary>The largest pixel count (width * height) a decode will attempt to allocate for. Equals <see cref="MaxDimension"/> squared, matching Gif's/Bmp's/Jpeg's MaxPixelCount for consistency.</summary>
    public const long MaxPixelCount = 268_435_456;

    /// <summary>The longest a single canonical Huffman code may be, per the VP8L bitstream spec.</summary>
    public const int MaxHuffmanCodeLength = 15;

    /// <summary>The largest valid value of the VP8L color cache's 4-bit <c>cache_bits</c> field.</summary>
    public const int MaxColorCacheBits = 11;

    /// <summary>The largest possible VP8L "green" alphabet size: 256 literals + 24 length codes + the largest possible color cache.</summary>
    public const int MaxGreenAlphabetSize = 256 + 24 + (1 << MaxColorCacheBits);

    /// <summary>The fixed size of VP8L's distance-code alphabet.</summary>
    public const int DistanceAlphabetSize = 40;

    /// <summary>The most VP8L transforms a single image stream may declare (one of each of the four kinds).</summary>
    public const int MaxTransforms = 4;

    /// <summary>The largest palette a VP8L color-indexing transform may declare.</summary>
    public const int MaxColorIndexingPaletteSize = 256;

    /// <summary>Sanity cap on the number of distinct meta-Huffman groups a VP8L entropy image may select, guarding against a hostile huffman_xbits choice.</summary>
    public const int MaxMetaHuffmanGroups = 1 << 16;

    /// <summary>The largest VP8 (lossy) coefficient-data partition count the bitstream may declare (2^4, the field's maximum).</summary>
    public const int MaxVp8Partitions = 16;
}
