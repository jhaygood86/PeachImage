namespace PeachImage.Formats.Gif.Encoding;

/// <summary>
/// Maps an image's pixels to palette indices with Floyd-Steinberg error-diffusion dithering, reducing banding
/// on photographic/gradient sources at the cost of a slightly noisier result. Transparent pixels (alpha below
/// <c>alphaThreshold</c>) are mapped directly to <c>transparentIndex</c> without diffusing error, since they
/// have no true quantization error to propagate.
/// </summary>
internal static class FloydSteinbergDitherer
{
    public static byte[] Map(Image image, GifPaletteKdTree paletteTree, int? transparentIndex, byte alphaThreshold)
    {
        byte[] palette = paletteTree.Palette;
        int width = image.Width, height = image.Height;
        byte[] indices = new byte[width * height];
        var pixels = image.GetPixelSpan();
        bool hasAlpha = image.PixelFormat == PixelFormat.Rgba32;
        int bytesPerPixel = hasAlpha ? 4 : 3;

        // Working buffer of float RGB (with accumulated diffusion error), row-major.
        float[] work = new float[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int offset = i * bytesPerPixel;
            work[(i * 3) + 0] = pixels[offset];
            work[(i * 3) + 1] = pixels[offset + 1];
            work[(i * 3) + 2] = pixels[offset + 2];
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;

                if (hasAlpha && transparentIndex.HasValue && pixels[(i * bytesPerPixel) + 3] < alphaThreshold)
                {
                    indices[i] = (byte)transparentIndex.Value;
                    continue;
                }

                float oldR = Math.Clamp(work[(i * 3) + 0], 0, 255);
                float oldG = Math.Clamp(work[(i * 3) + 1], 0, 255);
                float oldB = Math.Clamp(work[(i * 3) + 2], 0, 255);

                int index = paletteTree.NearestIndex((byte)oldR, (byte)oldG, (byte)oldB);
                indices[i] = (byte)index;

                float errR = oldR - palette[(index * 3) + 0];
                float errG = oldG - palette[(index * 3) + 1];
                float errB = oldB - palette[(index * 3) + 2];

                Diffuse(work, width, height, x + 1, y, errR, errG, errB, 7f / 16f);
                Diffuse(work, width, height, x - 1, y + 1, errR, errG, errB, 3f / 16f);
                Diffuse(work, width, height, x, y + 1, errR, errG, errB, 5f / 16f);
                Diffuse(work, width, height, x + 1, y + 1, errR, errG, errB, 1f / 16f);
            }
        }

        return indices;
    }

    private static void Diffuse(float[] work, int width, int height, int x, int y, float errR, float errG, float errB, float weight)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return;
        }

        int i = (y * width) + x;
        work[(i * 3) + 0] += errR * weight;
        work[(i * 3) + 1] += errG * weight;
        work[(i * 3) + 2] += errB * weight;
    }
}
