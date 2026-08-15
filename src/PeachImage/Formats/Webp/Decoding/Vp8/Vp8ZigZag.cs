namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// VP8's 4x4 coefficient zigzag scan order (RFC 6386 section 13.3, libwebp's <c>src/dec/vp8_dec.c</c>
/// <c>kZigzag</c>). This is a dedicated 16-entry table, not a truncation of JPEG's 8x8 zigzag.
/// </summary>
internal static class Vp8ZigZag
{
    public static readonly int[] Order =
    [
        0, 1, 4, 8, 5, 2, 3, 6,
        9, 12, 13, 10, 7, 11, 14, 15,
    ];
}