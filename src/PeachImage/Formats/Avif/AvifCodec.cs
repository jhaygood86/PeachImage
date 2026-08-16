namespace PeachImage.Formats.Avif;

/// <summary>
/// The AVIF codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode is a separate internal implementation detail
/// (<see cref="AvifDecoder"/>) composed privately rather than exposed as its own abstraction. Decode-only —
/// AVIF encoding is not planned yet.
/// </summary>
internal sealed class AvifCodec : IImageCodec
{
    private AvifCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new AvifCodec();

    /// <inheritdoc/>
    public string FormatName => "avif";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["avif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/avif"];

    /// <inheritdoc/>
    public int HeaderSize => 32;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) => AvifDecoder.IsSupportedFileFormat(header);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => false;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => AvifDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => AvifDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) =>
        throw new NotSupportedException("AVIF encoding is not yet implemented.");
}
