namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Shared machinery for VP8L's LZ77-style backward references: decoding a length or (pre-mapping) distance
/// prefix code's extra bits, and mapping a short "plane code" to an actual pixel distance via the fixed
/// 2D-neighborhood lookup table. All constants transcribed verbatim from libwebp's <c>src/dec/vp8l_dec.c</c>
/// (<c>GetCopyDistance</c>/<c>GetCopyLength</c>/<c>kCodeToPlane</c>/<c>PlaneCodeToDistance</c>) — a
/// transcription error here would silently corrupt every image using a short backward reference.
/// </summary>
internal static class Vp8LBackwardReferenceTables
{
    private const int CodeToPlaneCodes = 120;

    /// <summary>
    /// Maps a "plane code" in [1, 120] to a packed <c>(yOffset &lt;&lt; 4) | (8 - xOffset)</c> byte describing
    /// a small 2D pixel-neighborhood offset. Verified against libwebp's <c>kCodeToPlane</c>.
    /// </summary>
    private static readonly byte[] CodeToPlane =
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a,
        0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
        0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45,
        0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
        0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
        0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e,
        0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
        0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d,
        0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    /// <summary>
    /// Decodes a length or (pre-mapping) distance prefix code's value: symbols 0-3 map directly to 1-4;
    /// higher symbols read a doubling range of extra bits. The exact same formula serves both the green
    /// alphabet's length codes (256-279, passed as <c>symbol - 256</c>) and the distance alphabet's plane
    /// codes (0-39) — libwebp's <c>GetCopyLength</c> is a literal alias for <c>GetCopyDistance</c>.
    /// </summary>
    public static int DecodePrefixCodeValue(int symbol, Vp8LBitReader reader)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)reader.ReadBits(extraBits) + 1;
    }

    /// <summary>
    /// Maps a decoded distance "plane code" (the output of <see cref="DecodePrefixCodeValue"/> applied to a
    /// distance symbol) to an actual pixel distance. Codes beyond <see cref="CodeToPlaneCodes"/> are already
    /// a raw (large) distance offset; shorter codes look up a small 2D neighborhood offset, clamped to be at
    /// least 1 (distance 0 would mean "copy from the not-yet-written current pixel", which is meaningless).
    /// </summary>
    public static int PlaneCodeToDistance(int width, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes)
        {
            return planeCode - CodeToPlaneCodes;
        }

        byte distCode = CodeToPlane[planeCode - 1];
        int yOffset = distCode >> 4;
        int xOffset = 8 - (distCode & 0xF);
        int distance = (yOffset * width) + xOffset;
        return distance >= 1 ? distance : 1;
    }
}
