namespace PeachImage.Formats.Webp;

/// <summary>
/// The WebP codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> (and, via <see cref="IAnimatedImageCodec"/>, <see cref="AnimatedImage"/>)
/// dispatch through; decode and encode are separate internal implementation details
/// (<see cref="WebpDecoder"/>/<see cref="WebpEncoder"/>) composed privately rather than exposed as their own
/// abstractions. Encoding defaults to the lossless (VP8L) bitstream; set
/// <see cref="WebpEncoderOptions.Lossless"/> to <see langword="false"/> for lossy VP8 encoding. Animated WebP
/// encoding is not supported (<see cref="CanEncodeAnimation"/> is <see langword="false"/>) — only decoding.
/// </summary>
internal sealed class WebpCodec : IAnimatedImageCodec
{
    private WebpCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new WebpCodec();

    /// <inheritdoc/>
    public string FormatName => "webp";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["webp"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/webp"];

    /// <inheritdoc/>
    public int HeaderSize => 12;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
        && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public bool CanDecodeTransparency => true;

    /// <inheritdoc/>
    public bool CanEncodeTransparency => true;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => WebpDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => WebpDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => WebpEncoder.Encode(image, stream, options);

    /// <inheritdoc/>
    public bool CanEncodeAnimation => false;

    /// <inheritdoc/>
    public AnimatedImage DecodeAnimation(Stream stream, DecoderOptions? options = null) => WebpDecoder.DecodeAnimation(stream, options as WebpDecoderOptions);

    /// <inheritdoc/>
    public void EncodeAnimation(AnimatedImage image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("Animated WebP encoding is not supported; only decoding.");
}
