using PeachImage.Formats.Avif.Encoder;

namespace PeachImage.Formats.Avif;

/// <summary>Encodes AVIF images. Used internally by <see cref="AvifCodec"/>.</summary>
internal static class AvifEncoder
{
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var avifOptions = options as AvifEncoderOptions ?? new AvifEncoderOptions();
        AvifImageEncoder.Encode(image, stream, avifOptions);
    }
}
