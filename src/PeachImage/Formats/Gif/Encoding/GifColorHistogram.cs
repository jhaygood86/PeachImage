namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Accumulates a frequency count of distinct opaque RGB colors across one or more source images, for <see cref="MedianCutQuantizer"/> to build a palette from.</summary>
internal sealed class GifColorHistogram
{
    private readonly Dictionary<int, int> _counts = [];

    public IReadOnlyDictionary<int, int> Counts => _counts;

    public void Add(byte r, byte g, byte b)
    {
        int key = (r << 16) | (g << 8) | b;
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
    }

    /// <summary>Adds every pixel from <paramref name="image"/> (Rgb24 or Rgba32) whose alpha (if any) is at or above <paramref name="alphaThreshold"/>.</summary>
    public void AddOpaquePixels(Image image, byte alphaThreshold)
    {
        var pixels = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;

        if (image.PixelFormat == PixelFormat.Rgba32)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                if (pixels[offset + 3] >= alphaThreshold)
                {
                    Add(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                }
            }
        }
        else if (image.PixelFormat == PixelFormat.Rgb24)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 3;
                Add(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
            }
        }
        else
        {
            throw new GifEncodingException($"Cannot encode pixel format {image.PixelFormat} as GIF.");
        }
    }
}
