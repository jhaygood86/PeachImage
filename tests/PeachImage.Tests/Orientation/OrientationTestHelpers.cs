namespace PeachImage.Tests.Orientation;

internal static class OrientationTestHelpers
{
    public static bool SwapsDimensions(ImageOrientation orientation) =>
        orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;

    public static Image CreateRandomImage(int width, int height, PixelFormat format, int seed)
    {
        var image = Image.Create(width, height, format);
        new Random(seed).NextBytes(image.GetPixelSpan());
        return image;
    }

    // An Image that aliases `buffer` without owning it, like the frames of an animated image.
    public static Image WrapBuffer(int width, int height, PixelFormat format, byte[] buffer) =>
        Image.FromBuffer(width, height, format, buffer, owned: false);

    /// <summary>
    /// The trivially correct definition of each orientation: map every source pixel (x, y) to its destination coordinate and copy
    /// its bytes, one pixel at a time.
    /// </summary>
    public static byte[] ReferenceApply(byte[] source, int width, int height, int bytesPerPixel, ImageOrientation orientation, out int outWidth, out int outHeight)
    {
        bool swaps = SwapsDimensions(orientation);
        outWidth = swaps ? height : width;
        outHeight = swaps ? width : height;
        var result = new byte[outWidth * outHeight * bytesPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int dx, int dy) = orientation switch
                {
                    ImageOrientation.Normal => (x, y),
                    ImageOrientation.MirrorHorizontal => (width - 1 - x, y),
                    ImageOrientation.Rotate180 => (width - 1 - x, height - 1 - y),
                    ImageOrientation.MirrorVertical => (x, height - 1 - y),
                    ImageOrientation.Transpose => (y, x),
                    ImageOrientation.Rotate90 => (height - 1 - y, x),
                    ImageOrientation.Transverse => (height - 1 - y, width - 1 - x),
                    ImageOrientation.Rotate270 => (y, width - 1 - x),
                    _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
                };

                Array.Copy(source, ((y * width) + x) * bytesPerPixel, result, ((dy * outWidth) + dx) * bytesPerPixel, bytesPerPixel);
            }
        }

        return result;
    }
}
