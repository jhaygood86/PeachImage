namespace PeachImage.Formats.Gif.Encoding;

/// <summary>
/// Compresses a row-major buffer of palette indices with GIF's variable-width (2-12 bit) LZW, mirroring
/// <see cref="Decoding.GifLzwDecoder"/>'s code-size growth rule exactly (code size increases the moment the
/// code table becomes full for the current width — the "early change" convention giflib and effectively every
/// real-world GIF decoder expects).
/// </summary>
internal static class GifLzwEncoder
{
    private const int MaxCodeTableSize = 4096;

    public static void Encode(Stream stream, byte[] indices, int minCodeSize)
    {
        var writer = new GifLzwBitWriter(stream);

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;
        int maxCode = 1 << codeSize;

        var table = new Dictionary<int, int>();
        writer.WriteCode(clearCode, codeSize);

        int curCode = -1;
        foreach (byte b in indices)
        {
            if (curCode == -1)
            {
                curCode = b;
                continue;
            }

            int key = (curCode << 8) | b;
            if (table.TryGetValue(key, out int existingCode))
            {
                curCode = existingCode;
                continue;
            }

            writer.WriteCode(curCode, codeSize);

            if (nextCode < MaxCodeTableSize)
            {
                table[key] = nextCode;
                nextCode++;

                // The decoder can never add a table entry for the very first code after a Clear (there's no
                // previously-decoded code to combine with yet), so its nextCode is permanently one entry
                // behind this encoder's for the rest of that Clear "session". To keep code-size growth in
                // lockstep with the decoder (which only grows once its own lagging nextCode reaches
                // maxCode), the encoder must grow one entry later than the naive "compress"-algorithm timing.
                if (nextCode == maxCode + 1 && codeSize < 12)
                {
                    codeSize++;
                    maxCode = 1 << codeSize;
                }
            }
            else
            {
                writer.WriteCode(clearCode, codeSize);
                table.Clear();
                nextCode = endCode + 1;
                codeSize = minCodeSize + 1;
                maxCode = 1 << codeSize;
            }

            curCode = b;
        }

        if (curCode != -1)
        {
            writer.WriteCode(curCode, codeSize);
        }

        writer.WriteCode(endCode, codeSize);
        writer.Finish();
    }
}
