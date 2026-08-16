namespace PeachImage.Formats.Webp;

/// <summary>
/// The WebP codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode and encode are separate internal implementation
/// details (<see cref="WebpDecoder"/>/<see cref="WebpEncoder"/>) composed privately rather than exposed as
/// their own abstractions. Encoding only ever produces the lossless (VP8L) bitstream for now — VP8 lossy
/// encoding is a separate, much larger effort left for later.
/// </summary>
internal sealed class WebpCodec : IImageCodec
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
    public ImageInfo Identify(Stream stream) => WebpDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => WebpDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => WebpEncoder.Encode(image, stream, options);
}
