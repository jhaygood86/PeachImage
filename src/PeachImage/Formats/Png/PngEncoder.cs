using PeachImage.Formats.Png.Encoding;

namespace PeachImage.Formats.Png;

/// <summary>
/// Encodes images as PNG. <see cref="PixelFormat.Gray8"/>/<see cref="PixelFormat.Gray16"/> write
/// grayscale (color type 0); <see cref="PixelFormat.Rgb48"/>/<see cref="PixelFormat.Rgba64"/> always write
/// truecolor(-with-alpha) (color types 2/6), since indexed color needs 8-bit-per-channel source samples.
/// <see cref="PixelFormat.Rgb24"/>/<see cref="PixelFormat.Rgba32"/> write indexed color (PLTE, color type 3)
/// or truecolor(-with-alpha) depending on <see cref="PngEncoderOptions.ColorMode"/> (see there for the
/// default auto-detection behavior). CMYK is not supported. Used internally by <see cref="PngCodec"/>.
/// </summary>
internal static class PngEncoder
{
    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var pngOptions = options as PngEncoderOptions ?? new PngEncoderOptions();
        PngImageEncoder.Encode(image, stream, pngOptions);
    }
}
