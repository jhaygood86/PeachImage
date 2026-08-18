namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Decompresses GIF's variable-width (2-12 bit) LZW-compressed image data. Uses flat array-based prefix/suffix
/// code tables (not a dictionary of strings) for O(1) code expansion, the same approach giflib/stb_image use.
/// LZW is inherently sequential (each code depends on the last), so this is the decoder's dominant cost on the
/// decode benchmarks; unsafe/bounds-check-elimination tricks were measured and found not to help here (the
/// JIT was already eliminating the checks that matter), so the loop stays plain, safe array indexing.
/// Never throws on malformed/truncated input — a corrupt or short stream simply stops early, leaving the rest
/// of the output buffer zero-filled, mirroring <c>BmpRleDecoder</c>'s defensive convention.
///
/// Stage-level profiling (issue #35) found the per-code chain-walk (following <c>prefix</c> back to the code's
/// root byte) plus the reversed copy into the output buffer account for ~76% of decode time on long-run images
/// and ~54% on short-run ones — <see cref="GifLzwBitReader"/>'s bit-read cost is the other end of that tradeoff
/// (~12% vs. ~25%). The copy loop's per-byte <c>writePos &lt; pixelCount</c> bound only needs to protect the
/// last code of a stream (a corrupt/oversized run could otherwise overflow the output buffer); every other
/// code has room for its whole run, so that check is hoisted out of the loop for the common case below.
/// </summary>
internal static class GifLzwDecoder
{
    private const int MaxCodeTableSize = 4096;

    public static byte[] Decode(byte[] imageData, int minCodeSize, int pixelCount)
    {
        byte[] output = new byte[pixelCount];
        DecodeInto(imageData, imageData.Length, minCodeSize, output, pixelCount);
        return output;
    }

    /// <summary>
    /// Same as <see cref="Decode"/>, but writes into a caller-provided (e.g. array-pool-rented) buffer instead
    /// of allocating one, for callers on a hot decode path. <paramref name="imageDataLength"/> is the actual
    /// amount of valid data in <paramref name="imageData"/> (which, like <paramref name="output"/>, may itself
    /// be array-pool-rented and therefore longer than the real data).
    /// </summary>
    public static void DecodeInto(byte[] imageData, int imageDataLength, int minCodeSize, byte[] output, int pixelCount)
    {
        if (minCodeSize is < 2 or > 8 || pixelCount == 0)
        {
            return;
        }

        var reader = new GifLzwBitReader(imageData, imageDataLength);

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;
        int maxCode = 1 << codeSize;

        // ushort (not int): code values never exceed MaxCodeTableSize - 1 (4095), and the chain walk below
        // touches this table with an unpredictable access pattern, so the smaller footprint (8 KB vs. 16 KB)
        // is worth the cast on write.
        var prefix = new ushort[MaxCodeTableSize];
        var suffix = new byte[MaxCodeTableSize];
        var stack = new byte[MaxCodeTableSize];

        int prevCode = -1;
        int writePos = 0;

        while (writePos < pixelCount)
        {
            if (!reader.TryReadCode(codeSize, out int code))
            {
                break;
            }

            if (code == clearCode)
            {
                nextCode = endCode + 1;
                codeSize = minCodeSize + 1;
                maxCode = 1 << codeSize;
                prevCode = -1;
                continue;
            }

            if (code == endCode)
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
            while (c >= endCode + 1)
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
            if (runLength <= pixelCount - writePos)
            {
                // Common case: this code's whole expansion fits, so the per-byte "still room left" check the
                // truncating path below needs can be skipped entirely.
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
                for (int i = stackTop - 1; i >= 0 && writePos < pixelCount; i--)
                {
                    output[writePos++] = stack[i];
                }

                if (isNewCode && writePos < pixelCount)
                {
                    output[writePos++] = firstChar;
                }
            }

            if (prevCode != -1 && nextCode < MaxCodeTableSize)
            {
                prefix[nextCode] = (ushort)prevCode;
                suffix[nextCode] = firstChar;
                nextCode++;
                if (nextCode == maxCode && codeSize < 12)
                {
                    codeSize++;
                    maxCode = 1 << codeSize;
                }
            }

            prevCode = code;
        }
    }
}
