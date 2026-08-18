namespace PeachImage.Formats.Gif;

/// <summary>
/// The GIF codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> (and, via <see cref="IAnimatedImageCodec"/>, <see cref="AnimatedImage"/>)
/// dispatch through — decode and encode, both single-frame and animated, are separate internal
/// implementation details (<see cref="GifDecoder"/>/<see cref="GifEncoder"/>) composed privately rather
/// than exposed as their own abstractions.
/// </summary>
internal sealed class GifCodec : IAnimatedImageCodec
{
    private GifCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new GifCodec();

    /// <inheritdoc/>
    public string FormatName => "gif";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["gif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/gif"];

    /// <inheritdoc/>
    public int HeaderSize => 6;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 6
        && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8'
        && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a';

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => GifDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => GifDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => GifEncoder.Encode(image, stream, options);

    /// <inheritdoc/>
    public bool CanEncodeAnimation => true;

    /// <inheritdoc/>
    public AnimatedImage DecodeAnimation(Stream stream, DecoderOptions? options = null) => GifDecoder.DecodeAnimation(stream, options as GifDecoderOptions);

    /// <inheritdoc/>
    public void EncodeAnimation(AnimatedImage image, Stream stream, EncoderOptions? options = null) => GifEncoder.EncodeAnimation(image, stream, options as GifEncoderOptions);
}
