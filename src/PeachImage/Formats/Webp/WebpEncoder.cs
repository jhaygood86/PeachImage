using PeachImage.Formats.Webp.Encoding;

namespace PeachImage.Formats.Webp;

/// <summary>Encodes WebP images (VP8L lossless only, in this version). Used internally by <see cref="WebpCodec"/>.</summary>
internal static class WebpEncoder
{
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var webpOptions = options as WebpEncoderOptions ?? new WebpEncoderOptions();
        WebpImageEncoder.Encode(image, stream, webpOptions);
    }
}
