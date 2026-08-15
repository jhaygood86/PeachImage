namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>
/// Decodes BI_RLE8/BI_RLE4 compressed pixel data into a flat, row-major palette-index buffer (row 0 is the
/// first scanline in the compressed stream — the bottom row of the image, since RLE-compressed BMPs are
/// always bottom-up). Writes are bounds-checked and silently dropped when out of range, and truncated or
/// overflowing streams stop gracefully rather than throwing — malformed RLE data must never crash a decode.
/// </summary>
internal static class BmpRleDecoder
{
    public static byte[] DecodeRle8(ReadOnlySpan<byte> data, int width, int height)
    {
        var buffer = new byte[width * height];
        int pos = 0, row = 0, col = 0;

        while (pos < data.Length && row < height)
        {
            byte first = data[pos++];
            if (first == 0)
            {
                if (pos >= data.Length)
                {
                    break;
                }

                byte second = data[pos++];
                if (second == 0)
                {
                    row++;
                    col = 0;
                    continue;
                }

                if (second == 1)
                {
                    break;
                }

                if (second == 2)
                {
                    if (pos + 1 >= data.Length)
                    {
                        break;
                    }

                    col += data[pos++];
                    row += data[pos++];
                    continue;
                }

                int count = second;
                for (int i = 0; i < count; i++)
                {
                    if (pos >= data.Length)
                    {
                        return buffer;
                    }

                    WriteIndex(buffer, row, col, width, height, data[pos++]);
                    col++;
                }

                if ((count & 1) != 0)
                {
                    pos++; // Absolute-mode runs pad to an even byte count.
                }
            }
            else
            {
                byte count = first;
                if (pos >= data.Length)
                {
                    break;
                }

                byte value = data[pos++];
                for (int i = 0; i < count; i++)
                {
                    WriteIndex(buffer, row, col, width, height, value);
                    col++;
                }
            }
        }

        return buffer;
    }

    public static byte[] DecodeRle4(ReadOnlySpan<byte> data, int width, int height)
    {
        var buffer = new byte[width * height];
        int pos = 0, row = 0, col = 0;

        while (pos < data.Length && row < height)
        {
            byte first = data[pos++];
            if (first == 0)
            {
                if (pos >= data.Length)
                {
                    break;
                }

                byte second = data[pos++];
                if (second == 0)
                {
                    row++;
                    col = 0;
                    continue;
                }

                if (second == 1)
                {
                    break;
                }

                if (second == 2)
                {
                    if (pos + 1 >= data.Length)
                    {
                        break;
                    }

                    col += data[pos++];
                    row += data[pos++];
                    continue;
                }

                int pixelCount = second;
                int byteCount = (pixelCount + 1) / 2;
                for (int i = 0; i < pixelCount; i++)
                {
                    int byteIndex = pos + (i / 2);
                    if (byteIndex >= data.Length)
                    {
                        return buffer;
                    }

                    byte b = data[byteIndex];
                    byte value = (i & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
                    WriteIndex(buffer, row, col, width, height, value);
                    col++;
                }

                pos += byteCount;
                if ((byteCount & 1) != 0)
                {
                    pos++; // Absolute-mode runs pad to an even byte count.
                }
            }
            else
            {
                int pixelCount = first;
                if (pos >= data.Length)
                {
                    break;
                }

                byte value = data[pos++];
                byte hi = (byte)(value >> 4);
                byte lo = (byte)(value & 0x0F);
                for (int i = 0; i < pixelCount; i++)
                {
                    WriteIndex(buffer, row, col, width, height, (i & 1) == 0 ? hi : lo);
                    col++;
                }
            }
        }

        return buffer;
    }

    private static void WriteIndex(byte[] buffer, int row, int col, int width, int height, byte value)
    {
        if ((uint)row >= (uint)height || (uint)col >= (uint)width)
        {
            return;
        }

        buffer[(row * width) + col] = value;
    }
}
