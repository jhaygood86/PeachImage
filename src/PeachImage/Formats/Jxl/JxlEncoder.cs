using PeachImage.Formats.Jxl.Encoder;

namespace PeachImage.Formats.Jxl;

/// <summary>Encodes JPEG XL images. Used internally by <see cref="JxlCodec"/>.</summary>
internal static class JxlEncoder
{
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var jxlOptions = options as JxlEncoderOptions ?? new JxlEncoderOptions();
        JxlImageEncoder.Encode(image, stream, jxlOptions);
    }
}
