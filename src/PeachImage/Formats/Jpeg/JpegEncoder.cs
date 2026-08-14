using PeachImage.Formats.Jpeg.Encoding;

namespace PeachImage.Formats.Jpeg;

/// <summary>Encodes images as baseline sequential JPEG. Supports grayscale and RGB(A) (encoded as YCbCr) sources; CMYK is not yet supported.</summary>
public sealed class JpegEncoder : IImageEncoder
{
    /// <inheritdoc/>
    public string FormatName => "jpeg";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jpg", "jpeg", "jpe", "jfif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/jpeg"];

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var jpegOptions = options as JpegEncoderOptions ?? new JpegEncoderOptions();
        FrameEncoder.Encode(image, stream, jpegOptions);
    }
}
