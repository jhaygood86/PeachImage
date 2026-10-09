using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Shared.Orientation;

/// <summary>
/// Applies an <see cref="ImageOrientation"/> to a tightly packed pixel buffer, out of place or in place, for any pixel size.
/// Works purely on bytes and dimensions (no <see cref="Image"/>), so it allocates nothing.
/// </summary>
internal static unsafe class ImageOrientationApplier
{
    /// <summary>Whether <paramref name="orientation"/> exchanges the width and height of the image.</summary>
    public static bool SwapsDimensions(ImageOrientation orientation) =>
        orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;

    /// <summary>
    /// Writes <paramref name="source"/> (<paramref name="width"/> x <paramref name="height"/>) transformed by
    /// <paramref name="orientation"/> to <paramref name="destination"/>, which has the swapped dimensions when
    /// <see cref="SwapsDimensions"/>. The spans must not overlap and must be exactly the image's byte length.
    /// </summary>
    public static void Apply(ImageOrientation orientation, ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int bytesPerPixel)
    {
        int rowBytes = width * bytesPerPixel;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            switch (orientation)
            {
                case ImageOrientation.Normal:
                    source.CopyTo(destination);
                    break;
                case ImageOrientation.MirrorHorizontal:
                    for (int y = 0; y < height; y++)
                    {
                        PixelReverser.Reverse(src + ((nint)y * rowBytes), dst + ((nint)y * rowBytes), width, bytesPerPixel);
                    }

                    break;
                case ImageOrientation.Rotate180:
                    PixelReverser.Reverse(src, dst, width * height, bytesPerPixel);
                    break;
                case ImageOrientation.MirrorVertical:
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + ((nint)y * rowBytes), dst + ((nint)(height - 1 - y) * rowBytes), rowBytes, rowBytes);
                    }

                    break;
                case ImageOrientation.Transpose:
                    PixelTransposer.Transpose(src, dst, width, height, bytesPerPixel, flipX: false, flipY: false);
                    break;
                case ImageOrientation.Rotate90:
                    PixelTransposer.Transpose(src, dst, width, height, bytesPerPixel, flipX: true, flipY: false);
                    break;
                case ImageOrientation.Transverse:
                    PixelTransposer.Transpose(src, dst, width, height, bytesPerPixel, flipX: true, flipY: true);
                    break;
                case ImageOrientation.Rotate270:
                    PixelTransposer.Transpose(src, dst, width, height, bytesPerPixel, flipX: false, flipY: true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(orientation), orientation, message: null);
            }
        }
    }

    /// <summary>
    /// Transforms <paramref name="pixels"/> by <paramref name="orientation"/> in place. Only orientations that keep the dimensions
    /// can be applied this way: the mirrors and the 180 degree rotation always, the four others only for square images.
    /// </summary>
    /// <exception cref="InvalidOperationException">The orientation swaps width and height and the image is not square.</exception>
    public static void ApplyInPlace(ImageOrientation orientation, Span<byte> pixels, int width, int height, int bytesPerPixel)
    {
        if (SwapsDimensions(orientation) && width != height)
        {
            throw new InvalidOperationException(
                $"{orientation} turns a {width}x{height} image into {height}x{width}, which cannot be done in place. " +
                "Use ApplyOrientation(orientation) to get a new image, or ApplyOrientation(orientation, destination) to write into an image with the swapped dimensions.");
        }

        int rowBytes = width * bytesPerPixel;
        fixed (byte* p = pixels)
        {
            switch (orientation)
            {
                case ImageOrientation.Normal:
                    break;
                case ImageOrientation.MirrorHorizontal:
                    MirrorRows(p, width, height, bytesPerPixel);
                    break;
                case ImageOrientation.Rotate180:
                    PixelReverser.ReverseInPlace(p, width * height, bytesPerPixel);
                    break;
                case ImageOrientation.MirrorVertical:
                    SwapRows(p, height, rowBytes);
                    break;
                case ImageOrientation.Transpose:
                    PixelTransposer.TransposeInPlace(p, width, bytesPerPixel);
                    break;
                case ImageOrientation.Rotate90:
                    // Rotating clockwise is a transpose followed by a horizontal mirror, and counter-clockwise a transpose followed by a vertical one.
                    PixelTransposer.TransposeInPlace(p, width, bytesPerPixel);
                    MirrorRows(p, width, height, bytesPerPixel);
                    break;
                case ImageOrientation.Rotate270:
                    PixelTransposer.TransposeInPlace(p, width, bytesPerPixel);
                    SwapRows(p, height, rowBytes);
                    break;
                case ImageOrientation.Transverse:
                    PixelTransposer.TransposeInPlace(p, width, bytesPerPixel);
                    PixelReverser.ReverseInPlace(p, width * height, bytesPerPixel);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(orientation), orientation, message: null);
            }
        }
    }

    private static void MirrorRows(byte* p, int width, int height, int bytesPerPixel)
    {
        int rowBytes = width * bytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            PixelReverser.ReverseInPlace(p + ((nint)y * rowBytes), width, bytesPerPixel);
        }
    }

    // Exchanges row y with row height - 1 - y by swapping them a vector (then a word, then a byte) at a time: no temporary row.
    private static void SwapRows(byte* p, int height, int rowBytes)
    {
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            byte* a = p + ((nint)top * rowBytes);
            byte* b = p + ((nint)bottom * rowBytes);
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; i <= rowBytes - 16; i += 16)
                {
                    var va = Vector128.Load(a + i);
                    var vb = Vector128.Load(b + i);
                    vb.Store(a + i);
                    va.Store(b + i);
                }
            }

            for (; i <= rowBytes - 8; i += 8)
            {
                (*(ulong*)(a + i), *(ulong*)(b + i)) = (*(ulong*)(b + i), *(ulong*)(a + i));
            }

            for (; i < rowBytes; i++)
            {
                (a[i], b[i]) = (b[i], a[i]);
            }
        }
    }
}
