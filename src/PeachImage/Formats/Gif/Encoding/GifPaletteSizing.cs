namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Maps a used-color count to a valid GIF color table size (a power of two, 2-256) and the resulting LZW Minimum Code Size.</summary>
internal static class GifPaletteSizing
{
    public static int ColorTableSizeFor(int usedColorCount)
    {
        int size = Math.Max(2, usedColorCount);
        int powerOfTwo = 2;
        while (powerOfTwo < size)
        {
            powerOfTwo <<= 1;
        }

        return Math.Min(powerOfTwo, 256);
    }

    public static int MinCodeSizeFor(int colorTableSize)
    {
        int bits = 0;
        int value = colorTableSize;
        while (value > 1)
        {
            value >>= 1;
            bits++;
        }

        return Math.Max(2, bits);
    }

    /// <summary>The Logical Screen/Image Descriptor packed byte's 3-bit "Size of Color Table" field N, where the table holds 2^(N+1) entries.</summary>
    public static int SizeFieldFor(int colorTableSize)
    {
        int n = 0;
        int value = colorTableSize;
        while (value > 2)
        {
            value >>= 1;
            n++;
        }

        return n;
    }
}
