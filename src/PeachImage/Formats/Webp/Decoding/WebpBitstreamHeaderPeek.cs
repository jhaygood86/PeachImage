namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Reads just the width/height/alpha-flag header fields of a VP8/VP8L chunk's payload without running the
/// full pixel decode pipeline — used by <see cref="WebpDecoder.Identify"/>, which per <see cref="IImageDecoder"/>'s
/// contract must report dimensions without fully decoding pixel data.
/// </summary>
internal static class WebpBitstreamHeaderPeek
{
    /// <summary>
    /// Peeks a VP8L chunk's 5-byte header. Layout (after the fixed 1-byte 0x2F signature already checked by
    /// the caller): 14-bit width-minus-one, 14-bit height-minus-one, 1-bit alpha_is_used, 3-bit version — all
    /// packed LSB-first starting at bit 0 of the byte immediately following the signature. Because VP8L's bit
    /// reader is LSB-first, this 32-bit sequence is numerically identical to just treating those 4 bytes as a
    /// little-endian <see cref="uint"/> and extracting bitfields with shifts/masks — no bit-by-bit reader needed.
    /// </summary>
    public static bool TryPeekVp8L(ReadOnlySpan<byte> chunkData, out int width, out int height, out bool alphaIsUsed)
    {
        if (chunkData.Length < 5 || chunkData[0] != 0x2F)
        {
            width = 0;
            height = 0;
            alphaIsUsed = false;
            return false;
        }

        uint bits = (uint)(chunkData[1] | (chunkData[2] << 8) | (chunkData[3] << 16) | (chunkData[4] << 24));
        width = (int)(bits & 0x3FFF) + 1;
        height = (int)((bits >> 14) & 0x3FFF) + 1;
        alphaIsUsed = ((bits >> 28) & 0x1) != 0;
        return true;
    }

    /// <summary>
    /// Peeks a VP8 (lossy) keyframe's uncompressed header: a 3-byte frame tag, a 3-byte start code
    /// (0x9d 0x01 0x2a), then 14-bit width + 2-bit horizontal-scale and 14-bit height + 2-bit vertical-scale,
    /// each little-endian. The scale bits are an upscale display hint only and not needed for buffer sizing.
    /// </summary>
    public static bool TryPeekVp8(ReadOnlySpan<byte> chunkData, out int width, out int height)
    {
        if (chunkData.Length < 10
            || chunkData[3] != 0x9d || chunkData[4] != 0x01 || chunkData[5] != 0x2a)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = (chunkData[6] | (chunkData[7] << 8)) & 0x3FFF;
        height = (chunkData[8] | (chunkData[9] << 8)) & 0x3FFF;
        return true;
    }
}
