namespace PeachImage.Formats.Png;

/// <summary>
/// The PNG codec. Format identity and header-sniffing live here, the single <see cref="IImageCodec"/>
/// surface <see cref="Image"/> dispatches through; decode and encode are separate internal
/// implementation details (<see cref="PngDecoder"/>/<see cref="PngEncoder"/>) composed privately
/// rather than exposed as their own abstractions.
/// </summary>
internal sealed class PngCodec : IImageCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private PngCodec()
    {
    }

    /// <summary>The shared codec instance, one of the fixed set of built-in codecs <see cref="Image"/> dispatches through.</summary>
    public static IImageCodec Instance { get; } = new PngCodec();

    /// <inheritdoc/>
    public string FormatName => "png";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["png"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/png"];

    /// <inheritdoc/>
    public int HeaderSize => 8;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Signature);

    /// <inheritdoc/>
    public bool CanDecode => true;

    /// <inheritdoc/>
    public bool CanEncode => true;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream) => PngDecoder.Identify(stream);

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null) => PngDecoder.Decode(stream, options);

    /// <inheritdoc/>
    public void Encode(Image image, Stream stream, EncoderOptions? options = null) => PngEncoder.Encode(image, stream, options);
}
