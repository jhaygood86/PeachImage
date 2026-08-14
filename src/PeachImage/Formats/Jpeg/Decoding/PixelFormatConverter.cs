namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> between the small set of pixel formats a caller may request via <see cref="DecoderOptions.TargetPixelFormat"/>.</summary>
internal static class PixelFormatConverter
{
    /// <summary>Converts <paramref name="image"/> to <paramref name="target"/> if needed, disposing the original when a new image is produced.</summary>
    public static Image ConvertIfNeeded(Image image, PixelFormat? target)
    {
        if (target is not { } targetFormat || targetFormat == image.PixelFormat)
        {
            return image;
        }

        var converted = (image.PixelFormat, targetFormat) switch
        {
            (PixelFormat.Gray8, PixelFormat.Rgb24) => Expand(image, 1, 3, static (src, dst) => { dst[0] = dst[1] = dst[2] = src[0]; }),
            (PixelFormat.Gray8, PixelFormat.Rgba32) => Expand(image, 1, 4, static (src, dst) => { dst[0] = dst[1] = dst[2] = src[0]; dst[3] = 255; }),
            (PixelFormat.Rgb24, PixelFormat.Rgba32) => Expand(image, 3, 4, static (src, dst) => { src[..3].CopyTo(dst); dst[3] = 255; }),
            (PixelFormat.Rgba32, PixelFormat.Rgb24) => Expand(image, 4, 3, static (src, dst) => src[..3].CopyTo(dst)),
            (PixelFormat.Rgb24, PixelFormat.Gray8) => Expand(image, 3, 1, static (src, dst) => dst[0] = Luma(src[0], src[1], src[2])),
            (PixelFormat.Rgba32, PixelFormat.Gray8) => Expand(image, 4, 1, static (src, dst) => dst[0] = Luma(src[0], src[1], src[2])),
            _ => throw new JpegDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
        };

        image.Dispose();
        return converted;
    }

    private static byte Luma(byte r, byte g, byte b) =>
        (byte)Math.Clamp(Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b), MidpointRounding.AwayFromZero), 0, 255);

    private static Image Expand(Image source, int sourceBytesPerPixel, int destBytesPerPixel, PixelConversion convert)
    {
        var dest = Image.Create(source.Width, source.Height, destBytesPerPixel switch
        {
            1 => PixelFormat.Gray8,
            3 => PixelFormat.Rgb24,
            4 => PixelFormat.Rgba32,
            _ => throw new ArgumentOutOfRangeException(nameof(destBytesPerPixel)),
        });

        var src = source.GetPixelSpan();
        var dst = dest.GetPixelSpan();
        int pixelCount = source.Width * source.Height;

        for (int i = 0; i < pixelCount; i++)
        {
            convert(src.Slice(i * sourceBytesPerPixel, sourceBytesPerPixel), dst.Slice(i * destBytesPerPixel, destBytesPerPixel));
        }

        return dest;
    }

    private delegate void PixelConversion(ReadOnlySpan<byte> source, Span<byte> destination);
}
