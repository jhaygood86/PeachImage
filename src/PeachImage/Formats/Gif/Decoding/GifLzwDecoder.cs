namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Decompresses GIF's variable-width (2-12 bit) LZW-compressed image data. Uses flat array-based prefix/suffix
/// code tables (not a dictionary of strings) for O(1) code expansion, the same approach giflib/stb_image use.
/// LZW is inherently sequential (each code depends on the last), so this is the decoder's dominant cost on the
/// decode benchmarks; unsafe/bounds-check-elimination tricks were measured and found not to help here (the
/// JIT was already eliminating the checks that matter), so the loop stays plain, safe array indexing.
/// Never throws on malformed/truncated input — a corrupt or short stream simply stops early, leaving the rest
/// of the output buffer zero-filled, mirroring <c>BmpRleDecoder</c>'s defensive convention.
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

        var prefix = new int[MaxCodeTableSize];
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

            for (int i = stackTop - 1; i >= 0 && writePos < pixelCount; i--)
            {
                output[writePos++] = stack[i];
            }

            if (isNewCode && writePos < pixelCount)
            {
                output[writePos++] = firstChar;
            }

            if (prevCode != -1 && nextCode < MaxCodeTableSize)
            {
                prefix[nextCode] = prevCode;
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
