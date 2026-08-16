namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>Encode-only VP8L constants that have no decode-side counterpart in <see cref="Internal.WebpDecodingLimits"/>.</summary>
internal static class Vp8LEncodingLimits
{
    /// <summary>
    /// The longest a code-length-code (the small 19-symbol alphabet that Huffman-codes the *lengths* of a
    /// real alphabet's codes) may be. Bounded by the 3-bit field <see cref="Decoding.Vp8L.Vp8LCodeLengthReader"/>
    /// reads each code-length-code's own length from (<c>ReadBits(3)</c>), not by
    /// <see cref="Internal.WebpDecodingLimits.MaxHuffmanCodeLength"/>, which governs the five real alphabets instead.
    /// </summary>
    public const int MaxCodeLengthCodeLength = 7;

    /// <summary>
    /// The longest a single VP8L backward-reference token may span, derived from the green alphabet's
    /// highest length symbol (23): <c>extraBits=10, offset=3072, max=3072+1023+1</c>. A match longer than
    /// this must be split into multiple same-distance tokens.
    /// </summary>
    public const int MaxBackwardReferenceLength = 4096;

    /// <summary>
    /// The largest raw pixel distance a single VP8L backward-reference token can encode. The distance
    /// alphabet's highest symbol (39) can represent plane-code values up to <c>786432+262143+1 = 1,048,576</c>
    /// (<c>extraBits=18, offset=786432</c>) -- but a raw pixel distance is first mapped to a plane code via
    /// <see cref="Vp8LDistanceMapper"/>, and any distance not reachable through the short 120-entry
    /// neighborhood table takes the "raw" fallback of <c>distance + 120</c> (the exact inverse of
    /// <see cref="Decoding.Vp8L.Vp8LBackwardReferenceTables.PlaneCodeToDistance"/>'s own <c>planeCode - 120</c>
    /// branch). So the largest raw distance that still maps to an encodable plane code is <c>1,048,576 - 120</c>,
    /// not 1,048,576 itself -- using the unadjusted figure here let a distance near the ceiling produce a
    /// plane code past the last valid symbol, corrupting the distance histogram with an out-of-range index.
    /// </summary>
    public const int MaxBackwardReferenceDistance = 1_048_456;
}
