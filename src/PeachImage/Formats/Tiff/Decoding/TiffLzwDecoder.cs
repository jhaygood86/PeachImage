namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Decompresses TIFF's LZW-compressed strip data (Compression=5) — the classic Aldus/Adobe variant
/// virtually every real-world TIFF LZW encoder produces. Two deliberate differences from GIF's LZW (see
/// <c>Gif.Decoding.GifLzwDecoder</c>), both confirmed against libtiff's <c>tif_lzw.c</c> reference source
/// rather than assumed: codes are packed most-significant-bit-first (<see cref="TiffLzwBitReader"/>), and
/// the code table is rooted at TIFF's fixed 8-bit byte alphabet (ClearCode=256, EndOfInformationCode=257,
/// first free code=258, starting code width 9 bits — no per-file <c>minCodeSize</c> like GIF has) with
/// "early change" code-width growth: the width bumps to <c>n+1</c> bits as soon as the next free table
/// index reaches <c>(1&lt;&lt;n)-1</c> (511, 1023, 2047 — one code sooner than GIF's 512/1024/2048
/// thresholds). libtiff's exact check is <c>if (++free_entp &gt; maxcodep)</c> where <c>maxcodep</c>
/// corresponds to table index <c>(1&lt;&lt;nbits)-2</c>; in plain integer terms that's
/// "bump once <c>nextCode &gt;= (1&lt;&lt;nbits)-1</c>", which is what <see cref="Decode"/> checks below.
/// Otherwise structurally the same flat prefix/suffix/stack table approach as GIF's decoder, including its
/// "never throw on malformed/truncated input — stop early, leave the rest of the output as-is" convention.
/// </summary>
internal static class TiffLzwDecoder
{
    private const int ClearCode = 256;
    private const int EndCode = 257;
    private const int FirstFreeCode = 258;
    private const int MinCodeWidth = 9;
    private const int MaxCodeWidth = 12;
    private const int MaxCodeTableSize = 4096;

    public static void Decode(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        int outputLength = output.Length;
        if (outputLength == 0)
        {
            return;
        }

        var reader = new TiffLzwBitReader(compressed);

        int codeWidth = MinCodeWidth;
        int nextCode = FirstFreeCode;
        int bumpThreshold = (1 << codeWidth) - 1;

        var prefix = new ushort[MaxCodeTableSize];
        var suffix = new byte[MaxCodeTableSize];
        var stack = new byte[MaxCodeTableSize];

        int prevCode = -1;
        int writePos = 0;

        while (writePos < outputLength)
        {
            if (!reader.TryReadCode(codeWidth, out int code))
            {
                break;
            }

            if (code == ClearCode)
            {
                nextCode = FirstFreeCode;
                codeWidth = MinCodeWidth;
                bumpThreshold = (1 << codeWidth) - 1;
                prevCode = -1;
                continue;
            }

            if (code == EndCode)
            {
                break;
            }

            bool isNewCode = code == nextCode && prevCode != -1;
            if (!(code < nextCode || isNewCode))
            {
                // Invalid/corrupt code (out-of-range, and not the one legal "not yet in the table" case).
                break;
            }

            int stackTop = 0;
            int c = isNewCode ? prevCode : code;
            while (c >= FirstFreeCode)
            {
                if (stackTop >= stack.Length)
                {
                    return;
                }

                stack[stackTop++] = suffix[c];
                c = prefix[c];
            }

            stack[stackTop++] = (byte)c;
            byte firstChar = (byte)c;

            int runLength = stackTop + (isNewCode ? 1 : 0);
            if (runLength <= outputLength - writePos)
            {
                for (int i = stackTop - 1; i >= 0; i--)
                {
                    output[writePos++] = stack[i];
                }

                if (isNewCode)
                {
                    output[writePos++] = firstChar;
                }
            }
            else
            {
                for (int i = stackTop - 1; i >= 0 && writePos < outputLength; i--)
                {
                    output[writePos++] = stack[i];
                }

                if (isNewCode && writePos < outputLength)
                {
                    output[writePos++] = firstChar;
                }
            }

            if (prevCode != -1 && nextCode < MaxCodeTableSize)
            {
                prefix[nextCode] = (ushort)prevCode;
                suffix[nextCode] = firstChar;
                nextCode++;
                if (nextCode >= bumpThreshold && codeWidth < MaxCodeWidth)
                {
                    codeWidth++;
                    bumpThreshold = (1 << codeWidth) - 1;
                }
            }

            prevCode = code;
        }
    }
}
