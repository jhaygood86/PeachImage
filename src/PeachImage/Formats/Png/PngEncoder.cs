using PeachImage.Formats.Png.Encoding;

namespace PeachImage.Formats.Png;

/// <summary>
/// Encodes images as PNG. <see cref="PixelFormat.Gray8"/>/<see cref="PixelFormat.Gray16"/> write
/// grayscale (color type 0); <see cref="PixelFormat.Rgb24"/>/<see cref="PixelFormat.Rgb48"/> write
/// truecolor (color type 2); <see cref="PixelFormat.Rgba32"/>/<see cref="PixelFormat.Rgba64"/> write
/// truecolor-with-alpha (color type 6). No automatic palette quantization — indexed (color type 3)
/// output is not produced in v1. CMYK is not supported. Used internally by <see cref="PngCodec"/>.
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
