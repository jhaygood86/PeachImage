namespace PeachImage.Formats.Bmp.Encoding;

/// <summary>Packs one row of source pixels into BMP's on-disk byte layout (BGR(A), 4-byte row padding). Tight scalar loops — mirrors <c>BmpRowUnpacker</c>'s hot-path shape.</summary>
internal static class BmpRowPacker
{
    /// <summary>The row size, in bytes, once padded to a 4-byte boundary.</summary>
    public static int GetPaddedRowByteCount(int width, int bitCount) =>
        (((width * bitCount) + 31) / 32) * 4;

    /// <summary>Packs an RGB24 source row into BGR bytes, zero-padded to <paramref name="destRow"/>'s length.</summary>
    public static void PackRgb24Row(ReadOnlySpan<byte> srcRow, Span<byte> destRow)
    {
        int width = srcRow.Length / 3;
        for (int x = 0; x < width; x++)
        {
            int s = x * 3;
            int d = x * 3;
            destRow[d + 0] = srcRow[s + 2]; // B
            destRow[d + 1] = srcRow[s + 1]; // G
            destRow[d + 2] = srcRow[s + 0]; // R
        }

        destRow[(width * 3)..].Clear();
    }

    /// <summary>Packs an RGBA32 source row into BGRA bytes. 32bpp rows are never padded — already a multiple of 4.</summary>
    public static void PackRgba32Row(ReadOnlySpan<byte> srcRow, Span<byte> destRow)
    {
        int width = srcRow.Length / 4;
        for (int x = 0; x < width; x++)
        {
            int s = x * 4;
            int d = x * 4;
            destRow[d + 0] = srcRow[s + 2]; // B
            destRow[d + 1] = srcRow[s + 1]; // G
            destRow[d + 2] = srcRow[s + 0]; // R
            destRow[d + 3] = srcRow[s + 3]; // A
        }
    }

    /// <summary>Packs a Gray8 source row directly as 8bpp palette indices — the palette is a linear grayscale ramp, so the gray value already *is* the index — zero-padded to <paramref name="destRow"/>'s length.</summary>
    public static void PackGray8IndexedRow(ReadOnlySpan<byte> srcRow, Span<byte> destRow)
    {
        srcRow.CopyTo(destRow);
        destRow[srcRow.Length..].Clear();
    }
}
