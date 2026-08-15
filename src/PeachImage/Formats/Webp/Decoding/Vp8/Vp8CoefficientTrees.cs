namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Constants for VP8's DCT coefficient token decode (RFC 6386 section 13): the coefficient-position-to-band
/// mapping and the extra-bit probability tables for the six "category" large-value tokens. Transcribed verbatim
/// from libwebp's <c>src/dec/tree_dec.c</c> (<c>kBands</c>) and <c>src/dec/vp8_dec.c</c> (<c>kCat3</c>..
/// <c>kCat6</c>; cat1/cat2 use two fixed inline probabilities rather than a table, see
/// <see cref="Vp8CoefficientDecoder"/>).
/// </summary>
internal static class Vp8CoefficientTrees
{
    /// <summary>Maps a coefficient's zigzag-scan position (0-15, plus a sentinel 16th entry) to one of 8 probability bands.</summary>
    public static readonly int[] PositionToBand =
    [
        0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7,
        0, // sentinel, never actually indexed at position 16
    ];

    /// <summary>Extra-bit probabilities for the "cat3" token (values 19-34, 3 extra bits, MSB first).</summary>
    public static readonly byte[] Cat3 = [173, 148, 140];

    /// <summary>Extra-bit probabilities for the "cat4" token (values 35-66, 4 extra bits, MSB first).</summary>
    public static readonly byte[] Cat4 = [176, 155, 140, 135];

    /// <summary>Extra-bit probabilities for the "cat5" token (values 67-130, 5 extra bits, MSB first).</summary>
    public static readonly byte[] Cat5 = [180, 157, 141, 134, 130];

    /// <summary>Extra-bit probabilities for the "cat6" token (values 131-2114, 11 extra bits, MSB first).</summary>
    public static readonly byte[] Cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];
}