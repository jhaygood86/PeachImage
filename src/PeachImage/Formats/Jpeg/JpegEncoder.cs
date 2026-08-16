using PeachImage.Formats.Jpeg.Encoding;

namespace PeachImage.Formats.Jpeg;

/// <summary>Encodes images as baseline sequential JPEG. Supports grayscale and RGB(A) (encoded as YCbCr) sources; CMYK is not yet supported. Used internally by <see cref="JpegCodec"/>.</summary>
internal static class JpegEncoder
{
    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var jpegOptions = options as JpegEncoderOptions ?? new JpegEncoderOptions();
        FrameEncoder.Encode(image, stream, jpegOptions);
    }
}
