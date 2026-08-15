using PeachImage.Formats.Png.Encoding;

namespace PeachImage.Formats.Png;

/// <summary>
/// Encodes images as PNG. <see cref="PixelFormat.Gray8"/>/<see cref="PixelFormat.Gray16"/> write
/// grayscale (color type 0); <see cref="PixelFormat.Rgb24"/>/<see cref="PixelFormat.Rgb48"/> write
/// truecolor (color type 2); <see cref="PixelFormat.Rgba32"/>/<see cref="PixelFormat.Rgba64"/> write
/// truecolor-with-alpha (color type 6). No automatic palette quantization — indexed (color type 3)
/// output is not produced in v1. CMYK is not supported.
/// </summary>
public sealed class PngEncoder : IImageEncoder
{
    /// <inheritdoc/>
    public string FormatName => "png";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["png"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/png"];

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var pngOptions = options as PngEncoderOptions ?? new PngEncoderOptions();
        PngImageEncoder.Encode(image, stream, pngOptions);
    }
}
