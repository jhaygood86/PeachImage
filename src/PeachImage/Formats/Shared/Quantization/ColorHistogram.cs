namespace PeachImage.Formats.Shared.Quantization;

/// <summary>
/// Accumulates a frequency count of distinct opaque RGB colors across one or more source images, for
/// <see cref="MedianCutQuantizer"/> to build a palette from. Format-agnostic — the same histogram and
/// quantizer are shared by GIF and PNG indexed-color encoding, since median-cut quantization only cares
/// about RGB frequency, not the container format the result ends up in.
/// </summary>
internal sealed class ColorHistogram
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
            throw new ArgumentException($"Cannot quantize pixel format {image.PixelFormat}; only Rgb24 and Rgba32 are supported.", nameof(image));
        }
    }

    /// <summary>
    /// Like <see cref="AddOpaquePixels"/>, but abandons the scan (returning <see langword="false"/>) as soon
    /// as the running distinct-color count exceeds <paramref name="maxDistinctColors"/>. For callers that
    /// only need to know whether a source is low-color-count enough to quantize losslessly, this avoids
    /// paying for a full histogram build on a source that's obviously going to fail that check (e.g. a
    /// high-entropy photo blowing past the cap within its first few rows).
    /// </summary>
    public bool TryAddOpaquePixelsUpTo(Image image, byte alphaThreshold, int maxDistinctColors)
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
                    if (_counts.Count > maxDistinctColors)
                    {
                        return false;
                    }
                }
            }
        }
        else if (image.PixelFormat == PixelFormat.Rgb24)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 3;
                Add(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                if (_counts.Count > maxDistinctColors)
                {
                    return false;
                }
            }
        }
        else
        {
            throw new ArgumentException($"Cannot quantize pixel format {image.PixelFormat}; only Rgb24 and Rgba32 are supported.", nameof(image));
        }

        return true;
    }

    /// <summary>Whether any pixel in <paramref name="image"/> has alpha at or above 0 but below <paramref name="alphaThreshold"/>. Always <see langword="false"/> for a source with no alpha channel.</summary>
    public static bool HasTransparentPixel(Image image, byte alphaThreshold)
    {
        if (image.PixelFormat != PixelFormat.Rgba32)
        {
            return false;
        }

        var pixels = image.GetPixelSpan();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < alphaThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether <paramref name="image"/>'s alpha channel (if any) is strictly binary — every pixel
    /// fully opaque (255) or fully transparent (0), nothing in between. Indexed-color formats (GIF, and
    /// PNG's palette tRNS as used here) can only mark a palette entry as one or the other; a source with any
    /// intermediate alpha value can't be represented that way without discarding information, so callers
    /// that need a lossless guarantee should treat a <see langword="false"/> result as "don't index this".
    /// </summary>
    public static bool TryGetBinaryTransparency(Image image, out bool hasTransparency)
    {
        hasTransparency = false;

        if (image.PixelFormat != PixelFormat.Rgba32)
        {
            return true;
        }

        var pixels = image.GetPixelSpan();
        bool sawTransparent = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i];
            if (alpha == 0)
            {
                sawTransparent = true;
            }
            else if (alpha != 255)
            {
                return false;
            }
        }

        hasTransparency = sawTransparent;
        return true;
    }
}
