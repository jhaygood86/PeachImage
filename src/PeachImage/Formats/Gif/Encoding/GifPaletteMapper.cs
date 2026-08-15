namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Maps an image's pixels to palette indices, no dithering: each pixel independently gets its nearest palette entry.</summary>
internal static class GifPaletteMapper
{
    /// <summary>Maps <paramref name="image"/>'s pixels to indices into <paramref name="palette"/>, using <paramref name="transparentIndex"/> for any pixel whose alpha is below <paramref name="alphaThreshold"/>.</summary>
    public static byte[] Map(Image image, byte[] palette, int? transparentIndex, byte alphaThreshold)
    {
        int width = image.Width, height = image.Height;
        byte[] indices = new byte[width * height];
        var pixels = image.GetPixelSpan();
        bool hasAlpha = image.PixelFormat == PixelFormat.Rgba32;
        int bytesPerPixel = hasAlpha ? 4 : 3;

        for (int i = 0; i < indices.Length; i++)
        {
            int offset = i * bytesPerPixel;
            if (hasAlpha && transparentIndex.HasValue && pixels[offset + 3] < alphaThreshold)
            {
                indices[i] = (byte)transparentIndex.Value;
                continue;
            }

            indices[i] = (byte)GifPaletteLookup.NearestIndex(pixels[offset], pixels[offset + 1], pixels[offset + 2], palette);
        }

        return indices;
    }
}
