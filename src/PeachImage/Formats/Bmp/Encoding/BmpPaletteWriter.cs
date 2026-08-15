namespace PeachImage.Formats.Bmp.Encoding;

/// <summary>Writes the 256-entry linear grayscale RGBQUAD palette used when encoding <see cref="PixelFormat.Gray8"/> images as 8bpp indexed BMP.</summary>
internal static class BmpPaletteWriter
{
    public static void WriteGrayscalePalette(Stream stream)
    {
        Span<byte> entry = stackalloc byte[4];
        for (int i = 0; i < 256; i++)
        {
            entry[0] = (byte)i; // B
            entry[1] = (byte)i; // G
            entry[2] = (byte)i; // R
            entry[3] = 0;       // reserved
            stream.Write(entry);
        }
    }
}
