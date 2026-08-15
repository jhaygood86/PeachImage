namespace PeachImage.Formats.Bmp.Encoding;

/// <summary>Encodes 8bpp indexed pixel data as BI_RLE8. Correctness-first: always emits count/value runs (even length-1 runs), never absolute mode — simple to verify by round-tripping through <c>BmpRleDecoder</c>.</summary>
internal static class BmpRleEncoder
{
    /// <summary>Encodes <paramref name="pixels"/> (row-major, bottom-up, one byte per pixel) as a BI_RLE8 byte stream.</summary>
    public static byte[] EncodeRle8(byte[] pixels, int width, int height)
    {
        // Worst case is one (count,value) pair per pixel (2 bytes) plus a 2-byte end-of-line per row plus a
        // final 2-byte end-of-bitmap — reserve that upfront so the fill loop is plain array indexing, never
        // MemoryStream.WriteByte's per-call virtual dispatch and incremental buffer growth.
        var buffer = new byte[(pixels.Length * 2) + (height * 2) + 2];
        int pos = 0;

        for (int row = 0; row < height; row++)
        {
            int rowStart = row * width;
            int x = 0;
            while (x < width)
            {
                byte value = pixels[rowStart + x];
                int runLength = 1;
                while (x + runLength < width && runLength < 255 && pixels[rowStart + x + runLength] == value)
                {
                    runLength++;
                }

                buffer[pos++] = (byte)runLength;
                buffer[pos++] = value;
                x += runLength;
            }

            buffer[pos++] = 0;
            buffer[pos++] = 0; // End of line.
        }

        buffer[pos++] = 0;
        buffer[pos++] = 1; // End of bitmap.

        return buffer[..pos];
    }
}
