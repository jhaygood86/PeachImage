using PeachImage.Formats.Webp.Encoding;

namespace PeachImage.Formats.Webp;

/// <summary>Encodes still and animated WebP images. Used internally by <see cref="WebpCodec"/>.</summary>
internal static class WebpEncoder
{
    public static void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var webpOptions = options as WebpEncoderOptions ?? new WebpEncoderOptions();
        WebpImageEncoder.Encode(image, stream, webpOptions);
    }

    public static void EncodeAnimation(AnimatedImage image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var webpOptions = options as WebpEncoderOptions ?? new WebpEncoderOptions();
        WebpAnimationEncoder.Encode(image, stream, webpOptions);
    }
}
