using PeachImage.Formats.Gif.Encoding;

namespace PeachImage.Formats.Gif;

/// <summary>
/// Encodes images as GIF: builds a median-cut palette (with optional Floyd-Steinberg dithering, see
/// <see cref="GifEncoderOptions.Dither"/>) from the source pixels, then LZW-compresses the quantized result.
/// <see cref="Encode"/> writes a single-frame GIF; <see cref="EncodeAnimation"/> writes a full animated GIF
/// (per-frame delay/disposal, NETSCAPE2.0 loop count) from a <see cref="GifImage"/>.
/// </summary>
public sealed class GifEncoder : IImageEncoder
{
    /// <inheritdoc/>
    public string FormatName => "gif";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["gif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/gif"];

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var gifOptions = options as GifEncoderOptions ?? new GifEncoderOptions();
        GifImageEncoder.EncodeStatic(image, stream, gifOptions);
    }

    /// <summary>Encodes every frame of <paramref name="image"/> as an animated GIF, honoring each frame's delay/disposal and the animation's loop count.</summary>
    public static void EncodeAnimation(GifImage image, Stream stream, GifEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        GifImageEncoder.EncodeAnimation(image, stream, options ?? new GifEncoderOptions());
    }
}
