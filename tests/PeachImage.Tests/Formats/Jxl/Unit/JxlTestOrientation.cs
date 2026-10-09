namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>
/// Applies an EXIF-style orientation to an image. The decoder reports the orientation without applying it, but libjxl's output and
/// the conformance references are upright, so tests apply it to the decoded pixels before comparing.
/// </summary>
internal static class JxlTestOrientation
{
    /// <summary>Returns the upright version of <paramref name="image"/> (the same instance when already upright); the input is disposed otherwise.</summary>
    public static Image Apply(Image image, ImageOrientation orientation)
    {
        if (orientation == ImageOrientation.Normal)
        {
            return image;
        }

        int bytes = image.PixelFormat.GetBytesPerPixel();
        int w = image.Width;
        int h = image.Height;
        bool swap = (int)orientation >= 5;
        var result = Image.Create(swap ? h : w, swap ? w : h, image.PixelFormat);
        for (int y = 0; y < h; y++)
        {
            var src = image.GetRowSpan(y);
            for (int x = 0; x < w; x++)
            {
                // Destination coordinates for each EXIF orientation.
                (int dx, int dy) = orientation switch
                {
                    ImageOrientation.MirrorHorizontal => (w - 1 - x, y),
                    ImageOrientation.Rotate180 => (w - 1 - x, h - 1 - y),
                    ImageOrientation.MirrorVertical => (x, h - 1 - y),
                    ImageOrientation.Transpose => (y, x),
                    ImageOrientation.Rotate90 => (h - 1 - y, x),
                    ImageOrientation.Transverse => (h - 1 - y, w - 1 - x),
                    _ => (y, w - 1 - x),
                };
                src.Slice(x * bytes, bytes).CopyTo(result.GetRowSpan(dy).Slice(dx * bytes, bytes));
            }
        }

        result.HasAlpha = image.HasAlpha;
        image.Dispose();
        return result;
    }
}
