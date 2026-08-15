namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>Reads a BMP's color table (RGBQUAD or RGBTRIPLE entries, depending on header variant) for indexed (&lt;=8bpp) images.</summary>
internal static class BmpPalette
{
    /// <summary>
    /// Reads the palette into a flat <c>byte[entryCount * 3]</c> array in R,G,B order (source entries are
    /// B,G,R[,reserved]). The declared entry count is clamped to however many entries actually fit in the
    /// bytes available before <see cref="BmpHeader.PixelDataOffset"/>, defending against oversized/malformed
    /// <see cref="BmpHeader.ColorsUsed"/> declarations.
    /// </summary>
    public static byte[] Read(Stream stream, BmpHeader header)
    {
        long declaredCount = header.ColorsUsed != 0 ? header.ColorsUsed : (1L << header.BitCount);
        long availableBytes = Math.Max(0, (long)header.PixelDataOffset - header.HeaderBytesConsumed);
        long maxEntriesThatFit = header.PaletteEntrySize > 0 ? availableBytes / header.PaletteEntrySize : 0;
        int entryCount = (int)Math.Min(declaredCount, maxEntriesThatFit);

        var rgb = new byte[entryCount * 3];
        Span<byte> entry = stackalloc byte[4];

        for (int i = 0; i < entryCount; i++)
        {
            var entrySpan = entry[..header.PaletteEntrySize];
            BmpStreamHelpers.ReadExactlyOrThrow(stream, entrySpan);

            rgb[(i * 3) + 0] = entrySpan[2]; // R
            rgb[(i * 3) + 1] = entrySpan[1]; // G
            rgb[(i * 3) + 2] = entrySpan[0]; // B
        }

        return rgb;
    }
}
