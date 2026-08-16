using PeachImage.Formats.Bmp.Encoding;

namespace PeachImage.Formats.Bmp;

/// <summary>
/// Encodes images as Windows Bitmap (BMP). <see cref="PixelFormat.Rgb24"/> writes 24bpp BI_RGB;
/// <see cref="PixelFormat.Gray8"/> writes 8bpp indexed with a linear grayscale palette (optionally
/// RLE8-compressed, see <see cref="BmpEncoderOptions.UseRunLengthEncoding"/>); <see cref="PixelFormat.Rgba32"/>
/// writes 32bpp BITMAPV4HEADER + BI_BITFIELDS with an explicit alpha mask. CMYK is not yet supported. Used
/// internally by <see cref="BmpCodec"/>.
/// </summary>
internal static class BmpEncoder
{
    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var bmpOptions = options as BmpEncoderOptions ?? new BmpEncoderOptions();
        BmpImageEncoder.Encode(image, stream, bmpOptions);
    }
}
